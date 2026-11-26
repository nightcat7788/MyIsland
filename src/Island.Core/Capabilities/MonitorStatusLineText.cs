using System;

namespace Island.Core.Capabilities;

/// <summary>
/// 底部状态行**字段值的格式化规则**（两端共用，纯逻辑、零依赖、无 Avalonia / Win32 依赖）。
///
/// 为什么单独一个类：格式规则必须能被**零 NuGet 的测试工程**直接断言
/// （测试工程链的是 Island.Core\Capabilities\*.cs 源文件，不链 Avalonia 工程 —— 链不进去），
/// 放在 UI 层就断言不到，于是「读不到显示未知」这条硬规则会退化成没人守的口头约定。
///
/// 硬红线：**读不到就是「未知」**，绝不用 0 / 默认值 / 换算百分比充数。
/// </summary>
public static class MonitorStatusLineText
{
    /// <summary>读不到时的占位词（两端一致）。</summary>
    public const string Unknown = "未知";

    /// <summary>
    /// SSD 温度（摄氏度）：读不到（null / 非正值）⇒ 「未知」；
    /// 读得到 ⇒ 取整并带单位（42.4 ⇒ "42°C"）。
    /// </summary>
    public static string SsdTemp(double? celsius)
        => celsius is { } c && c > 0 ? $"{Math.Round(c)}°C" : Unknown;

    /// <summary>
    /// 运行时长：不足 1 天 ⇒ "3.2 小时"；≥1 天 ⇒ "1 天 3 小时"。
    /// 读不到（null / 非正值）⇒ 「未知」（0 小时不是"读数"，是"没读到"）。
    /// </summary>
    public static string Uptime(double? hours)
    {
        if (hours is not { } h || h <= 0) return Unknown;
        if (h < 24) return $"{h:0.0} 小时";
        int days = (int)Math.Floor(h / 24.0);
        int rest = (int)Math.Floor(h - days * 24.0);
        return $"{days} 天 {rest} 小时";
    }
}
