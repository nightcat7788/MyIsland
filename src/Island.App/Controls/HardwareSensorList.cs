using System;
using System.Collections.Generic;
using Island.App.Services;

namespace Island.App.Controls;

/// <summary>传感器读数的告警级别（暗色科技配色：正常走中性色，只有异常才上告警色）。</summary>
public enum HardwareSensorSeverity
{
    /// <summary>正常（中性色）。</summary>
    Normal = 0,
    /// <summary>偏高（告警色）。</summary>
    Warning = 1,
    /// <summary>危险（危险色）。</summary>
    Danger = 2,
}

/// <summary>按硬件分组后的一组传感器。</summary>
public sealed record HardwareSensorGroup(string Hardware, IReadOnlyList<HardwareSensorEntry> Sensors);

/// <summary>
/// 硬件传感器列表的**纯逻辑**：名字 → 语义 → 单位 / 格式 / 告警级别 / 状态词。
///
/// 纯逻辑（无 Win32 / WPF / 硬件库依赖）⇒ 可链进零 NuGet 包的测试工程（见 tests/Island.Tests/Island.Tests.csproj），
/// 于是"读不到显示未知、绝不显示 0"这条红线有门禁守着，而不是靠人记得。
///
/// 名字 → 语义的识别复用 <see cref="HardwareSensorRules"/>（既有结论：分层匹配 + 干扰项排除），
/// 本类只补上"语义 → 怎么显示"这一段。
/// 数据契约（<see cref="HardwareSensorEntry"/> / <see cref="IHardwareSensorSource"/>）在
/// <c>Services/HardwareSensorRules.cs</c> —— 那个文件 mac 端也编译，契约要两端同源。
/// </summary>
public static class HardwareSensorList
{
    /// <summary>温度的告警阈值（与监控页既有规则一致：≤70 正常、≤85 偏高、>85 危险）。</summary>
    public const double TemperatureWarningCelsius = 70;

    /// <summary>温度的危险阈值（同上）。</summary>
    public const double TemperatureDangerCelsius = 85;

    /// <summary>读不到时的状态词（**不是 0**）。</summary>
    public const string UnknownText = "未知";

    /// <summary>读不到且需要管理员权限时的状态词（与监控页既有约定同名）。</summary>
    public const string NeedAdminText = "需管理员";

    /// <summary>类别 → 单位。</summary>
    public static string UnitOf(HardwareSensorCategory category) => category switch
    {
        HardwareSensorCategory.Temperature => "°C",
        HardwareSensorCategory.Power => "W",
        HardwareSensorCategory.Fan => "RPM",
        HardwareSensorCategory.Clock => "MHz",
        HardwareSensorCategory.Data => "MB",
        HardwareSensorCategory.Voltage => "V",
        HardwareSensorCategory.Load => "%",
        _ => "",
    };

    /// <summary>类别 → 小数位数（温度 1 位、电压 2 位、转速 / 频率取整）。</summary>
    public static int DecimalsOf(HardwareSensorCategory category) => category switch
    {
        HardwareSensorCategory.Temperature => 1,
        HardwareSensorCategory.Power => 1,
        HardwareSensorCategory.Data => 1,
        HardwareSensorCategory.Load => 1,
        HardwareSensorCategory.Voltage => 2,
        HardwareSensorCategory.Fan => 0,
        HardwareSensorCategory.Clock => 0,
        _ => 0,
    };

    /// <summary>把读数格式化成显示文本（只处理"有值"的情况；没值走 <see cref="StatusText"/>）。</summary>
    public static string FormatValue(HardwareSensorCategory category, double value)
        => value.ToString("F" + DecimalsOf(category), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// 告警级别。**只有温度有阈值**（沿用监控页既有规则：≤70 正常、≤85 偏高、>85 危险），
    /// 其余类别一律正常 —— 没有既定口径就不自己发明告警线（正常的读数不该被染色）。
    /// </summary>
    public static HardwareSensorSeverity SeverityOf(HardwareSensorCategory category, double? value)
    {
        if (value is not { } v) return HardwareSensorSeverity.Normal;
        if (category != HardwareSensorCategory.Temperature) return HardwareSensorSeverity.Normal;
        if (v > TemperatureDangerCelsius) return HardwareSensorSeverity.Danger;
        if (v > TemperatureWarningCelsius) return HardwareSensorSeverity.Warning;
        return HardwareSensorSeverity.Normal;
    }

    /// <summary>
    /// 读数位显示什么。
    /// **没有值就是"未知"（或"需管理员"），绝不用 0 代替** —— 0 是合法读数，混淆两者就是撒谎。
    /// </summary>
    /// <param name="entry">传感器。</param>
    /// <param name="elevated">当前进程是否以管理员身份运行。</param>
    public static string StatusText(HardwareSensorEntry entry, bool elevated)
    {
        if (entry.Value is { } v) return FormatValue(entry.Category, v);
        // 读不到 + 需要提权的传感器（主板 / SuperIO、CPU 的 MSR 温度）+ 非管理员 ⇒ 说清是权限问题
        if (entry.RequiresElevation && !elevated) return NeedAdminText;
        return UnknownText;
    }

    /// <summary>类别 → 中文标签（识别不出具体语义时的兜底）。</summary>
    public static string CategoryLabel(HardwareSensorCategory category) => category switch
    {
        HardwareSensorCategory.Temperature => "温度",
        HardwareSensorCategory.Power => "功耗",
        HardwareSensorCategory.Fan => "风扇",
        HardwareSensorCategory.Clock => "频率",
        HardwareSensorCategory.Data => "数据",
        HardwareSensorCategory.Load => "占用",
        HardwareSensorCategory.Voltage => "电压",
        HardwareSensorCategory.Control => "控制",
        _ => "其它",
    };

    /// <summary>
    /// 传感器名 → 语义（中文标签）。识别规则复用 <see cref="HardwareSensorRules"/>（既有结论）。
    /// 识别不出来的（电压轨、各类扩展传感器……）退回类别标签 —— 宁可笼统，也不硬套一个语义。
    /// 干扰项（"Distance to TjMax" 之类）由规则类排除 ⇒ 不会被误标成"CPU 温度"。
    /// </summary>
    public static string SemanticLabel(HardwareSensorEntry entry)
    {
        var kind = HardwareSensorRules.KindOf(entry.Sensor, entry.Category);
        return kind switch
        {
            HardwareSensorKind.CpuTemperature => "CPU 温度",
            HardwareSensorKind.CpuPower => "CPU 功耗",
            HardwareSensorKind.CpuFan => "CPU 风扇",
            HardwareSensorKind.GpuTemperature => "GPU 温度",
            HardwareSensorKind.GpuPower => "GPU 功耗",
            HardwareSensorKind.GpuFan => "GPU 风扇",
            HardwareSensorKind.GpuMemoryUsed => "显存已用",
            HardwareSensorKind.GpuMemoryTotal => "显存总量",
            HardwareSensorKind.GpuClock => "核心频率",
            HardwareSensorKind.StorageTemperature => "存储温度",
            HardwareSensorKind.MotherboardTemperature => "主板温度",
            HardwareSensorKind.MotherboardFan => "机箱风扇",
            _ => CategoryLabel(entry.Category),
        };
    }

    /// <summary>
    /// 按硬件分组（保持枚举顺序）：同一条硬件的传感器排在一起，组内保持原顺序。
    /// </summary>
    public static IReadOnlyList<HardwareSensorGroup> GroupByHardware(IReadOnlyList<HardwareSensorEntry>? entries)
    {
        var groups = new List<HardwareSensorGroup>();
        if (entries is null || entries.Count == 0) return groups;
        var current = new List<HardwareSensorEntry>();
        string? currentName = null;
        foreach (var e in entries)
        {
            if (currentName is null || !string.Equals(currentName, e.Hardware, StringComparison.Ordinal))
            {
                if (currentName is not null)
                {
                    groups.Add(new HardwareSensorGroup(currentName, current));
                    current = new List<HardwareSensorEntry>();
                }
                currentName = e.Hardware;
            }
            current.Add(e);
        }
        if (currentName is not null) groups.Add(new HardwareSensorGroup(currentName, current));
        return groups;
    }

    /// <summary>统计：有读数 / 读不到（未知或需管理员）。给窗口顶部的汇总行用。</summary>
    public static (int Total, int WithValue, int Missing) Count(IReadOnlyList<HardwareSensorEntry>? entries)
    {
        if (entries is null || entries.Count == 0) return (0, 0, 0);
        int withValue = 0;
        foreach (var e in entries) if (e.Value.HasValue) withValue++;
        return (entries.Count, withValue, entries.Count - withValue);
    }
}
