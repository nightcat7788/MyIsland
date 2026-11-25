namespace Island.Core.Capabilities;

/// <summary>主题 token 的归属端。</summary>
public enum ThemeTokenScope
{
    /// <summary>两端都有同名 token（同一个语义角色；两端取值按设计不同）。</summary>
    Shared = 0,
    /// <summary>只有 Windows 端有。</summary>
    WindowsOnly = 1,
    /// <summary>只有 macOS 端有。</summary>
    MacOnly = 2,
}

/// <summary>主题 token 的取值类型。</summary>
public enum ThemeTokenKind
{
    /// <summary>颜色 token（Windows 端是 System.Windows.Media.Color，mac 端是 Avalonia.Media.Color）。</summary>
    Color = 0,
    /// <summary>开关型 token（bool），**不是颜色** —— Windows 的 SquareCorners 是这一类。</summary>
    Flag = 1,
}

/// <summary>一个主题 token 的语义登记（**语义名 + 两端属性映射**，不含具体色值）。</summary>
/// <param name="Key">语义键（稳定标识，用于测试与文档引用，不随显示名或属性名变化）。</param>
/// <param name="Title">显示名。</param>
/// <param name="Role">语义说明：这个颜色在这套界面里承担什么角色（**两端同名 = 同一角色**）。</param>
/// <param name="Kind">取值类型（颜色 / 开关）。</param>
/// <param name="Scope">归属端。</param>
/// <param name="WindowsProperty">Windows 端 Palette 上的属性名；该端没有则 null。</param>
/// <param name="MacProperty">macOS 端 Palette 上的属性名；该端没有则 null。</param>
/// <param name="SameValueAcrossPlatforms">两端取值是否相同；null = 另一端没有对应属性，无从比较。</param>
/// <param name="Note">差异说明（写清"为什么不同"，避免下次又靠猜）。</param>
public sealed record ThemeTokenEntry(
    string Key,
    string Title,
    string Role,
    ThemeTokenKind Kind,
    ThemeTokenScope Scope,
    string? WindowsProperty,
    string? MacProperty,
    bool? SameValueAcrossPlatforms,
    string? Note);

/// <summary>
/// **主题 token 语义清单 —— 两端唯一的真相源**（呈现规则层的另一半，与能力清单同构）。
///
/// 为什么要有它：两端各写一份 Palette（Windows `src/Island.App/Themes/Theme.cs`、
/// macOS `src/Island.Avalonia/Themes.cs`），里面有 12 个**同名**的颜色 token
/// （Surface / SurfaceAlt / Text / TextDim / TextTertiary / Accent / Rim / Track / Hairline / Warning / Up / Down），
/// 但**每套主题下取值都不同**。以前这件事只活在人脑里，于是"同名"很容易被误读成"同值"。
///
/// 本清单只做一件事：**把"同名 token 指同一语义角色"这件事显式化，并把差异公开出来。**
/// 换句话说：
///   · 统一的是**语义名**（Key），不是**具体色值**；
///   · 两端同名 token 的语义角色必须一致（都是"唯一强调色"、都是"跌=绿"…）；
///   · 两端**取值按设计不同**，这是有意保留的差异，不是缺陷、不是待办。
///
/// 为什么不统一具体色值：Windows 端保留**暗色科技**配色身份（一屏一主角 + Accent）是已定的产品决策，
/// 强行统一色值 = 把 mac 端的"按资源配色"搬进 Windows = 违背既有决策。本任务**不改任何具体颜色**。
///
/// 用法：
///   · 两端渲染代码引用 token 时，按语义角色对号入座（Accent 两端都是"唯一强调色"）；
///   · 差异要公开时，查本清单（以及 `docs/capabilities.md` 的人读版本）；
///   · 漂移门禁是 `tests/Island.Tests/ThemeTokenTests.cs`：清单改了、两端 Palette 没跟着改 ⇒ 门禁红。
/// </summary>
public static class ThemeTokenManifest
{
    /// <summary>
    /// 两端取值按设计不同的**统一原因**（共享 token 的 Note 都引用它）。
    /// Windows 端是暗色科技配色（近黑中性底 + 单一强调色 + 一屏一主角），
    /// mac 端是 Apple 系统色系（中性色阶 + 一个个性强调色）—— 两套配色体系各自成立，不合并。
    /// </summary>
    public const string ValueDifferenceReason =
        "按设计不同：Windows 端是暗色科技配色（近黑中性底 + 单一强调色 + 一屏一主角），" +
        "mac 端是 Apple 系统色系（中性色阶 + 一个个性强调色）；本任务只统一语义名，不统一具体色值。";

    /// <summary>两端共享的 12 个同名颜色 token。</summary>
    public static readonly IReadOnlyList<ThemeTokenEntry> SharedTokens = new ThemeTokenEntry[]
    {
        new ThemeTokenEntry(
            "surface", "底色",
            "窗口底色（最底层）：所有元素画在它之上。",
            ThemeTokenKind.Color, ThemeTokenScope.Shared,
            "Surface", "Surface", false, ValueDifferenceReason),

        new ThemeTokenEntry(
            "surface-alt", "次级底色",
            "次级容器底色：卡片 / 分组内小块，比底色抬升一层。",
            ThemeTokenKind.Color, ThemeTokenScope.Shared,
            "SurfaceAlt", "SurfaceAlt", false,
            ValueDifferenceReason +
            " 另：两端的层级角色并不完全对称 —— Windows 端 SurfaceAlt 实际当「卡片 / 抬升层」底色用" +
            "（ExpandedPanel 的 CardBg / OverlayBg），mac 端把「卡片底」让给 Elevated、SurfaceAlt 才是分组内次级块。"),

        new ThemeTokenEntry(
            "text-primary", "主文字",
            "主文字色：标题与关键读数。",
            ThemeTokenKind.Color, ThemeTokenScope.Shared,
            "Text", "Text", false, ValueDifferenceReason),

        new ThemeTokenEntry(
            "text-secondary", "次级文字",
            "次级文字色：说明文案与单位。",
            ThemeTokenKind.Color, ThemeTokenScope.Shared,
            "TextDim", "TextDim", false, ValueDifferenceReason),

        new ThemeTokenEntry(
            "text-tertiary", "三级文字",
            "三级文字色：弱化提示（10px 说明文字也要读得清）。",
            ThemeTokenKind.Color, ThemeTokenScope.Shared,
            "TextTertiary", "TextTertiary", false, ValueDifferenceReason),

        new ThemeTokenEntry(
            "accent", "强调色",
            "唯一强调色：选中态、焦点、关键数据发光点（一屏只允许一个主角）。",
            ThemeTokenKind.Color, ThemeTokenScope.Shared,
            "Accent", "Accent", false, ValueDifferenceReason),

        new ThemeTokenEntry(
            "rim", "描边",
            "描边色：卡片 / 容器的轮廓（浅色主题明显，深色主题近乎不可见）。",
            ThemeTokenKind.Color, ThemeTokenScope.Shared,
            "Rim", "Rim", false, ValueDifferenceReason),

        new ThemeTokenEntry(
            "track", "轨道",
            "轨道色：进度条与表盘的未填充部分。",
            ThemeTokenKind.Color, ThemeTokenScope.Shared,
            "Track", "Track", false, ValueDifferenceReason),

        new ThemeTokenEntry(
            "hairline", "发丝线",
            "发丝线：分组之间、卡片内部的 1px 分隔线。",
            ThemeTokenKind.Color, ThemeTokenScope.Shared,
            "Hairline", "Hairline", false, ValueDifferenceReason),

        new ThemeTokenEntry(
            "warning", "告警",
            "告警色：读数偏高 / 需要注意（非致命）。",
            ThemeTokenKind.Color, ThemeTokenScope.Shared,
            "Warning", "Warning", false, ValueDifferenceReason),

        new ThemeTokenEntry(
            "up", "上升",
            "上升色：涨=红（中国股市习惯），并兼作危险级别的状态色。",
            ThemeTokenKind.Color, ThemeTokenScope.Shared,
            "Up", "Up", false, ValueDifferenceReason),

        new ThemeTokenEntry(
            "down", "下降",
            "下降色：跌=绿。",
            ThemeTokenKind.Color, ThemeTokenScope.Shared,
            "Down", "Down", false, ValueDifferenceReason),
    };

    /// <summary>仅 macOS 端有的 token。</summary>
    public static readonly IReadOnlyList<ThemeTokenEntry> MacOnlyTokens = new ThemeTokenEntry[]
    {
        new ThemeTokenEntry(
            "elevated", "抬升层",
            "抬升层底色：卡片 / 浮起层，比底色亮一层（Apple 风格材质分层）。",
            ThemeTokenKind.Color, ThemeTokenScope.MacOnly,
            null, "Elevated", null,
            "仅 macOS 端有：Windows 端没有对应的抬升层 token，其卡片底色由 surface-alt 承担。"),

        new ThemeTokenEntry(
            "fill", "控件填充",
            "控件内嵌填充：进度槽、输入框底。",
            ThemeTokenKind.Color, ThemeTokenScope.MacOnly,
            null, "Fill", null,
            "仅 macOS 端有：Windows 端没有独立的控件内嵌填充 token。"),

        new ThemeTokenEntry(
            "selection", "选中底",
            "选中态的柔和底色：页签胶囊等。",
            ThemeTokenKind.Color, ThemeTokenScope.MacOnly,
            null, "Selection", null,
            "仅 macOS 端有：Windows 端选中态直接用 accent 表达，没有单独的柔和底色。"),
    };

    /// <summary>仅 Windows 端有的 token。</summary>
    public static readonly IReadOnlyList<ThemeTokenEntry> WindowsOnlyTokens = new ThemeTokenEntry[]
    {
        new ThemeTokenEntry(
            "square-corners", "直角",
            "直角开关：像素主题用 3px 微圆角的直角形态。",
            ThemeTokenKind.Flag, ThemeTokenScope.WindowsOnly,
            "SquareCorners", null, null,
            "仅 Windows 端有，且**不是颜色**（是 bool）：mac 端没有直角开关，圆角固定。"),
    };

    private static readonly ThemeTokenEntry[] AllTokens =
        SharedTokens.Concat(MacOnlyTokens).Concat(WindowsOnlyTokens).ToArray();

    /// <summary>清单里全部条目（供文档与测试遍历）。</summary>
    public static IReadOnlyList<ThemeTokenEntry> All => AllTokens;

    /// <summary>按语义键取条目（取不到返回 null）。</summary>
    public static ThemeTokenEntry? Find(string key) =>
        AllTokens.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.Ordinal));

    /// <summary>Windows 端 Palette 上登记的属性名（含开关型 token）。</summary>
    public static IEnumerable<string> WindowsProperties =>
        AllTokens.Where(e => e.WindowsProperty is not null).Select(e => e.WindowsProperty!);

    /// <summary>macOS 端 Palette 上登记的属性名（含开关型 token）。</summary>
    public static IEnumerable<string> MacProperties =>
        AllTokens.Where(e => e.MacProperty is not null).Select(e => e.MacProperty!);
}
