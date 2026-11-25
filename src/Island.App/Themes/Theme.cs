using System.Windows.Media;

namespace Island.App.Themes;

/// <summary>语义颜色 token：UI 只引用语义，不写死具体色值。</summary>
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

    // BrimDeck 式分级/层次 token（PanelTheme 的 warning / textTertiary / ringTrack）：
    // 读数 ≥70% 用 Warning，≥90% 用 Up（critical）；Track 是表盘/进度条的轨道色。
    public Color Warning { get; init; } = Color.FromRgb(0xE8, 0xA2, 0x4A);
    // 默认值对 6 套深色主题通用：取值按 WCAG 对比度 ≥4.5:1 校准（对最浅的深色底 #2A323E 为 4.58:1）。
    // 2026-11-23 复审：原值 #6B7A8F 只有 2.96~4.43:1，9.5~11px 的次级文字几乎糊在背景里。
    public Color TextTertiary { get; init; } = Color.FromRgb(0x8E, 0x9A, 0xB4);
    public Color Track { get; init; } = Color.FromRgb(0x30, 0x36, 0x40);

    /// <summary>表面是否为深色（封面取色的亮度钳制基准，BrimDeck PanelPalette.IsDark 同款）。</summary>
    public bool IsDark => (Surface.R * 299 + Surface.G * 587 + Surface.B * 114) / 1000 < 140;

    /// <summary>像素主题：直角（3px 微圆角）。</summary>
    public bool SquareCorners { get; init; }

    public static Palette Dark => new()
    {
        Surface = Color.FromRgb(28, 30, 38),
        SurfaceAlt = Color.FromRgb(40, 43, 54),
        Text = Colors.White,
        TextDim = Color.FromRgb(160, 166, 182),
        TextTertiary = Color.FromRgb(0x8E, 0x9A, 0xB4),   // 3.54 → 5.88:1（对 #1C1E26）
        Accent = Color.FromRgb(90, 150, 255),
        Rim = Color.FromRgb(90, 96, 116),
        Track = Color.FromRgb(0x30, 0x36, 0x40),
        Up = Color.FromRgb(232, 74, 74),
        Down = Color.FromRgb(60, 190, 120),
    };

    public static Palette Light => new()
    {
        Surface = Color.FromRgb(246, 247, 250),
        SurfaceAlt = Color.FromRgb(230, 233, 240),
        Text = Color.FromRgb(30, 32, 40),
        TextDim = Color.FromRgb(102, 107, 122),           // 4.36 → 4.96:1（浅底要更深）
        TextTertiary = Color.FromRgb(0x6B, 0x72, 0x80),     // 2.97 → 4.51:1（浅底不能用深色主题的默认值）
        Accent = Color.FromRgb(40, 110, 230),
        Rim = Color.FromRgb(190, 195, 210),
        Track = Color.FromRgb(0xD8, 0xDC, 0xE6),
        Warning = Color.FromRgb(0xD9, 0x8A, 0x2B),
        Up = Color.FromRgb(214, 48, 48),
        Down = Color.FromRgb(30, 160, 90),
    };

    /// <summary>One Dark Pro 风格。</summary>
    public static Palette OneDark => new()
    {
        Surface = Color.FromRgb(0x28, 0x2C, 0x34),
        SurfaceAlt = Color.FromRgb(0x3E, 0x44, 0x51),
        Text = Color.FromRgb(0xDC, 0xDF, 0xE4),
        TextDim = Color.FromRgb(0x9C, 0x99, 0xA5),       // 4.41 → 5.01:1
        Accent = Color.FromRgb(0x61, 0xAF, 0xEF),
        Rim = Color.FromRgb(0x5C, 0x63, 0x70),
        Up = Color.FromRgb(0xE0, 0x6C, 0x75),
        Down = Color.FromRgb(0x98, 0xC3, 0x79),
    };

    /// <summary>GitHub Dark 风格。</summary>
    public static Palette GitHubDark => new()
    {
        Surface = Color.FromRgb(0x0D, 0x11, 0x17),
        SurfaceAlt = Color.FromRgb(0x21, 0x27, 0x2E),
        Text = Color.FromRgb(0xE6, 0xED, 0xF3),
        TextDim = Color.FromRgb(0x8B, 0x94, 0x9E),
        Accent = Color.FromRgb(0x2F, 0x81, 0xF7),
        Rim = Color.FromRgb(0x30, 0x36, 0x40),
        Up = Color.FromRgb(0xF8, 0x51, 0x49),
        Down = Color.FromRgb(0x3F, 0xB9, 0x50),
    };

    /// <summary>Dracula 风格。</summary>
    public static Palette Dracula => new()
    {
        Surface = Color.FromRgb(0x28, 0x2A, 0x36),
        SurfaceAlt = Color.FromRgb(0x34, 0x37, 0x47),
        Text = Color.FromRgb(0xF8, 0xF8, 0xF2),
        TextDim = Color.FromRgb(0x88, 0x94, 0xAE),       // 2.78 → 4.67:1（九套里最低的就是它）
        Accent = Color.FromRgb(0xBD, 0x93, 0xF9),
        Rim = Color.FromRgb(0x44, 0x47, 0x5A),
        Up = Color.FromRgb(0xFF, 0x55, 0x55),
        Down = Color.FromRgb(0x50, 0xFA, 0x7B),
    };

    /// <summary>Night Owl 风格。</summary>
    public static Palette NightOwl => new()
    {
        Surface = Color.FromRgb(0x01, 0x1F, 0x35),
        SurfaceAlt = Color.FromRgb(0x0B, 0x2C, 0x45),
        Text = Color.FromRgb(0xD6, 0xDE, 0xEB),
        TextDim = Color.FromRgb(0x7F, 0xA0, 0xC4),       // 3.79 → 6.19:1
        Accent = Color.FromRgb(0x7E, 0xC6, 0xFF),
        Rim = Color.FromRgb(0x2A, 0x4C, 0x6E),
        Up = Color.FromRgb(0xEF, 0x53, 0x50),
        Down = Color.FromRgb(0x27, 0xC9, 0x3F),
    };

    /// <summary>海岛假日（暖沙+海蓝）。</summary>
    public static Palette Island => new()
    {
        Surface = Color.FromRgb(0x2A, 0x32, 0x3E),
        SurfaceAlt = Color.FromRgb(0x39, 0x44, 0x54),
        Text = Color.FromRgb(0xF5, 0xE9, 0xD9),
        TextDim = Color.FromRgb(0x9A, 0xA5, 0xB5),
        Accent = Color.FromRgb(0x27, 0xB5, 0xB5),
        Rim = Color.FromRgb(0x4A, 0x56, 0x66),
        Up = Color.FromRgb(0xFF, 0x6B, 0x6B),
        Down = Color.FromRgb(0x4E, 0xC9, 0xB0),
    };

    /// <summary>赛博朋克：边缘行者（霓虹黄+青）。</summary>
    public static Palette Cyberpunk => new()
    {
        Surface = Color.FromRgb(0x0A, 0x0E, 0x14),
        SurfaceAlt = Color.FromRgb(0x15, 0x1B, 0x24),
        Text = Color.FromRgb(0xF3, 0xF3, 0xF3),
        TextDim = Color.FromRgb(0x7B, 0x96, 0xFF),       // 4.43 → 7.04:1
        Accent = Color.FromRgb(0xFB, 0xD5, 0x2C),
        Rim = Color.FromRgb(0x2A, 0x3B, 0x55),
        Up = Color.FromRgb(0xFF, 0x00, 0x3C),
        Down = Color.FromRgb(0x00, 0xF0, 0xFF),
    };

    /// <summary>像素方块（Nothing OS / 点阵屏风格：纯黑底 + 琥珀单色 + 直角）。</summary>
    public static Palette Pixel => new()
    {
        Surface = Color.FromRgb(0x00, 0x00, 0x00),
        SurfaceAlt = Color.FromRgb(0x14, 0x14, 0x14),
        Text = Color.FromRgb(0xFF, 0xB0, 0x00),
        TextDim = Color.FromRgb(0xB8, 0x7A, 0x00),
        TextTertiary = Color.FromRgb(0xB5, 0x80, 0x00),   // 3.10 → 6.05:1（琥珀色在纯黑上要更亮）
        Accent = Color.FromRgb(0xFF, 0xB0, 0x00),
        Rim = Color.FromRgb(0x55, 0x3A, 0x00),
        Track = Color.FromRgb(0x2A, 0x1E, 0x00),
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
