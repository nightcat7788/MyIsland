using System.Windows.Media;

namespace Island.App.Themes;

/// <summary>语义颜色 token：UI 只引用语义，不写死具体色值。</summary>
///
/// 设计约定（2026-11-24 视觉重做，"暗色科技"）：
/// - 一套主题 = 近黑的中性色（底 / 抬升层 / 三级文字 / 轨道 / 发丝线）+ **一个**强调色 + 状态色。
///   大面积留黑，强调色只在三处发光：CPU 主读数（数字 + 环）、面板顶部的一束辉光、选中的页签与电源计划。
///   其余读数一律中性色，只有超阈值（≥70% 警示、≥90% 危险）才换成状态色 —— 告警是画面里唯一跳出来的颜色。
/// - 数据不再按指标配色（以前 CPU 蓝 / 内存青 / GPU 粉是写死的常量）：9 套主题因此只差一个强调色，
///   "像素""赛博朋克"这类有个性的主题在展开面板里完全看不出来。
///   ⚠️ **例外（2026-11-27）**：**收起态**（dash / combo / monitor 三条胶囊带）改为**按资源配色** ——
///   色值取自两端唯一真相源 `Island.Core/Capabilities/ResourceColorSpec.cs`（来源：用户给的参考图），
///   环/图标/柱用资源色、读数仍按阈值分级（正常白、≥70% 黄、≥90% 红）。
///   **展开面板仍然是本契约**：单强调色 + 状态色，不按指标配色（范围门禁见 ResourceColorTests）。
/// - 文字对比度：Text ≥ 7:1、TextDim ≥ 6:1、TextTertiary ≥ 4.5:1（10px 的说明文字也要读得清）；
///   强调色当文字用（选中的电源计划之外的场景）≥ 4.5:1。由 Island.Tests 的 DesignSystemTests 守着。
public sealed record Palette
{
    public Color Surface { get; init; }
    public Color SurfaceAlt { get; init; }
    public Color Text { get; init; }
    public Color TextDim { get; init; }
    public Color Accent { get; init; }
    public Color Rim { get; init; }
    public Color Up { get; init; }   // A股习惯：涨=红；兼作 critical 级别色
    public Color Down { get; init; } // 跌=绿

    // 读数 ≥70% 用 Warning，≥90% 用 Up（critical）；Track 是表盘/进度条的轨道色。
    public Color Warning { get; init; } = Color.FromRgb(0xF0, 0xA2, 0x4A);
    public Color TextTertiary { get; init; } = Color.FromRgb(0x8E, 0x9A, 0xB4);
    public Color Track { get; init; } = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
    /// <summary>发丝线：分组之间、卡片和按钮的 1px 边。带透明度，叠在玻璃上随底色走。</summary>
    public Color Hairline { get; init; } = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);

    /// <summary>表面是否为深色（封面取色的亮度钳制基准，BrimDeck PanelPalette.IsDark 同款）。</summary>
    public bool IsDark => (Surface.R * 299 + Surface.G * 587 + Surface.B * 114) / 1000 < 140;

    /// <summary>像素主题：直角（3px 微圆角）。</summary>
    public bool SquareCorners { get; init; }

    /// <summary>默认深色 —— "深空电青"配方：近黑的中性底 + 薄荷青，和产品图标里竖条根部的绿青光晕同色系。</summary>
    public static Palette Dark => new()
    {
        Surface = Color.FromRgb(0x08, 0x09, 0x0D),
        SurfaceAlt = Color.FromRgb(0x12, 0x14, 0x1A),
        Text = Color.FromRgb(0xF2, 0xF3, 0xF5),
        TextDim = Color.FromRgb(0xA9, 0xAE, 0xB9),
        TextTertiary = Color.FromRgb(0x7C, 0x82, 0x8F),
        Accent = Color.FromRgb(0x2D, 0xD4, 0xBF),
        Rim = Color.FromRgb(0x24, 0x27, 0x2F),
        Hairline = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF),
        Track = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF),
        Up = Color.FromRgb(0xFF, 0x5A, 0x5A),
        Down = Color.FromRgb(0x3C, 0xBE, 0x78),
    };

    /// <summary>浅色：暗色科技的那套（近黑底、辉光、网格）不适用，走安静的浅色仪表。</summary>
    public static Palette Light => new()
    {
        Surface = Color.FromRgb(0xF2, 0xF3, 0xF5),
        SurfaceAlt = Color.FromRgb(0xE4, 0xE6, 0xEB),
        Text = Color.FromRgb(0x1A, 0x1C, 0x22),
        TextDim = Color.FromRgb(0x57, 0x5C, 0x68),
        TextTertiary = Color.FromRgb(0x66, 0x6B, 0x7A),
        Accent = Color.FromRgb(0x2A, 0x63, 0xE4),
        Rim = Color.FromRgb(0xC5, 0xC8, 0xD0),
        Hairline = Color.FromArgb(0x1F, 0x00, 0x00, 0x00),
        Track = Color.FromArgb(0x1A, 0x00, 0x00, 0x00),
        Warning = Color.FromRgb(0x99, 0x55, 0x05),
        Up = Color.FromRgb(0xCC, 0x32, 0x26),
        Down = Color.FromRgb(0x0E, 0x7A, 0x46),
    };

    /// <summary>One Dark Pro 风格：蓝灰底 + 它自己的青色（蓝留给 GitHub Dark，否则两套分不出来）。</summary>
    public static Palette OneDark => new()
    {
        Surface = Color.FromRgb(0x1A, 0x1E, 0x26),
        SurfaceAlt = Color.FromRgb(0x25, 0x2A, 0x34),
        Text = Color.FromRgb(0xE0, 0xE3, 0xE8),
        TextDim = Color.FromRgb(0xB4, 0xBA, 0xC6),
        TextTertiary = Color.FromRgb(0x8A, 0x93, 0xA3),
        Accent = Color.FromRgb(0x56, 0xC1, 0xD4),
        Rim = Color.FromRgb(0x3E, 0x44, 0x50),
        Up = Color.FromRgb(0xE8, 0x60, 0x6A),
        Down = Color.FromRgb(0x98, 0xC3, 0x79),
        Warning = Color.FromRgb(0xE5, 0xB5, 0x67),
    };

    /// <summary>GitHub Dark 风格：近黑底 + Primer 蓝。</summary>
    public static Palette GitHubDark => new()
    {
        Surface = Color.FromRgb(0x0A, 0x0D, 0x12),
        SurfaceAlt = Color.FromRgb(0x15, 0x1B, 0x23),
        Text = Color.FromRgb(0xE6, 0xED, 0xF3),
        TextDim = Color.FromRgb(0xA4, 0xAC, 0xB8),
        TextTertiary = Color.FromRgb(0x86, 0x8F, 0x9C),
        Accent = Color.FromRgb(0x58, 0xA6, 0xFF),
        Rim = Color.FromRgb(0x26, 0x2C, 0x36),
        Up = Color.FromRgb(0xF8, 0x51, 0x49),
        Down = Color.FromRgb(0x3F, 0xB9, 0x50),
        Warning = Color.FromRgb(0xE3, 0xB3, 0x41),
    };

    /// <summary>Dracula 风格：深紫底 + 它的淡紫。</summary>
    public static Palette Dracula => new()
    {
        Surface = Color.FromRgb(0x16, 0x17, 0x23),
        SurfaceAlt = Color.FromRgb(0x22, 0x23, 0x34),
        Text = Color.FromRgb(0xF6, 0xF6, 0xF0),
        TextDim = Color.FromRgb(0xB2, 0xB6, 0xD4),
        TextTertiary = Color.FromRgb(0x87, 0x8D, 0xB6),
        Accent = Color.FromRgb(0xBD, 0x93, 0xF9),
        Rim = Color.FromRgb(0x36, 0x38, 0x50),
        Up = Color.FromRgb(0xFF, 0x55, 0x55),
        Down = Color.FromRgb(0x50, 0xFA, 0x7B),
        Warning = Color.FromRgb(0xFF, 0xB8, 0x6C),
    };

    /// <summary>Night Owl 风格：深海军蓝底 + 天蓝。</summary>
    public static Palette NightOwl => new()
    {
        Surface = Color.FromRgb(0x02, 0x1A, 0x2E),
        SurfaceAlt = Color.FromRgb(0x0A, 0x27, 0x42),
        Text = Color.FromRgb(0xD6, 0xDE, 0xEB),
        TextDim = Color.FromRgb(0xA0, 0xB7, 0xD3),
        TextTertiary = Color.FromRgb(0x78, 0x93, 0xB5),
        Accent = Color.FromRgb(0x7E, 0xC6, 0xFF),
        Rim = Color.FromRgb(0x1E, 0x3E, 0x5E),
        Up = Color.FromRgb(0xEF, 0x53, 0x50),
        Down = Color.FromRgb(0x2F, 0xD6, 0x5A),
        Warning = Color.FromRgb(0xFF, 0xCB, 0x6B),
    };

    /// <summary>海岛假日：暖白字 + 深蓝灰底 + 珊瑚色（日落压在海面上）。</summary>
    public static Palette Island => new()
    {
        Surface = Color.FromRgb(0x16, 0x20, 0x29),
        SurfaceAlt = Color.FromRgb(0x21, 0x2E, 0x39),
        Text = Color.FromRgb(0xF3, 0xED, 0xE3),
        TextDim = Color.FromRgb(0xB0, 0xBC, 0xC4),
        TextTertiary = Color.FromRgb(0x86, 0x95, 0x9F),
        Accent = Color.FromRgb(0xFF, 0x9E, 0x7A),
        Rim = Color.FromRgb(0x31, 0x43, 0x4F),
        Up = Color.FromRgb(0xF0, 0x56, 0x4A),
        Down = Color.FromRgb(0x3C, 0xC9, 0xA7),
        Warning = Color.FromRgb(0xF2, 0xB8, 0x4B),
    };

    /// <summary>赛博朋克：边缘行者（近黑底 + 柠檬黄）。</summary>
    public static Palette Cyberpunk => new()
    {
        Surface = Color.FromRgb(0x07, 0x0A, 0x10),
        SurfaceAlt = Color.FromRgb(0x10, 0x15, 0x1E),
        Text = Color.FromRgb(0xF2, 0xF3, 0xF5),
        TextDim = Color.FromRgb(0xA6, 0xAD, 0xB9),
        TextTertiary = Color.FromRgb(0x74, 0x7C, 0x89),
        Accent = Color.FromRgb(0xFB, 0xD5, 0x2C),
        Rim = Color.FromRgb(0x22, 0x2F, 0x45),
        Up = Color.FromRgb(0xFF, 0x2E, 0x63),
        Down = Color.FromRgb(0x00, 0xE5, 0xFF),
        Warning = Color.FromRgb(0xFF, 0x8A, 0x3D),
    };

    /// <summary>像素方块（Nothing OS / 点阵屏风格：纯黑底 + 琥珀单色 + 直角）。</summary>
    public static Palette Pixel => new()
    {
        Surface = Color.FromRgb(0x00, 0x00, 0x00),
        SurfaceAlt = Color.FromRgb(0x14, 0x14, 0x14),
        Text = Color.FromRgb(0xFF, 0xB0, 0x00),
        TextDim = Color.FromRgb(0xC9, 0x8A, 0x00),
        TextTertiary = Color.FromRgb(0xA8, 0x72, 0x00),
        Accent = Color.FromRgb(0xFF, 0xB0, 0x00),
        Rim = Color.FromRgb(0x55, 0x3A, 0x00),
        Hairline = Color.FromArgb(0x40, 0xFF, 0xB0, 0x00),
        Track = Color.FromArgb(0x1F, 0xFF, 0xB0, 0x00),
        Warning = Color.FromRgb(0xFF, 0x77, 0x00),
        Up = Color.FromRgb(0xFF, 0x44, 0x00),
        Down = Color.FromRgb(0x00, 0xFF, 0x88),
        SquareCorners = true,
    };

    /// <summary>按名称取主题（托盘菜单/设置用）。</summary>
    public static Palette? ByName(string name) => name switch
    {
        "Dark" => Dark, "Light" => Light, "OneDark" => OneDark,
        "GitHubDark" => GitHubDark, "Dracula" => Dracula, "NightOwl" => NightOwl,
        "Island" => Island, "Cyberpunk" => Cyberpunk, "Pixel" => Pixel,
        _ => null,
    };

    public SolidColorBrush Brush(Color c) => new(c);
}

public enum ThemeMode { Dark, Light, Auto }
