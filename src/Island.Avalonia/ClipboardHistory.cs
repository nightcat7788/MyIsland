using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System.Text.RegularExpressions;

namespace Island.Avalonia;

/// <summary>剪贴板条目类型（借鉴 BrimDeck Core/Shelf.cs 的 ClipKind）。</summary>
public enum ClipKind { Text, Link, Code, Color, Image }

/// <summary>一条剪贴板记录。Text 存原文（点一下才能粘回去），Preview 是单行截断。</summary>
public sealed record ClipItem(DateTime At, string Preview, ClipKind Kind = ClipKind.Text, string Text = "")
{
    /// <summary>图片数据（截图剪贴时才有）。只保留 PNG 字节，展示用缩略。</summary>
    public byte[]? Image { get; init; }
    /// <summary>常驻标记：右键「标记常驻」后不按保留策略清除（对齐 BrimDeck 的「常用」）。
    /// 与「常用」分类是两回事 —— 那个是按内容类型自动归类的。</summary>
    public bool Pinned { get; init; }
    /// <summary>原始文本的行数。多行时在预览末尾标「+N 行」（对齐 BrimDeck）。</summary>
    public int Lines { get; init; } = 1;
    /// <summary>图片文件名（落盘时用）。内存态为 null。</summary>
    public string? ImageFile { get; init; }
}

/// <summary>
/// 剪贴板内容分类（纯启发式，无网络调用）。规则顺序：链接 → 颜色 → 代码 → 文本。
/// 图片暂不识别（Avalonia 剪贴板 API 取位图需额外处理，这轮先只管文本）。
/// </summary>
public static class ClipClassifier
{
    // 颜色：#RGB / #RRGGBB / #RRGGBBAA / rgb(r,g,b) / rgba(r,g,b,a)
    private static readonly Regex ColorHex = new(@"^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);
    private static readonly Regex ColorRgb = new(@"^rgba?\(\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*(,\s*[\d.]+\s*)?\)$", RegexOptions.Compiled);

    // 代码：行首关键字 或 以 ; { } 结尾
    private static readonly Regex CodeKeyword = new(
        @"^\s*(using\s|import\s|from\s+\S+\s+import|package\s|def\s|class\s|function\s|public\s|private\s|protected\s|static\s|const\s|let\s|var\s|fn\s|pub\s|struct\s|enum\s|interface\s|type\s+\w+\s*=|#include|#define|if\s*\(|for\s*\(|while\s*\(|return\s)",
        RegexOptions.Compiled);

    /// <summary>判断文本类型。</summary>
    public static ClipKind Classify(string text)
    {
        string t = text.Trim();
        if (t.Length == 0) return ClipKind.Text;
        if (!t.Contains('\n') && t.Length <= 2048 && IsLink(t)) return ClipKind.Link;
        if (!t.Contains('\n') && (ColorHex.IsMatch(t) || ColorRgb.IsMatch(t))) return ClipKind.Color;
        return LooksLikeCode(t) ? ClipKind.Code : ClipKind.Text;
    }

    /// <summary>单行、无空白、http(s) 且有 host。</summary>
    public static bool IsLink(string text)
        => !text.Any(char.IsWhiteSpace)
        && Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.Host.Length > 0;

    /// <summary>多行：命中行过半才算代码；单行：需带括号或等号（避免把 "return 餐厅" 误判）。</summary>
    private static bool LooksLikeCode(string t)
    {
        var lines = t.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return false;
        if (lines.Length == 1)
        {
            string l = lines[0];
            if (!CodeKeyword.IsMatch(l)) return false;
            return l.Contains('(') || l.Contains('=') || l.EndsWith(';') || l.EndsWith('{');
        }
        int hits = lines.Count(l => CodeKeyword.IsMatch(l) || l.EndsWith(";") || l.EndsWith("{") || l.EndsWith("}"));
        return hits * 2 >= lines.Length;
    }

    /// <summary>颜色文本解析为 ARGB（识别失败返回 null）。</summary>
    public static Color? ParseColor(string text)
    {
        string t = text.Trim();
        try
        {
            if (t.StartsWith('#'))
            {
                string h = t[1..];
                return h.Length switch
                {
                    3 => Color.FromRgb(Convert.ToByte(h[0].ToString() + h[0], 16), Convert.ToByte(h[1].ToString() + h[1], 16), Convert.ToByte(h[2].ToString() + h[2], 16)),
                    6 => Color.FromRgb(Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16)),
                    8 => Color.FromArgb(Convert.ToByte(h[6..8], 16), Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16)),
                    _ => null,
                };
            }
            var m = Regex.Match(t, @"rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})");
            if (m.Success)
                return Color.FromRgb(byte.Parse(m.Groups[1].Value), byte.Parse(m.Groups[2].Value), byte.Parse(m.Groups[3].Value));
        }
        catch { /* 格式合法但数值越界等 */ }
        return null;
    }
}

/// <summary>
/// 剪贴板历史（跨平台）：轮询 TopLevel.Clipboard，1.5s 一次，纯内存最多 20 条。
/// Avalonia 的 Clipboard API 在 macOS/Windows/Linux 都可用；只读，不写不持久化。
/// </summary>
public sealed class ClipboardHistory : IDisposable
{
    private readonly List<ClipItem> _items = new();
    private readonly object _gate = new();
    private DispatcherTimer? _timer;
    private string? _last;
    private byte[]? _lastImg;   // 上一张图片的引用（去重）
    private int _polling;

    private void TrimExcessLocked()
    {
        for (int idx = _items.Count - 1; idx >= 0 && _items.Count > 20; idx--)
        {
            if (!_items[idx].Pinned) _items.RemoveAt(idx);
        }
        while (_items.Count > 20) _items.RemoveAt(_items.Count - 1);
    }

    public IReadOnlyList<ClipItem> Items { get { lock (_gate) return _items.ToList(); } }

    public bool Enabled { get; set; } = true;
    /// <summary>新增一条记录时触发（收起态要实时显示条数）。</summary>
    public event Action? ItemsChanged;

    public void Start(TopLevel top)
    {
        _timer?.Stop();
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += async (_, _) =>
        {
            if (!Enabled) return;
            if (System.Threading.Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
            try
            {
                var clip = top.Clipboard;
                if (clip is null) return;
                string? text = await clip.GetTextAsync();
                try
                {
                    var fmts = await clip.GetFormatsAsync();
                    // 跳过密码管理器等标记的敏感剪贴板格式
                    if (fmts.Any(f => f.Contains("Concealed", StringComparison.OrdinalIgnoreCase)
                                   || f.Contains("Transient", StringComparison.OrdinalIgnoreCase)
                                   || f.Contains("ExcludeClipboardContentFromMonitorProcessing", StringComparison.OrdinalIgnoreCase)
                                   || f.Contains("ClipboardViewerIgnore", StringComparison.OrdinalIgnoreCase)))
                    {
                        return;
                    }
                    string imgFmt = fmts.Contains("public.png") ? "public.png"
                        : fmts.Contains("image/png") ? "image/png" : "";
                    if (imgFmt.Length > 0)
                    {
                        var data = await clip.GetDataAsync(imgFmt);
                        byte[]? png = data switch
                        {
                            System.IO.MemoryStream ms2 => ms2.ToArray(),
                            byte[] b => b,
                            global::Avalonia.Media.Imaging.Bitmap bmp => EncodePng(bmp),
                            _ => null,
                        };
                        if (png is { Length: > 0 })
                        {
                            if (png.Length > 50_000_000) return;   // 50MB 上限
                            if (_lastImg is null || !_lastImg.AsSpan().SequenceEqual(png))
                            {
                                _lastImg = png;
                                lock (_gate)
                                {
                                    _items.Insert(0, new ClipItem(DateTime.Now, $"图片 {png.Length / 1024} KB", ClipKind.Image, "") { Image = png });
                                    TrimExcessLocked();
                                }
                                ItemsChanged?.Invoke();
                            }
                            return;
                        }
                    }
                }
                catch { /* 图片格式不认识就按文本走 */ }
                if (string.IsNullOrWhiteSpace(text) || text == _last) return;
                _lastImg = null;   // 来了新文本，图片指纹作废
                _last = text;
                if (text.Length > 1_000_000) text = text[..1_000_000];
                var kind = ClipClassifier.Classify(text);
                string normalized = text.Replace("\r", "");
                int lines = 1;
                for (int i = 0; i < normalized.Length; i++)
                    if (normalized[i] == '\n') lines++;
                int nl = normalized.IndexOf('\n');
                string firstLine = (nl >= 0 ? normalized[..nl] : normalized).Trim();
                if (firstLine.Length > 60) firstLine = firstLine[..60] + "…";
                lock (_gate)
                {
                    _items.Insert(0, new ClipItem(DateTime.Now, firstLine, kind, text) { Lines = lines });
                    TrimExcessLocked();
                }
                ItemsChanged?.Invoke();
            }
            catch (Exception ex) { Console.Error.WriteLine($"[clipboard] {ex.Message}"); }
            finally { System.Threading.Interlocked.Exchange(ref _polling, 0); }
        };
        _timer.Start();
    }

    /// <summary>
    /// 删除指定条目（按文本+时间匹配，因为列表是快照）。
    /// 复制了密码/敏感信息后用它立刻清掉——这是剪贴板管理器的安全刚需，
    /// 之前只能等它滚出 20 条。
    /// </summary>
    public void Remove(string text, DateTime at)
    {
        lock (_gate) _items.RemoveAll(it => it.At == at && it.Text == text);
        ItemsChanged?.Invoke();
    }

    /// <summary>清空全部记录。</summary>
    public void ClearAll()
    {
        lock (_gate) _items.Clear();
        ItemsChanged?.Invoke();
    }

    /// <summary>
    /// 手动加入一条记录（供「从选区添加」等外部入口，以及落盘往返测试用）。
    /// 按复制时间插入（新的在前）—— 与落盘读回时的排序一致，
    /// 否则「运行期按加入顺序、重启后按时间顺序」会让列表顺序在重启后跳变。
    /// </summary>
    public void Add(ClipItem item)
    {
        lock (_gate)
        {
            // 找第一个「比它旧」的位置插进去 → 时间新的始终在前
            int i = _items.FindIndex(it => it.At < item.At);
            if (i < 0) i = _items.Count;
            _items.Insert(i, item);
            TrimExcessLocked();
        }
        ItemsChanged?.Invoke();
    }

    /// <summary>
    /// 切换某条的「常驻」标记（右键菜单用）。常驻项不按保留策略清除。
    /// </summary>
    public void TogglePin(string text, DateTime at)
    {
        lock (_gate)
        {
            int i = _items.FindIndex(it => it.At == at && it.Text == text);
            if (i < 0) return;
            _items[i] = _items[i] with { Pinned = !_items[i].Pinned };
        }
        ItemsChanged?.Invoke();
    }

    /// <summary>某条是否已常驻（右键菜单打勾用）。</summary>
    public bool IsPinned(ClipItem item)
    {
        lock (_gate) return _items.Any(it => it.At == item.At && it.Text == item.Text && it.Pinned);
    }

    /// <summary>把一条记录写回剪贴板。记录自己写入的序列号，避免下一轮把它当成新条目重复记录。</summary>
    public async Task PickAsync(TopLevel top, ClipItem item)
    {
        try
        {
            var clip = top.Clipboard;
            if (clip is null) return;
            if (item.Kind == ClipKind.Image && item.Image is { } png)
            {
                _lastImg = png;   // 自己写的，下一轮跳过
                // Avalonia 11 的剪贴板写自定义格式走 SetDataObjectAsync + DataObject。
                // 【坑】macOS 的 NSPasteboard 认 UTI（public.png），不认 MIME（image/png）；
                // Windows 反过来。读取端两种都认（见 Start），写入端必须**两个都写**，
                // 否则 macOS 上粘贴到「预览/微信/浏览器」时对方找不到 public.png → 图片丢了。
                var dobj = new global::Avalonia.Input.DataObject();
                dobj.Set("public.png", png);
                dobj.Set("image/png", png);
                await clip.SetDataObjectAsync(dobj);
                var fmts = await clip.GetFormatsAsync();
                Console.Error.WriteLine($"[clipboard] pick image {png.Length}B → {string.Join(",", fmts)}");
            }
            else
            {
                _last = item.Text;   // 自己写的，下一轮跳过
                await clip.SetTextAsync(item.Text);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[clipboard] pick: {ex.Message}"); }
    }

    /// <summary>IBitmap → PNG 字节（剪贴板返回位图对象时）。</summary>
    private static byte[] EncodePng(global::Avalonia.Media.Imaging.Bitmap bmp)
    {
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms);
        return ms.ToArray();
    }

    // ==================== 落盘保留（BrimDeck 同款思路）====================
    // 记录只保存在本机，不加密、不联网。
    // 文本走 JSON；图片单独存文件（base64 塞进 JSON 会让文件暴涨、读写变慢）。
    // 保留期：session = 只在内存、退出即清（默认，隐私最稳）；
    // 1d / 7d / 30d = 落盘，启动时读回，过期的自动清掉（常驻项除外）。

    /// <summary>保留策略的可选值。</summary>
    public static readonly string[] RetentionOptions = { "session", "1d", "7d", "30d" };

    /// <summary>保留期 → 天数；session（不落盘）返回 0。</summary>
    public static int RetentionDays(string retention) => retention switch
    {
        "1d" => 1,
        "7d" => 7,
        "30d" => 30,
        _ => 0,
    };

    /// <summary>保留策略的中文标签（设置项显示用）。</summary>
    public static string RetentionLabel(string retention) => retention switch
    {
        "1d" => "保留 1 天",
        "7d" => "保留 7 天",
        "30d" => "保留 30 天",
        _ => "退出时清除",
    };

    /// <summary>剪贴板存储目录（与设置同目录）：%LOCALAPPDATA%/MyIsland/clipboard。</summary>
    public static string StoreDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyIsland", "clipboard");

    private static string JsonPath => Path.Combine(StoreDir, "clips.json");
    private static string ImgDir => Path.Combine(StoreDir, "images");

    /// <summary>
    /// 落盘保存。retention = "session" 时只清目录（退出即清）。
    /// 写盘失败不抛：记日志即可，剪贴板记录丢了不该让 app 崩。
    /// </summary>
    public void Save(string retention)
    {
        try
        {
            if (RetentionDays(retention) <= 0)
            {
                // 不保留：删掉旧档即可
                TryDeleteDir(StoreDir);
                return;
            }
            Directory.CreateDirectory(ImgDir);
            SetOwnerOnlyPermissions(StoreDir, isDir: true);
            SetOwnerOnlyPermissions(ImgDir, isDir: true);
            var rows = new List<ClipRow>();
            List<ClipItem> snap;
            lock (_gate) snap = _items.ToList();
            foreach (var it in snap)
            {
                string? imgFile = null;
                if (it.Image is { Length: > 0 } png)
                {
                    imgFile = $"{it.At.Ticks}.png";
                    string imgPath = Path.Combine(ImgDir, imgFile);
                    if (!File.Exists(imgPath))
                    {
                        try
                        {
                            File.WriteAllBytes(imgPath, png);
                            SetOwnerOnlyPermissions(imgPath, isDir: false);
                        }
                        catch { imgFile = null; }
                    }
                }
                rows.Add(new ClipRow(it.At, it.Preview, it.Kind.ToString(), it.Text, it.Pinned, it.Lines, imgFile));
            }
            var json = System.Text.Json.JsonSerializer.Serialize(rows,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
            string tmp = JsonPath + ".tmp";
            File.WriteAllText(tmp, json);
            SetOwnerOnlyPermissions(tmp, isDir: false);
            File.Move(tmp, JsonPath, true);
            // 清掉不再被任何条目引用的图片。之前 Save 只写不删：条目被删除 / 过期后，
            // 它的 PNG 永远留在 images/ 里，自动保存每 60 秒跑一次，长期开着
            // 「保留 30 天」会在磁盘上堆满历史截图。
            // 顺序必须是「先落 JSON，再删图」：反过来的话中途崩溃会留下
            // 「JSON 指向已删除文件」的坏档。
            // ImageFile 可能为 null（纯文本条目 / 图片写盘失败）。Where 不会收窄元素类型，
            // 所以在 Select 里显式滤掉 null，直接产出 HashSet<string>。
            var keptImages = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in rows)
                if (!string.IsNullOrEmpty(r.ImageFile)) keptImages.Add(r.ImageFile);
            DeleteOrphanImages(keptImages);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[clipboard] save: {ex.Message}"); }
    }

    /// <summary>删除 images/ 里不在 <paramref name="keep"/> 中的文件。单个文件删不掉不影响其它。</summary>
    private static void DeleteOrphanImages(HashSet<string> keep)
    {
        try
        {
            if (!Directory.Exists(ImgDir)) return;
            foreach (var path in Directory.EnumerateFiles(ImgDir, "*.png"))
            {
                // 精确匹配：文件名是代码生成的 "{ticks}.png"，对不上的一律视为孤儿。
                // 只扫 *.png —— 用户若手动往这目录放了别的文件，不动它。
                if (keep.Contains(Path.GetFileName(path))) continue;
                try { File.Delete(path); }
                catch (Exception ex) { Console.Error.WriteLine($"[clipboard] orphan image: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[clipboard] orphan scan: {ex.Message}"); }
    }

    /// <summary>
    /// 读回历史（启动时调用）。retention = "session" 时不读（也不保留任何文件）。
    /// </summary>
    public void Load(string retention)
    {
        if (RetentionDays(retention) <= 0) { TryDeleteDir(StoreDir); return; }
        try
        {
            if (!File.Exists(JsonPath)) return;
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<ClipRow>>(File.ReadAllText(JsonPath));
            if (rows is null) return;
            var loaded = new List<ClipItem>();
            foreach (var r in rows)
            {
                if (!Enum.TryParse<ClipKind>(r.Kind, out var kind)) kind = ClipKind.Text;
                byte[]? img = null;
                string? safeImgFile = null;
                if (!string.IsNullOrEmpty(r.ImageFile))
                {
                    string fileOnly = Path.GetFileName(r.ImageFile);
                    if (fileOnly == r.ImageFile && fileOnly.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        safeImgFile = fileOnly;
                        try
                        {
                            string p = Path.Combine(ImgDir, fileOnly);
                            if (File.Exists(p)) img = File.ReadAllBytes(p);
                        }
                        catch { /* 图片读不到就当纯文本条目 */ }
                    }
                }
                loaded.Add(new ClipItem(r.At, r.Preview, kind, r.Text)
                { Pinned = r.Pinned, Lines = r.Lines <= 0 ? 1 : r.Lines, Image = img, ImageFile = safeImgFile });
            }
            lock (_gate)
            {
                var merged = _items
                    .Concat(loaded)
                    .GroupBy(x => (x.At, x.Kind, x.Text))
                    .Select(g => g.First())
                    .OrderByDescending(i => i.At)
                    .ToList();
                _items.Clear();
                _items.AddRange(merged);
                TrimExcessLocked();
            }
            ItemsChanged?.Invoke();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[clipboard] load: {ex.Message}"); }
    }

    private static void SetOwnerOnlyPermissions(string path, bool isDir)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = isDir
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, mode);
        }
        catch { }
    }

    /// <summary>按保留期清理过期条目（常驻项跳过）。返回被清掉的条数。</summary>
    public int ApplyRetention(string retention)
    {
        int days = RetentionDays(retention);
        if (days <= 0) return 0;
        var cut = DateTime.Now.AddDays(-days);
        int removed;
        lock (_gate)
        {
            removed = _items.RemoveAll(it => !it.Pinned && it.At < cut);
        }
        if (removed > 0) ItemsChanged?.Invoke();
        return removed;
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch { /* 删不掉就留着，下次 Save 会覆盖 */ }
    }

    /// <summary>落盘用的行结构（图片不进 JSON，只记文件名）。</summary>
    private sealed class ClipRow
    {
        public ClipRow() { }
        public ClipRow(DateTime at, string preview, string kind, string text, bool pinned, int lines, string? imageFile)
        {
            At = at; Preview = preview; Kind = kind; Text = text; Pinned = pinned; Lines = lines; ImageFile = imageFile;
        }
        public DateTime At { get; set; }
        public string Preview { get; set; } = "";
        public string Kind { get; set; } = "Text";
        public string Text { get; set; } = "";
        public bool Pinned { get; set; }
        public int Lines { get; set; } = 1;
        public string? ImageFile { get; set; }
    }

    public void Dispose() => _timer?.Stop();
}
