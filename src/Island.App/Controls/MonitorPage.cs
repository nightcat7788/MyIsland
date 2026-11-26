using System.Windows;
using System.Windows.Media;
using Island.App.Services;
using Island.App.Themes;
using Island.Core.Capabilities;
using Island.Core.Monitoring;

namespace Island.App.Controls;

/// <summary>
/// 展开面板的系统监控页。整页自绘，读数变化只触发重绘、不重新测量布局；文本/画笔/几何走 RenderKit 缓存。
///
/// 布局 = **2×2 卡片栅格**（对齐 mac 版 <c>DrawMonitorPage</c>）：
///   · 栅格：内容宽 560（mac 口径）→ 卡片宽 256（(560 - 18×2 - 12) / 2）、列间距 / 排间距都是 12；
///           上排两张卡（高 181 = 卡头 42 + 3 行 × 44 + 行距 7），下排两张卡（高 156 = 42 + 2 × 46 + 22）。
///   · 卡片：卡头 42px（12px SemiBold 标题 + 12×12 矢量图标 + 标题下 1px 发丝线，左右内缩 15）
///           → 下面若干「读数行」（行高 44、行距 7）：12×12 图标 + 10px TextDim 标签
///             + 右对齐 14px SemiBold 数值 + 4px 圆角 2 的进度条 + 1.4px 走势曲线（框高 14）。
///   · 四张卡：① CPU（占用 / 频率 / 温度）② 内存（内存 / GPU / 磁盘）
///             ③ 上下速度（下行 / 上行 + 卡底网络与电池）④ 进程榜（占用最高 · CPU / 内存 + 排序分段控件）。
///   · 底部状态行：栅格**下方**一行 9px 中性色读数（功耗 / 主板温度 / 运行时长 / 今日流量），
///     字段位清单取自两端共享规格 MonitorStatusLineSpec（平台不支持的不出现，读不到显示"未知"）。
///
/// 配色身份不变（暗色科技）：一屏一主角 —— CPU 是唯一用强调色的读数，其余读数中性色，
/// 只有 ≥70% / ≥90% 才换成状态色。mac 的「按资源配色」不引入。
/// 数据由宿主按快照直接注入（UI 线程），每秒一帧。
/// </summary>
public sealed class MonitorPage : FrameworkElement
{
    public MonitorSnapshot? Snapshot { get; set; }
    public ThermalSnapshot? Thermal { get; set; }
    public double[]? CpuHistory { get; set; }
    public double[]? NetHistory { get; set; }
    /// <summary>下行/上行各自的历史（网速两行的填充曲线）。</summary>
    public double[]? DownHistory { get; set; }
    public double[]? UpHistory { get; set; }
    public Palette? Palette { get; set; }

    /// <summary>进程榜排序："cpu"（默认）或 "mem"。</summary>
    public string TopSort { get; private set; } = "cpu";
    private Rect _sortToggleRect;

    // 显示项过滤（null = 全部；宿主由 Settings.MonitorItems 注入）
    public HashSet<string>? Items { get; set; }
    private bool Want(string key) => Items is null || Items.Contains(key);

    /// <summary>非管理员运行时为 true：温度位显示"需管理员"提示而不是"—"。</summary>
    public bool TempNeedAdmin { get; set; }

    /// <summary>
    /// 以管理员运行、但温度驱动（PawnIO）还没到位时为 true：温度位显示"缺驱动"而不是"—"（2026-11-24）。
    /// 驱动是启动后在后台装的，装完会变回 false；宿主负责同步这个值。
    /// </summary>
    public bool TempNoDriver { get; set; }

    // 温度读不到时显示什么：缺权限 / 缺驱动 / 真的没有读数。两个状态词都是三个字，宽度相同。
    private string TempMissingText => TempNeedAdmin ? "需管理员" : TempNoDriver ? "缺驱动" : "—";
    private Color TempMissingColor(Palette pal) => TempNeedAdmin || TempNoDriver ? pal.Warning : pal.TextTertiary;

    private double Ppd => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    // ================= 栅格几何（mac 口径；只在本页用得到的数放这里） =================

    /// <summary>栅格内容区左右内缩（mac：18）。</summary>
    private const double GridPad = 18;
    /// <summary>卡片之间的间距（列 / 行同值，mac：12）。</summary>
    private const double GridGap = 12;
    /// <summary>卡头高度（标题 + 分隔线 + 留白，mac：42）。</summary>
    private const double CardHeadH = 42;
    /// <summary>卡内左右内缩（图标 / 数值 / 发丝线共用，mac：15）。</summary>
    private const double CardPadX = 15;
    /// <summary>卡片圆角（mac：12；像素主题 3）。</summary>
    private const double CardRadius = 12;
    /// <summary>读数行高（mac：44 —— 进度条下方留 16px 给历史曲线）。</summary>
    private const double RowH = 44;
    /// <summary>读数行距（mac：7）。</summary>
    private const double RowGap = 7;
    /// <summary>速度两行的间距（mac：46）。</summary>
    private const double SpeedRowH = 46;
    /// <summary>上排卡高（mac：卡头 42 + 3 行 × 44 + 行距 7 = 181）。</summary>
    private const double TopCardH = CardHeadH + 3 * RowH + RowGap;
    /// <summary>下排卡高（mac：max(42 + 2×46 + 22, 42 + 86) = 156）。</summary>
    private const double BotCardH = CardHeadH + 2 * SpeedRowH + 22;
    /// <summary>让位档的读数行高（去掉走势曲线，纯装饰先让位）。</summary>
    private const double TightRowH = 30;
    /// <summary>让位档的速度行距。</summary>
    private const double TightSpeedRowH = 34;
    /// <summary>让位档的卡高（上排 160 = 42 + 44 + 30 + 30 + 2×7；下排 132 = 42 + 2×34 + 22）。</summary>
    private const double TightTopH = CardHeadH + RowH + 2 * TightRowH + 2 * RowGap;
    private const double TightBotH = CardHeadH + 2 * TightSpeedRowH + 22;
    /// <summary>
    /// 紧凑档下排卡的**最低**高度（真机不够高时按剩余高度收一收）。
    /// 106 = 卡头 42 + 两行速度各 34 + 第二行的流量条底（27 + 3）—— 再矮流量条就跑到卡外了。
    /// </summary>
    private const double MinBotCardH = 106;
    /// <summary>进度条 / 流量条填充的最小可见长度。</summary>
    private const double MinFill = 4;
    /// <summary>流量条圆角（mac：1.5）。</summary>
    private const double FlowCorner = 1.5;
    /// <summary>流量条满格 = 2 MB/s（对数刻度的分母）。</summary>
    private const double FullSpeed = 2 * 1024 * 1024;
    /// <summary>频率条满格基准 = 5.0 GHz（常见加速上限；超了按满格算）。</summary>
    private const double FreqFull = 5.0;
    /// <summary>温度条满格基准（mac：110℃）。</summary>
    private const double TempFull = 110;
    /// <summary>速度数值字号（mac：16 SemiBold）。</summary>
    private const double SpeedValueFont = 16;
    /// <summary>读数行的图标边长（mac：12）。</summary>
    private const double RowIconSize = 12;

    /// <summary>磁盘空间（"12 GB/500 GB" 那一串）：与旧版口径一致。</summary>
    private static string DriveText(DiskStat d)
    {
        static string F(double gb) => gb >= 1024 ? $"{gb / 1024:F1} TB" : $"{gb:F0} GB";
        return $"{F(d.UsedGB)}/{F(d.TotalGB)}";
    }

    /// <summary>温度：正常是中性色（文字色），71–85° 警示，&gt;85° 危险。正常不着色，告警才是画面里跳出来的颜色。</summary>
    private Color TempColor(double t, Palette pal) => t <= 70 ? pal.Text : t <= 85 ? pal.Warning : pal.Up;

    // 电池读数缓存：DeviceService.Snapshot 每次访问都会 lock + ToList 复制整表，
    // 采样间隔最短可到 1 秒，监控页跟着每次快照重绘 —— 不缓存的话等于每次重绘复制一次设备表。
    private static DeviceInfo? _batteryCache;
    private static DateTime _batteryAt = DateTime.MinValue;

    private static DeviceInfo? BatteryDevice()
    {
        if ((DateTime.Now - _batteryAt).TotalSeconds < 2) return _batteryCache;
        _batteryAt = DateTime.Now;
        _batteryCache = DeviceService.Snapshot.FirstOrDefault(d => d.Kind == "battery");
        return _batteryCache;
    }

    // ---------- 温度更新：画面会变才重绘（2026-11-24 展开态降耗） ----------
    // 展开态下温度更新（约 1.4 秒一次）原来每次都让这一页整页重绘，而页面上只用得到取整后的几个数。
    // 判断依据是"这一页看得见的样子"（ThermalDisplay.Signature，纯函数，有用例）。
    /// <summary>上一次 OnRender 画上去的温度快照的"样子"。</summary>
    private string? _thermalDrawn;

    /// <summary>
    /// 温度更新到这一页：只记下新快照，并回答"画面会不会变"。会变（true）才需要调用方重绘。
    /// 比的是"上一次真正画上去的"，不是"上一次收到的" —— 中间夹着监控快照触发的重绘也不会误判。
    /// </summary>
    public bool ThermalUpdate(Services.ThermalSnapshot? t)
    {
        Thermal = t;
        return ThermalDisplay.Signature(t) != _thermalDrawn;
    }

    /// <summary>排序页签点击（进程榜 CPU/内存）。</summary>
    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_sortToggleRect.Contains(e.GetPosition(this)))
        {
            TopSort = e.GetPosition(this).X < _sortToggleRect.Left + _sortToggleRect.Width / 2 ? "cpu" : "mem";
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var pal = Palette ?? Palette.Dark;
        double W = ActualWidth, H = ActualHeight;
        if (W < 100 || H < 80) return;
        var s = Snapshot;
        var th = Thermal;
        // 记下"这一帧画的是哪份温度"：ThermalUpdate 拿它判断下一次温度更新要不要重绘。
        // 提前 return 的那几种情况（还没布局）不算画过，放在它们之后。
        _thermalDrawn = ThermalDisplay.Signature(th);
        double ppd = Ppd;
        _sortToggleRect = Rect.Empty;

        // ---------- 栅格几何 ----------
        // 内容宽 560（mac 口径）→ 卡片宽 (560 - 18×2 - 12) / 2 = 256；面板更窄时等比收窄，绝不重叠。
        double pad = GridPad;
        double cardW = (W - pad * 2 - GridGap) / 2;
        if (cardW < 150)
        {
            pad = Math.Max(0, (W - GridGap - 300) / 2);
            cardW = (W - pad * 2 - GridGap) / 2;
        }
        if (cardW < 60) return;

        // 高度分档：骨架不变，放不下时让曲线 / 行数逐级让位（老做法：排不下就不画，绝不越出面板）。
        //   A 完整档 349 = 181 + 12 + 156（mac 口径）
        //   B 让位档 304 = 160 + 12 + 132（只有 CPU 占用行保留走势曲线）
        //   C 紧凑档 290 = 146 + 12 + 132（全部读数行不带曲线）
        // ⚠️ 2026-11-26：栅格**下方**多了一条底部状态行（TopMargin + LineHeight = 20）。
        //    选档之前**先把状态行的高度从可用高度里扣掉**，三档的分界线因此整体下移 20：
        //    349 → 369 / 304 → 324。栅格规格本身一点没变（完整档仍是 181 + 12 + 156），
        //    变的只是"什么时候降档"——状态行必须永远放得下，不压栅格、不越出面板。
        double statusLineH = MonitorStatusLineSpec.TopMargin + MonitorStatusLineSpec.LineHeight;
        double availH = H - statusLineH;
        double rowH, firstRowH, topH, speedRowH, botH;
        double procGap = ExpandedPanelSpec.ProcessRowGap;
        int procRows;
        bool speedCurve;      // 速度两行的走势曲线（只有完整档放得下）
        if (availH >= TopCardH + GridGap + BotCardH)
        {
            rowH = RowH; firstRowH = RowH;
            topH = TopCardH; speedRowH = SpeedRowH; botH = BotCardH;
            procRows = ExpandedPanelSpec.ProcessMaxRows;
            speedCurve = true;
        }
        else if (availH >= TightTopH + GridGap + TightBotH)
        {
            rowH = TightRowH; firstRowH = RowH;
            topH = TightTopH; speedRowH = TightSpeedRowH; botH = TightBotH;
            procRows = 4;
            speedCurve = false;
        }
        else
        {
            rowH = TightRowH; firstRowH = TightRowH;
            topH = CardHeadH + 3 * rowH + 2 * RowGap; speedRowH = TightSpeedRowH; botH = TightBotH;
            procRows = 4;
            speedCurve = false;
            // 连紧凑档都装不下时（真机：展开面板 540×380 ⇒ 监控页只有 308 高，
            // 146 + 12 + 132 + 20 = 310 > 308），把下排卡按"扣掉状态行后的剩余高度"再收一收 ——
            // 状态行必须永远放得下（硬要求），宁可卡片矮 2px，也不让状态行越出面板。
            double botBudget = availH - topH - GridGap;
            if (botBudget < botH) botH = Math.Max(MinBotCardH, botBudget);
        }

        // 进程榜：行数与行距都从**卡内可用高度**倒推（先给卡底信息行留出位置）——
        // 固定行距 + 固定行数会撞上卡底那行：末行文字底边会压到「进程/线程 · 今日流量」那行
        // （2026-11-26 离屏渲染实测到的压字缺陷，monitor-page.png 与 expanded-panel.png 都能看见）。
        // 这里改成「可用高度 ÷ 行数 = 行距，向下取整」，让重叠在构造上就不可能发生。
        double procAvail = botH - CardHeadH - ExpandedPanelSpec.ProcessFooterReserve;
        procRows = ExpandedPanelSpec.ProcessRowsFor(procAvail, procRows);
        procGap = ExpandedPanelSpec.ProcessRowGapFor(procAvail, procRows);

        double x1 = pad, x2 = pad + cardW + GridGap;
        double y1 = 0, y2 = topH + GridGap;

        // 面板有圆角，画到页面之外的像素会露馅 —— 整个页面裁一刀（正常情况下内容都在里面）。
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, W, H)));

        // 中性读数 = 文字色降不透明度。像素主题的文字是琥珀色，中性读数跟着是琥珀，整页保持单色。
        Color Neutral(byte alpha) => Color.FromArgb(alpha, pal.Text.R, pal.Text.G, pal.Text.B);

        // 局部排版助手（都按基线放置，返回右端 x）：
        //   T 中文 / 界面字体的文字；N 等宽体的读数（数字等宽）；NW 量一段读数的宽度
        double T(string text, double x, double bl, double size, Color color, bool strong = false)
        {
            var t = RenderKit.Text(text, size, color, strong, ppd);
            RenderKit.Baseline(dc, t, x, bl);
            return x + t.WidthIncludingTrailingWhitespace;
        }
        double N(string text, double x, double bl, double size, Color color, bool strong = false)
            => RenderKit.Num(dc, text, x, bl, size, color, strong, ppd);
        double NW(string text, double size, bool strong) => RenderKit.NumWidth(text, size, strong, ppd);

        // 卡片外壳：半透明底（3.5% 的白）+ 1px 微光边 + 圆角 12（暗色科技的卡片配方；像素主题 3）
        void Card(double cx, double cy, double cw, double ch)
        {
            double cr = pal.SquareCorners ? 3 : CardRadius;
            dc.DrawRoundedRectangle(RenderKit.Faint(pal.Text, 0.035),
                RenderKit.PenOf(pal.Hairline, 1), RenderKit.SafeRect(cx, cy, cw, ch), cr, cr);
        }

        // 进度条：轨道 + 填充（mac 口径：高 4、圆角 2；填充比一个高度还短时不画，圆角会糊成一团）
        void Meter(double mx, double my, double mw, double share, Color fill)
        {
            double mh = ExpandedPanelSpec.MeterHeight, mc = ExpandedPanelSpec.MeterCorner;
            dc.DrawRoundedRectangle(RenderKit.BrushOf(pal.Track), null, RenderKit.SafeRect(mx, my, mw, mh), mc, mc);
            double fw = mw * (double.IsFinite(share) ? Math.Clamp(share, 0, 1) : 0);
            if (fw < mh) return;
            dc.DrawRoundedRectangle(RenderKit.BrushOf(fill), null, RenderKit.SafeRect(mx, my, fw, mh), mc, mc);
        }

        // 一行读数（mac 口径的 Row）：12×12 图标 + 10px TextDim 标签（+ 次级说明）
        //   + 右对齐 14px SemiBold 数值 + 4px 圆角 2 的进度条 + 1.4px 走势曲线（框高 14）
        void MetricRow(double cx, double cy, double cw, GlyphKind? icon, string iconText, Color iconColor,
            Color valueColor, Color meterColor, string label, string sub, string value, string unit,
            double share, double[]? history)
        {
            double right = cx + cw - CardPadX;
            double labelX = cx + 33;
            double nw = NW(value, ExpandedPanelSpec.MetricValueFont, true);
            // 单位：ASCII 走等宽读数体；非 ASCII（° ·）走界面字体 —— Consolas 里没有度数符号，
            // 用等宽体画会变成豆腐块（□）。
            bool unitAscii = unit.All(ch => ch < 128);
            double uw = unit.Length == 0 ? 0
                : unitAscii ? NW(unit, ExpandedPanelSpec.SecondaryFont, false)
                            : RenderKit.Text(unit, ExpandedPanelSpec.SecondaryFont, pal.TextDim, false, ppd).WidthIncludingTrailingWhitespace;
            double vx = right - nw - uw;      // 数值左端（整组右对齐到卡内右边）

            // 图标（12×12）：矢量图标没有对应字形时用一个小字符（温度 = 度数符号）
            if (icon is { } g)
                Glyphs.Draw(dc, g, RenderKit.SafeRect(cx + 15, cy + 4, RowIconSize, RowIconSize), iconColor);
            else
                RenderKit.Baseline(dc, RenderKit.Text(iconText, 12, iconColor, false, ppd), cx + 15, cy + 14);

            // 标签（10px TextDim）；次级说明（10px TextTertiary）排不下就不画
            double lx = T(label, labelX, cy + 14, ExpandedPanelSpec.RowLabelFont, pal.TextDim);
            if (sub.Length > 0 && vx - 10 > lx + 4)
            {
                var subT = RenderKit.Trimmed(sub, ExpandedPanelSpec.RowLabelFont, pal.TextTertiary, vx - 10 - lx - 4, false, ppd);
                RenderKit.Baseline(dc, subT, lx + 4, cy + 14);
            }

            // 数值（右对齐）：14px SemiBold + 单位 11px TextDim
            N(value, vx, cy + 15, ExpandedPanelSpec.MetricValueFont, valueColor, true);
            if (uw > 0)
            {
                if (unitAscii) N(unit, vx + nw, cy + 15, ExpandedPanelSpec.SecondaryFont, pal.TextDim);
                else RenderKit.Baseline(dc, RenderKit.Text(unit, ExpandedPanelSpec.SecondaryFont, pal.TextDim, false, ppd), vx + nw, cy + 15);
            }

            // 进度条
            Meter(labelX, cy + 22, cw - 48, share, meterColor);

            // 走势曲线（没有这一项的历史就不画，空框不如不画）
            if (history is not null)
                RenderKit.DrawSpark(dc, RenderKit.SafeRect(labelX, cy + 29, cw - 48, ExpandedPanelSpec.SparkHeight),
                    history, valueColor, 36, ExpandedPanelSpec.SparkStroke);
        }

        // 速度行（mac 口径的 Speed）：箭头 + 10px TextDim 标签 + 右对齐 16px SemiBold 数值
        //   + 3px 圆角 1.5 的流量条（对数刻度，2MB/s 满格）+ 14px 走势曲线
        void SpeedRow(double cx, double cy, double cw, string arrow, string label, double bytesPerSec,
            double[]? history, bool curve)
        {
            double labelX = cx + 33;
            double right = cx + cw - CardPadX;
            string text = MonitorSnapshot.FormatSpeed(bytesPerSec);
            double tw = NW(text, SpeedValueFont, true);
            N(text, right - tw, cy + 16, SpeedValueFont, pal.Text, true);
            N(arrow, cx + 15, cy + 16, ExpandedPanelSpec.SecondaryFont, pal.TextDim, true);
            T(label, labelX, cy + 16, ExpandedPanelSpec.RowLabelFont, pal.TextDim);

            // 流量条：对数刻度，低速（几 KB/s）也能看到明显条长
            double bw = cw - 48;
            dc.DrawRoundedRectangle(RenderKit.BrushOf(pal.Track), null,
                RenderKit.SafeRect(labelX, cy + 27, bw, 3), FlowCorner, FlowCorner);
            double frac = bytesPerSec <= 0 ? 0
                : Math.Clamp(Math.Log10(1 + bytesPerSec) / Math.Log10(1 + FullSpeed), 0, 1);
            if (frac > 0.002)
                dc.DrawRoundedRectangle(RenderKit.BrushOf(Neutral(0x99)), null,
                    RenderKit.SafeRect(labelX, cy + 27, Math.Max(MinFill, bw * frac), 3), FlowCorner, FlowCorner);

            // 走势曲线（让位档不画：两行间距压到 34 后，曲线会撞到卡底的信息行）
            if (curve && history is not null)
                RenderKit.DrawSpark(dc, RenderKit.SafeRect(labelX, cy + 33, bw, ExpandedPanelSpec.SparkHeight),
                    history, Neutral(0x99), 36, ExpandedPanelSpec.SparkStroke);
        }

        // ================= ① CPU 卡（上排左）：这一页的主角 =================
        if (Want("cpu"))
        {
            Card(x1, y1, cardW, topH);
            CardTitle(dc, pal, ppd, "CPU", GlyphKind.Monitor, x1, y1, x1 + cardW, pal.Text);

            double cy = y1 + CardHeadH;
            double cpu = s?.CpuUsage ?? 0;
            // CPU 是唯一用强调色的读数（暗色科技的身份）：告警时才换成状态色
            Color cpuColor = s is null ? pal.TextTertiary : RenderKit.LevelColor(cpu, pal.Accent, pal);
            MetricRow(x1, cy, cardW, GlyphKind.Sliders, "", cpuColor, cpuColor, cpuColor,
                "占用", "", s is null ? "—" : $"{Math.Round(cpu)}", "%", s is null ? 0 : cpu / 100, CpuHistory);

            // 频率（满格基准 5.0 GHz）
            double ghz = s?.CpuFrequencyGHz ?? 0;
            MetricRow(x1, cy + firstRowH + RowGap, cardW, GlyphKind.Gear, "", pal.TextDim, pal.Text, Neutral(0x8C),
                "频率", "", ghz > 0.1 ? $"{ghz:F1}" : "—", ghz > 0.1 ? " GHz" : "", ghz / FreqFull, null);

            // 温度（取整度数：与 ThermalDisplay.Signature 的口径一致）
            double ty = cy + firstRowH + RowGap + rowH + RowGap;
            string tempValue, tempUnit;
            Color tempColor;
            double tempShare;
            if (th?.CpuTemp is { } tv)
            {
                tempValue = $"{Math.Round(tv)}"; tempUnit = "°";
                tempColor = TempColor(tv, pal); tempShare = tv / TempFull;
            }
            else
            {
                tempValue = TempMissingText; tempUnit = "";
                tempColor = TempMissingColor(pal); tempShare = 0;
            }
            MetricRow(x1, ty, cardW, null, "°", pal.TextDim, tempColor, Neutral(0x8C),
                "温度", "", tempValue, tempUnit, tempShare, null);
        }

        // ================= ② 内存卡（上排右）：内存 / GPU / 磁盘 =================
        if (Want("mem") || Want("gpu") || Want("disk"))
        {
            Card(x2, y1, cardW, topH);
            CardTitle(dc, pal, ppd, "内存", GlyphKind.Grid, x2, y1, x2 + cardW, pal.Text);

            double cy = y1 + CardHeadH;
            int slot = 0;
            // 内存：占用 % + 已用/总量（次级说明里）
            if (Want("mem"))
            {
                double mp = s?.MemoryUsagePercent ?? 0;
                string sub = s is null ? "" : $"{s.MemoryUsedGB:F1}/{s.MemoryTotalGB:F0} GB";
                MetricRow(x2, cy + slot * (rowH + RowGap), cardW, GlyphKind.Monitor, "", pal.TextDim,
                    RenderKit.LevelColor(mp, pal.Text, pal),
                    RenderKit.LevelColor(mp, Neutral(0x8C), pal),
                    "内存", sub, s is null ? "—" : $"{Math.Round(mp)}", "%", mp / 100, null);
                slot++;
            }
            // GPU：占用 % + 温度 / 显存（次级说明里）
            if (Want("gpu"))
            {
                double gp = s?.GpuUsage ?? 0;
                var parts = new List<string>();
                if (th?.GpuTemp is { } gt) parts.Add($"{Math.Round(gt)}°");
                // 显存读数按**口径**渲染（2026-11-26 task15）：共享口径下那个"总量"是共享内存池大小
                // （≈ 系统内存），不是显存容量 —— 显示出来会被读成"核显有十几 GB 显存"，所以不显示，只标"共享"。
                // 专用 / 厂商显存照旧显示"已用/总量"（那就是显存容量）；口径未知则只显示已用（宁可少显示）。
                if (th?.GpuMemoryUsedGB is { } gmu)
                    parts.Add(GpuMemoryDisplay.Text(gmu, th.GpuMemoryTotalGB, th.GpuMemoryKind));
                MetricRow(x2, cy + slot * (rowH + RowGap), cardW, GlyphKind.Grid, "", pal.TextDim,
                    RenderKit.LevelColor(gp, pal.Text, pal),
                    RenderKit.LevelColor(gp, Neutral(0x8C), pal),
                    "GPU", string.Join(" · ", parts), s is null ? "—" : $"{Math.Round(gp)}", "%", gp / 100, null);
                slot++;
            }
            // 磁盘：活动 % + 空间
            if (Want("disk"))
            {
                double dp = s?.DiskUsagePercent ?? 0;
                string sub = "";
                if (s is not null)
                {
                    var disks = s.Disks is { Length: > 0 } dl ? dl : null;
                    if (disks is not null && disks[0].TotalGB > 0.1) sub = DriveText(disks[0]);
                    else if (s.DriveTotalGB > 0.1) sub = $"{s.DriveUsedGB:F0}/{s.DriveTotalGB:F0} GB";
                }
                MetricRow(x2, cy + slot * (rowH + RowGap), cardW, GlyphKind.File, "", pal.TextDim,
                    RenderKit.LevelColor(dp, pal.Text, pal),
                    RenderKit.LevelColor(dp, Neutral(0x8C), pal),
                    "磁盘", sub, s is null ? "—" : $"{Math.Round(dp)}", "%", dp / 100, null);
            }
        }

        // ================= ③ 上下速度卡（下排左） =================
        if (Want("net") || Want("power"))
        {
            Card(x1, y2, cardW, botH);
            CardTitle(dc, pal, ppd, "上下速度", GlyphKind.Bolt, x1, y2, x1 + cardW, pal.Text);

            double cy = y2 + CardHeadH;
            if (Want("net"))
            {
                SpeedRow(x1, cy, cardW, "↓", "下行", s?.DownloadSpeed ?? 0, DownHistory ?? NetHistory, speedCurve);
                SpeedRow(x1, cy + speedRowH, cardW, "↑", "上行", s?.UploadSpeed ?? 0, UpHistory ?? NetHistory, speedCurve);
            }

            // 卡底信息位：左 网络类型，右 电池（≤20% 用 pal.Up）
            double infoBase = y2 + botH - 9;
            if (Want("net"))
            {
                string link = string.IsNullOrEmpty(s?.LinkType) ? "—" : s.LinkType;
                T($"网络 {link}", x1 + CardPadX, infoBase, ExpandedPanelSpec.RowLabelFont, pal.TextTertiary);
            }
            if (Want("power"))
            {
                int? percent = null;
                bool charging = false;
                if (s is not null && s.BatteryPercent is { } bp) { percent = (int)Math.Round(bp); charging = s.BatteryCharging; }
                else
                {
                    var dev = BatteryDevice();
                    if (dev is not null) { percent = dev.Percent; charging = dev.Charging; }
                }
                if (percent is { } pv)
                {
                    string text = $"电池 {pv}%{(charging ? " · 充电中" : "")}";
                    Color bc = pv <= 20 ? pal.Up : pal.TextDim;
                    var bt = RenderKit.Text(text, ExpandedPanelSpec.RowLabelFont, bc, false, ppd);
                    RenderKit.Baseline(dc, bt, x1 + cardW - CardPadX - bt.WidthIncludingTrailingWhitespace, infoBase);
                }
            }
        }

        // ================= ④ 进程榜卡（下排右） =================
        if (Want("topproc"))
        {
            Card(x2, y2, cardW, botH);

            // 右上分段控件（96×20 圆角 6）：选中段 Accent 实心 + 文字 Surface 色，未选中段底 pal.Track
            double segW = ExpandedPanelSpec.SegmentedWidth, segH = ExpandedPanelSpec.SegmentedHeight;
            double segX = x2 + cardW - CardPadX - segW, segY = y2 + 1;
            _sortToggleRect = RenderKit.SafeRect(segX, segY, segW, segH);
            double segR = ExpandedPanelSpec.SegmentedCorner;
            dc.DrawRoundedRectangle(RenderKit.BrushOf(pal.Track), null, _sortToggleRect, segR, segR);
            if (TopSort == "mem")
                dc.DrawRoundedRectangle(RenderKit.BrushOf(pal.Accent), null,
                    RenderKit.SafeRect(segX + segW / 2, segY, segW / 2, segH), segR, segR);
            else
                dc.DrawRoundedRectangle(RenderKit.BrushOf(pal.Accent), null,
                    RenderKit.SafeRect(segX, segY, segW / 2, segH), segR, segR);
            string[] segLabels = { "CPU", "内存" };
            for (int i = 0; i < segLabels.Length; i++)
            {
                bool on = TopSort == (i == 0 ? "cpu" : "mem");
                var t = RenderKit.Text(segLabels[i], ExpandedPanelSpec.RowLabelFont,
                    on ? pal.Surface : pal.TextDim, on, ppd);
                double tx = segX + i * (segW / 2) + (segW / 2 - t.WidthIncludingTrailingWhitespace) / 2;
                RenderKit.Baseline(dc, t, tx, segY + segH / 2 + 3.5);
            }

            // 榜标题：跟随排序切换；卡片窄得放不下就只留"占用最高"（分段控件本身已经说明按什么排）
            string title = TopSort == "mem" ? "占用最高 · 内存" : "占用最高 · CPU";
            double titleMax = segX - 8 - (x2 + 20);
            if (RenderKit.Text(title, ExpandedPanelSpec.CardTitleFont, pal.Text, true, ppd)
                    .WidthIncludingTrailingWhitespace > titleMax)
                title = "占用最高";
            CardTitle(dc, pal, ppd, title, GlyphKind.File, x2, y2, x2 + cardW, pal.Text, titleMax);

            // 卡底信息位：进程 / 线程数（2×2 栅格重排时一度被删掉，这里补回 —— 这些读数在收起态
            // 没有对应项，删了就等于功能回退）。
            // 今日流量**移到了栅格下方的底部状态行**：同屏不重复，读数没丢，只是换了个更宽、
            // 更好读的位置（卡底这行窄，塞不下两段）。
            if (s is not null)
            {
                double bl = y2 + botH - ExpandedPanelSpec.ProcessFooterBaselineInset;
                T($"{s.ProcessCount} 进程 · {s.ThreadCount} 线程",
                    x2 + CardPadX, bl, ExpandedPanelSpec.StatusFont, pal.TextTertiary);
            }

            // 榜体：图标 15×15 + 名称 11px 比例字体 + 右对齐数值 11px SemiBold（行距 21、最多 5 行）
            double ry = y2 + CardHeadH;
            double rx0 = x2 + CardPadX;
            double right = x2 + cardW - CardPadX;
            var tops = TopSort == "mem" ? s?.TopProcessesByMem : s?.TopProcesses;
            if (tops is null || tops.Length == 0)
            {
                // 空态区分「未采集」与「暂无高占用应用」（mac 口径）
                string empty = tops is null ? "未采集（仅监控页读取）" : "暂无高占用应用";
                RenderKit.Baseline(dc, RenderKit.Text(empty, ExpandedPanelSpec.EmptyListFont, pal.TextTertiary, ppd: ppd),
                    rx0, ry + 12);
            }
            else
            {
                foreach (var p in tops.Take(procRows))
                {
                    double bl = ry + 12;
                    bool mem = TopSort == "mem";
                    string mainText = mem
                        ? (p.MemoryMB >= 1024 ? $"{p.MemoryMB / 1024:F1} GB" : $"{p.MemoryMB:F0} MB")
                        : $"{p.CpuPercent:F1}%";
                    double mainW = NW(mainText, ExpandedPanelSpec.ProcessValueFont, true);
                    N(mainText, right - mainW, bl, ExpandedPanelSpec.ProcessValueFont, pal.TextDim, true);

                    // 名称用比例字体（mac 口径：名单是中文 + 英文混排，等宽体会显得挤）
                    double nx = rx0 + 22;
                    var name = RenderKit.Trimmed(p.Name, ExpandedPanelSpec.ProcessNameFont, pal.Text,
                        right - mainW - 12 - nx, false, ppd);
                    RenderKit.Baseline(dc, name, nx, bl);

                    // 真实应用图标（15px 原尺寸，放大就糊；取不到画首字母圆）
                    var icon = RenderKit.AppIcon(p.ExePath);
                    if (icon is not null)
                    {
                        dc.DrawImage(icon, RenderKit.SafeRect(rx0, ry, ExpandedPanelSpec.ProcessIconSize, ExpandedPanelSpec.ProcessIconSize));
                    }
                    else
                    {
                        double c = ExpandedPanelSpec.ProcessIconSize / 2;
                        dc.DrawEllipse(RenderKit.Faint(pal.Text, 0.10), null, new Point(rx0 + c, ry + c), c, c);
                        // 首字母兜底（取不到应用图标时）
                        var ini = RenderKit.Mono(p.Name.Length > 0 ? char.ToUpper(p.Name[0]).ToString() : "?",
                            ExpandedPanelSpec.StatusFont, pal.TextDim, true, ppd);
                        dc.DrawText(ini, new Point(rx0 + c - ini.Width / 2, ry + c - ini.Height / 2));
                    }
                    ry += procGap;
                }
            }
        }

        // ================= 底部状态行（2×2 栅格下方，2026-11-26） =================
        // 字段位清单来自**两端共享的唯一规格**（Island.Core.Capabilities.MonitorStatusLineSpec）：
        //   · 平台不支持的字段位**直接不出现**（不置灰、不留空位）—— 由 SlotsFor 决定，本页不自己判断；
        //   · 支持但读不到 ⇒ 显示"未知"，绝不用 0 / 默认值 / 换算百分比充数（项目硬红线）。
        // 颜色：中性色（TextTertiary）—— 一屏一主角，强调色只给 CPU（暗色科技的身份）。
        {
            double gridBottom = y2 + botH;
            double slTop = gridBottom + MonitorStatusLineSpec.TopMargin;
            double slBaseline = slTop + MonitorStatusLineSpec.LineHeight - 3;   // 9px 文字坐在 14px 带的下方
            double sx = x1;              // 与栅格左缘对齐
            double right = x2 + cardW;   // 不越出栅格右缘

            foreach (var slot in MonitorStatusLineSpec.SlotsFor(macOS: false))
            {
                string? value = slot.Key switch
                {
                    "cpu-power" => ExpandedPanelSpec.StatusPowerText(th?.CpuPower),
                    "board-temp" => ExpandedPanelSpec.StatusTempText(th?.MotherboardTemp),
                    "runtime" => ExpandedPanelSpec.StatusUptimeText(s?.UptimeHours),
                    "today-traffic" => ExpandedPanelSpec.StatusTrafficText(s?.TodayDownload, s?.TodayUpload),
                    _ => null,
                };
                if (value is null) continue;   // 本平台不支持 ⇒ 这一项不出现

                // 读数走等宽体（数字等宽，跳动不抖）；含非 ASCII（° / ↓ / 中文）时走界面字体
                // —— Consolas 里没有这些字形，硬画会变成豆腐块。
                bool asciiOnly = value.All(ch => ch < 128);
                var label = RenderKit.Text(slot.Label + " ", MonitorStatusLineSpec.FontSize,
                    pal.TextTertiary, false, ppd);
                var val = asciiOnly
                    ? RenderKit.Mono(value, MonitorStatusLineSpec.FontSize, pal.TextTertiary, false, ppd)
                    : RenderKit.Text(value, MonitorStatusLineSpec.FontSize, pal.TextTertiary, false, ppd);
                double slotW = label.WidthIncludingTrailingWhitespace + val.WidthIncludingTrailingWhitespace;
                if (sx + slotW > right) break;   // 一行放不下就到此为止（不换行、不叠字）

                RenderKit.Baseline(dc, label, sx, slBaseline);
                RenderKit.Baseline(dc, val, sx + label.WidthIncludingTrailingWhitespace, slBaseline);
                sx += slotW + MonitorStatusLineSpec.SlotGap;
            }
        }

        dc.Pop();
    }

    /// <summary>
    /// 卡片标题（mac 口径）：12×12 矢量图标 + 12px SemiBold 标题，标题下画一根 1px 发丝线（左右各内缩 15）。
    /// 分隔线只出现在这里 —— 区块之间不再画发丝线（Windows 原来的画法是把区块切开）。
    /// </summary>
    /// <param name="maxTitleWidth">标题可用的最大宽度（0 = 不限；窄卡片里给分段控件让位时用）。</param>
    private static void CardTitle(DrawingContext dc, Palette pal, double ppd, string title, GlyphKind icon,
        double cx, double cy, double right, Color color, double maxTitleWidth = 0)
    {
        double isz = ExpandedPanelSpec.CardTitleIconSize;
        Glyphs.Draw(dc, icon, RenderKit.SafeRect(cx + 2, cy + 1, isz, isz), color);
        double tx = cx + isz + 8;
        var t = maxTitleWidth > 0
            ? RenderKit.Trimmed(title, ExpandedPanelSpec.CardTitleFont, color, maxTitleWidth, true, ppd)
            : RenderKit.Text(title, ExpandedPanelSpec.CardTitleFont, color, true, ppd);
        RenderKit.Baseline(dc, t, tx, cy + 11);
        double inset = ExpandedPanelSpec.CardTitleRuleInset;
        RenderKit.Hairline(dc, pal, cx + inset, right - inset, cy + 22);
    }
}
