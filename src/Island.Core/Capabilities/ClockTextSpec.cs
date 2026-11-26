namespace Island.Core.Capabilities;

/// <summary>
/// **时钟视图的文本规格**（两端唯一真相源，2026-11-26 新增）。
///
/// 为什么需要它：2026-11-26 把两端默认收起视图统一成 clock 之后，暴露出
/// 「同一个视图、两端内容不同」——mac 的时间胶囊是 **时间 + 秒 + 日期 + 天气**，
/// Windows 此前只有 **时间 + 天气 + 上下行网速**。
/// 用户拍板：Windows 补上秒与日期（对齐 mac）。
///
/// 于是"秒怎么写""日期怎么写"就成了**两端必须一致**的东西 ——
/// 两端各写一份就又会漂（这正是能力清单要治的病）。
/// 所以文本格式放这里，两端渲染代码都从这里取：
///   · <c>src/Island.App/Controls/IslandCanvas.cs</c>（Windows）
///   · <c>src/Island.Avalonia/IslandCanvas.cs</c>（macOS）
///
/// 注意：**字号不在这份规格里** —— 字号属于视觉身份（各端保留），和 §8 的配色同理。
/// </summary>
public static class ClockTextSpec
{
    /// <summary>
    /// 日期文本：<c>"11月27日·周五"</c>。
    /// 月/日**不补零**（11月7日，不是 11月07日）；星期用两字缩写（周一…周日）；
    /// 中间是间隔点 **·**（不是 ASCII 空格 —— 在中文字体下空格太宽，看起来像断开了）。
    /// </summary>
    public static string DateText(DateTime now)
        => $"{now.Month}月{now.Day}日·{WeekdayShort(now.DayOfWeek)}";

    /// <summary>秒文本：<c>"27"</c>（两位补零）。</summary>
    public static string SecondsText(DateTime now) => now.ToString("ss");

    /// <summary>星期两字缩写（周一/周二…）—— 比"星期二"紧凑。</summary>
    public static string WeekdayShort(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        DayOfWeek.Sunday => "周日",
        _ => "",
    };
}
