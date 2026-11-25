using System.Windows;
using System.Windows.Media;
using Island.App.Controls;

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

    // ---------- 每帧复用（2026-11-24）----------
    // Draw/Step 在 pet 视图下每帧都跑。这里的数组/列表/字型以前每帧 new 一遍（sides、弹簧数组、
    // centers、FontFamily+Typeface），现在只建一次：静止的 pet 视图每帧不再为它们分配。
    private static readonly int[] TwoSides = { -1, 1 };
    private static readonly int[] MonoSides = { 0 };
    private readonly Spring[] _springs;              // Step 每帧整体推进一遍
    private readonly List<Point> _centers = new(2);  // 各眼中心（腮红 / 汗滴 / Zzz 取用）

    // 困倦 "z" 的粗体字型：缓存一份（原来每帧 new FontFamily + new Typeface，一帧三只 z）
    private static readonly Typeface ZzzFace = new(
        new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public PetFace()
    {
        _springs = new[] { _ex, _ey, _lh, _rh, _tired, _wide, _morph, _blush };
    }

    private string _mood = "default";
    private string _lastMood = "";
    private bool _near;
    private double _lastAttention = Now;
    private double _surpriseUntil = 0;
    private double _blinkAt = Now + 2.5;
    private double _t = Now;

    public double Spread { get; set; } // 被按压时两眼额外分开
    public double Tilt { get; set; }   // 被按压时每只眼倾斜角

    /// <summary>
    /// 款式（8 款，与 mac 版 src/Island.Avalonia/PetFaceAv.cs 一一对应）：
    /// classic 经典双眼 / mono 独眼 / cat 猫咪（耳朵+胡须）/ robot 机器人（方眼+天线）/ pixel 像素；
    /// alien 外星人（第 6 款，圆眼+高光微眸+触角）/ ghost 幽灵（第 7 款，独立分支 DrawGhost）/
    /// frog 青蛙（第 8 款，圆眼+瞳孔+平嘴）。
    /// </summary>
    public string Style { get; set; } = "classic";

    // ---------- 情境感知（拓麻歌子式：对系统状态有反应；宿主每秒注入，纯读取零采集） ----------
    /// <summary>CPU 温度 ℃（≥85 流汗）。</summary>
    public double? CpuTemp { get; set; }
    /// <summary>磁盘活动时间 %（≥95 晕眩）。</summary>
    public double DiskActive { get; set; }
    /// <summary>眼睑遮罩色 = 本体表面色（宿主每帧注入，跟随主题）。</summary>
    public Color SurfaceColor { get; set; } = Color.FromRgb(16, 18, 24);
    /// <summary>
    /// 眼睛 / 嘴 / 天线的线色（宿主注入）。深色主题是亮白；浅色主题是深色 ——
    /// 2026-11-24 视觉重做发现：浅色主题下眼睛是写死的亮白，整个融进白色玻璃里，小形象等于隐形。
    /// </summary>
    public Color EyeColor { get; set; } = Color.FromRgb(240, 244, 255);

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
        foreach (var sp in _springs) sp.Step(dt);

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

    /// <summary>
    /// 画小形象。2026-11-24 起画刷/画笔全部取自 RenderKit 的冻结缓存，几何用完即 Freeze：
    /// 录制绘制指令时，未冻结的 Freezable 会被原样再克隆一份挂进指令流（一帧十几个画刷/画笔/几何
    /// 各克隆一次，全是只活一帧的垃圾）；冻结后录制直接引用缓存里的那一份，每帧零画刷/画笔分配。
    /// 离屏分层窗口探针实测（2026-11-24，pet-classic）：整块重绘一帧的分配 31.1KB → 27.6KB。
    /// 画法与参数未动。
    /// </summary>
    public void Draw(DrawingContext dc, double cx, double cy, double s, double alpha, Point look, bool near)
    {
        double blink = Step(look, near);
        string m = nowSurprise();
        double now = Now;   // 本帧时间戳：新增的三款（alien/ghost/frog）动效用它
        double tired = Easing.Clamp(_tired.Value, 0, 1);
        double wide = Easing.Clamp(_wide.Value, -0.3, 1);
        double morph = Easing.Clamp(_morph.Value, 0, 1);
        double bounce = m == "laugh" ? Math.Sin(Now * 16) * 1.6 * s : 0;

        double baseW = 11 * s, baseH = 15 * s, gap = 15 * s;
        var eyeColor = Color.FromArgb((byte)(255 * alpha), EyeColor.R, EyeColor.G, EyeColor.B);   // 线色跟随主题
        var glow = new Color { A = (byte)(90 * alpha), R = 120, G = 180, B = 255 };
        var pink = Color.FromArgb((byte)(255 * alpha), 255, 92, 138);
        var eyeBrush = RenderKit.BrushOf(eyeColor);   // 本帧用得最多的画刷，取一次
        var centers = _centers;
        centers.Clear();

        // 款式参数：mono 单只大眼；robot 方眼角；cat 圆眼角 + 耳朵胡须；pixel 纯方块无光晕
        int[] sides = Style == "mono" ? MonoSides : TwoSides;
        double eyeW = baseW * (Style == "mono" ? 1.5 : 1);
        double eyeH = baseH * (Style == "mono" ? 1.15 : 1);
        // 圆角半径系数：radFactor（圆角半径 = min(宽,高) × 系数）。
        // alien / frog 是正圆眼 ⇒ 0.5（mac 版 PetFaceAv.cs L250-L259）。
        // ⚠️ pixel 保持 Windows 原值 0.04（mac 是 0），本轮不动既有 5 款，避免回归。
        double radFactor = Style switch
        {
            "robot" => 0.18,
            "cat" => 0.5,
            "mono" => 0.46,
            "pixel" => 0.04,
            "alien" => 0.5,   // mac 版第 6 款：正圆眼
            "frog" => 0.5,    // mac 版第 8 款：正圆眼
            _ => 0.42,
        };

        // 幽灵款（mac 版第 7 款）：不是"眼睛主体循环"里的变体，而是**整只形象单独画**，
        // mac 侧同样是独立分支 PetFaceAv.cs L262-L266 → DrawGhost（L609-L714）。Windows 侧照此结构新建。
        if (Style == "ghost")
        {
            DrawGhost(dc, cx, cy, baseW, baseH, s, alpha, eyeColor, pink, m, blink, _ex.Value, _ey.Value, now);
            return;
        }

        // 机器人天线（先画，压在眼睛后面）
        if (Style == "robot")
        {
            var antPen = RenderKit.PenOf(eyeColor, 1.3 * s);
            double at = cy - baseH * 0.72;
            dc.DrawLine(antPen, new Point(cx, at), new Point(cx, at - 5 * s));
            dc.DrawEllipse(RenderKit.BrushOf(glow), null, new Point(cx, at - 6.5 * s), 2.4 * s, 2.4 * s);
            dc.DrawEllipse(eyeBrush, null, new Point(cx, at - 6.5 * s), 1.2 * s, 1.2 * s);
        }

        // 外星人触角（mac 版第 6 款，PetFaceAv.cs L294-L310）：左右各一根，
        // 随视线摆动 + 自呼吸式摆动，顶端一颗外晕 + 内芯的小灯。
        if (Style == "alien")
        {
            var apen = RenderKit.PenOf(eyeColor, 1.1 * s);
            double sway = (_ex.Value * 0.9 + Math.Sin(now * 2.4) * 0.45) * s;   // 视线摆 + 呼吸式摆动
            double pulse = 0.7 + 0.3 * Math.Sin(now * 3.6);                    // 顶端灯的呼吸
            // mac 侧的 headParallax 来自整套视差系统（呼吸/回弹/摇摆弹簧）；
            // Windows 本轮没有那套弹簧，只取视线分量（公式与 mac 相同）。
            double headParallax = _ex.Value * 2.2 * s;
            foreach (int sd in TwoSides)
            {
                double ax = cx + headParallax + sd * baseW * 0.42;   // 根部
                double ay = cy - baseH * 0.62;
                var tip = new Point(ax + sd * 2.2 * s + sway, ay - 4.4 * s);   // 终点（向外斜上）
                dc.DrawLine(apen, new Point(ax, ay), tip);
                // 外晕（RGB 140,255,200，α = 110×alpha×pulse）+ 内芯（眼色）
                dc.DrawEllipse(RenderKit.BrushOf(Color.FromArgb((byte)(110 * alpha * pulse), 140, 255, 200)),
                    null, new Point(tip.X, tip.Y - 0.8 * s), 2.3 * s, 2.3 * s);
                dc.DrawEllipse(RenderKit.BrushOf(eyeColor), null, new Point(tip.X, tip.Y - 0.8 * s), 1.3 * s, 1.3 * s);
            }
        }

        foreach (int side in sides)
        {
            double rx = cx + side * (gap / 2 + baseW / 2 + Spread * s / 2) + _ex.Value * 5 * s;
            double ry = cy + _ey.Value * 3 * s + bounce;
            centers.Add(new Point(rx, ry));
            double curious = 1 + 0.22 * Math.Max(0, _ex.Value * side);
            double w = eyeW * (1 + 0.12 * wide) * (1 + 0.25 * blink);
            double h = eyeH * curious * (1 + 0.18 * wide) * (1 - 0.88 * blink);
            var eyeRect = RenderKit.SafeRect(rx - w / 2, ry - h / 2, w, Math.Max(1.6 * s, h));

            // 光晕（像素款不要光晕——点阵屏没有渐变）。径向渐变按光晕色缓存（RenderKit.RadialBrushOf），
            // 两只眼共用同一份：渐变是相对包围盒映射的，几何各画各的，不受共用影响
            if (Style != "pixel")
                dc.DrawEllipse(RenderKit.RadialBrushOf(glow), null,
                    new Point(eyeRect.X + w / 2, eyeRect.Y + h / 2), w * 0.95, h * 0.75 + 3 * s);

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
                dc.DrawGeometry(eyeBrush, null, ear);
            }

            if (m == "love" && morph > 0.05)
            {
                var heart = HeartPath(rx, ry, 15 * s * (1 + 0.08 * Math.Sin(Now * 7)) * (0.6 + 0.4 * morph));
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
            // 晕眩（磁盘打满）：× 形眼
            if (m == "dizzy")
            {
                var xp = RenderKit.PenOf(eyeColor, 2.2 * s);
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
            eye.Freeze();
            dc.DrawGeometry(eyeBrush, null, eye);

            // 青蛙：跟随视线的深色瞳孔 + 白色高光点（mac 版第 8 款，PetFaceAv.cs L413-L423）
            if (Style == "frog" && eyeRect.Height > 3.5 * s)
            {
                double px = rx + _ex.Value * w * 0.16;
                double py = ry + _ey.Value * eyeRect.Height * 0.14;
                double pr = Math.Max(1.2 * s, w * 0.18);
                dc.DrawEllipse(RenderKit.BrushOf(Color.FromArgb((byte)(235 * alpha), 28, 32, 42)), null,
                    new Point(px, py), pr, pr);
                double hr = Math.Max(0.6 * s, pr * 0.32);
                dc.DrawEllipse(RenderKit.BrushOf(Color.FromArgb((byte)(230 * alpha), 255, 255, 255)), null,
                    new Point(px - pr * 0.35, py - pr * 0.35), hr, hr);
            }
            // 外星人：高光微眸随视线微移（mac 版第 6 款，PetFaceAv.cs L424-L431）。
            // ⚠️ mac 里这一段和 mono 共用（"mono" or "alien"），本轮只给 alien 加，不动 mono。
            else if (Style == "alien" && eyeRect.Height > 5 * s)
            {
                double hx = rx + _ex.Value * w * 0.14 - w * 0.14;
                double hy = ry + _ey.Value * eyeRect.Height * 0.12 - eyeRect.Height * 0.18;
                double hr = Math.Max(1.0 * s, w * 0.11);
                dc.DrawEllipse(RenderKit.BrushOf(Color.FromArgb((byte)(150 * alpha), 160, 215, 255)), null,
                    new Point(hx, hy), hr, hr);
            }

            // 眼睑（困倦/开心月牙）：上沿压一块本体色遮罩——此前这块只构建了几何但从没画出来
            double happy = side < 0 ? _lh.Value : _rh.Value;
            if (tired > 0.01 || happy > 0.01)
            {
                double lidH = eyeRect.Height * (0.5 * tired + 0.35 * happy);
                if (lidH > 0.5)
                    dc.DrawRoundedRectangle(RenderKit.BrushOf(Color.FromArgb(235, SurfaceColor.R, SurfaceColor.G, SurfaceColor.B)), null,
                        RenderKit.SafeRect(eyeRect.X - 1, eyeRect.Y - 1, w + 2, lidH + 1), 2, 2);
            }
            if (m == "star" && eyeRect.Height > 6 * s)
            {
                dc.DrawGeometry(RenderKit.BrushOf(Color.FromArgb((byte)(220 * alpha), 20, 20, 26)), null,
                    SparklePath(rx + 1.4 * s, ry - 2.2 * s, 3.4 * s));
                dc.DrawGeometry(RenderKit.BrushOf(Color.FromArgb((byte)(200 * alpha), 20, 20, 26)), null,
                    SparklePath(rx - 2.2 * s, ry + 3.0 * s, 1.6 * s));
            }
        }

        // 情境附加：高温汗滴（右眼旁，循环下落）/ 深夜困倦 Zzz（右上飘升）
        // 抽成独立方法：幽灵款走自己的绘制分支，但要复用同一套情境反应
        // （mac 侧同样是 DrawContextEffects，PetFaceAv.cs L493-L552）。
        DrawContextEffects(dc, centers, s, alpha, eyeColor, m);

        // 腮红（两眼各一点；mono 款只有一只眼 ⇒ 只画第一点，与原来 centers.Zip({-1,1}) 的配对结果一致）
        double bl = Easing.Clamp(_blush.Value, 0, 1);
        if (bl > 0.02)
        {
            var blushBrush = RenderKit.BrushOf(Color.FromArgb((byte)(150 * bl * alpha), 255, 124, 162));
            for (int i = 0; i < centers.Count && i < TwoSides.Length; i++)
                dc.DrawEllipse(blushBrush, null,
                    new Point(centers[i].X + TwoSides[i] * 7.5 * s, centers[i].Y + baseH * 0.55), 4.4 * s, 2.4 * s);
        }

        // 嘴
        double mx = cx + _ex.Value * 5 * s, my = cy + baseH * 0.66 + _ey.Value * 3 * s + bounce;
        var mouth = new StreamGeometry();
        using (var ctx = mouth.Open())
        {
            // 青蛙嘴更宽（13*s，其余款式 7*s）；mac 版第 8 款，PetFaceAv.cs L571。
            double mw = (Style == "frog" ? 13 : 7) * s;
            if (Style == "frog" && (m == "default" || m == "shy"))
            {
                // 青蛙：平直横线嘴（mac 版第 8 款，PetFaceAv.cs L590-L594）
                ctx.BeginFigure(new Point(mx - mw / 2, my), false, false);
                ctx.LineTo(new Point(mx + mw / 2, my), true, false);
            }
            else
            {
                // 其余款式（含外星人）：通用二次贝塞尔嘴
                ctx.BeginFigure(new Point(mx - mw / 2, my), false, false);
                ctx.QuadraticBezierTo(new Point(mx, my + (m == "laugh" ? 8.5 : 4.2) * s),
                                      new Point(mx + mw / 2, my), true, false);
            }
        }
        mouth.Freeze();
        dc.DrawGeometry(null, RenderKit.PenOf(eyeColor, 1.8 * s), mouth);

        // 猫咪胡须（嘴角两侧各两根）
        if (Style == "cat")
        {
            var wp = RenderKit.PenOf(Color.FromArgb((byte)(170 * alpha), eyeColor.R, eyeColor.G, eyeColor.B), 1.1 * s);
            foreach (int side in TwoSides)
            {
                double wx = cx + side * (gap / 2 + baseW + 3 * s);
                dc.DrawLine(wp, new Point(wx, my - 2.5 * s), new Point(wx + side * 6 * s, my - 4 * s));
                dc.DrawLine(wp, new Point(wx, my + 0.5 * s), new Point(wx + side * 6 * s, my + 1.5 * s));
            }
        }
    }

    /// <summary>
    /// 情境附加反应：高温流汗（眼旁循环下落的汗滴）/ 深夜困倦 Zzz（右上飘升）。
    /// 从主绘制流程里抽成独立方法，幽灵款（独立分支）复用同一套反应。
    /// </summary>
    private void DrawContextEffects(DrawingContext dc, List<Point> centers, double s, double alpha, Color eyeColor, string m)
    {
        double now = Now;
        if (m == "hot" && centers.Count > 0)
        {
            double fall = (now % 1.6) / 1.6;
            var rp = centers[^1];
            double dropX = rp.X + 8 * s, dropY = rp.Y - 12 * s + fall * 9 * s;
            // 透明度随 fall 连续变：按颜色走缓存即可（整段下落不到 90 种透明度，够不到缓存上限）
            var dropColor = Color.FromArgb((byte)(220 * alpha * (1 - fall * 0.4)), 130, 205, 255);
            var dropBrush = RenderKit.BrushOf(dropColor);
            dc.DrawEllipse(dropBrush, null, new Point(dropX, dropY), 2.2 * s, 2.4 * s);
            var tip = new StreamGeometry();
            using (var ctx = tip.Open())
            {
                ctx.BeginFigure(new Point(dropX, dropY - 5 * s), true, true);
                ctx.LineTo(new Point(dropX - 1.9 * s, dropY - 0.8 * s), true, false);
                ctx.LineTo(new Point(dropX + 1.9 * s, dropY - 0.8 * s), true, false);
            }
            tip.Freeze();
            dc.DrawGeometry(dropBrush, null, tip);
        }
        if (m == "sleepy" && centers.Count > 0)
        {
            for (int i = 0; i < 3; i++)
            {
                double zt = ((now / 2.4) + i * 0.33) % 1;
                double zx = centers[^1].X + 12 * s + i * 5.5 * s + Math.Sin(now * 2 + i) * 1.2 * s;
                double zy = centers[^1].Y - 12 * s - zt * 12 * s;
                // 字号随 zt 连续变，FormattedText 没法缓存（每帧仍建 3 个）；字型和画刷不再每帧建
                var zt2 = new FormattedText("z", System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    ZzzFace,
                    (6 + 4 * zt) * s,
                    RenderKit.BrushOf(Color.FromArgb((byte)(200 * alpha * (1 - zt * 0.5)), eyeColor.R, eyeColor.G, eyeColor.B)), 1);
                dc.DrawText(zt2, new Point(zx, zy));
            }
        }
    }

    /// <summary>
    /// 幽灵款（mac 版第 7 款）：无头无四肢的单块填充轮廓 + 深墨色小圆眼。
    /// 对应 mac 源码 src/Island.Avalonia/PetFaceAv.cs 的 DrawGhost（L609-L714）——
    /// mac 侧在 Draw 开头就 return，Windows 侧照此结构新建一个等价分支。
    /// </summary>
    private void DrawGhost(DrawingContext dc, double cx, double cy, double baseW, double baseH,
        double s, double alpha, Color eyeColor, Color pink, string mood, double blink, double ex, double ey, double now)
    {
        double w = baseW * 2.05, h = baseH * 1.15;
        double floatY = Math.Sin(now * 2.5) * 1.4 * s;          // 整体上下浮动
        double top = cy - h * 0.62 + floatY;
        double by = top + h * 0.62;

        // 身体：上半圆脑袋 + 3 段流动波浪底边（实心、无描边）
        // ⚠️ 身体色是固定色 RGB(240,244,255)（α = 235×alpha），不跟随主题——与 mac 侧一致。
        var body = new StreamGeometry();
        using (var c = body.Open())
        {
            c.BeginFigure(new Point(cx - w / 2, by), true, true);
            c.ArcTo(new Point(cx + w / 2, by), new Size(w / 2, h * 0.62), 0, false, SweepDirection.Clockwise, true, false);
            double seg = w / 3.0;
            for (int i = 0; i < 3; i++)
            {
                double xEnd = cx + w / 2 - (i + 1) * seg;
                double waveY = by + Math.Sin(now * 4.5 + i * 1.6) * 1.1 * s;   // 流动起伏
                c.ArcTo(new Point(xEnd, i == 2 ? by : waveY), new Size(seg / 2, seg * 0.42), 0, false, SweepDirection.Clockwise, true, false);
            }
        }
        body.Freeze();
        dc.DrawGeometry(RenderKit.BrushOf(Color.FromArgb((byte)(235 * alpha), 240, 244, 255)), null, body);

        // 两只深墨色小圆眼（眨眼压扁 + 困倦/晕眩/爱心/星星/眯眼的表情替换）
        double wide = Easing.Clamp(_wide.Value, -0.3, 1);
        double erX = 2.2 * s * (1 + 0.15 * wide) * (1 + 0.2 * blink);
        double erY = Math.Max(0.8 * s, 2.2 * s * (1 + 0.2 * wide) * (1 - 0.85 * blink));
        double exo = ex * 3 * s, eyo = ey * 2.2 * s;
        var darkInk = Color.FromArgb((byte)(235 * alpha), 40, 46, 62);
        var inkPen = RenderKit.PenOf(darkInk, 1.3 * s);
        var centers = _centers;
        foreach (int sd in TwoSides)
        {
            double rx = cx + sd * baseW * 0.48 + exo;
            double ry = top + h * 0.34 + eyo;
            centers.Add(new Point(rx, ry));
            if (mood == "sleepy")
            {
                // 困倦：水平线
                dc.DrawLine(inkPen, new Point(rx - erX, ry), new Point(rx + erX, ry));
            }
            else if (mood == "dizzy")
            {
                // 晕眩：旋转 × 形
                double ang = now * 5.0 * sd;
                double r = erX * 1.15;
                double c1 = Math.Cos(ang + Math.PI * 0.25) * r, s1 = Math.Sin(ang + Math.PI * 0.25) * r;
                double c2 = Math.Cos(ang + Math.PI * 0.75) * r, s2 = Math.Sin(ang + Math.PI * 0.75) * r;
                dc.DrawLine(inkPen, new Point(rx - c1, ry - s1), new Point(rx + c1, ry + s1));
                dc.DrawLine(inkPen, new Point(rx - c2, ry - s2), new Point(rx + c2, ry + s2));
            }
            else if (mood == "love")
            {
                // 爱心（心跳脉冲）
                double beat = Math.Pow(Math.Max(0, Math.Sin(now * 4.5)), 4) * 0.14;
                dc.DrawGeometry(RenderKit.BrushOf(pink), null, HeartPath(rx, ry, 5.2 * s * (1 + beat)));
            }
            else if (mood == "star")
            {
                // 四芒星（闪烁）
                double tw = 0.85 + 0.25 * Math.Sin(now * 9.0);
                dc.DrawGeometry(RenderKit.BrushOf(darkInk), null, SparklePath(rx, ry, 2.8 * s * tw));
            }
            else if (mood == "squint")
            {
                // 眯眼：折线
                int d = -sd; double k = 2.0 * s;
                dc.DrawLine(inkPen, new Point(rx - d * k, ry - k), new Point(rx + d * k, ry));
                dc.DrawLine(inkPen, new Point(rx + d * k, ry), new Point(rx - d * k, ry + k));
            }
            else
            {
                // 默认：填充椭圆
                dc.DrawEllipse(RenderKit.BrushOf(darkInk), null, new Point(rx, ry), erX, erY);
            }
        }

        // 腮红（左右各一点）
        double bl = Easing.Clamp(_blush.Value, 0, 1);
        if (bl > 0.02)
        {
            var blushBrush = RenderKit.BrushOf(Color.FromArgb((byte)(150 * bl * alpha), 255, 124, 162));
            dc.DrawEllipse(blushBrush, null, new Point(cx - baseW * 0.65 + exo, top + h * 0.48 + eyo), 2.8 * s, 1.5 * s);
            dc.DrawEllipse(blushBrush, null, new Point(cx + baseW * 0.65 + exo, top + h * 0.48 + eyo), 2.8 * s, 1.5 * s);
        }

        // 小嘴：惊讶 → 小椭圆；其余 → 二次贝塞尔（pout 上翘 / laugh 大笑 / 默认浅弧）
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

        // 情境反应（高温汗滴 / 深夜 Zzz）与主流程共用
        DrawContextEffects(dc, centers, s, alpha, eyeColor, mood);
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
