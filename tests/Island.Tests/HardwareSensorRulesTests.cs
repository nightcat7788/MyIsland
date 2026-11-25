using System;
using System.Collections.Generic;
using System.Linq;
using Island.App.Services;

namespace Island.Tests;

/// <summary>
/// 硬件传感器取值规则的用例（2026-11-26）。
///
/// 背景：ThermalService 早期靠"名字白名单 + 包含匹配"挑传感器
/// （Pick(hw, SensorType.Temperature, "CPU Package", "Package", ...)），
/// 换硬件/固件就可能读不到或读错，多候选时的取舍只看名单顺序，
/// 而且 "Distance to TjMax"、"SoC"、"VRM" 这类干扰项没有统一排除。
/// 重做后的规则在 src/Island.App/Services/HardwareSensorRules.cs（纯逻辑，可链进测试工程）。
///
/// 参考：LiteMonitor（Diorser/LiteMonitor，MIT）的 SensorMatcher / SensorMap 分层口径。
/// </summary>
internal static class HardwareSensorRulesTests
{
    public static void Run(Action<string, bool> check)
    {
        // ==================== 干扰项排除 ====================
        check("Distance to TjMax 是距离不是温度：排除",
            HardwareSensorRules.IsDistractor("P-Core #1 Distance to TjMax"));
        check("名字里带 Package 的 Distance 也要排除（否则会被当成封装温度）",
            HardwareSensorRules.IsDistractor("CPU Package Distance to TjMax"));
        check("tjmax 关键词本身也排除",
            HardwareSensorRules.IsDistractor("E-Core #9 Distance to TjMax"));
        check("SoC 不是 CPU 封装温度：排除",
            HardwareSensorRules.IsDistractor("SoC"));
        check("VRM 不是 CPU 封装温度：排除",
            HardwareSensorRules.IsDistractor("VRM"));
        check("水泵不是温度：排除",
            HardwareSensorRules.IsDistractor("Water Pump"));
        check("水冷液/冷却液：排除",
            HardwareSensorRules.IsDistractor("Liquid") && HardwareSensorRules.IsDistractor("CPU Coolant"));
        check("风扇在温度语义下排除，但转速本身不是干扰项（语义决定）",
            HardwareSensorRules.IsDistractor("CPU Fan", isTemperature: true)
            && !HardwareSensorRules.IsDistractor("CPU Fan"));
        check("聚合值 Core Average 是聚合口径，单独一层处理（算干扰项但不放行到常规层）",
            HardwareSensorRules.IsDistractor("Core Average"));
        check("Tctl 不是干扰项：它是合法候选，只是优先级低",
            !HardwareSensorRules.IsDistractor("Core (Tctl)")
            && !HardwareSensorRules.IsDistractor("Core (Tctl/Tdie)"));

        // ==================== CPU 温度分层优先级 ====================
        check("封装温度优先于 Tdie",
            HardwareSensorRules.Pick(HardwareSensorKind.CpuTemperature, Temps(
                ("CPU Package", 58), ("Core (Tdie)", 55)))
            is { } p1 && p1.Value == 58 && p1.SensorName == "CPU Package");

        check("Tdie 赢过 Tctl（Tctl 虚高 10–27°C，既有结论见 CpuTemperatureRule）",
            HardwareSensorRules.Pick(HardwareSensorKind.CpuTemperature, Temps(
                ("Core (Tctl)", 70), ("Core (Tdie)", 58)))
            is { } p2 && p2.Value == 58 && p2.SensorName == "Core (Tdie)");

        check("Tctl/Tdie 合并值赢过 Tctl",
            HardwareSensorRules.Pick(HardwareSensorKind.CpuTemperature, Temps(
                ("Core (Tctl)", 70), ("Core (Tctl/Tdie)", 55)))
            is { } p3 && p3.Value == 55 && p3.SensorName == "Core (Tctl/Tdie)");

        check("只有 Tctl 时也认（Tctl 单独出现）",
            HardwareSensorRules.Pick(HardwareSensorKind.CpuTemperature, Temps(
                ("Core (Tctl)", 70)))
            is { } p4 && p4.Value == 70 && p4.SensorName == "Core (Tctl)");

        check("只有平均值时兜底用它（优先级最低）",
            HardwareSensorRules.Pick(HardwareSensorKind.CpuTemperature, Temps(
                ("Core Average", 49)))
            is { } p5 && p5.Value == 49 && p5.SensorName == "Core Average");

        check("逐核读数取最大值（多核兜底口径）",
            HardwareSensorRules.Pick(HardwareSensorKind.CpuTemperature, Temps(
                ("P-Core #1", 50), ("P-Core #2", 48), ("E-Core #5", 58)))
            is { } p6 && p6.Value == 58);

        check("同层多候选取最大值（两个 CPU Package 58/61 → 61）",
            HardwareSensorRules.Pick(HardwareSensorKind.CpuTemperature, Temps(
                ("CPU Package", 58), ("CPU Package", 61)))
            is { } p7 && p7.Value == 61 && p7.SensorName == "CPU Package");

        check("多 CCD 的 Tdie 取最热的一个",
            HardwareSensorRules.Pick(HardwareSensorKind.CpuTemperature, Temps(
                ("Core (Tdie)", 58), ("CCD1 (Tdie)", 70), ("CCD2 (Tdie)", 61)))
            is { } p8 && p8.Value == 70);

        check("干扰项混在里面时只取真实读数（Distance to TjMax 不参与）",
            HardwareSensorRules.Pick(HardwareSensorKind.CpuTemperature, Temps(
                ("CPU Package", 58), ("P-Core #1 Distance to TjMax", 55)))
            is { } p9 && p9.Value == 58);

        check("只有逐核读数时不会选到 Distance to TjMax",
            HardwareSensorRules.Pick(HardwareSensorKind.CpuTemperature, Temps(
                ("P-Core #1", 50), ("P-Core #1 Distance to TjMax", 61)))
            is { } p10 && p10.Value == 50 && p10.SensorName == "P-Core #1");

        check("全干扰项时读数未知（不返回假数）",
            HardwareSensorRules.Pick(HardwareSensorKind.CpuTemperature, Temps(
                ("SoC", 70), ("VRM", 80), ("Water Pump", 40)))
            is null);

        check("空候选 / null 候选 → 读数未知",
            HardwareSensorRules.Pick(HardwareSensorKind.CpuTemperature, new List<HardwareSensor>()) is null
            && HardwareSensorRules.Pick(HardwareSensorKind.CpuTemperature, null) is null);

        check("坏读数（0 / 负数 / 超过物理上限）被丢弃",
            HardwareSensorRules.Pick(HardwareSensorKind.CpuTemperature, Temps(
                ("CPU Package", 0), ("Core Average", -5), ("CPU Package", 300))) is null);

        // ==================== 与既有 CPU 温度规则一致 ====================
        // CpuTemperatureRule.cs 是既有结论（分层口径的"真源"）；本类必须与它选出同一个读数。
        var cross = new (string Name, double Value)[]
        {
            ("Core Max", 58), ("Core Average", 49), ("P-Core #1", 50), ("P-Core #2", 48),
            ("E-Core #1", 50), ("E-Core #5", 48), ("CPU Package", 58),
            ("P-Core #1 Distance to TjMax", 55), ("E-Core #9 Distance to TjMax", 61),
        };
        var legacy = CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>(
            cross.Select(c => new CpuTemperatureCandidate(c.Name, c.Value)).ToList()));
        var now = HardwareSensorRules.Pick(HardwareSensorKind.CpuTemperature,
            cross.Select(c => new HardwareSensor(c.Name, c.Value)).ToList());
        check("与 CpuTemperatureRule 的结论一致（本机实测传感器全集）",
            legacy is { } lp && now is { } np && lp.Value == np.Value && lp.SensorName == np.SensorName);

        var cross2 = new (string Name, double Value)[]
        {
            ("Core (Tctl)", 70), ("Core (Tdie)", 58), ("CCD1 (Tdie)", 61),
        };
        var legacy2 = CpuTemperatureRule.Pick(new List<CpuTemperatureCandidate>(
            cross2.Select(c => new CpuTemperatureCandidate(c.Name, c.Value)).ToList()));
        var now2 = HardwareSensorRules.Pick(HardwareSensorKind.CpuTemperature,
            cross2.Select(c => new HardwareSensor(c.Name, c.Value)).ToList());
        check("与 CpuTemperatureRule 的结论一致（AMD Tctl/Tdie/CCD）",
            legacy2 is { } lp2 && now2 is { } np2 && lp2.Value == np2.Value && lp2.SensorName == np2.SensorName);

        // ==================== GPU 语义 ====================
        check("\"GPU Core\" 判为 GPU 温度",
            HardwareSensorRules.KindOf("GPU Core", isTemperature: true) == HardwareSensorKind.GpuTemperature);
        check("\"GPU Package\" 判为 GPU 功耗",
            HardwareSensorRules.KindOf("GPU Package", HardwareSensorCategory.Power) == HardwareSensorKind.GpuPower);
        check("\"GPU Memory Used\" 判为显存已用",
            HardwareSensorRules.KindOf("GPU Memory Used", HardwareSensorCategory.Data) == HardwareSensorKind.GpuMemoryUsed);
        check("\"GPU Memory Total\" 判为显存总量",
            HardwareSensorRules.KindOf("GPU Memory Total", HardwareSensorCategory.Data) == HardwareSensorKind.GpuMemoryTotal);
        check("\"GPU Core\" 作为时钟判为 GPU 频率",
            HardwareSensorRules.KindOf("GPU Core", HardwareSensorCategory.Clock) == HardwareSensorKind.GpuClock);
        check("\"CPU Package\" 判为 CPU 温度",
            HardwareSensorRules.KindOf("CPU Package", isTemperature: true) == HardwareSensorKind.CpuTemperature);
        check("认不出来的名字不硬塞语义",
            HardwareSensorRules.KindOf("Whatever", HardwareSensorCategory.Data) is null);

        check("GPU 温度：核心温度优先于热点温度",
            HardwareSensorRules.Pick(HardwareSensorKind.GpuTemperature, Temps(
                ("GPU Core", 55), ("GPU Hot Spot", 70)))
            is { } p11 && p11.Value == 55 && p11.SensorName == "GPU Core");
        check("GPU 温度：没有核心温度时用热点温度",
            HardwareSensorRules.Pick(HardwareSensorKind.GpuTemperature, Temps(
                ("GPU Hot Spot", 70)))
            is { } p12 && p12.Value == 70);
        check("GPU 功耗：封装功耗优先",
            HardwareSensorRules.Pick(HardwareSensorKind.GpuPower, Temps(
                ("GPU Package", 120), ("GPU Core", 60)))
            is { } p13 && p13.Value == 120 && p13.SensorName == "GPU Package");
        check("显存已用：厂商显存优先于 D3D 共享显存",
            HardwareSensorRules.Pick(HardwareSensorKind.GpuMemoryUsed, Temps(
                ("GPU Memory Used", 2048), ("D3D Shared Memory Used", 512)))
            is { } p14 && p14.Value == 2048);
        check("显存已用：没有厂商显存时用 D3D 专用显存",
            HardwareSensorRules.Pick(HardwareSensorKind.GpuMemoryUsed, Temps(
                ("D3D Dedicated Memory Used", 1024), ("D3D Shared Memory Used", 512)))
            is { } p15 && p15.Value == 1024);
        check("显存总量",
            HardwareSensorRules.Pick(HardwareSensorKind.GpuMemoryTotal, Temps(
                ("D3D Dedicated Memory Total", 8192)))
            is { } p16 && p16.Value == 8192);
        check("GPU 频率：核心频率优先，且不会把显存频率当成核心频率",
            HardwareSensorRules.Pick(HardwareSensorKind.GpuClock, Temps(
                ("GPU Core", 2100), ("GPU Memory", 11000)))
            is { } p17 && p17.Value == 2100 && p17.SensorName == "GPU Core");
        check("只有显存频率时不冒充核心频率",
            HardwareSensorRules.Pick(HardwareSensorKind.GpuClock, Temps(
                ("GPU Memory", 11000))) is null);
        check("GPU 风扇",
            HardwareSensorRules.Pick(HardwareSensorKind.GpuFan, Temps(
                ("GPU Fan", 1200)))
            is { } p18 && p18.Value == 1200);

        // ==================== 存储 / 主板语义 ====================
        check("\"Composite Temperature\" 判为存储温度",
            HardwareSensorRules.KindOf("Composite Temperature", isTemperature: true) == HardwareSensorKind.StorageTemperature);
        check("存储温度：SMART 合成温度优先",
            HardwareSensorRules.Pick(HardwareSensorKind.StorageTemperature, Temps(
                ("Composite Temperature", 42)))
            is { } p19 && p19.Value == 42);
        check("存储温度：NAND 温度也认",
            HardwareSensorRules.Pick(HardwareSensorKind.StorageTemperature, Temps(
                ("NAND", 40)))
            is { } p20 && p20.Value == 40);
        check("主板温度：System 优先于芯片组",
            HardwareSensorRules.Pick(HardwareSensorKind.MotherboardTemperature, Temps(
                ("System", 35), ("Chipset", 60)))
            is { } p21 && p21.Value == 35 && p21.SensorName == "System");
        check("主板温度：没有标准名字时取合理区间内最大的一条",
            HardwareSensorRules.Pick(HardwareSensorKind.MotherboardTemperature, Temps(
                ("Temperature #1", 30), ("Temperature #2", 70)))
            is { } p22 && p22.Value == 30);
        check("CPU 风扇只认名字带 cpu 的（机箱风扇不当成 CPU 风扇）",
            HardwareSensorRules.Pick(HardwareSensorKind.CpuFan, Temps(
                ("CPU Fan", 1500), ("System Fan", 900)))
            is { } p23 && p23.Value == 1500
            && HardwareSensorRules.Pick(HardwareSensorKind.CpuFan, Temps(("System Fan", 900))) is null);

        // ==================== 辅助方法 ====================
        check("BestTemperatureName 返回命中的传感器名（诊断日志用）",
            HardwareSensorRules.BestTemperatureName(Temps(("CPU Package", 58), ("Core (Tdie)", 55))) == "CPU Package");
        check("BestSensorName 选不到时返回 null",
            HardwareSensorRules.BestSensorName(HardwareSensorKind.GpuClock, Temps(("GPU Memory", 11000))) is null);
    }

    /// <summary>把 (名字, 读数) 列表转成候选列表。</summary>
    private static List<HardwareSensor> Temps(params (string Name, double Value)[] items)
    {
        var list = new List<HardwareSensor>();
        foreach (var it in items) list.Add(new HardwareSensor(it.Name, it.Value));
        return list;
    }
}
