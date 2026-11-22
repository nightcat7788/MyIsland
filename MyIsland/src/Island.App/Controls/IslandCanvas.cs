using System.Windows;
using System.Windows.Media;
using Island.App.Animation;
using Island.App.Services;
using Island.App.Themes;
using Island.Core.Monitoring;
using Island.Core.Quota;

namespace Island.App.Controls;

/// <summary>
/// 液态岛自绘控件：画外形（胶囊+液桥+水滴）、毛玻璃底、收起态内容（时钟/小形象/信息胶囊/监控）。
/// 所有尺寸/形变由宿主的弹簧驱动，本控件只按当前值渲染，不做布局。
/// 本体位置由宿主以 BodyRect（窗口 DIP 坐标）注入。
///
/// 渲染层参考 BrimDeck（MoFeng2223/BrimDeck）的 MonitorDraw/MonitorStrip 手法：
/// - FormattedText/Brush/Pen 全部按内容缓存并 Freeze，每帧只画不建（文本排版是重绘最贵部分）；
/// - 读数数字按"最宽样本"预留宽度（"100%"/"125°"/"9.9M"），数值跳动时布局不动；
/// - CPU/MEM/GPU 用 270° 开口表盘 + 字母（BrimDeck 的签名式 gauge）；
/// - 走势图用 Catmull-Rom 平滑 + 16% 透明填充；
/// - 读数分级配色：≥70% Warning、≥90% Critical（Up），正常保持文本色。
/// </summary>
public sealed class IslandCanvas : FrameworkElement
{
    public static readonly DependencyProperty PaletteProperty =
        DependencyProperty.Register(nameof(Palette), typeof(Palette), typeof(IslandCanvas),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(IslandCanvas),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CompactViewProperty =
        DependencyProperty.Register(nameof(CompactView), typeof(string), typeof(IslandCanvas),
            new FrameworkPropertyMetadata("pet", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SnapshotProperty =
        DependencyProperty.Register(nameof(Snapshot), typeof(MonitorSnapshot), typeof(IslandCanvas),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Palette? Palette { get => (Palette?)GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public string CompactView { get => (string)GetValue(CompactViewProperty); set => SetValue(CompactViewProperty, value); }
    public MonitorSnapshot? Snapshot { get => (MonitorSnapshot?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }

    // 宿主注入的液态参数
    public Rect BodyRect { get; set; }              // 窗口 DIP 坐标
    public double NeckStrength { get; set; } = 1;
    public double NeckStretch { get; set; }
    public double DripRadius { get; set; }
    public double Squash { get; set; } = 1;
    public string Edge { get; set; } = "top";
    public Point LookPoint { get; set; }
    public bool PointerNear { get; set; }
    public Brush? BackdropBrush { get; set; }      // 毛玻璃纹理（宿主按屏幕区域构造）
    public bool IsIndicator { get; set; }          // 指示条模式（窗口最大化时）：只画呼吸底

    // 天气（收起态 clock 视图右缘）：宿主注入
    public Services.WeatherInfo? Weather { get; set; }

    // 温度（收起态 dash 视图）：宿主注入（最近一次热快照）
    public Services.ThermalSnapshot? Thermal { get; set; }

    // 临时活动（音量指示等）：带过期时间，优先级最高
    public ActivityView? Activity { get; set; }
    public double[]? CpuHistory { get; set; }
    public double[]? NetHistory { get; set; }

    // AI 配额（收起态 combo 信息胶囊的配额环）：宿主每次刷新配额时注入
    public QuotaSnapshot? Quota { get; set; }

    // dash 显示项过滤（null = 全部显示；宿主由 Settings.DashItems 注入）
    public HashSet<string>? DashItems { get; set; }
    private bool Want(string key) => DashItems is null || DashItems.Contains(key);

    // combo 信息胶囊显示项过滤（rings/dials/clock/net）
    public HashSet<string>? ComboItems { get; set; }
    private bool WantCombo(string key) => ComboItems is null || ComboItems.Contains(key);

    private readonly PetFace _pet = new();
    public PetFace Pet => _pet;

    // ---------- 渲染助手：统一转发到 RenderKit（与展开页 MonitorPage 共享实现与缓存） ----------
    private double Ppd => VisualTreeHelper.GetDpi(this).PixelsPerDip;
    private static Color LevelColor(double pct, Color normal, Palette pal) => RenderKit.LevelColor(pct, normal, pal);
    private static SolidColorBrush BrushOf(Color c) => RenderKit.BrushOf(c);
    private static SolidColorBrush Faint(Color c, double opacity) => RenderKit.Faint(c, opacity);
    private static Pen PenOf(Color c, double thickness, double opacity = 1) => RenderKit.PenOf(c, thickness, opacity);
    private static FormattedText Text(string s, double size, Color color, bool strong = false, double ppd = 1)
        => RenderKit.Text(s, size, color, strong, ppd);
    private static FormattedText Trimmed(string s, double size, Color color, double width, bool strong, double ppd)
        => RenderKit.Trimmed(s, size, color, width, strong, ppd);
    private static void Baseline(DrawingContext dc, FormattedText t, double x, double baseline)
        => RenderKit.Baseline(dc, t, x, baseline);

    // ---------- 表盘 / 走势图：转发 RenderKit ----------
    private static void Dial(DrawingContext dc, Point center, double size, double ring,
        double? share, Color track, Color arc, Color letterColor, string letter, double ppd)
        => RenderKit.Dial(dc, center, size, ring, share, track, arc, letterColor, letter, ppd);

    private static void DrawSpark(DrawingContext dc, Rect area, double[]? data, Color color, int n = 36)
        => RenderKit.DrawSpark(dc, area, data, color, n);

    // ---------- 主渲染 ----------
    protected override void OnRender(DrawingContext dc)
    {
        var pal = Palette ?? Palette.Dark;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        // 挤压：高度按 Squash 缩放，保持中心
        var r = BodyRect;
        double squeezed = r.Height * Easing.Clamp(Squash, 0.5, 1.2);
        r = new Rect(r.X, r.Y + (r.Height - squeezed) / 2, r.Width, squeezed);

        double corner = pal.SquareCorners ? Math.Min(3, r.Height / 2) : Math.Min(18, r.Height / 2);
        double fillet = 13;

        // 贴边侧圆角随液桥强度收缩（液桥在场时贴边侧偏方，与液桥平滑衔接）
        double attach = corner * (1 - Easing.Clamp(NeckStrength, 0, 1) * 0.85);
        (double TL, double TR, double BR, double BL) radii = Edge switch
        {
            "top"    => (attach, attach, corner, corner),
            "bottom" => (corner, corner, attach, attach),
            "left"   => (attach, corner, corner, attach),
            "right"  => (corner, attach, attach, corner),
            _ => (corner, corner, corner, corner),
        };

        var geo = new GeometryGroup { FillRule = FillRule.Nonzero };
        geo.Children.Add(LiquidGeometry.RoundedRect(r, radii.TL, radii.TR, radii.BR, radii.BL));

        // 液桥锚定：本体贴合时边缘线就在本体贴边侧的边线上
        (double line, double centerU) = Edge switch
        {
            "top"    => (r.Top, r.X + r.Width / 2),
            "bottom" => (r.Bottom, r.X + r.Width / 2),
            "left"   => (r.Left, r.Y + r.Height / 2),
            "right"  => (r.Right, r.Y + r.Height / 2),
            _ => (0, 0),
        };
        double length = Edge is "top" or "bottom" ? r.Width : r.Height;
        var neck = LiquidGeometry.Neck(Edge, line, centerU, length, 0, NeckStretch, NeckStrength, fillet, corner);
        if (neck is not null) geo.Children.Add(neck);
        var drip = LiquidGeometry.Drip(Edge, line, centerU, DripRadius);
        if (drip is not null) geo.Children.Add(drip);

        // 毛玻璃纹理底 + 主题色压层
        if (BackdropBrush is not null)
            dc.DrawGeometry(BackdropBrush, null, geo);
        dc.DrawGeometry(Faint(pal.Surface, IsExpanded ? 0.94 : 0.86), PenOf(pal.Rim, 1.0, 0.45), geo);

        // 高光边缘（Liquid Glass 式 rim light）：顶部 1px 渐隐白边，深主题下给胶囊玻璃厚度
        if (pal.IsDark && !pal.SquareCorners)
            dc.DrawGeometry(null, RenderKit.RimLightPen, geo);

        if (IsExpanded || IsIndicator) return;   // 展开面板由 XAML 画；指示条只留呼吸底

        if (!IsExpanded)
        {
            // 临时活动优先（音量指示等，带过期时间）
            if (Activity is { } act && DateTime.Now < act.Until)
                DrawActivity(dc, pal, r, act);
            else
                DrawCompact(dc, pal, r);
        }
    }

    private void DrawActivity(DrawingContext dc, Palette pal, Rect r, ActivityView act)
    {
        double ppd = Ppd;
        double cy = r.Y + r.Height / 2;
        // 图标圆（音量=主题色，告警=告警红）
        var tint = act.Kind == "temp" ? pal.Up : pal.Accent;
        string label = act.Kind == "volume" ? (act.Muted ? "静音" : "音量") : act.Label;
        dc.DrawEllipse(Faint(tint, 0.25), null, new Point(r.X + 22, cy), 13, 13);
        var glyph = Text(label, 9, pal.Text, ppd: ppd);
        dc.DrawText(glyph, new Point(r.X + 22 - glyph.Width / 2, cy - glyph.Height / 2));
        // 数值条
        double x = r.X + 44, w = r.Width - 70 - 44;
        var enter = Easing.EaseOut((DateTime.Now - act.Start).TotalSeconds / 0.3);
        double filled = Math.Max(4, w * act.Value * enter);
        dc.DrawRoundedRectangle(BrushOf(pal.SurfaceAlt), null, new Rect(x, cy - 3, w, 6), 3, 3);
        dc.DrawRoundedRectangle(BrushOf(pal.Text), null, new Rect(x, cy - 3, filled, 6), 3, 3);
        // 右侧读数（默认百分比；告警类直接给文本，如 "92°"）
        var pct = Text(act.RightText ?? $"{Math.Round(act.Value * 100)}", 12, pal.Text, true, ppd);
        dc.DrawText(pct, new Point(r.Right - 14 - pct.Width, cy - pct.Height / 2));
    }

    private void DrawCompact(DrawingContext dc, Palette pal, Rect r)
    {
        double cx = r.X + r.Width / 2;
        double cy = r.Y + r.Height / 2;

        switch (CompactView)
        {
            case "pet":
                _pet.SurfaceColor = pal.Surface;   // 眼睑遮罩跟随主题
                _pet.Draw(dc, cx, cy, 1.0, 1, LookPoint, PointerNear);
                break;
            case "clock":
                DrawClock(dc, pal, r, cy, Snapshot);
                break;
            case "monitor":
                DrawMonitor(dc, pal, r, cy, Snapshot, NetHistory);
                break;
            case "combo":
                DrawCombo(dc, pal, r, cy, Snapshot);
                break;
            case "dash":
                DrawDash(dc, pal, r, cy, Snapshot, Thermal, NetHistory);
                break;
            case "devices":
                DrawDevices(dc, pal, r, cy);
                break;
            case "clipboard":
                DrawClipboardHint(dc, pal, r, cy);
                break;
        }
    }

    /// <summary>
    /// monitor 视图（BrimDeck MonitorStrip 式单行读数带）：
    /// 上行 = 核心柱 + CPU/MEM/GPU 表盘（270° 环 + 字母 + 读数，读数按最宽样本预留，跳数不动布局）；
    /// 下行 = 平滑网速曲线 + 上下行速率。GitHub Dark 底。
    /// </summary>
    private void DrawMonitor(DrawingContext dc, Palette pal, Rect r, double cy,
        MonitorSnapshot? snap, double[]? netHistory)
    {
        if (snap is null) return;
        if (r.Width < 40 || r.Height < 20) return;
        double ppd = Ppd;
        dc.DrawRoundedRectangle(Faint(Color.FromRgb(0x0D, 0x11, 0x17), 0.92), null, r, 16, 16);

        var cpuC = Color.FromRgb(0x2E, 0x9B, 0xFF);
        var memC = Color.FromRgb(0x4F, 0xE3, 0xD7);
        var gpuC = Color.FromRgb(0xF2, 0x72, 0xA8);
        var diskC = Color.FromRgb(0x8F, 0xA8, 0xF0);

        // ---- 行1：核心柱 + 表盘 ----
        double y1 = r.Y + r.Height * 0.30;
        double x = r.X + 16;

        var cores = snap.CoreUsages;
        if (cores is not null && cores.Length > 0)
        {
            int n = Math.Min(8, cores.Length);
            double bw = 3.2, gap = 2.8, maxH = 14;
            double baseY = y1 + maxH / 2;
            for (int i = 0; i < n; i++)
            {
                double v = Easing.Clamp(cores[i] / 100.0, 0, 1);
                // 轨道 + 填充（BrimDeck Columns：空载也见轮廓）
                dc.DrawRoundedRectangle(BrushOf(pal.Track), null,
                    new Rect(x + i * (bw + gap), baseY - maxH, bw, maxH + 2), 1.4, 1.4);
                double h = 2 + v * maxH;
                var c = LevelColor(v * 100, v > 0.6 ? cpuC : memC, pal);
                dc.DrawRoundedRectangle(BrushOf(c), null,
                    new Rect(x + i * (bw + gap), baseY - h, bw, h), 1.4, 1.4);
            }
            x += n * (bw + gap) + 14;
        }

        // 表盘 + 读数：数字宽度按 "100%" 预留，读数变化时整行不动（BrimDeck 最宽样本手法）
        const double dialSize = 18, dialFont = 11.5;
        x += DrawDialGauge(dc, pal, x, y1, ppd, snap.CpuUsage, cpuC, "C", dialSize, dialFont) + 14;
        x += DrawDialGauge(dc, pal, x, y1, ppd, snap.MemoryUsagePercent, memC, "M", dialSize, dialFont) + 14;
        if (snap.GpuUsage > 0)
            x += DrawDialGauge(dc, pal, x, y1, ppd, snap.GpuUsage, gpuC, "G", dialSize, dialFont) + 14;
        x += DrawDialGauge(dc, pal, x, y1, ppd, snap.DiskUsagePercent, diskC, "D", dialSize, dialFont) + 14;

        // ---- 行2：平滑网速曲线 + 当前速率 ----
        double y2 = r.Y + r.Height * 0.74;
        var sparkRect = new Rect(r.X + 16, y2 - 7, r.Width - 90, 14);
        DrawSpark(dc, sparkRect, netHistory, pal.Accent, 60);
        var down = Text("↓" + MonitorSnapshot.FormatSpeed(snap.DownloadSpeed), 10,
            Color.FromRgb(0x60, 0xC0, 0x78), ppd: ppd);
        var up = Text("↑" + MonitorSnapshot.FormatSpeed(snap.UploadSpeed), 10,
            Color.FromRgb(0xD8, 0x90, 0x78), ppd: ppd);
        double rx = r.Right - 14;
        dc.DrawText(up, new Point(rx - up.Width, y2 - up.Height - 1));
        dc.DrawText(down, new Point(rx - down.Width, y2 + 1));
    }

    /// <summary>
    /// combo 信息胶囊（参考 BrimDeck 刘海轮播态）：配额环 + 时间 + 系统读数表盘 + 网速。
    /// 小形象视图 10 秒后自动变形为此视图，30 秒后变回（IslandWindow 的 pet 轮换）。
    /// </summary>
    private void DrawCombo(DrawingContext dc, Palette pal, Rect r, double cy, MonitorSnapshot? snap)
    {
        if (r.Width < 40 || r.Height < 20) return;
        double ppd = Ppd;
        dc.DrawRoundedRectangle(Faint(Color.FromRgb(0x0D, 0x11, 0x17), 0.92), null, r, 16, 16);
        double x = r.X + 14;

        // ① 配额环（满环 + 首字母；未连接/无数据的工具不占位）
        if (WantCombo("rings") && Quota is { } q)
        {
            double rx0 = x;
            foreach (var p in q.Providers)
            {
                if (p.Status is not ProviderStatus.Connected) continue;
                var baseColor = QuotaRingColor(p.Id, pal);
                // 环色随级别：≥70% Warning、≥90% Critical（BrimDeck 配额环同款语义）
                var arc = p.UsedPercent is { } up ? LevelColor(up, baseColor, pal) : baseColor;
                Ring(dc, new Point(x + 10, cy), 20, 2.4,
                    p.UsedPercent is { } u ? u / 100 : null,
                    pal.Track, arc, pal.Text,
                    p.DisplayName.Length > 0 ? p.DisplayName[..1] : "?", ppd);
                x += 20 + 7;
            }
            if (x > rx0) x += 6;
        }

        // ② 时间（按样本 "00:00" 预留宽度，分针跳变不推挤右侧读数）
        if (WantCombo("clock"))
        {
            var clock = Text(DateTime.Now.ToString("HH:mm"), 15, pal.Text, true, ppd);
            Baseline(dc, clock, x, Math.Round(cy + 15 * 0.36));
            x += Math.Ceiling(Text("00:00", 15, pal.Text, true, ppd).WidthIncludingTrailingWhitespace) + 16;
        }

        // ③ 系统读数表盘（开口环 + 字母，与配额满环区分）
        if (WantCombo("dials") && snap is not null)
        {
            x += DrawDialGauge(dc, pal, x, cy, ppd, snap.CpuUsage,
                Color.FromRgb(0x2E, 0x9B, 0xFF), "C", 18, 11.5) + 13;
            x += DrawDialGauge(dc, pal, x, cy, ppd, snap.MemoryUsagePercent,
                Color.FromRgb(0x4F, 0xE3, 0xD7), "M", 18, 11.5) + 13;
            if (snap.GpuUsage > 0)
                x += DrawDialGauge(dc, pal, x, cy, ppd, snap.GpuUsage,
                    Color.FromRgb(0xF2, 0x72, 0xA8), "G", 18, 11.5) + 13;
        }

        // ④ 右缘：上下行速率（两行小字）
        if (WantCombo("net") && snap is not null)
        {
            var down = Text("↓" + MonitorSnapshot.FormatSpeed(snap.DownloadSpeed), 10,
                Color.FromRgb(0x60, 0xC0, 0x78), ppd: ppd);
            var up = Text("↑" + MonitorSnapshot.FormatSpeed(snap.UploadSpeed), 10,
                Color.FromRgb(0xD8, 0x90, 0x78), ppd: ppd);
            double rx = r.Right - 14;
            dc.DrawText(up, new Point(rx - up.Width, cy - up.Height - 1));
            dc.DrawText(down, new Point(rx - down.Width, cy + 1));
        }
    }

    /// <summary>读数表盘 + 右侧数字（BrimDeck MonitorStrip.Gauge）：返回占用宽度（数字按最宽样本预留）。</summary>
    private double DrawDialGauge(DrawingContext dc, Palette pal, double x, double yc, double ppd,
        double? usage, Color arc, string letter, double dialSize, double font)
    {
        Dial(dc, new Point(x + dialSize / 2, yc), dialSize, Math.Max(1.4, dialSize * 0.11),
            usage / 100, pal.Track, arc, pal.Text, letter, ppd);
        double at = x + dialSize + 4;
        string figure = usage is { } u ? $"{Math.Round(u)}%" : "—";
        var color = usage is { } u2 ? LevelColor(u2, pal.Text, pal) : pal.TextTertiary;
        Baseline(dc, Text(figure, font, color, true, ppd), at, Math.Round(yc + font * 0.36));
        double slot = Math.Ceiling(Text("100%", font, pal.Text, true, ppd).WidthIncludingTrailingWhitespace);
        return dialSize + 4 + slot;
    }

    /// <summary>配额环：满环（转发 RenderKit.Ring）。</summary>
    private static void Ring(DrawingContext dc, Point center, double size, double thickness,
        double? share, Color track, Color arc, Color letterColor, string letter, double ppd)
        => RenderKit.Ring(dc, center, size, thickness, share, track, arc, letterColor, letter, ppd);

    /// <summary>配额环品牌色（Claude 橙 / Codex 绿 / Cursor 蓝），未知工具用主题强调色。</summary>
    private static Color QuotaRingColor(string id, Palette pal) => id switch
    {
        "claude" => Color.FromRgb(0xD9, 0x77, 0x57),
        "codex" => Color.FromRgb(0x10, 0xA3, 0x7F),
        "cursor" => Color.FromRgb(0x5A, 0x96, 0xFF),
        _ => pal.Accent,
    };

    /// <summary>
    /// dash 视图（用户定稿结构）：6 组等距「彩色图标+白值」。
    /// 每组宽度按最宽样本预留（跳数不挤压邻组）；核心柱带轨道；用量读数按 70/90 分级配色。
    /// </summary>
    private void DrawDash(DrawingContext dc, Palette pal, Rect r, double cy,
        MonitorSnapshot? snap, Services.ThermalSnapshot? thermal, double[]? netHistory)
    {
        if (snap is null) return;
        if (r.Width < 40 || r.Height < 20) return;
        double ppd = Ppd;
        dc.DrawRoundedRectangle(Faint(Color.FromRgb(0x0D, 0x11, 0x17), 0.92), null, r, 16, 16);

        double x = r.X + 14;
        Color TempColor(double? t) => t switch
        {
            null => Color.FromRgb(0x90, 0xA8, 0xF0),
            <= 60 => Color.FromRgb(0x60, 0xC0, 0x78),
            <= 75 => pal.Warning,
            _ => pal.Up,
        };

        // 每组：图标上、数值下；宽度 = max(图标, 数值, 样本)，样本保证跳数时组距不动
        void Item(string glyph, Color color, string value, string sample, Color? valueColor = null)
        {
            var icon = Text(glyph, 10, color, true, ppd);
            dc.DrawText(icon, new Point(x, cy - 13));
            var val = Text(value, 13, valueColor ?? Colors.White, true, ppd);
            dc.DrawText(val, new Point(x, cy + 3));
            double slot = Math.Max(Text(sample, 13, pal.Text, true, ppd).Width, icon.Width);
            x += Math.Max(slot, val.Width) + 26;
        }

        // ① 核心柱（等距 6 组之首，亮蓝；轨道+填充）
        var cores = snap.CoreUsages;
        if (Want("cores") && cores is not null && cores.Length > 0)
        {
            int n = Math.Min(8, cores.Length);
            double bw = 4, gap = 2.6, maxH = 26;
            double baseY = cy + maxH / 2 - 4;
            for (int i = 0; i < n; i++)
            {
                double v = Easing.Clamp(cores[i] / 100.0, 0, 1);
                dc.DrawRoundedRectangle(BrushOf(pal.Track), null,
                    new Rect(x + i * (bw + gap), baseY - maxH, bw, maxH), 1.6, 1.6);
                double h = 2 + v * maxH;
                dc.DrawRoundedRectangle(BrushOf(LevelColor(v * 100, Color.FromRgb(0x60, 0xA8, 0xF0), pal)), null,
                    new Rect(x + i * (bw + gap), baseY - h, bw, h), 1.6, 1.6);
            }
            x += n * (bw + gap) + 26;
        }

        // ② CPU 温度 ③ GPU 温度 ④ 内存 ⑤ GPU% ⑥ 硬盘 ⑦ 网速↓ ⑧ 网速↑
        double? ct = thermal?.CpuTemp, gt = thermal?.GpuTemp;
        if (Want("cputemp")) Item("CPU", TempColor(ct), ct is { } c1 ? $"{Math.Round(c1)}°" : "--", "125°");
        if (Want("gputemp")) Item("GPU", TempColor(gt), gt is { } g1 ? $"{Math.Round(g1)}°" : "--", "125°");
        if (Want("mem"))
            Item("MEM", Color.FromRgb(0xA8, 0xF0, 0xF0), $"{snap.MemoryUsagePercent:F0}%", "100%",
                LevelColor(snap.MemoryUsagePercent, Colors.White, pal));
        if (Want("gpu") && snap.GpuUsage > 0)
            Item("GPU%", Color.FromRgb(0xF0, 0xF0, 0xA8), $"{snap.GpuUsage:F0}%", "100%",
                LevelColor(snap.GpuUsage, Colors.White, pal));
        if (Want("disk"))
            Item("盘", Color.FromRgb(0x8F, 0xA8, 0xF0), $"{snap.DiskUsagePercent:F0}%", "100%",
                LevelColor(snap.DiskUsagePercent, Colors.White, pal));
        // SSD 温度（读不到显示 "--"）
        if (Want("ssd"))
        {
            double? st = thermal?.SsdTemp;
            Item("SSD", TempColor(st), st is { } s1 ? $"{Math.Round(s1)}°" : "--", "125°");
        }
        // 本机电池（DeviceService 已在采；有电池才显示）
        var bat = DeviceService.Snapshot.FirstOrDefault(d => d.Kind == "battery");
        if (Want("battery") && bat is not null)
        {
            var bc = bat.Charging ? Color.FromRgb(0x60, 0xC0, 0x78)
                   : bat.Percent <= 20 ? pal.Up
                   : bat.Percent <= 40 ? pal.Warning
                   : Color.FromRgb(0xA8, 0xF0, 0xA8);
            Item(bat.Charging ? "电+" : "电", bc, $"{bat.Percent}%", "100%",
                bat.Percent <= 20 ? pal.Up : bat.Percent <= 40 ? pal.Warning : Colors.White);
        }
        if (Want("net"))
        {
            Item("↓", Color.FromRgb(0x60, 0xA8, 0xF0), MonitorSnapshot.FormatSpeed(snap.DownloadSpeed).Replace(" ", ""), "9.9MB/s");
            Item("↑", Color.FromRgb(0xD8, 0x90, 0x30), MonitorSnapshot.FormatSpeed(snap.UploadSpeed).Replace(" ", ""), "9.9MB/s");
        }
    }

    /// <summary>设备电量行（GitHub Dark 底，与 dash 统一）：图标 + 电量环/数字。</summary>
    private void DrawDevices(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        if (r.Width < 40 || r.Height < 20) return;
        double ppd = Ppd;
        dc.DrawRoundedRectangle(Faint(Color.FromRgb(0x0D, 0x11, 0x17), 0.92), null, r, 16, 16);

        var devices = DeviceService.Snapshot;
        double x = r.X + 16;
        if (devices.Count == 0)
        {
            var empty = Text("📡 未检测到无线设备电量（蓝牙/HID）", 11, pal.TextDim, ppd: ppd);
            dc.DrawText(empty, new Point(x, cy - empty.Height / 2));
            return;
        }
        var title = Text("设备", 10, pal.TextDim, ppd: ppd);
        dc.DrawText(title, new Point(x, cy - title.Height / 2));
        x += title.Width + 12;

        foreach (var d in devices.Take(5))
        {
            // 电量色：≤20% critical、≤40% warning，其余绿（与读数分级一致）
            var color = d.Percent <= 20 ? pal.Up
                      : d.Percent <= 40 ? pal.Warning
                      : Color.FromRgb(0x60, 0xC0, 0x78);
            // 电量环
            double cr = 10;
            dc.DrawEllipse(null, PenOf(pal.Track, 2.6), new Point(x + cr, cy), cr, cr);
            var arc = ArcGeometry(x + cr, cy, cr - 1.4, d.Percent / 100.0);
            dc.DrawGeometry(null, PenOf(color, 2.6), arc);
            // 图标字
            var glyph = Text(DeviceService.Glyph(d.Kind), 8, Colors.White, true, ppd);
            dc.DrawText(glyph, new Point(x + cr - glyph.Width / 2, cy - glyph.Height / 2));
            x += cr * 2 + 5;
            // 百分比
            var pct = Text($"{d.Percent}%", 12, Colors.White, true, ppd);
            dc.DrawText(pct, new Point(x, cy - pct.Height / 2));
            x += pct.Width + 14;
        }
    }

    /// <summary>剪贴板提示行：最近一条内容预览。</summary>
    private void DrawClipboardHint(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        double ppd = Ppd;
        var last = ClipboardService.LastPreview;
        double x = r.X + 16;
        var icon = Text("⎘", 13, pal.Accent, ppd: ppd);
        dc.DrawText(icon, new Point(x, cy - icon.Height / 2));
        x += icon.Width + 8;
        var t = Text(string.IsNullOrEmpty(last) ? "剪贴板为空" : last, 11.5,
            string.IsNullOrEmpty(last) ? pal.TextDim : pal.Text, ppd: ppd);
        dc.DrawText(t, new Point(x, cy - t.Height / 2));
        int count = ClipboardService.Count;
        if (count > 0)
        {
            var n = Text($"{count} 条", 10, pal.TextDim, ppd: ppd);
            dc.DrawText(n, new Point(r.Right - 14 - n.Width, cy - n.Height / 2));
        }
    }

    /// <summary>电量环弧线（从顶部顺时针，fraction=0..1）。</summary>
    private static StreamGeometry ArcGeometry(double cx, double cy, double radius, double fraction)
    {
        fraction = Easing.Clamp(fraction, 0.02, 1);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            // 起点顶部，顺时针
            double start = -Math.PI / 2;
            double end = start + fraction * 2 * Math.PI;
            var p0 = new Point(cx + radius * Math.Cos(start), cy + radius * Math.Sin(start));
            var p1 = new Point(cx + radius * Math.Cos(end), cy + radius * Math.Sin(end));
            ctx.BeginFigure(p0, false, false);
            bool large = fraction > 0.5;
            ctx.ArcTo(p1, new Size(radius, radius), 0, large, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        return geo;
    }

    /// <summary>时钟视图：左时间，中天气（已接线），右上下行网速。时间字号固定，宽度稳定。</summary>
    private void DrawClock(DrawingContext dc, Palette pal, Rect r, double cy, MonitorSnapshot? snap)
    {
        double ppd = Ppd;
        var t = Text(DateTime.Now.ToString("HH:mm"), 15, pal.Text, true, ppd);
        dc.DrawText(t, new Point(r.X + 16, cy - t.Height / 2));

        double rx = r.Right - 14;
        // 右缘：实时网速（上/下两行小字）
        if (snap is not null)
        {
            var down = Text("↓" + MonitorSnapshot.FormatSpeed(snap.DownloadSpeed), 10, pal.TextDim, ppd: ppd);
            var up = Text("↑" + MonitorSnapshot.FormatSpeed(snap.UploadSpeed), 10, pal.TextDim, ppd: ppd);
            dc.DrawText(up, new Point(rx - up.Width, cy - up.Height - 1));
            dc.DrawText(down, new Point(rx - down.Width, cy + 1));
            rx -= Math.Max(up.Width, down.Width) + 12;
        }
        // 天气（BrimDeck compact-clock：时钟旁给环境信息）；无数据时不占位
        if (Weather is { } w)
        {
            var temp = Text($"{Math.Round(w.Temp)}°", 12, pal.Text, true, ppd);
            var emoji = Text(w.Emoji, 12, pal.Text, ppd: ppd);
            double wx = rx - emoji.Width - 3 - temp.Width;
            if (wx > r.X + 16 + t.Width + 8)   // 空间够才画，避免压到时间
            {
                dc.DrawText(emoji, new Point(wx, cy - emoji.Height / 2));
                dc.DrawText(temp, new Point(wx + emoji.Width + 3, cy - temp.Height / 2));
            }
        }
    }

}

/// <summary>收起态临时活动（音量指示 / 高温告警等，参考 BrimDeck compact-alert）。</summary>
public sealed record ActivityView
{
    public string Kind { get; init; } = "volume";     // volume / temp
    public string Label { get; set; } = "音量";        // 图标圆内的字
    public string? RightText { get; set; }             // 右侧读数（null=按 Value 显示百分比）
    public double Value { get; set; }
    public bool Muted { get; set; }
    public DateTime Start { get; set; } = DateTime.Now;
    public DateTime Until { get; set; } = DateTime.Now.AddSeconds(1.8);
}
