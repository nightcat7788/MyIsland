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
    // 读数 ≥70% 用 Warning，≥90% 用 Up（critical）；Track 是表盘/进度条的轨道色；Hairline 是胶囊分组细分隔线。
    public Color Warning { get; init; } = Color.FromRgb(0xFF, 0x9F, 0x0A);
    // 默认值对 6 套深色主题通用：取值按 WCAG 对比度 ≥4.5:1 校准（对最浅的深色底 #2A323E 为 4.58:1）。
    // 2026-11-23 复审：原值 #6B7A8F 只有 2.96~4.43:1，9.5~11px 的次级文字几乎糊在背景里。
    public Color TextTertiary { get; init; } = Color.FromRgb(0x8E, 0x9A, 0xB4);
    public Color Track { get; init; } = Color.FromRgb(0x3A, 0x3A, 0x3C);
    public Color Hairline { get; init; } = Color.FromRgb(0x45, 0x45, 0x49);

    /// <summary>表面是否为深色（封面取色的亮度钳制基准，BrimDeck PanelPalette.IsDark 同款）。</summary>
    public bool IsDark => (Surface.R * 299 + Surface.G * 587 + Surface.B * 114) / 1000 < 140;

    /// <summary>像素主题：直角（3px 微圆角）。</summary>
    public bool SquareCorners { get; init; }

    /// <summary>默认深色（与 macOS 版 Apple 风格中性色阶一致：#1C1C1E 胶囊底 + 系统蓝强调）。</summary>
    public static Palette Dark => new()
    {
        Surface = Color.FromRgb(0x1C, 0x1C, 0x1E),
        SurfaceAlt = Color.FromRgb(0x25, 0x25, 0x27),
        Text = Color.FromRgb(0xF2, 0xF2, 0xF7),
        TextDim = Color.FromRgb(0xA3, 0xA3, 0xAA),
        TextTertiary = Color.FromRgb(0x8E, 0x9A, 0xB4),
        Hairline = Color.FromRgb(0x45, 0x45, 0x49),
        Accent = Color.FromRgb(0x0A, 0x84, 0xFF),
        Rim = Color.FromRgb(0x38, 0x38, 0x3C),
        Track = Color.FromRgb(0x3A, 0x3A, 0x3C),
        Warning = Color.FromRgb(0xFF, 0x9F, 0x0A),
        Up = Color.FromRgb(0xFF, 0x45, 0x50),
        Down = Color.FromRgb(0x30, 0xD1, 0x58),
    };

    public static Palette Light => new()
    {
        Surface = Color.FromRgb(0xF2, 0xF2, 0xF5),
        SurfaceAlt = Color.FromRgb(0xE8, 0xE8, 0xED),
        Text = Color.FromRgb(0x1C, 0x1C, 0x1E),
        TextDim = Color.FromRgb(0x6E, 0x6E, 0x73),
        TextTertiary = Color.FromRgb(0x6B, 0x72, 0x80),
        Hairline = Color.FromRgb(0xDE, 0xDE, 0xE3),
        Accent = Color.FromRgb(0x0A, 0x84, 0xFF),
        Rim = Color.FromRgb(0xD8, 0xDC, 0xE6),
        Track = Color.FromRgb(0xDF, 0xE1, 0xE8),
        Warning = Color.FromRgb(0xD9, 0x8A, 0x2B),
        Up = Color.FromRgb(0xD6, 0x30, 0x30),
        Down = Color.FromRgb(0x1E, 0xA0, 0x5A),
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

    /// <summary>海岛假日·白天（与 macOS 版 IslandDay 一致：清爽浅青灰 + 海蓝强调）。</summary>
    public static Palette IslandDay => new()
    {
        Surface = Color.FromRgb(0xEE, 0xF3, 0xF6),
        SurfaceAlt = Color.FromRgb(0xDD, 0xE7, 0xEC),
        Text = Color.FromRgb(0x16, 0x2A, 0x33),
        TextDim = Color.FromRgb(0x5A, 0x71, 0x7C),
        TextTertiary = Color.FromRgb(0x6D, 0x82, 0x8C),
        Hairline = Color.FromRgb(0xD3, 0xDF, 0xE5),
        Rim = Color.FromRgb(0xDA, 0xE4, 0xE9),
        Track = Color.FromRgb(0xDD, 0xE7, 0xEC),
        Accent = Color.FromRgb(0x0B, 0x7A, 0x8C),
        Warning = Color.FromRgb(0xB8, 0x74, 0x00),
        Up = Color.FromRgb(0xC0, 0x39, 0x39),
        Down = Color.FromRgb(0x17, 0x8C, 0x55),
    };

    /// <summary>海岛假日·夜晚（与 macOS 版 IslandNight 一致：深青灰底 + 同色系强调）。</summary>
    public static Palette IslandNight => IslandDay with
    {
        Surface = Color.FromRgb(0x14, 0x1E, 0x24),
        SurfaceAlt = Color.FromRgb(0x22, 0x32, 0x3B),
        Text = Color.FromRgb(0xE8, 0xF1, 0xF5),
        TextDim = Color.FromRgb(0x9A, 0xB0, 0xBA),
        TextTertiary = Color.FromRgb(0x78, 0x90, 0x9B),
        Hairline = Color.FromRgb(0x32, 0x45, 0x50),
        Rim = Color.FromRgb(0x28, 0x3A, 0x44),
        Track = Color.FromRgb(0x2A, 0x3B, 0x44),
        Accent = Color.FromRgb(0x27, 0xB5, 0xB5),
        Warning = Color.FromRgb(0xFF, 0x9F, 0x0A),
        Up = Color.FromRgb(0xFF, 0x6B, 0x6B),
        Down = Color.FromRgb(0x4E, 0xC9, 0xB0),
    };

    /// <summary>海岛假日日夜切换时刻（本地时间，与 macOS 版一致：06:30–18:30 白天）。</summary>
    public static readonly TimeSpan IslandDayStart = new(6, 30, 0);
    public static readonly TimeSpan IslandNightStart = new(18, 30, 0);

    public static bool IsIslandDaytime(DateTime local)
    {
        var t = local.TimeOfDay;
        return t >= IslandDayStart && t < IslandNightStart;
    }

    public static Palette IslandAt(DateTime local) => IsIslandDaytime(local) ? IslandDay : IslandNight;

    public static DateTime NextIslandSwitch(DateTime local)
    {
        var today = local.Date;
        var day = today + IslandDayStart;
        var night = today + IslandNightStart;
        if (local < day) return day;
        if (local < night) return night;
        return day.AddDays(1);
    }

    /// <summary>海岛假日（默认跟随当前时段日/夜版）。</summary>
    public static Palette Island => IslandAt(DateTime.Now);

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
        Hairline = Color.FromRgb(0x40, 0x2C, 0x00),
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
        "Island" or "IslandAuto" => IslandAt(DateTime.Now),
        "IslandDay" => IslandDay, "IslandNight" => IslandNight,
        "Cyberpunk" => Cyberpunk, "Pixel" => Pixel,
        _ => null,
    };

    public SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

public enum ThemeMode { Dark, Light, Auto }
