using System.Windows;
using System.Windows.Media;

namespace Island.App.Animation;

/// <summary>
/// 原创发光双眼小形象。用弹簧驱动视线、眼睑、表情形变。
/// 移植自 wisland/ui/compact.py 的 PetFace，改用 WPF DrawingContext 绘制。
/// 表情：default/happy/wink/love/star/shy/pout/squint/surprise。
/// </summary>
public sealed class PetFace
{
    private static readonly string[] Moods = { "happy", "wink", "love", "star", "shy", "pout", "squint" };

    private readonly Spring _ex = new(0, 140, 0.8, 0.01);   // 视线 x
    private readonly Spring _ey = new(0, 140, 0.8, 0.01);   // 视线 y
    private readonly Spring _lh = new(0, 160, 0.75, 0.003); // 左眼开心月牙
    private readonly Spring _rh = new(0, 160, 0.75, 0.003); // 右眼
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

    public double Spread { get; set; } // 被按压时两眼额外分开
    public double Tilt { get; set; }   // 被按压时每只眼倾斜角

    /// <summary>款式：classic 经典双眼 / mono 独眼 / cat 猫咪（耳朵+胡须）/ robot 机器人（方眼+天线）。</summary>
    public string Style { get; set; } = "classic";

    // ---------- 情境感知（拓麻歌子式：对系统状态有反应；宿主每秒注入，纯读取零采集） ----------
    /// <summary>CPU 温度 ℃（≥85 流汗）。</summary>
    public double? CpuTemp { get; set; }
    /// <summary>磁盘活动时间 %（≥95 晕眩）。</summary>
    public double DiskActive { get; set; }
    /// <summary>眼睑遮罩色 = 本体表面色（宿主每帧注入，跟随主题）。</summary>
    public Color SurfaceColor { get; set; } = Color.FromRgb(16, 18, 24);

    /// <summary>当前心情：惊讶 > 互动 > 情境（高温/晕眩/深夜困倦）> 默认。</summary>
    private string CurrentMood(double now, bool near)
    {
        if (now < _surpriseUntil) return "surprise";
        if (near) return _mood;
        if (CpuTemp is >= 85) return "hot";
        if (DiskActive >= 95) return "dizzy";
        if ((DateTime.Now.Hour >= 23 || DateTime.Now.Hour < 6) && now - _lastAttention > 30) return "sleepy";
        return "default";
    }

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
        string m = nowSurprise();
        double tired = Easing.Clamp(_tired.Value, 0, 1);
        double wide = Easing.Clamp(_wide.Value, -0.3, 1);
        double morph = Easing.Clamp(_morph.Value, 0, 1);
        double bounce = m == "laugh" ? Math.Sin(Now * 16) * 1.6 * s : 0;

        double baseW = 11 * s, baseH = 15 * s, gap = 15 * s;
        var eyeColor = new Color { A = (byte)(255 * alpha), R = 240, G = 244, B = 255 }; // 亮白
        var glow = new Color { A = (byte)(90 * alpha), R = 120, G = 180, B = 255 };
        var pink = Color.FromArgb((byte)(255 * alpha), 255, 92, 138);
        var centers = new List<Point>();

        // 款式参数：mono 单只大眼；robot 方眼角；cat 圆眼角 + 耳朵胡须；pixel 纯方块无光晕
        var sides = Style == "mono" ? new[] { 0 } : new[] { -1, 1 };
        double eyeW = baseW * (Style == "mono" ? 1.5 : 1);
        double eyeH = baseH * (Style == "mono" ? 1.15 : 1);
        double radFactor = Style switch { "robot" => 0.18, "cat" => 0.5, "mono" => 0.46, "pixel" => 0.04, _ => 0.42 };

        // 机器人天线（先画，压在眼睛后面）
        if (Style == "robot")
        {
            var antPen = new Pen(new SolidColorBrush(eyeColor), 1.3 * s)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            double at = cy - baseH * 0.72;
            dc.DrawLine(antPen, new Point(cx, at), new Point(cx, at - 5 * s));
            dc.DrawEllipse(new SolidColorBrush(glow), null, new Point(cx, at - 6.5 * s), 2.4 * s, 2.4 * s);
            dc.DrawEllipse(new SolidColorBrush(eyeColor), null, new Point(cx, at - 6.5 * s), 1.2 * s, 1.2 * s);
        }

        foreach (int side in sides)
        {
            double rx = cx + side * (gap / 2 + baseW / 2 + Spread * s / 2) + _ex.Value * 5 * s;
            double ry = cy + _ey.Value * 3 * s + bounce;
            centers.Add(new Point(rx, ry));
            double curious = 1 + 0.22 * Math.Max(0, _ex.Value * side);
            double w = eyeW * (1 + 0.12 * wide) * (1 + 0.25 * blink);
            double h = eyeH * curious * (1 + 0.18 * wide) * (1 - 0.88 * blink);
            var eyeRect = new Rect(rx - w / 2, ry - h / 2, w, Math.Max(1.6 * s, h));

            // 光晕（像素款不要光晕——点阵屏没有渐变）
            if (Style != "pixel")
            {
                var glowBrush = new RadialGradientBrush(glow, Colors.Transparent)
                {
                    Center = new Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5,
                };
                dc.DrawEllipse(glowBrush, null,
                    new Point(eyeRect.X + w / 2, eyeRect.Y + h / 2), w * 0.95, h * 0.75 + 3 * s);
            }

            // 猫咪耳朵（先于眼睛主体，与心情无关始终在场）
            if (Style == "cat")
            {
                var ear = new StreamGeometry();
                using (var ctx = ear.Open())
                {
                    ctx.BeginFigure(new Point(rx - w * 0.55, eyeRect.Top + 2 * s), true, true);
                    ctx.LineTo(new Point(rx - w * 0.08, eyeRect.Top + 1 * s), true, false);
                    ctx.LineTo(new Point(rx - w * 0.34, eyeRect.Top - 6 * s), true, false);
                }
                ear.Freeze();
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
                    ctx.BeginFigure(new Point(rx - d * k, ry - k), false, false);
                    ctx.LineTo(new Point(rx + d * k, ry), true, false);
                    ctx.LineTo(new Point(rx - d * k, ry + k), true, false);
                }
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(eyeColor), 2.6 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, seg);
                continue;
            }
            // 晕眩（磁盘打满）：× 形眼
            if (m == "dizzy")
            {
                var xp = new Pen(new SolidColorBrush(eyeColor), 2.2 * s)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                double ex = w * 0.28, ey = Math.Max(1.6 * s, h) * 0.28;
                dc.DrawLine(xp, new Point(rx - ex, ry - ey), new Point(rx + ex, ry + ey));
                dc.DrawLine(xp, new Point(rx + ex, ry - ey), new Point(rx - ex, ry + ey));
                continue;
            }

            // 眼睛主体（圆角矩形，圆角随款式）
            var eye = new StreamGeometry();
            double rad = Math.Min(w, eyeRect.Height) * radFactor;
            using (var ctx = eye.Open())
            {
                ctx.BeginFigure(new Point(eyeRect.X + rad, eyeRect.Y), true, true);
                // 用 RoundedRect 生成后合并太复杂，这里直接画圆角矩形近似
                ctx.LineTo(new Point(eyeRect.Right - rad, eyeRect.Y), true, false);
                ctx.ArcTo(new Point(eyeRect.Right, eyeRect.Y + rad), new Size(rad, rad), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(eyeRect.Right, eyeRect.Bottom - rad), true, false);
                ctx.ArcTo(new Point(eyeRect.Right - rad, eyeRect.Bottom), new Size(rad, rad), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(eyeRect.X + rad, eyeRect.Bottom), true, false);
                ctx.ArcTo(new Point(eyeRect.X, eyeRect.Bottom - rad), new Size(rad, rad), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(eyeRect.X, eyeRect.Y + rad), true, false);
                ctx.ArcTo(new Point(eyeRect.X + rad, eyeRect.Y), new Size(rad, rad), 0, false, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(new SolidColorBrush(eyeColor), null, eye);

            // 眼睑（困倦/开心月牙）：上沿压一块本体色遮罩——此前这块只构建了几何但从没画出来
            double happy = side < 0 ? _lh.Value : _rh.Value;
            if (tired > 0.01 || happy > 0.01)
            {
                double lidH = eyeRect.Height * (0.5 * tired + 0.35 * happy);
                if (lidH > 0.5)
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, SurfaceColor.R, SurfaceColor.G, SurfaceColor.B)), null,
                        new Rect(eyeRect.X - 1, eyeRect.Y - 1, w + 2, lidH + 1), 2, 2);
            }
            if (m == "star" && eyeRect.Height > 6 * s)
            {
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(220 * alpha), 20, 20, 26)), null,
                    SparklePath(rx + 1.4 * s, ry - 2.2 * s, 3.4 * s));
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(200 * alpha), 20, 20, 26)), null,
                    SparklePath(rx - 2.2 * s, ry + 3.0 * s, 1.6 * s));
            }
        }

        // 情境附加：高温汗滴（右眼旁，循环下落）/ 深夜困倦 Zzz（右上飘升）
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
                ctx.BeginFigure(new Point(dropX, dropY - 5 * s), true, true);
                ctx.LineTo(new Point(dropX - 1.9 * s, dropY - 0.8 * s), true, false);
                ctx.LineTo(new Point(dropX + 1.9 * s, dropY - 0.8 * s), true, false);
            }
            tip.Freeze();
            dc.DrawGeometry(new SolidColorBrush(dropColor), null, tip);
        }
        if (m == "sleepy" && centers.Count > 0)
        {
            for (int i = 0; i < 3; i++)
            {
                double zt = ((Now / 2.4) + i * 0.33) % 1;
                double zx = centers[^1].X + 12 * s + i * 5.5 * s + Math.Sin(Now * 2 + i) * 1.2 * s;
                double zy = centers[^1].Y - 12 * s - zt * 12 * s;
                var zt2 = new FormattedText("z", System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    (6 + 4 * zt) * s,
                    new SolidColorBrush(Color.FromArgb((byte)(200 * alpha * (1 - zt * 0.5)), eyeColor.R, eyeColor.G, eyeColor.B)), 1);
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
        double mx = cx + _ex.Value * 5 * s, my = cy + baseH * 0.66 + _ey.Value * 3 * s + bounce;
        var mouth = new StreamGeometry();
        using (var ctx = mouth.Open())
        {
            double mw = 7 * s;
            ctx.BeginFigure(new Point(mx - mw / 2, my), false, false);
            ctx.QuadraticBezierTo(new Point(mx, my + (m == "laugh" ? 8.5 : 4.2) * s),
                                  new Point(mx + mw / 2, my), true, false);
        }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(eyeColor), 1.8 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, mouth);

        // 猫咪胡须（嘴角两侧各两根）
        if (Style == "cat")
        {
            var wp = new Pen(new SolidColorBrush(
                Color.FromArgb((byte)(170 * alpha), eyeColor.R, eyeColor.G, eyeColor.B)), 1.1 * s)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            foreach (int side in new[] { -1, 1 })
            {
                double wx = cx + side * (gap / 2 + baseW + 3 * s);
                dc.DrawLine(wp, new Point(wx, my - 2.5 * s), new Point(wx + side * 6 * s, my - 4 * s));
                dc.DrawLine(wp, new Point(wx, my + 0.5 * s), new Point(wx + side * 6 * s, my + 1.5 * s));
            }
        }
    }

    private string nowSurprise() => CurrentMood(Now, _near);

    private static StreamGeometry HeartPath(double cx, double cy, double size)
    {
        var g = new StreamGeometry();
        double w = size;
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(cx, cy + w * 0.42), true, true);
            c.BezierTo(new Point(cx - w * 0.9, cy - w * 0.05), new Point(cx - w * 0.45, cy - w * 0.75), new Point(cx, cy - w * 0.28), true, false);
            c.BezierTo(new Point(cx + w * 0.45, cy - w * 0.75), new Point(cx + w * 0.9, cy - w * 0.05), new Point(cx, cy + w * 0.42), true, false);
        }
        return g;
    }

    private static StreamGeometry SparklePath(double cx, double cy, double r)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(cx, cy - r), true, true);
            c.QuadraticBezierTo(new Point(cx, cy), new Point(cx + r, cy), true, false);
            c.QuadraticBezierTo(new Point(cx, cy), new Point(cx, cy + r), true, false);
            c.QuadraticBezierTo(new Point(cx, cy), new Point(cx - r, cy), true, false);
            c.QuadraticBezierTo(new Point(cx, cy), new Point(cx, cy - r), true, false);
        }
        return g;
    }
}
