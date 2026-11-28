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

    /// <summary>温度分级色常量（≤60° 绿 / 60°–90° 黄 / ≥90° 红）。</summary>
    public static readonly Color ThermalCoolColor = Color.FromRgb(0x2F, 0xD0, 0x6B);
    public static readonly Color ThermalWarmColor = Color.FromRgb(0xF5, 0xC2, 0x42);
    public static readonly Color ThermalHotColor = Color.FromRgb(0xFF, 0x45, 0x3A);

    /// <summary>读数级别色：正常保持本色，超阈值取 Warning/Critical。</summary>
    public static Color LevelColor(double pct, Color normal, Palette pal)
        => pct >= CritPct ? pal.Up : pct >= WarnPct ? pal.Warning : normal;

    /// <summary>温度分级纯函数：≤60° 绿色 / >60° 且 &lt;90° 黄色 / ≥90° 红色。</summary>
    public static Color ThermalColor(double celsius)
        => celsius >= 90 ? ThermalHotColor
            : celsius > 60 ? ThermalWarmColor
            : ThermalCoolColor;

    // ---------- 矩形夹紧：渲染链路的最后一道防线 ----------
    /// <summary>
    /// 构造一个宽高非负、数值有限的 Rect。WPF 的 Rect 构造器对负宽高/NaN/Inf 直接抛 ArgumentException，
    /// 而渲染链路上任何一处 NaN（弹簧值、快照值、窗口尺寸）都会让整窗以未处理异常崩掉
    /// （2026-11-23 首帧 Arrange 即崩，crash.log：宽度和高度必须为非负值），
    /// 因此所有渲染用矩形统一从这里出，坏值只会被削平，不会再把进程带走。
    /// </summary>
    public static Rect SafeRect(double x, double y, double w, double h)
        => new(Fin(x), Fin(y), Size(w), Size(h));

    private static double Fin(double v) => double.IsFinite(v) ? v : 0;
    private static double Size(double v) => double.IsFinite(v) ? (v < 0 ? 0 : v) : 0;

    // ---------- 缓存：全部 Freeze，每帧零分配 ----------
    // 用 ConcurrentDictionary：这几个缓存以前是普通 Dictionary，将来谁在后台线程调
    // BrushOf/Faint/PenOf/Text 就会撞 "A concurrent update was performed on this collection"。
    // 现在没有后台线程调用点，但换成并发字典零成本，也和 _icons/_wallpaperAccents 保持一致。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, SolidColorBrush> _brushes = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(uint Color, int OpacityPct), SolidColorBrush> _faints = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(uint Color, int OpacityPct, double Thickness), Pen> _pens = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Text, double Size, bool Strong, uint Color, double Ppd), FormattedText> _texts = new();

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

    private static readonly FontFamily UiFontFamily = new("Segoe UI");
    private static readonly Typeface NormalTypeface = new(UiFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface StrongTypeface = new(UiFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    /// <summary>缓存的文本排版：同一字符串/字号/字重/颜色/DPI 只排版一次。</summary>
    public static FormattedText Text(string s, double size, Color color, bool strong = false, double ppd = 1)
    {
        double safeSize = double.IsFinite(size) ? Math.Max(1, size) : 1;
        double safePpd = double.IsFinite(ppd) && ppd > 0 ? ppd : 1;
        var k = (s, safeSize, strong, Key(color), safePpd);
        if (_texts.TryGetValue(k, out var t)) return t;
        if (_texts.Count > 320) _texts.Clear();
        t = new FormattedText(s,
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            strong ? StrongTypeface : NormalTypeface,
            safeSize, BrushOf(color), safePpd);
        return _texts[k] = t;
    }

    /// <summary>限宽省略文本（复用缓存的 Typeface）。</summary>
    public static FormattedText Trimmed(string s, double size, Color color, double width, bool strong, double ppd)
        => new(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            strong ? StrongTypeface : NormalTypeface,
            double.IsFinite(size) ? Math.Max(1, size) : 1, BrushOf(color),
            double.IsFinite(ppd) && ppd > 0 ? ppd : 1)
        { MaxTextWidth = Math.Max(1, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };

    /// <summary>按基线放置文本：同一行不同字号的读数坐在同一条基线上。</summary>
    public static void Baseline(DrawingContext dc, FormattedText t, double x, double baseline)
        => dc.DrawText(t, new Point(x, baseline - t.Baseline));

    // ---------- 360° 圆环与渐变频谱柱（对齐 macOS 版收起态胶囊） ----------

    /// <summary>
    /// 360° 闭合圆环（对齐 macOS 版收起态「环包字母/图标」）：整圈底环 + 从正上方顺时针推进的彩色弧。
    /// </summary>
    public static void Ring(DrawingContext dc, Point center, double diameter, double thickness,
        double share, Color track, Color arc)
    {
        double safeDia = double.IsFinite(diameter) ? Math.Max(1.0, diameter) : 1.0;
        double safeTh = double.IsFinite(thickness) ? Math.Max(0.2, thickness) : 0.2;
        double radius = Math.Max(0.5, safeDia / 2 - safeTh / 2);
        dc.DrawEllipse(null, PenOf(track, safeTh, 0.9), center, radius, radius);
        double f = double.IsFinite(share) ? Math.Clamp(share, 0, 1) : 0;
        if (f > 0.004)
        {
            Point At(double a) => new(center.X + radius * Math.Sin(a), center.Y - radius * Math.Cos(a));
            double sweep = f * 2 * Math.PI;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                if (sweep <= Math.PI)
                {
                    ctx.BeginFigure(At(0), false, false);
                    ctx.ArcTo(At(sweep), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                }
                else
                {
                    ctx.BeginFigure(At(0), false, false);
                    ctx.ArcTo(At(Math.PI), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                    ctx.ArcTo(At(sweep), new Size(radius, radius), 0, sweep - Math.PI > Math.PI, SweepDirection.Clockwise, true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, PenOf(arc, safeTh), geo);
        }
    }

    /// <summary>渐变频谱柱（对齐 macOS 版 dash 收起态的 CPU 历史蓝→青柱状图）。</summary>
    public static void Spectrum(DrawingContext dc, Rect box, ReadOnlySpan<double> values, int count,
        Color from, Color to)
    {
        if (count < 2 || box.Width < 4 || box.Height < 2) return;
        double bw = Math.Max(2.4, Math.Min(4.0, box.Width / count * 0.7));
        double gap = count > 1 ? Math.Max(0, (box.Width - bw * count) / (count - 1)) : 0;
        for (int i = 0; i < count; i++)
        {
            double v = double.IsFinite(values[i]) ? Math.Clamp(values[i], 0, 1) : 0;
            double h = Math.Max(3, v * box.Height);
            double x = box.X + i * (bw + gap);
            double t = count > 1 ? (double)i / (count - 1) : 0;
            var c = Color.FromRgb(
                ToByte((from.R + (to.R - from.R) * t) / 255.0),
                ToByte((from.G + (to.G - from.G) * t) / 255.0),
                ToByte((from.B + (to.B - from.B) * t) / 255.0));
            dc.DrawRoundedRectangle(BrushOf(c), null, SafeRect(x, box.Bottom - h, bw, h), 1, 1);
        }
    }

    // ---------- 表盘（BrimDeck MonitorDraw.Dial：270° 开口环 + 字母） ----------
    private const double DialStart = Math.PI * 1.25;   // 从左下角起
    private const double DialSweep = Math.PI * 1.5;    // 顺时针 270°

    public static void Dial(DrawingContext dc, Point center, double size, double ring,
        double? share, Color track, Color arc, Color letterColor, string letter, double ppd)
    {
        // size <= ring 时 radius 会变成负数 → new Size(负,负) 直接抛 ArgumentException，
        // 而这是 OnRender 里的路径，抛了整窗崩。这里把尺寸/环宽/半径都夹到合法范围。
        double safeSize = double.IsFinite(size) ? Math.Max(0, size) : 0;
        double safeRing = double.IsFinite(ring) ? Math.Max(0.1, ring) : 0.1;
        double radius = Math.Max(0.5, safeSize / 2 - safeRing / 2);
        // 开口环的可见包围盒居中：下端只到 radius/√2，顶端到 radius（BrimDeck centerVisibleBounds）
        var c = new Point(center.X, center.Y + radius * (1 - Math.Sqrt(0.5)) / 2);
        Arc(dc, c, radius, ring, 1, track);
        if (share is { } v && v > 0.002) Arc(dc, c, radius, ring, Math.Min(1, v), arc);
        var t = Text(letter, Math.Max(1, size * 7 / 16), letterColor, true, ppd);
        dc.DrawText(t, new Point(c.X - t.Width / 2, c.Y - t.Height / 2));
    }

    private static void Arc(DrawingContext dc, Point c, double radius, double thickness, double fraction, Color color)
    {
        // 兜底：半径/线宽/占比非法时不能让 new Size(...) 或 Pen 抛异常（渲染链路抛异常 = 整窗崩）
        double r = double.IsFinite(radius) ? Math.Max(0.5, radius) : 0.5;
        double th = double.IsFinite(thickness) ? Math.Max(0.1, thickness) : 0.1;
        double f = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        Point At(double angle) => new(c.X + r * Math.Sin(angle), c.Y - r * Math.Cos(angle));
        double sweep = DialSweep * f;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(At(DialStart), false, false);
            ctx.ArcTo(At(DialStart + sweep), new Size(r, r), 0,
                sweep > Math.PI, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, PenOf(color, th), geo);
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
            return Color.FromRgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
        }
        catch { return null; }
    }

    /// <summary>0..1 → 字节：先夹到 [0,1] 再乘 255 并夹到 0..255。
    /// 不夹的话 (r+m) 能到 1.28，(byte)326 会回绕成 50，颜色直接错乱。</summary>
    private static byte ToByte(double v)
    {
        if (!double.IsFinite(v)) return 0;
        double s = Math.Clamp(v, 0, 1) * 255.0;
        return (byte)Math.Clamp(s, 0, 255);
    }

    // 壁纸主色缓存：解码 16×16 位图 + 取色要 30–150ms，同一张壁纸不重复解码。
    // 键里带上壁纸文件的修改时间：换了壁纸（路径不变）会自动失效重算。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Color?> _wallpaperAccents = new();

    /// <summary>桌面壁纸主色（macOS 式壁纸取色；纯色壁纸/读不到返回 null）。</summary>
    public static Color? WallpaperAccent(bool dark)
    {
        try
        {
            string? path = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Control Panel\Desktop", "Wallpaper", null) as string;
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;
            long stamp = System.IO.File.GetLastWriteTimeUtc(path).Ticks;
            string key = path + "|" + stamp + "|" + (dark ? "d" : "l");
            return _wallpaperAccents.GetOrAdd(key, _ => DecodeWallpaperAccent(path, dark));
        }
        catch { return null; }
    }

    /// <summary>解码壁纸位图并取主色（只由 WallpaperAccent 调用，结果进缓存）。</summary>
    private static Color? DecodeWallpaperAccent(string path, bool dark)
    {
        try
        {
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

    // ---------- 缓存：顶部 sheen / 底部内阴影渐变（每帧零分配） ----------
    private static LinearGradientBrush? _sheenBrush;
    private static LinearGradientBrush? _shadeBrush;

    /// <summary>顶部高光带渐变（白 12% → 透明，90°）：冻结的静态缓存，每帧零分配。</summary>
    public static LinearGradientBrush SheenBrush
    {
        get
        {
            if (_sheenBrush is null)
            {
                var b = new LinearGradientBrush(
                    Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF), Colors.Transparent, 90);
                b.Freeze();
                _sheenBrush = b;
            }
            return _sheenBrush;
        }
    }

    /// <summary>底部内阴影渐变（透明 → 黑 18%，90°）：冻结的静态缓存，每帧零分配。</summary>
    public static LinearGradientBrush ShadeBrush
    {
        get
        {
            if (_shadeBrush is null)
            {
                var b = new LinearGradientBrush(Colors.Transparent,
                    Color.FromArgb(0x2E, 0x00, 0x00, 0x00), 90);
                b.Freeze();
                _shadeBrush = b;
            }
            return _shadeBrush;
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

    public static ImageSource? AppIcon(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_icons.Count > 96) _icons.Clear();
        return _icons.GetOrAdd(path, LoadIcon);
    }

    /// <summary>修剪瞬态文本排版与进程图标缓存（保留体积极小的画刷/画笔热缓存），供内存清理与定期瘦身调用。</summary>
    public static void TrimCaches()
    {
        _texts.Clear();
        _icons.Clear();
        if (_wallpaperAccents.Count > 8) _wallpaperAccents.Clear();
    }

    private static ImageSource? LoadIcon(string path)
    {
        var fi = new SHFILEINFO();
        try
        {
            IntPtr r = SHGetFileInfo(path, 0, ref fi, (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SHGFI_SMALLICON);
            if (r == IntPtr.Zero || fi.hIcon == IntPtr.Zero) return null;
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally
        {
            if (fi.hIcon != IntPtr.Zero) DestroyIcon(fi.hIcon);
        }
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
