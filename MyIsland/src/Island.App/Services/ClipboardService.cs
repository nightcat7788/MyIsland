using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace Island.App.Services;

/// <summary>一条剪贴板记录。</summary>
public sealed record ClipboardItem(DateTime At, string Kind, string Preview, string Full);

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
    /// 全文总字节上限（2MB，按 UTF-16 计）。
    /// 只靠条数上限不够：一条几 MB 的长文本就能把内存吃光。
    /// </summary>
    private const long MaxTotalBytes = 2 * 1024 * 1024;

    /// <summary>保留时长上限（24 小时）：再老的记录没人会翻，白占内存。</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>不记录这些来源进程的剪贴板（进程名，如 "keepass"）。</summary>
    private static readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近一条预览（收起态显示）。</summary>
    public static string LastPreview { get { lock (_gate) return _items.Count == 0 ? "" : _items[0].Preview; } }

    /// <summary>记录条数。</summary>
    public static int Count { get { lock (_gate) return _items.Count; } }

    /// <summary>历史副本（新→旧）。</summary>
    public static List<ClipboardItem> Items { get { lock (_gate) return new List<ClipboardItem>(_items); } }

    /// <summary>当前排除的来源进程名（副本）。</summary>
    public static string[] ExcludedProcesses { get { lock (_gate) return _excluded.ToArray(); } }

    private static HwndSource? _source;

    /// <summary>
    /// 读取线程的指令队列 + 线程本身。
    /// 读取必须离开 UI 线程（OLE 往返可能长时间阻塞），又必须是 STA
    /// （WPF 的 Clipboard 类会校验线程公寓状态），所以起一个专用 STA 线程。
    /// </summary>
    private static BlockingCollection<Action>? _queue;
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
        if (src is null) return;
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
                _queue?.Add(() => Record(seq));
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
            if (_reader is not null) return;
            _queue ??= new BlockingCollection<Action>(new ConcurrentQueue<Action>());
            var q = _queue;
            var t = new Thread(() =>
            {
                foreach (var act in q.GetConsumingEnumerable())
                {
                    // 单条失败只丢这一条：绝不让异常冒到 UI 线程
                    try { act(); } catch { }
                }
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
    private static void Record(uint seq)
    {
        try
        {
            // 内容已被换掉 → 丢弃这次（等新的 WM_CLIPBOARDUPDATE）
            if (GetClipboardSequenceNumber() != seq) return;

            // 来源进程排除（默认不排除任何进程 = 保持原行为）
            string? owner = GetClipboardOwnerProcessName();
            if (owner is not null && IsExcluded(owner)) return;

            if (!Clipboard.ContainsText() && !Clipboard.ContainsImage()) return;

            string kind, preview, full;
            if (Clipboard.ContainsText())
            {
                full = Clipboard.GetText() ?? "";
                if (string.IsNullOrWhiteSpace(full)) return;
                kind = "text";
                preview = full.Length > 40 ? full.Substring(0, 40) + "…" : full;
                preview = preview.Replace("\r", "").Replace("\n", " ");
            }
            else
            {
                var img = Clipboard.GetImage();
                if (img is null) return;
                kind = "image";
                full = "";
                preview = $"图片 {img.Width:F0}x{img.Height:F0}";
            }

            lock (_gate)
            {
                if (_detached) return;   // 窗口已关：不再落库
                // 去重：与最近一条相同则跳过
                if (_items.Count > 0 && _items[0].Full == full && _items[0].Kind == kind) return;
                _items.Insert(0, new ClipboardItem(DateTime.Now, kind, preview, full));
                Trim();
            }
        }
        catch
        {
            // 剪贴板被别的进程独占时 OLE 会抛异常：这里吞掉，不影响宿主
        }
    }

    /// <summary>裁剪历史：条数 / 总字节 / 保留时长，超限丢最旧的一条。</summary>
    /// <remarks>调用方必须已持有 _gate。</remarks>
    private static void Trim()
    {
        while (_items.Count > Max) _items.RemoveAt(_items.Count - 1);

        DateTime now = DateTime.Now;
        while (_items.Count > 0 && now - _items[_items.Count - 1].At > MaxAge)
            _items.RemoveAt(_items.Count - 1);

        // 全文总字节上限：至少保留最新一条（否则一条超大粘贴就把历史清空，反而更没用）
        long total = TotalBytes();
        while (total > MaxTotalBytes && _items.Count > 1)
        {
            total -= _items[_items.Count - 1].Full.Length * 2;
            _items.RemoveAt(_items.Count - 1);
        }
    }

    /// <summary>当前历史占用的字节数（字符串按 UTF-16 计）。</summary>
    private static long TotalBytes()
    {
        long total = 0;
        foreach (var it in _items) total += it.Full.Length * 2;
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
