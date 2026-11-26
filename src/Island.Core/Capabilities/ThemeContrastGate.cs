namespace Island.Core.Capabilities;

/// <summary>
/// 一条**可读性门槛**：某个语义 token 在某个平台上，对底色的最低对比度（WCAG 相对亮度比）。
/// </summary>
/// <param name="TokenKey">语义键（对应 <see cref="ThemeTokenManifest.SharedTokens"/> 的 Key）。</param>
/// <param name="Role">语义说明。</param>
/// <param name="WindowsMin">Windows 端最低对比度。</param>
/// <param name="MacMin">macOS 端最低对比度。</param>
/// <param name="Note">差异说明（为什么两端门槛不同 —— 避免下次又靠猜）。</param>
public sealed record ContrastRule(
    string TokenKey,
    string Role,
    double WindowsMin,
    double MacMin,
    string Note);

/// <summary>
/// **主题可读性门槛（按平台）** —— 2026-11-26 新增，是配色差异公开的另一半。
///
/// 背景（实测）：Windows 端 9 套主题全部通过 WCAG 门槛（正文 ≥7:1、次要 ≥6:1、
/// 三级 ≥4.5:1、强调 ≥4.5:1、状态色 ≥3:1），但 **macOS 端 9 套主题全部不通过同一套门槛**：
/// 三级文字最差只有 2.50:1（Light 主题），而三级文字正是**底部状态行（9px）**用的颜色。
/// mac 端此前**一条对比度断言都没有** —— 测试工程只链了 Windows 的 Theme.cs，
/// mac 的 Themes.cs 依赖 Avalonia 进不了零依赖测试工程，于是这层规则是"裸奔"的。
///
/// 本类的定位：**统一的是"两端都要有门槛"这条规则，门槛值按平台各设**。
///   · Windows 端沿用 WCAG 口径（正文 7 / 次要 6 / 三级 4.5 / 强调 4.5 / 状态色 3）；
///   · macOS 端按 Apple 视觉口径取**当前设计的实测基线**（三级文字 2.4、强调 3.0 等），
///     **不改任何具体色值**（守住 §8 的产品决策），但把基线钉住 ⇒ 以后谁把某个颜色调暗到基线以下，
///     门禁立刻红（"改坏即红"）。
///
/// 两端的门槛差异是**有意的**（Apple 的三级文字本来就更淡），但它必须**公开**，
/// 而不是靠人记得 —— 这就是本类存在的意义。
///
/// 门禁：<c>tests/Island.Tests/ThemeContrastTests.cs</c>（静态解析两端主题源码 → 逐套主题逐 token 比对）。
/// </summary>
public static class ThemeContrastGate
{
    /// <summary>
    /// 全部可读性门槛。Token 键取自 <see cref="ThemeTokenManifest.SharedTokens"/>，
    /// 语义角色在两端一致（同 token = 同角色），**门槛值按平台各设**。
    /// </summary>
    public static readonly IReadOnlyList<ContrastRule> Rules = new ContrastRule[]
    {
        new ContrastRule(
            "text-primary", "主文字：标题与关键读数",
            7.0, 7.0,
            "两端同门槛：正文可读性没有理由打折。"),

        new ContrastRule(
            "text-secondary", "次级文字：说明文案与单位",
            6.0, 4.4,
            "mac 端 4.4：Apple 的次级标签本来就比 WCAG 6:1 淡；基线取当前实测下限 4.54 再留 0.1 余量。"),

        new ContrastRule(
            "text-tertiary", "三级文字：弱化提示（10px 说明文字也要读得清）",
            4.5, 2.4,
            "⚠️ 两端差最大的一项：Windows ≥4.5，mac 2.4（实测下限 2.50）。" +
            "这个 token 是**底部状态行（9px）**的颜色 ⇒ mac 上那行字确实偏淡。" +
            "已按产品决策保留 mac 的 Apple 口径（不改具体色值），但下限被钉住，改暗即红。"),

        new ContrastRule(
            "accent", "强调色（也当文字用：选中的电源计划 / 排序分段选中段）",
            4.5, 3.0,
            "mac 端 3.0：实测下限 3.26（Light 主题 #0A84FF 压在 #F2F2F5 上）—— Apple 系统蓝本身就不到 4.5:1。"),

        new ContrastRule(
            "warning", "告警色：读数偏高 / 需要注意（非致命）",
            3.0, 2.9,
            "mac 端 2.9：实测下限 2.9989（Light 主题）—— 贴着 3:1，门槛留 0.1 余量。"),

        new ContrastRule(
            "up", "上升色：涨=红（并兼作危险级别状态色）",
            3.0, 3.0,
            "两端同门槛：状态色是「能不能立刻看见」，没有理由打折。"),

        new ContrastRule(
            "down", "下降色：跌=绿",
            3.0, 3.0,
            "两端同门槛：同上。"),

        new ContrastRule(
            "track", "轨道色：进度条与表盘的未填充部分",
            1.12, 1.11,
            "口径是「叠在底色上要比底色亮 ≥ 12%」（半透明色先与底色合成再算）。" +
            "mac 实测下限 1.1239（IslandDay），门槛 1.11。"),

        new ContrastRule(
            "hairline", "发丝线：分组之间、卡片内部的 1px 分隔线",
            1.12, 1.11,
            "同上口径；mac 实测下限 1.1999。"),
    };

    /// <summary>某平台下这条门槛的最低对比度。</summary>
    public static double MinFor(ContrastRule rule, bool macOS) => macOS ? rule.MacMin : rule.WindowsMin;

    /// <summary>sRGB 相对亮度（WCAG 定义）。</summary>
    public static double Luminance(byte r, byte g, byte b)
    {
        double R = Channel(r), G = Channel(g), B = Channel(b);
        return 0.2126 * R + 0.7152 * G + 0.0722 * B;
    }

    /// <summary>两个不透明色之间的对比度比值（1.0 ~ 21.0）。</summary>
    public static double Ratio(
        byte r1, byte g1, byte b1,
        byte r2, byte g2, byte b2)
    {
        double l1 = Luminance(r1, g1, b1);
        double l2 = Luminance(r2, g2, b2);
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    /// <summary>半透明色叠在底色上之后的结果（alpha 0-255）。</summary>
    public static (byte R, byte G, byte B) Over(
        byte r, byte g, byte b, byte a,
        byte br, byte bg, byte bb)
    {
        double k = a / 255.0;
        return (
            (byte)Math.Round(r * k + br * (1 - k)),
            (byte)Math.Round(g * k + bg * (1 - k)),
            (byte)Math.Round(b * k + bb * (1 - k)));
    }

    private static double Channel(byte v)
    {
        double c = v / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
