using Avalonia;
using Avalonia.Media;
using Island.App.Animation;
using Island.App.Themes;

namespace Island.Avalonia;

/// <summary>
/// Avalonia 版渲染助手（与 Windows 版 RenderKit 同源）：文本/画刷/画笔按内容缓存，
/// 270° 开口表盘、Catmull-Rom 平滑走势、70/90 分级配色。
/// </summary>
internal static class RenderKit
{
    public const double WarnPct = 70, CritPct = 90;

    public static Color LevelColor(double pct, Color normal, Palette pal)
        => pct >= CritPct ? pal.Up : pct >= WarnPct ? pal.Warning : normal;

    private static readonly Dictionary<uint, SolidColorBrush> _brushes = new();
    private static readonly Dictionary<(uint Color, int OpacityPct), SolidColorBrush> _faints = new();
    private static readonly Dictionary<(uint Color, int OpacityPct, double Thickness), Pen> _pens = new();
    private static readonly Dictionary<(string Text, double Size, bool Strong, uint Color), FormattedText> _texts = new();

    private static uint Key(Color c) => (uint)((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);

    public static SolidColorBrush BrushOf(Color c)
    {
        uint k = Key(c);
        if (_brushes.TryGetValue(k, out var b)) return b;
        if (_brushes.Count > 256) _brushes.Clear();
        b = new SolidColorBrush(c);
        return _brushes[k] = b;
    }

    public static SolidColorBrush Faint(Color c, double opacity)
    {
        var k = (Key(c), (int)Math.Round(opacity * 100));
        if (_faints.TryGetValue(k, out var b)) return b;
        if (_faints.Count > 256) _faints.Clear();
        b = new SolidColorBrush(c, opacity);
        return _faints[k] = b;
    }

    public static Pen PenOf(Color c, double thickness, double opacity = 1)
    {
        var k = (Key(c), (int)Math.Round(opacity * 100), thickness);
        if (_pens.TryGetValue(k, out var p)) return p;
        if (_pens.Count > 128) _pens.Clear();
        p = new Pen(Faint(c, opacity), thickness) { LineCap = PenLineCap.Round };
        return _pens[k] = p;
    }

    private static readonly Typeface _regular = new("Segoe UI, PingFang SC, Helvetica Neue, sans-serif");
    private static readonly Typeface _strong = new("Segoe UI, PingFang SC, Helvetica Neue, sans-serif",
        FontStyle.Normal, FontWeight.SemiBold);

    public static FormattedText Text(string s, double size, Color color, bool strong = false)
    {
        var k = (s, size, strong, Key(color));
        if (_texts.TryGetValue(k, out var t)) return t;
        if (_texts.Count > 512) _texts.Clear();
        t = new FormattedText(s,
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            strong ? _strong : _regular, size, BrushOf(color));
        return _texts[k] = t;
    }

    public static FormattedText Trimmed(string s, double size, Color color, double width, bool strong)
        => new(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            strong ? _strong : _regular, size, BrushOf(color))
        { MaxTextWidth = Math.Max(1, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };

    public static void Baseline(DrawingContext dc, FormattedText t, double x, double baseline)
        => dc.DrawText(t, new Point(x, baseline - t.Baseline));

    // ---------- 表盘（270° 开口环 + 字母） ----------
    private const double DialStart = Math.PI * 1.25;
    private const double DialSweep = Math.PI * 1.5;

    public static void Dial(DrawingContext dc, Point center, double size, double ring,
        double? share, Color track, Color arc, Color letterColor, string letter)
    {
        double radius = size / 2 - ring / 2;
        var c = new Point(center.X, center.Y + radius * (1 - Math.Sqrt(0.5)) / 2);
        Arc(dc, c, radius, ring, 1, track);
        if (share is { } v && v > 0.002) Arc(dc, c, radius, ring, Math.Min(1, v), arc);
        var t = Text(letter, Math.Max(1, size * 7 / 16), letterColor, true);
        dc.DrawText(t, new Point(c.X - t.Width / 2, c.Y - t.Height / 2));
    }

    private static void Arc(DrawingContext dc, Point c, double radius, double thickness, double fraction, Color color)
    {
        Point At(double angle) => new(c.X + radius * Math.Sin(angle), c.Y - radius * Math.Cos(angle));
        double sweep = DialSweep * fraction;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            // Avalonia：BeginFigure(点, 是否填充) + EndFigure(是否闭合)，ArcTo 无 stroke 参数
            ctx.BeginFigure(At(DialStart), false);
            ctx.ArcTo(At(DialStart + sweep), new Size(radius, radius), 0,
                sweep > Math.PI, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, PenOf(color, thickness), geo);
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
        dc.DrawGeometry(Faint(color, 0.16), null, under);
        dc.DrawGeometry(null, PenOf(color, 1.2, 0.9), line);
    }

    private static void Curve(StreamGeometryContext ctx, Point[] p, bool closed, double bottom)
    {
        ctx.BeginFigure(closed ? new Point(p[0].X, bottom) : p[0], closed);
        if (closed) ctx.LineTo(p[0]);
        for (int i = 0; i < p.Length - 1; i++)
        {
            Point a = p[Math.Max(0, i - 1)], b = p[i], c = p[i + 1], d = p[Math.Min(p.Length - 1, i + 2)];
            var c1 = new Point(b.X + (c.X - a.X) / 6, b.Y + (c.Y - a.Y) / 6);
            var c2 = new Point(c.X - (d.X - b.X) / 6, c.Y - (d.Y - b.Y) / 6);
            // Avalonia 的 Point 不可变：钳制要重建
            c1 = new Point(c1.X, Math.Min(c1.Y, bottom));
            c2 = new Point(c2.X, Math.Min(c2.Y, bottom));
            ctx.CubicBezierTo(c1, c2, c);
        }
        if (closed) ctx.LineTo(new Point(p[^1].X, bottom));
        ctx.EndFigure(closed);
    }
}
