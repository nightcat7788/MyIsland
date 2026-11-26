using System.Text;
using Avalonia;
using Avalonia.Media;
using Island.App.Themes;
using Island.Avalonia;
using Island.Core.Monitoring;

namespace Island.Tools.AvaloniaProbe;

/// <summary>
/// macOS 端离屏渲染视觉回归探针（开发期专用，不随产品发布）。
///
/// 用法：
///   avalonia-probe [--out &lt;目录&gt;]
///
/// 做什么：把 mac 端的展开面板（监控页 / 设备页）与收起态 dash **离屏**渲染成 PNG，
/// 并对渲染结果判定一组可自动断言的不变量（非全黑 / 有内容 / 2×2 栅格几何 /
/// 底部状态行存在且不压栅格 / 卡片间距 / 图标盒确有绘制 …）。
/// 全部通过 ⇒ 退出码 0；任一失败 ⇒ 退出码非 0。
///
/// 为什么需要它：mac 端此前**没有任何视觉验证** —— 五轮跨端一致性对齐都以 mac 为基准，
/// 而基准端恰恰是唯一看不见的那一端，只能确认"编译通过"，看不到画出来是什么样。
/// 离屏渲染（不建窗口、元素排版后直接 Render）不经过屏幕，所以锁屏会话下照样能拿到真像素。
///
/// 判定逻辑与 tests\Island.Tests 是**同一份源码**（AvaloniaVisualCore.cs），
/// 免得"工具里过了、门禁里是另一套标准"。
///
/// 数据一律是**合成**的（不读真硬件）：真硬件每台机器、每个时刻都不一样，PNG 就没法当基线比。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string? outDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("--out 需要目录参数"); return 2; }
                    outDir = args[++i];
                    break;
                case "--help":
                case "-h":
                    Usage();
                    return 0;
                default:
                    Console.Error.WriteLine("未知参数：" + args[i]);
                    Usage();
                    return 2;
            }
        }

        outDir ??= DefaultOutputDirectory();

        Console.WriteLine("macOS 端离屏渲染视觉探针");
        Console.WriteLine($"  输出：{outDir}");
        Console.WriteLine();

        ProbeReport report;
        try
        {
            report = Run(new ProbeOptions { OutputDirectory = outDir });
        }
        catch (Exception ex)
        {
            // 渲染失败就是失败：不许用默认值凑一张"看起来正常"的图。
            Console.Error.WriteLine("FAIL: 离屏渲染抛异常 —— " + ex);
            return 1;
        }

        int failed = 0;
        foreach (var check in report.Checks)
        {
            Console.WriteLine((check.Passed ? "PASS: " : "FAIL: ") + check.Name);
            Console.WriteLine("      " + check.Detail);
            if (!check.Passed) failed++;
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"全部通过：{report.Checks.Count} 条"
            : $"失败 {failed} 条（共 {report.Checks.Count} 条）");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 探针参数。
    /// </summary>
    private sealed class ProbeOptions
    {
        public string OutputDirectory { get; init; } = "";
    }

    /// <summary>
    /// 跑一遍探针：渲染三个画面 + 判定全部不变量 + 落盘 PNG。
    /// </summary>
    private static ProbeReport Run(ProbeOptions options)
    {
        AvaloniaProbeRenderer.EnsurePlatform();

        var report = new ProbeReport();
        var palette = Palette.Dark;
        var probePalette = AvaloniaProbeRenderer.PaletteOf(palette);
        double panelWidth = MonitorLayoutSpec.ExpandedPanelWidth;
        double panelHeight = MonitorLayoutSpec.MonitorPanelHeight;

        Directory.CreateDirectory(options.OutputDirectory);

        // ---------- ① 展开面板 · 监控页 ----------
        var monitorCanvas = new IslandCanvas
        {
            Palette = palette,
            IsExpanded = true,
            Page = "monitor",
            BodyRect = new Rect(0, 0, panelWidth, panelHeight),
            Snapshot = SyntheticSnapshot(),
        };
        var monitor = AvaloniaProbeRenderer.Render(monitorCanvas, panelWidth, panelHeight, palette.Surface);
        SavePng(monitor.PngBytes, Path.Combine(options.OutputDirectory, "monitor-page.png"));
        AvaloniaVisualCore.CheckMonitorGeometry(report, panelWidth, panelHeight);
        AvaloniaVisualCore.CheckMonitorPage(report, monitor.Image, probePalette, panelWidth, panelHeight);

        // ---------- ② 展开面板 · 设备页 ----------
        int deviceCount = 5;
        double devicesHeight = 42 + DevicesHeight(deviceCount) + 24;
        var devicesCanvas = new IslandCanvas
        {
            Palette = palette,
            IsExpanded = true,
            Page = "devices",
            BodyRect = new Rect(0, 0, panelWidth, devicesHeight),
            Devices = SyntheticDevices(deviceCount),
        };
        var devices = AvaloniaProbeRenderer.Render(devicesCanvas, panelWidth, devicesHeight, palette.Surface);
        SavePng(devices.PngBytes, Path.Combine(options.OutputDirectory, "devices-page.png"));
        AvaloniaVisualCore.CheckDevicesGeometry(report, panelWidth, deviceCount);
        AvaloniaVisualCore.CheckDevicesPage(report, devices.Image, probePalette, panelWidth, deviceCount);

        // ---------- ③ 收起态 · dash ----------
        //
        // 宽度要用画布自己量出来的 dash 宽度（MeasureDashWidth），不能拿展开面板的 560 顶替：
        // dash 的内容按自己的排版宽度居中摆放，面板给窄了 ⇒ 内容溢出被裁（2026-11-26 实测）。
        var dashCanvas = new IslandCanvas
        {
            Palette = palette,
            IsExpanded = false,
            View = "dash",
            Snapshot = SyntheticSnapshot(),
        };
        double dashWidth = dashCanvas.MeasureDashWidth();
        dashCanvas.BodyRect = new Rect(0, 0, dashWidth, 46);
        var dash = AvaloniaProbeRenderer.Render(dashCanvas, dashWidth, 46, palette.Surface);
        SavePng(dash.PngBytes, Path.Combine(options.OutputDirectory, "dash.png"));
        AvaloniaVisualCore.CheckDash(report, dash.Image, probePalette);

        return report;
    }

    /// <summary>设备页内容高度（与 IslandWindow.ExpandHeight 的算法一致）。</summary>
    private static double DevicesHeight(int deviceCount)
    {
        const double cardH = 150, gap = 12;
        double w = 520;
        int perRow = Math.Max(1, (int)((w + gap) / (132 + gap)));
        int rows = (deviceCount + perRow - 1) / perRow;
        if (rows == 0) return 40;
        return rows * cardH + (rows - 1) * gap;
    }

    /// <summary>
    /// 合成的监控快照：读数固定 ⇒ 渲染结果确定 ⇒ 可以当回归基线比。
    /// 不读真硬件（真硬件每台机器、每个时刻都不一样）。
    /// </summary>
    private static MonitorSnapshot SyntheticSnapshot()
    {
        var s = new MonitorSnapshot
        {
            Timestamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local),
            CpuUsage = 42,
            CpuFrequencyGHz = 3.7,
            MemoryUsagePercent = 63,
            MemoryUsedGB = 9.9,
            MemoryTotalGB = 16,
            GpuUsage = 18,
            GpuName = "Apple GPU",
            DiskUsagePercent = 7,
            DriveUsedGB = 220,
            DriveTotalGB = 512,
            DriveName = "/",
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
            BatteryPercent = 88,
            BatteryCharging = false,
            ThermalState = "正常",
            TopProcesses = new[]
            {
                new TopProcess { Name = "Code", CpuPercent = 22.5, MemoryMB = 1240, ExePath = "" },
                new TopProcess { Name = "Chrome", CpuPercent = 9.5, MemoryMB = 860, ExePath = "" },
                new TopProcess { Name = "MyIsland", CpuPercent = 3.2, MemoryMB = 210, ExePath = "" },
                new TopProcess { Name = "Finder", CpuPercent = 1.1, MemoryMB = 140, ExePath = "" },
                new TopProcess { Name = "Terminal", CpuPercent = 0.8, MemoryMB = 320, ExePath = "" },
            },
            TopProcessesByMem = new[]
            {
                new TopProcess { Name = "Code", CpuPercent = 22.5, MemoryMB = 1240, ExePath = "" },
                new TopProcess { Name = "Chrome", CpuPercent = 9.5, MemoryMB = 860, ExePath = "" },
                new TopProcess { Name = "Terminal", CpuPercent = 0.8, MemoryMB = 320, ExePath = "" },
                new TopProcess { Name = "MyIsland", CpuPercent = 3.2, MemoryMB = 210, ExePath = "" },
                new TopProcess { Name = "Finder", CpuPercent = 1.1, MemoryMB = 140, ExePath = "" },
            },
            Status = new ReadingStatus
            {
                Cpu = Reading<double>.Of(42),
                Memory = Reading<double>.Of(63),
                Gpu = Reading<double>.Of(18),
                Disk = Reading<double>.Of(7),
                NetDown = Reading<double>.Of(1_572_864),
                NetUp = Reading<double>.Of(131_072),
                CpuTemp = Reading<double>.Of(55),
                GpuTemp = Reading<double>.Of(48),
                SsdTemp = Reading<double>.Of(42),
                Battery = Reading<double>.Of(88),
            },
        };
        return s;
    }

    /// <summary>合成的设备列表（设备页用；不读真硬件）。</summary>
    private static IReadOnlyList<DeviceBattery.Device> SyntheticDevices(int count)
    {
        var kinds = new[] { "battery", "mouse", "keyboard", "headset", "gamepad", "ble" };
        var names = new[] { "MacBook Pro", "Magic Mouse", "Magic Keyboard", "AirPods Pro", "Xbox 手柄", "未知蓝牙设备" };
        var list = new List<DeviceBattery.Device>();
        for (int i = 0; i < count; i++)
        {
            int idx = i % kinds.Length;
            list.Add(new DeviceBattery.Device(kinds[idx], names[idx], 60 + i * 5, false, $"dev{i}"));
        }
        return list;
    }

    /// <summary>把 PNG 字节写到磁盘。</summary>
    private static void SavePng(byte[] png, string path) => File.WriteAllBytes(path, png);

    /// <summary>
    /// 默认输出目录：用户临时目录下的 MyIsland-avalonia-visual。
    /// 为什么不放仓库里：输出的是 PNG 产物（每次跑都变），放进仓库会变成一堆未跟踪文件。
    /// 想放到别处用 --out 指定。
    /// </summary>
    private static string DefaultOutputDirectory()
        => Path.Combine(Path.GetTempPath(), "MyIsland-avalonia-visual");

    private static void Usage()
    {
        Console.WriteLine("用法：avalonia-probe [--out <目录>]");
        Console.WriteLine("  --out      PNG 输出目录（默认：%TEMP%\\MyIsland-avalonia-visual）");
    }
}
