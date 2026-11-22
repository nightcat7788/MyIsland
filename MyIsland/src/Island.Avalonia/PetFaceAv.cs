using Avalonia;
using Avalonia.Media;
using Island.App.Animation;

namespace Island.Avalonia;

/// <summary>
/// 发光双眼小形象（Avalonia 版，与 Windows 版 PetFace 同源）：
/// 弹簧驱动视线/眼睑/表情；款式 classic/mono/cat/robot；
/// 情境：高温流汗、磁盘打满晕眩、深夜困倦 Zzz；点击"惊讶"。
/// </summary>
public sealed class PetFaceAv
{
    private static readonly string[] Moods = { "happy", "wink", "love", "star", "shy", "pout", "squint" };

    private readonly Spring _ex = new(0, 140, 0.8, 0.01);
    private readonly Spring _ey = new(0, 140, 0.8, 0.01);
    private readonly Spring _lh = new(0, 160, 0.75, 0.003);
    private readonly Spring _rh = new(0, 160, 0.75, 0.003);
    private readonly Spring _tired = new(0, 60, 1.0, 0.003);
    private readonly Spring _wide = new(0, 220, 0.45, 0.003);
    private readonly Spring _morph = new(0, 200, 0.7, 0.003);
    private readonly Spring _blush = new(0, 120, 0.9, 0.003);

    private string _mood = "default";
    private string _lastMood = "";
    private bool _near;
    private double _lastAttention = Now;
    private double _surpriseUntil = 0;
    private double _blinkAt = Now + 2.5;
    private double _t = Now;

    public double Spread { get; set; }
    public string Style { get; set; } = "classic";

    // 情境（宿主每秒注入）
    public double? CpuTemp { get; set; }
    public double DiskActive { get; set; }
    /// <summary>眼睑遮罩色 = 本体表面色（宿主每帧注入，跟随主题）。</summary>
    public Color SurfaceColor { get; set; } = Color.FromRgb(16, 18, 24);

    private static double Now => Environment.TickCount64 / 1000.0;

    public void React(string kind = "surprise")
    {
        _lastAttention = Now;
        if (kind == "surprise") { _surpriseUntil = Now + 1.4; _wide.Kick(9); }
    }

    private void PickMood()
    {
        var choices = Moods.Where(m => m != _lastMood).ToArray();
        _mood = choices[(int)(Random.Shared.NextDouble() * choices.Length)];
        _lastMood = _mood;
    }

    private string CurrentMood(double now, bool near)
    {
        if (now < _surpriseUntil) return "surprise";
        if (near) return _mood;
        if (CpuTemp is >= 85) return "hot";
        if (DiskActive >= 95) return "dizzy";
        if ((DateTime.Now.Hour >= 23 || DateTime.Now.Hour < 6) && now - _lastAttention > 30) return "sleepy";
        return "default";
    }

    private double Step(Point look, bool near)
    {
        double now = Now;
        double dt = Math.Clamp(now - _t, 0.001, 0.05);
        _t = now;
        if (near && !_near) PickMood();
        else if (!near && _near) _mood = "default";
        _near = near;
        if (near) _lastAttention = now;

        string m = CurrentMood(now, near);

        double tx, ty;
        if (!near && Math.Abs(look.X) > 420)
        {
            tx = Math.Sin(now * 0.55) * 0.8 + Math.Sin(now * 1.7) * 0.15;
            ty = Math.Sin(now * 0.37) * 0.35;
        }
        else { tx = Easing.Clamp(look.X / 90, -1, 1); ty = Easing.Clamp(look.Y / 60, -1, 1); }
        if (m == "shy") { tx = tx * 0.3 + 0.5; ty = 0.75; }

        _ex.To(tx); _ey.To(ty);
        _lh.To(m is "happy" or "laugh" or "shy" ? (m == "shy" ? 0.55 : 1) : 0);
        _rh.To(m is "happy" or "laugh" or "shy" or "wink" ? (m == "shy" ? 0.55 : 1) : 0);
        _tired.To(m == "sleepy" ? 0.72 : now - _lastAttention > 60 && m == "default" ? 0.55 : 0);
        _wide.To(m switch { "star" => 0.3, "surprise" => 0.35, _ => 0 });
        _morph.To(m is "love" or "squint" or "pout" ? 1 : 0);
        _blush.To(m switch { "shy" => 1, "love" => 0.8, "happy" => 0.5, "laugh" => 0.6, "wink" => 0.5, _ => 0 });
        foreach (var sp in new[] { _ex, _ey, _lh, _rh, _tired, _wide, _morph, _blush }) sp.Step(dt);

        double dur = 0.16 + 0.18 * _tired.Value;
        if (now >= _blinkAt + dur)
        {
            double gap = 2.4 + (Math.Sin(now * 12.9898) * 0.5 + 0.5) * 3.2;
            _blinkAt = now + gap * (_tired.Value > 0.3 ? 1.6 : 1.0);
        }
        if (m is "default" or "pout" or "shy" or "star" && _blinkAt <= now && now < _blinkAt + dur)
            return Math.Sin((now - _blinkAt) / dur * Math.PI);
        return 0;
    }

    public void Draw(DrawingContext dc, double cx, double cy, double s, double alpha, Point look, bool near)
    {
        double blink = Step(look, near);
        string m = CurrentMood(Now, _near);
        double tired = Easing.Clamp(_tired.Value, 0, 1);
        double wide = Easing.Clamp(_wide.Value, -0.3, 1);
        double morph = Easing.Clamp(_morph.Value, 0, 1);

        double baseW = 11 * s, baseH = 15 * s, gap = 15 * s;
        // Avalonia 的 Color 不可变
        var eyeColor = Color.FromArgb((byte)(255 * alpha), 240, 244, 255);
        var glow = Color.FromArgb((byte)(90 * alpha), 120, 180, 255);
        var pink = Color.FromArgb((byte)(255 * alpha), 255, 92, 138);
        var centers = new List<Point>();

        var sides = Style == "mono" ? new[] { 0 } : new[] { -1, 1 };
        double eyeW = baseW * (Style == "mono" ? 1.5 : 1);
        double eyeH = baseH * (Style == "mono" ? 1.15 : 1);
        double radFactor = Style switch { "robot" => 0.18, "cat" => 0.5, "mono" => 0.46, _ => 0.42 };

        // 机器人天线
        if (Style == "robot")
        {
            var antPen = new Pen(new SolidColorBrush(eyeColor), 1.3 * s) { LineCap = PenLineCap.Round };
            double at = cy - baseH * 0.72;
            dc.DrawLine(antPen, new Point(cx, at), new Point(cx, at - 5 * s));
            dc.DrawEllipse(new SolidColorBrush(glow), null, new Point(cx, at - 6.5 * s), 2.4 * s, 2.4 * s);
            dc.DrawEllipse(new SolidColorBrush(eyeColor), null, new Point(cx, at - 6.5 * s), 1.2 * s, 1.2 * s);
        }

        foreach (int side in sides)
        {
            double rx = cx + side * (gap / 2 + baseW / 2 + Spread * s / 2) + _ex.Value * 5 * s;
            double ry = cy + _ey.Value * 3 * s;
            centers.Add(new Point(rx, ry));
            double curious = 1 + 0.22 * Math.Max(0, _ex.Value * side);
            double w = eyeW * (1 + 0.12 * wide) * (1 + 0.25 * blink);
            double h = eyeH * curious * (1 + 0.18 * wide) * (1 - 0.88 * blink);
            var eyeRect = new Rect(rx - w / 2, ry - h / 2, w, Math.Max(1.6 * s, h));

            // 光晕
            var glowBrush = new RadialGradientBrush
            {
                GradientStops = { new GradientStop(glow, 0), new GradientStop(Colors.Transparent, 1) },
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            };
            dc.DrawEllipse(glowBrush, null,
                new Point(eyeRect.X + w / 2, eyeRect.Y + h / 2), w * 0.95, h * 0.75 + 3 * s);

            // 猫咪耳朵
            if (Style == "cat")
            {
                var ear = new StreamGeometry();
                using (var ctx = ear.Open())
                {
                    ctx.BeginFigure(new Point(rx - w * 0.55, eyeRect.Top + 2 * s), true);
                    ctx.LineTo(new Point(rx - w * 0.08, eyeRect.Top + 1 * s));
                    ctx.LineTo(new Point(rx - w * 0.34, eyeRect.Top - 6 * s));
                    ctx.EndFigure(true);
                }
                dc.DrawGeometry(new SolidColorBrush(eyeColor), null, ear);
            }

            if (m == "love" && morph > 0.05)
            {
                var heart = HeartPath(rx, ry, 15 * s * (1 + 0.08 * Math.Sin(Now * 7)) * (0.6 + 0.4 * morph));
                dc.DrawGeometry(new SolidColorBrush(pink), null, heart);
                continue;
            }
            if (m == "squint" && morph > 0.05)
            {
                int d = -side; double k = 5.5 * s * morph;
                var seg = new StreamGeometry();
                using (var ctx = seg.Open())
                {
                    ctx.BeginFigure(new Point(rx - d * k, ry - k), false);
                    ctx.LineTo(new Point(rx + d * k, ry));
                    ctx.LineTo(new Point(rx - d * k, ry + k));
                    ctx.EndFigure(false);
                }
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(eyeColor), 2.6 * s) { LineCap = PenLineCap.Round }, seg);
                continue;
            }
            // 晕眩：× 形眼
            if (m == "dizzy")
            {
                var xp = new Pen(new SolidColorBrush(eyeColor), 2.2 * s) { LineCap = PenLineCap.Round };
                double ex = w * 0.28, ey = Math.Max(1.6 * s, h) * 0.28;
                dc.DrawLine(xp, new Point(rx - ex, ry - ey), new Point(rx + ex, ry + ey));
                dc.DrawLine(xp, new Point(rx + ex, ry - ey), new Point(rx - ex, ry + ey));
                continue;
            }

            // 眼睛主体（圆角矩形）
            var eye = new StreamGeometry();
            double rad = Math.Min(w, eyeRect.Height) * radFactor;
            using (var ctx = eye.Open())
            {
                ctx.BeginFigure(new Point(eyeRect.X + rad, eyeRect.Y), true);
                ctx.LineTo(new Point(eyeRect.Right - rad, eyeRect.Y));
                ctx.ArcTo(new Point(eyeRect.Right, eyeRect.Y + rad), new Size(rad, rad), 0, false, SweepDirection.Clockwise);
                ctx.LineTo(new Point(eyeRect.Right, eyeRect.Bottom - rad));
                ctx.ArcTo(new Point(eyeRect.Right - rad, eyeRect.Bottom), new Size(rad, rad), 0, false, SweepDirection.Clockwise);
                ctx.LineTo(new Point(eyeRect.X + rad, eyeRect.Bottom));
                ctx.ArcTo(new Point(eyeRect.X, eyeRect.Bottom - rad), new Size(rad, rad), 0, false, SweepDirection.Clockwise);
                ctx.LineTo(new Point(eyeRect.X, eyeRect.Y + rad));
                ctx.ArcTo(new Point(eyeRect.X + rad, eyeRect.Y), new Size(rad, rad), 0, false, SweepDirection.Clockwise);
                ctx.EndFigure(true);
            }
            dc.DrawGeometry(new SolidColorBrush(eyeColor), null, eye);

            // 困倦眼睑（上沿压下半圆，颜色 = 本体表面色）
            if (tired > 0.01)
            {
                double lidH = eyeRect.Height * 0.5 * tired;
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(235 * alpha), SurfaceColor.R, SurfaceColor.G, SurfaceColor.B)), null,
                    new RoundedRect(new Rect(eyeRect.X - 1, eyeRect.Y - 1, w + 2, lidH + 1), 2));
            }
            if (m == "star" && eyeRect.Height > 6 * s)
            {
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(220 * alpha), 20, 20, 26)), null,
                    SparklePath(rx + 1.4 * s, ry - 2.2 * s, 3.4 * s));
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(200 * alpha), 20, 20, 26)), null,
                    SparklePath(rx - 2.2 * s, ry + 3.0 * s, 1.6 * s));
            }
        }

        // 高温汗滴
        if (m == "hot" && centers.Count > 0)
        {
            double fall = (Now % 1.6) / 1.6;
            var rp = centers[^1];
            double dropX = rp.X + 8 * s, dropY = rp.Y - 12 * s + fall * 9 * s;
            var dropColor = Color.FromArgb((byte)(220 * alpha * (1 - fall * 0.4)), 130, 205, 255);
            dc.DrawEllipse(new SolidColorBrush(dropColor), null, new Point(dropX, dropY), 2.2 * s, 2.4 * s);
            var tip = new StreamGeometry();
            using (var ctx = tip.Open())
            {
                ctx.BeginFigure(new Point(dropX, dropY - 5 * s), true);
                ctx.LineTo(new Point(dropX - 1.9 * s, dropY - 0.8 * s));
                ctx.LineTo(new Point(dropX + 1.9 * s, dropY - 0.8 * s));
                ctx.EndFigure(true);
            }
            dc.DrawGeometry(new SolidColorBrush(dropColor), null, tip);
        }
        // 困倦 Zzz
        if (m == "sleepy" && centers.Count > 0)
        {
            for (int i = 0; i < 3; i++)
            {
                double zt = ((Now / 2.4) + i * 0.33) % 1;
                double zx = centers[^1].X + 12 * s + i * 5.5 * s + Math.Sin(Now * 2 + i) * 1.2 * s;
                double zy = centers[^1].Y - 12 * s - zt * 12 * s;
                var zt2 = new FormattedText("z", System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI, PingFang SC", FontStyle.Normal, FontWeight.Bold),
                    (6 + 4 * zt) * s,
                    new SolidColorBrush(Color.FromArgb((byte)(200 * alpha * (1 - zt * 0.5)), eyeColor.R, eyeColor.G, eyeColor.B)));
                dc.DrawText(zt2, new Point(zx, zy));
            }
        }

        // 腮红
        double bl = Easing.Clamp(_blush.Value, 0, 1);
        if (bl > 0.02)
            foreach (var (p, side) in centers.Zip(new[] { -1, 1 }))
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(150 * bl * alpha), 255, 124, 162)), null,
                    new Point(p.X + side * 7.5 * s, p.Y + baseH * 0.55), 4.4 * s, 2.4 * s);

        // 嘴
        double mx = cx + _ex.Value * 5 * s, my = cy + baseH * 0.66 + _ey.Value * 3 * s;
        var mouth = new StreamGeometry();
        using (var ctx = mouth.Open())
        {
            double mw = 7 * s;
            ctx.BeginFigure(new Point(mx - mw / 2, my), false);
            ctx.QuadraticBezierTo(new Point(mx, my + 4.2 * s), new Point(mx + mw / 2, my));
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(eyeColor), 1.8 * s) { LineCap = PenLineCap.Round }, mouth);

        // 猫咪胡须
        if (Style == "cat")
        {
            var wp = new Pen(new SolidColorBrush(
                Color.FromArgb((byte)(170 * alpha), eyeColor.R, eyeColor.G, eyeColor.B)), 1.1 * s)
            { LineCap = PenLineCap.Round };
            foreach (int side in new[] { -1, 1 })
            {
                double wx = cx + side * (gap / 2 + baseW + 3 * s);
                dc.DrawLine(wp, new Point(wx, my - 2.5 * s), new Point(wx + side * 6 * s, my - 4 * s));
                dc.DrawLine(wp, new Point(wx, my + 0.5 * s), new Point(wx + side * 6 * s, my + 1.5 * s));
            }
        }
    }

    private static StreamGeometry HeartPath(double cx, double cy, double size)
    {
        var g = new StreamGeometry();
        double w = size;
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(cx, cy + w * 0.42), true);
            c.CubicBezierTo(new Point(cx - w * 0.9, cy - w * 0.05), new Point(cx - w * 0.45, cy - w * 0.75), new Point(cx, cy - w * 0.28));
            c.CubicBezierTo(new Point(cx + w * 0.45, cy - w * 0.75), new Point(cx + w * 0.9, cy - w * 0.05), new Point(cx, cy + w * 0.42));
            c.EndFigure(true);
        }
        return g;
    }

    private static StreamGeometry SparklePath(double cx, double cy, double r)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(cx, cy - r), true);
            c.QuadraticBezierTo(new Point(cx, cy), new Point(cx + r, cy));
            c.QuadraticBezierTo(new Point(cx, cy), new Point(cx, cy + r));
            c.QuadraticBezierTo(new Point(cx, cy), new Point(cx - r, cy));
            c.QuadraticBezierTo(new Point(cx, cy), new Point(cx, cy - r));
            c.EndFigure(true);
        }
        return g;
    }
}
