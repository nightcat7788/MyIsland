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
    // 心情表：8 款，与 mac 版 src/Island.Avalonia/PetFaceAv.cs 一一对应（mac 多一个 laugh）。
    private static readonly string[] Moods = { "happy", "wink", "love", "star", "shy", "pout", "squint", "laugh" };

    private readonly Spring _ex = new(0, 140, 0.8, 0.01);   // 视线 x
    private readonly Spring _ey = new(0, 140, 0.8, 0.01);   // 视线 y
    private readonly Spring _lh = new(0, 160, 0.75, 0.003); // 左眼开心月牙
    private readonly Spring _rh = new(0, 160, 0.75, 0.003); // 右眼
    private readonly Spring _tired = new(0, 60, 1.0, 0.003);
    private readonly Spring _wide = new(0, 220, 0.45, 0.003);
    private readonly Spring _morph = new(0, 200, 0.7, 0.003);
    private readonly Spring _blush = new(0, 120, 0.9, 0.003);
    // 下面三个是 mac 的动效骨架（呼吸之外的位移/形变），Windows 侧此前没有，本轮补上（参数与 mac 逐条一致）
    private readonly Spring _squash = new(0, 260, 0.45, 0.003);  // 挤压回弹
    private readonly Spring _bob = new(0, 185, 0.72, 0.004);     // 上下弹跳
    private readonly Spring _sway = new(0, 95, 0.82, 0.004);     // 左右摆

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
        _springs = new[] { _ex, _ey, _lh, _rh, _tired, _wide, _morph, _blush, _squash, _bob, _sway };
    }

    private string _mood = "default";
    private string _lastMood = "";
    private bool _near;
    private double _lastAttention = Now;
    private double _surpriseUntil = 0;
    private string _reactMood = "surprise";                       // 连环反应的当前表情
    private double _blinkAt = Now + 2.5;
    private double _nextIdleGlance = Now + 2.2;                   // 下次闲置扫视
    private double _idleGlanceUntil;                             // 扫视持续到什么时候
    private double _idleLookX, _idleLookY;                       // 扫视落点
    private double _nextIdleMotion = Now + 3.0;                   // 下次闲置小动作（弹跳/侧摆）
    private double _nextIdleWink = Now + Random.Shared.NextDouble() * 10 + 5;  // 下次闲置眨眼
    private double _idleWinkUntil;                               // 闲置眨眼持续到什么时候
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
        if (now < _surpriseUntil) return _reactMood;
        if (near) return _mood;
        if (CpuTemp is >= 85) return "hot";
        if (DiskActive >= 95) return "dizzy";
        if ((DateTime.Now.Hour >= 23 || DateTime.Now.Hour < 6) && now - _lastAttention > 30) return "sleepy";
        if (now < _idleWinkUntil) return "wink";   // 闲置随机眨眼（优先级低于情境表情）
        return "default";
    }

    private static double Now => Environment.TickCount64 / 1000.0;

    public void React(string kind = "surprise")
    {
        double now = Now;
        _lastAttention = now;
        if (kind == "surprise")
        {
            // 连环反应：surprise → laugh → love → star → surprise（mac 版 PetFaceAv.cs L117-L130）
            _reactMood = now < _surpriseUntil
                ? _reactMood switch { "surprise" => "laugh", "laugh" => "love", "love" => "star", _ => "surprise" }
                : "surprise";
            _surpriseUntil = now + 1.4;
            _wide.Kick(9);                                   // 睁大眼
            _squash.Kick(-7.5);                              // 压扁一下
            _bob.Kick(-34);                                  // 弹起来
            _sway.Kick(Random.Shared.NextDouble() * 18 - 9);  // 随机侧摆
        }
    }

    private void PickMood()
    {
        // 与 mac 版一致：常态从前 7 个里随机挑；抽到与上次相同的就落到最后一个（laugh）。
        // 这样 laugh 有明确来源（连续两次抽中同一表情），而不是凭空塞进来。
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
            _squash.Kick(-4.2);                                  // 靠近时轻压一下
            _bob.Kick(-20);                                      // 轻轻弹一下
            _sway.Kick(Math.Clamp(look.X / 18, -1, 1) * 10);      // 视线方向带一点侧摆
        }
        else if (!near && _near) _mood = "default";
        _near = near;
        if (near) _lastAttention = now;

        // 安静时偶尔做一次短促扫视，然后自然回到正前方，避免一直机械地左右摆动。
        if (!near && now >= _nextIdleGlance)
        {
            _idleLookX = Random.Shared.NextDouble() * 1.2 - 0.6;
            _idleLookY = Random.Shared.NextDouble() * 0.56 - 0.28;
            _idleGlanceUntil = now + 0.22 + Random.Shared.NextDouble() * 0.28;   // 持续 0.22–0.5s
            _nextIdleGlance = now + 3.0 + Random.Shared.NextDouble() * 4.0;      // 每 3–7s 一次
        }
        // 闲置随机眨眼（wink）
        if (!near && now >= _nextIdleWink)
        {
            _idleWinkUntil = now + 0.42;                                          // 持续 0.42s
            _nextIdleWink = now + 11.0 + Random.Shared.NextDouble() * 15.0;       // 每 11–26s 一次
        }
        // 闲置时的小幅弹跳 / 侧摆
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
            // 扫视期间保持落点，结束后回正；只叠加很轻的呼吸式眼动。
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
        _squash.To(0);   // 挤压弹簧只在被 Kick 时动，其余时间回 0
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

        // ---- 动效骨架（对齐 mac 版 PetFaceAv.cs L262-L283）----
        // 挤压回弹：横向压扁、纵向拉长（mac：scaleX = 1 - sq*0.14，scaleY = 1 + sq*0.16）
        double sq = Easing.Clamp(_squash.Value, -0.45, 0.55);
        double scaleX = 1.0 - sq * 0.14;
        double scaleY = 1.0 + sq * 0.16;
        // 呼吸起伏：2.2/s，困倦时放慢到 1.1/s
        double breathRate = m == "sleepy" ? 1.1 : 2.2;
        double breath = Math.Sin(now * breathRate);
        double breathY = breath * (m == "sleepy" ? 0.9 : 0.55) * s;
        // 上下弹跳（弹簧）+ 大笑时的额外抖动；高温时整体高频抖动
        double bounce = _bob.Value * s + (m == "laugh" ? Math.Sin(now * 12) * 0.8 * s : 0);
        double jitterX = m == "hot" ? Math.Sin(now * 28) * 0.35 * s : 0;
        double faceX = cx + jitterX + _sway.Value * s;   // 左右摆
        double faceY = cy + breathY + bounce;
        double headParallax = _ex.Value * 2.2 * s;      // 头部视差（刘海 / 触角 / 天线共用）

        double baseW = 11 * s * scaleX * (1 + breath * 0.006);
        double baseH = 15 * s * scaleY * (1 + breath * 0.012);
        double gap = 15 * s * scaleX;
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
        // pixel 是纯方块眼 ⇒ 0（mac 版同值；Windows 原为 0.04，本轮对齐）。
        double radFactor = Style switch
        {
            "robot" => 0.18,
            "cat" => 0.5,
            "mono" => 0.46,
            "pixel" => 0,
            "alien" => 0.5,   // mac 版第 6 款：正圆眼
            "frog" => 0.5,    // mac 版第 8 款：正圆眼
            _ => 0.42,
        };

        // 幽灵款（mac 版第 7 款）：不是"眼睛主体循环"里的变体，而是**整只形象单独画**，
        // mac 侧同样是独立分支 PetFaceAv.cs L262-L266 → DrawGhost（L609-L714）。Windows 侧照此结构新建。
        if (Style == "ghost")
        {
            DrawGhost(dc, faceX, faceY, baseW, baseH, s, alpha, eyeColor, pink, m, blink, _ex.Value, _ey.Value, now);
            return;
        }

        // 机器人天线（先画，压在眼睛后面）：顶灯呼吸闪烁；高温改橙红快闪；
        // 惊讶或高温时从顶灯发散信号波环（mac 版 PetFaceAv.cs L268-L292）。
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
                double wr = (2.8 + waveT * 5.2) * s;      // 半径 2.8s → 8.0s
                var wColor = m == "hot"
                    ? Color.FromArgb((byte)(170 * alpha * (1 - waveT)), 255, 120, 80)
                    : Color.FromArgb((byte)(160 * alpha * (1 - waveT)), 140, 200, 255);
                dc.DrawEllipse(null, RenderKit.PenOf(wColor, 1.1 * s), new Point(ax, tipY), wr, wr * 0.65);
            }
        }

        // 外星人触角（mac 版第 6 款，PetFaceAv.cs L294-L310）：左右各一根，
        // 随视线摆动 + 自呼吸式摆动，顶端一颗外晕 + 内芯的小灯。
        if (Style == "alien")
        {
            var apen = RenderKit.PenOf(eyeColor, 1.1 * s);
            double sway = (_ex.Value * 0.9 + Math.Sin(now * 2.4) * 0.45) * s;   // 视线摆 + 呼吸式摆动
            double pulse = 0.7 + 0.3 * Math.Sin(now * 3.6);                    // 顶端灯的呼吸
            // headParallax 用 Draw 层的全局值（mac 侧同样取自整套视差系统）。
            foreach (int sd in TwoSides)
            {
                double ax = faceX + headParallax + sd * baseW * 0.42;   // 根部
                double ay = faceY - baseH * 0.62;
                var tip = new Point(ax + sd * 2.2 * s + sway, ay - 4.4 * s);   // 终点（向外斜上）
                dc.DrawLine(apen, new Point(ax, ay), tip);
                // 外晕（RGB 140,255,200，α = 110×alpha×pulse）+ 内芯（眼色）
                dc.DrawEllipse(RenderKit.BrushOf(Color.FromArgb((byte)(110 * alpha * pulse), 140, 255, 200)),
                    null, new Point(tip.X, tip.Y - 0.8 * s), 2.3 * s, 2.3 * s);
                dc.DrawEllipse(RenderKit.BrushOf(eyeColor), null, new Point(tip.X, tip.Y - 0.8 * s), 1.3 * s, 1.3 * s);
            }
        }

        // 像素风：刘海 5 块（保留视差，mac 版 PetFaceAv.cs L312-L323）
        if (Style == "pixel")
        {
            // ⚠️ mac 这里是写死的亮白；Windows 的线色由宿主按主题注入，这里用 EyeColor 跟随主题
            var brow = RenderKit.BrushOf(Color.FromArgb((byte)(230 * alpha), EyeColor.R, EyeColor.G, EyeColor.B));
            for (int i = 0; i < 5; i++)
            {
                double bx = faceX + headParallax - baseW * 0.95 + i * baseW * 0.48;
                double bh = (i == 1 || i == 2 || i == 3) ? 4.2 * s : 2.4 * s;
                dc.DrawRoundedRectangle(brow, null,
                    RenderKit.SafeRect(bx, faceY - baseH * 0.86 - bh, baseW * 0.44, bh), 0.6 * s, 0.6 * s);
            }
        }

        // 猫咪耳朵抖动：悬停 / 惊讶 / 开心时轻微抽动（mac 版 PetFaceAv.cs L326-L328）
        double earTwitch = (Style == "cat" && (near || m is "surprise" or "happy" or "laugh"))
            ? Math.Sin(now * 10) * 0.9 * s * (0.4 + Math.Min(1, Math.Abs(wide)))
            : 0;

        foreach (int side in sides)
        {
            double rx = faceX + side * (gap / 2 + baseW / 2 + Spread * s / 2) + _ex.Value * 5 * s;
            double ry = faceY + _ey.Value * 3 * s;
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

            // 猫咪耳朵（先于眼睛主体，与心情无关始终在场；带视差偏移与抽动）
            if (Style == "cat")
            {
                double earShift = -_ex.Value * 1.4 * s;   // 随视线平移
                var ear = new StreamGeometry();
                using (var ctx = ear.Open())
                {
                    ctx.BeginFigure(new Point(rx - w * 0.55 + earShift, eyeRect.Top + 2 * s), true, true);
                    ctx.LineTo(new Point(rx - w * 0.08 + earShift, eyeRect.Top + 1 * s), true, false);
                    ctx.LineTo(new Point(rx - w * 0.34 + earShift + side * earTwitch, eyeRect.Top - 6 * s - Math.Abs(earTwitch) * 0.5), true, false);
                }
                ear.Freeze();
                dc.DrawGeometry(eyeBrush, null, ear);
            }

            if (m == "love" && morph > 0.05)
            {
                // 爱心眼：双跳心跳脉冲（mac 版 PetFaceAv.cs L378-L381）
                double beat = Math.Pow(Math.Max(0, Math.Sin(now * 4.5)), 4) * 0.14
                            + Math.Pow(Math.Max(0, Math.Sin(now * 4.5 - 0.55)), 6) * 0.09;
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
            // 晕眩（磁盘打满）：× 形眼
            if (m == "dizzy")
            {
                var xp = RenderKit.PenOf(eyeColor, 2.2 * s);
                double ex = w * 0.28, ey = Math.Max(1.6 * s, h) * 0.28;
                dc.DrawLine(xp, new Point(rx - ex, ry - ey), new Point(rx + ex, ry + ey));
                dc.DrawLine(xp, new Point(rx + ex, ry - ey), new Point(rx - ex, ry + ey));
                continue;
            }

            // 眼睛主体（圆角矩形，圆角随款式；radFactor = 0 的款式（pixel）画纯方块）
            double rad = Math.Min(w, eyeRect.Height) * radFactor;
            if (rad < 0.01)
            {
                dc.DrawRectangle(eyeBrush, null, eyeRect);
            }
            else
            {
                var eye = new StreamGeometry();
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
            }

            // 机器人：屏幕微扫描线（mac 版 PetFaceAv.cs L410-L416）
            if (Style == "robot" && eyeRect.Height > 5 * s)
            {
                double scanY = eyeRect.Y + ((now * 14 * s) % eyeRect.Height);
                dc.DrawLine(RenderKit.PenOf(Color.FromArgb((byte)(70 * alpha), 80, 140, 220), 0.9 * s),
                    new Point(eyeRect.X + 1.2 * s, scanY), new Point(eyeRect.Right - 1.2 * s, scanY));
            }

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
            // 独眼 / 外星人：高光微眸随视线微移（mac 版第 2/6 款共用同一段，PetFaceAv.cs L424-L431）。
            else if ((Style == "mono" || Style == "alien") && eyeRect.Height > 5 * s)
            {
                double hx = rx + _ex.Value * w * 0.14 - w * 0.14;
                double hy = ry + _ey.Value * eyeRect.Height * 0.12 - eyeRect.Height * 0.18;
                double hr = Math.Max(1.0 * s, w * 0.11);
                dc.DrawEllipse(RenderKit.BrushOf(Color.FromArgb((byte)(150 * alpha), 160, 215, 255)), null,
                    new Point(hx, hy), hr, hr);
            }

            // 眼睑遮罩拆成两块（mac 版 PetFaceAv.cs L436-L452）：
            // ① 困倦自上往下盖：高度 0.52×眼高，圆角 2；
            // ② 开心自下往上顶：高度 0.44×眼高，圆角 w×0.46（弯弯笑眼）。
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
                // 星星眼：双星交替闪烁（mac 版 PetFaceAv.cs L454-L461）
                double tw1 = 0.8 + 0.28 * Math.Sin(now * 9.0);
                double tw2 = 0.8 + 0.28 * Math.Cos(now * 9.0);
                dc.DrawGeometry(RenderKit.BrushOf(Color.FromArgb((byte)(220 * alpha), 20, 20, 26)), null,
                    SparklePath(rx + 1.4 * s, ry - 2.2 * s, 3.4 * s * tw1));
                dc.DrawGeometry(RenderKit.BrushOf(Color.FromArgb((byte)(200 * alpha), 20, 20, 26)), null,
                    SparklePath(rx - 2.2 * s, ry + 3.0 * s, 1.7 * s * tw2));
            }
        }

        // 情境附加：高温汗滴（右眼旁，循环下落）/ 深夜困倦 Zzz（右上飘升）
        // 抽成独立方法：幽灵款走自己的绘制分支，但要复用同一套情境反应
        // （mac 侧同样是 DrawContextEffects，PetFaceAv.cs L493-L552）。
        DrawContextEffects(dc, centers, s, alpha, eyeColor, pink, m);

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
        double mx = faceX + _ex.Value * 3.8 * s, my = faceY + baseH * 0.66 + _ey.Value * 3 * s;
        var mouth = new StreamGeometry();
        using (var ctx = mouth.Open())
        {
            // 青蛙嘴更宽（13*s，其余款式 7*s）；mac 版第 8 款，PetFaceAv.cs L571。
            double mw = (Style == "frog" ? 13 : 7) * s;
            if (Style == "cat" && (m == "default" || m == "happy" || m == "shy" || m == "wink"))
            {
                // 猫咪经典 ω 嘴：两段二次贝塞尔（mac 版 PetFaceAv.cs L583-L588）
                ctx.BeginFigure(new Point(mx - mw * 0.52, my - 0.3 * s), false, false);
                ctx.QuadraticBezierTo(new Point(mx - mw * 0.26, my + 3.2 * s), new Point(mx, my + 0.2 * s), true, false);
                ctx.QuadraticBezierTo(new Point(mx + mw * 0.26, my + 3.2 * s), new Point(mx + mw * 0.52, my - 0.3 * s), true, false);
            }
            else if (Style == "frog" && (m == "default" || m == "shy"))
            {
                // 青蛙：平直横线嘴（mac 版第 8 款，PetFaceAv.cs L590-L594）
                ctx.BeginFigure(new Point(mx - mw / 2, my), false, false);
                ctx.LineTo(new Point(mx + mw / 2, my), true, false);
            }
            else
            {
                // 其余款式（含外星人）：通用二次贝塞尔嘴（大笑更深）
                ctx.BeginFigure(new Point(mx - mw / 2, my), false, false);
                ctx.QuadraticBezierTo(new Point(mx, my + (m == "laugh" ? 7.8 : 4.2) * s),
                                      new Point(mx + mw / 2, my), true, false);
            }
        }
        mouth.Freeze();
        dc.DrawGeometry(null, RenderKit.PenOf(eyeColor, 1.8 * s), mouth);

        // 猫咪胡须（嘴角两侧各两根）；透明度 0.67×alpha（mac 版 PetFaceAv.cs L490）
        if (Style == "cat")
        {
            var wp = RenderKit.PenOf(Color.FromArgb((byte)(170 * alpha), eyeColor.R, eyeColor.G, eyeColor.B), 1.1 * s);
            foreach (int side in TwoSides)
            {
                double wx = faceX + side * (gap / 2 + baseW + 3 * s);
                dc.DrawLine(wp, new Point(wx, my - 2.5 * s), new Point(wx + side * 6 * s, my - 4 * s));
                dc.DrawLine(wp, new Point(wx, my + 0.5 * s), new Point(wx + side * 6 * s, my + 1.5 * s));
            }
        }
    }

    /// <summary>
    /// 情境附加反应（对齐 mac 版 PetFaceAv.cs 的 DrawContextEffects L517-L556）：
    /// 高温流汗（眼旁循环下落的汗滴）/ 深夜困倦 Zzz（右上飘升）/
    /// 晕眩黄色星屑（头顶绕圈）/ 上浮爱心 / 浮空星屑。
    /// 从主绘制流程里抽成独立方法，幽灵款（独立分支）复用同一套反应。
    /// </summary>
    private void DrawContextEffects(DrawingContext dc, List<Point> centers, double s, double alpha, Color eyeColor, Color pink, string m)
    {
        double now = Now;
        if (centers.Count == 0) return;
        Point lp = centers[0];    // 左眼（mono 只有一只，两端取同一个）
        Point rp = centers[^1];   // 右眼
        if (m == "hot")
        {
            double fall = (now % 1.6) / 1.6;
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
        else if (m == "sleepy")
        {
            for (int i = 0; i < 3; i++)
            {
                double zt = ((now / 2.4) + i * 0.33) % 1;
                double zx = rp.X + 12 * s + i * 5.5 * s + Math.Sin(now * 2 + i) * 1.2 * s;
                double zy = rp.Y - 12 * s - zt * 12 * s;
                // 字号随 zt 连续变，FormattedText 没法缓存（每帧仍建 3 个）；字型和画刷不再每帧建
                var zt2 = new FormattedText("z", System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    ZzzFace,
                    (6 + 4 * zt) * s,
                    RenderKit.BrushOf(Color.FromArgb((byte)(200 * alpha * (1 - zt * 0.5)), eyeColor.R, eyeColor.G, eyeColor.B)), 1);
                dc.DrawText(zt2, new Point(zx, zy));
            }
        }
        else if (m == "dizzy")
        {
            // 晕眩：头顶两颗黄色星屑绕圈转（mac 版 PetFaceAv.cs L536-L544）
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
            // 上浮爱心（mac 版 PetFaceAv.cs L545-L551）
            double t1 = (now * 0.9) % 1.0;
            double hx = rp.X + 9 * s + Math.Sin(now * 4) * 1.4 * s;
            double hy = rp.Y - 4 * s - t1 * 11 * s;
            var hc = Color.FromArgb((byte)(190 * alpha * (1 - t1)), pink.R, pink.G, pink.B);
            dc.DrawGeometry(RenderKit.BrushOf(hc), null, HeartPath(hx, hy, (3.6 - 0.8 * t1) * s));
        }
        else if (m == "star")
        {
            // 浮空星屑（mac 版 PetFaceAv.cs L552-L556）
            double tw = 0.6 + 0.4 * Math.Sin(now * 8);
            var sc = Color.FromArgb((byte)(195 * alpha * tw), 255, 235, 145);
            dc.DrawGeometry(RenderKit.BrushOf(sc), null, SparklePath(rp.X + 10 * s, rp.Y - 8 * s, 2.2 * s * tw));
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
        DrawContextEffects(dc, centers, s, alpha, eyeColor, pink, mood);
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
