using System.Windows;
using System.Windows.Media;
using Island.App.Services;
using Island.App.Themes;
using Island.Core.Monitoring;

namespace Island.App.Controls;

/// <summary>
/// 展开面板的系统监控页（参考 BrimDeck MonitorGauges / ftop 布局）：整页自绘，
/// 读数变化只触发重绘、不重新测量布局；文本/画笔/几何走 RenderKit 缓存。
/// 布局：CPU 表盘行（大数字 + 频率/温度 + 走势）→ 核心柱 → MEM 表盘行 + 全宽进度条
///      → GPU 表盘行 → 上下行网速行（带平滑曲线）→ 功耗/风扇 + 流量累计行。
/// 数据由宿主按快照直接注入（UI 线程），每秒一帧。
/// </summary>
public sealed class MonitorPage : FrameworkElement
{
    public MonitorSnapshot? Snapshot { get; set; }
    public ThermalSnapshot? Thermal { get; set; }
    public double[]? CpuHistory { get; set; }
    public double[]? NetHistory { get; set; }
    /// <summary>下行/上行各自的历史（网速行左右两条填充曲线）。</summary>
    public double[]? DownHistory { get; set; }
    public double[]? UpHistory { get; set; }
    public Palette? Palette { get; set; }

    /// <summary>进程榜排序："cpu"（默认）或 "mem"（BrimDeck 右上角分段开关）。</summary>
    public string TopSort { get; private set; } = "cpu";
    private Rect _sortToggleRect;

    // 显示项过滤（null = 全部；宿主由 Settings.MonitorItems 注入）
    public HashSet<string>? Items { get; set; }
    private bool Want(string key) => Items is null || Items.Contains(key);

    /// <summary>非管理员运行时为 true：温度位显示"需管理员"提示而不是"—"。</summary>
    public bool TempNeedAdmin { get; set; }

    private double Ppd => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private static readonly Color CpuC = Color.FromRgb(0x2E, 0x9B, 0xFF);
    private static readonly Color MemC = Color.FromRgb(0x4F, 0xE3, 0xD7);
    private static readonly Color GpuC = Color.FromRgb(0xF2, 0x72, 0xA8);
    private static readonly Color DownC = Color.FromRgb(0x60, 0xC0, 0x78);
    private static readonly Color UpC = Color.FromRgb(0xD8, 0x90, 0x78);
    private static readonly Color PowerC = Color.FromRgb(0xE8, 0xC8, 0x8F);
    private static readonly Color DiskC = Color.FromRgb(0x8F, 0xA8, 0xF0);

    private static string DriveText(DiskStat d)
    {
        static string F(double gb) => gb >= 1024 ? $"{gb / 1024:F1} TB" : $"{gb:F0} GB";
        return $"{d.Name} {F(d.UsedGB)} / {F(d.TotalGB)}";
    }

    /// <summary>温度分级：≤70° 绿 / 71–85° 橙 / >85° 红。</summary>
    private static Color TempColor(double t, Palette pal)
        => t <= 70 ? Color.FromRgb(0x60, 0xC0, 0x78) : t <= 85 ? pal.Warning : pal.Up;

    /// <summary>横向渐变条（BrimDeck 容量条同款：蓝→青渐变，圆头）。</summary>
    private static void GradientBar(DrawingContext dc, Rect r, double frac, Color from, Color to, Palette pal)
    {
        dc.DrawRoundedRectangle(RenderKit.BrushOf(pal.Track), null, r, r.Height / 2, r.Height / 2);
        double w = Math.Clamp(frac, 0, 1) * r.Width;
        if (w < 4) return;
        var grad = new LinearGradientBrush(from, to, 0);
        grad.Freeze();
        dc.DrawRoundedRectangle(grad, null, new Rect(r.X, r.Y, w, r.Height), r.Height / 2, r.Height / 2);
    }

    /// <summary>分段开关点击（进程榜 CPU/内存）。</summary>
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
        double ppd = Ppd;
        // 左右双栏（BrimDeck 监控页式）：左 62% 读数区，右进程榜
        double x0 = 6, lw = W * 0.62, w = lw - 12;
        double y = 2, x;
        const double dial = 26;

        // ---------- CPU 行：表盘 + 大数字 + 频率/温度 + 右侧走势曲线 ----------
        if (Want("cpu"))
        {
            RenderKit.Dial(dc, new Point(x0 + dial / 2, y + dial / 2), dial, 2.6,
                s is null ? null : s.CpuUsage / 100, pal.Track, CpuC, pal.Text, "C", ppd);
            x = x0 + dial + 10;
            double baseline = Math.Round(y + dial / 2 + 21 * 0.36);
            var num = RenderKit.Text(s is null ? "—" : $"{Math.Round(s.CpuUsage)}", 21, pal.Text, true, ppd);
            RenderKit.Baseline(dc, num, x, baseline); x += num.WidthIncludingTrailingWhitespace + 2;
            var pctU = RenderKit.Text("%", 11, pal.TextDim, ppd: ppd);
            RenderKit.Baseline(dc, pctU, x, baseline); x += pctU.WidthIncludingTrailingWhitespace + 10;
            if (s is not null)
            {
                if (s.CpuFrequencyGHz > 0.1)
                {
                    var d = RenderKit.Text($"{s.CpuFrequencyGHz:F1} GHz", 11, pal.TextDim, ppd: ppd);
                    RenderKit.Baseline(dc, d, x, baseline); x += d.WidthIncludingTrailingWhitespace + 10;
                }
                double? ct = th?.CpuTemp;
                var dt = RenderKit.Text(
                    ct is { } t2 ? $"{Math.Round(t2)}°" : TempNeedAdmin ? "需管理员" : "—", 11,
                    ct is { } t3 ? TempColor(t3, pal) : TempNeedAdmin ? pal.Warning : pal.TextTertiary, true, ppd);
                RenderKit.Baseline(dc, dt, x, baseline);
            }
            RenderKit.DrawSpark(dc, new Rect(lw * 0.52, y, lw - 6 - lw * 0.52, 26), CpuHistory, CpuC, 36);
            y += 34;
        }

        // ---------- 核心柱（ftop 风格：大圆角柱，轨道+填充，按核心各自分级配色） ----------
        // ---------- 核心柱（ftop 风格：大圆角柱，轨道+填充，按核心各自分级配色） ----------
        var cores = s?.CoreUsages;
        if (Want("cores") && cores is not null && cores.Length > 0)
        {
            int n = Math.Min(16, cores.Length);
            double gap = 4, bw = (w - gap * (n - 1)) / n, bh = 42;
            for (int i = 0; i < n; i++)
            {
                double v = Math.Clamp(cores[i] / 100.0, 0, 1);
                double bx = x0 + i * (bw + gap);
                dc.DrawRoundedRectangle(RenderKit.BrushOf(pal.Track), null, new Rect(bx, y, bw, bh), 3, 3);
                double fill = Math.Max(2, v * bh);
                dc.DrawRoundedRectangle(
                    RenderKit.BrushOf(RenderKit.LevelColor(v * 100, v > 0.6 ? CpuC : MemC, pal)), null,
                    new Rect(bx, y + bh - fill, bw, fill), 3, 3);
            }
            y += 50;
        }

        // ---------- MEM 行：表盘 + 用量文本 + 全宽进度条 ----------
        if (Want("mem"))
        {
            RenderKit.Dial(dc, new Point(x0 + dial / 2, y + dial / 2), dial, 2.6,
                s is null ? null : s.MemoryUsagePercent / 100, pal.Track, MemC, pal.Text, "M", ppd);
            if (s is not null)
            {
                x = x0 + dial + 10;
                double bl = Math.Round(y + dial / 2 + 15 * 0.36);
                var used = RenderKit.Text($"{s.MemoryUsedGB:F1}", 15, pal.Text, true, ppd);
                var tot = RenderKit.Text($" / {s.MemoryTotalGB:F0} GB", 11, pal.TextDim, ppd: ppd);
                RenderKit.Baseline(dc, used, x, bl); x += used.WidthIncludingTrailingWhitespace;
                RenderKit.Baseline(dc, tot, x, bl); x += tot.WidthIncludingTrailingWhitespace + 10;
                // 提交内存（commit charge，pagefile 口径）
                if (s.CommitLimitGB > 0.1)
                {
                    double cpct = s.CommitGB / s.CommitLimitGB * 100;
                    var cm = RenderKit.Text($"提交 {s.CommitGB:F0}/{s.CommitLimitGB:F0} GB", 10,
                        RenderKit.LevelColor(cpct, pal.TextTertiary, pal), ppd: ppd);
                    RenderKit.Baseline(dc, cm, x, bl);
                }
                var mp = RenderKit.Text($"{Math.Round(s.MemoryUsagePercent)}%", 11,
                    RenderKit.LevelColor(s.MemoryUsagePercent, pal.TextDim, pal), true, ppd);
                RenderKit.Baseline(dc, mp, lw - 6 - mp.WidthIncludingTrailingWhitespace, bl);
            }
            y += 30;
            double memFrac = s is null ? 0 : Math.Clamp(s.MemoryUsagePercent / 100, 0, 1);
            // BrimDeck 同款渐变容量条（蓝→青，圆头，略加粗到 6px）
            GradientBar(dc, new Rect(x0, y, w, 6), memFrac, CpuC, MemC, pal);
            y += 15;
        }

        // ---------- GPU 行：表盘 + 读数 + 温度 + 右侧显卡名 ----------
        // GPU 行常显（核显空闲时 GpuUsage=0 也保留温度/显存/显卡名，避免整行消失）
        if (Want("gpu") && s is not null)
        {
            RenderKit.Dial(dc, new Point(x0 + dial / 2, y + dial / 2), dial, 2.6,
                s.GpuUsage / 100, pal.Track, GpuC, pal.Text, "G", ppd);
            x = x0 + dial + 10;
            double bl = Math.Round(y + dial / 2 + 15 * 0.36);
            var gnum = RenderKit.Text($"{Math.Round(s.GpuUsage)}%", 15, pal.Text, true, ppd);
            RenderKit.Baseline(dc, gnum, x, bl); x += gnum.WidthIncludingTrailingWhitespace + 10;
            double? gt = th?.GpuTemp;
            var gtT = RenderKit.Text(
                gt is { } g2 ? $"{Math.Round(g2)}°" : TempNeedAdmin ? "需管理员" : "—", 11,
                gt is { } g3 ? TempColor(g3, pal) : TempNeedAdmin ? pal.Warning : pal.TextTertiary, true, ppd);
            RenderKit.Baseline(dc, gtT, x, bl); x += gtT.WidthIncludingTrailingWhitespace + 10;
            // 显存（LiteMonitor 同款：D3D 专用/共享）
            if (th?.GpuMemoryUsedGB is { } gmu)
            {
                var gm = RenderKit.Text(
                    $"显存 {gmu:F1}{(th.GpuMemoryTotalGB is { } gtot ? $"/{gtot:F0}" : "")} GB",
                    10, pal.TextDim, ppd: ppd);
                RenderKit.Baseline(dc, gm, x, bl);
            }
            if (!string.IsNullOrEmpty(s.GpuName))
            {
                var name = RenderKit.Trimmed(s.GpuName, 10, pal.TextTertiary, lw * 0.3, false, ppd);
                RenderKit.Baseline(dc, name, lw - 6 - name.WidthIncludingTrailingWhitespace, bl);
            }
            y += 32;
        }

        // ---------- 硬盘行（每块物理盘一行）：盘符表盘 + 活动时间% + 读/写速度 + 右侧空间 ----------
        if (Want("disk") && s is not null)
        {
            // 优先逐盘实例；计数器实例缺失时回退单行（系统盘/_Total）
            var rows = new List<DiskStat>();
            if (s.Disks is { Length: > 0 } dl) rows.AddRange(dl);
            if (rows.Count == 0)
                rows.Add(new DiskStat
                {
                    Name = s.DriveName, ActivePercent = s.DiskUsagePercent,
                    ReadSpeed = s.DiskReadSpeed, WriteSpeed = s.DiskWriteSpeed,
                    UsedGB = s.DriveUsedGB, TotalGB = s.DriveTotalGB,
                });

            int rowIdx = 0;
            foreach (var d in rows.Take(3))   // 版面最多 3 行（面板高度固定，C/D/E 全覆盖）
            {
                // 表盘字母 = 盘符首字符（C/D/E…），一眼认盘
                string letter = d.Name.Length > 0 ? d.Name[..1] : "D";
                RenderKit.Dial(dc, new Point(x0 + dial / 2, y + dial / 2), dial, 2.6,
                    d.ActivePercent / 100, pal.Track, DiskC, pal.Text, letter, ppd);
                x = x0 + dial + 10;
                double bl = Math.Round(y + dial / 2 + 15 * 0.36);
                var dnum = RenderKit.Text($"{Math.Round(d.ActivePercent)}%", 15,
                    RenderKit.LevelColor(d.ActivePercent, pal.Text, pal), true, ppd);
                RenderKit.Baseline(dc, dnum, x, bl); x += dnum.WidthIncludingTrailingWhitespace + 12;
                var rw = RenderKit.Text(
                    $"读 {MonitorSnapshot.FormatSpeed(d.ReadSpeed)}  写 {MonitorSnapshot.FormatSpeed(d.WriteSpeed)}",
                    10, pal.TextDim, ppd: ppd);
                RenderKit.Baseline(dc, rw, x, bl);
                x += rw.WidthIncludingTrailingWhitespace + 10;
                // SSD 温度（BrimDeck 驱动器行同款：字母后跟温度，分级配色）
                if (ReferenceEquals(d, rows[0]) && th?.SsdTemp is { } st)
                {
                    var stT = RenderKit.Text($"{Math.Round(st)}°", 11, TempColor(st, pal), true, ppd);
                    RenderKit.Baseline(dc, stT, x, bl);
                }
                if (d.TotalGB > 0)
                {
                    var drv = RenderKit.Text(DriveText(d), 10, pal.TextTertiary, ppd: ppd);
                    RenderKit.Baseline(dc, drv, lw - 6 - drv.WidthIncludingTrailingWhitespace, bl);
                }
                y += 32;
                // 系统盘（首行）下方加容量渐变条（BrimDeck 磁盘行同款）
                if (rowIdx == 0 && d.TotalGB > 0.1)
                {
                    GradientBar(dc, new Rect(x0 + dial + 10, y - 7, w - dial - 10, 5),
                        d.UsedGB / d.TotalGB, MemC, DiskC, pal);
                    y += 8;
                }
                rowIdx++;
            }
        }

        // ---------- 网速行（BrimDeck 同款：左右两条填充曲线，↓ 绿 / ↑ 橙） ----------
        if (Want("net") && s is not null)
        {
            double half = (w - 16) / 2;
            // 左：下行
            var di = RenderKit.Text("↓", 12, DownC, true, ppd);
            RenderKit.Baseline(dc, di, x0, Math.Round(y + 13));
            var dr = RenderKit.Text(MonitorSnapshot.FormatSpeed(s.DownloadSpeed), 12, pal.Text, true, ppd);
            RenderKit.Baseline(dc, dr, x0 + 14, Math.Round(y + 13));
            double sparkX = x0 + 14 + dr.WidthIncludingTrailingWhitespace + 8;
            if (half - (sparkX - x0) > 30)
                RenderKit.DrawSpark(dc, new Rect(sparkX, y + 2, half - (sparkX - x0), 15), DownHistory ?? NetHistory, DownC, 36);

            // 右：上行
            double x2 = x0 + half + 16;
            var ui = RenderKit.Text("↑", 12, UpC, true, ppd);
            RenderKit.Baseline(dc, ui, x2, Math.Round(y + 13));
            var ur = RenderKit.Text(MonitorSnapshot.FormatSpeed(s.UploadSpeed), 12, pal.Text, true, ppd);
            RenderKit.Baseline(dc, ur, x2 + 14, Math.Round(y + 13));
            double sparkX2 = x2 + 14 + ur.WidthIncludingTrailingWhitespace + 8;
            // ping 在右缘，曲线到 ping 为止
            double rightReserve = 0;
            if (s.PingMs >= 0)
            {
                var pc = s.PingMs <= 50 ? DownC : s.PingMs <= 120 ? pal.Warning : pal.Up;
                var ping = RenderKit.Text($"ping {s.PingMs}ms", 10, pc, ppd: ppd);
                RenderKit.Baseline(dc, ping, lw - 6 - ping.WidthIncludingTrailingWhitespace, Math.Round(y + 13));
                rightReserve = ping.WidthIncludingTrailingWhitespace + 10;
            }
            if (x0 + w - rightReserve - sparkX2 > 30)
                RenderKit.DrawSpark(dc, new Rect(sparkX2, y + 2, x0 + w - rightReserve - sparkX2, 15), UpHistory ?? NetHistory, UpC, 36);
            y += 28;
        }

        // ---------- 功耗/风扇行 + 右侧流量累计 ----------
        if (Want("power"))
        {
            double bl = Math.Round(y + 13);
            var bolt = RenderKit.Text("⚡", 12, PowerC, ppd: ppd);
            RenderKit.Baseline(dc, bolt, x0, bl);
            x = x0 + 16;
            string power = th?.CpuPower is { } p && p > 0.01 ? $"{Math.Round(p)} W"
                : th?.GpuPower is { } gp && gp > 0.01 ? $"GPU {Math.Round(gp)} W" : "—";
            var pw = RenderKit.Text(power, 12, pal.Text, true, ppd);
            RenderKit.Baseline(dc, pw, x, bl); x += pw.WidthIncludingTrailingWhitespace + 14;
            string fan = (th?.CpuFanRpm ?? th?.GpuFanRpm) is { } f && f > 0.01
                ? $"风扇 {Math.Round(f)} rpm" : "";
            if (fan.Length > 0)
            {
                var ft = RenderKit.Text(fan, 10, pal.TextDim, ppd: ppd);
                RenderKit.Baseline(dc, ft, x, bl); x += ft.WidthIncludingTrailingWhitespace + 14;
            }
            // 主板温度（LiteMonitor HOST 组同款）
            if (th?.MotherboardTemp is { } mb)
            {
                var mbT = RenderKit.Text($"主板 {Math.Round(mb)}°", 10, TempColor(mb, pal), ppd: ppd);
                RenderKit.Baseline(dc, mbT, x, bl); x += mbT.WidthIncludingTrailingWhitespace + 14;
            }
            // 系统运行时长（免管理员）
            if (s is not null && s.UptimeHours > 0)
            {
                var up = RenderKit.Text($"运行 {FormatUptime(s.UptimeHours)}", 10, pal.TextDim, ppd: ppd);
                RenderKit.Baseline(dc, up, x, bl); x += up.WidthIncludingTrailingWhitespace + 14;
            }
            // 本机电池（BrimDeck 底行同款：分级色百分比 + 充电闪电）
            var bat = DeviceService.Snapshot.FirstOrDefault(d => d.Kind == "battery");
            if (bat is not null)
            {
                var bc = bat.Charging ? Color.FromRgb(0x3F, 0xC1, 0x5E)
                       : bat.Percent <= 20 ? pal.Up
                       : bat.Percent <= 40 ? pal.Warning : pal.Text;
                var bt = RenderKit.Text($"{(bat.Charging ? "⚡" : "")}电池 {bat.Percent}%", 10, bc, true, ppd);
                RenderKit.Baseline(dc, bt, x, bl);
            }
            if (s is not null)
            {
                var sess = RenderKit.Text(s.SessionText, 9.5, pal.TextTertiary, ppd: ppd);
                RenderKit.Baseline(dc, sess, lw - 6 - sess.WidthIncludingTrailingWhitespace, bl);
            }
        }

        if (Want("topproc")) DrawTopProcesses(dc, pal, s, W, H, lw, ppd);
    }

    /// <summary>运行时长人性化：&lt;1h 显示分钟，&lt;48h 显示 x.x h，否则 x 天。</summary>
    private static string FormatUptime(double hours)
    {
        if (hours < 1) return $"{(int)(hours * 60)} 分钟";
        if (hours < 48) return $"{hours:F1} h";
        return $"{hours / 24:F1} 天";
    }

    /// <summary>右栏：进程榜（BrimDeck 同款：CPU/内存分段开关 + 真实应用图标 + 分级配色读数）。</summary>
    private void DrawTopProcesses(DrawingContext dc, Palette pal, MonitorSnapshot? s,
        double W, double H, double lw, double ppd)
    {
        double rx0 = lw + 14, rw = W - 6 - rx0;
        if (rw < 110) return;
        // 分栏发丝线
        dc.DrawLine(RenderKit.PenOf(pal.Track, 1, 0.7), new Point(lw + 4, 6), new Point(lw + 4, H - 8));

        // ---- 分段开关：CPU | 内存（胶囊两段，选中段主题色浅底） ----
        const double segW = 74;
        _sortToggleRect = new Rect(W - 6 - segW, 4, segW, 20);
        dc.DrawRoundedRectangle(RenderKit.BrushOf(pal.Track), null, _sortToggleRect, 10, 10);
        double halfW = segW / 2;
        var selRect = new Rect(_sortToggleRect.X + (TopSort == "cpu" ? 1 : halfW), 5, halfW - 1, 18);
        dc.DrawRoundedRectangle(new SolidColorBrush(pal.Accent) { Opacity = 0.28 }, null, selRect, 9, 9);
        var cpuT = RenderKit.Text("CPU", 9.5, TopSort == "cpu" ? pal.Text : pal.TextTertiary, TopSort == "cpu", ppd);
        var memT = RenderKit.Text("内存", 9.5, TopSort == "mem" ? pal.Text : pal.TextTertiary, TopSort == "mem", ppd);
        dc.DrawText(cpuT, new Point(_sortToggleRect.X + halfW / 2 - cpuT.Width / 2, 6 + (18 - cpuT.Height) / 2 + 1));
        dc.DrawText(memT, new Point(_sortToggleRect.X + halfW + halfW / 2 - memT.Width / 2, 6 + (18 - memT.Height) / 2 + 1));

        double ry = 28;
        // 进程/线程总量（PDH System 计数器，免管理员）
        if (s is not null && s.ProcessCount > 0)
        {
            var counts = RenderKit.Text(
                $"共 {s.ProcessCount} 进程 · {s.ThreadCount} 线程", 9, pal.TextTertiary, ppd: ppd);
            RenderKit.Baseline(dc, counts, rx0, Math.Round(ry + 8));
            ry += 16;
        }

        var tops = TopSort == "mem" ? s?.TopProcessesByMem : s?.TopProcesses;
        if (tops is null || tops.Length == 0)
        {
            var wait = RenderKit.Text("采样中…", 9.5, pal.TextTertiary, ppd: ppd);
            RenderKit.Baseline(dc, wait, rx0, Math.Round(ry + 11));
            return;
        }
        foreach (var p in tops.Take(5))
        {
            double rowCy = ry + 8;
            // 真实应用图标（SHGetFileInfo 缓存；取不到画首字母圆）
            var icon = RenderKit.AppIcon(p.ExePath);
            if (icon is not null)
            {
                dc.DrawImage(icon, new Rect(rx0, rowCy - 7, 14, 14));
            }
            else
            {
                dc.DrawEllipse(RenderKit.BrushOf(pal.Track), null, new Point(rx0 + 7, rowCy), 7, 7);
                var ini = RenderKit.Text(p.Name.Length > 0 ? p.Name[..1].ToUpper() : "?", 8, pal.TextDim, true, ppd);
                dc.DrawText(ini, new Point(rx0 + 7 - ini.Width / 2, rowCy - ini.Height / 2));
            }

            // 主读数（按排序档：CPU% 分级配色 / 内存 MB）
            string mainText; Color mainColor;
            if (TopSort == "mem")
            {
                mainText = p.MemoryMB >= 1024 ? $"{p.MemoryMB / 1024:F1} GB" : $"{p.MemoryMB:F0} MB";
                mainColor = pal.Text;
            }
            else
            {
                mainText = $"{p.CpuPercent:F1}%";
                mainColor = RenderKit.LevelColor(p.CpuPercent, pal.Text, pal);
            }
            var main = RenderKit.Text(mainText, 11, mainColor, true, ppd);
            double bl = Math.Round(ry + 15);
            RenderKit.Baseline(dc, main, W - 6 - main.WidthIncludingTrailingWhitespace, bl);
            var name = RenderKit.Trimmed(p.Name, 11, pal.Text,
                rw - 22 - main.WidthIncludingTrailingWhitespace - 8, false, ppd);
            RenderKit.Baseline(dc, name, rx0 + 20, bl);
            ry += 23;
        }
    }
}
