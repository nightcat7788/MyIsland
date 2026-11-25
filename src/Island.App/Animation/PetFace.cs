using System.Windows;
using System.Windows.Media;
using Island.App.Controls;

namespace Island.App.Animation;

/// <summary>
/// 原创发光双眼小形象。用弹簧驱动视线、眼睑、挤压回弹、表情形变。
/// 移植自 wisland/ui/compact.py 的 PetFace，改用 WPF DrawingContext 绘制。
/// 表情：default/happy/wink/love/star/shy/pout/squint/laugh/surprise。
/// </summary>
public sealed class PetFace
{
    private static readonly Dictionary<uint, RadialGradientBrush> _glowBrushes = new();
    private static RadialGradientBrush GlowBrush(Color glow)
    {
        uint k = (uint)(glow.A << 24 | glow.R << 16 | glow.G << 8 | glow.B);
        if (_glowBrushes.TryGetValue(k, out var b)) return b;
        b = new RadialGradientBrush(glow, Colors.Transparent)
        {
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        b.Freeze();
        if (_glowBrushes.Count > 64) _glowBrushes.Clear();
        return _glowBrushes[k] = b;
    }

    private static readonly string[] Moods = { "happy", "wink", "love", "star", "shy", "pout", "squint", "laugh" };
    private static readonly int[] TwoSides = { -1, 1 };
    private static readonly int[] OneSide = { 0 };

    private readonly Spring _ex = new(0, 140, 0.8, 0.01);   // 视线 x
    private readonly Spring _ey = new(0, 140, 0.8, 0.01);   // 视线 y
    private readonly Spring _lh = new(0, 160, 0.75, 0.003); // 左眼开心月牙
    private readonly Spring _rh = new(0, 160, 0.75, 0.003); // 右眼
    private readonly Spring _tired = new(0, 60, 1.0, 0.003);
    private readonly Spring _wide = new(0, 220, 0.45, 0.003);
    private readonly Spring _morph = new(0, 200, 0.7, 0.003);
    private readonly Spring _blush = new(0, 120, 0.9, 0.003);
    private readonly Spring _squash = new(0, 260, 0.45, 0.003);
    private readonly Spring _bob = new(0, 185, 0.72, 0.004);
    private readonly Spring _sway = new(0, 95, 0.82, 0.004);
    private readonly Spring[] _springs;
    public PetFace() { _springs = new[] { _ex, _ey, _lh, _rh, _tired, _wide, _morph, _blush, _squash, _bob, _sway }; }

    private string _mood = "default";
    private string _lastMood = "";
    private bool _near;
    private double _lastAttention = Now;
    private double _surpriseUntil = 0;
    private string _reactMood = "surprise";
    private double _blinkAt = Now + 2.5;
    private bool _doubleBlink;
    private double _nextIdleGlance = Now + 2.2;
    private double _idleGlanceUntil;
    private double _idleLookX, _idleLookY;
    private double _nextIdleMotion = Now + 3.0;
    private double _nextIdleWink = Now + Random.Shared.NextDouble() * 10 + 5;
    private double _idleWinkUntil;
    private double _t = Now;

    public double Spread { get; set; } // 被按压时两眼额外分开
    public double Tilt { get; set; }   // 被按压时每只眼倾斜角

    /// <summary>款式：classic/mono/cat/robot/pixel/alien/ghost/frog。</summary>
    public string Style { get; set; } = "classic";

    // ---------- 情境感知（拓麻歌子式：对系统状态有反应；宿主每秒注入，纯读取零采集） ----------
    /// <summary>CPU 温度 ℃（≥85 流汗）。</summary>
    public double? CpuTemp { get; set; }
    /// <summary>磁盘活动时间 %（≥95 晕眩）。</summary>
    public double DiskActive { get; set; }
    /// <summary>眼睑遮罩色 = 本体表面色（宿主每帧注入，跟随主题）。</summary>
    public Color SurfaceColor { get; set; } = Color.FromRgb(16, 18, 24);

    /// <summary>当前心情：惊讶/点击反馈 > 互动 > 情境（高温/晕眩/深夜困倦）> 默认。</summary>
    private string CurrentMood(double now, bool near)
    {
        if (now < _surpriseUntil) return _reactMood;
        if (near) return _mood;
        if (CpuTemp is >= 85) return "hot";
        if (DiskActive >= 95) return "dizzy";
        if ((DateTime.Now.Hour >= 23 || DateTime.Now.Hour < 6) && now - _lastAttention > 30) return "sleepy";
        if (now < _idleWinkUntil) return "wink";
        return "default";
    }

    private static double Now => Environment.TickCount64 / 1000.0;

    public void React(string kind = "surprise")
    {
        double now = Now;
        _lastAttention = now;
        if (kind == "surprise")
        {
            _reactMood = now < _surpriseUntil
                ? _reactMood switch { "surprise" => "laugh", "laugh" => "love", "love" => "star", _ => "surprise" }
                : "surprise";
            _surpriseUntil = now + 1.4;
            _wide.Kick(9);
            _squash.Kick(-7.5);
            _bob.Kick(-34);
            _sway.Kick(Random.Shared.NextDouble() * 18 - 9);
        }
    }

    private void PickMood()
    {
        int idx = Random.Shared.Next(Moods.Length - 1);
        string pick = Moods[idx];
        if (pick == _lastMood) pick = Moods[^1];
        _mood = pick;
        _lastMood = pick;
    }

    private double Step(Point look, bool near)
    {
        double now = Now;
        double dt = Math.Clamp(now - _t, 0.001, 0.05);
        _t = now;
        if (near && !_near)
        {
            PickMood();
            _squash.Kick(-4.2);
            _bob.Kick(-20);
            _sway.Kick(Math.Clamp(look.X / 18, -1, 1) * 10);
        }
        else if (!near && _near) _mood = "default";
        _near = near;
        if (near) _lastAttention = now;

        // 安静时偶尔做一次短促扫视，然后自然回到正前方，避免一直机械地左右摆动。
        if (!near && now >= _nextIdleGlance)
        {
            _idleLookX = Random.Shared.NextDouble() * 1.2 - 0.6;
            _idleLookY = Random.Shared.NextDouble() * 0.56 - 0.28;
            _idleGlanceUntil = now + 0.22 + Random.Shared.NextDouble() * 0.28;
            _nextIdleGlance = now + 3.0 + Random.Shared.NextDouble() * 4.0;
        }
        if (!near && now >= _nextIdleWink)
        {
            _idleWinkUntil = now + 0.42;
            _nextIdleWink = now + 11.0 + Random.Shared.NextDouble() * 15.0;
        }
        if (!near && now >= _nextIdleMotion)
        {
            _bob.Kick(-8 - Random.Shared.NextDouble() * 7);
            _sway.Kick(Random.Shared.NextDouble() * 22 - 11);
            _nextIdleMotion = now + 5.0 + Random.Shared.NextDouble() * 5.0;
        }

        string m = CurrentMood(now, near);

        double tx, ty;
        if (!near && Math.Abs(look.X) > 420)
        {
            bool glancing = now < _idleGlanceUntil;
            tx = (glancing ? _idleLookX : 0) + Math.Sin(now * 0.43) * 0.025;
            ty = (glancing ? _idleLookY : 0) + Math.Sin(now * 0.31) * 0.016;
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
        _squash.To(0);
        foreach (var sp in _springs) sp.Step(dt);

        double dur = 0.16 + 0.18 * _tired.Value;
        if (now >= _blinkAt + dur)
        {
            if (!_doubleBlink && (m is "default" or "pout" or "shy" or "star") && Random.Shared.NextDouble() < 0.22)
            {
                _doubleBlink = true;
                _blinkAt = now + 0.14;
            }
            else
            {
                _doubleBlink = false;
                double gap = 2.4 + (Math.Sin(now * 12.9898) * 0.5 + 0.5) * 3.2;
                _blinkAt = now + gap * (_tired.Value > 0.3 ? 1.6 : 1.0);
            }
        }
        if (m is "default" or "pout" or "shy" or "star" && _blinkAt <= now && now < _blinkAt + dur)
            return Math.Sin((now - _blinkAt) / dur * Math.PI);
        return 0;
    }

    public void Draw(DrawingContext dc, double cx, double cy, double s, double alpha, Point look, bool near)
    {
        double blink = Step(look, near);
        double now = Now;
        string m = CurrentMood(now, _near);
        double tired = Easing.Clamp(_tired.Value, 0, 1);
        double wide = Easing.Clamp(_wide.Value, -0.3, 1);
        double morph = Easing.Clamp(_morph.Value, 0, 1);
        double sq = Easing.Clamp(_squash.Value, -0.45, 0.55);
        double scaleX = 1.0 - sq * 0.14;
        double scaleY = 1.0 + sq * 0.16;

        double breathRate = m == "sleepy" ? 1.1 : 2.2;
        double breath = Math.Sin(now * breathRate);
        double breathY = breath * (m == "sleepy" ? 0.9 : 0.55) * s;
        double bounce = _bob.Value * s + (m == "laugh" ? Math.Sin(now * 12) * 0.8 * s : 0);
        double jitterX = m == "hot" ? Math.Sin(now * 28) * 0.35 * s : 0;
        double faceX = cx + jitterX + _sway.Value * s;
        double faceY = cy + breathY + bounce;
        double headParallax = _ex.Value * 2.2 * s;

        double baseW = 11 * s * scaleX * (1 + breath * 0.006);
        double baseH = 15 * s * scaleY * (1 + breath * 0.012);
        double gap = 15 * s * scaleX;
        var eyeColor = Color.FromArgb((byte)(255 * alpha), 240, 244, 255);
        var glow = Color.FromArgb((byte)(90 * alpha), 120, 180, 255);
        var pink = Color.FromArgb((byte)(255 * alpha), 255, 92, 138);
        Point leftC = default, rightC = default;
        int cCount = 0;

        var sides = Style == "mono" ? OneSide : TwoSides;
        double eyeW = baseW * (Style == "mono" ? 1.5 : 1);
        double eyeH = baseH * (Style == "mono" ? 1.15 : 1);
        double radFactor = Style switch
        {
            "robot" => 0.18,
            "cat" => 0.5,
            "mono" => 0.46,
            "pixel" => 0,
            "alien" => 0.5,
            "frog" => 0.5,
            _ => 0.42,
        };

        if (Style == "ghost")
        {
            DrawGhost(dc, faceX, faceY, baseW, baseH, s, alpha, eyeColor, pink, m, blink, _ex.Value, _ey.Value, now);
            return;
        }

        // 机器人天线（顶灯呼吸闪烁；高温或点击时发散信号波，高温改橙红快闪）
        if (Style == "robot")
        {
            var antPen = RenderKit.PenOf(eyeColor, 1.3 * s);
            double ax = faceX + headParallax;
            double at = faceY - baseH * 0.72;
            double tipY = at - 6.5 * s;
            dc.DrawLine(antPen, new Point(ax, at), new Point(ax, at - 5 * s));
            double pulse = m == "hot" ? 0.5 + 0.5 * Math.Sin(now * 10) : 0.65 + 0.35 * Math.Sin(now * 3.2);
            var tipGlow = m == "hot"
                ? Color.FromArgb((byte)(170 * alpha * pulse), 255, 110, 80)
                : Color.FromArgb((byte)(130 * alpha * pulse), glow.R, glow.G, glow.B);
            var tipCore = m == "hot" ? Color.FromArgb((byte)(255 * alpha), 255, 140, 100) : eyeColor;
            dc.DrawEllipse(RenderKit.BrushOf(tipGlow), null, new Point(ax, tipY), 2.8 * s, 2.8 * s);
            dc.DrawEllipse(RenderKit.BrushOf(tipCore), null, new Point(ax, tipY), 1.25 * s, 1.25 * s);
            if (now < _surpriseUntil || m == "hot")
            {
                double waveT = (now * 2.2) % 1.0;
                double wr = (2.8 + waveT * 5.2) * s;
                var wColor = m == "hot"
                    ? Color.FromArgb((byte)(170 * alpha * (1 - waveT)), 255, 120, 80)
                    : Color.FromArgb((byte)(160 * alpha * (1 - waveT)), 140, 200, 255);
                dc.DrawEllipse(null, RenderKit.PenOf(wColor, 1.1 * s), new Point(ax, tipY), wr, wr * 0.65);
            }
        }

        // 外星人触角：随视线轻微摆动 + 顶端微光
        if (Style == "alien")
        {
            var apen = RenderKit.PenOf(eyeColor, 1.1 * s);
            double sway = (_ex.Value * 0.9 + Math.Sin(now * 2.4) * 0.45) * s;
            double pulse = 0.7 + 0.3 * Math.Sin(now * 3.6);
            foreach (int sd in TwoSides)
            {
                double ax = faceX + headParallax + sd * baseW * 0.42;
                double ay = faceY - baseH * 0.62;
                var tip = new Point(ax + sd * 2.2 * s + sway, ay - 4.4 * s);
                dc.DrawLine(apen, new Point(ax, ay), tip);
                dc.DrawEllipse(RenderKit.BrushOf(Color.FromArgb((byte)(110 * alpha * pulse), 140, 255, 200)),
                    null, new Point(tip.X, tip.Y - 0.8 * s), 2.3 * s, 2.3 * s);
                dc.DrawEllipse(RenderKit.BrushOf(eyeColor), null, new Point(tip.X, tip.Y - 0.8 * s), 1.3 * s, 1.3 * s);
            }
        }

        // 像素风：刘海
        if (Style == "pixel")
        {
            var brow = RenderKit.BrushOf(Color.FromArgb((byte)(230 * alpha), 240, 244, 255));
            for (int i = 0; i < 5; i++)
            {
                double bx = faceX + headParallax - baseW * 0.95 + i * baseW * 0.48;
                double bh = (i == 1 || i == 2 || i == 3) ? 4.2 * s : 2.4 * s;
                dc.DrawRoundedRectangle(brow, null,
                    RenderKit.SafeRect(bx, faceY - baseH * 0.86 - bh, baseW * 0.44, bh), 0.6 * s, 0.6 * s);
            }
        }

        double earTwitch = (Style == "cat" && (near || m is "surprise" or "happy" or "laugh"))
            ? Math.Sin(now * 10) * 0.9 * s * (0.4 + Math.Min(1, Math.Abs(wide)))
            : 0;

        foreach (int side in sides)
        {
            double rx = faceX + side * (gap / 2 + baseW / 2 + Spread * s / 2) + _ex.Value * 5 * s;
            double ry = faceY + _ey.Value * 3 * s;
            if (cCount == 0) leftC = new Point(rx, ry);
            else rightC = new Point(rx, ry);
            cCount++;

            double curious = 1 + 0.20 * Math.Max(0, _ex.Value * side) - 0.07 * Math.Max(0, -_ex.Value * side);
            double w = eyeW * (1 + 0.12 * wide) * (1 + 0.25 * blink);
            double h = eyeH * curious * (1 + 0.18 * wide) * (1 - 0.88 * blink);
            var eyeRect = RenderKit.SafeRect(rx - w / 2, ry - h / 2, w, Math.Max(1.6 * s, h));

            // 光晕（像素款不要光晕——点阵屏没有渐变；缓存画刷避免每帧 GC）
            if (Style != "pixel")
            {
                dc.DrawEllipse(GlowBrush(glow), null,
                    new Point(eyeRect.X + w / 2, eyeRect.Y + h / 2), w * 0.95, h * 0.75 + 3 * s);
            }

            // 猫咪耳朵（悬停/开心/惊讶时微抖，带视差）
            if (Style == "cat")
            {
                double earShift = -_ex.Value * 1.4 * s;
                var ear = new StreamGeometry();
                using (var ctx = ear.Open())
                {
                    ctx.BeginFigure(new Point(rx - w * 0.55 + earShift, eyeRect.Top + 2 * s), true, true);
                    ctx.LineTo(new Point(rx - w * 0.08 + earShift, eyeRect.Top + 1 * s), true, false);
                    ctx.LineTo(new Point(rx - w * 0.34 + earShift + side * earTwitch, eyeRect.Top - 6 * s - Math.Abs(earTwitch) * 0.5), true, false);
                }
                ear.Freeze();
                dc.DrawGeometry(RenderKit.BrushOf(eyeColor), null, ear);
            }

            if (m == "love" && morph > 0.05)
            {
                double beat = Math.Pow(Math.Max(0, Math.Sin(now * 4.5)), 4) * 0.14 + Math.Pow(Math.Max(0, Math.Sin(now * 4.5 - 0.55)), 6) * 0.09;
                var heart = HeartPath(rx, ry, 15 * s * (1 + beat) * (0.6 + 0.4 * morph));
                dc.DrawGeometry(RenderKit.BrushOf(pink), null, heart);
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
                seg.Freeze();
                dc.DrawGeometry(null, RenderKit.PenOf(eyeColor, 2.6 * s), seg);
                continue;
            }
            // 晕眩（磁盘打满）：旋转 × 形眼
            if (m == "dizzy")
            {
                var xp = RenderKit.PenOf(eyeColor, 2.2 * s);
                double ang = now * 5.0 * side;
                double r = Math.Min(w, Math.Max(1.6 * s, h)) * 0.34;
                double c1 = Math.Cos(ang + Math.PI * 0.25) * r, s1 = Math.Sin(ang + Math.PI * 0.25) * r;
                double c2 = Math.Cos(ang + Math.PI * 0.75) * r, s2 = Math.Sin(ang + Math.PI * 0.75) * r;
                dc.DrawLine(xp, new Point(rx - c1, ry - s1), new Point(rx + c1, ry + s1));
                dc.DrawLine(xp, new Point(rx - c2, ry - s2), new Point(rx + c2, ry + s2));
                continue;
            }

            // 眼睛主体（直接画 RoundedRectangle）
            double rad = Math.Min(w, eyeRect.Height) * radFactor;
            dc.DrawRoundedRectangle(RenderKit.BrushOf(eyeColor), null, eyeRect, rad, rad);

            // 机器人：屏幕微扫描线
            if (Style == "robot" && eyeRect.Height > 5 * s)
            {
                double scanY = eyeRect.Y + ((now * 14 * s) % eyeRect.Height);
                dc.DrawLine(RenderKit.PenOf(Color.FromArgb((byte)(70 * alpha), 80, 140, 220), 0.9 * s),
                    new Point(eyeRect.X + 1.2 * s, scanY), new Point(eyeRect.Right - 1.2 * s, scanY));
            }

            // 青蛙：跟随视线的瞳孔 + 高光点
            if (Style == "frog" && eyeRect.Height > 3.5 * s)
            {
                double px = rx + _ex.Value * w * 0.16;
                double py = ry + _ey.Value * eyeRect.Height * 0.14;
                double pr = Math.Max(1.2 * s, w * 0.18);
                dc.DrawEllipse(RenderKit.BrushOf(Color.FromArgb((byte)(235 * alpha), 28, 32, 42)), null,
                    new Point(px, py), pr, pr);
                dc.DrawEllipse(RenderKit.BrushOf(Color.FromArgb((byte)(230 * alpha), 255, 255, 255)), null,
                    new Point(px - pr * 0.35, py - pr * 0.35), Math.Max(0.6 * s, pr * 0.32), Math.Max(0.6 * s, pr * 0.32));
            }
            else if (Style is "mono" or "alien" && eyeRect.Height > 5 * s)
            {
                double hx = rx + _ex.Value * w * 0.14 - w * 0.14;
                double hy = ry + _ey.Value * eyeRect.Height * 0.12 - eyeRect.Height * 0.18;
                dc.DrawEllipse(RenderKit.BrushOf(Color.FromArgb((byte)(150 * alpha), 160, 215, 255)), null,
                    new Point(hx, hy), Math.Max(1.0 * s, w * 0.11), Math.Max(1.0 * s, w * 0.11));
            }

            // 眼睑：困倦从上往下盖，开心/wink 从下往上顶出弯弯笑眼
            var lidBrush = RenderKit.BrushOf(Color.FromArgb((byte)(240 * alpha), SurfaceColor.R, SurfaceColor.G, SurfaceColor.B));
            if (tired > 0.01)
            {
                double lidH = eyeRect.Height * 0.52 * tired;
                if (lidH > 0.5)
                    dc.DrawRoundedRectangle(lidBrush, null,
                        RenderKit.SafeRect(eyeRect.X - 1, eyeRect.Y - 1, w + 2, lidH + 1), 2, 2);
            }
            double happy = side < 0 ? _lh.Value : _rh.Value;
            if (happy > 0.01)
            {
                double cheekH = eyeRect.Height * 0.44 * happy;
                if (cheekH > 0.5)
                    dc.DrawRoundedRectangle(lidBrush, null,
                        RenderKit.SafeRect(eyeRect.X - 1, eyeRect.Bottom - cheekH, w + 2, cheekH + 1), w * 0.46, w * 0.46);
            }

            if (m == "star" && eyeRect.Height > 6 * s)
            {
                double tw1 = 0.8 + 0.28 * Math.Sin(now * 9.0);
                double tw2 = 0.8 + 0.28 * Math.Cos(now * 9.0);
                dc.DrawGeometry(RenderKit.BrushOf(Color.FromArgb((byte)(220 * alpha), 20, 20, 26)), null,
                    SparklePath(rx + 1.4 * s, ry - 2.2 * s, 3.4 * s * tw1));
                dc.DrawGeometry(RenderKit.BrushOf(Color.FromArgb((byte)(200 * alpha), 20, 20, 26)), null,
                    SparklePath(rx - 2.2 * s, ry + 3.0 * s, 1.7 * s * tw2));
            }
        }

        Point lastC = cCount > 1 ? rightC : leftC;
        if (cCount > 0) DrawContextEffects(dc, leftC, lastC, s, alpha, eyeColor, pink, m, now);

        // 腮红
        double bl = Easing.Clamp(_blush.Value, 0, 1);
        if (bl > 0.02 && cCount > 0)
        {
            var blushBrush = RenderKit.BrushOf(Color.FromArgb((byte)(150 * bl * alpha), 255, 124, 162));
            dc.DrawEllipse(blushBrush, null, new Point(leftC.X - 7.5 * s, leftC.Y + baseH * 0.55), 4.4 * s, 2.4 * s);
            if (cCount > 1)
                dc.DrawEllipse(blushBrush, null, new Point(rightC.X + 7.5 * s, rightC.Y + baseH * 0.55), 4.4 * s, 2.4 * s);
        }

        // 动态嘴型
        double mx = faceX + _ex.Value * 3.8 * s, my = faceY + baseH * 0.66 + _ey.Value * 3 * s;
        DrawMouth(dc, mx, my, s, eyeColor, m, now);

        // 猫咪胡须（嘴角两侧各两根）
        if (Style == "cat")
        {
            var wp = RenderKit.PenOf(eyeColor, 1.1 * s, 0.67 * alpha);
            foreach (int side in TwoSides)
            {
                double wx = faceX + side * (gap / 2 + baseW + 3 * s);
                dc.DrawLine(wp, new Point(wx, my - 2.5 * s), new Point(wx + side * 6 * s, my - 4 * s));
                dc.DrawLine(wp, new Point(wx, my + 0.5 * s), new Point(wx + side * 6 * s, my + 1.5 * s));
            }
        }
    }

    private static void DrawContextEffects(DrawingContext dc, Point lp, Point rp, double s, double alpha, Color eyeColor, Color pink, string m, double now)
    {
        if (m == "hot")
        {
            double fall = (now % 1.6) / 1.6;
            double dropX = rp.X + 8 * s, dropY = rp.Y - 12 * s + fall * 9 * s;
            var dropColor = Color.FromArgb((byte)(220 * alpha * (1 - fall * 0.4)), 130, 205, 255);
            dc.DrawEllipse(RenderKit.BrushOf(dropColor), null, new Point(dropX, dropY), 2.2 * s, 2.4 * s);
            var tip = new StreamGeometry();
            using (var ctx = tip.Open())
            {
                ctx.BeginFigure(new Point(dropX, dropY - 5 * s), true, true);
                ctx.LineTo(new Point(dropX - 1.9 * s, dropY - 0.8 * s), true, false);
                ctx.LineTo(new Point(dropX + 1.9 * s, dropY - 0.8 * s), true, false);
            }
            tip.Freeze();
            dc.DrawGeometry(RenderKit.BrushOf(dropColor), null, tip);
        }
        else if (m == "sleepy")
        {
            for (int i = 0; i < 3; i++)
            {
                double zt = ((now / 2.4) + i * 0.33) % 1;
                double zx = rp.X + 12 * s + i * 5.5 * s + Math.Sin(now * 2 + i) * 1.2 * s;
                double zy = rp.Y - 12 * s - zt * 12 * s;
                var zt2 = new FormattedText("z", System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    (6 + 4 * zt) * s,
                    RenderKit.BrushOf(Color.FromArgb((byte)(200 * alpha * (1 - zt * 0.5)), eyeColor.R, eyeColor.G, eyeColor.B)), 1);
                dc.DrawText(zt2, new Point(zx, zy));
            }
        }
        else if (m == "dizzy")
        {
            double midX = (lp.X + rp.X) * 0.5, topY = Math.Min(lp.Y, rp.Y) - 12.5 * s;
            for (int i = 0; i < 2; i++)
            {
                double a = now * 4.5 + i * Math.PI;
                double sx = midX + Math.Cos(a) * 10 * s;
                double sy = topY + Math.Sin(a) * 2.2 * s;
                dc.DrawGeometry(RenderKit.BrushOf(Color.FromArgb((byte)(210 * alpha), 255, 224, 120)), null,
                    SparklePath(sx, sy, 2.2 * s));
            }
        }
        else if (m == "love")
        {
            double t1 = (now * 0.9) % 1.0;
            double hx = rp.X + 9 * s + Math.Sin(now * 4) * 1.4 * s;
            double hy = rp.Y - 4 * s - t1 * 11 * s;
            var hc = Color.FromArgb((byte)(190 * alpha * (1 - t1)), pink.R, pink.G, pink.B);
            dc.DrawGeometry(RenderKit.BrushOf(hc), null, HeartPath(hx, hy, (3.6 - 0.8 * t1) * s));
        }
        else if (m == "star")
        {
            double tw = 0.6 + 0.4 * Math.Sin(now * 8);
            var sc = Color.FromArgb((byte)(195 * alpha * tw), 255, 235, 145);
            dc.DrawGeometry(RenderKit.BrushOf(sc), null, SparklePath(rp.X + 10 * s, rp.Y - 8 * s, 2.2 * s * tw));
        }
    }

    private void DrawMouth(DrawingContext dc, double mx, double my, double s, Color eyeColor, string m, double now)
    {
        var pen = RenderKit.PenOf(eyeColor, 1.8 * s);
        if (m == "surprise")
        {
            dc.DrawEllipse(null, pen, new Point(mx, my + 1.2 * s), 2.2 * s, 2.6 * s);
            return;
        }
        if (m == "sleepy")
        {
            double yawn = 0.5 + 0.5 * Math.Sin(now * 1.6);
            dc.DrawEllipse(null, pen, new Point(mx, my + 1.2 * s), 1.6 * s, (1.0 + 1.4 * yawn) * s);
            return;
        }
        var mouth = new StreamGeometry();
        using (var ctx = mouth.Open())
        {
            double mw = Style == "frog" ? 13 * s : 7 * s;
            if (m == "pout")
            {
                ctx.BeginFigure(new Point(mx - mw * 0.45, my + 2 * s), false, false);
                ctx.QuadraticBezierTo(new Point(mx, my - 2.2 * s), new Point(mx + mw * 0.45, my + 2 * s), true, false);
            }
            else if (m is "hot" or "dizzy")
            {
                ctx.BeginFigure(new Point(mx - mw * 0.5, my + 0.5 * s), false, false);
                ctx.QuadraticBezierTo(new Point(mx - mw * 0.25, my - 1.5 * s), new Point(mx, my + 0.5 * s), true, false);
                ctx.QuadraticBezierTo(new Point(mx + mw * 0.25, my + 2.5 * s), new Point(mx + mw * 0.5, my + 0.5 * s), true, false);
            }
            else if (Style == "cat" && m is "default" or "happy" or "shy" or "wink")
            {
                ctx.BeginFigure(new Point(mx - mw * 0.52, my - 0.3 * s), false, false);
                ctx.QuadraticBezierTo(new Point(mx - mw * 0.26, my + 3.2 * s), new Point(mx, my + 0.2 * s), true, false);
                ctx.QuadraticBezierTo(new Point(mx + mw * 0.26, my + 3.2 * s), new Point(mx + mw * 0.52, my - 0.3 * s), true, false);
            }
            else if (Style == "frog" && m is "default" or "shy")
            {
                ctx.BeginFigure(new Point(mx - mw / 2, my), false, false);
                ctx.LineTo(new Point(mx + mw / 2, my), true, false);
            }
            else
            {
                double depth = m == "laugh" ? 7.8 * s : 4.2 * s;
                ctx.BeginFigure(new Point(mx - mw / 2, my), false, false);
                ctx.QuadraticBezierTo(new Point(mx, my + depth), new Point(mx + mw / 2, my), true, false);
            }
        }
        mouth.Freeze();
        dc.DrawGeometry(null, pen, mouth);
    }

    private void DrawGhost(DrawingContext dc, double cx, double cy, double baseW, double baseH,
        double s, double alpha, Color eyeColor, Color pink, string mood, double blink, double ex, double ey, double now)
    {
        double w = baseW * 2.05, h = baseH * 1.15;
        double floatY = Math.Sin(now * 2.5) * 1.4 * s;
        double top = cy - h * 0.62 + floatY;
        double by = top + h * 0.62;

        var body = new StreamGeometry();
        using (var c = body.Open())
        {
            c.BeginFigure(new Point(cx - w / 2, by), true, true);
            c.ArcTo(new Point(cx + w / 2, by), new Size(w / 2, h * 0.62), 0, false, SweepDirection.Clockwise, true, false);
            double seg = w / 3.0;
            for (int i = 0; i < 3; i++)
            {
                double xEnd = cx + w / 2 - (i + 1) * seg;
                double waveY = by + Math.Sin(now * 4.5 + i * 1.6) * 1.1 * s;
                c.ArcTo(new Point(xEnd, i == 2 ? by : waveY), new Size(seg / 2, seg * 0.42), 0, false, SweepDirection.Clockwise, true, false);
            }
        }
        body.Freeze();
        dc.DrawGeometry(RenderKit.BrushOf(Color.FromArgb((byte)(235 * alpha), 240, 244, 255)), null, body);

        double wide = Easing.Clamp(_wide.Value, -0.3, 1);
        double erX = 2.2 * s * (1 + 0.15 * wide) * (1 + 0.2 * blink);
        double erY = Math.Max(0.8 * s, 2.2 * s * (1 + 0.2 * wide) * (1 - 0.85 * blink));
        double exo = ex * 3 * s, eyo = ey * 2.2 * s;
        var darkInk = Color.FromArgb((byte)(235 * alpha), 40, 46, 62);
        var inkPen = RenderKit.PenOf(darkInk, 1.3 * s);
        Point firstEye = default, lastEye = default;
        int idx = 0;
        foreach (int sd in TwoSides)
        {
            double rx = cx + sd * baseW * 0.48 + exo;
            double ry = top + h * 0.34 + eyo;
            if (idx++ == 0) firstEye = new Point(rx, ry);
            lastEye = new Point(rx, ry);
            if (mood == "sleepy")
            {
                dc.DrawLine(inkPen, new Point(rx - erX, ry), new Point(rx + erX, ry));
            }
            else if (mood == "dizzy")
            {
                double ang = now * 5.0 * sd;
                double r = erX * 1.15;
                double c1 = Math.Cos(ang + Math.PI * 0.25) * r, s1 = Math.Sin(ang + Math.PI * 0.25) * r;
                double c2 = Math.Cos(ang + Math.PI * 0.75) * r, s2 = Math.Sin(ang + Math.PI * 0.75) * r;
                dc.DrawLine(inkPen, new Point(rx - c1, ry - s1), new Point(rx + c1, ry + s1));
                dc.DrawLine(inkPen, new Point(rx - c2, ry - s2), new Point(rx + c2, ry + s2));
            }
            else if (mood == "love")
            {
                double beat = Math.Pow(Math.Max(0, Math.Sin(now * 4.5)), 4) * 0.14;
                dc.DrawGeometry(RenderKit.BrushOf(pink), null, HeartPath(rx, ry, 5.2 * s * (1 + beat)));
            }
            else if (mood == "star")
            {
                double tw = 0.85 + 0.25 * Math.Sin(now * 9.0);
                dc.DrawGeometry(RenderKit.BrushOf(darkInk), null, SparklePath(rx, ry, 2.8 * s * tw));
            }
            else if (mood == "squint")
            {
                int d = -sd; double k = 2.0 * s;
                dc.DrawLine(inkPen, new Point(rx - d * k, ry - k), new Point(rx + d * k, ry));
                dc.DrawLine(inkPen, new Point(rx + d * k, ry), new Point(rx - d * k, ry + k));
            }
            else
            {
                dc.DrawEllipse(RenderKit.BrushOf(darkInk), null, new Point(rx, ry), erX, erY);
            }
        }

        double bl = Easing.Clamp(_blush.Value, 0, 1);
        if (bl > 0.02)
        {
            var blushBrush = RenderKit.BrushOf(Color.FromArgb((byte)(150 * bl * alpha), 255, 124, 162));
            dc.DrawEllipse(blushBrush, null, new Point(cx - baseW * 0.65 + exo, top + h * 0.48 + eyo), 2.8 * s, 1.5 * s);
            dc.DrawEllipse(blushBrush, null, new Point(cx + baseW * 0.65 + exo, top + h * 0.48 + eyo), 2.8 * s, 1.5 * s);
        }

        if (mood == "surprise")
        {
            dc.DrawEllipse(null, inkPen, new Point(cx + exo, top + h * 0.50 + eyo), 1.6 * s, 2.0 * s);
        }
        else
        {
            var mouth = new StreamGeometry();
            using (var c = mouth.Open())
            {
                double mw = 3.4 * s;
                double my = top + h * 0.49 + eyo;
                c.BeginFigure(new Point(cx + exo - mw / 2, my), false, false);
                double depth = mood == "pout" ? -2.2 : mood == "laugh" ? 4.2 : 2.6;
                c.QuadraticBezierTo(new Point(cx + exo, my + depth * s), new Point(cx + exo + mw / 2, my), true, false);
            }
            mouth.Freeze();
            dc.DrawGeometry(null, inkPen, mouth);
        }

        DrawContextEffects(dc, firstEye, new Point(lastEye.X + 4 * s, lastEye.Y), s, alpha, eyeColor, pink, mood, now);
    }

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
        g.Freeze();
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
        g.Freeze();
        return g;
    }
}
