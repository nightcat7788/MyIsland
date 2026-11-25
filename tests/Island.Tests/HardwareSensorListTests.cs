using System;
using System.Collections.Generic;
using Island.App.Controls;
using Island.App.Services;

namespace Island.Tests;

/// <summary>
/// 硬件传感器列表的纯逻辑用例（2026-11-26 task4：硬件详情面板）。
///
/// 守的是两条红线：
///  ① **读不到就是"未知"，绝不用 0 代替**（0 是合法读数：风扇停转 0 RPM、显卡空闲 0 W）；
///  ② 名字 → 语义的映射不乱套（干扰项不被误标成"CPU 温度"）。
///
/// 被测对象在 src/Island.App/Controls/HardwareSensorList.cs（纯逻辑，可链进零包测试工程）。
/// </summary>
internal static class HardwareSensorListTests
{
    public static void Run(Action<string, bool> check)
    {
        // ==================== 单位 ====================
        check("单位：温度 °C / 功耗 W / 风扇 RPM / 频率 MHz / 电压 V / 占用 %",
            HardwareSensorList.UnitOf(HardwareSensorCategory.Temperature) == "°C"
            && HardwareSensorList.UnitOf(HardwareSensorCategory.Power) == "W"
            && HardwareSensorList.UnitOf(HardwareSensorCategory.Fan) == "RPM"
            && HardwareSensorList.UnitOf(HardwareSensorCategory.Clock) == "MHz"
            && HardwareSensorList.UnitOf(HardwareSensorCategory.Voltage) == "V"
            && HardwareSensorList.UnitOf(HardwareSensorCategory.Load) == "%");

        // ==================== 格式 ====================
        check("读数格式：温度一位小数、风扇取整、电压两位小数",
            HardwareSensorList.FormatValue(HardwareSensorCategory.Temperature, 48.46) == "48.5"
            && HardwareSensorList.FormatValue(HardwareSensorCategory.Fan, 1200) == "1200"
            && HardwareSensorList.FormatValue(HardwareSensorCategory.Voltage, 1.234) == "1.23");

        // ==================== 读不到 ≠ 0 ====================
        var missing = new HardwareSensorEntry("AMD Ryzen", "CPU Package", HardwareSensorCategory.Temperature, null, false);
        check("读不到显示「未知」，绝不是 0",
            HardwareSensorList.StatusText(missing, elevated: true) == HardwareSensorList.UnknownText
            && HardwareSensorList.StatusText(missing, elevated: false) != "0");

        var needsAdmin = new HardwareSensorEntry("主板", "CPU Fan", HardwareSensorCategory.Fan, null, true);
        check("读不到且需要提权、非管理员 ⇒ 「需管理员」",
            HardwareSensorList.StatusText(needsAdmin, elevated: false) == HardwareSensorList.NeedAdminText);
        check("提权后仍读不到 ⇒ 「未知」（不假装有值）",
            HardwareSensorList.StatusText(needsAdmin, elevated: true) == HardwareSensorList.UnknownText);

        var zero = new HardwareSensorEntry("NVIDIA", "GPU Fan", HardwareSensorCategory.Fan, 0, false);
        check("读数为 0 显示「0」（与读不到区分开）",
            HardwareSensorList.StatusText(zero, elevated: true) == "0"
            && HardwareSensorList.StatusText(zero, elevated: true) != HardwareSensorList.UnknownText);

        // ==================== 告警级别（与监控页既有规则一致） ====================
        check("温度告警：≤70 正常 / ≤85 偏高 / >85 危险",
            HardwareSensorList.SeverityOf(HardwareSensorCategory.Temperature, 70) == HardwareSensorSeverity.Normal
            && HardwareSensorList.SeverityOf(HardwareSensorCategory.Temperature, 75) == HardwareSensorSeverity.Warning
            && HardwareSensorList.SeverityOf(HardwareSensorCategory.Temperature, 85) == HardwareSensorSeverity.Warning
            && HardwareSensorList.SeverityOf(HardwareSensorCategory.Temperature, 95) == HardwareSensorSeverity.Danger);
        check("没有既定口径的类别不自己发明告警线（功耗 / 风扇一律正常）",
            HardwareSensorList.SeverityOf(HardwareSensorCategory.Power, 300) == HardwareSensorSeverity.Normal
            && HardwareSensorList.SeverityOf(HardwareSensorCategory.Fan, 6000) == HardwareSensorSeverity.Normal);

        // ==================== 名字 → 语义 ====================
        check("名字 → 语义：CPU Package → CPU 温度",
            HardwareSensorList.SemanticLabel(Entry("CPU", "CPU Package", HardwareSensorCategory.Temperature, 55)) == "CPU 温度");
        check("名字 → 语义：GPU Core → GPU 温度",
            HardwareSensorList.SemanticLabel(Entry("显卡", "GPU Core", HardwareSensorCategory.Temperature, 48)) == "GPU 温度");
        check("名字 → 语义：CPU Fan → CPU 风扇、System Fan → 机箱风扇",
            HardwareSensorList.SemanticLabel(Entry("主板", "CPU Fan", HardwareSensorCategory.Fan, 1200)) == "CPU 风扇"
            && HardwareSensorList.SemanticLabel(Entry("主板", "System Fan #1", HardwareSensorCategory.Fan, 900)) == "机箱风扇");
        check("名字 → 语义：GPU Memory Used → 显存已用",
            HardwareSensorList.SemanticLabel(Entry("显卡", "GPU Memory Used", HardwareSensorCategory.Data, 1024)) == "显存已用");
        check("名字 → 语义：认不出来的退回类别标签（电压）",
            HardwareSensorList.SemanticLabel(Entry("主板", "Vcore", HardwareSensorCategory.Voltage, 1.2)) == "电压");

        // ==================== 干扰项不被误标 ====================
        check("干扰项不被误标成 CPU 温度：Distance to TjMax",
            HardwareSensorList.SemanticLabel(Entry("AMD Ryzen", "P-Core #1 Distance to TjMax", HardwareSensorCategory.Temperature, 40)) != "CPU 温度");

        // ==================== 分组 ====================
        var entries = new List<HardwareSensorEntry>
        {
            Entry("CPU", "CPU Package", HardwareSensorCategory.Temperature, 55),
            Entry("CPU", "Core #1", HardwareSensorCategory.Temperature, 51),
            Entry("主板", "System Fan #1", HardwareSensorCategory.Fan, 900),
        };
        var groups = HardwareSensorList.GroupByHardware(entries);
        check("按硬件分组且保持枚举顺序",
            groups.Count == 2
            && groups[0].Hardware == "CPU" && groups[0].Sensors.Count == 2
            && groups[1].Hardware == "主板" && groups[1].Sensors.Count == 1);

        // ==================== 统计（有值 / 读不到分开数） ====================
        var (total, withValue, missingCount) = HardwareSensorList.Count(entries);
        check("统计：有值与读不到分开数",
            total == 3 && withValue == 3 && missingCount == 0);
        var (t2, w2, m2) = HardwareSensorList.Count(new List<HardwareSensorEntry>
        {
            Entry("CPU", "CPU Package", HardwareSensorCategory.Temperature, 55),
            missing,
        });
        check("统计：读不到的算进「未知」",
            t2 == 2 && w2 == 1 && m2 == 1);
    }

    private static HardwareSensorEntry Entry(string hardware, string sensor, HardwareSensorCategory category, double? value)
        => new(hardware, sensor, category, value, false);
}
