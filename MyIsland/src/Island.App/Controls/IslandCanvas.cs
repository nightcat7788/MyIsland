using System.Windows;
using System.Windows.Media;
using Island.App.Animation;
using Island.App.Services;
using Island.App.Themes;
using Island.Core.Monitoring;

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

    /// <summary>前台窗口（进程名 + 标题，宿主每秒注入；活动窗口视图消费）。</summary>
    public (string Proc, string Title)? ActiveWindow { get; set; }

    /// <summary>麦克风使用中（宿主每秒注入）：收起态右上角画隐私绿点（L4 隐私点同款）。</summary>
    public bool MicActive { get; set; }

    // 临时活动（音量指示等）：带过期时间，优先级最高
    public ActivityView? Activity { get; set; }
    public double[]? CpuHistory { get; set; }
    public double[]? NetHistory { get; set; }

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

        // 胶囊形态：圆角 = 高度一半（体育场形），方角主题下保持小圆角
        double corner = pal.SquareCorners ? Math.Min(3, r.Height / 2) : r.Height / 2;
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

        // ---------- 液态毛玻璃（Liquid Glass）分层渲染 ----------
        // ① 毛玻璃纹理底（壁纸模糊采样）
        if (BackdropBrush is not null)
            dc.DrawGeometry(BackdropBrush, null, geo);
        // ② 主题色压层：降到 0.58（收起）让模糊底透出来；展开 0.9 保可读性
        dc.DrawGeometry(Faint(pal.Surface, IsExpanded ? 0.90 : 0.58), PenOf(pal.Rim, 1.0, 0.5), geo);
        // ③ 顶部 sheen 高光带：白 12% → 45% 高度处消隐（液态玻璃的顶部反光）
        if (!pal.SquareCorners && !IsIndicator && r.Width > 24 && r.Height > 16)
        {
            var sheen = new LinearGradientBrush(
                Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF), Colors.Transparent, 90);
            sheen.Freeze();
            var sheenGeo = LiquidGeometry.RoundedRect(
                new Rect(r.X + 1.5, r.Y + 1, Math.Max(4, r.Width - 3), Math.Max(2, r.Height * 0.45)),
                corner, corner, 0, 0);
            dc.DrawGeometry(sheen, null, sheenGeo);
        }
        // ④ 底部内阴影（玻璃厚度感：底部 18% 处压一条暗带）
        if (!pal.SquareCorners && !IsIndicator && r.Width > 24 && r.Height > 16)
        {
            var shade = new LinearGradientBrush(Colors.Transparent,
                Color.FromArgb(0x2E, 0x00, 0x00, 0x00), 90);
            shade.Freeze();
            var shadeGeo = LiquidGeometry.RoundedRect(
                new Rect(r.X + 1.5, r.Y + r.Height * 0.62, Math.Max(4, r.Width - 3), Math.Max(2, r.Height * 0.36)),
                0, 0, corner, corner);
            dc.DrawGeometry(shade, null, shadeGeo);
        }
        // ⑤ 高光边缘（rim light）：顶部 1px 渐隐白边，深主题下给胶囊玻璃厚度
        if (pal.IsDark && !pal.SquareCorners)
            dc.DrawGeometry(null, RenderKit.RimLightPen, geo);

        if (IsExpanded || IsIndicator) return;   // 展开面板由 XAML 画；指示条只留呼吸底

        // 隐私点（iPhone/L4 同款）：麦克风使用中，胶囊右上角常绿点
        if (MicActive)
        {
            double dotX = r.Right - r.Height * 0.42, dotY = r.Y + r.Height * 0.32;
            dc.DrawEllipse(BrushOf(Color.FromRgb(0x3F, 0xC1, 0x5E)), null,
                new Point(dotX, dotY), 3.4, 3.4);
            dc.DrawEllipse(null, PenOf(Color.FromRgb(0x3F, 0xC1, 0x5E), 1.2, 0.45),
                new Point(dotX, dotY), 5.2, 5.2);
        }

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

    /// <summary>收起态内容实测宽度（DIP）：各视图渲染时回报，宿主据此自动调节岛体大小。</summary>
    public double CompactContentWidth { get; private set; }
    /// <summary>是否已完成首次内容测量（未测量前宿主流固定设计宽度）。</summary>
    public bool HasMeasuredCompact { get; private set; }

    private void DrawCompact(DrawingContext dc, Palette pal, Rect r)
    {
        double cx = r.X + r.Width / 2;
        double cy = r.Y + r.Height / 2;

        double w;
        switch (CompactView)
        {
            case "pet":
                _pet.SurfaceColor = pal.Surface;   // 眼睑遮罩跟随主题
                _pet.Draw(dc, cx, cy, 1.0, 1, LookPoint, PointerNear);
                w = 110;
                break;
            case "clock":
                w = DrawClock(dc, pal, r, cy, Snapshot);
                break;
            case "monitor":
                w = DrawMonitor(dc, pal, r, cy, Snapshot, NetHistory);
                break;
            case "combo":
                w = DrawCombo(dc, pal, r, cy, Snapshot);
                break;
            case "dash":
                w = DrawDash(dc, pal, r, cy, Snapshot, Thermal, NetHistory);
                break;
            case "devices":
                w = DrawDevices(dc, pal, r, cy);
                break;
            case "clipboard":
                w = DrawClipboardHint(dc, pal, r, cy);
                break;
            case "window":
                w = DrawActiveWindow(dc, pal, r, cy);
                break;
            default:
                w = 110;
                break;
        }
        if (w > 40)
        {
            CompactContentWidth = w;
            HasMeasuredCompact = true;
        }
    }

    /// <summary>
    /// monitor 视图（BrimDeck compact-monitor 同款单排高密度读数带）：
    /// 细核心柱群（≤16 核）+ C/M/G/D 表盘读数 + 紧凑网速（↓3.40M ↑420K）。GitHub Dark 底。
    /// </summary>
    private double DrawMonitor(DrawingContext dc, Palette pal, Rect r, double cy,
        MonitorSnapshot? snap, double[]? netHistory)
    {
        if (snap is null) return 0;
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        dc.DrawRoundedRectangle(Faint(Color.FromRgb(0x0D, 0x11, 0x17), 0.66), null, r, r.Height / 2, r.Height / 2);

        var cpuC = Color.FromRgb(0x2E, 0x9B, 0xFF);
        var memC = Color.FromRgb(0x4F, 0xE3, 0xD7);
        var gpuC = Color.FromRgb(0xF2, 0x72, 0xA8);
        var diskC = Color.FromRgb(0x8F, 0xA8, 0xF0);

        double x = r.X + 14;

        // ---- 细核心柱群（BrimDeck 同款：2.6px 细柱、2px 间距、最多 16 核，垂直居中） ----
        var cores = snap.CoreUsages;
        if (cores is not null && cores.Length > 0)
        {
            int n = Math.Min(16, cores.Length);
            double bw = 2.6, gap = 2, maxH = Math.Min(24, r.Height - 18);
            for (int i = 0; i < n; i++)
            {
                double v = Easing.Clamp(cores[i] / 100.0, 0, 1);
                dc.DrawRoundedRectangle(BrushOf(pal.Track), null,
                    new Rect(x + i * (bw + gap), cy - maxH / 2, bw, maxH), 1.2, 1.2);
                double h = 1.6 + v * (maxH - 1.6);
                var c = LevelColor(v * 100, cpuC, pal);
                dc.DrawRoundedRectangle(BrushOf(c), null,
                    new Rect(x + i * (bw + gap), cy + maxH / 2 - h, bw, h), 1.2, 1.2);
            }
            x += n * (bw + gap) + 14;
        }

        // ---- 表盘读数（数字按 "100%" 预留，跳数不动布局） ----
        const double dialSize = 18, dialFont = 11.5;
        x += DrawDialGauge(dc, pal, x, cy, ppd, snap.CpuUsage, cpuC, "C", dialSize, dialFont) + 13;
        x += DrawDialGauge(dc, pal, x, cy, ppd, snap.MemoryUsagePercent, memC, "M", dialSize, dialFont) + 13;
        if (snap.GpuUsage > 0)
            x += DrawDialGauge(dc, pal, x, cy, ppd, snap.GpuUsage, gpuC, "G", dialSize, dialFont) + 13;
        x += DrawDialGauge(dc, pal, x, cy, ppd, snap.DiskUsagePercent, diskC, "D", dialSize, dialFont) + 13;

        // ---- 紧凑网速（↓3.40M ↑420K，彩色箭头 + 白字，BrimDeck 同款密度） ----
        var down = Text(MonitorSnapshot.CompactSpeed(snap.DownloadSpeed), 11, pal.Text, true, ppd);
        var up = Text(MonitorSnapshot.CompactSpeed(snap.UploadSpeed), 11, pal.Text, true, ppd);
        var di = Text("↓", 11, Color.FromRgb(0x60, 0xC0, 0x78), true, ppd);
        var ui = Text("↑", 11, Color.FromRgb(0xD8, 0x90, 0x78), true, ppd);
        double bl = Math.Round(cy + 11 * 0.38);
        Baseline(dc, di, x, bl); x += di.Width + 3;
        Baseline(dc, down, x, bl); x += down.WidthIncludingTrailingWhitespace + 10;
        Baseline(dc, ui, x, bl); x += ui.Width + 3;
        Baseline(dc, up, x, bl); x += up.WidthIncludingTrailingWhitespace;

        return x - r.X + 14;
    }

    /// <summary>
    /// combo 信息胶囊（参考 BrimDeck 刘海轮播态）：时间 + 系统读数表盘 + 网速。
    /// 小形象视图 10 秒后自动变形为此视图，30 秒后变回（IslandWindow 的 pet 轮换）。
    /// </summary>
    private double DrawCombo(DrawingContext dc, Palette pal, Rect r, double cy, MonitorSnapshot? snap)
    {
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        dc.DrawRoundedRectangle(Faint(Color.FromRgb(0x0D, 0x11, 0x17), 0.66), null, r, r.Height / 2, r.Height / 2);
        double x = r.X + 14;

        // ① 时间（按样本 "00:00" 预留宽度，分针跳变不推挤右侧读数）
        if (WantCombo("clock"))
        {
            var clock = Text(DateTime.Now.ToString("HH:mm"), 15, pal.Text, true, ppd);
            Baseline(dc, clock, x, Math.Round(cy + 15 * 0.36));
            x += Math.Ceiling(Text("00:00", 15, pal.Text, true, ppd).WidthIncludingTrailingWhitespace) + 16;
        }

        // ② 系统读数表盘（开口环 + 字母）
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

        // ③ 右缘：上下行速率（两行小字）
        double netBlock = 0;
        if (WantCombo("net") && snap is not null)
        {
            var down = Text("↓" + MonitorSnapshot.FormatSpeed(snap.DownloadSpeed), 10,
                Color.FromRgb(0x60, 0xC0, 0x78), ppd: ppd);
            var up = Text("↑" + MonitorSnapshot.FormatSpeed(snap.UploadSpeed), 10,
                Color.FromRgb(0xD8, 0x90, 0x78), ppd: ppd);
            double rx = r.Right - 14;
            dc.DrawText(up, new Point(rx - up.Width, cy - up.Height - 1));
            dc.DrawText(down, new Point(rx - down.Width, cy + 1));
            netBlock = Math.Max(up.Width, down.Width) + 14;
        }

        // 内容实测宽 = 左侧流（时间+表盘）+ 右侧网速块（无则只留右 padding）
        return x - r.X + (netBlock > 0 ? 12 + netBlock : 14);
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

    /// <summary>
    /// dash 视图（用户定稿结构）：6 组等距「彩色图标+白值」。
    /// 每组宽度按最宽样本预留（跳数不挤压邻组）；核心柱带轨道；用量读数按 70/90 分级配色。
    /// </summary>
    private double DrawDash(DrawingContext dc, Palette pal, Rect r, double cy,
        MonitorSnapshot? snap, Services.ThermalSnapshot? thermal, double[]? netHistory)
    {
        if (snap is null) return 0;
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        dc.DrawRoundedRectangle(Faint(Color.FromRgb(0x0D, 0x11, 0x17), 0.66), null, r, r.Height / 2, r.Height / 2);

        double x = r.X + 14;
        // 温度分级：≤70° 绿 / 71–85° 橙 / >85° 红（与监控页一致）
        Color TempColor(double? t) => t switch
        {
            null => Color.FromRgb(0x90, 0xA8, 0xF0),
            <= 70 => Color.FromRgb(0x60, 0xC0, 0x78),
            <= 85 => pal.Warning,
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
        // GPU% 常显（核显空闲=0 也占位，避免岛体宽度随负载跳动）
        if (Want("gpu"))
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
        // 内容实测宽 = 已排内容（含末尾组距 26，扣掉）+ 右留白 14
        return x - r.X - 26 + 14;
    }

    /// <summary>设备电量行（BrimDeck compact-devices 同款：文件数 + 设备图标 + 大号分级配色百分比）。</summary>
    private double DrawDevices(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        dc.DrawRoundedRectangle(Faint(Color.FromRgb(0x0D, 0x11, 0x17), 0.66), null, r, r.Height / 2, r.Height / 2);

        double x = r.X + 16;

        // 文件中转计数（有文件才显示，BrimDeck 同款 📁 N）
        int files = FileTrayService.Count;
        if (files > 0)
        {
            var fi = Text("文", 11, pal.Accent, true, ppd);
            dc.DrawText(fi, new Point(x, cy - fi.Height / 2));
            x += fi.Width + 4;
            var fc = Text($"{files}", 13, pal.Text, true, ppd);
            dc.DrawText(fc, new Point(x, cy - fc.Height / 2));
            x += fc.Width + 20;
        }

        var devices = DeviceService.Snapshot;
        if (devices.Count == 0 && files == 0)
        {
            var empty = Text("📡 未检测到无线设备电量（蓝牙/HID）", 11, pal.TextDim, ppd: ppd);
            dc.DrawText(empty, new Point(x, cy - empty.Height / 2));
            return empty.Width + 32 + (files > 0 ? x - r.X : 0);
        }

        foreach (var d in devices.Take(5))
        {
            // 分级配色：充电绿 / ≤20 红 / ≤40 橙 / 其余白
            var color = d.Charging ? Color.FromRgb(0x3F, 0xC1, 0x5E)
                      : d.Percent <= 20 ? pal.Up
                      : d.Percent <= 40 ? pal.Warning
                      : pal.Text;
            // 设备图标（dim）+ 大号百分比（分级色，粗体）
            var glyph = Text(DeviceService.Glyph(d.Kind), 11, pal.TextDim, true, ppd);
            dc.DrawText(glyph, new Point(x, cy - glyph.Height / 2));
            x += glyph.Width + 5;
            var pct = Text($"{d.Percent}%", 13, color, true, ppd);
            dc.DrawText(pct, new Point(x, cy - pct.Height / 2));
            x += pct.Width;
            // 充电小闪电
            if (d.Charging)
            {
                var bolt = Text("⚡", 9, Color.FromRgb(0x3F, 0xC1, 0x5E), ppd: ppd);
                dc.DrawText(bolt, new Point(x + 1, cy - pct.Height / 2 - 2));
                x += bolt.Width + 2;
            }
            x += 18;
        }
        // 内容实测宽 = 已排内容（扣末尾组距 18）+ 右留白 16
        return x - r.X - 18 + 16;
    }

    /// <summary>剪贴板提示行：最近一条内容预览。</summary>
    private double DrawClipboardHint(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        double ppd = Ppd;
        var last = ClipboardService.LastPreview;
        double x = r.X + 16;
        var icon = Text("⎘", 13, pal.Accent, ppd: ppd);
        dc.DrawText(icon, new Point(x, cy - icon.Height / 2));
        x += icon.Width + 8;
        // 预览超长时截断（内容宽度上限 260，避免剪贴板长文本撑爆岛体）
        string preview = string.IsNullOrEmpty(last) ? "剪贴板为空" : last;
        var previewColor = string.IsNullOrEmpty(last) ? pal.TextDim : pal.Text;
        var t = Text(preview, 11.5, previewColor, ppd: ppd);
        const double maxPreview = 260;
        if (t.Width > maxPreview)
            t = RenderKit.Trimmed(preview, 11.5, previewColor, maxPreview, false, ppd);
        dc.DrawText(t, new Point(x, cy - t.Height / 2));
        x += t.Width;
        double countBlock = 0;
        int count = ClipboardService.Count;
        if (count > 0)
        {
            var n = Text($"{count} 条", 10, pal.TextDim, ppd: ppd);
            dc.DrawText(n, new Point(r.Right - 14 - n.Width, cy - n.Height / 2));
            countBlock = n.Width + 14;
        }
        return x - r.X + (countBlock > 0 ? 12 + countBlock : 14);
    }

    /// <summary>活动窗口视图（YASB ActiveWindow 同款）：主题色圆点 + 进程名 + 窗口标题，超长截断。</summary>
    private double DrawActiveWindow(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        dc.DrawRoundedRectangle(Faint(Color.FromRgb(0x0D, 0x11, 0x17), 0.66), null, r, r.Height / 2, r.Height / 2);

        double x = r.X + 16;
        // 呼吸圆点（主题色，标识"正在跟随前台窗口"）
        dc.DrawEllipse(BrushOf(pal.Accent), null, new Point(x + 4, cy), 4, 4);
        x += 16;

        var (proc, title) = ActiveWindow ?? ("", "");
        string procText = string.IsNullOrEmpty(proc) ? "桌面" : proc;
        var p = Text(procText, 11.5, pal.Accent, true, ppd);
        dc.DrawText(p, new Point(x, cy - p.Height / 2));
        x += p.Width + 10;

        const double maxTitle = 300;
        string tt = string.IsNullOrEmpty(title) ? "" : title;
        var t = Text(string.IsNullOrEmpty(tt) ? "" : tt, 11, pal.TextDim, ppd: ppd);
        if (t.Width > maxTitle)
            t = RenderKit.Trimmed(tt, 11, pal.TextDim, maxTitle, false, ppd);
        if (!string.IsNullOrEmpty(tt))
            dc.DrawText(t, new Point(x, cy - t.Height / 2));
        return x - r.X + (string.IsNullOrEmpty(tt) ? 0 : t.Width) + 16;
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
    private double DrawClock(DrawingContext dc, Palette pal, Rect r, double cy, MonitorSnapshot? snap)
    {
        double ppd = Ppd;
        var t = Text(DateTime.Now.ToString("HH:mm"), 15, pal.Text, true, ppd);
        dc.DrawText(t, new Point(r.X + 16, cy - t.Height / 2));

        double netBlock = 0;
        double rx = r.Right - 14;
        // 右缘：实时网速（上/下两行小字）
        if (snap is not null)
        {
            var down = Text("↓" + MonitorSnapshot.FormatSpeed(snap.DownloadSpeed), 10, pal.TextDim, ppd: ppd);
            var up = Text("↑" + MonitorSnapshot.FormatSpeed(snap.UploadSpeed), 10, pal.TextDim, ppd: ppd);
            dc.DrawText(up, new Point(rx - up.Width, cy - up.Height - 1));
            dc.DrawText(down, new Point(rx - down.Width, cy + 1));
            netBlock = Math.Max(up.Width, down.Width) + 14;
            rx -= Math.Max(up.Width, down.Width) + 12;
        }
        // 天气（BrimDeck compact-clock：时钟旁给环境信息）；无数据时不占位
        double weatherBlock = 0;
        if (Weather is { } w)
        {
            var temp = Text($"{Math.Round(w.Temp)}°", 12, pal.Text, true, ppd);
            var emoji = Text(w.Emoji, 12, pal.Text, ppd: ppd);
            double wx = rx - emoji.Width - 3 - temp.Width;
            // 天气块始终计入内容宽度（否则缩放动画中途会暂时不画 → 宽度振荡）
            weatherBlock = emoji.Width + 3 + temp.Width + 12;
            if (wx > r.X + 16 + t.Width + 8)   // 空间够才画，避免压到时间
            {
                dc.DrawText(emoji, new Point(wx, cy - emoji.Height / 2));
                dc.DrawText(temp, new Point(wx + emoji.Width + 3, cy - temp.Height / 2));
            }
        }
        // 内容实测宽 = 时间 + 天气 + 网速块（按出现与否累加）
        return 16 + t.Width + 12 + weatherBlock + netBlock;
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
