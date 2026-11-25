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

    /// <summary>颜色的 32 位键：调用方想记"上次用的颜色"来跳过没变化的绘制时用。</summary>
    public static uint KeyOf(Color c) => Key(c);

    /// <summary>
    /// 画布统一字族。
    /// 显式给出 CJK 回退（微软雅黑 UI 自带拉丁字形）：不指定的话中文字符会走
    /// DirectWrite 的**系统回退链**，额外加载一整套中文字体（实测单个中文字族约 20MB 级）。
    /// </summary>
    private static readonly FontFamily UiFont = new("Segoe UI, Microsoft YaHei UI");

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

    // ---------- 径向渐变（小形象眼周光晕）：按颜色缓存一份冻结的 ----------
    // 2026-11-24：PetFace 每帧给每只眼 new 一个未冻结的 RadialGradientBrush，录制绘制指令时还会被再克隆一份。
    // 渐变的形状是固定的（中心 0.5,0.5、半径 0.5、相对包围盒 → 透明），只跟"光晕色"有关，所以按颜色缓存。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, RadialGradientBrush> _radials = new();

    /// <summary>"该色 → 透明"的径向渐变：中心 (0.5, 0.5)、半径 0.5（相对包围盒），按颜色缓存并 Freeze。</summary>
    public static RadialGradientBrush RadialBrushOf(Color center)
    {
        uint k = Key(center);
        if (_radials.TryGetValue(k, out var b)) return b;
        if (_radials.Count > 64) _radials.Clear();
        b = new RadialGradientBrush(center, Colors.Transparent)
        {
            Center = new Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5,
        };
        b.Freeze();
        return _radials[k] = b;
    }

    /// <summary>缓存的文本排版：同一字符串/字号/字重/颜色/DPI 只排版一次。</summary>
    public static FormattedText Text(string s, double size, Color color, bool strong = false, double ppd = 1)
    {
        // 字号/DPI 非法时 FormattedText 会抛（emSize<=0、pixelsPerDip<=0），这里是渲染链路，
        // 抛异常等于整窗崩 —— 夹到合法值（和 SafeRect 同一个思路：坏值只被削平）
        double safeSize = double.IsFinite(size) ? Math.Max(1, size) : 1;
        double safePpd = double.IsFinite(ppd) && ppd > 0 ? ppd : 1;
        var k = (s, safeSize, strong, Key(color), safePpd);
        if (_texts.TryGetValue(k, out var t)) return t;
        if (_texts.Count > 512) _texts.Clear();
        t = new FormattedText(s,
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(UiFont, FontStyles.Normal,
                strong ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            safeSize, BrushOf(color), safePpd);
        return _texts[k] = t;
    }

    // 限宽省略文本的缓存（2026-11-24 展开态降耗）。键里带上宽度：读数是等宽体，名字旁边那段读数的宽度
    // 只有寥寥几种（"1.5%" 与 "12.3%"），"文本 + 可用宽度"的组合翻来覆去就那么几个，命中率很高。
    // 不缓存时监控页每次重绘都要重新排版 7 段限宽文本（6 个进程名 + 显卡名），离屏探针实测约 3ms，
    // 占一次"读数没变"的重绘的大半。现有调用点拿到后只画不改，所以共享同一份排版是安全的；
    // 以后谁要改返回值（设 MaxTextWidth 之类），先 clone。
    // 两份缓存各有上限，超了整体清空（与 _texts 同一个取舍：宁可偶尔重排，也不让它无限长）。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Text, double Size, bool Strong, uint Color, double Width, double Ppd), FormattedText> _trimmedTexts = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Text, double Size, bool Strong, uint Color, double Width, double Ppd), FormattedText> _trimmedMono = new();

    /// <summary>限宽夹到合法值：非数 / 负数 / 0 都按 1 算（原来 NaN 会原样漏进 FormattedText）。</summary>
    private static double SafeWidth(double width) => double.IsFinite(width) ? Math.Max(1, width) : 1;

    private static Typeface Face(bool strong, bool mono)
        => new(mono ? MonoFont : UiFont, FontStyles.Normal,
            strong ? (mono ? FontWeights.Bold : FontWeights.SemiBold) : FontWeights.Normal, FontStretches.Normal);

    private static FormattedText Ellipsized(string s, double size, Color color, double width, bool strong, double ppd, bool mono)
        => new(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face(strong, mono), size, BrushOf(color), ppd)
        { MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };

    /// <summary>限宽省略文本：同一串字在同一种可用宽度下只排版一次（宽度变了就是另一份）。</summary>
    public static FormattedText Trimmed(string s, double size, Color color, double width, bool strong, double ppd)
    {
        double safeSize = double.IsFinite(size) ? Math.Max(1, size) : 1;
        double safePpd = double.IsFinite(ppd) && ppd > 0 ? ppd : 1;
        double w = SafeWidth(width);
        var k = (s, safeSize, strong, Key(color), w, safePpd);
        if (_trimmedTexts.TryGetValue(k, out var t)) return t;
        if (_trimmedTexts.Count > 256) _trimmedTexts.Clear();
        return _trimmedTexts[k] = Ellipsized(s, safeSize, color, w, strong, safePpd, mono: false);
    }

    /// <summary>按基线放置文本：同一行不同字号的读数坐在同一条基线上。</summary>
    public static void Baseline(DrawingContext dc, FormattedText t, double x, double baseline)
        => dc.DrawText(t, new Point(x, baseline - t.Baseline));

    // ---------- 数据字体：等宽（2026-11-24 视觉重做，暗色科技） ----------
    // 读数、单位、拉丁标签用等宽体（Consolas）："终端感"来自字体的等宽节奏，不是加装饰。中文落在微软雅黑 UI 上
    //（Consolas 没有汉字，按字体回退排在同一行，基线由排版引擎对齐）。等宽体的数字天然等宽：读数从 11 跳到 88 时
    // 整行不抖（界面字体 Segoe UI 的数字是比例宽的，"1" 只占 "0" 的一半左右）。
    // ⚠️ 一段读数只调一次 DrawText（整串排版）。曾经为了"数字居中进格子"逐字画：每一次 DrawText 在软件渲染里
    //    约合 0.2ms，仪表盘视图一帧多出 5ms（离屏探针实测 7 → 12ms/帧）。换数据字体时确认新字体的数字是等宽的，
    //    DesignSystemTests 守着这一条。
    private static readonly FontFamily MonoFont = new("Consolas, Microsoft YaHei UI");
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Text, double Size, bool Strong, uint Color, double Ppd), FormattedText> _monoTexts = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(double Size, bool Strong, double Ppd), double> _digitCells = new();

    /// <summary>缓存的等宽文本排版（读数、单位、拉丁标签、进程名）。Consolas 只有常规和粗体两档。</summary>
    public static FormattedText Mono(string s, double size, Color color, bool strong = false, double ppd = 1)
    {
        // 字号/DPI 非法时 FormattedText 会抛，这里是渲染链路 —— 夹到合法值（与 Text 同样的处理）
        double safeSize = double.IsFinite(size) ? Math.Max(1, size) : 1;
        double safePpd = double.IsFinite(ppd) && ppd > 0 ? ppd : 1;
        var k = (s, safeSize, strong, Key(color), safePpd);
        if (_monoTexts.TryGetValue(k, out var t)) return t;
        if (_monoTexts.Count > 512) _monoTexts.Clear();
        t = new FormattedText(s,
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(MonoFont, FontStyles.Normal, strong ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            safeSize, BrushOf(color), safePpd);
        return _monoTexts[k] = t;
    }

    /// <summary>限宽省略的等宽文本（进程名这类）：按"文本 + 可用宽度"缓存，见 <see cref="Trimmed"/> 上面的说明。</summary>
    public static FormattedText MonoTrimmed(string s, double size, Color color, double width, bool strong, double ppd)
    {
        double safeSize = double.IsFinite(size) ? Math.Max(1, size) : 1;
        double safePpd = double.IsFinite(ppd) && ppd > 0 ? ppd : 1;
        double w = SafeWidth(width);
        var k = (s, safeSize, strong, Key(color), w, safePpd);
        if (_trimmedMono.TryGetValue(k, out var t)) return t;
        if (_trimmedMono.Count > 256) _trimmedMono.Clear();
        return _trimmedMono[k] = Ellipsized(s, safeSize, color, w, strong, safePpd, mono: true);
    }

    /// <summary>同一种颜色换个不透明度（辉光用的半透明版本）。</summary>
    public static Color Halo(Color c, double alpha)
        => Color.FromArgb((byte)Math.Clamp((int)Math.Round(alpha * 255), 0, 255), c.R, c.G, c.B);

    /// <summary>"0"–"9" 里最宽的那个在这组字号 / 字重 / DPI 下的宽度（数字格子的宽度）。</summary>
    public static double DigitCell(double size, bool strong, double ppd)
    {
        double safeSize = double.IsFinite(size) ? Math.Max(1, size) : 1;
        double safePpd = double.IsFinite(ppd) && ppd > 0 ? ppd : 1;
        return _digitCells.GetOrAdd((safeSize, strong, safePpd), k =>
        {
            double w = 0;
            for (int d = 0; d < 10; d++) w = Math.Max(w, Mono(d.ToString(), k.Size, Colors.Black, k.Strong, k.Ppd).Width);
            return w;
        });
    }

    /// <summary>一段读数用 <see cref="Num"/> 排出来占的宽度（右对齐 / 给后面的文字让位时先量）。</summary>
    public static double NumWidth(string s, double size, bool strong, double ppd)
        => Mono(s, size, Colors.Black, strong, ppd).WidthIncludingTrailingWhitespace;

    /// <summary>按基线画一段读数（等宽体，数字等宽）。返回右端 x。</summary>
    public static double Num(DrawingContext dc, string s, double x, double baseline, double size, Color color, bool strong, double ppd)
    {
        var t = Mono(s, size, color, strong, ppd);
        dc.DrawText(t, new Point(x, baseline - t.Baseline));
        return x + t.WidthIncludingTrailingWhitespace;
    }

    /// <summary>1px 发丝线（对齐到像素，半透明的线落在半像素上会发虚成两行）。</summary>
    public static void Hairline(DrawingContext dc, Palette pal, double x1, double x2, double y)
        => dc.DrawLine(PenOf(pal.Hairline, 1), new Point(x1, Math.Round(y) + 0.5), new Point(x2, Math.Round(y) + 0.5));

    // ---------- 顶部辉光（暗色科技的签名） ----------
    // "近黑底 + 一束光"：本体顶部中央透出一团强调色，径向渐变到透明。相对本体包围盒定位，与本体尺寸无关，
    // 所以按"颜色 + 不透明度 + 纵向半径"缓存，展开 / 收起动画的每一帧都复用同一份冻结的画刷。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(uint Color, int RadiusY), RadialGradientBrush> _spotlights = new();

    /// <summary>本体顶部的辉光画刷（中心在上边中点）。alpha 是中心的峰值不透明度，radiusY 是相对本体高度的纵向半径。</summary>
    public static RadialGradientBrush Spotlight(Color accent, double alpha, double radiusY)
    {
        double a = double.IsFinite(alpha) ? Math.Clamp(alpha, 0, 1) : 0;
        double ry = double.IsFinite(radiusY) ? Math.Max(0.05, radiusY) : 0.05;
        var k = (Key(Halo(accent, a)), (int)Math.Round(ry * 1000));
        return _spotlights.GetOrAdd(k, key =>
        {
            Color peak = Color.FromRgb((byte)(key.Color >> 16), (byte)(key.Color >> 8), (byte)key.Color);
            var b = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.02), GradientOrigin = new Point(0.5, 0.02),
                RadiusX = 0.62, RadiusY = key.RadiusY / 1000.0,
            };
            b.GradientStops.Add(new GradientStop(Halo(peak, a), 0));
            b.GradientStops.Add(new GradientStop(Halo(peak, a * 0.45), 0.45));
            b.GradientStops.Add(new GradientStop(Halo(peak, 0), 1));
            b.Freeze();
            return b;
        });
    }

    /// <summary>细进度条（圆头轨道 + 填充），share 为 0..1；填充短于一个高度时不画（圆头会糊成一团）。</summary>
    public static void Meter(DrawingContext dc, Rect r, double share, Palette pal, Color fill)
    {
        double rr = r.Height / 2;
        dc.DrawRoundedRectangle(BrushOf(pal.Track), null, r, rr, rr);
        double w = (double.IsFinite(share) ? Math.Clamp(share, 0, 1) : 0) * r.Width;
        if (w < r.Height) return;
        dc.DrawRoundedRectangle(BrushOf(fill), null, SafeRect(r.X, r.Y, w, r.Height), rr, rr);
    }

    // ---------- 表盘（BrimDeck MonitorDraw.Dial：270° 开口环 + 字母） ----------
    private const double DialStart = Math.PI * 1.25;   // 从左下角起
    private const double DialSweep = Math.PI * 1.5;    // 顺时针 270°

    public static void Dial(DrawingContext dc, Point center, double size, double ring,
        double? share, Color track, Color arc, Color letterColor, string letter, double ppd, double glow = 0)
    {
        // size <= ring 时 radius 会变成负数 → new Size(负,负) 直接抛 ArgumentException，
        // 而这是 OnRender 里的路径，抛了整窗崩。这里把尺寸/环宽/半径都夹到合法范围。
        double safeSize = double.IsFinite(size) ? Math.Max(0, size) : 0;
        double safeRing = double.IsFinite(ring) ? Math.Max(0.1, ring) : 0.1;
        // 辉光占掉的宽度要算进半径，否则描出来的光晕会被 size 裁掉的视觉边界压扁
        double safeGlow = double.IsFinite(glow) ? Math.Max(0, glow) : 0;
        double radius = Math.Max(0.5, safeSize / 2 - safeGlow - safeRing / 2);
        // 开口环的可见包围盒居中：下端只到 radius/√2，顶端到 radius（BrimDeck centerVisibleBounds）
        var c = new Point(center.X, center.Y + radius * (1 - Math.Sqrt(0.5)) / 2);
        // 辉光（glow > 0 时）：同一道弧先用两条更宽、更淡的线描一遍（暗色科技的签名手法，只给主角环用）
        if (safeGlow > 0 && share is { } v0 && v0 > 0.002)
        {
            Arc(dc, c, radius, safeRing + safeGlow * 2.6, Math.Min(1, v0), Halo(arc, 0.10));
            Arc(dc, c, radius, safeRing + safeGlow, Math.Min(1, v0), Halo(arc, 0.24));
        }
        Arc(dc, c, radius, safeRing, 1, track);
        if (share is { } v && v > 0.002) Arc(dc, c, radius, safeRing, Math.Min(1, v), arc);
        // 空字母 = 只画环（监控页的 CPU 主读数环：字母由旁边的标签承担）
        if (letter.Length == 0) return;
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

    // ---------- 360° 完整圆环（mac RenderKit.Ring 同款）----------
    /// <summary>
    /// 360° 完整圆环：底环 + 按占比的彩色弧（从 12 点顺时针）。
    /// 与 <see cref="Dial"/>（270° 开口环 + 环内字母）不同：这里是闭环，数字放环外。
    /// 收起态用这个；展开面板的 CPU 主读数环仍是 <see cref="Dial"/>，两者并存（mac 侧同样并存）。
    /// 源：src/Island.Avalonia/RenderKit.cs L184-L215。
    /// </summary>
    public static void Ring(DrawingContext dc, Point center, double diameter, double thickness,
        double share, Color track, Color arc)
    {
        double radius = CompactSpec.RingRadius(diameter, thickness);
        // CompactSpec.RingRadius 按 mac 取消了 0.5 下限（RenderKit.Ring:187），
        // 于是 thickness > diameter 时半径为负（直径为 NaN 时半径也是 NaN）。
        // 实测（.NET 8 / WPF）：负半径、NaN 半径都不会抛异常，但画出来是无意义的形状，
        // 所以这里提前返回不画，交给调用方保证 thickness <= diameter。
        if (radius <= 0) return;
        // 底环：整圈
        dc.DrawEllipse(null, PenOf(track, thickness, 0.9), center, radius, radius);
        double sweep = CompactSpec.RingSweep(share);
        // 占比死区与 mac 一致（RenderKit.cs L191）：f <= 0.004 不画弧
        if (sweep <= CompactSpec.RingDeadZone * 2 * Math.PI) return;
        // 弧：从 12 点（-90°）顺时针；超过半圈要拆两段（一次 ArcTo 画不出 180° 以上）
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            Point At(double a) => new(center.X + radius * Math.Sin(a), center.Y - radius * Math.Cos(a));
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
            // WPF 的 StreamGeometryContext 没有 EndFigure（那是 Avalonia 的 API）：
            // figure 随 using 结束时关闭上下文而结束，开口与否在 BeginFigure 的 isClosed 上
            // （这里 false = 不补闭合线段，与 mac 侧 EndFigure(false) 等价）。
        }
        geo.Freeze();
        dc.DrawGeometry(null, PenOf(arc, thickness), geo);
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
    public static void DrawSpark(DrawingContext dc, Rect area, double[]? data, Color color, int n = 36,
        double strokeWidth = 1.2)
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
        dc.DrawGeometry(null, PenOf(color, strokeWidth, 0.9), line);
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

/// <summary>
/// 发光文字的位图缓存（2026-11-24，暗色科技里 CPU 主读数的辉光）。
/// 做法：把文字画进一张**半分辨率**的小位图，手工做三遍可分离的盒式模糊（叠加起来等效高斯），
/// 之后每帧只是把这张图放大贴上去（双线性插值，天然平滑）。文字 / 颜色没变就直接复用，一个字节都不动。
/// 为什么不用现成的：BlurEffect 在软件渲染里每个渲染通道都要重算一遍模糊（约 3.5ms，而毛玻璃每更新一次
/// 就是一个渲染通道）；多层错位描边每次重绘约 10ms（一次 DrawText 在软件渲染里约合 0.2ms）。
/// 这里重做一次不到 1ms，每帧贴图约 0.3ms。位图是预乘 alpha（Pbgra32），四个通道一起模糊，预乘关系不变。
/// </summary>
internal sealed class GlowText
{
    private const double Scale = 0.5;     // 半分辨率：辉光本来就是模糊的
    private const int Radius = 3;         // 半分辨率下的盒式模糊半径（三遍叠加 ≈ 全分辨率 10px 的高斯）
    private const double Strength = 0.8;  // 辉光强度（直接乘进各通道，等于给整张图加不透明度）

    private (string Text, uint Color, double Size, bool Strong, double Ppd)? _key;
    private BitmapSource _bmp = null!;
    private double _pad, _w, _h, _top;

    /// <summary>
    /// 取辉光位图。返回 (位图, 摆放矩形)：矩形以文字的落笔点（左端基线点）为原点。
    /// 调用方先在矩形处贴图，再在落笔点照常写实心字。
    /// </summary>
    public (BitmapSource Bmp, Rect Bounds) Get(string text, double size, Color color, bool strong, double ppd)
    {
        uint ck = RenderKit.KeyOf(color);
        if (_key == (text, ck, size, strong, ppd)) return (_bmp, new Rect(-_pad, _top - _pad, _w, _h));

        // 字号 / DPI 非法时 Mono 会夹到合法值；下面的尺寸再夹一次（NaN 转整数是未定义的）
        var t = RenderKit.Mono(text, size, color, strong, ppd);
        _pad = double.IsFinite(size) ? Math.Max(1, size) * 0.5 : 0.5;
        _w = t.WidthIncludingTrailingWhitespace + 2 * _pad;
        _h = t.Height + 2 * _pad;
        _top = -t.Baseline;
        int bw = Math.Max(1, (int)Math.Round(_w * Scale));
        int bh = Math.Max(1, (int)Math.Round(_h * Scale));

        var rtb = new RenderTargetBitmap(bw, bh, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var c = dv.RenderOpen()) c.DrawText(t, new Point(_pad, _pad + t.Baseline));
        rtb.Render(dv);

        var wb = new WriteableBitmap(rtb);
        int stride = wb.BackBufferStride;
        var px = new byte[stride * bh];
        var tmp = new byte[px.Length];
        wb.CopyPixels(px, stride, 0);
        for (int pass = 0; pass < 3; pass++) { BlurH(px, tmp, bw, bh, stride); BlurV(tmp, px, bw, bh, stride); }
        for (int i = 0; i < px.Length; i++) px[i] = (byte)(px[i] * Strength);
        wb.WritePixels(new Int32Rect(0, 0, bw, bh), px, stride, 0);
        wb.Freeze();
        _bmp = wb;
        _key = (text, ck, size, strong, ppd);
        return (_bmp, new Rect(-_pad, _top - _pad, _w, _h));
    }

    private static int Edge(int i, int n) => i < 0 ? 0 : i >= n ? n - 1 : i;

    // 一维盒式模糊（滑动窗口和，边缘夹紧）：横向一遍、纵向一遍。三个通道方向无关，四个字节通道各自处理。
    private static void BlurH(byte[] src, byte[] dst, int w, int h, int stride)
    {
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int ch = 0; ch < 4; ch++)
            {
                int sum = 0;
                for (int k = -Radius; k <= Radius; k++) sum += src[row + Edge(k, w) * 4 + ch];
                for (int x = 0; x < w; x++)
                {
                    dst[row + x * 4 + ch] = (byte)((sum + Radius) / (2 * Radius + 1));
                    sum += src[row + Edge(x + Radius + 1, w) * 4 + ch] - src[row + Edge(x - Radius, w) * 4 + ch];
                }
            }
        }
    }

    private static void BlurV(byte[] src, byte[] dst, int w, int h, int stride)
    {
        for (int x = 0; x < w; x++)
        {
            for (int ch = 0; ch < 4; ch++)
            {
                int sum = 0;
                for (int k = -Radius; k <= Radius; k++) sum += src[Edge(k, h) * stride + x * 4 + ch];
                for (int y = 0; y < h; y++)
                {
                    dst[y * stride + x * 4 + ch] = (byte)((sum + Radius) / (2 * Radius + 1));
                    sum += src[Edge(y + Radius + 1, h) * stride + x * 4 + ch] - src[Edge(y - Radius, h) * stride + x * 4 + ch];
                }
            }
        }
    }
}
