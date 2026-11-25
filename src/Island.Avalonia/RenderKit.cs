using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
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

    // ---- 资源缓存：全部用 Immutable 类型 ----
    // Avalonia 的 RenderDataDrawingContext.AddResource 对 IImmutableBrush/ImmutablePen 直接 return，
    // 对可变对象走 AddRefOnCompositor（每帧每个绘制节点都做引用计数+资源登记）。改不可变后这段开销归零。
    // 缓存不再"满了整表 Clear"（那会造成偶发集中重建卡一下）：读数文本的键空间有限（值 0~100 × 几种字号），
    // 让它自然涨到几千条也才几 MB；真要上限就 LRU 式淘汰最旧的一批。
    private static readonly Dictionary<uint, ImmutableSolidColorBrush> _brushes = new();
    private static readonly Dictionary<(uint Color, int OpacityPct), ImmutableSolidColorBrush> _faints = new();
    private static readonly Dictionary<(uint Color, int OpacityPct, double Thickness), ImmutablePen> _pens = new();
    private static readonly Dictionary<(string Text, double Size, bool Strong, uint Color), FormattedText> _texts = new();
    private static readonly Dictionary<(string Text, double Size, bool Strong, uint Color, int Width), FormattedText> _trimmed = new();
    // 等宽字体单独一套缓存：键里带 Mono 标志，避免与比例字体的条目串味。
    private static readonly Dictionary<(string Text, double Size, bool Strong, uint Color), FormattedText> _monoTexts = new();
    private static readonly Dictionary<(string Text, double Size, bool Strong, uint Color, int Width), FormattedText> _monoTrimmed = new();
    private const int CacheSoftLimit = 4096;

    private static uint Key(Color c) => (uint)((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);

    private static void Trim<TKey, TValue>(Dictionary<TKey, TValue> d) where TKey : notnull where TValue : class
    {
        if (d.Count < CacheSoftLimit) return;
        // 淘汰插入顺序最旧的 1/4（Dictionary 在无删除时保持插入序，这里是近似 LRU）
        int n = d.Count / 4; var keys = new List<TKey>(n);
        foreach (var k in d.Keys) { keys.Add(k); if (keys.Count >= n) break; }
        foreach (var k in keys) d.Remove(k);
    }

    public static ImmutableSolidColorBrush BrushOf(Color c)
    {
        uint k = Key(c);
        if (_brushes.TryGetValue(k, out var b)) return b;
        Trim(_brushes);
        return _brushes[k] = new ImmutableSolidColorBrush(c, 1.0, null);
    }

    public static ImmutableSolidColorBrush Faint(Color c, double opacity)
    {
        var k = (Key(c), (int)Math.Round(opacity * 100));
        if (_faints.TryGetValue(k, out var b)) return b;
        Trim(_faints);
        return _faints[k] = new ImmutableSolidColorBrush(c, opacity, null);
    }

    public static ImmutablePen PenOf(Color c, double thickness, double opacity = 1)
    {
        var k = (Key(c), (int)Math.Round(opacity * 100), thickness);
        if (_pens.TryGetValue(k, out var p)) return p;
        Trim(_pens);
        // ImmutablePen：LineCap 固定 Round（全项目统一）；签名 (brush, thickness, dashStyle, lineCap, lineJoin, miterLimit)
        p = new ImmutablePen(Faint(c, opacity), thickness, null, PenLineCap.Round, PenLineJoin.Round);
        return _pens[k] = p;
    }

    private static readonly Typeface _regular = new("Segoe UI, PingFang SC, Helvetica Neue, sans-serif");
    private static readonly Typeface _strong = new("Segoe UI, PingFang SC, Helvetica Neue, sans-serif",
        FontStyle.Normal, FontWeight.SemiBold);
    // 等宽字体：代码条目用（BrimDeck 剪贴板同款）。macOS 首选 Menlo，
    // 第二个 SF Mono，Windows 侧回落 Consolas —— 三平台都能拿到等宽字形。
    private static readonly Typeface _mono = new("Menlo, SF Mono, Consolas, Monaco, monospace");
    private static readonly Typeface _monoStrong = new("Menlo, SF Mono, Consolas, Monaco, monospace",
        FontStyle.Normal, FontWeight.SemiBold);

    public static FormattedText Text(string s, double size, Color color, bool strong = false)
    {
        var k = (s, size, strong, Key(color));
        if (_texts.TryGetValue(k, out var t)) return t;
        Trim(_texts);
        t = new FormattedText(s,
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            strong ? _strong : _regular, size, BrushOf(color));
        return _texts[k] = t;
    }

    /// <summary>裁剪文本也走缓存：键带宽度。剪贴板页/屏幕页一帧 40+ 段裁剪文本，之前每帧 new。</summary>
    public static FormattedText Trimmed(string s, double size, Color color, double width, bool strong)
    {
        var k = (s, size, strong, Key(color), (int)Math.Round(width));
        if (_trimmed.TryGetValue(k, out var t)) return t;
        Trim(_trimmed);
        t = new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            strong ? _strong : _regular, size, BrushOf(color))
        { MaxTextWidth = Math.Max(1, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        return _trimmed[k] = t;
    }

    /// <summary>
    /// 等宽字体文本（代码条目用）。与 <see cref="Text"/> 分开缓存 —— 键里没有 typeface 维度，
    /// 混用会导致比例字体的条目被当成等宽取用。
    /// </summary>
    public static FormattedText Mono(string s, double size, Color color, bool strong = false)
    {
        var k = (s, size, strong, Key(color));
        if (_monoTexts.TryGetValue(k, out var t)) return t;
        Trim(_monoTexts);
        t = new FormattedText(s,
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            strong ? _monoStrong : _mono, size, BrushOf(color));
        return _monoTexts[k] = t;
    }

    /// <summary>等宽 + 单行裁剪（代码条目在窄带里的显示）。</summary>
    public static FormattedText MonoTrimmed(string s, double size, Color color, double width, bool strong)
    {
        var k = (s, size, strong, Key(color), (int)Math.Round(width));
        if (_monoTrimmed.TryGetValue(k, out var t)) return t;
        Trim(_monoTrimmed);
        t = new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            strong ? _monoStrong : _mono, size, BrushOf(color))
        { MaxTextWidth = Math.Max(1, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        return _monoTrimmed[k] = t;
    }

    // ---- 位图缓存：应用图标（PNG 字节 → Avalonia Bitmap）----
    private static readonly Dictionary<string, Bitmap> _bitmaps = new();
    private static readonly object _bitmapGate = new();

    /// <summary>
    /// 把 PNG 字节解码成可绘制的 Bitmap（带缓存）。同一份字节只解码一次。
    /// 解码失败返回 null（调用方显示首字母兜底）。
    /// </summary>
    public static Bitmap? Bitmap(byte[]? png)
    {
        if (png is null || png.Length == 0) return null;
        string key = Convert.ToHexString(png.AsSpan(0, Math.Min(64, png.Length))) + png.Length;
        lock (_bitmapGate)
        {
            if (_bitmaps.TryGetValue(key, out var hit)) return hit;
            try
            {
                using var ms = new MemoryStream(png);
                var bmp = new Bitmap(ms);
                if (_bitmaps.Count > 256) _bitmaps.Clear();     // 粗略封顶，避免无限增长
                _bitmaps[key] = bmp;
                return bmp;
            }
            catch { return null; }
        }
    }

    /// <summary>在矩形内等比缩放绘制位图（居中）。</summary>
    public static void DrawBitmap(DrawingContext dc, Bitmap bmp, Rect box)
    {
        if (bmp.PixelSize.Width <= 0 || bmp.PixelSize.Height <= 0) return;
        double sx = box.Width / bmp.PixelSize.Width, sy = box.Height / bmp.PixelSize.Height;
        double s = Math.Min(sx, sy);
        double w = bmp.PixelSize.Width * s, h = bmp.PixelSize.Height * s;
        // ⚠ 矩阵顺序：Avalonia 行向量约定下 A*B = 先 A 后 B。
        //   写成 Translation * Scale 会把平移量也缩放（图标被拉到左上方，几乎看不见）——
        //   和 DrawIcon 之前那个坑同源。正确：Scale * Translation = 先缩放再平移到位。
        using (dc.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2)))
            dc.DrawImage(bmp, new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height));
    }

    public static void Baseline(DrawingContext dc, FormattedText t, double x, double baseline)
        => dc.DrawText(t, new Point(x, baseline - t.Baseline));

    // ---------- 表盘（270° 开口环 + 字母） ----------
    private const double DialStart = Math.PI * 1.25;
    private const double DialSweep = Math.PI * 1.5;

    /// <summary>
    /// 360° 圆环（对齐 BrimDeck 收起态的"环包图标"）：底环 + 按占比的彩色弧，开口从正上方顺时针。
    /// 和 <see cref="Dial"/>（270° 表盘 + 字母）不同，这个环里放图标、环外放数字。
    /// </summary>
    public static void Ring(DrawingContext dc, Point center, double diameter, double thickness,
        double share, Color track, Color arc)
    {
        double radius = diameter / 2 - thickness / 2;
        // 底环：整圈
        dc.DrawEllipse(null, PenOf(track, thickness, 0.9), center, radius, radius);
        double f = double.IsNaN(share) ? 0 : Math.Clamp(share, 0, 1);
        if (f > 0.004)
        {
            // 弧：从顶部（-90°）顺时针。用 StreamGeometry + ArcTo（>180° 要拆成两段）
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                Point At(double a) => new(center.X + radius * Math.Sin(a), center.Y - radius * Math.Cos(a));
                double sweep = f * 2 * Math.PI;
                if (sweep <= Math.PI)
                {
                    ctx.BeginFigure(At(0), false);
                    ctx.ArcTo(At(sweep), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
                }
                else
                {
                    // 两段：0 → π → sweep
                    ctx.BeginFigure(At(0), false);
                    ctx.ArcTo(At(Math.PI), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
                    ctx.ArcTo(At(sweep), new Size(radius, radius), 0, sweep - Math.PI > Math.PI, SweepDirection.Clockwise);
                }
                ctx.EndFigure(false);
            }
            dc.DrawGeometry(null, PenOf(arc, thickness), geo);
        }
    }

    /// <summary>
    /// 历史曲线：把 <paramref name="values"/>（已归一 0..1）画成一条折线，下方到基线之间填充半透明的同色。
    /// 对齐 BrimDeck 监控页每张读数卡旁边的曲线。
    /// </summary>
    public static void Sparkline(DrawingContext dc, Rect box, ReadOnlySpan<double> values, int count, Color color)
    {
        if (count < 2 || box.Width < 4 || box.Height < 2) return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double dx = box.Width / (count - 1);
            // 填充区域：折线 + 沿底边回到起点
            ctx.BeginFigure(new Point(box.X, box.Bottom - values[0] * box.Height), true);
            for (int i = 1; i < count; i++)
                ctx.LineTo(new Point(box.X + i * dx, box.Bottom - Math.Clamp(values[i], 0, 1) * box.Height));
            ctx.LineTo(new Point(box.Right, box.Bottom));
            ctx.LineTo(new Point(box.X, box.Bottom));
            ctx.EndFigure(true);
        }
        dc.DrawGeometry(Faint(color, 0.16), null, geo);

        // 折线（单独画，线宽固定）
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            double dx = box.Width / (count - 1);
            ctx.BeginFigure(new Point(box.X, box.Bottom - values[0] * box.Height), false);
            for (int i = 1; i < count; i++)
                ctx.LineTo(new Point(box.X + i * dx, box.Bottom - Math.Clamp(values[i], 0, 1) * box.Height));
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, PenOf(color, 1.4), line);
    }

    /// <summary>
    /// 渐变频谱柱（对齐 BrimDeck 收起形态里的柱状图）：一排竖条，高度按 values，
    /// 颜色从左到右由蓝渐变到青。
    /// </summary>
    public static void Spectrum(DrawingContext dc, Rect box, ReadOnlySpan<double> values, int count,
        Color from, Color to)
    {
        if (count < 2 || box.Width < 4 || box.Height < 2) return;
        // 柱子要够宽，低值时也看得见（之前 1.6px 宽 + 最小 2px 高，
        // 在 CPU 长期低位时看起来像一串虚线而不是柱状图）。
        double bw = Math.Max(2.4, Math.Min(4.0, box.Width / count * 0.7));
        double gap = count > 1 ? Math.Max(0, (box.Width - bw * count) / (count - 1)) : 0;
        for (int i = 0; i < count; i++)
        {
            double v = Math.Clamp(values[i], 0, 1);
            double h = Math.Max(3, v * box.Height);   // 最小 3px，保证低值时也看得见
            double x = box.X + i * (bw + gap);
            double t = count > 1 ? (double)i / (count - 1) : 0;
            var c = Color.FromRgb(
                (byte)(from.R + (to.R - from.R) * t),
                (byte)(from.G + (to.G - from.G) * t),
                (byte)(from.B + (to.B - from.B) * t));
            dc.DrawRectangle(BrushOf(c), null, new RoundedRect(new Rect(x, box.Bottom - h, bw, h), 1));
        }
    }

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
        // 最小可见弧：12% 在 270° 表盘上只有 32°，细环上几乎看不见（看着像个点）。
        // 给一个 25° 的下限，低占用也能一眼看出"有值"。
        double sweep = Math.Max(DialSweep * fraction, Math.Min(DialSweep * 0.16, DialSweep * fraction * 3));
        if (fraction > 0.002) sweep = Math.Max(sweep, Math.PI * 0.14);   // ≈25°
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
