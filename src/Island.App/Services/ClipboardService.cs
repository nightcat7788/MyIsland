using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Island.App.Services;

/// <summary>一条剪贴板记录。Sequence 用于区分内容不同但 Full 都为空的图片。</summary>
public sealed record ClipboardItem(DateTime At, string Kind, string Preview, string Full)
{
    public uint Sequence { get; init; }
    /// <summary>自动分类标签：common（常用文本）/ link（链接）/ code（代码）/ color（色值）/ image（图片）。</summary>
    public string Category { get; init; } = "common";
    /// <summary>文本总行数（>1 时在列表末尾标「+N 行」）。</summary>
    public int Lines { get; init; } = 1;
    /// <summary>常驻置顶（不随条数上限自动淘汰）。</summary>
    public bool Pinned { get; init; }
    /// <summary>十六进制颜色串（Category=="color" 时用于预览色块）。</summary>
    public string? HexColor { get; init; }
    /// <summary>图片条目的缩放 PNG 字节（支持列表缩略图与点击写回系统剪贴板）。</summary>
    public byte[]? ImagePng { get; init; }
}

/// <summary>
/// 剪贴板工作台服务（对齐 macOS 版 ClipboardHistory 功能）：
/// - 自动分类（文本 / 链接 / 代码 / 颜色 / 图片缩略图）+ 多行计数 + 常驻置顶 + 单条删除；
/// - 自动跳过标记了 Windows 标准隐私格式及密码管理器进程的敏感内容；
/// - 支持按 Settings.ClipboardRetention（session / 1d / 7d / 30d）本地保留与一键导出到下载目录。
/// </summary>
public static class ClipboardService
{
    private static readonly object _gate = new();
    private static readonly List<ClipboardItem> _items = new();
    private static HwndSourceHook? _hook;
    private static IntPtr _hwnd;
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private const int Max = 50;
    private const long MaxTotalBytes = 4 * 1024 * 1024;
    private static string _retention = "session";

    private static readonly string[] BuiltinExcluded =
    {
        "keepass", "keepassxc", "1password", "bitwarden", "enpass", "roboform", "dashlane", "protonpass"
    };
    private static readonly HashSet<string> _excluded = new(BuiltinExcluded, StringComparer.OrdinalIgnoreCase);

    public static string LastPreview { get { lock (_gate) return _items.Count == 0 ? "" : _items[0].Preview; } }
    public static int Count { get { lock (_gate) return _items.Count; } }
    public static List<ClipboardItem> Items { get { lock (_gate) return new List<ClipboardItem>(_items); } }
    public static string[] ExcludedProcesses { get { lock (_gate) return _excluded.ToArray(); } }

    public static event Action? Changed;

    private static HwndSource? _source;
    private static BlockingCollection<Action>? _queue;
    private static Thread? _reader;
    private static bool _detached;

    private static string HistoryFilePath => Path.Combine(
        Settings.DirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyIsland"),
        "clipboard-history.json");

    public static void Attach(IntPtr hwnd, string retention = "session")
    {
        SetRetention(retention, loadIfPersisted: true);
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
        SaveIfNeeded();
        if (_source is not null && _hook is not null) _source.RemoveHook(_hook);
        if (_hwnd != IntPtr.Zero) RemoveClipboardFormatListener(_hwnd);
        _source = null;
        _hook = null;
        lock (_gate)
        {
            _detached = true;
            try { _queue?.CompleteAdding(); } catch { }
            _queue = null;
            _reader = null;
        }
    }

    public static int RetentionDays(string? policy) => policy switch
    {
        "1d" => 1,
        "7d" => 7,
        "30d" => 30,
        _ => 0,
    };

    public static void SetRetention(string? policy, bool loadIfPersisted = false)
    {
        string norm = RetentionDays(policy) > 0 ? policy! : "session";
        lock (_gate)
        {
            _retention = norm;
            if (loadIfPersisted && RetentionDays(norm) > 0 && _items.Count == 0)
                LoadLocked();
            TrimLocked();
        }
        SaveIfNeeded();
        Changed?.Invoke();
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            try
            {
                uint seq = GetClipboardSequenceNumber();
                EnsureReader();
                _queue?.Add(() => Record(seq));
            }
            catch { }
            handled = false;
        }
        return IntPtr.Zero;
    }

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
                    try { act(); } catch { }
                }
            })
            { Name = "MyIsland.Clipboard", IsBackground = true };
            try { t.SetApartmentState(ApartmentState.STA); } catch { }
            t.Start();
            _reader = t;
        }
    }

    /// <summary>识别 Windows 剪贴板隐私扩展标记（密码管理器与浏览器插件通用规范）。</summary>
    private static bool HasSensitiveClipboardMarker(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", false))
                return true;
            if (data.GetDataPresent("ClipboardViewerIgnore", false))
                return true;
            if (data.GetDataPresent("CanIncludeInClipboardHistory", false))
            {
                object? val = data.GetData("CanIncludeInClipboardHistory", false);
                if (val is MemoryStream ms && ms.Length >= 4)
                {
                    var buf = new byte[4];
                    int n = ms.Read(buf, 0, 4);
                    if (n == 4 && BitConverter.ToInt32(buf, 0) == 0) return true;
                }
                else if (val is int i && i == 0) return true;
                else if (val is bool b && !b) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>纯函数识别文本类型（供单测与生产共用）：link / color / code / common。</summary>
    internal static (string Category, int Lines, string? HexColor) ClassifyText(string full)
    {
        if (string.IsNullOrWhiteSpace(full)) return ("common", 1, null);
        string trimmed = full.Trim();
        int lines = 1;
        for (int i = 0; i < full.Length; i++)
            if (full[i] == '\n') lines++;

        if (lines == 1 && ColorHexRegex.IsMatch(trimmed))
            return ("color", 1, NormalizeHexColor(trimmed));

        if (lines == 1 && (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            && !trimmed.Any(char.IsWhiteSpace))
            return ("link", 1, null);

        if (LooksLikeCode(trimmed, lines))
            return ("code", lines, null);

        return ("common", lines, null);
    }

    private static readonly Regex ColorHexRegex = new(@"^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);

    private static string NormalizeHexColor(string hex)
    {
        // CSS #RRGGBBAA 统一截取前 7 位 #RRGGBB 用于实心预览块
        return hex.Length == 9 ? hex[..7].ToUpperInvariant() : hex.ToUpperInvariant();
    }

    private static bool LooksLikeCode(string s, int lines)
    {
        int score = 0;
        if (lines >= 2 && (s.Contains("  ") || s.Contains('\t'))) score++;
        if (s.Contains('{') && s.Contains('}')) score += 2;
        if (s.Contains("=>") || s.Contains("->") || s.Contains("::")) score += 2;
        if (s.Contains("public ") || s.Contains("private ") || s.Contains("function ")
            || s.Contains("const ") || s.Contains("import ") || s.Contains("using ")
            || s.Contains("def ") || s.Contains("SELECT ", StringComparison.OrdinalIgnoreCase))
            score += 2;
        if (lines >= 2 && (s.Contains(';') || s.Contains("()"))) score++;
        return score >= 2;
    }

    private static void Record(uint seq)
    {
        try
        {
            if (GetClipboardSequenceNumber() != seq) return;

            string? owner = GetClipboardOwnerProcessName();
            if (owner is not null && IsExcluded(owner)) return;

            IDataObject? data = Clipboard.GetDataObject();
            if (data is null) return;
            if (HasSensitiveClipboardMarker(data)) return;

            ClipboardItem item;
            if (data.GetDataPresent(DataFormats.UnicodeText, true) || data.GetDataPresent(DataFormats.Text, true))
            {
                string full = (data.GetData(DataFormats.UnicodeText, true) as string)
                    ?? (data.GetData(DataFormats.Text, true) as string)
                    ?? "";
                if (string.IsNullOrWhiteSpace(full)) return;
                var (cat, lines, hex) = ClassifyText(full);
                string firstLine = full.Replace("\r", "").Split('\n')
                    .Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? full.Trim();
                string preview = firstLine.Length > 56 ? firstLine[..56] + "…" : firstLine;
                item = new ClipboardItem(DateTime.Now, "text", preview, full)
                {
                    Sequence = seq,
                    Category = cat,
                    Lines = lines,
                    HexColor = hex,
                };
            }
            else if (data.GetDataPresent(DataFormats.Bitmap, true))
            {
                var img = Clipboard.GetImage();
                if (img is null) return;
                byte[]? pngBytes = EncodeThumbnailPng(img, 360);
                item = new ClipboardItem(DateTime.Now, "image", $"图片 {img.PixelWidth}×{img.PixelHeight}", "")
                {
                    Sequence = seq,
                    Category = "image",
                    Lines = 1,
                    ImagePng = pngBytes,
                };
            }
            else
            {
                return;
            }

            lock (_gate)
            {
                if (_detached) return;
                if (_items.Count > 0 && IsDuplicate(_items[0], item.Kind, item.Full, seq)) return;
                // 若历史中已有同全文条目，保留其常驻状态并提到顶部
                bool wasPinned = false;
                if (item.Kind == "text")
                {
                    int oldIdx = _items.FindIndex(x => x.Kind == "text" && x.Full == item.Full);
                    if (oldIdx >= 0)
                    {
                        wasPinned = _items[oldIdx].Pinned;
                        _items.RemoveAt(oldIdx);
                    }
                }
                if (wasPinned) item = item with { Pinned = true };
                _items.Insert(0, item);
                TrimLocked();
            }
            SaveIfNeeded();
            Changed?.Invoke();
        }
        catch { }
    }

    private static byte[]? EncodeThumbnailPng(BitmapSource src, int maxWidth)
    {
        try
        {
            BitmapSource target = src;
            if (src.PixelWidth > maxWidth && src.PixelWidth > 0)
            {
                double scale = (double)maxWidth / src.PixelWidth;
                target = new TransformedBitmap(src, new ScaleTransform(scale, scale));
            }
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(target));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    /// <summary>在 UI 线程把选中的文本或图片条目重新写回系统剪贴板。</summary>
    public static bool CopyBack(ClipboardItem? item)
    {
        if (item is null) return false;
        try
        {
            if (item.Kind == "text" && !string.IsNullOrEmpty(item.Full))
            {
                Clipboard.SetDataObject(item.Full, true);
                return true;
            }
            if (item.Kind == "image" && item.ImagePng is { Length: > 0 } png)
            {
                using var ms = new MemoryStream(png);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                Clipboard.SetImage(bmp);
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>切换单条常驻置顶状态。</summary>
    public static void TogglePin(DateTime at, uint seq, string full)
    {
        lock (_gate)
        {
            int idx = _items.FindIndex(x => x.At == at && x.Sequence == seq && x.Full == full);
            if (idx < 0) return;
            _items[idx] = _items[idx] with { Pinned = !_items[idx].Pinned };
        }
        SaveIfNeeded();
        Changed?.Invoke();
    }

    /// <summary>删除单条剪贴板记录。</summary>
    public static void Remove(DateTime at, uint seq, string full)
    {
        lock (_gate)
        {
            int idx = _items.FindIndex(x => x.At == at && x.Sequence == seq && x.Full == full);
            if (idx < 0) return;
            _items.RemoveAt(idx);
        }
        SaveIfNeeded();
        Changed?.Invoke();
    }

    /// <summary>导出全部剪贴板条目到下载文件夹，并在资源管理器打开该目录。</summary>
    public static string? ExportToDownloads()
    {
        List<ClipboardItem> snapshot;
        lock (_gate) snapshot = _items.ToList();
        if (snapshot.Count == 0) return null;
        try
        {
            string downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                $"MyIsland-剪贴板导出-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(downloads);
            for (int i = 0; i < snapshot.Count; i++)
            {
                var c = snapshot[i];
                string stamp = c.At.ToString("HHmmss");
                if (c.Kind == "image" && c.ImagePng is { Length: > 0 } png)
                {
                    File.WriteAllBytes(Path.Combine(downloads, $"{i + 1:00}-{stamp}-图片.png"), png);
                }
                else if (!string.IsNullOrEmpty(c.Full))
                {
                    string safe = string.Concat(c.Preview.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch))).Trim();
                    if (safe.Length > 20) safe = safe[..20];
                    if (safe.Length == 0) safe = "文本";
                    File.WriteAllText(Path.Combine(downloads, $"{i + 1:00}-{stamp}-{safe}.txt"), c.Full);
                }
            }
            try
            {
                Process.Start(new ProcessStartInfo { FileName = downloads, UseShellExecute = true });
            }
            catch { }
            return downloads;
        }
        catch { return null; }
    }

    internal static bool IsDuplicate(ClipboardItem latest, string kind, string full, uint sequence)
    {
        if (!latest.Kind.Equals(kind, StringComparison.Ordinal)) return false;
        return kind == "image" ? latest.Sequence == sequence : latest.Full == full;
    }

    /// <summary>淘汰超限记录：优先保留 Pinned（常驻）条目（对齐 macOS 版淘汰规则）。</summary>
    internal static void TrimList(List<ClipboardItem> list, int maxCount, TimeSpan maxAge, long maxBytes)
    {
        DateTime now = DateTime.Now;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!list[i].Pinned && now - list[i].At > maxAge)
                list.RemoveAt(i);
        }

        while (list.Count > maxCount)
        {
            int victim = FindLastUnpinned(list);
            if (victim < 0) victim = list.Count - 1;
            list.RemoveAt(victim);
        }

        long total = EstimateBytes(list);
        while (total > maxBytes && list.Count > 1)
        {
            int victim = FindLastUnpinned(list);
            if (victim < 0) break;
            total -= ItemBytes(list[victim]);
            list.RemoveAt(victim);
        }
    }

    private static int FindLastUnpinned(List<ClipboardItem> list)
    {
        for (int i = list.Count - 1; i >= 0; i--)
            if (!list[i].Pinned) return i;
        return -1;
    }

    private static long ItemBytes(ClipboardItem it)
        => it.Full.Length * 2L + (it.ImagePng?.Length ?? 0);

    private static long EstimateBytes(List<ClipboardItem> list)
    {
        long total = 0;
        foreach (var it in list) total += ItemBytes(it);
        return total;
    }

    private static void TrimLocked()
    {
        int days = RetentionDays(_retention);
        TimeSpan maxAge = days > 0 ? TimeSpan.FromDays(days) : TimeSpan.FromHours(24);
        TrimList(_items, Max, maxAge, MaxTotalBytes);
    }

    private sealed class PersistedDto
    {
        public DateTime At { get; set; }
        public string Kind { get; set; } = "text";
        public string Preview { get; set; } = "";
        public string Full { get; set; } = "";
        public string Category { get; set; } = "common";
        public int Lines { get; set; } = 1;
        public bool Pinned { get; set; }
        public string? HexColor { get; set; }
        public byte[]? ImagePng { get; set; }
    }

    private static void LoadLocked()
    {
        try
        {
            string path = HistoryFilePath;
            if (!File.Exists(path)) return;
            string json = File.ReadAllText(path);
            var dtos = JsonSerializer.Deserialize<List<PersistedDto>>(json);
            if (dtos is null) return;
            _items.Clear();
            uint seqSeed = 1;
            foreach (var d in dtos)
            {
                _items.Add(new ClipboardItem(d.At, d.Kind, d.Preview, d.Full)
                {
                    Sequence = seqSeed++,
                    Category = d.Category,
                    Lines = Math.Max(1, d.Lines),
                    Pinned = d.Pinned,
                    HexColor = d.HexColor,
                    ImagePng = d.ImagePng,
                });
            }
        }
        catch { }
    }

    private static void SaveIfNeeded()
    {
        try
        {
            string path = HistoryFilePath;
            int days;
            List<PersistedDto>? dtos = null;
            lock (_gate)
            {
                days = RetentionDays(_retention);
                if (days > 0)
                {
                    dtos = _items.Select(x => new PersistedDto
                    {
                        At = x.At,
                        Kind = x.Kind,
                        Preview = x.Preview,
                        Full = x.Full,
                        Category = x.Category,
                        Lines = x.Lines,
                        Pinned = x.Pinned,
                        HexColor = x.HexColor,
                        ImagePng = x.ImagePng,
                    }).ToList();
                }
            }

            if (days <= 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }

            string dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dtos));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    public static void SetExcludedProcesses(string? csv)
    {
        var names = (csv ?? "").Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        SetExcludedProcesses(names);
    }

    public static void SetExcludedProcesses<T>(IEnumerable<T>? names) where T : class
    {
        lock (_gate)
        {
            _excluded.Clear();
            foreach (var b in BuiltinExcluded) _excluded.Add(b);
            if (names is null) return;
            foreach (var n in names)
            {
                string t = Normalize(n?.ToString() ?? "");
                if (t.Length > 0) _excluded.Add(t);
            }
        }
    }

    private static string Normalize(string name)
    {
        string t = name.Trim();
        if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) t = t[..^4];
        return t;
    }

    private static bool IsExcluded(string processName)
    {
        string t = Normalize(processName);
        if (t.Length == 0) return false;
        lock (_gate) return _excluded.Contains(t);
    }

    public static void Clear(bool keepPinned = true)
    {
        lock (_gate)
        {
            if (keepPinned) _items.RemoveAll(x => !x.Pinned);
            else _items.Clear();
        }
        SaveIfNeeded();
        Changed?.Invoke();
    }

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
