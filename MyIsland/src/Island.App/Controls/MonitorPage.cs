using System.Windows;
using System.Windows.Media;
using Island.App.Services;
using Island.App.Themes;
using Island.Core.Monitoring;

namespace Island.App.Controls;

/// <summary>
/// 展开面板的系统监控页。整页自绘，读数变化只触发重绘、不重新测量布局；文本/画笔/几何走 RenderKit 缓存。
/// 布局（2026-11-24 视觉重做，暗色科技）：
///   左栏 = 仪表区：CPU 主读数（标签 + 大号数字 + 带辉光的开口环 + 频率 / 温度 + 走势）→ 每核一根细竖条
///          → 发丝线 → 内存 / GPU / 各磁盘各一行（左边是定宽的标签列，读数纵向对齐）
///          → 发丝线 → 下行 / 上行各一行（读数 + 走势）→ 页脚（功耗、主板温度、运行时长）。
///   右栏 = 进程榜：一块半透明卡片（细白边），里面是标题 + 纯文字排序页签 + 进程行 + 进程 / 线程数 + 今日流量。
/// 层级规则：一屏一主角 —— CPU 是唯一用强调色、唯一发光的读数；其余读数是中性色，
/// 只有 ≥70% / ≥90% 才换成状态色。发光是稀缺资源：主角之外只有面板顶部那束辉光在发亮。
/// 字体：读数、单位、拉丁标签、进程名用等宽体（RenderKit.Num / Mono，"终端感"），中文用界面字体；
/// 字号只用四档：36（主读数）/ 17（行读数）/ 12（标签与说明）/ 11（页脚与计数）。
/// 数据由宿主按快照直接注入（UI 线程），每秒一帧。
/// </summary>
public sealed class MonitorPage : FrameworkElement
{
    // ---------- CPU 主读数的辉光（2026-11-24） ----------
    // 辉光是一张缓存的小位图（RenderKit.GlowText）：文字 / 颜色没变就直接贴，变了才重做一次。
    // 试过的两种做法都更贵：48 次错位描边每次重绘约 10ms（一次 DrawText 在软件渲染里约合 0.2ms）；
    // BlurEffect 图层每个渲染通道都要重算模糊（约 3.5ms），而毛玻璃每更新一次就是一个渲染通道。
    private readonly GlowText _heroGlow = new();

    public MonitorSnapshot? Snapshot { get; set; }
    public ThermalSnapshot? Thermal { get; set; }
    public double[]? CpuHistory { get; set; }
    public double[]? NetHistory { get; set; }
    /// <summary>下行/上行各自的历史（网速两行的填充曲线）。</summary>
    public double[]? DownHistory { get; set; }
    public double[]? UpHistory { get; set; }
    public Palette? Palette { get; set; }

    /// <summary>进程榜排序："cpu"（默认）或 "mem"（右上角文字页签）。</summary>
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
            TopSort = TopSort == "cpu" ? "mem" : "cpu";
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
        double y = 0;

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
        // 温度读数："62°" 走等宽读数，"需管理员" 这类状态词走界面字体
        double Temp(double? value, double x, double bl, double size)
        {
            if (value is not { } v) return T(TempMissingText, x, bl, size, TempMissingColor(pal), true);
            string text = $"{Math.Round(v)}°";
            return N(text, x, bl, size, TempColor(v, pal), true);
        }
        void Hairline(double at) => RenderKit.Hairline(dc, pal, x0, lw - 6, at);

        // ---------- CPU 主读数：这一页的主角（唯一用强调色、唯一发光的读数） ----------
        if (Want("cpu"))
        {
            double cpu = s?.CpuUsage ?? 0;
            Color cpuColor = s is null ? pal.TextTertiary : RenderKit.LevelColor(cpu, pal.Accent, pal);
            bool lit = pal.IsDark;          // 暗色科技的那套发光只属于深色主题
            const double bl = 50;
            RenderKit.Dial(dc, new Point(x0 + 24, 32), 48, 3.5, s is null ? null : cpu / 100,
                pal.Track, cpuColor, pal.Text, "", ppd, glow: lit ? 3.5 : 0);
            const double tx = x0 + 54;
            M("CPU", tx, 12, 10, pal.TextTertiary, true);
            // 主读数：先贴辉光（只在深色主题、且有读数时），再写实心字
            string hero = s is null ? "—" : $"{Math.Round(cpu)}";
            if (lit && s is not null)
            {
                var (bmp, bounds) = _heroGlow.Get(hero, 36, cpuColor, false, ppd);
                dc.DrawImage(bmp, new Rect(tx + bounds.X, bl + bounds.Y, bounds.Width, bounds.Height));
            }
            double x = N(hero, tx, bl, 36, s is null ? pal.TextTertiary : cpuColor);
            if (s is not null)
            {
                M("%", x + 4, bl, 13, pal.TextDim);
                // 频率 / 温度：两行右对齐到同一位置，给走势图让出固定宽度
                const double meta = 184;
                if (s.CpuFrequencyGHz > 0.1)
                {
                    string f = $"{s.CpuFrequencyGHz:F1} GHz";
                    N(f, meta - NW(f, 12, false), 27, 12, pal.TextDim);
                }
                if (th?.CpuTemp is { } cv)
                {
                    string ct = $"{Math.Round(cv)}°";
                    N(ct, meta - NW(ct, 12, true), 51, 12, TempColor(cv, pal), true);
                }
                else
                {
                    var mt = RenderKit.Text(TempMissingText, 12, TempMissingColor(pal), true, ppd);
                    RenderKit.Baseline(dc, mt, meta - mt.WidthIncludingTrailingWhitespace, 51);
                }
            }
            // 走势线跟主读数同色：CPU 告警时数字、环、走势一起变
            RenderKit.DrawSpark(dc, RenderKit.SafeRect(196, 10, lw - 6 - 196, 46), CpuHistory, cpuColor, 36);
            y = 64;
        }

        // ---------- 每核一根细竖条（与收起态读数带同构）：强调色，≥70% / ≥90% 换状态色 ----------
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
            y += 27;
        }
        else y += 8;

        Hairline(y);
        y += 13;

        // ---------- 读数行：左边标签列定宽，读数从同一列起，纵向对齐 ----------
        const double lx = 48;
        double vx = x0 + lx;

        if (Want("mem") && s is not null)
        {
            Color valueColor = RenderKit.LevelColor(s.MemoryUsagePercent, pal.Text, pal);
            T("内存", x0, y + 21, 12, pal.TextDim);
            double x = N($"{s.MemoryUsedGB:F1}", vx, y + 21, 17, valueColor, true);
            x = N($" /{s.MemoryTotalGB:F0} GB", x, y + 21, 12, pal.TextDim);
            if (s.CommitLimitGB > 0.1)
            {
                x = T("  提交 ", x, y + 21, 11, pal.TextTertiary);
                x = N($"{s.CommitGB:F0}/{s.CommitLimitGB:F0} GB", x, y + 21, 11, pal.TextTertiary);
            }
            string pct = $"{Math.Round(s.MemoryUsagePercent)}";
            N(pct, lw - 6 - 13 - NW(pct, 17, true), y + 21, 17, valueColor, true);
            N("%", lw - 6 - 12, y + 21, 11, pal.TextDim);
            RenderKit.Meter(dc, RenderKit.SafeRect(vx, y + 32, w - lx, 3), s.MemoryUsagePercent / 100, pal,
                RenderKit.LevelColor(s.MemoryUsagePercent, Neutral(0x8C), pal));
            y += 42;
        }

        if (Want("gpu") && s is not null)
        {
            M("GPU", x0, y + 21, 12, pal.TextDim);
            double x = N($"{Math.Round(s.GpuUsage)}", vx, y + 21, 17, RenderKit.LevelColor(s.GpuUsage, pal.Text, pal), true);
            x = N("%", x + 2, y + 21, 11, pal.TextDim) + 14;
            x = Temp(th?.GpuTemp, x, y + 21, 12) + 14;
            // 显存先占位，显卡名吃剩余宽度
            double gx = x;
            if (th?.GpuMemoryUsedGB is { } gmu)
                gx = N($"显存 {gmu:F1}{(th.GpuMemoryTotalGB is { } gtot ? $"/{gtot:F0}" : "")} GB", x, y + 21, 11, pal.TextDim) + 12;
            if (!string.IsNullOrEmpty(s.GpuName))
            {
                var name = RenderKit.Trimmed(s.GpuName, 11, pal.TextTertiary, lw - 6 - gx, false, ppd);
                RenderKit.Baseline(dc, name, lw - 6 - name.WidthIncludingTrailingWhitespace, y + 21);
            }
            y += 34;
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
            double rowH = shown >= 3 ? 28 : 33;   // 三块盘时行距收紧，保证页脚留在版面内
            for (int i = 0; i < shown; i++)
            {
                var d = rows[i];
                M((d.Name.Length > 0 ? d.Name[..1] : "D") + ":", x0, y + 21, 12, pal.TextDim, true);
                double x = N($"{Math.Round(d.ActivePercent)}", vx, y + 21, 17, pal.Text, true);
                x = N("%", x + 2, y + 21, 11, pal.TextDim) + 14;
                // 右侧空间先占位；读写速度从左往右排，排不下就不画（不叠字）
                double right = lw - 6;
                string space = d.TotalGB > 0 ? DriveText(d) : "";
                if (space.Length > 0)
                {
                    right -= NW(space, 11, false);
                    N(space, right, y + 21, 11, pal.TextTertiary);
                    right -= 10;
                }
                // 读写速度用紧凑单位（↓108K/s ↑2.1M/s）：等宽体比界面字体宽，全称（"108.5 KB/s"）放不下读写两项加空间。
                // 从左往右排，排不下就不画（不叠字）。SSD 温度挪到了页脚（这一行放不下第四项）。
                double rx = N($"↓{Compact(d.ReadSpeed)}", x, y + 21, 11, pal.TextDim) + 10;
                string wr = $"↑{Compact(d.WriteSpeed)}";
                if (rx + NW(wr, 11, false) < right)
                    rx = N(wr, rx, y + 21, 11, pal.TextDim) + 10;
                y += rowH;
            }
        }

        Hairline(y + 3);
        y += 15;

        // ---------- 网速：下行 / 上行各一行，读数在左、走势线铺满余下宽度（走势线是中性色） ----------
        if (Want("net") && s is not null)
        {
            double rx = lw - 6;
            double pingW = 0;
            if (s.PingMs >= 0)
            {
                string ping = $"{s.PingMs} ms";
                Color pc = s.PingMs <= 50 ? pal.TextDim : s.PingMs <= 120 ? pal.Warning : pal.Up;
                pingW = NW(ping, 12, false);
                N(ping, rx - pingW, y + 42, 12, pc);
                pingW += 14;
            }
            double x = N("↓", x0, y + 18, 12, pal.TextDim, true) + 6;
            x = N(MonitorSnapshot.FormatSpeed(s.DownloadSpeed), x, y + 18, 17, pal.Text, true);
            RenderKit.DrawSpark(dc, RenderKit.SafeRect(x + 10, y + 4, rx - x - 18, 16), DownHistory ?? NetHistory, Neutral(0x99), 36);
            double x2 = N("↑", x0, y + 40, 12, pal.TextDim, true) + 6;
            x2 = N(MonitorSnapshot.FormatSpeed(s.UploadSpeed), x2, y + 40, 17, pal.Text, true);
            RenderKit.DrawSpark(dc, RenderKit.SafeRect(x2 + 10, y + 26, rx - pingW - x2 - 18, 16), UpHistory ?? NetHistory, Neutral(0x99), 36);
            y += 48;
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
            if (devRows > 0 && y + devRows * 22 + 30 <= H)
            {
                foreach (var d in externals.Take(devRows))
                {
                    double bl = y + 15;
                    Color vc = d.Charging ? Color.FromRgb(0x3F, 0xC1, 0x5E)
                        : d.LevelText is { } lv ? (lv is "空" or "低" ? pal.Up : lv == "中" ? pal.Warning : pal.Text)
                        : d.Percent <= 20 ? pal.Up
                        : d.Percent <= 40 ? pal.Warning : pal.Text;
                    var glyphT = RenderKit.Text(DeviceService.Glyph(d.Kind), 11, pal.TextDim, true, ppd);
                    RenderKit.Baseline(dc, glyphT, x0, bl);
                    // 读数先占位（右对齐），名字吃剩余宽度（进程榜同款排法）
                    double right = lw - 6;
                    double valueW;
                    if (d.LevelText is { } levelText)
                    {
                        var lvT = RenderKit.Text(levelText, 13, vc, true, ppd);
                        valueW = lvT.WidthIncludingTrailingWhitespace;
                        RenderKit.Baseline(dc, lvT, right - valueW, bl);
                    }
                    else
                    {
                        valueW = RenderKit.NumWidth($"{d.Percent}%", 13, true, ppd);
                        RenderKit.Num(dc, $"{d.Percent}%", right - valueW, bl, 13, vc, true, ppd);
                    }
                    var name = RenderKit.MonoTrimmed(d.Name, 11.5, pal.Text,
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
            double bl = y + 16;
            double rx = lw - 6;
            bool list = Want("topproc") && W - 4 - (lw + 10) >= 110;
            if (s is not null && !list)
            {
                string today = Today(s);
                double tw = NW(today, 11, false);
                N(today, rx - tw, bl, 11, pal.TextTertiary);
                rx -= tw + 14;
            }
            double x = x0;
            // 每一项先量宽度，排得下才画（页脚右端可能还有今日流量）
            string power = th?.CpuPower is { } p && p > 0.01 ? $"{Math.Round(p)} W"
                : th?.GpuPower is { } gp && gp > 0.01 ? $"GPU {Math.Round(gp)} W" : "";
            if (power.Length > 0 && x + 44 + NW(power, 11, true) < rx)
            {
                x = T("功耗 ", x, bl, 11, pal.TextDim);
                x = N(power, x, bl, 11, pal.Text, true) + 16;
            }
            if (th?.MotherboardTemp is { } mb && x + 78 < rx)
            {
                x = T("主板 ", x, bl, 11, pal.TextDim);
                x = Temp(mb, x, bl, 11) + 16;
            }
            if (th?.SsdTemp is { } ssd && x + 68 < rx)
            {
                x = T("SSD ", x, bl, 11, pal.TextDim);
                x = Temp(ssd, x, bl, 11) + 16;
            }
            if (s is { UptimeHours: > 0 } && x + 76 < rx)
            {
                x = T("运行 ", x, bl, 11, pal.TextDim);
                x = N(FormatUptime(s.UptimeHours), x, bl, 11, pal.TextDim) + 16;
            }
            // 本机电池（有电池才显示）：充电绿 / ≤20 红 / ≤40 橙 / 其余文字色
            var bat = s is null ? null : BatteryDevice();
            if (bat is not null && x + 64 < rx)
            {
                Color bc = bat.Charging ? pal.Down : bat.Percent <= 20 ? pal.Up : bat.Percent <= 40 ? pal.Warning : pal.Text;
                x = T("电池 ", x, bl, 11, pal.TextDim);
                x = N($"{bat.Percent}%", x, bl, 11, bc, true) + 4;
                if (bat.Charging) Glyphs.Draw(dc, GlyphKind.Bolt, RenderKit.SafeRect(x, bl - 10, 10, 10), pal.Down);
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
    /// 右栏进程榜：一块半透明卡片（深色主题是 3.5% 的白 + 1px 微光边，暗色科技的卡片配方），
    /// 里面是标题 + 排序页签（纯文字，选中项一条强调色下划线）+ 进程行 + 进程 / 线程数与今日流量。
    /// 进程名和读数用等宽体。
    /// </summary>
    private void DrawTopProcesses(DrawingContext dc, Palette pal, MonitorSnapshot? s,
        double W, double H, double lw, double ppd)
    {
        double cw = W - 6 - (lw + 10);
        if (cw < 124) return;
        double cardX = lw + 10, right = W - 18, rx0 = cardX + 14, rw = right - rx0;
        if (rw < 100) return;
        double cr = pal.SquareCorners ? 3 : 14;
        dc.DrawRoundedRectangle(RenderKit.Faint(pal.Text, 0.035),
            RenderKit.PenOf(pal.Hairline, 1), RenderKit.SafeRect(cardX, 0, cw, H - 2), cr, cr);

        // ---- 标题 + 排序页签 ----
        double x = rx0;
        var title = RenderKit.Text("进程", 10, pal.TextTertiary, true, ppd);
        RenderKit.Baseline(dc, title, x, 14);
        const double tabW = 40;
        double tx = right - tabW * 2;
        _sortToggleRect = RenderKit.SafeRect(tx, 4, tabW * 2, 22);
        // BrimDeck 同款分段控件：整条药丸底 + 选中段浮起的浅药丸（原来是文字 + 强调色下划线）
        dc.DrawRoundedRectangle(RenderKit.Faint(pal.Text, 0.06), null, _sortToggleRect, 11, 11);
        foreach (var (label, on) in new[] { ("CPU", TopSort == "cpu"), ("内存", TopSort == "mem") })
        {
            if (on)
                dc.DrawRoundedRectangle(RenderKit.Faint(pal.Text, 0.14), null,
                    RenderKit.SafeRect(tx + 2, 6, tabW - 4, 18), 9, 9);
            var t = label == "CPU" ? RenderKit.Mono(label, 11, on ? pal.Text : pal.TextTertiary, on, ppd)
                                   : RenderKit.Text(label, 11, on ? pal.Text : pal.TextTertiary, on, ppd);
            RenderKit.Baseline(dc, t, tx + (tabW - t.WidthIncludingTrailingWhitespace) / 2, 19);
            tx += tabW;
        }

        double ry = 42;
        var tops = TopSort == "mem" ? s?.TopProcessesByMem : s?.TopProcesses;
        if (tops is null || tops.Length == 0)
        {
            RenderKit.Baseline(dc, RenderKit.Text("采样中…", 12, pal.TextTertiary, ppd: ppd), rx0, ry + 14);
            ry += 30;
        }
        else
        {
            foreach (var p in tops.Take(6))
            {
                double bl = ry + 17;
                // 读数先占位（右对齐），名称吃剩余宽度
                bool mem = TopSort == "mem";
                string mainText = mem
                    ? (p.MemoryMB >= 1024 ? $"{p.MemoryMB / 1024:F1} GB" : $"{p.MemoryMB:F0} MB")
                    : $"{p.CpuPercent:F1}%";
                Color mainColor = mem ? pal.Text : RenderKit.LevelColor(p.CpuPercent, pal.Text, pal);
                double mainW = RenderKit.NumWidth(mainText, 12.5, true, ppd);
                RenderKit.Num(dc, mainText, right - mainW, bl, 12.5, mainColor, true, ppd);

                double nx = rx0 + 26;
                var name = RenderKit.MonoTrimmed(p.Name, 12, pal.Text, right - mainW - 12 - nx, false, ppd);
                RenderKit.Baseline(dc, name, nx, bl);

                // 真实应用图标（16px 原尺寸，放大就糊；取不到画首字母圆）
                var icon = RenderKit.AppIcon(p.ExePath);
                if (icon is not null)
                {
                    dc.DrawImage(icon, RenderKit.SafeRect(rx0, bl - 13, 16, 16));
                }
                else
                {
                    dc.DrawEllipse(RenderKit.Faint(pal.Text, 0.10), null, new Point(rx0 + 8, bl - 5), 8, 8);
                    var ini = RenderKit.Mono(p.Name.Length > 0 ? char.ToUpper(p.Name[0]).ToString() : "?", 9.5, pal.TextDim, true, ppd);
                    dc.DrawText(ini, new Point(rx0 + 8 - ini.Width / 2, bl - 5 - ini.Height / 2));
                }
                ry += 34;
            }
        }
        if (s is { ProcessCount: > 0 })
        {
            RenderKit.Num(dc, $"{s.ProcessCount} 进程 · {s.ThreadCount} 线程", rx0, ry + 10, 11, pal.TextTertiary, false, ppd);
            RenderKit.Num(dc, Today(s), rx0, ry + 30, 11, pal.TextTertiary, false, ppd);
        }
    }
}
