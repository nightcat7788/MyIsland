using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Island.App.Controls;
using Island.App.Services;
using Island.App.Themes;
using Island.Core.Capabilities;
using Island.Core.Monitoring;

namespace Island.Tools.VisualProbe;

/// <summary>一条渲染不变量的判定结果（名字 / 是否通过 / 实测数值）。</summary>
public sealed record ProbeCheck(string Name, bool Passed, string Detail);

/// <summary>探针产物：若干张渲染图 + 若干条不变量判定。</summary>
public sealed class ProbeReport
{
    public List<ProbeCheck> Checks { get; } = new();
    public List<(string Name, BitmapSource Bitmap)> Images { get; } = new();

    /// <summary>全部不变量都通过（探针退出码 / 测试门禁都看它）。</summary>
    public bool AllPassed => Checks.All(c => c.Passed);
}

/// <summary>探针参数。</summary>
public sealed class ProbeOptions
{
    /// <summary>渲染画布宽（DIP）—— 560 是 mac 口径的内容宽，卡片宽正好 256。</summary>
    public int Width { get; init; } = 560;

    /// <summary>渲染画布高（DIP）—— 必须 >= 349 才走"完整档"栅格（181 + 12 + 156）。</summary>
    public int Height { get; init; } = 420;

    /// <summary>主题（默认深色）。</summary>
    public Palette? Palette { get; init; }

    /// <summary>PNG 输出目录；null = 只判定不落盘（测试门禁走这条）。</summary>
    public string? OutputDirectory { get; init; }
}

/// <summary>
/// 离屏渲染视觉回归探针的**核心**：渲染被测控件 + 判定渲染不变量。
///
/// ⚠️ 覆盖范围：监控页 + 设备页（两个纯自绘的 FrameworkElement）。
/// **展开面板整体（ExpandedPanel）没有覆盖**，原因是它离屏渲染不可行（不是"懒得做"）：
///   · ExpandedPanel.xaml 的资源字典写死了 <c>/Island.App;component/Controls/PanelStyles.xaml</c>，
///     那是**按程序集名**寻址的编译期资源 URI —— 只有真的加载 Island.App 程序集才能解析；
///   · 要加载 Island.App 就得 ProjectReference 它，而本工程刻意**只链接源文件**：
///     同一份 MonitorPage 源码一旦既被链接进来、又被 Island.App.dll 带进来，
///     类型重名会产生 CS0436 警告，门禁要求 0 警告；
///   · 展开面板还牵着剪贴板服务、控制服务（电源计划子进程）、能力清单等一串运行期依赖，
///     离屏渲染它们会把"渲染一张图"变成"启动半个应用"。
///   ⇒ 展开面板的验证仍靠真机截图；监控页 / 设备页这两个**纯绘制**的页面在这里拿门禁。
///
/// 设计要点（都是踩过的坑，别改回去）：
///  · **不建窗口**：元素 Measure/Arrange 之后直接 Render。锁屏会话里"挂到已显示窗口上再
///    RenderTargetBitmap"会得到全黑，无窗口直渲染才有效（见 docs/改进记录.md 十三节）。
///  · **必须 STA**：WPF 的离屏渲染要在 STA 线程上做；本类自己开一条 STA 工作线程，
///    调用方（控制台工具 / 测试门禁）的线程状态不影响结果。
///  · **DpiScale = 1.0**：位图 96 DPI 且像素尺寸 == DIP 尺寸，1 DIP = 1 像素，
///    几何断言里的坐标直接就是像素坐标。
///  · **合成数据**：读数一律来自合成的快照 / 合成设备列表，渲染结果确定、可做回归基线。
/// </summary>
public static class VisualProbeCore
{
    // ---------- 像素判定阈值（见报告：实测值都留了余量） ----------
    /// <summary>"有绘制"阈值：与底色的通道最大差 >= 2。卡片 3.5% 白底已超过它。</summary>
    private const int PaintedDelta = 2;

    /// <summary>"是描边"阈值：发丝线（alpha 0x24）叠在卡片底上后的通道最大差。</summary>
    private const int BorderDelta = 15;

    /// <summary>"是文字/图形"阈值：按绝对亮度算（文字 ~242、强调色 ~212，卡片底与描边都远低于它）。</summary>
    private const int InkChannel = 160;

    /// <summary>整体不变量：非背景像素占比下限（防"整张全黑"回归）。</summary>
    private const double MinPaintedRatio = 0.02;

    /// <summary>整体不变量：高亮内容像素占比下限（防"只画出卡片底、没有文字"）。</summary>
    private const double MinInkRatio = 0.0005;

    /// <summary>
    /// 真机展开面板给监控页的尺寸（宽 504 = 540 - 18×2；高 328 = 400 - 14 - 18 - 24 - 16）。
    /// 紧凑档就是为这个高度准备的 —— 状态行也必须在这里放得下。
    /// </summary>
    private const int CompactPanelWidth = 504;
    private const int CompactPanelHeight = 328;

    /// <summary>
    /// 跑一遍探针：渲染两个页面 + 判定全部不变量。
    /// 全程在一条 STA 工作线程上完成。
    /// </summary>
    public static ProbeReport Run(ProbeOptions options)
    {
        ProbeReport? report = null;
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            try { report = RunOnStaThread(options); }
            catch (Exception ex) { failure = ex; }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.IsBackground = true;
        worker.Start();
        worker.Join();

        if (failure != null) throw failure;
        return report!;
    }

    private static ProbeReport RunOnStaThread(ProbeOptions options)
    {
        var report = new ProbeReport();
        var palette = options.Palette ?? Palette.Dark;

        // ---------- 监控页（2×2 卡片栅格） ----------
        var monitor = new MonitorPage
        {
            Palette = palette,
            Snapshot = SyntheticSnapshot(),
            Thermal = SyntheticThermal(),
            CpuHistory = SyntheticHistory(36),
            NetHistory = SyntheticHistory(36, 0.5),
            DownHistory = SyntheticHistory(36, 0.3),
            UpHistory = SyntheticHistory(36, 0.8),
        };
        var monitorBitmap = Render(monitor, options.Width, options.Height, palette);
        report.Images.Add(("monitor-page", monitorBitmap));
        CheckMonitorPage(report, monitorBitmap, options);
        // 分组门禁（两端统一的卡片分组，2026-11-26 task16）
        CheckMonitorPageGrouping(report, monitorBitmap, options);

        // ---------- 监控页（真机尺寸：紧凑档） ----------
        // 完整档（420 高）放得下 ≠ 真机放得下。真机的展开面板是 540×380 ⇒ 监控页只有
        // 540 - 18×2 = 504 宽、380 - 14 - 18 - 24（顶栏）- 16（顶栏下留白）= 308 高。
        // 这一档（紧凑档）必须**连状态行一起**放得下 —— 单独渲染一次来盯住它。
        var compact = new MonitorPage
        {
            Palette = palette,
            Snapshot = SyntheticSnapshot(),
            Thermal = SyntheticThermal(),
            CpuHistory = SyntheticHistory(36),
            NetHistory = SyntheticHistory(36, 0.5),
            DownHistory = SyntheticHistory(36, 0.3),
            UpHistory = SyntheticHistory(36, 0.8),
        };
        var compactBitmap = Render(compact, CompactPanelWidth, CompactPanelHeight, palette);
        report.Images.Add(("monitor-page-compact", compactBitmap));
        CheckMonitorPageCompact(report, compactBitmap);
        // 分组门禁在真机尺寸（让位档）下同样成立
        CheckMonitorPageGrouping(report, compactBitmap, new ProbeOptions
        {
            Width = CompactPanelWidth,
            Height = CompactPanelHeight,
            Palette = palette,
        });

        // ---------- 设备页（卡片网格） ----------
        var devices = new DevicesPage { Palette = palette };
        var devicesBitmap = Render(devices, options.Width, options.Height, palette);
        report.Images.Add(("devices-page", devicesBitmap));
        CheckDevicesPage(report, devicesBitmap, options);

        // ---------- 落盘（工具侧；测试门禁不落盘） ----------
        if (!string.IsNullOrEmpty(options.OutputDirectory))
        {
            Directory.CreateDirectory(options.OutputDirectory);
            foreach (var (name, bitmap) in report.Images)
            {
                string path = Path.Combine(options.OutputDirectory, name + ".png");
                SavePng(bitmap, path);

                // 落盘复核：**从文件读回来**再判一次"非全黑"——
                // 内存里的位图没黑，不代表写出来的 PNG 没黑。
                var reloaded = LoadPng(path);
                var pixels = new BitmapPixels(reloaded, palette.Surface);
                double ratio = pixels.PaintedRatio();
                report.Checks.Add(new ProbeCheck(
                    $"PNG 产物非全黑（{name}.png 落盘复核）",
                    ratio > MinPaintedRatio,
                    $"重读 {path}：非背景像素占比 {ratio:P2}（下限 {MinPaintedRatio:P0}）"));
            }
        }

        return report;
    }

    // ================= 合成数据 =================

    /// <summary>
    /// 合成的监控快照：读数固定 ⇒ 渲染结果确定 ⇒ 可以做回归基线。
    /// 不读真硬件（真硬件每台机器、每个时刻都不一样，PNG 就没法当基线比）。
    /// </summary>
    public static MonitorSnapshot SyntheticSnapshot() => new()
    {
        Timestamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local),
        CpuUsage = 42,
        CpuFrequencyGHz = 3.7,
        MemoryUsagePercent = 63,
        MemoryUsedGB = 9.9,
        MemoryTotalGB = 16,
        GpuUsage = 18,
        GpuName = "集成显卡",
        DiskUsagePercent = 7,
        DriveUsedGB = 220,
        DriveTotalGB = 512,
        DriveName = "C:",
        Disks = new[] { new DiskStat { Name = "C:", ActivePercent = 7, UsedGB = 220, TotalGB = 512 } },
        TopProcesses = new[]
        {
            new TopProcess { Name = "Code.exe", CpuPercent = 22.5, MemoryMB = 1240, ExePath = "" },
            new TopProcess { Name = "chrome.exe", CpuPercent = 9.5, MemoryMB = 860, ExePath = "" },
            new TopProcess { Name = "MyIsland.exe", CpuPercent = 3.2, MemoryMB = 210, ExePath = "" },
            new TopProcess { Name = "explorer.exe", CpuPercent = 1.1, MemoryMB = 140, ExePath = "" },
            new TopProcess { Name = "Teams.exe", CpuPercent = 0.8, MemoryMB = 320, ExePath = "" },
        },
        TopProcessesByMem = new[]
        {
            new TopProcess { Name = "Code.exe", CpuPercent = 22.5, MemoryMB = 1240, ExePath = "" },
            new TopProcess { Name = "chrome.exe", CpuPercent = 9.5, MemoryMB = 860, ExePath = "" },
            new TopProcess { Name = "Teams.exe", CpuPercent = 0.8, MemoryMB = 320, ExePath = "" },
            new TopProcess { Name = "MyIsland.exe", CpuPercent = 3.2, MemoryMB = 210, ExePath = "" },
            new TopProcess { Name = "explorer.exe", CpuPercent = 1.1, MemoryMB = 140, ExePath = "" },
        },
        BatteryPercent = 88,
        BatteryCharging = false,
        CpuTemp = 55,
        GpuTemp = 48,
        SsdTemp = 42,
        DownloadSpeed = 1_572_864,
        UploadSpeed = 131_072,
        SessionDownload = 12_884_901_888,
        SessionUpload = 1_073_741_824,
        TodayDownload = 42_949_672_960,
        TodayUpload = 5_368_709_120,
        LinkType = "wifi",
        UptimeHours = 6.5,
        ProcessCount = 213,
        ThreadCount = 1580,
        CommitGB = 11.2,
        CommitLimitGB = 25.6,
        PingMs = 18,
    };

    /// <summary>合成的温度快照。</summary>
    public static ThermalSnapshot SyntheticThermal() => new()
    {
        CpuTemp = 55,
        GpuTemp = 48,
        SsdTemp = 42,
        MotherboardTemp = 45,
        GpuMemoryUsedGB = 1.5,
        GpuMemoryTotalGB = 8,
        UpdatedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local),
    };

    /// <summary>合成的历史序列（固定公式，不用 Random ⇒ 每次渲染结果一致）。</summary>
    public static double[] SyntheticHistory(int count, double phase = 0)
    {
        var data = new double[count];
        for (int i = 0; i < count; i++)
            data[i] = 0.5 + 0.4 * Math.Sin((i + phase) * 0.7);
        return data;
    }

    // ================= 渲染 =================

    /// <summary>
    /// 离屏渲染一个控件：铺一层主题底色（面板本来就有底），再 Measure/Arrange/Render。
    /// 顺序不能换：先 Render 会得到零尺寸或全黑。
    /// </summary>
    public static BitmapSource Render(FrameworkElement element, int width, int height, Palette palette)
    {
        var host = new Grid
        {
            Background = new SolidColorBrush(palette.Surface),
            Width = width,
            Height = height,
        };
        host.Children.Add(element);

        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();

        // 96 DPI + 像素尺寸 == DIP 尺寸 ⇒ DpiScale 1.0（1 DIP = 1 像素，坐标可直接当像素用）
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>存 PNG。</summary>
    public static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>读回 PNG（落盘复核用）。</summary>
    public static BitmapSource LoadPng(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    // ================= 监控页不变量 =================

    private static void CheckMonitorPage(ProbeReport report, BitmapSource bitmap, ProbeOptions options)
    {
        var pixels = new BitmapPixels(bitmap, (options.Palette ?? Palette.Dark).Surface);
        int w = options.Width, h = options.Height;

        // ---- 1. 整体：不是全黑 / 有实际内容 ----
        double painted = pixels.PaintedRatio();
        report.Checks.Add(new ProbeCheck(
            "监控页：渲染结果非全黑（非背景像素占比 > 2%）",
            painted > MinPaintedRatio,
            $"非背景像素 {pixels.PaintedCount()}/{w * h} = {painted:P2}（下限 {MinPaintedRatio:P0}）"));

        double ink = pixels.InkRatio();
        report.Checks.Add(new ProbeCheck(
            "监控页：有高亮内容（文字/图标像素占比 > 0.05%）",
            ink > MinInkRatio,
            $"高亮像素 {pixels.InkCount()}/{w * h} = {ink:P3}（下限 {MinInkRatio:P3}）"));

        // ---- 2. 栅格常量（读 MonitorPage 的私有常量 ⇒ 断言的是产品源码，不是副本） ----
        double pad = ConstOf(typeof(MonitorPage), "GridPad");
        double gap = ConstOf(typeof(MonitorPage), "GridGap");
        double head = ConstOf(typeof(MonitorPage), "CardHeadH");
        double padX = ConstOf(typeof(MonitorPage), "CardPadX");
        double topH = ConstOf(typeof(MonitorPage), "TopCardH");
        double botH = ConstOf(typeof(MonitorPage), "BotCardH");
        double cardW = (w - pad * 2 - gap) / 2;

        report.Checks.Add(new ProbeCheck(
            "监控页：栅格常量与 mac 口径一致（卡头 42 / 上排 181 / 下排 156 / 间距 12 / 内缩 15）",
            head == 42 && topH == 181 && botH == 156 && gap == 12 && padX == 15,
            $"CardHeadH={head} TopCardH={topH} BotCardH={botH} GridGap={gap} CardPadX={padX}"));

        report.Checks.Add(new ProbeCheck(
            "监控页：卡片宽 256（内容宽 560 口径）",
            cardW == 256,
            $"内容宽 {w} ⇒ 卡片宽 (w - 2×{pad} - {gap})/2 = {cardW}"));

        // ---- 3. 四张卡的矩形：互不相交 + 都在内容区内 ----
        // 卡片矩形**由产品代码按共享规格算出来**（MonitorPage.PlannedCards）——
        // 探针不抄一份卡片清单（抄了就会与产品代码漂移，门禁变假）。
        double x1 = pad, x2 = pad + cardW + gap, y1 = 0, y2 = topH + gap;
        var cards = MonitorPage.PlannedCards(w, h)
            .Select(c => (Name: c.Title, X: c.Rect.Left, Y: c.Rect.Top, W: c.Rect.Width, H: c.Rect.Height))
            .ToArray();

        bool disjoint = true;
        for (int i = 0; i < cards.Length; i++)
            for (int j = i + 1; j < cards.Length; j++)
                disjoint &= !Overlaps(cards[i], cards[j]);
        report.Checks.Add(new ProbeCheck(
            "监控页：四张卡片互不重叠",
            disjoint,
            $"左列 x={x1} / 右列 x={x2}（列宽 {cardW}，列间距 {gap}）；上排 y={y1} / 下排 y={y2}（行高 {topH}/{botH}）"));

        bool inside = cards.All(c => c.X >= 0 && c.Y >= 0 && c.X + c.W <= w && c.Y + c.H <= h);
        report.Checks.Add(new ProbeCheck(
            "监控页：四张卡片都在内容区内（不越界）",
            inside,
            $"内容区 {w}×{h}；卡片底边 y={y2 + botH}"));

        // ---- 3b. 进程榜卡：榜体末行与卡底信息行必须互不压字 ----
        // 2026-11-26 离屏渲染实测到的真实缺陷：末行（Teams.exe）与卡底那行
        // 「213 进程 · 1580 线程   今日 ↓4.40 GB ↑15.0 GB」叠在一起（monitor-page.png 可见）。
        // 根因：行数与行距写死，卡底信息行是后补回来的，没有给它留位置。
        // 修法：行数/行距从卡内可用高度倒推（见 ExpandedPanelSpec.ProcessRowsFor / ProcessRowGapFor）。
        double procAvail = botH - head - ExpandedPanelSpec.ProcessFooterReserve;
        int expectedRows = ExpandedPanelSpec.ProcessRowsFor(procAvail, ExpandedPanelSpec.ProcessMaxRows);
        double expectedGap = ExpandedPanelSpec.ProcessRowGapFor(procAvail, expectedRows);
        double lastRowBottom = head + (expectedRows - 1) * expectedGap + ExpandedPanelSpec.ProcessIconSize;
        double footerTextTop = botH - ExpandedPanelSpec.ProcessFooterBaselineInset - ExpandedPanelSpec.StatusFont * 0.85;
        report.Checks.Add(new ProbeCheck(
            "监控页：进程榜末行不压卡底信息行（几何，间隙 ≥ 2px）",
            lastRowBottom + 2 <= footerTextTop,
            $"卡内可用 {procAvail:F0} ⇒ 榜体 {expectedRows} 行 / 行距 {expectedGap}；" +
            $"末行内容底边 y={lastRowBottom:F0}，卡底信息行文字顶边 y={footerTextTop:F1}"));

        // 像素判定：卡内（卡头之下、卡底之上）的高亮内容必须分成「榜体行数 + 1」条带，
        // 带与带之间至少留 2px 空行 —— 压字就会连成一片（带数变少）或带间空行为 0。
        int px0 = (int)(x2 + padX);
        int px1 = (int)(x2 + cardW - padX) - 1;
        int py0 = (int)(y2 + head) + 1;
        int py1 = (int)(y2 + botH) - 2;
        var inkRows = pixels.ContentRows(px0, py0, px1, py1, 25);
        var bands = InkBands(inkRows);
        int minGap = bands.Count < 2 ? 0 : int.MaxValue;
        for (int i = 1; i < bands.Count; i++) minGap = Math.Min(minGap, bands[i].Item1 - bands[i - 1].Item2 - 1);
        report.Checks.Add(new ProbeCheck(
            "监控页：进程榜卡内各行与卡底信息行互不压字（像素带）",
            bands.Count == expectedRows + 1 && minGap >= 2,
            $"卡内 y∈[{py0},{py1}] 有 {bands.Count} 条高亮带（期望 {expectedRows + 1} = {expectedRows} 行榜体 + 1 条卡底行），" +
            $"带间最小空行 {minGap}（下限 2）"));

        // ---- 3c. 底部状态行（2026-11-26）：栅格下方有内容，且不与栅格重叠 ----
        // 2×2 栅格**下方**多了一条 9px 状态行（功耗 / 主板温度 / 运行时长 / 今日流量）。
        // 判据三条：
        //   ① 栅格底边之下「TopMargin + LineHeight」这条带里必须有内容（状态行文字）；
        //   ② 栅格底边与状态行顶边之间的留白必须干净（状态行不许压到栅格）；
        //   ③ 状态行不许越出页面底部。
        double gridBottom = y2 + botH;
        double slTop = gridBottom + MonitorStatusLineSpec.TopMargin;
        double slBottom = slTop + MonitorStatusLineSpec.LineHeight - 1;
        int gapTop = (int)gridBottom + 1, gapBottom = (int)slTop - 1;
        double gapInk = gapBottom >= gapTop ? pixels.MaxDelta(0, gapTop, w - 1, gapBottom) : 0;
        double slInk = slBottom >= (int)slTop
            ? pixels.MaxDelta((int)x1, (int)slTop, (int)(x2 + cardW), (int)slBottom)
            : 0;
        bool slInPage = slBottom < h;
        // 渲染出来的状态行文字（用同一套取值规则从合成读数算出来，方便报告与回归对照）
        var snap = SyntheticSnapshot();
        var th = SyntheticThermal();
        string statusText = string.Join("   ", MonitorStatusLineSpec.SlotsFor(macOS: false)
            .Select(slot => slot.Label + " " + (slot.Key switch
            {
                "cpu-power" => ExpandedPanelSpec.StatusPowerText(th.CpuPower),
                "board-temp" => ExpandedPanelSpec.StatusTempText(th.MotherboardTemp),
                "runtime" => ExpandedPanelSpec.StatusUptimeText(snap.UptimeHours),
                "today-traffic" => ExpandedPanelSpec.StatusTrafficText(snap.TodayDownload, snap.TodayUpload),
                _ => "—",
            })));
        report.Checks.Add(new ProbeCheck(
            "监控页：栅格下方有底部状态行，且状态行不与栅格重叠",
            slInk >= 20 && gapInk < PaintedDelta && slInPage,
            $"栅格底边 y={gridBottom:F0}；状态行带 y∈[{slTop:F0},{slBottom:F0}]" +
            $"（TopMargin {MonitorStatusLineSpec.TopMargin} + LineHeight {MonitorStatusLineSpec.LineHeight}）；" +
            $"带内最大通道差 {slInk}（下限 20）；栅格与状态行之间的留白 y∈[{gapTop},{gapBottom}] 最大通道差 {gapInk}" +
            $"（阈值 < {PaintedDelta}）；页面高 {h}，状态行底边 {slBottom:F0}（须 < {h}）；" +
            $"合成读数下的状态行文字：「{statusText}」"));

        // ---- 4. 像素探针：卡片边界真的落在规格位置上 ----
        // 左边距必须干净（面板左侧留白 18）。
        // 注意：卡片的 1px 描边**骑在**矩形边上（覆盖 e-1 与 e 两个像素），所以"干净区"从 e+1 算起。
        double leftMargin = pixels.MaxDelta(0, 0, (int)pad - 2, h - 1);
        report.Checks.Add(new ProbeCheck(
            "监控页：左内缩 18 像素内无任何绘制",
            leftMargin < PaintedDelta,
            $"x∈[0,{pad - 1}) 区域与底色的最大通道差 = {leftMargin}（阈值 < {PaintedDelta}）"));

        // 右内缩：右列卡片右缘 542 ⇒ x∈[543,560) 干净
        double rightMargin = pixels.MaxDelta((int)(x2 + cardW) + 1, 0, w - 1, h - 1);
        report.Checks.Add(new ProbeCheck(
            "监控页：右内缩 18 像素内无任何绘制",
            rightMargin < PaintedDelta,
            $"x∈[{(int)(x2 + cardW) + 1},{w}) 区域与底色的最大通道差 = {rightMargin}（阈值 < {PaintedDelta}）"));

        // 两列之间的竖向间隙：左卡右缘 e1=274、右卡左缘 e2=286，
        // 描边各渗到 e-1（273 / 285），真正干净的中间区是 [e1+1, e2-2]。
        // ⚠️ 纵向只扫**栅格范围内**（0 → 栅格底边）：栅格**下方**是底部状态行（整行文字，
        //    必然横穿两列之间的那条缝），那不是"列间隙"—— 状态行自己有独立的不变量盯着。
        double colGap = pixels.MaxDelta((int)(x1 + cardW) + 1, 0, (int)x2 - 2, (int)(y2 + botH));
        report.Checks.Add(new ProbeCheck(
            "监控页：列间距 12 且间隙内无任何绘制",
            colGap < PaintedDelta,
            $"x∈[{cardW + pad + 1},{pad + cardW + gap - 1}]、y∈[0,{(int)(y2 + botH)}] 最大通道差 = {colGap}（阈值 < {PaintedDelta}）"));

        // 两排之间的横向间隙：上排底缘 e1=181、下排顶缘 e2=193，干净区是 [e1+1, e2-2]。
        double rowGap = pixels.MaxDelta(0, (int)topH + 1, w - 1, (int)y2 - 2);
        report.Checks.Add(new ProbeCheck(
            "监控页：排间距 12 且间隙内无任何绘制",
            rowGap < PaintedDelta,
            $"y∈[{topH + 1},{y2 - 1}] 最大通道差 = {rowGap}（阈值 < {PaintedDelta}）"));

        // 四条边真的画在规格位置（描边像素，允许 ±1 的抗锯齿位移）
        int midY = (int)(y1 + topH / 2);
        int midX = (int)(x1 + cardW / 2);
        report.Checks.Add(new ProbeCheck(
            "监控页：左列卡片左右缘落在 x=18 / x=274",
            pixels.AnyBorderNear((int)x1, midY, 1) && pixels.AnyBorderNear((int)(x1 + cardW), midY, 1),
            $"x={x1} 与 x={x1 + cardW} 处的描边强度 = {pixels.MaxDelta((int)x1 - 1, midY, (int)x1 + 1, midY)}"));

        report.Checks.Add(new ProbeCheck(
            "监控页：右列卡片左右缘落在 x=286 / x=542",
            pixels.AnyBorderNear((int)x2, midY, 1) && pixels.AnyBorderNear((int)(x2 + cardW), midY, 1),
            $"x={x2} 与 x={x2 + cardW} 处的描边强度 = {pixels.MaxDelta((int)x2 - 1, midY, (int)x2 + 1, midY)}"));

        report.Checks.Add(new ProbeCheck(
            "监控页：上排卡底边落在 y=181、下排卡顶边落在 y=193",
            pixels.AnyBorderNear(midX, (int)topH, 1) && pixels.AnyBorderNear(midX, (int)y2, 1),
            $"y={topH} 与 y={y2} 处的描边强度 = {pixels.MaxDelta(midX - 1, (int)topH - 1, midX + 1, (int)topH + 1)}"));

        // ---- 5. 文本不越出卡片：卡内高亮像素不得越过"卡右 - 内缩 15" ----
        bool textInside = true;
        var detail = new List<string>();
        foreach (var card in cards)
        {
            int limit = (int)(card.X + card.W - padX) + 1;   // 允许 1 像素抗锯齿
            int rightmost = pixels.RightmostInk((int)card.X, (int)card.Y, (int)(card.X + card.W - 3), (int)(card.Y + card.H - 1));
            textInside &= rightmost <= limit;
            detail.Add($"{card.Name} 最右高亮像素 x={rightmost}（上限 {limit}）");
        }
        report.Checks.Add(new ProbeCheck(
            "监控页：文本不越出卡片（右对齐读数 x <= 卡右 - 内缩 15）",
            textInside,
            string.Join("；", detail)));

        // ---- 6. 卡片内部确实画了东西（不是空壳） ----
        double cpuCard = pixels.PaintedRatio((int)(x1 + 6), (int)(y1 + 6), (int)(x1 + cardW - 6), (int)(y1 + topH - 6));
        report.Checks.Add(new ProbeCheck(
            "监控页：占用卡内部有实际绘制",
            cpuCard > 0.5,
            $"占用卡内部非背景像素占比 = {cpuCard:P1}"));
    }

    // ================= 监控页不变量（真机尺寸：紧凑档） =================

    /// <summary>
    /// 真机尺寸（504×308 ⇒ 紧凑档）下的不变量：**状态行也必须放得下**。
    ///
    /// 为什么单独盯一档：完整档（420 高）放得下，不代表真机放得下 —— 真机监控页只有 308 高，
    /// 扣掉状态行（TopMargin + LineHeight = 20）只剩 288 给栅格。这里按产品源码里的常量
    /// 把"紧凑档应该长什么样"算一遍，再在像素上验证：
    ///   ① 状态行带里确实有文字；
    ///   ② 状态行顶边不低于「栅格底边 + TopMargin」（不压栅格）；
    ///   ③ 状态行底边在页面内（不越出面板）。
    /// </summary>
    private static void CheckMonitorPageCompact(ProbeReport report, BitmapSource bitmap)
    {
        var pixels = new BitmapPixels(bitmap, Palette.Dark.Surface);
        int w = bitmap.PixelWidth, h = bitmap.PixelHeight;

        // 期望几何（全部取自 MonitorPage 的私有常量 ⇒ 断言的是产品源码，不是抄一份副本）
        double head = ConstOf(typeof(MonitorPage), "CardHeadH");
        double gap = ConstOf(typeof(MonitorPage), "GridGap");
        double rowGap = ConstOf(typeof(MonitorPage), "RowGap");
        double tightRowH = ConstOf(typeof(MonitorPage), "TightRowH");
        double tightBotH = ConstOf(typeof(MonitorPage), "TightBotH");
        double minBotH = ConstOf(typeof(MonitorPage), "MinBotCardH");
        double statusH = MonitorStatusLineSpec.TopMargin + MonitorStatusLineSpec.LineHeight;

        double availH = h - statusH;
        double topCardH = ConstOf(typeof(MonitorPage), "TopCardH");
        double botCardH = ConstOf(typeof(MonitorPage), "BotCardH");
        double tightTopH = ConstOf(typeof(MonitorPage), "TightTopH");
        // 与 MonitorPage 的分档逻辑**逐条镜像**（断言的是产品源码，不是抄一份副本）
        double topH, botH;
        string tier;
        if (availH >= topCardH + gap + botCardH)
        {
            tier = "完整档"; topH = topCardH; botH = botCardH;
        }
        else if (availH >= tightTopH + gap + tightBotH)
        {
            tier = "让位档"; topH = tightTopH; botH = tightBotH;
        }
        else
        {
            tier = "紧凑档"; topH = head + 3 * tightRowH + 2 * rowGap; botH = tightBotH;
            double botBudget = availH - topH - gap;
            if (botBudget < botH) botH = Math.Max(minBotH, botBudget);
        }
        double gridBottom = topH + gap + botH;
        double slTop = gridBottom + MonitorStatusLineSpec.TopMargin;
        double slBottom = slTop + MonitorStatusLineSpec.LineHeight - 1;

        int gapTop = (int)gridBottom + 1, gapBottom = (int)slTop - 1;
        double gapInk = gapBottom >= gapTop ? pixels.MaxDelta(0, gapTop, w - 1, gapBottom) : 0;
        double slInk = slBottom >= (int)slTop
            ? pixels.MaxDelta(0, (int)slTop, w - 1, (int)slBottom)
            : 0;

        report.Checks.Add(new ProbeCheck(
            $"监控页（真机 {w}×{h}）：{tier}也放得下底部状态行",
            slInk >= 20                             // 状态行带里有文字
                && gapInk < PaintedDelta              // 栅格与状态行之间不重叠
                && slBottom < h,                      // 不越出面板
            $"页面 {w}×{h}：扣掉状态行 {statusH:F0} ⇒ 可用 {availH:F0} ⇒ {tier}；" +
            $"栅格 {topH:F0} + {gap:F0} + 下排卡 {botH:F0} ⇒ 底边 y={gridBottom:F0}；" +
            $"状态行带 y∈[{slTop:F0},{slBottom:F0}]，带内最大通道差 {slInk}（下限 20）；" +
            $"留白 y∈[{gapTop},{gapBottom}] 最大通道差 {gapInk}（阈值 < {PaintedDelta}）"));
    }

    // ================= 监控页不变量（分组：两端统一，2026-11-26 task16） =================

    /// <summary>
    /// **分组门禁**：监控页的卡片分组必须与两端共享规格 <see cref="MonitorPageLayoutSpec"/> 一致。
    ///
    /// 守门点是"**每个读数行真的画在它该在的那张卡里**"：
    ///   · 规格算出每一行该落在哪一带（行带，由产品代码 <c>MonitorPage.PlannedCards</c> 给出），
    ///     那一带里**必须有绘制**（行标签 / 图标在那儿）；
    ///   · 平台不支持的行（Windows 的 SSD 温度）那一带里**必须没有**绘制 ——
    ///     整行不出现，不是显示 0、也不是显示"未知"。
    /// ⇒ 温度还留在占用卡 ⇒ 温度卡的行带是空的 ⇒ 红。
    /// </summary>
    private static void CheckMonitorPageGrouping(ProbeReport report, BitmapSource bitmap, ProbeOptions options)
    {
        var palette = options.Palette ?? Palette.Dark;
        var pixels = new BitmapPixels(bitmap, palette.Surface);

        // 卡片与行带几何**由产品代码算**（共享规格 + 产品排版），探针不另抄一份。
        var cards = MonitorPage.PlannedCards(options.Width, options.Height);
        var spec = MonitorPageLayoutSpec.CardsFor(macOS: false);

        // ---- 1. 卡片清单与规格一致（数量 / 顺序 / 标题） ----
        report.Checks.Add(new ProbeCheck(
            "监控页：卡片清单与共享规格一致（四张卡，顺序与标题取自 MonitorPageLayoutSpec）",
            cards.Count == spec.Count && spec.Count == 4
                && cards.Select(c => c.Key).SequenceEqual(spec.Select(c => c.Key))
                && cards.Select(c => c.Title).SequenceEqual(spec.Select(c => c.Title)),
            $"规格：{string.Join(" / ", spec.Select(c => $"{c.Key}·{c.Title}"))}；" +
            $"页面：{string.Join(" / ", cards.Select(c => $"{c.Key}·{c.Title}"))}"));

        // ---- 2. 四张卡的矩形：与规格一致 + 互不重叠 + 都在内容区内 ----
        bool disjoint = true;
        for (int i = 0; i < cards.Count; i++)
            for (int j = i + 1; j < cards.Count; j++)
                disjoint &= !Overlaps(cards[i].Rect, cards[j].Rect);
        bool inside = cards.All(c => c.Rect.Left >= 0 && c.Rect.Top >= 0
            && c.Rect.Right <= options.Width && c.Rect.Bottom <= options.Height);
        report.Checks.Add(new ProbeCheck(
            "监控页：四张卡的矩形与规格一致、互不重叠、都在内容区内",
            disjoint && inside,
            string.Join("；", cards.Select(c => $"{c.Title} @ {c.Rect}")) +
            $"（内容区 {options.Width}×{options.Height}；重叠：{(disjoint ? "无" : "有")}）"));

        // ---- 3. 每张卡画出来的行 == 规格里该平台的行（平台不支持的行不会被画） ----
        // 平台专属行（如 Windows 的主板温度）补的是**同一格**：mac 那一格是存储温度，
        // Windows 那一格是主板温度 —— 所以"不支持的行"在 Windows 上**没有位置**
        // （给它一个位置就是画重 / 越界）。这里断言的是"画出来的行 == 规格里该平台的行"。
        var specByKey = spec.ToDictionary(c => c.Key);
        bool rowsMatch = true;
        var rowDetail = new List<string>();
        foreach (var card in cards)
        {
            var expected = MonitorPageLayoutSpec.RowsFor(specByKey[card.Key], macOS: false)
                .Where(r => r.Label.Length > 0).Select(r => r.Key).ToList();
            var actual = card.Rows.Where(r => r.Drawn).Select(r => r.RowKey).ToList();
            rowsMatch &= expected.SequenceEqual(actual);
            rowDetail.Add($"{card.Title}：{string.Join(" / ", actual)}");
        }
        report.Checks.Add(new ProbeCheck(
            "监控页：每张卡画出来的行 == 规格里该平台的行（平台不支持的行不会被画）",
            rowsMatch,
            string.Join("；", rowDetail) +
            "（Windows 不画 SSD 温度 —— 那一格在 Windows 上归主板温度；平台不支持 ⇒ 整行不出现）"));

        // ---- 4. 每个读数行：规格说它该在哪一带，那一带里必须有绘制 ----
        var missing = new List<string>();
        foreach (var card in cards)
        {
            foreach (var band in card.Rows.Where(r => r.Drawn))
            {
                if (!BandHasInk(pixels, band.Rect)) missing.Add($"{card.Title}/{band.Label}({band.RowKey})");
            }
        }
        report.Checks.Add(new ProbeCheck(
            "监控页：规格里的每个读数行都画在它该在的那张卡里（分组门禁）",
            missing.Count == 0,
            string.Join("；", cards.SelectMany(c => c.Rows).Where(b => b.Drawn).Select(b =>
                $"{b.CardKey}/{b.Label}{(BandHasInk(pixels, b.Rect) ? " 有绘制" : " 缺绘制")}"))));
    }

    /// <summary>行带里有没有"有内容"的像素（按该区域的常见色当底色判 —— 卡底色不算内容）。</summary>
    private static bool BandHasInk(BitmapPixels pixels, Rect band)
    {
        int x0 = (int)Math.Round(band.Left), y0 = (int)Math.Round(band.Top);
        int x1 = (int)Math.Round(band.Right) - 1, y1 = (int)Math.Round(band.Bottom) - 1;
        if (x1 < x0 || y1 < y0) return false;
        foreach (bool row in pixels.ContentRows(x0, y0, x1, y1, 25))
            if (row) return true;
        return false;
    }

    // ================= 设备页不变量 =================

    private static void CheckDevicesPage(ProbeReport report, BitmapSource bitmap, ProbeOptions options)
    {
        var palette = options.Palette ?? Palette.Dark;
        var pixels = new BitmapPixels(bitmap, palette.Surface);
        int w = options.Width, h = options.Height;

        double painted = pixels.PaintedRatio();
        report.Checks.Add(new ProbeCheck(
            "设备页：渲染结果非全黑（非背景像素占比 > 2%）",
            painted > MinPaintedRatio,
            $"非背景像素 {pixels.PaintedCount()}/{w * h} = {painted:P2}（下限 {MinPaintedRatio:P0}）"));

        double ink = pixels.InkRatio();
        report.Checks.Add(new ProbeCheck(
            "设备页：有高亮内容（电量环/读数像素占比 > 0.05%）",
            ink > MinInkRatio,
            $"高亮像素 {pixels.InkCount()}/{w * h} = {ink:P3}（下限 {MinInkRatio:P3}）"));

        double cardW = ExpandedPanelSpec.DeviceCardWidth;
        double cardH = ExpandedPanelSpec.DeviceCardHeight;
        double gap = ExpandedPanelSpec.DeviceCardGap;
        report.Checks.Add(new ProbeCheck(
            "设备页：卡片规格常量（132×150 / 间距 12）",
            cardW == 132 && cardH == 150 && gap == 12,
            $"DeviceCardWidth={cardW} DeviceCardHeight={cardH} DeviceCardGap={gap}"));

        // 第一张卡（合成设备 5 台 ⇒ 一行三张）
        const double inset = 4;
        // 描边骑在卡边上（渗到 e-1），干净区是 [e1+1, e2-2]
        double colGap = pixels.MaxDelta((int)(inset + cardW) + 1, 0, (int)(inset + cardW + gap) - 2, h - 1);
        report.Checks.Add(new ProbeCheck(
            "设备页：卡片列间距 12 且间隙内无任何绘制",
            colGap < PaintedDelta,
            $"x∈[{(int)(inset + cardW) + 1},{(int)(inset + cardW + gap) - 1}] 最大通道差 = {colGap}（阈值 < {PaintedDelta}）"));

        double cardBody = pixels.PaintedRatio((int)(inset + 16), 0, (int)(inset + cardW - 16), (int)cardH - 8);
        report.Checks.Add(new ProbeCheck(
            "设备页：卡片内部有实际绘制（电量环 + 读数）",
            cardBody > 0.2,
            $"首张卡内部非背景像素占比 = {cardBody:P1}"));
    }


    // ================= 展开面板整体（可选：需要 Island.App 程序集） =================

    /// <summary>
    /// 尝试渲染展开面板整体（含顶栏页签）。
    ///
    /// 为什么要走反射：ExpandedPanel.xaml 的资源字典写死了 <c>/Island.App;component/Controls/PanelStyles.xaml</c>，
    /// 那是**按程序集名**寻址的编译期资源 —— 只有真的加载 Island.App 程序集才解析得了。
    /// 而直接 ProjectReference Island.App 会让 MonitorPage / DevicesPage 两份类型重名（CS0436 警告），
    /// 门禁要求 0 警告，所以这里改成**运行时反射加载**，编译期零耦合。
    ///
    /// 找不到 Island.App 程序集、或渲染失败 ⇒ 返回 null，调用方如实报告"没渲染出来"，
    /// 绝不拿一张全黑图充数。
    /// </summary>
    public static BitmapSource? TryRenderExpandedPanel(string islandAppDll, int width, int height, Palette palette)
        => TryRenderExpandedPanel(islandAppDll, width, height, palette, out _);

    /// <summary>同 <see cref="TryRenderExpandedPanel(string,int,int,Palette)"/>，但把失败原因带出来（如实报告，不猜）。</summary>
    public static BitmapSource? TryRenderExpandedPanel(string islandAppDll, int width, int height, Palette palette, out string? error)
    {
        error = null;
        BitmapSource? result = null;
        Exception? failure = null;

        // 展开面板的构造函数会建 WPF 控件（UserControl + XAML），**必须 STA 线程**，
        // 调用方（控制台主线程）是 MTA ⇒ 这里自己开一条 STA 工作线程。
        var worker = new Thread(() =>
        {
            try { result = RenderExpandedPanelOnSta(islandAppDll, width, height, palette); }
            catch (Exception ex) { failure = ex; }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.IsBackground = true;
        worker.Start();
        worker.Join();

        if (failure != null)
        {
            error = failure.GetType().Name + ": " + failure.Message
                + (failure.InnerException is { } inner ? " → " + inner.GetType().Name + ": " + inner.Message : "")
                + (failure.InnerException?.InnerException is { } inner2 ? " → " + inner2.GetType().Name + ": " + inner2.Message : "");
        }
        return result;
    }

    private static BitmapSource? RenderExpandedPanelOnSta(string islandAppDll, int width, int height, Palette palette)
    {
        try
        {
            if (!File.Exists(islandAppDll)) return null;

            // 依赖解析：Island.App 的第三方依赖（NAudio / System.Management 等）都在它自己的输出目录里
            string dir = Path.GetDirectoryName(islandAppDll)!;
            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                string candidate = Path.Combine(dir, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };

            Assembly app = AssemblyLoadContext.Default.LoadFromAssemblyPath(islandAppDll);
            Type? panelType = app.GetType("Island.App.Controls.ExpandedPanel");
            if (panelType is null) return null;

            object? instance = Activator.CreateInstance(panelType);
            if (instance is not FrameworkElement panel) return null;

            // 主题：展开面板的控件从**它自己的**资源字典取色，宿主是这么写的
            // （IslandWindow.UpdatePanelResources → ExpandedPanel.ApplyTheme）。
            var applyTheme = panelType.GetMethod("ApplyTheme", BindingFlags.Public | BindingFlags.Static);
            if (applyTheme is not null)
            {
                var appPaletteType = app.GetType("Island.App.Themes.Palette")!;
                // 用被加载程序集里的 Palette 类型取同名静态属性（Dark / OneDark …）
                object? appPalette = appPaletteType.GetProperty(paletteNameFor(palette),
                    BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (appPalette is not null)
                    applyTheme.Invoke(null, new[] { panel.Resources, appPalette });
            }

            // 喂读数：展开面板里的监控页读的是**被加载程序集自己的** MonitorSnapshot 类型
            //（Island.App 把 Island.Core 的源码合并进了自己，类型跟探针这边不是同一个），
            // 所以要用反射在目标程序集里造一份同样的合成读数。
            try
            {
                InjectSyntheticReadings(app, panel);
            }
            catch
            {
                // 喂不进去读数不算失败：面板照样能渲染（只是读数位显示 "—"），
                // 但那样出来的图看不出内容，所以这里让上层知道。
                throw new InvalidOperationException("展开面板读数注入失败", new InvalidOperationException("见内层异常"));
            }

            panel.Width = width;
            panel.Height = height;

            var host = new Grid { Background = new SolidColorBrush(palette.Surface), Width = width, Height = height };
            host.Children.Add(panel);
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            // 渲染不出来就是渲染不出来（原因由调用方报告），不吞异常后假装成功
            throw new InvalidOperationException("展开面板离屏渲染失败", ex);
        }
    }


    /// <summary>
    /// 用反射给展开面板里的监控页喂一份**合成读数**。
    ///
    /// 为什么必须用反射：展开面板（连同它内嵌的 MonitorPage）在 Island.App 程序集里，
    /// 它的 Snapshot 属性类型是**那个程序集自己的** Island.Core.Monitoring.MonitorSnapshot
    /// ——Island.App 把 Island.Core 的源码合并进了自己，所以同名类型在两边不是同一个 CLR 类型，
    /// 直接赋值会 TypeLoadException。
    /// </summary>
    private static void InjectSyntheticReadings(Assembly app, FrameworkElement panel)
    {
        var monitorProp = panel.GetType().GetProperty("Monitor");
        object? monitor = monitorProp?.GetValue(panel);
        if (monitor is null) return;

        // 在目标程序集里造 MonitorSnapshot
        var snapshotType = app.GetType("Island.Core.Monitoring.MonitorSnapshot");
        if (snapshotType is null) return;
        object snapshot = Activator.CreateInstance(snapshotType)!;

        var snapshotSource = SyntheticSnapshot();
        foreach (var name in new[]
        {
            "CpuUsage", "CpuFrequencyGHz", "MemoryUsagePercent", "MemoryUsedGB", "MemoryTotalGB",
            "GpuUsage", "DiskUsagePercent", "DriveUsedGB", "DriveTotalGB", "DriveName",
            "DownloadSpeed", "UploadSpeed", "SessionDownload", "SessionUpload",
            "TodayDownload", "TodayUpload", "LinkType", "ProcessCount", "ThreadCount", "UptimeHours",
        })
        {
            var prop = snapshotType.GetProperty(name);
            var value = snapshotSource.GetType().GetProperty(name)?.GetValue(snapshotSource);
            if (prop is not null && value is not null && prop.CanWrite)
                prop.SetValue(snapshot, value);
        }

        // 电池百分比（可空）
        var batteryProp = snapshotType.GetProperty("BatteryPercent");
        var batteryValue = snapshotSource.GetType().GetProperty("BatteryPercent")?.GetValue(snapshotSource);
        if (batteryProp is not null && batteryValue is not null && batteryProp.CanWrite)
            batteryProp.SetValue(snapshot, batteryValue);

        // 进程榜（数组类型也要在目标程序集里造）
        var topType = app.GetType("Island.Core.Monitoring.TopProcess");
        var topSource = snapshotSource.TopProcesses;
        if (topType is not null && topSource is not null && topSource.Length > 0)
        {
            var array = Array.CreateInstance(topType, topSource.Length);
            for (int i = 0; i < topSource.Length; i++)
            {
                var item = Activator.CreateInstance(topType)!;
                foreach (var name in new[] { "Name", "CpuPercent", "MemoryMB", "ExePath" })
                {
                    var prop = topType.GetProperty(name);
                    var value = topSource[i].GetType().GetProperty(name)?.GetValue(topSource[i]);
                    if (prop is not null && value is not null && prop.CanWrite)
                        prop.SetValue(item, value);
                }
                array.SetValue(item, i);
            }
            var topProp = snapshotType.GetProperty("TopProcesses");
            if (topProp is not null && topProp.CanWrite) topProp.SetValue(snapshot, array);
        }

        var setSnapshot = monitor.GetType().GetProperty("Snapshot");
        if (setSnapshot is not null && setSnapshot.CanWrite) setSnapshot.SetValue(monitor, snapshot);

        // 温度快照
        var thermalType = app.GetType("Island.App.Services.ThermalSnapshot");
        var monitorThermal = monitor.GetType().GetProperty("Thermal");
        if (thermalType is not null && monitorThermal is not null && monitorThermal.CanWrite)
        {
            object thermal = Activator.CreateInstance(thermalType)!;
            var thermalSource = SyntheticThermal();
            foreach (var name in new[] { "CpuTemp", "GpuTemp", "SsdTemp", "MotherboardTemp", "GpuMemoryUsedGB", "GpuMemoryTotalGB" })
            {
                var prop = thermalType.GetProperty(name);
                var value = thermalSource.GetType().GetProperty(name)?.GetValue(thermalSource);
                if (prop is not null && value is not null && prop.CanWrite)
                    prop.SetValue(thermal, value);
            }
            monitorThermal.SetValue(monitor, thermal);
        }

        // 历史序列（走势图）：目标程序集里造 double[]
        var historyProp = monitor.GetType().GetProperty("CpuHistory");
        if (historyProp is not null && historyProp.CanWrite)
        {
            var source = SyntheticHistory(36);
            var array = Array.CreateInstance(typeof(double), source.Length);
            for (int i = 0; i < source.Length; i++) array.SetValue(source[i], i);
            historyProp.SetValue(monitor, array);
        }
    }

    /// <summary>把探针用的 Palette 名字映射成 Island.App 里同名的静态属性（Dark / OneDark / …）。</summary>
    private static string paletteNameFor(Palette palette)
    {
        foreach (var name in new[] { "Dark", "Light", "OneDark", "GitHubDark", "Dracula", "NightOwl", "Island", "Cyberpunk", "Pixel" })
        {
            var candidate = Palette.ByName(name);
            if (candidate is not null && candidate.Text == palette.Text && candidate.Accent == palette.Accent)
                return name;
        }
        return "Dark";
    }

    // ================= 工具 =================

    /// <summary>一张位图的"非背景像素占比"（相对给定底色）—— 工具侧用来判断渲染是不是全黑。</summary>
    public static double PaintedRatioOf(BitmapSource bitmap, Color background)
        => new BitmapPixels(bitmap, background).PaintedRatio();

    /// <summary>读取一个 const 字段的真实值（断言的是产品源码里的常量，不是抄一份副本）。</summary>
    private static double ConstOf(Type type, string name)
    {
        FieldInfo? field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        if (field == null) throw new InvalidOperationException($"{type.Name} 里找不到常量 {name}");
        object? value = field.GetRawConstantValue();
        if (value is not double d) throw new InvalidOperationException($"{type.Name}.{name} 不是 double");
        return d;
    }

    /// <summary>把逐行布尔数组切成连续的高亮带（[起始行, 结束行]）。</summary>
    private static List<(int Start, int End)> InkBands(bool[] rows)
    {
        var bands = new List<(int Start, int End)>();
        int start = -1;
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i] && start < 0) start = i;
            else if (!rows[i] && start >= 0) { bands.Add((start, i - 1)); start = -1; }
        }
        if (start >= 0) bands.Add((start, rows.Length - 1));
        return bands;
    }

    private static bool Overlaps(
        (string Name, double X, double Y, double W, double H) a,
        (string Name, double X, double Y, double W, double H) b)
        => a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    /// <summary>两个矩形是否相交（卡片带版本）。</summary>
    private static bool Overlaps(Rect a, Rect b)
        => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>
    /// 位图像素读取 + 判定用的统计。所有坐标都是像素坐标（DpiScale = 1 ⇒ 与 DIP 一一对应）。
    /// </summary>
    private sealed class BitmapPixels
    {
        private readonly byte[] _data;
        private readonly int _width;
        private readonly int _height;
        private readonly byte _bgR, _bgG, _bgB;

        public BitmapPixels(BitmapSource bitmap, Color background)
        {
            _width = bitmap.PixelWidth;
            _height = bitmap.PixelHeight;
            _data = new byte[_width * _height * 4];
            bitmap.CopyPixels(_data, _width * 4, 0);
            _bgR = background.R;
            _bgG = background.G;
            _bgB = background.B;
        }

        private (byte B, byte G, byte R) At(int x, int y)
        {
            int i = (y * _width + x) * 4;
            return (_data[i], _data[i + 1], _data[i + 2]);
        }

        /// <summary>与底色的通道最大差。</summary>
        public int Delta(int x, int y)
        {
            var (b, g, r) = At(x, y);
            return Math.Max(Math.Abs(r - _bgR), Math.Max(Math.Abs(g - _bgG), Math.Abs(b - _bgB)));
        }

        /// <summary>该像素是不是"有绘制"（与底色有可见差别）。</summary>
        public bool IsPainted(int x, int y) => Delta(x, y) >= PaintedDelta;

        /// <summary>
        /// 指定矩形里的"有内容"逐行判定：先取该区域**最常见的颜色**当底色（卡片自身的底），
        /// 再把与它差得够远的像素算作有内容（文字/图标/分隔线）。
        /// 为什么不用全局底色：卡片底色与页面底色不同，用全局底色会把整张卡判成"有内容"；
        /// 为什么不用高亮阈值：卡底那行是三级色（偏暗），按高亮阈值会整行被判漏 —— 而它正是要判的那行。
        /// </summary>
        public bool[] ContentRows(int x0, int y0, int x1, int y1, int minDelta)
        {
            int n = Math.Max(0, y1 - y0 + 1);
            var rows = new bool[n];
            int rx0 = Math.Max(0, x0), rx1 = Math.Min(x1, _width - 1);
            int ry0 = Math.Max(0, y0), ry1 = Math.Min(y1, _height - 1);
            if (rx1 < rx0 || ry1 < ry0) return rows;

            // 最常见颜色 = 卡片底色（文字只占极小比例）
            var hist = new Dictionary<int, int>();
            for (int y = ry0; y <= ry1; y++)
                for (int x = rx0; x <= rx1; x++)
                {
                    var (b, g, r) = At(x, y);
                    int key = (r << 16) | (g << 8) | b;
                    hist.TryGetValue(key, out int c);
                    hist[key] = c + 1;
                }
            int modal = 0, modalCount = -1;
            foreach (var kv in hist)
                if (kv.Value > modalCount) { modalCount = kv.Value; modal = kv.Key; }
            int mr = (modal >> 16) & 0xFF, mg = (modal >> 8) & 0xFF, mb = modal & 0xFF;

            for (int y = ry0; y <= ry1; y++)
                for (int x = rx0; x <= rx1; x++)
                {
                    var (b, g, r) = At(x, y);
                    int d = Math.Max(Math.Abs(r - mr), Math.Max(Math.Abs(g - mg), Math.Abs(b - mb)));
                    if (d >= minDelta) { rows[y - y0] = true; break; }
                }
            return rows;
        }

        /// <summary>指定矩形里每一行有没有"高亮内容"（文字/图标/强调色）。</summary>
        public bool[] InkRows(int x0, int y0, int x1, int y1)
        {
            int n = Math.Max(0, y1 - y0 + 1);
            var rows = new bool[n];
            for (int y = Math.Max(0, y0); y <= Math.Min(y1, _height - 1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(x1, _width - 1); x++)
                    if (IsInk(x, y)) { rows[y - y0] = true; break; }
            return rows;
        }

        /// <summary>该像素是不是"高亮内容"（文字/图标/强调色）。</summary>
        public bool IsInk(int x, int y)
        {
            var (b, g, r) = At(x, y);
            return Math.Max(r, Math.Max(g, b)) >= InkChannel;
        }

        public int PaintedCount()
        {
            int n = 0;
            for (int y = 0; y < _height; y++)
                for (int x = 0; x < _width; x++)
                    if (IsPainted(x, y)) n++;
            return n;
        }

        public int InkCount()
        {
            int n = 0;
            for (int y = 0; y < _height; y++)
                for (int x = 0; x < _width; x++)
                    if (IsInk(x, y)) n++;
            return n;
        }

        public double PaintedRatio() => (double)PaintedCount() / (_width * _height);

        public double InkRatio() => (double)InkCount() / (_width * _height);

        /// <summary>区域内的非背景像素占比。</summary>
        public double PaintedRatio(int x0, int y0, int x1, int y1)
        {
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            x1 = Math.Min(_width - 1, x1); y1 = Math.Min(_height - 1, y1);
            int n = 0, total = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    total++;
                    if (IsPainted(x, y)) n++;
                }
            return total == 0 ? 0 : (double)n / total;
        }

        /// <summary>区域内的最大通道差。</summary>
        public int MaxDelta(int x0, int y0, int x1, int y1)
        {
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            x1 = Math.Min(_width - 1, x1); y1 = Math.Min(_height - 1, y1);
            int max = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    max = Math.Max(max, Delta(x, y));
            return max;
        }

        /// <summary>附近是否存在描边像素（允许 ±1 像素的抗锯齿位移）。</summary>
        public bool AnyBorderNear(int x, int y, int tolerance)
        {
            for (int dy = -tolerance; dy <= tolerance; dy++)
                for (int dx = -tolerance; dx <= tolerance; dx++)
                {
                    int px = x + dx, py = y + dy;
                    if (px < 0 || py < 0 || px >= _width || py >= _height) continue;
                    if (Delta(px, py) >= BorderDelta) return true;
                }
            return false;
        }

        /// <summary>区域里最靠右的高亮像素 x（没有则返回 x0-1）。</summary>
        public int RightmostInk(int x0, int y0, int x1, int y1)
        {
            int rightmost = x0 - 1;
            for (int y = y0; y <= y1; y++)
                for (int x = x1; x >= x0; x--)
                    if (IsInk(x, y)) { rightmost = Math.Max(rightmost, x); break; }
            return rightmost;
        }
    }
}
