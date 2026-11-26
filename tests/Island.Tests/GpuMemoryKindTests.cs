using System;
using System.Collections.Generic;
using Island.App.Services;
using Island.Core.Monitoring;

namespace Island.Tests;

/// <summary>
/// GPU 显存读数的**口径**用例（2026-11-26 task15）。
///
/// 背景（本机实测事实，Intel Arc 130T 核显）：
/// LHM 在这台机器上只报 D3D Shared Memory（已用 1500 MB / 总量 18384 MB），
/// 那个"总量"是**共享内存池大小（≈ 系统内存）**，不是显存容量。
/// 界面若把它当"显存总量"显示，用户会以为核显有十几 GB 显存 —— 误导性读数。
///
/// 口径怎么来的：**不是另发明一套判定**，而是显存分层的层号本身
/// （厂商显存 → D3D 专用显存 → D3D 共享显存，见 HardwareSensorRules.GpuMemoryUsedTiers）。
/// 数值与口径同源：同一个传感器选出来的，值和口径一起给出。
///
/// 本用例是**纯逻辑**（不依赖 WPF / Avalonia / 硬件库），可直接链进零依赖的测试工程。
/// </summary>
internal static class GpuMemoryKindTests
{
    public static void Run(Action<string, bool> check)
    {
        // ==================== 规则：三种口径都能区分出来 ====================

        check("口径：厂商显存（GPU Memory Used）判为 vendor",
            HardwareSensorRules.PickGpuMemory(HardwareSensorKind.GpuMemoryUsed, Sensors(
                ("GPU Memory Used", 2048))) is { } p1
            && p1.Kind == GpuMemoryKinds.Vendor && p1.Value == 2048);

        check("口径：厂商显存总量（GPU Memory Total）也判为 vendor",
            HardwareSensorRules.PickGpuMemory(HardwareSensorKind.GpuMemoryTotal, Sensors(
                ("GPU Memory Total", 8192))) is { } p1t
            && p1t.Kind == GpuMemoryKinds.Vendor && p1t.Value == 8192);

        check("口径：D3D 专用显存判为 dedicated",
            HardwareSensorRules.PickGpuMemory(HardwareSensorKind.GpuMemoryUsed, Sensors(
                ("D3D Dedicated Memory Used", 1024), ("D3D Shared Memory Used", 512))) is { } p2
            && p2.Kind == GpuMemoryKinds.Dedicated && p2.Value == 1024);

        check("口径：D3D 共享内存判为 shared（本机核显走的就是这条）",
            HardwareSensorRules.PickGpuMemory(HardwareSensorKind.GpuMemoryUsed, Sensors(
                ("D3D Shared Memory Used", 1500), ("D3D Shared Memory Free", 16884),
                ("D3D Shared Memory Total", 18384))) is { } p3
            && p3.Kind == GpuMemoryKinds.Shared && p3.Value == 1500);

        check("口径：厂商显存优先于 D3D 共享显存（与既有分层一致）",
            HardwareSensorRules.PickGpuMemory(HardwareSensorKind.GpuMemoryUsed, Sensors(
                ("GPU Memory Used", 2048), ("D3D Shared Memory Used", 512))) is { } p4
            && p4.Kind == GpuMemoryKinds.Vendor && p4.Value == 2048);

        check("口径：总量也带同一口径（与已用同源，不是另判一次）",
            HardwareSensorRules.PickGpuMemory(HardwareSensorKind.GpuMemoryTotal, Sensors(
                ("D3D Shared Memory Used", 1500), ("D3D Shared Memory Total", 18384))) is { } p5
            && p5.Kind == GpuMemoryKinds.Shared && p5.Value == 18384);

        // ---- 本机实测回归（2026-11-26，Intel(R) Arc(TM) 130T 核显，lhm-probe matrix 实测原值） ----
        // LHM 在这台机器上只报 D3D Shared Memory：Used = 1500.23 MB、Total = 18384.42 MB（Free 读不到值）。
        // 换算成 GB：已用 1.46 GB、总量 17.95 GB —— 那个"总量"是共享内存池大小（系统内存 31.5 GB），
        // 不是显存容量。这条断言锁住"改完之后的实际渲染文本"，别再退回成"1.5/18 GB"。
        const double realUsedMB = 1500.23046875, realTotalMB = 18384.419921875;
        var realUsed = HardwareSensorRules.PickGpuMemory(HardwareSensorKind.GpuMemoryUsed, Sensors(
            ("D3D Shared Memory Total", realTotalMB),
            ("D3D Shared Memory Free", 16884.19),
            ("D3D Shared Memory Used", realUsedMB)));
        var realTotal = HardwareSensorRules.PickGpuMemory(HardwareSensorKind.GpuMemoryTotal, Sensors(
            ("D3D Shared Memory Total", realTotalMB),
            ("D3D Shared Memory Free", 16884.19),
            ("D3D Shared Memory Used", realUsedMB)));
        check("本机实测：核显走的是共享口径（已用/总量都是 D3D Shared Memory）",
            realUsed is { Kind: GpuMemoryKinds.Shared } ru && realTotal is { Kind: GpuMemoryKinds.Shared } rt
            && ru.Value == realUsedMB && rt.Value == realTotalMB);
        check("本机实测：口径一致 ⇒ 合并后仍是共享",
            GpuMemoryDisplay.CombineKind(realUsed?.Kind, realTotal?.Kind) == GpuMemoryKinds.Shared);
        check("本机实测：渲染文本是 \"1.5 GB 共享\"，不含 17.9 / 18 那个共享池大小",
            GpuMemoryDisplay.Text(realUsedMB / 1024.0, realTotalMB / 1024.0, GpuMemoryKinds.Shared) == "1.5 GB 共享");

        check("口径：判不出来时是未知（\"\"），不是瞎猜一个",
            HardwareSensorRules.PickGpuMemory(HardwareSensorKind.GpuMemoryUsed, Sensors(
                ("Something Else Used", 700))) is null);

        // ==================== 显示文本：口径决定形状 ====================

        // 共享（核显）：只显示已用 + "共享"标注，**不显示那个总量**
        // —— 那个总量是共享内存池大小（≈ 系统内存），显示出来会被读成"显存容量"。
        string shared = GpuMemoryDisplay.Text(1.5, 17.95, GpuMemoryKinds.Shared);
        check("共享口径：渲染文本里有「共享」",
            shared.Contains("共享"));
        check("共享口径：渲染文本里不含那个总量（17.9 / 18 都不出现）",
            !shared.Contains("17.9") && !shared.Contains("18")
            && !shared.Contains("/"));
        check("共享口径：已用数值仍在（1.5 GB）",
            shared.Contains("1.5") && shared.Contains("GB"));

        // 专用 / 厂商显存：那就是显存容量，照旧显示"已用/总量"，且不得误标成共享
        string dedicated = GpuMemoryDisplay.Text(1.5, 8, GpuMemoryKinds.Dedicated);
        string vendor = GpuMemoryDisplay.Text(1.5, 8, GpuMemoryKinds.Vendor);
        check("专用显存：显示已用/总量（8 GB 真是显存容量）",
            dedicated.Contains("1.5") && dedicated.Contains("8"));
        check("厂商显存：显示已用/总量",
            vendor.Contains("1.5") && vendor.Contains("8"));
        check("专用/厂商显存：不得误标成共享",
            !dedicated.Contains("共享") && !vendor.Contains("共享"));
        check("专用/厂商显存：不加多余标注（不加噪音）",
            !dedicated.Contains("共享") && !dedicated.Contains("未知")
            && !vendor.Contains("共享") && !vendor.Contains("未知"));

        // 口径未知：不显示口径标注，**也不把可能冒充显存容量的总量显示出来**（宁可少显示）
        string unknown = GpuMemoryDisplay.Text(1.5, 17.95, GpuMemoryKinds.Unknown);
        check("口径未知：不得显示成有口径（不出现共享/专用/厂商）",
            !unknown.Contains("共享") && !unknown.Contains("专用") && !unknown.Contains("厂商"));
        check("口径未知：也不显示那个可能是共享池的总量",
            !unknown.Contains("17.9") && !unknown.Contains("18") && !unknown.Contains("/"));
        check("口径未知：已用数值仍在（1.5 GB）",
            unknown.Contains("1.5") && unknown.Contains("GB"));

        check("读不到已用显存时渲染文本为空（不编数字）",
            GpuMemoryDisplay.Text(null, 8, GpuMemoryKinds.Dedicated) == "");

        // ==================== 口径合并：已用与总量不一致 ⇒ 未知 ====================

        check("口径合并：两边一致 ⇒ 认那个口径",
            GpuMemoryDisplay.CombineKind(GpuMemoryKinds.Shared, GpuMemoryKinds.Shared) == GpuMemoryKinds.Shared);
        check("口径合并：只有一边有 ⇒ 用那一边",
            GpuMemoryDisplay.CombineKind(GpuMemoryKinds.Dedicated, GpuMemoryKinds.Unknown) == GpuMemoryKinds.Dedicated);
        check("口径合并：两边不一致（跨 GPU 混采）⇒ 未知，不猜",
            GpuMemoryDisplay.CombineKind(GpuMemoryKinds.Shared, GpuMemoryKinds.Dedicated) == GpuMemoryKinds.Unknown);
        check("口径合并：两边都没有 ⇒ 未知",
            GpuMemoryDisplay.CombineKind(GpuMemoryKinds.Unknown, GpuMemoryKinds.Unknown) == GpuMemoryKinds.Unknown);

        // ==================== 快照字段 ====================

        check("快照：GpuMemoryKind 默认未知（\"\"）",
            new MonitorSnapshot().GpuMemoryKind == GpuMemoryKinds.Unknown);
        check("快照：GpuMemoryKind 能带上口径",
            new MonitorSnapshot { GpuMemoryKind = GpuMemoryKinds.Shared }.GpuMemoryKind == GpuMemoryKinds.Shared);
    }

    /// <summary>候选传感器列表（名字 + 值）。</summary>
    private static List<HardwareSensor> Sensors(params (string Name, double Value)[] items)
    {
        var list = new List<HardwareSensor>();
        foreach (var (name, value) in items) list.Add(new HardwareSensor(name, value));
        return list;
    }
}
