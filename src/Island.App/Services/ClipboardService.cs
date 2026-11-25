using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Interop;

namespace Island.App.Services;

/// <summary>一条剪贴板记录。</summary>
public sealed record ClipboardItem(DateTime At, string Kind, string Preview, string Full)
{
    public BitmapSource? Image { get; init; }
    public string ImageDigest { get; init; } = "";
    public long StoredBytes => (long)(Full.Length + Preview.Length + ImageDigest.Length) * 2
        + (Image is null ? 0 : (long)Image.PixelWidth * Image.PixelHeight * 4);
}

/// <summary>
/// 剪贴板历史（纯本地，不落盘）。
/// AddClipboardFormatListener 接收 WM_CLIPBOARDUPDATE；
/// 真正的读取（ContainsText/GetText/GetImage）放到专用 STA 后台线程：
/// 这些是多次 OLE 往返 + 位图拷贝，宿主占用剪贴板异常时会把 UI 线程卡住。
/// 上限：条数上限 + 全文总字节上限 + 保留时长，超限丢最旧。
/// 排除：ExcludedProcesses 里列出的来源进程不记录（默认空 = 全部记录，保持原行为）。
/// </summary>
public static class ClipboardService
{
    private static readonly object _gate = new();
    private static readonly List<ClipboardItem> _items = new();
    private static HwndSourceHook? _hook;
    private static IntPtr _hwnd;
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    /// <summary>保留条数上限。</summary>
    private const int Max = 50;

    /// <summary>
    /// 历史载荷总预算（2MB）：文本、预览和摘要按 UTF-16，图片按 BGRA32 像素计。
    /// 单条超预算直接跳过，不为了最新一条突破内存上限。
    /// </summary>
    private const long MaxTotalBytes = 2 * 1024 * 1024;

    /// <summary>保留时长上限（24 小时）：再老的记录没人会翻，白占内存。</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>不记录这些来源进程的剪贴板（进程名，如 "keepass"）。</summary>
    private static readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近一条预览（收起态显示）。</summary>
    public static string LastPreview { get { lock (_gate) { Trim(); return _items.Count == 0 ? "" : _items[0].Preview; } } }

    /// <summary>记录条数。</summary>
    public static int Count { get { lock (_gate) { Trim(); return _items.Count; } } }

    /// <summary>历史副本（新→旧）。</summary>
    public static List<ClipboardItem> Items { get { lock (_gate) { Trim(); return new List<ClipboardItem>(_items); } } }

    /// <summary>当前排除的来源进程名（副本）。</summary>
    public static string[] ExcludedProcesses { get { lock (_gate) return _excluded.ToArray(); } }

    private static HwndSource? _source;

    /// <summary>
    /// 读取线程的指令队列 + 线程本身。
    /// 读取必须离开 UI 线程（OLE 往返可能长时间阻塞），又必须是 STA
    /// （WPF 的 Clipboard 类会校验线程公寓状态），所以起一个专用 STA 线程。
    /// </summary>
    private static BlockingCollection<uint>? _queue;
    private static int _generation;
    private static Thread? _reader;

    /// <summary>已卸载（窗口关闭）：读线程在收尾时不再落库。</summary>
    private static bool _detached;

    /// <summary>在主窗口句柄上挂剪贴板监听（窗口加载后调用）。</summary>
    public static void Attach(IntPtr hwnd)
    {
        if (_source is not null) return;
        _hwnd = hwnd;
        if (!AddClipboardFormatListener(hwnd)) return;
        var src = HwndSource.FromHwnd(hwnd);
        if (src is null) { RemoveClipboardFormatListener(hwnd); return; }
        _source = src;
        _hook = WndProc;
        src.AddHook(_hook);
        lock (_gate) _detached = false;
    }

    public static void Detach()
    {
        if (_source is not null && _hook is not null) _source.RemoveHook(_hook);
        if (_hwnd != IntPtr.Zero) RemoveClipboardFormatListener(_hwnd);
        _source = null;
        _hook = null;
        // 读取线程也收掉：窗口关了就不该再记录，后台线程也别留着
        lock (_gate)
        {
            _detached = true;
            _generation++;
            _items.Clear();
            try { _queue?.CompleteAdding(); } catch { }
            _queue = null;
            _reader = null;
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            try
            {
                // 记下当前剪贴板序号：等后台线程真正去读时若序号已变，
                // 说明内容又被换掉了 → 这次不记（会有新的 WM_CLIPBOARDUPDATE 来处理），
                // 否则会把新内容当成旧事件的内容记下来
                uint seq = GetClipboardSequenceNumber();
                EnsureReader();
                lock (_gate)
                {
                    if (_queue is { IsAddingCompleted: false } q)
                    {
                        if (!q.TryAdd(seq)) { q.TryTake(out _); q.TryAdd(seq); }
                    }
                }
            }
            catch { }
            handled = false;
        }
        return IntPtr.Zero;
    }

    /// <summary>确保后台 STA 读取线程已就绪（懒启动：没有剪贴板事件就不占线程）。</summary>
    private static void EnsureReader()
    {
        lock (_gate)
        {
            if (_detached || _reader is not null) return;
            _queue ??= new BlockingCollection<uint>(new ConcurrentQueue<uint>(), 1);
            var q = _queue;
            int generation = _generation;
            var t = new Thread(() =>
            {
                try
                {
                    foreach (uint seq in q.GetConsumingEnumerable())
                    {
                        try { Record(seq, generation); } catch { }
                    }
                }
                finally { q.Dispose(); }
            })
            { Name = "MyIsland.Clipboard", IsBackground = true };
            // WPF Clipboard 要求 STA 线程；设失败也要把线程起起来，
            // 否则队列没人消费，指令会一直堆在内存里
            try { t.SetApartmentState(ApartmentState.STA); } catch { }
            t.Start();
            _reader = t;
        }
    }

    /// <summary>在后台 STA 线程上读取一次剪贴板并记入历史。</summary>
    private static void Record(uint seq, int generation)
    {
        try
        {
            // 内容已被换掉 → 丢弃这次（等新的 WM_CLIPBOARDUPDATE）
            if (GetClipboardSequenceNumber() != seq) return;

            // 来源进程排除（默认不排除任何进程 = 保持原行为）
            string? owner = GetClipboardOwnerProcessName();
            if (owner is not null && IsExcluded(owner)) return;

            if (!TryReadText(out string? text)) return;
            ClipboardItem? item;
            if (text is not null) item = CreateTextItem(text, DateTime.Now);
            else
            {
                if (!Clipboard.ContainsImage() || GetClipboardSequenceNumber() != seq) return;
                var image = Clipboard.GetImage();
                item = image is null ? null : CreateImageItem(image, DateTime.Now);
            }
            if (item is null || GetClipboardSequenceNumber() != seq) return;
            lock (_gate)
            {
                if (_detached || generation != _generation) return;
                Store(item);
            }
        }
        catch
        {
            // 剪贴板被别的进程独占时 OLE 会抛异常：这里吞掉，不影响宿主
        }
    }

    internal static ClipboardItem? CreateTextItem(string text, DateTime at)
    {
        if ((long)text.Length * 2 + 84 > MaxTotalBytes || string.IsNullOrWhiteSpace(text)) return null;
        string preview = (text.Length > 40 ? text[..40] + "…" : text).Replace("\r", "").Replace("\n", " ");
        return new ClipboardItem(at, "text", preview, text);
    }

    internal static ClipboardItem? CreateImageItem(BitmapSource source, DateTime at)
    {
        long bytes = (long)source.PixelWidth * source.PixelHeight * 4;
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0 || bytes > MaxTotalBytes - 256) return null;
        var normalized = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(source.PixelWidth * 4);
        var pixels = new byte[(int)bytes];
        normalized.CopyPixels(pixels, stride, 0);
        var image = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        string digest = Convert.ToHexString(SHA256.HashData(pixels));
        return new ClipboardItem(at, "image", $"图片 {image.PixelWidth}x{image.PixelHeight}", "")
        { Image = image, ImageDigest = digest };
    }

    internal static void Store(ClipboardItem item)
    {
        lock (_gate)
        {
            Trim();
            if (item.StoredBytes > MaxTotalBytes) return;
            if (_items.Count > 0 && _items[0].Kind == item.Kind &&
                (item.Kind == "text" ? _items[0].Full == item.Full :
                    _items[0].ImageDigest == item.ImageDigest &&
                    _items[0].Image?.PixelWidth == item.Image?.PixelWidth &&
                    _items[0].Image?.PixelHeight == item.Image?.PixelHeight)) return;
            _items.Insert(0, item);
            Trim();
        }
    }

    private static readonly string[] SensitiveFormats =
    {
        "ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore",
        "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard",
        "application/x-keepassxc-clipboard"
    };

    internal static bool IsSensitiveFormat(string format) =>
        Array.Exists(SensitiveFormats, name => string.Equals(name, format, StringComparison.OrdinalIgnoreCase));

    // 在分配托管字符串/图像前检查原生载荷大小；无法验证的格式不采集。
    private static bool TryReadText(out string? text)
    {
        text = null;
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            foreach (string format in SensitiveFormats)
                if (IsClipboardFormatAvailable(RegisterClipboardFormat(format))) return false;
            if (IsClipboardFormatAvailable(13))
            {
                IntPtr handle = GetClipboardData(13);
                ulong bytes = GlobalSize(handle).ToUInt64();
                if (bytes < 2 || bytes > (ulong)(MaxTotalBytes - 84) || (bytes & 1) != 0) return false;
                IntPtr ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero) return false;
                try
                {
                    int length = 0, limit = checked((int)bytes / 2);
                    while (length < limit && Marshal.ReadInt16(ptr, length * 2) != 0) length++;
                    if (length == limit) return false;
                    text = Marshal.PtrToStringUni(ptr, length);
                    return true;
                }
                finally { GlobalUnlock(handle); }
            }
            uint dib = IsClipboardFormatAvailable(17) ? 17u : 8u;
            if (!IsClipboardFormatAvailable(dib)) return false;
            IntPtr dibHandle = GetClipboardData(dib);
            ulong size = GlobalSize(dibHandle).ToUInt64();
            if (size < 40 || size > (ulong)(MaxTotalBytes - 256)) return false;
            IntPtr header = GlobalLock(dibHandle);
            if (header == IntPtr.Zero) return false;
            try
            {
                int headerSize = Marshal.ReadInt32(header);
                long width = Marshal.ReadInt32(header, 4), height = Math.Abs((long)Marshal.ReadInt32(header, 8));
                int compression = Marshal.ReadInt32(header, 16);
                return headerSize >= 40 && (ulong)headerSize <= size && width > 0 && height > 0
                    && width <= (MaxTotalBytes - 256) / 4 / height && (compression == 0 || compression == 3);
            }
            finally { GlobalUnlock(dibHandle); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>裁剪历史：条数 / 总字节 / 保留时长，超限丢最旧的一条。</summary>
    /// <remarks>调用方必须已持有 _gate。</remarks>
    private static void Trim()
    {
        while (_items.Count > Max) _items.RemoveAt(_items.Count - 1);

        DateTime now = DateTime.Now;
        while (_items.Count > 0 && now - _items[_items.Count - 1].At > MaxAge)
            _items.RemoveAt(_items.Count - 1);

        long total = TotalBytes();
        while (total > MaxTotalBytes && _items.Count > 0)
        {
            total -= _items[_items.Count - 1].StoredBytes;
            _items.RemoveAt(_items.Count - 1);
        }
    }

    /// <summary>当前历史占用的字节数（字符串按 UTF-16 计）。</summary>
    private static long TotalBytes()
    {
        long total = 0;
        foreach (var it in _items) total += it.StoredBytes;
        return total;
    }

    /// <summary>
    /// 设置不记录的来源进程（逗号分隔的进程名，如 "keepass,1password"；不区分大小写）。
    /// 默认空 = 全部记录（保持原有默认行为）。
    /// ⚠️ 宿主接线（App.xaml.cs 负责）：启动时把设置项里的排除列表读出来调用一次即可：
    ///   ClipboardService.SetExcludedProcesses(settings.ClipboardExcludedProcesses);
    /// </summary>
    public static void SetExcludedProcesses(string? csv)
    {
        var names = (csv ?? "").Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        SetExcludedProcesses(names);
    }

    /// <summary>
    /// 设置不记录的来源进程（进程名集合，不区分大小写）。
    /// 集合重载用泛型：传 null 时类型无法推断，只有上面的字符串重载可选，
    /// 这样 SetExcludedProcesses(null) 不会在两个重载之间产生歧义（CS0121）。
    /// </summary>
    public static void SetExcludedProcesses<T>(IEnumerable<T>? names) where T : class
    {
        lock (_gate)
        {
            _excluded.Clear();
            if (names is null) return;
            foreach (var n in names)
            {
                string t = Normalize(n?.ToString() ?? "");
                if (t.Length > 0) _excluded.Add(t);
            }
        }
    }

    /// <summary>进程名归一化：去空白、去 .exe 后缀（设置里写 "keepass.exe" 也要能匹配 "keepass"）。</summary>
    private static string Normalize(string name)
    {
        string t = name.Trim();
        if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 4);
        return t;
    }

    /// <summary>来源进程是否在排除列表里（记录入口按这个过滤）。</summary>
    private static bool IsExcluded(string processName)
    {
        string t = Normalize(processName);
        if (t.Length == 0) return false;
        lock (_gate) return _excluded.Contains(t);
    }

    /// <summary>由主线程的用户点击恢复历史；只写剪贴板，不切焦点或模拟粘贴。</summary>
    public static bool TryRestore(ClipboardItem? item)
    {
        if (item is null || item.StoredBytes > MaxTotalBytes || DateTime.Now - item.At > MaxAge) return false;
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) return false;
        try
        {
            if (item.Kind == "text" && !string.IsNullOrWhiteSpace(item.Full))
                Clipboard.SetText(item.Full);
            else if (item.Kind == "image" && item.Image is { IsFrozen: true } image)
                Clipboard.SetImage(image);
            else return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>清空历史（托盘菜单入口）。</summary>
    public static void Clear() { lock (_gate) _items.Clear(); }

    /// <summary>
    /// 取当前剪贴板的所有者进程名（取不到返回 null = 不做排除判断）。
    /// 所有者窗口即"是它设置了剪贴板"的那个应用。
    /// </summary>
    private static string? GetClipboardOwnerProcessName()
    {
        try
        {
            IntPtr owner = GetClipboardOwner();
            if (owner == IntPtr.Zero) return null;
            if (GetWindowThreadProcessId(owner, out uint pid) == 0 || pid == 0) return null;
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch { return null; }
    }

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
