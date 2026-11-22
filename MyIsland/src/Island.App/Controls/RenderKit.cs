using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Island.App.Animation;
using Island.App.Themes;

namespace Island.App.Controls;

/// <summary>
/// 共享渲染层（参考 BrimDeck MonitorDraw）：收起态 IslandCanvas 与展开页 MonitorPage 共用。
/// - FormattedText/Brush/Pen 按内容缓存并 Freeze，重绘只画不建（文本排版是重绘最贵部分）；
/// - 读数数字按"最宽样本"预留宽度；不同字号读数按基线对齐；
/// - 270° 开口表盘（系统读数）与满环（配额）区分；走势图 Catmull-Rom 平滑 + 16% 填充；
/// - 读数分级配色：≥70% Warning、≥90% Critical（Up），正常保持本色。
/// </summary>
internal static class RenderKit
{
    // ---------- 读数分级（BrimDeck AppPresets：warning 70 / critical 90） ----------
    public const double WarnPct = 70, CritPct = 90;

    /// <summary>读数级别色：正常保持本色，超阈值取 Warning/Critical。</summary>
    public static Color LevelColor(double pct, Color normal, Palette pal)
        => pct >= CritPct ? pal.Up : pct >= WarnPct ? pal.Warning : normal;

    // ---------- 缓存：全部 Freeze，每帧零分配 ----------
    private static readonly Dictionary<uint, SolidColorBrush> _brushes = new();
    private static readonly Dictionary<(uint Color, int OpacityPct), SolidColorBrush> _faints = new();
    private static readonly Dictionary<(uint Color, int OpacityPct, double Thickness), Pen> _pens = new();
    private static readonly Dictionary<(string Text, double Size, bool Strong, uint Color, double Ppd), FormattedText> _texts = new();

    private static uint Key(Color c) => (uint)((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);

    public static SolidColorBrush BrushOf(Color c)
    {
        uint k = Key(c);
        if (_brushes.TryGetValue(k, out var b)) return b;
        if (_brushes.Count > 256) _brushes.Clear();
        b = new SolidColorBrush(c); b.Freeze();
        return _brushes[k] = b;
    }

    /// <summary>带不透明度的画刷（压层/轨道/填充用）。</summary>
    public static SolidColorBrush Faint(Color c, double opacity)
    {
        var k = (Key(c), (int)Math.Round(opacity * 100));
        if (_faints.TryGetValue(k, out var b)) return b;
        if (_faints.Count > 256) _faints.Clear();
        b = new SolidColorBrush(c) { Opacity = opacity }; b.Freeze();
        return _faints[k] = b;
    }

    public static Pen PenOf(Color c, double thickness, double opacity = 1)
    {
        var k = (Key(c), (int)Math.Round(opacity * 100), thickness);
        if (_pens.TryGetValue(k, out var p)) return p;
        if (_pens.Count > 128) _pens.Clear();
        p = new Pen(Faint(c, opacity), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return _pens[k] = p;
    }

    /// <summary>缓存的文本排版：同一字符串/字号/字重/颜色/DPI 只排版一次。</summary>
    public static FormattedText Text(string s, double size, Color color, bool strong = false, double ppd = 1)
    {
        var k = (s, size, strong, Key(color), ppd);
        if (_texts.TryGetValue(k, out var t)) return t;
        if (_texts.Count > 512) _texts.Clear();
        t = new FormattedText(s,
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                strong ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, BrushOf(color), ppd);
        return _texts[k] = t;
    }

    /// <summary>限宽省略文本（宽度每次不同故不缓存——BrimDeck MonitorDraw.Trimmed 同款取舍）。</summary>
    public static FormattedText Trimmed(string s, double size, Color color, double width, bool strong, double ppd)
        => new(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                strong ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, BrushOf(color), ppd)
        { MaxTextWidth = Math.Max(1, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };

    /// <summary>按基线放置文本：同一行不同字号的读数坐在同一条基线上。</summary>
    public static void Baseline(DrawingContext dc, FormattedText t, double x, double baseline)
        => dc.DrawText(t, new Point(x, baseline - t.Baseline));

    // ---------- 表盘（BrimDeck MonitorDraw.Dial：270° 开口环 + 字母） ----------
    private const double DialStart = Math.PI * 1.25;   // 从左下角起
    private const double DialSweep = Math.PI * 1.5;    // 顺时针 270°

    public static void Dial(DrawingContext dc, Point center, double size, double ring,
        double? share, Color track, Color arc, Color letterColor, string letter, double ppd)
    {
        double radius = size / 2 - ring / 2;
        // 开口环的可见包围盒居中：下端只到 radius/√2，顶端到 radius（BrimDeck centerVisibleBounds）
        var c = new Point(center.X, center.Y + radius * (1 - Math.Sqrt(0.5)) / 2);
        Arc(dc, c, radius, ring, 1, track);
        if (share is { } v && v > 0.002) Arc(dc, c, radius, ring, Math.Min(1, v), arc);
        var t = Text(letter, Math.Max(1, size * 7 / 16), letterColor, true, ppd);
        dc.DrawText(t, new Point(c.X - t.Width / 2, c.Y - t.Height / 2));
    }

    private static void Arc(DrawingContext dc, Point c, double radius, double thickness, double fraction, Color color)
    {
        Point At(double angle) => new(c.X + radius * Math.Sin(angle), c.Y - radius * Math.Cos(angle));
        double sweep = DialSweep * fraction;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(At(DialStart), false, false);
            ctx.ArcTo(At(DialStart + sweep), new Size(radius, radius), 0,
                sweep > Math.PI, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, PenOf(color, thickness), geo);
    }

    // ---------- 取色（BrimDeck MusicVisuals.Accent 移植：饱和度加权平均 + HSL 亮度钳制） ----------
    /// <summary>从图片提取主彩色（16×16 降采样，灰色像素权重 0.12）。无彩色返回 null。</summary>
    public static Color? DominantColor(BitmapSource? source, bool dark)
    {
        if (source is null) return null;
        try
        {
            var scaled = new TransformedBitmap(source,
                new ScaleTransform(16d / source.PixelWidth, 16d / source.PixelHeight));
            var bitmap = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[16 * 16 * 4];
            bitmap.CopyPixels(pixels, 64, 0);
            double r = 0, g = 0, b = 0, weight = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                double max = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
                double min = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
                double w = Math.Max(.12, (max - min) / 255d) * pixels[i + 3] / 255d;
                b += pixels[i] * w; g += pixels[i + 1] * w; r += pixels[i + 2] * w; weight += w;
            }
            if (weight == 0) return null;
            r /= weight * 255; g /= weight * 255; b /= weight * 255;
            double maxC = Math.Max(r, Math.Max(g, b)), minC = Math.Min(r, Math.Min(g, b));
            double light = (maxC + minC) / 2, delta = maxC - minC;
            double saturation = delta == 0 ? 0 : delta / (1 - Math.Abs(2 * light - 1));
            double hue = delta == 0 ? 0 : maxC == r ? ((g - b) / delta + (g < b ? 6 : 0)) / 6
                : maxC == g ? ((b - r) / delta + 2) / 6 : ((r - g) / delta + 4) / 6;
            saturation = Math.Max(.35, saturation);
            light = dark ? Math.Clamp(light, .62, .78) : Math.Clamp(light, .30, .42);
            double chroma = (1 - Math.Abs(2 * light - 1)) * saturation;
            double x = chroma * (1 - Math.Abs(hue * 6 % 2 - 1)), m = light - chroma / 2;
            (r, g, b) = (hue * 6) switch
            {
                < 1 => (chroma, x, 0d), < 2 => (x, chroma, 0d), < 3 => (0d, chroma, x),
                < 4 => (0d, x, chroma), < 5 => (x, 0d, chroma), _ => (chroma, 0d, x),
            };
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }
        catch { return null; }
    }

    /// <summary>桌面壁纸主色（macOS 式壁纸取色；纯色壁纸/读不到返回 null）。</summary>
    public static Color? WallpaperAccent(bool dark)
    {
        try
        {
            string? path = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Control Panel\Desktop", "Wallpaper", null) as string;
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 32;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return DominantColor(bmp, dark);
        }
        catch { return null; }
    }

    // ---------- 高光边缘（缓存：渐变画笔冻结一份，每帧零分配） ----------
    private static Pen? _rimLightPen;

    /// <summary>顶部 1px 渐隐白边（Liquid Glass 式 rim light），深主题用。</summary>
    public static Pen RimLightPen
    {
        get
        {
            if (_rimLightPen is null)
            {
                var hl = new LinearGradientBrush(Color.FromArgb(70, 255, 255, 255), Colors.Transparent,
                    new Point(0, 0), new Point(0, 1));
                hl.Freeze();
                _rimLightPen = new Pen(hl, 1.1);
                _rimLightPen.Freeze();
            }
            return _rimLightPen;
        }
    }

    // ---------- 平滑走势图（Catmull-Rom + 16% 填充，右缘最新） ----------
    public static void DrawSpark(DrawingContext dc, Rect area, double[]? data, Color color, int n = 36)
    {
        if (data is null || data.Length < 3 || area.Width < 8 || area.Height < 4) return;
        int count = Math.Min(n, data.Length);
        int start = data.Length - count;
        double step = area.Width / (count - 1);
        var pts = new Point[count];
        for (int i = 0; i < count; i++)
            pts[i] = new Point(area.Right - (count - 1 - i) * step,
                area.Bottom - 1 - Easing.Clamp(data[start + i], 0, 1) * (area.Height - 2));
        var line = new StreamGeometry();
        var under = new StreamGeometry();
        using (var ctx = line.Open()) Curve(ctx, pts, false, area.Bottom);
        using (var ctx = under.Open()) Curve(ctx, pts, true, area.Bottom);
        line.Freeze(); under.Freeze();
        dc.DrawGeometry(Faint(color, 0.16), null, under);
        dc.DrawGeometry(null, PenOf(color, 1.2, 0.9), line);
    }

    /// <summary>Catmull-Rom 过点转三次贝塞尔；closed 时向底部闭合供填充，且曲线不许越过底边。</summary>
    private static void Curve(StreamGeometryContext ctx, Point[] p, bool closed, double bottom)
    {
        ctx.BeginFigure(closed ? new Point(p[0].X, bottom) : p[0], closed, closed);
        if (closed) ctx.LineTo(p[0], false, false);
        for (int i = 0; i < p.Length - 1; i++)
        {
            Point a = p[Math.Max(0, i - 1)], b = p[i], c = p[i + 1], d = p[Math.Min(p.Length - 1, i + 2)];
            var c1 = new Point(b.X + (c.X - a.X) / 6, b.Y + (c.Y - a.Y) / 6);
            var c2 = new Point(c.X - (d.X - b.X) / 6, c.Y - (d.Y - b.Y) / 6);
            c1.Y = Math.Min(c1.Y, bottom); c2.Y = Math.Min(c2.Y, bottom);
            ctx.BezierTo(c1, c2, c, true, true);
        }
        if (closed) ctx.LineTo(new Point(p[^1].X, bottom), false, false);
    }

    // ---------- 应用图标（SHGetFileInfo 小图标 + 缓存；进程榜用） ----------
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> _icons = new();

    /// <summary>按可执行文件路径取 16px 图标（失败缓存 null，不重试）。</summary>
    public static ImageSource? AppIcon(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return _icons.GetOrAdd(path, LoadIcon);
    }

    private static ImageSource? LoadIcon(string path)
    {
        try
        {
            var fi = new SHFILEINFO();
            IntPtr r = SHGetFileInfo(path, 0, ref fi, (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SHGFI_SMALLICON);
            if (r == IntPtr.Zero || fi.hIcon == IntPtr.Zero) return null;
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            DestroyIcon(fi.hIcon);
            return src;
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi,
        uint cbFileInfo, uint uFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1;
}
