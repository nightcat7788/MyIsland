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
/// - 读数分级配色：≥70% Warning、≥90% Critical（Up），正常保持文本色；
/// - 小形象单独画在子级图层里（2026-11-24）：眼睛动画每帧只重画这一小块，不再牵动外壳重绘（见 InvalidatePet）。
/// </summary>
public sealed class IslandCanvas : FrameworkElement
{
    /// <summary>
    /// 当前胶囊外壳的四角半径（贴边侧随液桥收缩成"近方角"）。
    /// 视图自己画的胶囊底必须用同一套半径，否则两层轮廓不重合 ⇒ 贴边侧会露出一圈多余描边。
    /// </summary>
    private (double TL, double TR, double BR, double BL) _capsuleRadii;

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

    // dash 显示项过滤（null = 全部显示；宿主由 Settings.DashItems 注入）
    public HashSet<string>? DashItems { get; set; }
    private bool Want(string key) => DashItems is null || DashItems.Contains(key);

    // combo 信息胶囊显示项过滤（rings/dials/clock/net）
    public HashSet<string>? ComboItems { get; set; }
    private bool WantCombo(string key) => ComboItems is null || ComboItems.Contains(key);

    private readonly PetFace _pet = new();
    public PetFace Pet => _pet;

    // ---------- 小形象图层（2026-11-24 渲染分层） ----------
    // 小形象的眼睛按 24fps 连续动，外壳（液态玻璃）和其它输入却不动。以前每帧 InvalidateVisual()
    // 把整块画布重录一遍：640×124 的分层窗口每帧都要把外壳的几何/渐变/毛玻璃底连同内容重新栅格化。
    // 现在小形象单独画进子级 DrawingVisual，每帧只重录这一小块；外壳只在它自己的输入真的变了时才重画。
    // 离屏分层窗口探针实测（2026-11-24，pet-classic，300 帧 × 4 轮取中位数，进程绑定单核）：
    // 每帧 UI 线程的渲染通道 0.40ms → 0.17ms，每帧分配 31.1KB → 23.5KB。
    // ⚠️ 子级永远画在父级内容之上：小形象不该露面的状态（切到别的视图 / 展开 / 指示条 / 临时活动
    //    压在收起态内容上）必须显式把图层清空，否则旧的小形象会浮在新内容（如音量条）上面。
    private readonly DrawingVisual _petLayer = new();
    private bool _petLayerActive;          // 图层里此刻有小形象（false = 空绘制）
    private bool _petDrawnSinceRender;     // 上次整块重绘之后，InvalidatePet 已经直接重画过图层
    private static readonly Color _defaultEyeColor = Color.FromRgb(240, 244, 255);   // 深色主题下小形象的亮白线色

    /// <summary>小形象的输入快照（位置 + 影响表情的量）：两次快照相同，图层就不必重画。</summary>
    private (Point Center, Color Surface, Point Look, bool Near, string? Style, double Spread, double Tilt,
        double? CpuTemp, double DiskActive) _petInputs;

    /// <summary>
    /// 外壳输入的快照：整块重绘（OnRender）时记一份，InvalidatePet 每次比对，全等才敢只重画小形象。
    /// 这里列的都是普通 CLR 属性（不像 AffectsRender 的依赖属性会自动触发重绘）——宿主若只调
    /// InvalidatePet()，漏掉任何一个，外壳就会停在旧的一帧（弹簧运动中 / 首帧本体矩形刚注入 / 临时活动刚过期）。
    /// </summary>
    private (Rect Body, double Squash, double Neck, double Stretch, double Drip, string? Edge,
        bool Indicator, bool Mic, bool Overlay, ActivityView? Activity, Brush? Backdrop, double W, double H) _shellInputs;
    private bool _rendered;                // 至少整块重绘过一次（之前快照无意义）

    public IslandCanvas()
    {
        // AddVisualChild 只登记父子关系（DrawingVisual 不是 UIElement，布局/测量不碰它，所以不用 Remove）。
        // 少了这一步，命中测试从图层沿 GetParent 上溯不到本控件，鼠标事件的路由源就不是本控件了。
        AddVisualChild(_petLayer);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index)
        => index == 0 ? _petLayer : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>临时活动（音量/高温）此刻是否压在收起态内容上（带过期时间）。</summary>
    private bool OverlayVisible => Activity is { } a && DateTime.Now < a.Until;

    /// <summary>
    /// 此刻小形象该不该露面：pet 视图、收起、非指示条、没有临时活动压在上面，且本体矩形已注入。
    /// 最后一组条件与 OnRender 开头两处提前 return 同条件：外壳不画的时候，小形象更不能单独画出来
    ///（否则首帧会看到一双眼睛悬在窗口左上角）。
    /// </summary>
    private bool PetShown(bool overlay)
        => CompactView == "pet" && !IsExpanded && !IsIndicator && !overlay
           && !(ActualWidth <= 0 || ActualHeight <= 0 || BodyRect.Width <= 0 || BodyRect.Height <= 0);

    /// <summary>
    /// 小形象帧：只重画小形象图层，不重画外壳（宿主在 pet 视图、收起、弹簧静止时每帧调它）。
    /// 外壳的输入和上次整块重绘一致，才走"只重画图层"这条路；否则必须整块重绘，见方法内注释。
    /// </summary>
    public void InvalidatePet()
    {
        bool overlay = OverlayVisible;

        if (!_rendered || ShellInputs(overlay) != _shellInputs)
        {
            // 外壳的输入自上次整块重绘后变了（弹簧还在动 / 本体矩形刚注入 / 临时活动刚出现或刚过期）。
            // 这时不能只补画小形象：子级压在父级内容之上，旧外壳或过期的音量条还留在画面上，
            // 小形象会叠在它们上面。改为请求整块重绘，OnRender 会顺带把小形象同步进图层。
            InvalidateVisual();
            return;
        }

        if (!PetShown(overlay))
        {
            // 小形象此刻不该露面（视图不是 pet / 展开 / 指示条 / 临时活动在前）：图层保持空白。
            ClearPetLayer();
            return;
        }

        DrawPetLayer(Palette ?? Palette.Dark);
    }

    /// <summary>
    /// 整块重绘时同步小形象图层：该露面就补画，不该露面就清空。
    /// 刚被 InvalidatePet 用同一组输入直接重画过的不重画——宿主在弹簧运动期间会同时调
    /// InvalidateVisual() + InvalidatePet()，这样一帧里小形象只前进一次
    ///（PetFace.Draw 每调一次就按真实时间推一步弹簧，一帧推两步等于让眼睛走得偏快）。
    /// </summary>
    private void SyncPetLayer(Palette pal, bool overlay)
    {
        _shellInputs = ShellInputs(overlay);
        _rendered = true;

        if (!PetShown(overlay))
        {
            ClearPetLayer();
            return;
        }

        if (!_petLayerActive || !_petDrawnSinceRender || PetInputs(pal) != _petInputs)
            DrawPetLayer(pal);
        _petDrawnSinceRender = false;
    }

    private (Rect Body, double Squash, double Neck, double Stretch, double Drip, string? Edge,
        bool Indicator, bool Mic, bool Overlay, ActivityView? Activity, Brush? Backdrop, double W, double H)
        ShellInputs(bool overlay)
        => (BodyRect, Squash, NeckStrength, NeckStretch, DripRadius, Edge,
            IsIndicator, MicActive, overlay, Activity, BackdropBrush, ActualWidth, ActualHeight);

    private (Point Center, Color Surface, Point Look, bool Near, string? Style, double Spread, double Tilt,
        double? CpuTemp, double DiskActive) PetInputs(Palette pal)
    {
        var r = SqueezedBody();
        return (new Point(r.X + r.Width / 2, r.Y + r.Height / 2), pal.Surface, LookPoint, PointerNear,
            _pet.Style, _pet.Spread, _pet.Tilt, _pet.CpuTemp, _pet.DiskActive);
    }

    /// <summary>
    /// 本体矩形按挤压（Squash）缩放后的结果：高度按 Squash 缩放，保持中心。
    /// 外壳、收起态内容、小形象图层共用这一个算法，三者才在同一套坐标上（挤压时小形象跟着本体居中）。
    /// </summary>
    private Rect SqueezedBody()
    {
        var r = BodyRect;
        double squeezed = r.Height * Easing.Clamp(Squash, 0.5, 1.2);
        return RenderKit.SafeRect(r.X, r.Y + (r.Height - squeezed) / 2, r.Width, squeezed);
    }

    /// <summary>把小形象重画进图层：替换的只有 _petLayer 这一个子级的绘制内容，外壳的绘制内容不动。</summary>
    private void DrawPetLayer(Palette pal)
    {
        var r = SqueezedBody();
        using (var dc = _petLayer.RenderOpen())
        {
            _pet.SurfaceColor = pal.Surface;   // 眼睑遮罩跟随主题
            _pet.EyeColor = pal.IsDark ? _defaultEyeColor : pal.Text;   // 浅色主题下亮白的眼睛会融进玻璃里
            _pet.Draw(dc, r.X + r.Width / 2, r.Y + r.Height / 2, 1.0, 1, LookPoint, PointerNear);
        }
        _petInputs = PetInputs(pal);
        _petLayerActive = true;
        _petDrawnSinceRender = true;
    }

    /// <summary>图层清空（空绘制）：小形象不该露面的每个转场都走这里，不清就是旧画面残留在新内容之上。</summary>
    private void ClearPetLayer()
    {
        if (!_petLayerActive) return;
        using (_petLayer.RenderOpen()) { }
        _petLayerActive = false;
    }

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

    // ---------- 主渲染 ----------
    protected override void OnRender(DrawingContext dc)
    {
        var pal = Palette ?? Palette.Dark;
        // 小形象不在这里画：它在子级图层里（见 InvalidatePet）。整块重绘时顺手把图层同步到当前状态。
        // "临时活动是否在前"整块重绘只看这一次表：图层的取舍与下面"画活动条还是画收起态内容"
        // 必须基于同一时刻，否则恰好卡在过期瞬间会出一帧"音量条上叠着小形象"的画面。
        bool overlay = OverlayVisible;
        SyncPetLayer(pal, overlay);

        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        // 宿主还没注入本体矩形时（窗口首帧 Arrange 早于第一帧定时器）直接不画：
        // 此时所有子元素的宽度/高度都会算成负数或零，画出来是错位且会抛异常
        if (BodyRect.Width <= 0 || BodyRect.Height <= 0) return;

        // 挤压：高度按 Squash 缩放，保持中心（小形象图层用的是同一个矩形，见 SqueezedBody）
        var r = SqueezedBody();
        _bodyNow = r;

        // 形态：收起=全胶囊（圆角=高度一半）；展开=26px 连续圆角面板（wisland 玻璃面板同款）
        double corner = pal.SquareCorners ? Math.Min(3, r.Height / 2)
            : IsExpanded ? 26 : r.Height / 2;
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

        _capsuleRadii = radii;

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
        // ② 主题色压层：降到 0.58（收起）让模糊底透出来；展开 0.74（玻璃面板感，卡片保可读）
        dc.DrawGeometry(Faint(pal.Surface, IsExpanded ? 0.74 : 0.58), PenOf(pal.Rim, 1.0, 0.5), geo);
        // ③ 顶部 sheen 高光带：白 12% → 45% 高度处消隐（液态玻璃的顶部反光）
        if (!pal.SquareCorners && !IsIndicator && r.Width > 24 && r.Height > 16)
        {
            // 缓存的静态画刷（RenderKit.SheenBrush）：每帧新建渐变画刷与"每帧零分配"的设计声明不符
            var sheen = RenderKit.SheenBrush;
            // 高光带的顶角跟随外壳（贴边侧是近方角），否则角上会露出一条与外壳不贴合的亮月牙
            var sheenGeo = LiquidGeometry.RoundedRect(
                RenderKit.SafeRect(r.X + 1.5, r.Y + 1, Math.Max(4, r.Width - 3), Math.Max(2, r.Height * 0.45)),
                radii.TL, radii.TR, 0, 0);
            dc.DrawGeometry(sheen, null, sheenGeo);
        }
        // ④ 底部内阴影（玻璃厚度感：底部 18% 处压一条暗带）
        if (!pal.SquareCorners && !IsIndicator && r.Width > 24 && r.Height > 16)
        {
            var shade = RenderKit.ShadeBrush;
            var shadeGeo = LiquidGeometry.RoundedRect(
                RenderKit.SafeRect(r.X + 1.5, r.Y + r.Height * 0.62, Math.Max(4, r.Width - 3), Math.Max(2, r.Height * 0.36)),
                0, 0, radii.BR, radii.BL);
            dc.DrawGeometry(shade, null, shadeGeo);
        }
        // ⑤ 高光边缘（rim light）：顶部 1px 渐隐白边，深主题下给胶囊玻璃厚度
        if (pal.IsDark && !pal.SquareCorners)
            dc.DrawGeometry(null, RenderKit.RimLightPen, geo);

        // ⑥ 顶部辉光 + 细网格（2026-11-24 视觉重做，暗色科技）：深色主题的本体顶部中央透出一团强调色，
        //    展开面板里再铺一层细网格，线本身就是径向渐变（离辉光中心越远越淡，相当于给网格加了径向遮罩）。
        //    像素主题不铺网格（它的语言是点阵，不是线框）；指示条太小，不点灯。
        //    读数类视图（读数带 / 信息胶囊 / 仪表盘 / 设备 / 活动窗口）会在下面再压一层底色，把这束光盖住，
        //    所以它们的辉光由 DrawCapsuleBase 画在压层之上，这里不画（否则叠两遍）。
        //    这一段只在外壳重绘时画（静止时不重绘），不给常驻帧率添成本。
        bool veiled = CompactView is "monitor" or "combo" or "dash" or "devices" or "window";
        if (pal.IsDark && !IsIndicator && r.Width > 60 && !(veiled && !IsExpanded))
        {
            dc.PushClip(geo);
            bool expanded = IsExpanded && r.Height > 80;
            dc.DrawRectangle(RenderKit.Spotlight(pal.Accent, expanded ? 0.20 : 0.15, expanded ? 0.95 : 1.5), null,
                RenderKit.SafeRect(r.X, r.Y, r.Width, r.Height));
            if (expanded && !pal.SquareCorners)
            {
                var fade = new RadialGradientBrush
                {
                    MappingMode = BrushMappingMode.Absolute,
                    Center = new Point(r.X + r.Width / 2, r.Y), GradientOrigin = new Point(r.X + r.Width / 2, r.Y),
                    RadiusX = r.Width * 0.62, RadiusY = r.Height * 1.05,
                };
                fade.GradientStops.Add(new GradientStop(RenderKit.Halo(pal.Text, 0.075), 0));
                fade.GradientStops.Add(new GradientStop(RenderKit.Halo(pal.Text, 0), 1));
                var pen = new Pen(fade, 1);
                // 1px 的线落在像素中线上才清晰；从本体里侧起排，避免贴边的半格
                for (double gx = Math.Round(r.X + 22) + 0.5; gx < r.Right - 8; gx += 22)
                    dc.DrawLine(pen, new Point(gx, r.Top), new Point(gx, r.Bottom));
                for (double gy = Math.Round(r.Y + 22) + 0.5; gy < r.Bottom - 8; gy += 22)
                    dc.DrawLine(pen, new Point(r.Left, gy), new Point(r.Right, gy));
            }
            dc.Pop();
        }

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
            if (overlay && Activity is { } act)
                DrawActivity(dc, pal, r, act);
            else
            {
                // 变形中（刚切到更宽 / 更高的视图，本体还在长）：内容按"最终的本体矩形"排版，裁进当前的本体 ——
                // 胶囊长大时把最终的版面从中间揭开。以前内容按当前矩形排：信息胶囊的网速块贴着当前右缘、
                // 与表盘叠成一团，装不下的部分直接溢出胶囊外面（小形象 10 秒后变形的那一下，每 40 秒出现一次；
                // 2026-11-24 修复）。只管"变大"：缩小时内容本来就装得下，走原来的路径不变。
                // 本体静止时最终矩形 = 当前矩形，同样走原来的路径 —— 画面与修复前逐像素相同。
                var final = FinalBodyRect?.Invoke() ?? Rect.Empty;
                if (final.Width > BodyRect.Width + 0.5 || final.Height > BodyRect.Height + 0.5)
                {
                    dc.PushClip(geo);
                    DrawCompact(dc, pal, final);
                    dc.Pop();
                }
                else
                    DrawCompact(dc, pal, r);
            }
        }
    }

    /// <summary>
    /// 收起态"本体要去的地方"：弹簧目标值对应的本体矩形（窗口 DIP），由宿主提供；离屏渲染 / 测试里为 null。
    /// 每次渲染时现取 —— 宿主在同一次操作里切视图、改弹簧目标，紧接着的那一帧渲染拿到的就是新目标，
    /// 不需要宿主逐帧推送。返回空矩形 = 不适用（宿主正在做展开动画）。见 OnRender 的"变形中"。
    /// </summary>
    public Func<Rect>? FinalBodyRect { get; set; }

    /// <summary>这一帧的本体矩形（挤压后）。读数类视图的压层始终按它画：变形中内容按最终矩形排版，压层不变。</summary>
    private Rect _bodyNow;

    /// <summary>
    /// 读数类视图（读数带 / 信息胶囊 / 仪表盘 / 设备 / 活动窗口）自己的胶囊底：在玻璃上再压一层底色，保证小字可读。
    /// 压层颜色跟主题走（2026-11-24 视觉重做）。以前这里写死 GitHub Dark 的 #0D1117：浅色主题下这几个视图
    /// 变成灰黑色带，和同一主题里的白色玻璃小形象 / 时钟不是一套材质。
    /// ⚠️ 必须与外壳（液态玻璃）用同一套四角半径：外壳在贴边侧是"近方角"（随液桥收缩），
    ///    这里若还用全圆角，两层轮廓不重合 ⇒ 贴边侧会露出一圈多余描边，看起来像叠了两层背景。
    /// 按当前的本体矩形（_bodyNow）画，不用各视图排版用的那个矩形：变形中内容按最终矩形排版，压层要一直盖住整个本体。
    /// </summary>
    private void DrawCapsuleBase(DrawingContext dc, Palette pal)
    {
        var geo = LiquidGeometry.RoundedRect(_bodyNow, _capsuleRadii.TL, _capsuleRadii.TR, _capsuleRadii.BR, _capsuleRadii.BL);
        dc.DrawGeometry(Faint(pal.IsDark ? pal.Surface : Colors.White, 0.62), null, geo);
        // 顶部辉光画在压层之上（深色主题；见 OnRender ⑥）
        if (pal.IsDark)
            dc.DrawGeometry(RenderKit.Spotlight(pal.Accent, 0.15, 1.5), null, geo);
    }

    /// <summary>中性读数的颜色：文字色降到 60% 不透明度（深色主题是灰白，像素主题是暗琥珀——整页保持单色）。</summary>
    private static Color Neutral(Palette pal) => Color.FromArgb(0x99, pal.Text.R, pal.Text.G, pal.Text.B);

    /// <summary>读数（数字等宽，见 RenderKit.Num）：按基线放置，返回右端 x。</summary>
    private static double Num(DrawingContext dc, string s, double x, double baseline, double size, Color color, bool strong, double ppd)
        => RenderKit.Num(dc, s, x, baseline, size, color, strong, ppd);

    /// <summary>紧凑网速单行（↓3.40M ↑420K）占用的宽度：箭头 + 3 + 读数 + 10 + 箭头 + 3 + 读数。</summary>
    private static double NetWidth(double size, MonitorSnapshot snap, double ppd)
        => Text("↓", size, Colors.Black, true, ppd).Width + 3
         + RenderKit.NumWidth(MonitorSnapshot.CompactSpeed(snap.DownloadSpeed), size, true, ppd) + 10
         + Text("↑", size, Colors.Black, true, ppd).Width + 3
         + RenderKit.NumWidth(MonitorSnapshot.CompactSpeed(snap.UploadSpeed), size, true, ppd);

    /// <summary>紧凑网速单行：箭头是次要文字色，读数是等宽数字。返回右端 x。</summary>
    private static double DrawNet(DrawingContext dc, Palette pal, double x, double bl, double size, MonitorSnapshot snap, double ppd)
    {
        var di = Text("↓", size, pal.TextDim, true, ppd);
        Baseline(dc, di, x, bl); x += di.Width + 3;
        x = Num(dc, MonitorSnapshot.CompactSpeed(snap.DownloadSpeed), x, bl, size, pal.Text, true, ppd) + 10;
        var ui = Text("↑", size, pal.TextDim, true, ppd);
        Baseline(dc, ui, x, bl); x += ui.Width + 3;
        return Num(dc, MonitorSnapshot.CompactSpeed(snap.UploadSpeed), x, bl, size, pal.Text, true, ppd);
    }

    /// <summary>温度的显示色：正常是文字色（不着色），71–85° 警示，&gt;85° 危险；读不到是第三级文字色。</summary>
    private static Color TempColor(double? t, Palette pal) => t switch
    {
        null => pal.TextTertiary,
        <= 70 => pal.Text,
        <= 85 => pal.Warning,
        _ => pal.Up,
    };

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
        // 数值条：本体宽度还没量出来时（首帧 BodyRect 为空）w 会是负数，
        // 负宽度的矩形会让 WPF 抛"宽度和高度必须为非负值"直接把整窗带走 ⇒ 先夹住
        double x = r.X + 44, w = Math.Max(0, r.Width - 70 - 44);
        var enter = Easing.EaseOut((DateTime.Now - act.Start).TotalSeconds / 0.3);
        double filled = Math.Min(Math.Max(4, w * act.Value * enter), w);
        dc.DrawRoundedRectangle(BrushOf(pal.SurfaceAlt), null, RenderKit.SafeRect(x, cy - 3, w, 6), 3, 3);
        dc.DrawRoundedRectangle(BrushOf(pal.Text), null, RenderKit.SafeRect(x, cy - 3, filled, 6), 3, 3);
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
                // 小形象画在自己的图层里（_petLayer，见 InvalidatePet / SyncPetLayer），这里只回报宽度。
                // 照旧报 110：宿主按它定岛体尺寸，分层前后不变。
                w = 110;
                break;
            case "clock":
                w = DrawClock(dc, pal, r, cy, Snapshot);
                break;
            case "monitor":
                w = DrawMonitor(dc, pal, r, cy, Snapshot);
                break;
            case "combo":
                w = DrawCombo(dc, pal, r, cy, Snapshot, Thermal);
                break;
            case "dash":
                w = DrawDash(dc, pal, r, cy, Snapshot, Thermal);
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
    /// monitor 视图（单排高密度读数带）：细核心柱群（≤16 核）+ C/M/G/D 表盘读数 + 紧凑网速。
    /// 配色与展开后的监控页同一套层级（2026-11-24 视觉重做）：CPU 是强调色，其余表盘是中性色，
    /// 只有 ≥70% / ≥90% 才换成状态色。以前每个指标一种写死的颜色，9 套主题的收起态只差底色。
    /// </summary>
    private double DrawMonitor(DrawingContext dc, Palette pal, Rect r, double cy,
        MonitorSnapshot? snap)
    {
        if (snap is null) return 0;
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        DrawCapsuleBase(dc, pal);

        Color neutral = Neutral(pal);
        double x = r.X + 14;

        // ---- 细核心柱群（2.6px 细柱、2px 间距、最多 16 核，垂直居中） ----
        var cores = snap.CoreUsages;
        if (cores is not null && cores.Length > 0)
        {
            int n = Math.Min(16, cores.Length);
            double bw = 2.6, gap = 2, maxH = Math.Max(0, Math.Min(24, r.Height - 18));
            for (int i = 0; i < n; i++)
            {
                double v = Easing.Clamp(cores[i] / 100.0, 0, 1);
                dc.DrawRoundedRectangle(BrushOf(pal.Track), null,
                    RenderKit.SafeRect(x + i * (bw + gap), cy - maxH / 2, bw, maxH), 1.2, 1.2);
                double h = 1.6 + v * (maxH - 1.6);
                dc.DrawRoundedRectangle(BrushOf(LevelColor(v * 100, pal.Accent, pal)), null,
                    RenderKit.SafeRect(x + i * (bw + gap), cy + maxH / 2 - h, bw, h), 1.2, 1.2);
            }
            x += n * (bw + gap) + 14;
        }

        // ---- 表盘读数（数字按 "100%" 预留，跳数不动布局） ----
        const double dialSize = 20, dialFont = 12.5;
        x += DrawDialGauge(dc, pal, x, cy, ppd, snap.CpuUsage, pal.Accent, "C", dialSize, dialFont) + 16;
        x += DrawDialGauge(dc, pal, x, cy, ppd, snap.MemoryUsagePercent, neutral, "M", dialSize, dialFont) + 16;
        if (snap.GpuUsage > 0)
            x += DrawDialGauge(dc, pal, x, cy, ppd, snap.GpuUsage, neutral, "G", dialSize, dialFont) + 16;
        x += DrawDialGauge(dc, pal, x, cy, ppd, snap.DiskUsagePercent, neutral, "D", dialSize, dialFont) + 16;

        // ---- 紧凑网速 ----
        x = DrawNet(dc, pal, x, Math.Round(cy + 11 * 0.38), 11, snap, ppd);

        return x - r.X + 14;
    }

    /// <summary>
    /// combo 信息胶囊（参考 BrimDeck 刘海轮播态）：时间 + 系统读数表盘 + 温度/硬盘读数 + 网速。
    /// 小形象视图 10 秒后自动变形为此视图，30 秒后变回（IslandWindow 的 pet 轮换）。
    /// </summary>
    private double DrawCombo(DrawingContext dc, Palette pal, Rect r, double cy,
        MonitorSnapshot? snap, Services.ThermalSnapshot? thermal)
    {
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        DrawCapsuleBase(dc, pal);
        double x = r.X + 14;
        Color neutral = Neutral(pal);

        // ① 时间（按样本 "00:00" 预留宽度，分针跳变不推挤右侧读数；数字等宽）
        if (WantCombo("clock"))
        {
            Num(dc, DateTime.Now.ToString("HH:mm"), x, Math.Round(cy + 15 * 0.36), 16, pal.Text, true, ppd);
            x += Math.Ceiling(RenderKit.NumWidth("00:00", 16, true, ppd)) + 16;
        }

        // ② 系统读数表盘（开口环 + 字母）：CPU 强调色，其余中性色
        if (WantCombo("dials") && snap is not null)
        {
            x += DrawDialGauge(dc, pal, x, cy, ppd, snap.CpuUsage, pal.Accent, "C", 20, 12.5) + 16;
            x += DrawDialGauge(dc, pal, x, cy, ppd, snap.MemoryUsagePercent, neutral, "M", 20, 12.5) + 16;
            if (snap.GpuUsage > 0)
                x += DrawDialGauge(dc, pal, x, cy, ppd, snap.GpuUsage, neutral, "G", 20, 12.5) + 16;
        }

        // ③ 温度与硬盘：和上面的 C/M/G 表盘同一套环（环内字母=设备，数字带单位）
        //    温度环的弧与数字按分级配色（正常 / 警示 / 危险），读不到就是空环 + "—"
        if (snap is not null)
        {
            double? ct = thermal?.CpuTemp, gt = thermal?.GpuTemp;
            if (WantCombo("cputemp"))
                x += DrawDialGauge(dc, pal, x, cy, ppd, ct, neutral, "C", 20, 12.5,
                    fill: ct is { } t1 ? Math.Clamp(t1 / 125.0, 0, 1) : null,
                    text: ct is { } t2 ? $"{Math.Round(t2)}°" : "—",
                    textColor: TempColor(ct, pal),
                    sample: "125°") + 16;
            if (WantCombo("gputemp"))
                x += DrawDialGauge(dc, pal, x, cy, ppd, gt, neutral, "G", 20, 12.5,
                    fill: gt is { } t3 ? Math.Clamp(t3 / 125.0, 0, 1) : null,
                    text: gt is { } t4 ? $"{Math.Round(t4)}°" : "—",
                    textColor: TempColor(gt, pal),
                    sample: "125°") + 16;
            if (WantCombo("disk"))
                x += DrawDialGauge(dc, pal, x, cy, ppd, snap.DiskUsagePercent, neutral, "D", 20, 12.5) + 16;
        }

        // ④ 右缘：紧凑网速单行（先量宽度，才能贴右缘放置）
        double netBlock = 0;
        if (WantCombo("net") && snap is not null)
        {
            const double size = 11;
            double nw = NetWidth(size, snap, ppd);
            DrawNet(dc, pal, r.Right - 14 - nw, Math.Round(cy + size * 0.38), size, snap, ppd);
            netBlock = nw + 14;
        }

        // 内容实测宽 = 左侧流（时间+表盘）+ 右侧网速块（无则只留右 padding）
        return x - r.X + (netBlock > 0 ? 12 + netBlock : 14);
    }

    /// <summary>读数表盘 + 右侧数字：返回占用宽度（数字按最宽样本预留，数字等宽）。
    /// 环 20px，数字 12.5px 粗体，环与数字间距 5px。
    ///
    /// fill / text / textColor / sample 可覆盖：
    /// - 温度环的填充比例与显示数值不同（环按 0–125℃ 填，数字是 "62°"）；
    /// - textColor 给出时环和数字都用它（温度按分级配色），否则都按占用率分级（≥70% 警示、≥90% 危险）。
    /// 这样"占用率环"与"温度环/硬盘环"是同一套视觉语言。</summary>
    private double DrawDialGauge(DrawingContext dc, Palette pal, double x, double yc, double ppd,
        double? usage, Color arc, string letter, double dialSize, double font,
        double? fill = null, string? text = null, Color? textColor = null, string sample = "100%")
    {
        // 环用 arc（按占用率分级），数字用文字色（按占用率分级）；给了 textColor 就两者都用它
        Color arcColor = textColor ?? (usage is { } a ? LevelColor(a, arc, pal) : arc);
        Color color = textColor ?? (usage is { } u ? LevelColor(u, pal.Text, pal) : pal.TextTertiary);
        Dial(dc, new Point(x + dialSize / 2, yc), dialSize, Math.Max(1.6, dialSize * 0.11),
            fill ?? (usage is { } f ? f / 100 : null), pal.Track, arcColor, pal.Text, letter, ppd);
        double at = x + dialSize + 5;
        double bl = Math.Round(yc + font * 0.36);
        string figure = text ?? (usage is { } u1 ? $"{Math.Round(u1)}%" : "—");
        if (figure == "—") Baseline(dc, Text(figure, font, color, true, ppd), at, bl);
        else Num(dc, figure, at, bl, font, color, true, ppd);
        double slot = Math.Ceiling(RenderKit.NumWidth(sample, font, true, ppd));
        return dialSize + 5 + slot;
    }


    /// <summary>
    /// dash 视图（用户定稿结构）：等距的「标签 + 读数」，核心柱带打头。
    /// 每组宽度按最宽样本预留（跳数不挤压邻组）；读数等宽；用量读数按 70/90 分级配色。
    /// 标签一律第三级文字色（2026-11-24 视觉重做：以前每个标签一种颜色，一行里五六个色相）。
    /// </summary>
    private double DrawDash(DrawingContext dc, Palette pal, Rect r, double cy,
        MonitorSnapshot? snap, Services.ThermalSnapshot? thermal)
    {
        if (snap is null) return 0;
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        DrawCapsuleBase(dc, pal);

        double x = r.X + 14;

        // 每组：标签上、数值下；宽度 = max(标签, 数值, 样本)，样本保证跳数时组距不动
        void Item(string label, string value, string sample, Color? valueColor = null)
        {
            var icon = Text(label, 9.5, pal.TextTertiary, ppd: ppd);
            dc.DrawText(icon, new Point(x, cy - 14));
            Num(dc, value, x, cy + 15, 13, valueColor ?? pal.Text, true, ppd);
            x += Math.Max(RenderKit.NumWidth(sample, 13, true, ppd), icon.Width) + 26;
        }

        // ① 核心柱（等距各组之首，强调色；轨道+填充）
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
                    RenderKit.SafeRect(x + i * (bw + gap), baseY - maxH, bw, maxH), 1.6, 1.6);
                double h = 2 + v * maxH;
                dc.DrawRoundedRectangle(BrushOf(LevelColor(v * 100, pal.Accent, pal)), null,
                    RenderKit.SafeRect(x + i * (bw + gap), baseY - h, bw, h), 1.6, 1.6);
            }
            x += n * (bw + gap) + 26;
        }

        // ② CPU 温度 ③ GPU 温度 ④ 内存 ⑤ GPU% ⑥ 硬盘 ⑦ SSD 温度 ⑧ 电池 ⑨ 网速
        double? ct = thermal?.CpuTemp, gt = thermal?.GpuTemp;
        if (Want("cputemp")) Item("CPU", ct is { } c1 ? $"{Math.Round(c1)}°" : "--", "125°", TempColor(ct, pal));
        if (Want("gputemp")) Item("GPU", gt is { } g1 ? $"{Math.Round(g1)}°" : "--", "125°", TempColor(gt, pal));
        if (Want("mem"))
            Item("MEM", $"{snap.MemoryUsagePercent:F0}%", "100%", LevelColor(snap.MemoryUsagePercent, pal.Text, pal));
        // GPU% 常显（核显空闲=0 也占位，避免岛体宽度随负载跳动）
        if (Want("gpu"))
            Item("GPU%", $"{snap.GpuUsage:F0}%", "100%", LevelColor(snap.GpuUsage, pal.Text, pal));
        if (Want("disk"))
            Item("盘", $"{snap.DiskUsagePercent:F0}%", "100%", LevelColor(snap.DiskUsagePercent, pal.Text, pal));
        // SSD 温度（读不到显示 "--"）
        if (Want("ssd"))
        {
            double? st = thermal?.SsdTemp;
            Item("SSD", st is { } s1 ? $"{Math.Round(s1)}°" : "--", "125°", TempColor(st, pal));
        }
        // 本机电池（DeviceService 已在采；有电池才显示）
        var bat = DeviceService.Snapshot.FirstOrDefault(d => d.Kind == "battery");
        if (Want("battery") && bat is not null)
        {
            Color bc = bat.Charging ? pal.Down
                   : bat.Percent <= 20 ? pal.Up
                   : bat.Percent <= 40 ? pal.Warning
                   : pal.Text;
            Item(bat.Charging ? "电+" : "电", $"{bat.Percent}%", "100%", bc);
        }
        if (Want("net"))
        {
            Item("↓", MonitorSnapshot.CompactSpeed(snap.DownloadSpeed), "9.99M");
            Item("↑", MonitorSnapshot.CompactSpeed(snap.UploadSpeed), "9.99M");
        }
        // 内容实测宽 = 已排内容（含末尾组距 26，扣掉）+ 右留白 14
        return x - r.X - 26 + 14;
    }

    /// <summary>设备电量行（BrimDeck compact-devices 同款：文件数 + 设备图标 + 大号分级配色百分比）。</summary>
    private double DrawDevices(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        DrawCapsuleBase(dc, pal);

        double x = r.X + 16;

        // 文件中转计数（有文件才显示）
        int files = FileTrayService.Count;
        if (files > 0)
        {
            var fi = Text("文", 11, pal.TextDim, true, ppd);
            dc.DrawText(fi, new Point(x, cy - fi.Height / 2));
            x += fi.Width + 4;
            x = Num(dc, $"{files}", x, Math.Round(cy + 5), 13, pal.Text, true, ppd) + 20;
        }

        var devices = DeviceService.Snapshot;
        if (devices.Count == 0 && files == 0)
        {
            var empty = Text("未检测到设备电量", 11, pal.TextDim, ppd: ppd);
            dc.DrawText(empty, new Point(x, cy - empty.Height / 2));
            return empty.Width + 32;
        }

        foreach (var d in devices.Take(5))
        {
            // 分级配色：充电绿 / ≤20 红 / ≤40 橙 / 其余文字色
            var color = d.Charging ? pal.Down
                      : d.Percent <= 20 ? pal.Up
                      : d.Percent <= 40 ? pal.Warning
                      : pal.Text;
            // 设备图标（次要文字色）+ 大号百分比（分级色，粗体，数字等宽）
            var glyph = Text(DeviceService.Glyph(d.Kind), 11, pal.TextDim, true, ppd);
            dc.DrawText(glyph, new Point(x, cy - glyph.Height / 2));
            x += glyph.Width + 5;
            x = Num(dc, $"{d.Percent}%", x, Math.Round(cy + 13 * 0.36), 13, color, true, ppd);
            // 充电小闪电（矢量图标）：颜色之外再给一个形状线索，不只靠绿色区分"充电中"
            if (d.Charging)
            {
                Glyphs.Draw(dc, GlyphKind.Bolt, RenderKit.SafeRect(x + 1, cy - 8, 9, 9), pal.Down);
                x += 11;
            }
            x += 18;
        }
        // 内容实测宽 = 已排内容（扣末尾组距 18）+ 右留白 16
        return x - r.X - 18 + 16;
    }

    /// <summary>剪贴板提示行：图标 + 最近一条内容预览。</summary>
    private double DrawClipboardHint(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        double ppd = Ppd;
        var last = ClipboardService.LastPreview;
        bool empty = string.IsNullOrEmpty(last);
        double x = r.X + 16;
        Glyphs.Draw(dc, GlyphKind.Clipboard, RenderKit.SafeRect(x, cy - 7.5, 15, 15), empty ? pal.TextDim : pal.Text);
        x += 23;
        // 预览超长时截断（内容宽度上限 260，避免剪贴板长文本撑爆岛体）
        string preview = empty ? "剪贴板为空" : last;
        var previewColor = empty ? pal.TextDim : pal.Text;
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
            string cs = $"{count} 条";
            double cw = RenderKit.NumWidth(cs, 10, false, ppd);
            Num(dc, cs, r.Right - 14 - cw, Math.Round(cy + 4), 10, pal.TextDim, false, ppd);
            countBlock = cw + 14;
        }
        return x - r.X + (countBlock > 0 ? 12 + countBlock : 14);
    }

    /// <summary>活动窗口视图：强调色圆点（"正在跟随前台窗口"）+ 进程名 + 窗口标题，超长截断。</summary>
    private double DrawActiveWindow(DrawingContext dc, Palette pal, Rect r, double cy)
    {
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        DrawCapsuleBase(dc, pal);

        double x = r.X + 16;
        dc.DrawEllipse(BrushOf(pal.Accent), null, new Point(x + 4, cy), 4, 4);
        x += 16;

        var (proc, title) = ActiveWindow ?? ("", "");
        string procText = string.IsNullOrEmpty(proc) ? "桌面" : proc;
        var p = Text(procText, 11.5, pal.Text, true, ppd);
        dc.DrawText(p, new Point(x, cy - p.Height / 2));
        x += p.Width + 10;

        const double maxTitle = 300;
        string tt = string.IsNullOrEmpty(title) ? "" : title;
        var t = Text(tt, 11, pal.TextDim, ppd: ppd);
        if (t.Width > maxTitle)
            t = RenderKit.Trimmed(tt, 11, pal.TextDim, maxTitle, false, ppd);
        if (!string.IsNullOrEmpty(tt))
            dc.DrawText(t, new Point(x, cy - t.Height / 2));
        return x - r.X + (string.IsNullOrEmpty(tt) ? 0 : t.Width) + 16;
    }

    /// <summary>时钟视图：左时间，中天气（已接线），右上下行网速。时间按样本 "00:00" 预留宽度。</summary>
    private double DrawClock(DrawingContext dc, Palette pal, Rect r, double cy, MonitorSnapshot? snap)
    {
        double ppd = Ppd;
        // 时间：数字等宽，并按样本 "00:00" 预留宽度（分针跳变不推挤天气）
        double tw = RenderKit.NumWidth("00:00", 15, true, ppd);
        Num(dc, DateTime.Now.ToString("HH:mm"), r.X + 16, Math.Round(cy + 15 * 0.36), 15, pal.Text, true, ppd);

        double netBlock = 0;
        double rx = r.Right - 14;
        // 右缘：紧凑网速单行
        if (snap is not null)
        {
            const double size = 10;
            double nw = NetWidth(size, snap, ppd);
            DrawNet(dc, pal, rx - nw, Math.Round(cy + size * 0.38), size, snap, ppd);
            netBlock = nw + 14;
            rx -= nw + 12;
        }
        // 天气（时钟旁给环境信息）；无数据时不占位
        double weatherBlock = 0;
        if (Weather is { } w)
        {
            // 过期（最近一次成功更新超过 30 分钟）时不显示旧读数：
            // 断网后把几小时前的温度当"实时"画出来，比没有数据更糟 —— 统一按铁律显示 "--"。
            bool stale = w.IsStale;
            string temp = stale ? "--" : $"{Math.Round(w.Temp)}°";
            Color tempColor = stale ? pal.TextDim : pal.Text;
            var emoji = Text(stale ? "" : w.Emoji, 12, pal.Text, ppd: ppd);
            double tempW = RenderKit.NumWidth(temp, 12, true, ppd);
            double wx = rx - emoji.Width - 3 - tempW;
            // 天气块始终计入内容宽度（否则缩放动画中途会暂时不画 → 宽度振荡）
            weatherBlock = emoji.Width + 3 + tempW + 12;
            if (wx > r.X + 16 + tw + 8)   // 空间够才画，避免压到时间
            {
                if (!stale) dc.DrawText(emoji, new Point(wx, cy - emoji.Height / 2));
                Num(dc, temp, wx + emoji.Width + 3, Math.Round(cy + 12 * 0.36), 12, tempColor, true, ppd);
            }
        }
        // 内容实测宽 = 时间 + 天气 + 网速块（按出现与否累加）
        return 16 + tw + 12 + weatherBlock + netBlock;
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
