using System.Windows;
using System.Windows.Media;
using Island.App.Services;
using Island.App.Themes;
using Island.Core.Monitoring;

namespace Island.App.Controls;

/// <summary>
/// 展开面板的系统监控页。整页自绘，读数变化只触发重绘、不重新测量布局；文本/画笔/几何走 RenderKit 缓存。
///
/// 排版规格对齐 mac 版 <c>DrawMonitorPage</c>（数值抄在 <see cref="ExpandedPanelSpec"/>，实现只引用常量）：
///   字号阶梯 = 14（行数值）/ 12（卡片标题）/ 11（次级文字）/ 10（行标签）/ 9（状态提示）。
///   卡片标题 = 12×12 矢量图标 + 12px SemiBold 标题 + 标题下 1px 发丝线（左右各内缩 15）。
///   分隔线只出现在卡片内的标题下，区块之间不再画发丝线（mac 口径）。
///
/// 布局（Windows 仍是「左仪表栏 + 右进程卡」；2×2 卡片栅格是下一批次）：
///   左栏 = 卡片「占用」→ CPU（唯一的主角，唯一用强调色的读数）→ 每核竖条 → 内存 / GPU / 磁盘各一行
///        → 卡片「上下速度」→ 下行 / 上行各一行（读数 + 走势）→ 外设电量行 → 页脚。
///   右栏 = 进程榜卡片：标题「占用最高 · CPU / 内存」+ 排序分段控件（96×20，选中段 Accent 实心）
///          + 进程行（行距 21、最多 5 行、图标 15、名称 11px 比例字体）+ 进程 / 线程数与今日流量。
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

        // 左右双栏：左栏仪表区定宽（上限 288），其余归右栏的进程卡片
        const double x0 = 2;
        double lw = Math.Min(288, W * 0.60), w = lw - 6;

        // 中性读数 = 文字色降不透明度。像素主题的文字是琥珀色，中性读数跟着是琥珀，整页保持单色。
        Color Neutral(byte alpha) => Color.FromArgb(alpha, pal.Text.R, pal.Text.G, pal.Text.B);

        // 局部排版助手（都按基线放置，返回右端 x）：
        //   T 中文 / 界面字体的文字；M 等宽体的拉丁标签与单位；N 等宽体的读数（数字等宽）；NW 量一段读数的宽度
        double T(string text, double x, double bl, double size, Color color, bool strong = false)
        {
            var t = RenderKit.Text(text, size, color, strong, ppd);
            RenderKit.Baseline(dc, t, x, bl);
            return x + t.WidthIncludingTrailingWhitespace;
        }
        double M(string text, double x, double bl, double size, Color color, bool strong = false)
        {
            var t = RenderKit.Mono(text, size, color, strong, ppd);
            RenderKit.Baseline(dc, t, x, bl);
            return x + t.WidthIncludingTrailingWhitespace;
        }
        double N(string text, double x, double bl, double size, Color color, bool strong = false)
            => RenderKit.Num(dc, text, x, bl, size, color, strong, ppd);
        double NW(string text, double size, bool strong) => RenderKit.NumWidth(text, size, strong, ppd);

        // 温度读数："62°" 走等宽读数（跟随所在行的字号）；"需管理员" / "缺驱动" 是状态提示，固定 9px
        double Temp(double? value, double x, double bl, double size)
        {
            if (value is not { } v) return T(TempMissingText, x, bl, ExpandedPanelSpec.StatusFont, TempMissingColor(pal), true);
            string text = $"{Math.Round(v)}°";
            return N(text, x, bl, size, TempColor(v, pal), true);
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

        // ---------- 左栏：仪表区 ----------
        double y = 0;
        // 读数行：左边标签列定宽，读数从同一列起，纵向对齐
        const double lx = 48;
        double vx = x0 + lx;

        // ---------- 卡片「占用」：CPU / 内存 / GPU / 磁盘 ----------
        if (Want("cpu") || Want("mem") || Want("gpu") || Want("disk"))
        {
            CardTitle(dc, pal, ppd, "占用", GlyphKind.Monitor, x0, y, lw - 6, pal.Text);
            // GPU 角标：占用卡右上角（mac：GPU {n}%，10px SemiBold 右对齐；数字来自监控快照）
            if (s is not null && s.GpuUsage > 0)
            {
                var badge = RenderKit.Text($"GPU {Math.Round(s.GpuUsage)}%",
                    ExpandedPanelSpec.RowLabelFont, pal.Text, true, ppd);
                RenderKit.Baseline(dc, badge, lw - 6 - badge.WidthIncludingTrailingWhitespace, y + 11);
            }
            y += 28;

            // CPU：这一页的主角（唯一用强调色的读数 —— 强调色是 Windows 暗色科技的身份，mac 没有）
            if (Want("cpu"))
            {
                double cpu = s?.CpuUsage ?? 0;
                Color cpuColor = s is null ? pal.TextTertiary : RenderKit.LevelColor(cpu, pal.Accent, pal);
                double bl = y + 13;
                M("CPU", x0, bl, ExpandedPanelSpec.RowLabelFont, pal.TextDim, true);
                double x = N(s is null ? "—" : $"{Math.Round(cpu)}", vx, bl,
                    ExpandedPanelSpec.MetricValueFont, cpuColor, true);
                x = N("%", x + 2, bl, ExpandedPanelSpec.SecondaryFont, pal.TextDim);
                if (s is not null)
                {
                    if (s.CpuFrequencyGHz > 0.1)
                    {
                        string f = $"{s.CpuFrequencyGHz:F1} GHz";
                        x = N(f, x + 14, bl, ExpandedPanelSpec.SecondaryFont, pal.TextDim);
                    }
                    x = Temp(th?.CpuTemp, x + 14, bl, ExpandedPanelSpec.SecondaryFont);
                }
                // 走势线跟读数同色：CPU 告警时数字、走势一起变
                double sparkX = x + 14, sparkW = lw - 6 - sparkX;
                if (sparkW >= 40)
                    RenderKit.DrawSpark(dc, RenderKit.SafeRect(sparkX, bl - 11, sparkW, ExpandedPanelSpec.SparkHeight),
                        CpuHistory, cpuColor, 36, ExpandedPanelSpec.SparkStroke);
                y += 24;
            }

            // 每核一根细竖条（与收起态读数带同构）：强调色，≥70% / ≥90% 换状态色
            var cores = s?.CoreUsages;
            if (Want("cores") && cores is { Length: > 0 })
            {
                int n = Math.Min(16, cores.Length);
                double gap = 3, bh = 16, bw = (w - gap * (n - 1)) / n;
                double rr = pal.SquareCorners ? 0.5 : 2;
                for (int i = 0; i < n; i++)
                {
                    double v = Math.Clamp(cores[i] / 100.0, 0, 1);
                    double bx = x0 + i * (bw + gap);
                    dc.DrawRoundedRectangle(RenderKit.BrushOf(pal.Track), null, RenderKit.SafeRect(bx, y, bw, bh), rr, rr);
                    double fh = Math.Max(bh * 0.12, v * bh);
                    dc.DrawRoundedRectangle(RenderKit.BrushOf(RenderKit.LevelColor(v * 100, pal.Accent, pal)), null,
                        RenderKit.SafeRect(bx, y + bh - fh, bw, fh), rr, rr);
                }
                y += 22;
            }
            else y += 6;

            if (Want("mem") && s is not null)
            {
                Color valueColor = RenderKit.LevelColor(s.MemoryUsagePercent, pal.Text, pal);
                double bl = y + 14;
                T("内存", x0, bl, ExpandedPanelSpec.RowLabelFont, pal.TextDim);
                double x = N($"{s.MemoryUsedGB:F1}", vx, bl, ExpandedPanelSpec.MetricValueFont, valueColor, true);
                x = N($" /{s.MemoryTotalGB:F0} GB", x, bl, ExpandedPanelSpec.SecondaryFont, pal.TextDim);
                if (s.CommitLimitGB > 0.1)
                {
                    x = T("  提交 ", x, bl, ExpandedPanelSpec.SecondaryFont, pal.TextTertiary);
                    x = N($"{s.CommitGB:F0}/{s.CommitLimitGB:F0} GB", x, bl, ExpandedPanelSpec.SecondaryFont, pal.TextTertiary);
                }
                string pct = $"{Math.Round(s.MemoryUsagePercent)}";
                N(pct, lw - 6 - 13 - NW(pct, ExpandedPanelSpec.MetricValueFont, true), bl,
                    ExpandedPanelSpec.MetricValueFont, valueColor, true);
                N("%", lw - 6 - 12, bl, ExpandedPanelSpec.SecondaryFont, pal.TextDim);
                Meter(vx, y + 24, w - lx, s.MemoryUsagePercent / 100,
                    RenderKit.LevelColor(s.MemoryUsagePercent, Neutral(0x8C), pal));
                y += 34;
            }

            if (Want("gpu") && s is not null)
            {
                double bl = y + 14;
                M("GPU", x0, bl, ExpandedPanelSpec.RowLabelFont, pal.TextDim);
                double x = N($"{Math.Round(s.GpuUsage)}", vx, bl, ExpandedPanelSpec.MetricValueFont,
                    RenderKit.LevelColor(s.GpuUsage, pal.Text, pal), true);
                x = N("%", x + 2, bl, ExpandedPanelSpec.SecondaryFont, pal.TextDim) + 14;
                x = Temp(th?.GpuTemp, x, bl, ExpandedPanelSpec.SecondaryFont) + 14;
                // 显存先占位，显卡名吃剩余宽度
                double gx = x;
                if (th?.GpuMemoryUsedGB is { } gmu)
                    gx = N($"显存 {gmu:F1}{(th.GpuMemoryTotalGB is { } gtot ? $"/{gtot:F0}" : "")} GB",
                        x, bl, ExpandedPanelSpec.SecondaryFont, pal.TextDim) + 12;
                if (!string.IsNullOrEmpty(s.GpuName))
                {
                    var name = RenderKit.Trimmed(s.GpuName, ExpandedPanelSpec.SecondaryFont, pal.TextTertiary,
                        lw - 6 - gx, false, ppd);
                    RenderKit.Baseline(dc, name, lw - 6 - name.WidthIncludingTrailingWhitespace, bl);
                }
                y += 28;
            }

            // 硬盘：每块物理盘一行；计数器实例缺失时回退单行（系统盘/_Total）
            if (Want("disk") && s is not null)
            {
                var rows = new List<DiskStat>();
                if (s.Disks is { Length: > 0 } dl) rows.AddRange(dl);
                if (rows.Count == 0)
                    rows.Add(new DiskStat
                    {
                        Name = s.DriveName, ActivePercent = s.DiskUsagePercent,
                        ReadSpeed = s.DiskReadSpeed, WriteSpeed = s.DiskWriteSpeed,
                        UsedGB = s.DriveUsedGB, TotalGB = s.DriveTotalGB,
                    });

                int shown = Math.Min(3, rows.Count);
                double rowH = shown >= 3 ? 26 : 30;   // 三块盘时行距收紧，保证页脚留在版面内
                for (int i = 0; i < shown; i++)
                {
                    var d = rows[i];
                    double bl = y + 14;
                    M((d.Name.Length > 0 ? d.Name[..1] : "D") + ":", x0, bl, ExpandedPanelSpec.RowLabelFont, pal.TextDim, true);
                    double x = N($"{Math.Round(d.ActivePercent)}", vx, bl, ExpandedPanelSpec.MetricValueFont, pal.Text, true);
                    x = N("%", x + 2, bl, ExpandedPanelSpec.SecondaryFont, pal.TextDim) + 14;
                    // 右侧空间先占位；读写速度从左往右排，排不下就不画（不叠字）
                    double right = lw - 6;
                    string space = d.TotalGB > 0 ? DriveText(d) : "";
                    if (space.Length > 0)
                    {
                        right -= NW(space, ExpandedPanelSpec.SecondaryFont, false);
                        N(space, right, bl, ExpandedPanelSpec.SecondaryFont, pal.TextTertiary);
                        right -= 10;
                    }
                    // 读写速度用紧凑单位（↓108K/s ↑2.1M/s）：等宽体比界面字体宽，全称（"108.5 KB/s"）放不下读写两项加空间。
                    // 从左往右排，排不下就不画（不叠字）。SSD 温度挪到了页脚（这一行放不下第四项）。
                    double rx = N($"↓{Compact(d.ReadSpeed)}", x, bl, ExpandedPanelSpec.SecondaryFont, pal.TextDim) + 10;
                    string wr = $"↑{Compact(d.WriteSpeed)}";
                    if (rx + NW(wr, ExpandedPanelSpec.SecondaryFont, false) < right)
                        rx = N(wr, rx, bl, ExpandedPanelSpec.SecondaryFont, pal.TextDim) + 10;
                    y += rowH;
                }
            }
        }

        // ---------- 卡片「上下速度」：下行 / 上行 ----------
        if (Want("net") && s is not null)
        {
            CardTitle(dc, pal, ppd, "上下速度", GlyphKind.Bolt, x0, y, lw - 6, pal.Text);
            y += 28;
            double rx = lw - 6;
            double bl1 = y + 13, bl2 = y + 35;
            double pingW = 0;
            if (s.PingMs >= 0)
            {
                string ping = $"{s.PingMs} ms";
                Color pc = s.PingMs <= 50 ? pal.TextDim : s.PingMs <= 120 ? pal.Warning : pal.Up;
                pingW = NW(ping, ExpandedPanelSpec.SecondaryFont, false);
                N(ping, rx - pingW, bl2, ExpandedPanelSpec.SecondaryFont, pc);
                pingW += 14;
            }
            double x = N("↓", x0, bl1, ExpandedPanelSpec.SecondaryFont, pal.TextDim, true) + 6;
            x = N(MonitorSnapshot.FormatSpeed(s.DownloadSpeed), x, bl1, ExpandedPanelSpec.MetricValueFont, pal.Text, true);
            RenderKit.DrawSpark(dc, RenderKit.SafeRect(x + 10, bl1 - 12, rx - x - 18, ExpandedPanelSpec.SparkHeight),
                DownHistory ?? NetHistory, Neutral(0x99), 36);
            double x2 = N("↑", x0, bl2, ExpandedPanelSpec.SecondaryFont, pal.TextDim, true) + 6;
            x2 = N(MonitorSnapshot.FormatSpeed(s.UploadSpeed), x2, bl2, ExpandedPanelSpec.MetricValueFont, pal.Text, true);
            RenderKit.DrawSpark(dc, RenderKit.SafeRect(x2 + 10, bl2 - 12, rx - pingW - x2 - 18, ExpandedPanelSpec.SparkHeight),
                UpHistory ?? NetHistory, Neutral(0x99), 36);
            y += 44;
        }

        // ---------- 外设电量（2026-11-25 设备电量监控模块）：每台外设一行，读数右对齐、名字吃剩余宽度。
        // 本机电池不在这一组（页脚已经有"电池"），蓝牙设备 / GATT 直读 / 手柄档位都在这里。
        // 排不下就不画（页脚还要留位），与页脚同一套规矩。
        if (Want("devices"))
        {
            var externals = new List<DeviceInfo>();
            foreach (var d in DeviceService.Snapshot)
                if (d.Name != "笔记本电池") externals.Add(d);
            int devRows = Math.Min(externals.Count, 3);
            if (devRows > 0 && y + 28 + devRows * 22 + 30 <= H)
            {
                CardTitle(dc, pal, ppd, "外设", GlyphKind.Battery, x0, y, lw - 6, pal.Text);
                y += 28;
                foreach (var d in externals.Take(devRows))
                {
                    double bl = y + 14;
                    Color vc = d.Charging ? Color.FromRgb(0x3F, 0xC1, 0x5E)
                        : d.LevelText is { } lv ? (lv is "空" or "低" ? pal.Up : lv == "中" ? pal.Warning : pal.Text)
                        : d.Percent <= 20 ? pal.Up
                        : d.Percent <= 40 ? pal.Warning : pal.Text;
                    var glyphT = RenderKit.Text(DeviceService.Glyph(d.Kind), ExpandedPanelSpec.SecondaryFont, pal.TextDim, true, ppd);
                    RenderKit.Baseline(dc, glyphT, x0, bl);
                    // 读数先占位（右对齐），名字吃剩余宽度（进程榜同款排法）
                    double right = lw - 6;
                    double valueW;
                    if (d.LevelText is { } levelText)
                    {
                        var lvT = RenderKit.Text(levelText, ExpandedPanelSpec.SecondaryFont, vc, true, ppd);
                        valueW = lvT.WidthIncludingTrailingWhitespace;
                        RenderKit.Baseline(dc, lvT, right - valueW, bl);
                    }
                    else
                    {
                        valueW = RenderKit.NumWidth($"{d.Percent}%", ExpandedPanelSpec.SecondaryFont, true, ppd);
                        RenderKit.Num(dc, $"{d.Percent}%", right - valueW, bl, ExpandedPanelSpec.SecondaryFont, vc, true, ppd);
                    }
                    var name = RenderKit.Trimmed(d.Name, ExpandedPanelSpec.SecondaryFont, pal.Text,
                        right - valueW - 14 - (x0 + 16), false, ppd);
                    RenderKit.Baseline(dc, name, x0 + 16, bl);
                    y += 22;
                }
            }
        }

        // ---------- 页脚：从左往右排，排不下就不画 ----------
        // 右栏进程榜关着时，今日流量落在这里的右端（开着时由右栏自己画在它的页脚）。
        if (Want("power"))
        {
            double bl = y + 14;
            double rx = lw - 6;
            bool list = Want("topproc") && W - 4 - (lw + 10) >= 110;
            if (s is not null && !list)
            {
                string today = Today(s);
                double tw = NW(today, ExpandedPanelSpec.RowLabelFont, false);
                N(today, rx - tw, bl, ExpandedPanelSpec.RowLabelFont, pal.TextTertiary);
                rx -= tw + 14;
            }
            double x = x0;
            // 每一项先量宽度，排得下才画（页脚右端可能还有今日流量）
            string power = th?.CpuPower is { } p && p > 0.01 ? $"{Math.Round(p)} W"
                : th?.GpuPower is { } gp && gp > 0.01 ? $"GPU {Math.Round(gp)} W" : "";
            if (power.Length > 0 && x + 44 + NW(power, ExpandedPanelSpec.RowLabelFont, true) < rx)
            {
                x = T("功耗 ", x, bl, ExpandedPanelSpec.RowLabelFont, pal.TextDim);
                x = N(power, x, bl, ExpandedPanelSpec.RowLabelFont, pal.Text, true) + 16;
            }
            if (th?.MotherboardTemp is { } mb && x + 78 < rx)
            {
                x = T("主板 ", x, bl, ExpandedPanelSpec.RowLabelFont, pal.TextDim);
                x = Temp(mb, x, bl, ExpandedPanelSpec.RowLabelFont) + 16;
            }
            if (th?.SsdTemp is { } ssd && x + 68 < rx)
            {
                x = T("SSD ", x, bl, ExpandedPanelSpec.RowLabelFont, pal.TextDim);
                x = Temp(ssd, x, bl, ExpandedPanelSpec.RowLabelFont) + 16;
            }
            if (s is { UptimeHours: > 0 } && x + 76 < rx)
            {
                x = T("运行 ", x, bl, ExpandedPanelSpec.RowLabelFont, pal.TextDim);
                x = N(FormatUptime(s.UptimeHours), x, bl, ExpandedPanelSpec.RowLabelFont, pal.TextDim) + 16;
            }
            // 本机电池（有电池才显示）：充电绿 / ≤20 红 / ≤40 橙 / 其余文字色
            var bat = s is null ? null : BatteryDevice();
            if (bat is not null && x + 64 < rx)
            {
                Color bc = bat.Charging ? pal.Down : bat.Percent <= 20 ? pal.Up : bat.Percent <= 40 ? pal.Warning : pal.Text;
                x = T("电池 ", x, bl, ExpandedPanelSpec.RowLabelFont, pal.TextDim);
                x = N($"{bat.Percent}%", x, bl, ExpandedPanelSpec.RowLabelFont, bc, true) + 4;
                if (bat.Charging) Glyphs.Draw(dc, GlyphKind.Bolt, RenderKit.SafeRect(x, bl - 9, 10, 10), pal.Down);
            }
        }

        if (Want("topproc")) DrawTopProcesses(dc, pal, s, W, H, lw, ppd);
    }

    private static string Today(MonitorSnapshot s)
        => $"今日 ↓{MonitorSnapshot.FormatBytes(s.TodayDownload)} ↑{MonitorSnapshot.FormatBytes(s.TodayUpload)}";

    /// <summary>紧凑速度（"108K/s" / "2.1M/s"）：与收起态读数带同一个口径，只是带上了 "/s"。</summary>
    private static string Compact(double bytesPerSecond) => MonitorSnapshot.CompactSpeed(bytesPerSecond) + "/s";

    /// <summary>运行时长人性化：&lt;1h 显示分钟，&lt;48h 显示 x.x h，否则 x 天。</summary>
    private static string FormatUptime(double hours)
    {
        if (hours < 1) return $"{(int)(hours * 60)} 分钟";
        if (hours < 48) return $"{hours:F1} h";
        return $"{hours / 24:F1} 天";
    }

    /// <summary>
    /// 卡片标题（mac 口径）：12×12 矢量图标 + 12px SemiBold 标题，标题下画一根 1px 发丝线（左右各内缩 15）。
    /// 分隔线只出现在这里 —— 区块之间不再画发丝线（Windows 原来的画法是把区块切开）。
    /// </summary>
    private static void CardTitle(DrawingContext dc, Palette pal, double ppd, string title, GlyphKind icon,
        double cx, double cy, double right, Color color)
    {
        double isz = ExpandedPanelSpec.CardTitleIconSize;
        Glyphs.Draw(dc, icon, RenderKit.SafeRect(cx + 2, cy + 1, isz, isz), color);
        var t = RenderKit.Text(title, ExpandedPanelSpec.CardTitleFont, color, true, ppd);
        RenderKit.Baseline(dc, t, cx + isz + 8, cy + 11);
        double inset = ExpandedPanelSpec.CardTitleRuleInset;
        RenderKit.Hairline(dc, pal, cx + inset, right - inset, cy + 22);
    }


    /// <summary>
    /// 右栏进程榜：一块半透明卡片（深色主题是 3.5% 的白 + 1px 微光边，暗色科技的卡片配方），
    /// 里面是卡片标题（「占用最高 · CPU / 内存」，12px SemiBold + 12×12 图标 + 标题下 1px 发丝线）
    /// + 排序分段控件（96×20 圆角 6，选中段 Accent 实心 + 文字用 Surface 色）
    /// + 进程行（行距 21、最多 5 行、图标 15、名称 11px 比例字体、数值 11px）+ 进程 / 线程数与今日流量。
    /// </summary>
    private void DrawTopProcesses(DrawingContext dc, Palette pal, MonitorSnapshot? s,
        double W, double H, double lw, double ppd)
    {
        double cw = W - 6 - (lw + 10);
        if (cw < 124) return;
        double cardX = lw + 10, cardRight = cardX + cw;
        double rx0 = cardX + 14, right = W - 18, rw = right - rx0;
        if (rw < 100) return;
        double cr = pal.SquareCorners ? 3 : 14;
        dc.DrawRoundedRectangle(RenderKit.Faint(pal.Text, 0.035),
            RenderKit.PenOf(pal.Hairline, 1), RenderKit.SafeRect(cardX, 0, cw, H - 2), cr, cr);

        // ---- 卡片标题（兼榜标题）：跟随当前排序方式切换文案 ----
        CardTitle(dc, pal, ppd, TopSort == "mem" ? "占用最高 · 内存" : "占用最高 · CPU",
            GlyphKind.Grid, cardX, 8, cardRight, pal.Text);

        // ---- 排序分段控件：96×20 圆角 6；选中段 Accent 实心底 + 文字用 Surface 色，未选中段底色 pal.Track ----
        // 放在标题分隔线下面（Windows 的卡片比 mac 的窄，标题与分段控件挤在一行会叠字）。
        double segW = ExpandedPanelSpec.SegmentedWidth, segH = ExpandedPanelSpec.SegmentedHeight;
        double segX = right - segW, segY = 42;
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

        double ry = 72;
        var tops = TopSort == "mem" ? s?.TopProcessesByMem : s?.TopProcesses;
        if (tops is null || tops.Length == 0)
        {
            // 空态区分「未采集」与「暂无高占用应用」（mac 口径）
            string empty = tops is null ? "未采集（仅监控页读取）" : "暂无高占用应用";
            RenderKit.Baseline(dc, RenderKit.Text(empty, ExpandedPanelSpec.EmptyListFont, pal.TextTertiary, ppd: ppd), rx0, ry + 12);
            ry += 24;
        }
        else
        {
            foreach (var p in tops.Take(ExpandedPanelSpec.ProcessMaxRows))
            {
                double bl = ry + 12;
                // 读数先占位（右对齐），名称吃剩余宽度
                bool mem = TopSort == "mem";
                string mainText = mem
                    ? (p.MemoryMB >= 1024 ? $"{p.MemoryMB / 1024:F1} GB" : $"{p.MemoryMB:F0} MB")
                    : $"{p.CpuPercent:F1}%";
                Color mainColor = mem ? pal.Text : RenderKit.LevelColor(p.CpuPercent, pal.Text, pal);
                double mainW = RenderKit.NumWidth(mainText, ExpandedPanelSpec.ProcessValueFont, true, ppd);
                RenderKit.Num(dc, mainText, right - mainW, bl, ExpandedPanelSpec.ProcessValueFont, mainColor, true, ppd);

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
                    // 首字母兜底（取不到应用图标时）：mac 用 9px，这里取状态行的 9px 档
                    var ini = RenderKit.Mono(p.Name.Length > 0 ? char.ToUpper(p.Name[0]).ToString() : "?",
                        ExpandedPanelSpec.StatusFont, pal.TextDim, true, ppd);
                    dc.DrawText(ini, new Point(rx0 + c - ini.Width / 2, ry + c - ini.Height / 2));
                }
                ry += ExpandedPanelSpec.ProcessRowGap;
            }
        }
        if (s is { ProcessCount: > 0 })
        {
            RenderKit.Num(dc, $"{s.ProcessCount} 进程 · {s.ThreadCount} 线程", rx0, ry + 10,
                ExpandedPanelSpec.RowLabelFont, pal.TextTertiary, false, ppd);
            RenderKit.Num(dc, Today(s), rx0, ry + 26, ExpandedPanelSpec.RowLabelFont, pal.TextTertiary, false, ppd);
        }
    }
}
