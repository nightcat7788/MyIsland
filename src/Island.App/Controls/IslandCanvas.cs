using System.Windows;
using System.Windows.Media;
using Island.App.Animation;
using Island.App.Services;
using Island.App.Themes;
using Island.Core.Monitoring;

namespace Island.App.Controls;

/// <summary>
/// 灵动岛自绘控件（与 macOS 版 Island.Avalonia 视觉对齐）：
/// - 外壳：默认启用 Vibe Island 式「刘海融合造型」（不透明底 + 顶部内凹圆弧 + 凹口描边 + 双耳微高光），
///   关闭时退回不透明纯圆角药丸胶囊；
/// - 时间胶囊（clock）：大号时间 + 基线对齐秒数 │ 日期·星期 │ 矢量天气图标 + 温度 + 城市；
/// - 信息胶囊（combo）：时间 + C/M/G/D 360° 标识环与数字主角百分比 │ 温度组 │ 单行上下行网速；
/// - 仪表盘（dash）：CPU 历史蓝青频谱柱 │ 剪贴板条数 │ 温度组 │ 占用环组 │ 电池 │ 设备电量环 │ 网速；
/// - 同时保留 Windows 端扩展收起视图（pet / monitor / devices / clipboard / window）与音量/高温活动浮条。
/// </summary>
public sealed class IslandCanvas : FrameworkElement
{
    // ============ 收起态统一四级字号阶梯（与 macOS 版一致） ============
    private const double FontHero = 22;      // 主数值：时间胶囊的大号时间
    private const double FontValue = 13;     // 数值：百分比 / 温度 / 网速 / 日期
    private const double FontBody = 11.5;    // 正文：秒数 / 副标题
    private const double FontLabel = 10;     // 标签：城市名 / 缩写

    private const double ClockPadX = 18;
    private const double ClockColGap = 14;
    private const double ClockCityMaxW = 132;

    private const double ComboPadX = 16;
    private const double CompactIconBox = 12 * 0.9;
    private const double CompactIconGap = 4;
    private const double CompactTempGap = 10;
    private const double TempLabelGap = 3;
    private const double TempValueMinW = 22;
    private const double CompactGroupGap = 11;

    private const double DashPadX = 14;
    private const double DashRing = 16;
    private const double DashItemGap = 14;

    public static readonly DependencyProperty PaletteProperty =
        DependencyProperty.Register(nameof(Palette), typeof(Palette), typeof(IslandCanvas),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(IslandCanvas),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CompactViewProperty =
        DependencyProperty.Register(nameof(CompactView), typeof(string), typeof(IslandCanvas),
            new FrameworkPropertyMetadata("combo", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SnapshotProperty =
        DependencyProperty.Register(nameof(Snapshot), typeof(MonitorSnapshot), typeof(IslandCanvas),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Palette? Palette { get => (Palette?)GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public string CompactView { get => (string)GetValue(CompactViewProperty); set => SetValue(CompactViewProperty, value); }
    public MonitorSnapshot? Snapshot { get => (MonitorSnapshot?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }

    // 宿主注入的形态与装饰参数
    public Rect BodyRect { get; set; }              // 窗口 DIP 坐标
    public double NeckStrength { get; set; } = 1;
    public double NeckStretch { get; set; }
    public double DripRadius { get; set; }
    public double Squash { get; set; } = 1;
    public string Edge { get; set; } = "top";
    public Point LookPoint { get; set; }
    public bool PointerNear { get; set; }
    public Brush? BackdropBrush { get; set; }
    public bool IsIndicator { get; set; }

    /// <summary>收起态贴顶刘海造型（默认 false = 与 macOS 时间胶囊一致的悬浮纯圆角胶囊；true = 贴顶肩角刘海）。</summary>
    public bool DecorNotch { get; set; } = false;
    /// <summary>环内显示 C/M/G/D 字母标识（关=12×12 矢量图标）。</summary>
    public bool DecorLetters { get; set; } = true;
    /// <summary>信息胶囊数字主角样式（true=17px 粗体数字主角；false=经典同级版）。</summary>
    public bool ComboNumericHero { get; set; } = true;

    /// <summary>CPU 历史序列（0..1，供 dash 频谱柱渲染）。</summary>
    public double[]? CpuHistory { get; set; }
    public int CpuHistoryCount { get; set; }

    /// <summary>当前系统媒体会话快照与实时音频律动频谱（供 media 胶囊渲染）。</summary>
    public MediaSnapshot? Media { get; set; }
    public double[]? AudioBars { get; set; }

    public WeatherInfo? Weather { get; set; }
    public ThermalSnapshot? Thermal { get; set; }
    public (string Proc, string Title)? ActiveWindow { get; set; }
    public bool MicActive { get; set; }
    public ActivityView? Activity { get; set; }

    public HashSet<string>? DashItems { get; set; }
    private bool Want(string key) => DashItems is null || DashItems.Contains(key);

    public HashSet<string>? ComboItems { get; set; }
    private bool WantCombo(string key) => ComboItems is null || ComboItems.Contains(key);

    private readonly PetFace _pet = new();
    public PetFace Pet => _pet;

    private double Ppd
    {
        get
        {
            try
            {
                double v = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                return double.IsFinite(v) && v > 0 ? v : 1.0;
            }
            catch { return 1.0; }
        }
    }

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

    // ---- 资源色与浅色主题 WCAG 对比度校正（与 macOS 版一致） ----
    private static readonly Color CpuC = Color.FromRgb(0x2E, 0x9B, 0xFF);
    private static readonly Color MemC = Color.FromRgb(0x4F, 0xE3, 0xD7);
    private static readonly Color GpuC = Color.FromRgb(0xF2, 0x72, 0xA8);
    private static readonly Color DiskC = Color.FromRgb(0x5F, 0xD3, 0xA0);
    private static readonly Color BattC = Color.FromRgb(0x3F, 0xB8, 0xFF);
    private static readonly Color NetC = Color.FromRgb(0x6F, 0xB8, 0xFF);
    private static readonly Color DownC = Color.FromRgb(0x60, 0xC0, 0x78);
    private static readonly Color UpC = Color.FromRgb(0xD8, 0x90, 0x78);

    private enum Res { Cpu, Mem, Gpu, Disk, Batt, Net }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Color, Palette), Color> _readableCache = new();

    private static Color ResColor(Res r, Palette pal) => r switch
    {
        Res.Cpu => ReadableCached(CpuC, pal),
        Res.Mem => ReadableCached(MemC, pal),
        Res.Gpu => ReadableCached(GpuC, pal),
        Res.Disk => ReadableCached(DiskC, pal),
        Res.Batt => ReadableCached(BattC, pal),
        _ => ReadableCached(NetC, pal),
    };

    private static Color ReadableCached(Color c, Palette pal)
        => _readableCache.GetOrAdd((c, pal), static k => Readable(k.Item1, k.Item2));

    private static Color Readable(Color c, Palette pal, double min = 3.0)
    {
        if (pal.IsDark) return c;
        double bg = RelativeLuminance(pal.Surface);
        if (ContrastRatio(RelativeLuminance(c), bg) >= min) return c;
        var hsv = ColorToHsv(c);
        for (int i = 1; i <= 12; i++)
        {
            double v = hsv.v * (1 - i * 0.07);
            double s = Math.Min(1, hsv.s + i * 0.03);
            var cand = ColorFromHsv(hsv.h, s, Math.Max(0.08, v));
            if (ContrastRatio(RelativeLuminance(cand), bg) >= min) return cand;
        }
        return ColorFromHsv(hsv.h, Math.Min(1, hsv.s + 0.3), 0.15);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Lin(byte b) { double x = b / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static double ContrastRatio(double l1, double l2)
    {
        double a = Math.Max(l1, l2), b = Math.Min(l1, l2);
        return (a + 0.05) / (b + 0.05);
    }

    private static (double h, double s, double v) ColorToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60; if (h < 0) h += 360;
        }
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static Color ColorFromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb(
            (byte)Math.Clamp(Math.Round((r + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((g + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((b + m) * 255), 0, 255));
    }

    private static Color Mute(Color c, Color toward, double towardShare)
        => Color.FromRgb(
            (byte)Math.Clamp(Math.Round(c.R + (toward.R - c.R) * towardShare), 0, 255),
            (byte)Math.Clamp(Math.Round(c.G + (toward.G - c.G) * towardShare), 0, 255),
            (byte)Math.Clamp(Math.Round(c.B + (toward.B - c.B) * towardShare), 0, 255));

    // ---------- 主渲染 ----------
    protected override void OnRender(DrawingContext dc)
    {
        var pal = Palette ?? Palette.Dark;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (BodyRect.Width <= 0 || BodyRect.Height <= 0) return;

        var r = BodyRect;
        double squeezed = r.Height * Easing.Clamp(Squash, 0.5, 1.2);
        r = RenderKit.SafeRect(r.X, r.Y + (r.Height - squeezed) / 2, r.Width, squeezed);

        // 展开态 / 指示条模式（对齐 macOS DrawExpanded：独立悬浮圆角面板，不带刘海耳翼）
        if (IsExpanded)
        {
            double expCorner = pal.SquareCorners ? 3 : 20;
            dc.DrawRoundedRectangle(Faint(Colors.Black, 0.24), null,
                RenderKit.SafeRect(r.X, r.Y + 2, r.Width, r.Height), expCorner + 1, expCorner + 1);
            dc.DrawRoundedRectangle(BrushOf(pal.Surface), PenOf(pal.Rim, 1.0, 0.50), r, expCorner, expCorner);
            return;
        }
        if (IsIndicator)
        {
            double indCorner = pal.SquareCorners ? 2 : r.Height / 2;
            dc.DrawRoundedRectangle(Faint(pal.Surface, 0.92), PenOf(pal.Rim, 1.0, 0.5), r, indCorner, indCorner);
            return;
        }

        double cy = r.Y + r.Height / 2;
        bool notchDrawn = DecorNotch && Edge == "top" && !pal.SquareCorners
            && DrawTopShoulderNotch(dc, pal, r, earR: Math.Min(11, r.Height * 0.28), botR: Math.Min(18, r.Height * 0.44));
        if (!notchDrawn)
        {
            double corner = pal.SquareCorners ? Math.Min(3, r.Height / 2) : r.Height / 2;
            // 柔和底影 + 不透明纯圆角药丸胶囊（1:1 复刻 macOS 时间胶囊立体悬浮质感）
            if (!pal.SquareCorners)
            {
                dc.DrawRoundedRectangle(Faint(Colors.Black, 0.26), null,
                    RenderKit.SafeRect(r.X, r.Y + 1.5, r.Width, r.Height), corner, corner);
            }
            dc.DrawRoundedRectangle(BrushOf(pal.Surface), PenOf(pal.Rim, 1.0, 0.50), r, corner, corner);
            if (!pal.SquareCorners && r.Width > 24 && r.Height > 16)
            {
                dc.DrawRoundedRectangle(Faint(Colors.White, 0.038), null,
                    RenderKit.SafeRect(r.X + 1, r.Y + 1, Math.Max(4, r.Width - 2), Math.Max(2, r.Height * 0.34)),
                    Math.Max(1, corner - 1), Math.Max(1, corner - 1));
            }
            if ( DripRadius > 0.2 )
            {
                (double line, double centerU) = Edge switch
                {
                    "top" => (r.Top, r.X + r.Width / 2),
                    "bottom" => (r.Bottom, r.X + r.Width / 2),
                    "left" => (r.Left, r.Y + r.Height / 2),
                    "right" => (r.Right, r.Y + r.Height / 2),
                    _ => (0, 0),
                };
                var drip = LiquidGeometry.Drip(Edge, line, centerU, DripRadius);
                if (drip is not null) dc.DrawGeometry(BrushOf(pal.Surface), null, drip);
            }
        }

        double contentCy = cy;

        // 隐私绿点（麦克风使用中）
        if (MicActive)
        {
            double dotX = r.Right - r.Height * 0.38, dotY = r.Y + r.Height * 0.28;
            dc.DrawEllipse(BrushOf(Color.FromRgb(0x3F, 0xC1, 0x5E)), null, new Point(dotX, dotY), 3.2, 3.2);
            dc.DrawEllipse(null, PenOf(Color.FromRgb(0x3F, 0xC1, 0x5E), 1.1, 0.45), new Point(dotX, dotY), 5.0, 5.0);
        }

        if (Activity is { } act && DateTime.Now < act.Until)
            DrawActivity(dc, pal, r, contentCy, act);
        else
            DrawCompact(dc, pal, r, contentCy);
    }

    /// <summary>
    /// 真 MacBook 刘海贴顶造型（完全对齐 compact-notch.png / monitor.png 参考图）：
    /// 顶边与屏幕上沿严丝合缝齐平，左右上角以反相圆弧耳翼（Shoulder Fillets）向外顺滑过渡至屏幕顶边，
    /// 下方左右大圆角闭合，侧边与底边勾勒极淡 Rim 轮廓线（顶边不描线，与屏幕黑边融为一体）。
    /// </summary>
    private static bool DrawTopShoulderNotch(DrawingContext dc, Palette pal, Rect r, double earR, double botR)
    {
        if (r.Width < 44 || r.Height < 22) return false;
        earR = Math.Clamp(earR, 4, Math.Min(16, r.Height * 0.4));
        botR = Math.Clamp(botR, 6, Math.Min(26, Math.Min(r.Width / 2 - 2, r.Height - earR - 2)));

        var fillGeo = new StreamGeometry();
        using (var ctx = fillGeo.Open())
        {
            ctx.BeginFigure(new Point(r.X - earR, r.Y), true, true);
            ctx.LineTo(new Point(r.Right + earR, r.Y), false, false);
            // 右上反圆角耳翼：圆心在 (r.Right + earR, r.Y + earR)，从顶点逆时针转 90° 到左点 (r.Right, r.Y + earR)
            ctx.ArcTo(new Point(r.Right, r.Y + earR), new Size(earR, earR),
                0, false, SweepDirection.Counterclockwise, false, false);
            ctx.LineTo(new Point(r.Right, r.Bottom - botR), false, false);
            ctx.ArcTo(new Point(r.Right - botR, r.Bottom), new Size(botR, botR),
                0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(r.X + botR, r.Bottom), false, false);
            ctx.ArcTo(new Point(r.X, r.Bottom - botR), new Size(botR, botR),
                0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(r.X, r.Y + earR), false, false);
            // 左上反圆角耳翼：圆心在 (r.X - earR, r.Y + earR)，从右点逆时针转 90° 回到顶点 (r.X - earR, r.Y)
            ctx.ArcTo(new Point(r.X - earR, r.Y), new Size(earR, earR),
                0, false, SweepDirection.Counterclockwise, false, false);
        }
        fillGeo.Freeze();
        dc.DrawGeometry(BrushOf(pal.Surface), null, fillGeo);

        // 仅沿左耳翼 → 左壁 → 底边 → 右壁 → 右耳翼描 1px 淡轮廓线（顶边 y=r.Y 不描边以融入屏幕顶框）
        var rimGeo = new StreamGeometry();
        using (var rctx = rimGeo.Open())
        {
            rctx.BeginFigure(new Point(r.X - earR, r.Y + 0.5), false, false);
            rctx.ArcTo(new Point(r.X, r.Y + earR), new Size(earR, earR),
                0, false, SweepDirection.Clockwise, true, false);
            rctx.LineTo(new Point(r.X, r.Bottom - botR), true, false);
            rctx.ArcTo(new Point(r.X + botR, r.Bottom), new Size(botR, botR),
                0, false, SweepDirection.Counterclockwise, true, false);
            rctx.LineTo(new Point(r.Right - botR, r.Bottom), true, false);
            rctx.ArcTo(new Point(r.Right, r.Bottom - botR), new Size(botR, botR),
                0, false, SweepDirection.Counterclockwise, true, false);
            rctx.LineTo(new Point(r.Right, r.Y + earR), true, false);
            rctx.ArcTo(new Point(r.Right + earR, r.Y + 0.5), new Size(earR, earR),
                0, false, SweepDirection.Clockwise, true, false);
        }
        rimGeo.Freeze();
        dc.DrawGeometry(null, PenOf(pal.Rim, 1.0, 0.55), rimGeo);
        return true;
    }

    private void DrawActivity(DrawingContext dc, Palette pal, Rect r, double cy, ActivityView act)
    {
        double ppd = Ppd;
        var tint = act.Kind == "temp" ? pal.Up : pal.Accent;
        string label = act.Kind == "volume" ? (act.Muted ? "静音" : "音量") : act.Label;
        dc.DrawEllipse(Faint(tint, 0.25), null, new Point(r.X + 22, cy), 13, 13);
        var glyph = Text(label, 9, pal.Text, ppd: ppd);
        dc.DrawText(glyph, new Point(r.X + 22 - glyph.Width / 2, cy - glyph.Height / 2));
        double x = r.X + 44, w = Math.Max(0, r.Width - 70 - 44);
        var enter = Easing.EaseOut((DateTime.Now - act.Start).TotalSeconds / 0.3);
        double filled = Math.Min(Math.Max(4, w * act.Value * enter), w);
        dc.DrawRoundedRectangle(BrushOf(pal.SurfaceAlt), null, RenderKit.SafeRect(x, cy - 3, w, 6), 3, 3);
        dc.DrawRoundedRectangle(BrushOf(pal.Text), null, RenderKit.SafeRect(x, cy - 3, filled, 6), 3, 3);
        var pct = Text(act.RightText ?? $"{Math.Round(act.Value * 100)}", 12, pal.Text, true, ppd);
        dc.DrawText(pct, new Point(r.Right - 14 - pct.Width, cy - pct.Height / 2));
    }

    public double CompactContentWidth { get; private set; }
    public bool HasMeasuredCompact { get; private set; }
    public void ResetCompactMeasure() => HasMeasuredCompact = false;

    /// <summary>按当前状态直接测算收起视图所需胶囊宽度（DIP），首帧即可精确到位。</summary>
    public double MeasureCompactWidth(string view)
    {
        var pal = Palette ?? Palette.Dark;
        double ppd = Ppd;
        return view switch
        {
            "clock" => LayoutClock(pal, ppd).CapsuleW,
            "combo" => LayoutCombo(pal, ppd).CapsuleW,
            "dash" => LayoutDash(pal, ppd).CapsuleW,
            "media" => MeasureMediaWidth(pal, ppd),
            "pet" => 110,
            _ => HasMeasuredCompact ? CompactContentWidth : 0,
        };
    }

    private void DrawCompact(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        double cx = r.X + r.Width / 2;
        double w;
        switch (CompactView)
        {
            case "pet":
                _pet.SurfaceColor = pal.Surface;
                _pet.Draw(dc, cx, cy, 1.0, 1, LookPoint, PointerNear);
                w = 110;
                break;
            case "clock":
                w = DrawClock(dc, pal, r, cy);
                break;
            case "combo":
                w = DrawCombo(dc, pal, r, cy);
                break;
            case "dash":
                w = DrawDash(dc, pal, r, cy);
                break;
            case "media":
                w = DrawMedia(dc, pal, r, cy);
                break;
            case "monitor":
                w = DrawMonitor(dc, pal, r, cy, Snapshot);
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
        if (w > 0)
        {
            CompactContentWidth = Math.Max(110, w);
            HasMeasuredCompact = true;
        }
    }

    private void DrawGaugeCenter(DrawingContext dc, Glyph icon, Res res, double x, double cy, double dial, Color color, double ppd)
    {
        if (DecorLetters)
        {
            string letter = res switch
            {
                Res.Cpu => "C", Res.Mem => "M", Res.Gpu => "G",
                Res.Disk => "D", Res.Batt => "B", Res.Net => "N", _ => "?",
            };
            var t = Text(letter, dial * 0.50, color, true, ppd);
            dc.DrawText(t, new Point(x + (dial - t.Width) / 2, cy - t.Height / 2));
        }
        else
        {
            DrawIcon(dc, icon, x + (dial - 12 * 0.62) / 2, cy, color, 0.62);
        }
    }

    // ============ 1. 时间胶囊（clock，与 macOS 版完全同款） ============
    //   [ 21:24 35  │  11月27日·周五  │  ☀ 18° 北京 ]

    private sealed class ClockLayout
    {
        public FormattedText Time = null!, Date = null!, Sec = null!;
        public double TimeSlotW, SecSlotW;
        public Glyph? Icon;
        public Color IconColor;
        public FormattedText? Temp, City;
        public bool HasWeather;
        public FormattedText? BattText;
        public Color BattColor;
        public bool HasBattery;

        public const double SecGap = 3;
        public const double IconW = 13;
        public const double IconGap = 7;
        public const double CityGap = 7;
        public const double BattIconW = 13;
        public const double BattGap = 5;

        public double DateW => Date.WidthIncludingTrailingWhitespace;
        public double TempW => Temp?.WidthIncludingTrailingWhitespace ?? 0;
        public double CityW => City?.WidthIncludingTrailingWhitespace ?? 0;
        public double BattW => HasBattery && BattText is not null
            ? BattIconW + BattGap + BattText.WidthIncludingTrailingWhitespace : 0;
        public double LeftW => TimeSlotW + SecGap + SecSlotW;
        public double WeatherW => HasWeather
            ? IconW + IconGap + TempW + (City is not null ? CityGap + CityW : 0)
            : 0;
        public double ContentW => LeftW + ClockColGap + DateW
            + (HasWeather ? ClockColGap + WeatherW : 0)
            + (HasBattery ? ClockColGap + BattW : 0);
        public double CapsuleW => ContentW + ClockPadX * 2;
    }

    private ClockLayout LayoutClock(Palette pal, double ppd)
    {
        var now = DateTime.Now;
        var timeTxt = Text(now.ToString("HH:mm"), FontHero, pal.Text, true, ppd);
        var secTxt = Text(now.ToString("ss"), FontBody, pal.TextDim, true, ppd);
        // 用数字模板锁住最小槽宽，每秒跳字时胶囊宽度完全不抖
        double timeSlot = Math.Max(timeTxt.WidthIncludingTrailingWhitespace,
            Text("00:00", FontHero, pal.Text, true, ppd).WidthIncludingTrailingWhitespace);
        double secSlot = Math.Max(secTxt.WidthIncludingTrailingWhitespace,
            Text("00", FontBody, pal.TextDim, true, ppd).WidthIncludingTrailingWhitespace);

        var l = new ClockLayout
        {
            Time = timeTxt,
            Sec = secTxt,
            TimeSlotW = timeSlot,
            SecSlotW = secSlot,
            Date = Text(now.ToString("M月d日") + "·" + WeekdayShort(now.DayOfWeek), FontValue, pal.TextDim, false, ppd),
        };

        if (Weather is { IsStale: false } w)
        {
            l.HasWeather = true;
            l.Icon = WeatherGlyph(w.Condition);
            l.IconColor = WeatherColor(w.Condition, pal);
            l.Temp = Text($"{Math.Round(w.Temp)}°", FontValue, pal.Text, true, ppd);
            if (!string.IsNullOrWhiteSpace(w.City))
            {
                var full = Text(w.City, FontLabel, pal.TextDim, false, ppd);
                l.City = full.WidthIncludingTrailingWhitespace <= ClockCityMaxW
                    ? full
                    : Trimmed(w.City, FontLabel, pal.TextDim, ClockCityMaxW, false, ppd);
            }
        }

        // 笔记本/带电池机型：在时间胶囊右端显示紧凑矢量电池与百分比（台式机无电池时自动隐藏）
        var batDev = DeviceService.Snapshot.FirstOrDefault(d => d.Kind == "battery");
        double? bp = batDev?.Percent ?? Snapshot?.BatteryPercent;
        bool charging = (batDev?.Charging ?? false) || (batDev?.PluggedIn ?? false) || (Snapshot?.BatteryCharging ?? false);
        if (bp is { } bpVal)
        {
            l.HasBattery = true;
            int pct = (int)Math.Round(Math.Clamp(bpVal, 0, 100));
            l.BattColor = charging ? DownC : pct <= 20 ? pal.Up : pct <= 40 ? pal.Warning : ResColor(Res.Batt, pal);
            l.BattText = Text($"{pct}%", FontValue, pct <= 20 && !charging ? pal.Up : pal.Text, true, ppd);
        }
        return l;
    }

    private double DrawClock(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        var L = LayoutClock(pal, ppd);
        double x = r.X + (r.Width - L.ContentW) / 2;
        double mid = cy;

        // ① 大号时间 + 基线对齐秒数
        double timeTop = mid - L.Time.Height / 2;
        dc.DrawText(L.Time, new Point(x, timeTop));
        dc.DrawText(L.Sec, new Point(x + L.TimeSlotW + ClockLayout.SecGap,
            timeTop + (L.Time.Baseline - L.Sec.Baseline)));
        x += L.LeftW;

        void Divider(double px)
            => dc.DrawLine(PenOf(pal.Hairline, 1, 0.85), new Point(px, mid - 10), new Point(px, mid + 10));

        // ② 日期·星期
        Divider(x + ClockColGap / 2);
        x += ClockColGap;
        dc.DrawText(L.Date, new Point(x, mid - L.Date.Height / 2));
        x += L.DateW;

        // ③ 天气：矢量图标 + 温度 + 城市
        if (L.HasWeather && L.Icon is { } g)
        {
            Divider(x + ClockColGap / 2);
            x += ClockColGap;
            DrawIcon(dc, g, x, mid, Mute(L.IconColor, pal.Text, 0.62), ClockLayout.IconW / 12.0);
            x += ClockLayout.IconW + ClockLayout.IconGap;
            if (L.Temp is not null)
            {
                dc.DrawText(L.Temp, new Point(x, mid - L.Temp.Height / 2));
                x += L.TempW;
            }
            if (L.City is not null)
            {
                x += ClockLayout.CityGap;
                dc.DrawText(L.City, new Point(x, mid - L.City.Height / 2));
            }
        }

        // ④ 本机电池（仅带电池机型出现）
        if (L.HasBattery && L.BattText is not null)
        {
            Divider(x + ClockColGap / 2);
            x += ClockColGap;
            DrawIcon(dc, Glyph.Battery, x, mid, L.BattColor, ClockLayout.BattIconW / 12.0);
            x += ClockLayout.BattIconW + ClockLayout.BattGap;
            dc.DrawText(L.BattText, new Point(x, mid - L.BattText.Height / 2));
        }
        return L.CapsuleW;
    }

    private static string WeekdayShort(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "周一", DayOfWeek.Tuesday => "周二", DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四", DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六", DayOfWeek.Sunday => "周日",
        _ => "",
    };

    private static Glyph WeatherGlyph(string? condition)
    {
        string c = condition ?? "";
        if (c.Contains('雷')) return Glyph.Bolt;
        if (c.Contains('雨')) return Glyph.Rain;
        if (c.Contains('雪')) return Glyph.Snow;
        if (c.Contains('雾') || c.Contains('霾')) return Glyph.Fog;
        if (c.Contains('云') || c.Contains('阴')) return Glyph.Cloud;
        return Glyph.Sun;
    }

    private static Color WeatherColor(string? condition, Palette pal)
    {
        string c = condition ?? "";
        if (c.Contains('雷')) return Color.FromRgb(0xC9, 0xA6, 0xFF);
        if (c.Contains('雨')) return Color.FromRgb(0x6F, 0xB8, 0xFF);
        if (c.Contains('雪')) return Color.FromRgb(0xA8, 0xE6, 0xF0);
        if (c.Contains('雾') || c.Contains('霾')) return pal.TextDim;
        if (c.Contains('云') || c.Contains('阴')) return Color.FromRgb(0x9F, 0xB4, 0xD0);
        return Color.FromRgb(0xFF, 0xC4, 0x6B);
    }

    // ============ 2. 信息胶囊（combo，1:1 对齐 Mac 实机悬浮药丸胶囊） ============
    //   [ 03:08  (C) 3%  (M) 18%  (G) 15%  │  (C) 29°  (G) 30°  (D) 54%  │  ↓ 6.1K  ↑ 120.5K ]

    private static readonly Color NetDownArrowC = Color.FromRgb(0x2F, 0xD0, 0x6B);
    private static readonly Color NetUpArrowC = Color.FromRgb(0xFF, 0x3B, 0x5C);

    /// <summary>温度分级配色：≤60° 绿色 / 60°–90° 黄色 / ≥90° 红色。</summary>
    private static Color ComboTempColor(double temp, Palette pal)
        => ReadableCached(RenderKit.ThermalColor(temp), pal);

    private struct ComboStyle
    {
        public double FigureFont;
        public double Dial;
        public double DialGap;
        public double GaugeGap;
        public double ClockGap;
        public bool ClockStrong;
        public bool FigureResColor;
    }

    private ComboStyle CurrentComboStyle => ComboNumericHero
        ? new ComboStyle
        {
            FigureFont = 13.5, Dial = 22, DialGap = 5, GaugeGap = 10, ClockGap = 12,
            ClockStrong = false, FigureResColor = false,
        }
        : new ComboStyle
        {
            FigureFont = FontValue, Dial = 20, DialGap = 4, GaugeGap = 10, ClockGap = 12,
            ClockStrong = true, FigureResColor = true,
        };

    private sealed class ComboLayout
    {
        public FormattedText? Clock;
        public double ClockSlotW;
        public FormattedText? NetDownArrow, NetDownVal, NetUpArrow, NetUpVal;
        public double NetDownValSlotW, NetUpValSlotW;
        // 左组（使用率环：C% / M% / G%）
        public readonly List<(Glyph Icon, Res Res, double Share, Color RingColor, FormattedText Figure, double FigureSlotW)> LeftRings = new();
        // 中组（温度环 + 硬盘/电池环：C° / G° / D% / B%）
        public readonly List<(Glyph Icon, Res Res, double Share, Color RingColor, FormattedText Figure, double FigureSlotW)> MidRings = new();
        public ComboStyle Style;

        public const double NetArrowGap = 4;
        public const double NetPairGap = 8;

        private double MeasureGroup(List<(Glyph Icon, Res Res, double Share, Color RingColor, FormattedText Figure, double FigureSlotW)> list)
            => list.Count == 0 ? 0
                : list.Sum(g => Style.Dial + Style.DialGap + g.FigureSlotW + Style.GaugeGap) - Style.GaugeGap;

        public double LeftRingsW => MeasureGroup(LeftRings);
        public double MidRingsW => MeasureGroup(MidRings);

        public double NetW => (NetDownArrow is not null && NetDownVal is not null && NetUpArrow is not null && NetUpVal is not null)
            ? NetDownArrow.WidthIncludingTrailingWhitespace + NetArrowGap + NetDownValSlotW
              + NetPairGap
              + NetUpArrow.WidthIncludingTrailingWhitespace + NetArrowGap + NetUpValSlotW
            : 0;

        public double ContentW
        {
            get
            {
                double w = 0;
                bool hasLeft = false;
                if (Clock is not null)
                {
                    w += ClockSlotW;
                    hasLeft = true;
                }
                if (LeftRings.Count > 0)
                {
                    if (hasLeft) w += Style.ClockGap;
                    w += LeftRingsW;
                    hasLeft = true;
                }
                if (MidRings.Count > 0)
                {
                    if (hasLeft) w += CompactGroupGap * 2;
                    w += MidRingsW;
                    hasLeft = true;
                }
                if (NetW > 0)
                {
                    if (hasLeft) w += CompactGroupGap * 2;
                    w += NetW;
                }
                return Math.Max(84, w);
            }
        }

        public double CapsuleW => ContentW + ComboPadX * 2;
    }

    private ComboLayout LayoutCombo(Palette pal, double ppd)
    {
        var snap = Snapshot;
        var style = CurrentComboStyle;
        var l = new ComboLayout { Style = style };

        if (WantCombo("clock"))
        {
            var cColor = style.ClockStrong ? pal.Text : pal.TextDim;
            l.Clock = Text(DateTime.Now.ToString("HH:mm"), FontValue, cColor, style.ClockStrong, ppd);
            l.ClockSlotW = Math.Max(l.Clock.WidthIncludingTrailingWhitespace,
                Text("00:00", FontValue, cColor, style.ClockStrong, ppd).WidthIncludingTrailingWhitespace);
        }

        double minPctSlot = Math.Ceiling(Text("9%", style.FigureFont, pal.Text, true, ppd).WidthIncludingTrailingWhitespace);
        double minTempSlot = Math.Ceiling(Text("88°", style.FigureFont, pal.Text, true, ppd).WidthIncludingTrailingWhitespace);

        void AddUsageRing(List<(Glyph, Res, double, Color, FormattedText, double)> target, Glyph icon, Res res, double usage)
        {
            Color rc = ResColor(res, pal);
            Color figC = style.FigureResColor ? rc : LevelColor(usage, pal.Text, pal);
            var fig = Text($"{Math.Round(usage)}%", style.FigureFont, figC, true, ppd);
            double share = usage > 0 ? Math.Max(usage / 100.0, 0.05) : 0;
            target.Add((icon, res, share, rc, fig, Math.Max(fig.WidthIncludingTrailingWhitespace, minPctSlot)));
        }

        void AddTempRing(Glyph icon, Res res, double? temp)
        {
            if (temp is not { } tv || tv <= 0) return;
            Color tc = ComboTempColor(tv, pal);
            var fig = Text($"{Math.Round(tv)}°", style.FigureFont, tc, true, ppd);
            double share = Math.Clamp(tv / 100.0, 0.06, 1.0);
            l.MidRings.Add((icon, res, share, tc, fig, Math.Max(fig.WidthIncludingTrailingWhitespace, minTempSlot)));
        }

        // ① 左组：CPU / 内存 / GPU / 硬盘 占用环（C% / M% / G% / D%）
        if (WantCombo("dials"))
        {
            double gpuPct = Math.Max(snap?.GpuUsage ?? 0, Thermal?.GpuLoad ?? 0);
            AddUsageRing(l.LeftRings, Glyph.Cpu, Res.Cpu, snap?.CpuUsage ?? 0);
            AddUsageRing(l.LeftRings, Glyph.Mem, Res.Mem, snap?.MemoryUsagePercent ?? 0);
            AddUsageRing(l.LeftRings, Glyph.Gpu, Res.Gpu, gpuPct);
        }
        if (WantCombo("disk"))
        {
            AddUsageRing(l.LeftRings, Glyph.Disk, Res.Disk, snap?.DiskUsagePercent ?? 0);
        }

        // ② 中组：温度环（CPU° / GPU° / 硬盘°）+ 电池状态环（B% / 充电⚡）
        double? cpuT = Thermal?.CpuTemp ?? snap?.CpuTemp;
        double? gpuT = Thermal?.GpuTemp ?? snap?.GpuTemp;
        double? ssdT = Thermal?.SsdTemp ?? snap?.SsdTemp;

        if (WantCombo("cputemp")) AddTempRing(Glyph.Cpu, Res.Cpu, cpuT);
        if (WantCombo("gputemp")) AddTempRing(Glyph.Gpu, Res.Gpu, gpuT);
        if (WantCombo("ssdtemp")) AddTempRing(Glyph.Ssd, Res.Disk, ssdT);
        if (WantCombo("battery"))
        {
            var devs = DeviceService.Snapshot;
            var bat = devs.FirstOrDefault(d => d.Kind == "battery");
            double? bp = bat?.Percent ?? snap?.BatteryPercent;
            bool charging = (bat?.Charging ?? false) || (bat?.PluggedIn ?? false) || (snap?.BatteryCharging ?? false);

            // 台式机无内置电池时，自动展示首个无线外设（耳机/鼠标/键盘等）电量
            var primaryWireless = bp is null ? devs.FirstOrDefault(d => d.Kind != "battery") : null;
            if (bp is { } bpVal)
            {
                int pct = (int)Math.Round(Math.Clamp(bpVal, 0, 100));
                Color rc = charging
                    ? ReadableCached(RenderKit.ThermalCoolColor, pal)
                    : pct <= 20 ? pal.Up
                    : pct <= 40 ? pal.Warning
                    : ResColor(Res.Batt, pal);
                Color figC = charging
                    ? ReadableCached(RenderKit.ThermalCoolColor, pal)
                    : pct <= 20 ? pal.Up
                    : (style.FigureResColor ? rc : pal.Text);
                string label = charging ? $"{pct}%⚡" : $"{pct}%";
                var fig = Text(label, style.FigureFont, figC, true, ppd);
                l.MidRings.Add((Glyph.Battery, Res.Batt, Math.Clamp(pct / 100.0, 0.05, 1.0), rc, fig,
                    Math.Max(fig.WidthIncludingTrailingWhitespace, minPctSlot)));
            }
            else if (primaryWireless is { } pw)
            {
                bool pwChg = pw.Charging || pw.PluggedIn;
                int pct = pw.Percent >= 0 ? Math.Clamp(pw.Percent, 0, 100) : 100;
                Color rc = pwChg
                    ? ReadableCached(RenderKit.ThermalCoolColor, pal)
                    : pct <= 20 ? pal.Up
                    : pct <= 40 ? pal.Warning
                    : ResColor(Res.Batt, pal);
                Color figC = pwChg
                    ? ReadableCached(RenderKit.ThermalCoolColor, pal)
                    : pct <= 20 ? pal.Up
                    : (style.FigureResColor ? rc : pal.Text);
                string label = pw.Percent < 0 ? "⚡" : (pwChg ? $"{pct}%⚡" : $"{pct}%");
                var fig = Text(label, style.FigureFont, figC, true, ppd);
                l.MidRings.Add((Glyph.Battery, Res.Batt, Math.Clamp(pct / 100.0, 0.05, 1.0), rc, fig,
                    Math.Max(fig.WidthIncludingTrailingWhitespace, minPctSlot)));
            }
        }

        // ③ 右组：彩色箭头（绿↓ / 红↑）+ 粗体纯白速率数字（6.1K / 120.5K）
        if (WantCombo("net"))
        {
            string dn = MonitorSnapshot.FormatSpeedFixed(snap?.DownloadSpeed ?? 0).Trim();
            string up = MonitorSnapshot.FormatSpeedFixed(snap?.UploadSpeed ?? 0).Trim();
            double minSpeedSlot = Math.Ceiling(Text("99.9K", style.FigureFont, pal.Text, true, ppd).WidthIncludingTrailingWhitespace);
            l.NetDownArrow = Text("↓", style.FigureFont, ReadableCached(NetDownArrowC, pal), true, ppd);
            l.NetDownVal = Text(dn, style.FigureFont, pal.Text, true, ppd);
            l.NetDownValSlotW = Math.Max(l.NetDownVal.WidthIncludingTrailingWhitespace, minSpeedSlot);
            l.NetUpArrow = Text("↑", style.FigureFont, ReadableCached(NetUpArrowC, pal), true, ppd);
            l.NetUpVal = Text(up, style.FigureFont, pal.Text, true, ppd);
            l.NetUpValSlotW = Math.Max(l.NetUpVal.WidthIncludingTrailingWhitespace, minSpeedSlot);
        }

        return l;
    }

    private double DrawCombo(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        var L = LayoutCombo(pal, ppd);
        double x = r.X + (r.Width - L.ContentW) / 2;

        void Divider()
            => dc.DrawLine(PenOf(pal.Hairline, 1, 0.78),
                new Point(x + CompactGroupGap, cy - 9), new Point(x + CompactGroupGap, cy + 9));
        void Gap() { Divider(); x += CompactGroupGap * 2; }

        void DrawRingGroup(List<(Glyph Icon, Res Res, double Share, Color RingColor, FormattedText Figure, double FigureSlotW)> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                RenderKit.Ring(dc, new Point(x + L.Style.Dial / 2, cy), L.Style.Dial,
                    2.3, g.Share, pal.Track, g.RingColor);
                DrawGaugeCenter(dc, g.Icon, g.Res, x, cy, L.Style.Dial, g.RingColor, ppd);
                x += L.Style.Dial + L.Style.DialGap;
                dc.DrawText(g.Figure, new Point(x, cy - g.Figure.Height / 2));
                x += g.FigureSlotW;
                if (i < list.Count - 1) x += L.Style.GaugeGap;
            }
        }

        bool hasLeft = false;
        if (L.Clock is not null)
        {
            dc.DrawText(L.Clock, new Point(x, cy - L.Clock.Height / 2));
            x += L.ClockSlotW;
            hasLeft = true;
        }

        if (L.LeftRings.Count > 0)
        {
            if (hasLeft) x += L.Style.ClockGap;
            DrawRingGroup(L.LeftRings);
            hasLeft = true;
        }

        if (L.MidRings.Count > 0)
        {
            if (hasLeft) Gap();
            DrawRingGroup(L.MidRings);
            hasLeft = true;
        }

        if (L.NetDownArrow is not null && L.NetDownVal is not null && L.NetUpArrow is not null && L.NetUpVal is not null)
        {
            if (hasLeft) Gap();
            dc.DrawText(L.NetDownArrow, new Point(x, cy - L.NetDownArrow.Height / 2));
            x += L.NetDownArrow.WidthIncludingTrailingWhitespace + ComboLayout.NetArrowGap;
            double dnOffset = L.NetDownValSlotW - L.NetDownVal.WidthIncludingTrailingWhitespace;
            dc.DrawText(L.NetDownVal, new Point(x + dnOffset, cy - L.NetDownVal.Height / 2));
            x += L.NetDownValSlotW + ComboLayout.NetPairGap;

            dc.DrawText(L.NetUpArrow, new Point(x, cy - L.NetUpArrow.Height / 2));
            x += L.NetUpArrow.WidthIncludingTrailingWhitespace + ComboLayout.NetArrowGap;
            double upOffset = L.NetUpValSlotW - L.NetUpVal.WidthIncludingTrailingWhitespace;
            dc.DrawText(L.NetUpVal, new Point(x + upOffset, cy - L.NetUpVal.Height / 2));
        }

        return L.CapsuleW;
    }

    // ============ 3. 单排仪表盘（dash，与 macOS 版 LayoutDash 一致） ============

    private sealed class DashLayout
    {
        public double[]? Spectrum;
        public int SpectrumCount;
        public int ClipCount;
        public FormattedText? ClipText;
        public readonly List<(Glyph Icon, Res Res, bool Ok, FormattedText Text)> Temps = new();
        public readonly List<(Glyph Icon, Res Res, double Share, FormattedText Text)> Usage = new();
        public (Glyph Icon, double Share, Color Color, FormattedText Text)? Battery;
        public readonly List<Color> WirelessRings = new();
        public readonly List<double> WirelessShares = new();
        public FormattedText? NetDown, NetUp;

        public const double SpectrumW = 46;
        public const double SpectrumH = 20;
        public const double NetGap = 10;

        public double ClipW => ClipText is null ? 0 : 13 + Math.Max(ClipText.WidthIncludingTrailingWhitespace, 16);
        public double TempW => Temps.Count == 0 ? 0
            : Temps.Sum(t => CompactIconBox + CompactIconGap + Math.Max(t.Text.WidthIncludingTrailingWhitespace, TempValueMinW) + CompactTempGap) - CompactTempGap;
        public double UsageW => Usage.Count == 0 ? 0
            : Usage.Sum(u => DashRing + 5 + Math.Max(u.Text.WidthIncludingTrailingWhitespace, 34) + DashItemGap) - DashItemGap;
        public double BattW => Battery is { } b ? DashRing + 5 + Math.Max(b.Text.WidthIncludingTrailingWhitespace, 34) : 0;
        public double DevW => WirelessShares.Count == 0 ? 0 : WirelessShares.Count * (14 + 6) - 6;
        public double NetW => (NetDown is not null && NetUp is not null)
            ? NetDown.WidthIncludingTrailingWhitespace + NetGap + NetUp.WidthIncludingTrailingWhitespace : 0;

        public double ContentW
        {
            get
            {
                double w = 0;
                int groups = 0;
                if (SpectrumCount > 1) { w += SpectrumW; groups++; }
                if (ClipCount > 0 && ClipText is not null) { w += ClipW; groups++; }
                if (Temps.Count > 0) { w += TempW; groups++; }
                if (Usage.Count > 0) { w += UsageW; groups++; }
                if (Battery is not null) { w += BattW; groups++; }
                if (WirelessShares.Count > 0) { w += DevW; groups++; }
                if (NetDown is not null && NetUp is not null) { w += NetW; groups++; }
                if (groups == 0) return 84;
                return w + (groups - 1) * CompactGroupGap * 2;
            }
        }

        public double CapsuleW => ContentW + DashPadX * 2;
    }

    private DashLayout LayoutDash(Palette pal, double ppd)
    {
        var snap = Snapshot;
        var l = new DashLayout();

        // ⓪ CPU 频谱柱（对应 cores 开关）
        if (Want("cores") && CpuHistory is { Length: > 0 } hist && CpuHistoryCount > 1)
        {
            int n = Math.Min(12, Math.Min(CpuHistoryCount, hist.Length));
            var slice = new double[n];
            Array.Copy(hist, hist.Length - n, slice, 0, n);
            l.Spectrum = slice;
            l.SpectrumCount = n;
        }

        int clipCount = ClipboardService.Count;
        if (clipCount > 0 && DashItems is null)
        {
            l.ClipCount = clipCount;
            l.ClipText = Text($"{clipCount}", FontValue, pal.Text, true, ppd);
        }

        // ① 温度组
        void AddDashTemp(bool want, Glyph icon, Res res, double? val, bool omitWhenNull = false)
        {
            if (!want) return;
            if (omitWhenNull && val is null) return;
            bool ok = val is { } v0 && v0 > 0;
            double tv = val ?? 0;
            Color c = !ok ? pal.TextTertiary : ComboTempColor(tv, pal);
            l.Temps.Add((icon, res, ok, Text(ok ? $"{Math.Round(tv)}°" : "--", FontValue, c, true, ppd)));
        }

        AddDashTemp(Want("cputemp"), Glyph.Cpu, Res.Cpu, Thermal?.CpuTemp ?? snap?.CpuTemp);
        AddDashTemp(Want("gputemp"), Glyph.Gpu, Res.Gpu, Thermal?.GpuTemp ?? snap?.GpuTemp);
        AddDashTemp(Want("ssd"), Glyph.Ssd, Res.Disk, Thermal?.SsdTemp ?? snap?.SsdTemp, omitWhenNull: DashItems is null);

        // ② 占用组（环包图标/字母）
        if (Want("mem"))
        {
            double mv = snap?.MemoryUsagePercent ?? 0;
            l.Usage.Add((Glyph.Mem, Res.Mem, mv / 100.0, Text($"{mv:F0}%", FontValue, ResColor(Res.Mem, pal), true, ppd)));
        }
        if (Want("gpu") && (snap is null || snap.GpuUsage > 0 || DashItems is not null))
        {
            double gv = snap?.GpuUsage ?? 0;
            l.Usage.Add((Glyph.Gpu, Res.Gpu, gv / 100.0, Text($"{gv:F0}%", FontValue, ResColor(Res.Gpu, pal), true, ppd)));
        }
        if (Want("disk"))
        {
            double dv = snap?.DiskUsagePercent ?? 0;
            l.Usage.Add((Glyph.Disk, Res.Disk, dv / 100.0, Text($"{dv:F0}%", FontValue, ResColor(Res.Disk, pal), true, ppd)));
        }

        // ③ 电池与无线外设电量环（DeviceService 优先，首帧回退 MonitorSnapshot.BatteryPercent）
        if (Want("battery"))
        {
            var devs = DeviceService.Snapshot;
            var bat = devs.FirstOrDefault(d => d.Kind == "battery");
            double? bp = bat?.Percent ?? snap?.BatteryPercent;
            bool charging = (bat?.Charging ?? false) || (bat?.PluggedIn ?? false) || (snap?.BatteryCharging ?? false);
            if (bp is { } bpVal)
            {
                int pct = (int)Math.Round(Math.Clamp(bpVal, 0, 100));
                Color bc = pct <= 20 && !charging ? pal.Up : pct <= 40 && !charging ? pal.Warning : charging ? DownC : ResColor(Res.Batt, pal);
                l.Battery = (Glyph.Battery, pct / 100.0, bc, Text($"{pct}%", FontValue, ReadableCached(bc, pal), true, ppd));
            }
            foreach (var dev in devs.Where(d => d.Kind != "battery").Take(4))
            {
                Color dc2 = dev.Charging ? DownC : dev.Percent is >= 0 and <= 20 ? pal.Up : dev.Percent is >= 0 and <= 40 ? pal.Warning : ResColor(Res.Batt, pal);
                l.WirelessShares.Add(dev.Percent >= 0 ? dev.Percent / 100.0 : (dev.Charging ? 1.0 : 0.05));
                l.WirelessRings.Add(dc2);
            }
        }

        // ④ 单行网速
        if (Want("net"))
        {
            l.NetDown = Text("↓" + MonitorSnapshot.FormatSpeedFixed(snap?.DownloadSpeed ?? 0), FontValue, pal.Down, false, ppd);
            l.NetUp = Text("↑" + MonitorSnapshot.FormatSpeedFixed(snap?.UploadSpeed ?? 0), FontValue, pal.Up, false, ppd);
        }

        return l;
    }

    private double DrawDash(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        var L = LayoutDash(pal, ppd);
        double x = r.X + (r.Width - L.ContentW) / 2;

        void Divider()
            => dc.DrawLine(PenOf(pal.Hairline, 1, 0.8),
                new Point(x + CompactGroupGap, cy - 9), new Point(x + CompactGroupGap, cy + 9));
        void Gap() { Divider(); x += CompactGroupGap * 2; }

        bool hasLeft = false;

        if (L.SpectrumCount > 1 && L.Spectrum is not null)
        {
            RenderKit.Spectrum(dc, RenderKit.SafeRect(x, cy - 10, DashLayout.SpectrumW, DashLayout.SpectrumH),
                L.Spectrum, L.SpectrumCount, ResColor(Res.Cpu, pal), ResColor(Res.Mem, pal));
            x += DashLayout.SpectrumW;
            hasLeft = true;
        }

        if (L.ClipCount > 0 && L.ClipText is not null)
        {
            if (hasLeft) Gap();
            DrawIcon(dc, Glyph.ClipBox, x, cy, pal.TextDim, 0.95);
            x += 13;
            Baseline(dc, L.ClipText, x, Math.Round(cy + FontValue * 0.36));
            x += Math.Max(L.ClipText.WidthIncludingTrailingWhitespace, 16);
            hasLeft = true;
        }

        if (L.Temps.Count > 0)
        {
            if (hasLeft) Gap();
            for (int i = 0; i < L.Temps.Count; i++)
            {
                var t = L.Temps[i];
                DrawIcon(dc, t.Icon, x, cy, t.Ok ? ResColor(t.Res, pal) : pal.TextTertiary, 0.9);
                x += CompactIconBox + CompactIconGap;
                Baseline(dc, t.Text, x, Math.Round(cy + FontValue * 0.36));
                x += Math.Max(t.Text.WidthIncludingTrailingWhitespace, TempValueMinW);
                if (i < L.Temps.Count - 1) x += CompactTempGap;
            }
            hasLeft = true;
        }

        if (L.Usage.Count > 0)
        {
            if (hasLeft) Gap();
            for (int i = 0; i < L.Usage.Count; i++)
            {
                var u = L.Usage[i];
                var c = ResColor(u.Res, pal);
                RenderKit.Ring(dc, new Point(x + DashRing / 2, cy), DashRing, 2.2, u.Share, pal.Track, c);
                DrawGaugeCenter(dc, u.Icon, u.Res, x, cy, DashRing, c, ppd);
                x += DashRing + 5;
                Baseline(dc, u.Text, x, Math.Round(cy + FontValue * 0.36));
                x += Math.Max(u.Text.WidthIncludingTrailingWhitespace, 34);
                if (i < L.Usage.Count - 1) x += DashItemGap;
            }
            hasLeft = true;
        }

        if (L.Battery is { } b)
        {
            if (hasLeft) Gap();
            RenderKit.Ring(dc, new Point(x + DashRing / 2, cy), DashRing, 2.2, b.Share, pal.Track, b.Color);
            DrawGaugeCenter(dc, b.Icon, Res.Batt, x, cy, DashRing, b.Color, ppd);
            x += DashRing + 5;
            Baseline(dc, b.Text, x, Math.Round(cy + FontValue * 0.36));
            x += Math.Max(b.Text.WidthIncludingTrailingWhitespace, 34);
            hasLeft = true;
        }

        if (L.WirelessShares.Count > 0)
        {
            if (hasLeft) Gap();
            for (int i = 0; i < L.WirelessShares.Count; i++)
            {
                RenderKit.Ring(dc, new Point(x + 7, cy), 14, 2.0, L.WirelessShares[i], pal.Track, L.WirelessRings[i]);
                x += 14 + 6;
            }
            x -= 6;
            hasLeft = true;
        }

        if (L.NetDown is not null && L.NetUp is not null)
        {
            if (hasLeft) Gap();
            Baseline(dc, L.NetDown, x, Math.Round(cy + FontValue * 0.36));
            x += L.NetDown.WidthIncludingTrailingWhitespace + DashLayout.NetGap;
            Baseline(dc, L.NetUp, x, Math.Round(cy + FontValue * 0.36));
        }

        return L.CapsuleW;
    }

    // ============ 4. 媒体律动胶囊（media，对齐 BrimDeck compact-notch.png：左圆角封面 + 歌名 + 右封面同色律动柱） ============

    private double MeasureMediaWidth(Palette pal, double ppd)
    {
        var m = Media;
        if (m is null || !m.HasMedia)
            return Text("暂无正在播放的媒体", FontBody, pal.TextDim, false, ppd).WidthIncludingTrailingWhitespace + 68;
        string label = string.IsNullOrEmpty(m.Artist) ? m.Title : $"{m.Title} · {m.Artist}";
        var full = Text(label, FontBody, pal.Text, true, ppd);
        double textW = Math.Min(210, full.WidthIncludingTrailingWhitespace);
        // 左封面 28 + 间距 10 + 文本 + 间距 12 + 5 根律动柱(24) + 左右内边距 28
        return 14 + 28 + 10 + textW + 12 + 24 + 14;
    }

    private double DrawMedia(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        var m = Media;
        Color accent = m?.AccentColor ?? pal.Accent;
        double x = r.X + 14;

        // 左：28×28 圆角封面或唱片矢量
        var coverBox = RenderKit.SafeRect(x, cy - 14, 28, 28);
        if (m?.Cover is not null)
        {
            dc.PushClip(new RectangleGeometry(coverBox, 7, 7));
            dc.DrawImage(m.Cover, coverBox);
            dc.Pop();
            dc.DrawRoundedRectangle(null, PenOf(pal.Rim, 0.8, 0.5), coverBox, 7, 7);
        }
        else
        {
            dc.DrawRoundedRectangle(BrushOf(pal.SurfaceAlt), PenOf(pal.Rim, 0.8, 0.5), coverBox, 7, 7);
            Point dc0 = new(x + 14, cy);
            dc.DrawEllipse(Faint(accent, 0.22), PenOf(accent, 1.2, 0.8), dc0, 8.5, 8.5);
            dc.DrawEllipse(BrushOf(accent), null, dc0, 2.8, 2.8);
        }
        x += 28 + 10;

        if (m is null || !m.HasMedia)
        {
            var idle = Text("暂无正在播放的媒体", FontBody, pal.TextDim, false, ppd);
            dc.DrawText(idle, new Point(x, cy - idle.Height / 2));
            return x - r.X + idle.WidthIncludingTrailingWhitespace + 14;
        }

        // 右：5 根圆头实时音频律动柱（封面主色，与 compact-notch.png 完全一致）
        const int barCount = 5;
        const double barW = 3.2, barGap = 2.0, barMaxH = 18;
        double waveTotalW = barCount * barW + (barCount - 1) * barGap;
        double waveX = r.Right - 14 - waveTotalW;
        var bars = AudioBars;
        for (int i = 0; i < barCount; i++)
        {
            int srcIdx = bars is { Length: > 0 } ? Math.Min(bars.Length - 1, i * (bars.Length / barCount)) : 0;
            double v = m.IsPlaying && bars is { Length: > 0 }
                ? Math.Clamp(bars[srcIdx], 0.18, 1.0)
                : (0.22 + (i % 2) * 0.12);
            double bh = Math.Max(4, v * barMaxH);
            double bx = waveX + i * (barW + barGap);
            dc.DrawRoundedRectangle(BrushOf(accent), null,
                RenderKit.SafeRect(bx, cy - bh / 2, barW, bh), barW / 2, barW / 2);
        }

        // 中：歌曲标题 · 歌手
        double availTextW = Math.Max(40, waveX - 12 - x);
        string label = string.IsNullOrEmpty(m.Artist) ? m.Title : $"{m.Title} · {m.Artist}";
        var txt = Text(label, FontBody, pal.Text, true, ppd);
        if (txt.WidthIncludingTrailingWhitespace > availTextW)
            txt = Trimmed(label, FontBody, pal.Text, availTextW, true, ppd);
        dc.DrawText(txt, new Point(x, cy - txt.Height / 2));

        return MeasureMediaWidth(pal, ppd);
    }

    // ============ 5. Windows 扩展收起视图（monitor / devices / clipboard / window） ============

    private double DrawMonitor(DrawingContext dc, Palette pal, Rect r, double cy, MonitorSnapshot? snap)
    {
        if (snap is null || r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        double x = r.X + 14;

        var cores = snap.CoreUsages;
        if (cores is { Length: > 0 })
        {
            int n = Math.Min(16, cores.Length);
            double bw = 2.6, gap = 2, maxH = Math.Max(0, Math.Min(22, r.Height - 20));
            for (int i = 0; i < n; i++)
            {
                double v = Easing.Clamp(cores[i] / 100.0, 0, 1);
                dc.DrawRoundedRectangle(BrushOf(pal.Track), null,
                    RenderKit.SafeRect(x + i * (bw + gap), cy - maxH / 2, bw, maxH), 1.2, 1.2);
                double h = 1.6 + v * (maxH - 1.6);
                var c = LevelColor(v * 100, ResColor(Res.Cpu, pal), pal);
                dc.DrawRoundedRectangle(BrushOf(c), null,
                    RenderKit.SafeRect(x + i * (bw + gap), cy + maxH / 2 - h, bw, h), 1.2, 1.2);
            }
            x += n * (bw + gap) + 14;
        }

        const double dialSize = 20, dialFont = 12.5;
        x += DrawDialGauge(dc, pal, x, cy, ppd, snap.CpuUsage, ResColor(Res.Cpu, pal), "C", dialSize, dialFont) + 14;
        x += DrawDialGauge(dc, pal, x, cy, ppd, snap.MemoryUsagePercent, ResColor(Res.Mem, pal), "M", dialSize, dialFont) + 14;
        if (snap.GpuUsage > 0)
            x += DrawDialGauge(dc, pal, x, cy, ppd, snap.GpuUsage, ResColor(Res.Gpu, pal), "G", dialSize, dialFont) + 14;
        x += DrawDialGauge(dc, pal, x, cy, ppd, snap.DiskUsagePercent, ResColor(Res.Disk, pal), "D", dialSize, dialFont) + 14;

        var down = Text("↓" + MonitorSnapshot.FormatSpeedFixed(snap.DownloadSpeed), 11.5, pal.Down, true, ppd);
        var up = Text("↑" + MonitorSnapshot.FormatSpeedFixed(snap.UploadSpeed), 11.5, pal.Up, true, ppd);
        double bl = Math.Round(cy + 11.5 * 0.36);
        Baseline(dc, down, x, bl); x += down.WidthIncludingTrailingWhitespace + 8;
        Baseline(dc, up, x, bl); x += up.WidthIncludingTrailingWhitespace;
        return x - r.X + 14;
    }

    private static double DrawDialGauge(DrawingContext dc, Palette pal, double x, double yc, double ppd,
        double usage, Color arc, string letter, double dialSize, double font)
    {
        RenderKit.Ring(dc, new Point(x + dialSize / 2, yc), dialSize, Math.Max(1.8, dialSize * 0.13),
            usage / 100.0, pal.Track, arc);
        var lt = Text(letter, dialSize * 0.48, arc, true, ppd);
        dc.DrawText(lt, new Point(x + (dialSize - lt.Width) / 2, yc - lt.Height / 2));
        double at = x + dialSize + 5;
        var color = LevelColor(usage, pal.Text, pal);
        Baseline(dc, Text($"{Math.Round(usage)}%", font, color, true, ppd), at, Math.Round(yc + font * 0.36));
        double slot = Math.Ceiling(Text("100%", font, pal.Text, true, ppd).WidthIncludingTrailingWhitespace);
        return dialSize + 5 + slot;
    }

    private double DrawDevices(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        double x = r.X + 16;

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
            var empty = Text("未检测到无线设备电量（蓝牙/HID）", 11, pal.TextDim, ppd: ppd);
            dc.DrawText(empty, new Point(x, cy - empty.Height / 2));
            return empty.Width + 32;
        }

        foreach (var d in devices.Take(5))
        {
            var color = d.Charging ? DownC : d.Percent is >= 0 and <= 20 ? pal.Up : d.Percent is >= 0 and <= 40 ? pal.Warning : pal.Text;
            var glyph = Text(DeviceService.Glyph(d.Kind), 11, pal.TextDim, true, ppd);
            dc.DrawText(glyph, new Point(x, cy - glyph.Height / 2));
            x += glyph.Width + 5;
            string pctStr = d.Percent >= 0 ? $"{d.Percent}%" : (d.Charging ? "充电" : "--");
            var pct = Text(pctStr, 13, color, true, ppd);
            dc.DrawText(pct, new Point(x, cy - pct.Height / 2));
            x += pct.Width;
            if (d.Charging)
            {
                var bolt = Text("⚡", 9, DownC, ppd: ppd);
                dc.DrawText(bolt, new Point(x + 1, cy - pct.Height / 2 - 2));
                x += bolt.Width + 2;
            }
            x += 18;
        }
        return x - r.X - 18 + 16;
    }

    private double DrawClipboardHint(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        double ppd = Ppd;
        var last = ClipboardService.LastPreview;
        double x = r.X + 16;
        DrawIcon(dc, Glyph.ClipBox, x, cy, pal.Accent, 1.0);
        x += 18;
        string preview = string.IsNullOrEmpty(last) ? "剪贴板为空" : last;
        var previewColor = string.IsNullOrEmpty(last) ? pal.TextDim : pal.Text;
        var t = Text(preview, 11.5, previewColor, ppd: ppd);
        const double maxPreview = 260;
        if (t.Width > maxPreview)
            t = Trimmed(preview, 11.5, previewColor, maxPreview, false, ppd);
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

    private double DrawActiveWindow(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        double x = r.X + 16;
        dc.DrawEllipse(BrushOf(pal.Accent), null, new Point(x + 4, cy), 4, 4);
        x += 16;

        var (proc, title) = ActiveWindow ?? ("", "");
        string procText = string.IsNullOrEmpty(proc) ? "桌面" : proc;
        var p = Text(procText, 11.5, pal.Accent, true, ppd);
        dc.DrawText(p, new Point(x, cy - p.Height / 2));
        x += p.Width + 10;

        const double maxTitle = 300;
        string tt = string.IsNullOrEmpty(title) ? "" : title;
        var t = Text(tt, 11, pal.TextDim, ppd: ppd);
        if (t.Width > maxTitle)
            t = Trimmed(tt, 11, pal.TextDim, maxTitle, false, ppd);
        if (!string.IsNullOrEmpty(tt))
            dc.DrawText(t, new Point(x, cy - t.Height / 2));
        return x - r.X + (string.IsNullOrEmpty(tt) ? 0 : t.Width) + 16;
    }

    // ============ 5. 12×12 矢量图标库（与 macOS 版 BuildIcon/DrawIcon 一致，缓存冻结零分配） ============

    private enum Glyph
    {
        Cpu, Gpu, Mem, Down, Up, Battery, Disk, Ssd,
        Sun, Cloud, Rain, Snow, Fog, Bolt, ClipBox,
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Glyph, (StreamGeometry Stroke, bool HasStroke, StreamGeometry Fill, bool HasFill)> _iconGeos = new();

    private static double DrawIcon(DrawingContext dc, Glyph g, double x, double cy, Color color, double scale = 1.0)
    {
        var geos = _iconGeos.GetOrAdd(g, BuildIcon);
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, x, cy - 6 * scale));
        try
        {
            if (geos.HasStroke)
                dc.DrawGeometry(null, PenOf(color, 1.3 * scale), geos.Stroke);
            if (geos.HasFill)
                dc.DrawGeometry(BrushOf(color), null, geos.Fill);
        }
        finally
        {
            dc.Pop();
        }
        return 12 * scale;
    }

    private static (StreamGeometry Stroke, bool HasStroke, StreamGeometry Fill, bool HasFill) BuildIcon(Glyph g)
    {
        var stroke = new StreamGeometry();
        var fills = new StreamGeometry();
        bool hasStroke = false, hasFill = false;

        using (var sctx = stroke.Open())
        using (var fctx = fills.Open())
        {
            void Seg(double ax, double ay, double bx, double by)
            {
                hasStroke = true;
                sctx.BeginFigure(new Point(ax, ay), false, false);
                sctx.LineTo(new Point(bx, by), true, false);
            }
            void PolyLine(bool closed, params (double x, double y)[] pts)
            {
                if (pts.Length < 2) return;
                hasStroke = true;
                sctx.BeginFigure(new Point(pts[0].x, pts[0].y), false, closed);
                for (int i = 1; i < pts.Length; i++)
                    sctx.LineTo(new Point(pts[i].x, pts[i].y), true, false);
            }
            void Box(double x, double y, double w, double h, double r = 0)
            {
                hasStroke = true;
                if (r <= 0)
                {
                    sctx.BeginFigure(new Point(x, y), false, true);
                    sctx.LineTo(new Point(x + w, y), true, false);
                    sctx.LineTo(new Point(x + w, y + h), true, false);
                    sctx.LineTo(new Point(x, y + h), true, false);
                }
                else
                {
                    var rr = new Rect(x, y, w, h);
                    sctx.BeginFigure(new Point(rr.X + r, rr.Y), false, true);
                    sctx.LineTo(new Point(rr.Right - r, rr.Y), true, false);
                    sctx.ArcTo(new Point(rr.Right, rr.Y + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                    sctx.LineTo(new Point(rr.Right, rr.Bottom - r), true, false);
                    sctx.ArcTo(new Point(rr.Right - r, rr.Bottom), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                    sctx.LineTo(new Point(rr.X + r, rr.Bottom), true, false);
                    sctx.ArcTo(new Point(rr.X, rr.Bottom - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                    sctx.LineTo(new Point(rr.X, rr.Y + r), true, false);
                    sctx.ArcTo(new Point(rr.X + r, rr.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                }
            }
            void FRect(double x, double y, double w, double h)
            {
                hasFill = true;
                fctx.BeginFigure(new Point(x, y), true, true);
                fctx.LineTo(new Point(x + w, y), false, false);
                fctx.LineTo(new Point(x + w, y + h), false, false);
                fctx.LineTo(new Point(x, y + h), false, false);
            }
            void FPoly(params (double x, double y)[] pts)
            {
                if (pts.Length == 0) return;
                hasFill = true;
                fctx.BeginFigure(new Point(pts[0].x, pts[0].y), true, true);
                for (int i = 1; i < pts.Length; i++)
                    fctx.LineTo(new Point(pts[i].x, pts[i].y), false, false);
            }
            void FEllipse(double cx, double cy, double rx, double ry)
            {
                hasFill = true;
                fctx.BeginFigure(new Point(cx + rx, cy), true, true);
                fctx.ArcTo(new Point(cx - rx, cy), new Size(rx, ry), 0, true, SweepDirection.Clockwise, false, false);
                fctx.ArcTo(new Point(cx + rx, cy), new Size(rx, ry), 0, true, SweepDirection.Clockwise, false, false);
            }

            switch (g)
            {
                case Glyph.Cpu:
                    Box(3, 3, 6, 6);
                    FEllipse(6, 6, 1.4, 1.4);
                    foreach (var (ax, ay, bx, by) in new[]
                    {
                        (5.0, 1.0, 5.0, 3.0), (7.0, 1.0, 7.0, 3.0), (5.0, 9.0, 5.0, 11.0), (7.0, 9.0, 7.0, 11.0),
                        (1.0, 5.0, 3.0, 5.0), (1.0, 7.0, 3.0, 7.0), (9.0, 5.0, 11.0, 5.0), (9.0, 7.0, 11.0, 7.0),
                    })
                        Seg(ax, ay, bx, by);
                    break;
                case Glyph.Gpu:
                    Box(2.2, 2.2, 7.6, 7.6, 1.4);
                    FRect(3.8, 3.8, 4.4, 4.4);
                    FEllipse(6, 6, 1.1, 1.1);
                    break;
                case Glyph.Mem:
                    Box(1, 4, 10, 4);
                    for (int i = 0; i < 4; i++) Seg(2.5 + i * 2.2, 8, 2.5 + i * 2.2, 10);
                    break;
                case Glyph.Down:
                    Seg(6, 1, 6, 10);
                    PolyLine(false, (2.5, 6.5), (6, 10), (9.5, 6.5));
                    break;
                case Glyph.Up:
                    Seg(6, 11, 6, 2);
                    PolyLine(false, (2.5, 5.5), (6, 2), (9.5, 5.5));
                    break;
                case Glyph.Battery:
                    Box(1, 3, 9, 6);
                    FRect(10, 5, 1.5, 2);
                    FRect(2.2, 4.2, 4, 3.6);
                    break;
                case Glyph.Disk:
                    Box(1, 3, 10, 6);
                    Seg(1, 6, 11, 6);
                    break;
                case Glyph.Ssd:
                    Box(0.8, 3, 10.4, 6, 1);
                    FRect(2, 4.2, 2.4, 3.6);
                    FRect(5.1, 4.2, 2.4, 3.6);
                    Seg(8.9, 4.4, 8.9, 7.6);
                    Seg(10.3, 4.4, 10.3, 7.6);
                    break;
                case Glyph.Sun:
                {
                    var pts = new (double, double)[14];
                    for (int i = 0; i < 14; i++)
                    {
                        double a = i * Math.PI * 2 / 14;
                        pts[i] = (6 + Math.Cos(a) * 2.6, 6 + Math.Sin(a) * 2.6);
                    }
                    FPoly(pts);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        double c1 = Math.Cos(a), s1 = Math.Sin(a);
                        Seg(6 + c1 * 4.2, 6 + s1 * 4.2, 6 + c1 * 5.6, 6 + s1 * 5.6);
                    }
                    break;
                }
                case Glyph.Cloud:
                    FPoly((3.2, 5.2), (5.0, 5.2), (5.6, 4.4), (7.0, 4.4), (7.8, 5.3), (9.4, 5.3), (9.4, 8.4), (3.2, 8.4));
                    break;
                case Glyph.Rain:
                    FPoly((3.2, 6.0), (5.0, 6.0), (5.6, 5.2), (7.0, 5.2), (7.8, 6.1), (9.4, 6.1), (9.4, 8.6), (3.2, 8.6));
                    for (int i = 0; i < 3; i++) { double rx = 4.2 + i * 2.0; Seg(rx, 9.4, rx - 0.8, 11.2); }
                    break;
                case Glyph.Snow:
                    FPoly((3.2, 6.0), (5.0, 6.0), (5.6, 5.2), (7.0, 5.2), (7.8, 6.1), (9.4, 6.1), (9.4, 8.6), (3.2, 8.6));
                    FRect(3.2, 10.2, 0.8, 1.0);
                    FRect(5.7, 10.8, 0.8, 0.8);
                    FRect(8.2, 10.2, 0.8, 1.0);
                    break;
                case Glyph.Fog:
                    FPoly((3.2, 6.8), (5.0, 6.8), (5.6, 6.0), (7.0, 6.0), (7.8, 6.9), (9.4, 6.9), (9.4, 8.8), (3.2, 8.8));
                    for (int i = 0; i < 3; i++) { double yy = 9.6 + i * 0.8; Seg(3.2, yy, 9.2, yy); }
                    break;
                case Glyph.Bolt:
                    PolyLine(true, (7.2, 1.0), (3.4, 6.8), (6.2, 6.8), (4.8, 11.0), (8.6, 5.2), (5.8, 5.2));
                    break;
                case Glyph.ClipBox:
                    Box(1.6, 3.2, 8.8, 7.6, 1.2);
                    PolyLine(false, (4.2, 3.2), (4.2, 1.8), (7.8, 1.8), (7.8, 3.2));
                    break;
            }
        }
        stroke.Freeze();
        fills.Freeze();
        return (stroke, hasStroke, fills, hasFill);
    }
}

/// <summary>收起态临时活动（音量指示 / 高温告警等，参考 BrimDeck compact-alert）。</summary>
public sealed record ActivityView
{
    public string Kind { get; init; } = "volume";
    public string Label { get; init; } = "音量";
    public string? RightText { get; init; }
    public double Value { get; set; }
    public bool Muted { get; set; }
    public DateTime Start { get; set; } = DateTime.Now;
    public DateTime Until { get; set; } = DateTime.Now.AddSeconds(1.8);
}
