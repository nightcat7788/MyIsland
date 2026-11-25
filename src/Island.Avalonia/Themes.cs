using Avalonia.Media;

namespace Island.App.Themes;

/// <summary>
/// 语义色 token（2026-11-24 重做，参照 Apple 系统色 + ui-aesthetics-skill 规范）。
///
/// 设计原则（依据 references/color-system.md 与 depth-lighting-system.md）：
/// 1. **中性色先定**：每套主题共享同一套中性色阶结构（背景 / 表面 / 浮起层 / 文字三级 / 分隔线），
///    主题之间只换"个性色"，不再各造一套互相不搭的中性色（原版赛博朋克纯黑、海岛暖沙、Pixel 琥珀，
///    中性色在主题间漂移过大 = Muddy Premium Palette）。
/// 2. **强调色单一**：一个主强调色（默认 macOS 蓝），只用于选中态、焦点、关键数据。
///    读数数值走中性色文字，不再一个读数一个颜色（原版 CPU 蓝/内存青/磁盘紫/温度全绿 = 彩虹数值）。
/// 3. **语义色克制**：温度按等级、涨跌按方向、成功/警告/错误用成熟色而非原 traffic-light 色。
/// 4. **材质分层**：深色下用亮度阶建立层级（Elevated 比 Surface 亮），浅色下用极轻色调差 + 极淡描边。
///    描边只用于真正需要分隔的地方（Hairline），不做"每个卡片一圈框"。
/// </summary>
public sealed record Palette
{
    // ---- 表面层级（深色下靠亮度阶；浅色下靠极轻色调差）----
    /// <summary>窗口底色（最底层）。</summary>
    public Color Surface { get; init; }
    /// <summary>卡片/浮起层底色（比 Surface 略亮，形成层级）。</summary>
    public Color Elevated { get; init; }
    /// <summary>控件内嵌填充（进度槽、输入框底）。</summary>
    public Color Fill { get; init; }
    /// <summary>次级容器（分组内的小块）。</summary>
    public Color SurfaceAlt { get; init; }

    // ---- 文字三级 ----
    public Color Text { get; init; }
    public Color TextDim { get; init; }
    public Color TextTertiary { get; init; }

    // ---- 线条 ----
    /// <summary>细分隔线（比 Rim 更淡，用于卡片内分隔）。</summary>
    public Color Hairline { get; init; }
    /// <summary>卡片描边（浅色主题才明显，深色主题近乎不可见）。</summary>
    public Color Rim { get; init; }
    /// <summary>进度条未填充部分。</summary>
    public Color Track { get; init; }

    // ---- 强调与语义 ----
    /// <summary>主强调色（选中态、焦点、关键数据）。</summary>
    public Color Accent { get; init; }
    /// <summary>选中态的柔和底（页签胶囊等）。</summary>
    public Color Selection { get; init; }
    public Color Warning { get; init; }
    /// <summary>危险/上升（对齐中国股市：红涨）。</summary>
    public Color Up { get; init; }
    /// <summary>正常/下降（绿跌）。</summary>
    public Color Down { get; init; }

    public bool IsDark => (Surface.R * 299 + Surface.G * 587 + Surface.B * 114) / 1000 < 140;

    // =====================================================================
    // 主题定义
    // 每套主题都从同一套"结构"出发，只改个性色 —— 保证跨主题切换时层级关系一致。
    // =====================================================================

    /// <summary>
    /// 默认深色（Apple 风格）：接近 macOS 窗口底色的冷中性，强调色为系统蓝。
    /// 表面层级：#1C1C1E → #2C2C2E → #3A3A3C
    /// </summary>
    public static Palette Dark => new()
    {
        Surface = Color.FromRgb(0x1C, 0x1C, 0x1E),
        Elevated = Color.FromRgb(0x2A, 0x2A, 0x2D),
        Fill = Color.FromRgb(0x3A, 0x3A, 0x3C),
        SurfaceAlt = Color.FromRgb(0x25, 0x25, 0x27),
        Text = Color.FromRgb(0xF2, 0xF2, 0xF7),
        TextDim = Color.FromRgb(0xA3, 0xA3, 0xAA),
        TextTertiary = Color.FromRgb(0x73, 0x73, 0x7A),
        Hairline = Color.FromRgb(0x45, 0x45, 0x49),
        Rim = Color.FromRgb(0x38, 0x38, 0x3C),
        Track = Color.FromRgb(0x3A, 0x3A, 0x3C),
        Accent = Color.FromRgb(0x0A, 0x84, 0xFF),
        Selection = Color.FromRgb(0x0A, 0x84, 0xFF),
        Warning = Color.FromRgb(0xFF, 0x9F, 0x0A),
        Up = Color.FromRgb(0xFF, 0x45, 0x50),
        Down = Color.FromRgb(0x30, 0xD1, 0x58),
    };

    /// <summary>
    /// 浅色（Apple 风格）：macOS 浅色窗口的暖白中性，几乎不用描边，靠极轻色调差分层。
    /// </summary>
    public static Palette Light => new()
    {
        Surface = Color.FromRgb(0xF2, 0xF2, 0xF5),
        Elevated = Color.FromRgb(0xFF, 0xFF, 0xFF),
        Fill = Color.FromRgb(0xE8, 0xE8, 0xED),
        SurfaceAlt = Color.FromRgb(0xF7, 0xF7, 0xFA),
        Text = Color.FromRgb(0x1C, 0x1C, 0x1E),
        TextDim = Color.FromRgb(0x6E, 0x6E, 0x73),
        TextTertiary = Color.FromRgb(0x9A, 0x9A, 0xA0),
        Hairline = Color.FromRgb(0xDE, 0xDE, 0xE3),
        Rim = Color.FromRgb(0xE4, 0xE4, 0xE9),
        Track = Color.FromRgb(0xE3, 0xE3, 0xE8),
        Accent = Color.FromRgb(0x0A, 0x84, 0xFF),
        Selection = Color.FromRgb(0x0A, 0x84, 0xFF),
        Warning = Color.FromRgb(0xC9, 0x7A, 0x00),
        Up = Color.FromRgb(0xD7, 0x3A, 0x3A),
        Down = Color.FromRgb(0x1E, 0x9E, 0x5A),
    };

    /// <summary>One Dark Pro：保持其标志性的蓝紫底色，但中性色阶重排成与其它主题一致的结构。</summary>
    public static Palette OneDark => new()
    {
        Surface = Color.FromRgb(0x23, 0x27, 0x2E),
        Elevated = Color.FromRgb(0x2E, 0x33, 0x3C),
        Fill = Color.FromRgb(0x39, 0x3F, 0x49),
        SurfaceAlt = Color.FromRgb(0x27, 0x2C, 0x34),
        Text = Color.FromRgb(0xDC, 0xDF, 0xE4),
        TextDim = Color.FromRgb(0x9D, 0xA5, 0xB4),
        TextTertiary = Color.FromRgb(0x71, 0x7C, 0x8F),
        Hairline = Color.FromRgb(0x3D, 0x44, 0x4F),
        Rim = Color.FromRgb(0x33, 0x39, 0x43),
        Track = Color.FromRgb(0x39, 0x3F, 0x49),
        Accent = Color.FromRgb(0x61, 0xAF, 0xEF),
        Selection = Color.FromRgb(0x61, 0xAF, 0xEF),
        Warning = Color.FromRgb(0xE5, 0xC0, 0x7B),
        Up = Color.FromRgb(0xE0, 0x6C, 0x75),
        Down = Color.FromRgb(0x98, 0xC3, 0x79),
    };

    /// <summary>GitHub Dark：偏冷的中性 + GitHub 蓝。</summary>
    public static Palette GitHubDark => new()
    {
        Surface = Color.FromRgb(0x0D, 0x11, 0x17),
        Elevated = Color.FromRgb(0x1C, 0x23, 0x2C),
        Fill = Color.FromRgb(0x21, 0x26, 0x2D),
        SurfaceAlt = Color.FromRgb(0x11, 0x16, 0x1D),
        Text = Color.FromRgb(0xE6, 0xED, 0xF3),
        TextDim = Color.FromRgb(0x91, 0x9C, 0xA8),
        TextTertiary = Color.FromRgb(0x6E, 0x76, 0x81),
        Hairline = Color.FromRgb(0x2A, 0x31, 0x3A),
        Rim = Color.FromRgb(0x21, 0x27, 0x2E),
        Track = Color.FromRgb(0x21, 0x27, 0x2E),
        Accent = Color.FromRgb(0x2F, 0x81, 0xF7),
        Selection = Color.FromRgb(0x2F, 0x81, 0xF7),
        Warning = Color.FromRgb(0xD2, 0xA8, 0x5C),
        Up = Color.FromRgb(0xF8, 0x51, 0x49),
        Down = Color.FromRgb(0x3F, 0xB9, 0x50),
    };

    /// <summary>Dracula：紫调底色 + 其标志性的粉紫强调。</summary>
    public static Palette Dracula => new()
    {
        Surface = Color.FromRgb(0x23, 0x25, 0x2E),
        Elevated = Color.FromRgb(0x31, 0x34, 0x40),
        Fill = Color.FromRgb(0x39, 0x3D, 0x4A),
        SurfaceAlt = Color.FromRgb(0x27, 0x2A, 0x33),
        Text = Color.FromRgb(0xF8, 0xF8, 0xF2),
        TextDim = Color.FromRgb(0xA8, 0xAB, 0xB8),
        TextTertiary = Color.FromRgb(0x7A, 0x7D, 0x8A),
        Hairline = Color.FromRgb(0x45, 0x49, 0x58),
        Rim = Color.FromRgb(0x38, 0x3C, 0x47),
        Track = Color.FromRgb(0x39, 0x3D, 0x4A),
        Accent = Color.FromRgb(0xBD, 0x93, 0xF9),
        Selection = Color.FromRgb(0xBD, 0x93, 0xF9),
        Warning = Color.FromRgb(0xFF, 0xB8, 0x6C),
        Up = Color.FromRgb(0xFF, 0x55, 0x55),
        Down = Color.FromRgb(0x50, 0xFA, 0x7B),
    };

    /// <summary>Night Owl：深蓝底 + 其标志性的天蓝强调。</summary>
    public static Palette NightOwl => new()
    {
        Surface = Color.FromRgb(0x01, 0x18, 0x2A),
        Elevated = Color.FromRgb(0x0D, 0x28, 0x40),
        Fill = Color.FromRgb(0x10, 0x2E, 0x48),
        SurfaceAlt = Color.FromRgb(0x05, 0x1D, 0x31),
        Text = Color.FromRgb(0xD6, 0xDE, 0xEB),
        TextDim = Color.FromRgb(0x82, 0x95, 0xAD),
        TextTertiary = Color.FromRgb(0x5F, 0x7A, 0x99),
        Hairline = Color.FromRgb(0x1D, 0x3B, 0x54),
        Rim = Color.FromRgb(0x14, 0x30, 0x47),
        Track = Color.FromRgb(0x10, 0x2E, 0x48),
        Accent = Color.FromRgb(0x7E, 0xC6, 0xFF),
        Selection = Color.FromRgb(0x7E, 0xC6, 0xFF),
        Warning = Color.FromRgb(0xE6, 0xB8, 0x62),
        Up = Color.FromRgb(0xEF, 0x53, 0x50),
        Down = Color.FromRgb(0x27, 0xC9, 0x3F),
    };

    /// <summary>
    /// 海岛假日·白天：清爽的浅青灰底 + 海蓝强调。
    /// （原版是暖沙 #F4ECDD + 青绿强调——暖米白作"高级感"是规范明令避免的 Muddy Premium 做法）
    /// </summary>
    public static Palette IslandDay => new()
    {
        Surface = Color.FromRgb(0xEE, 0xF3, 0xF6),
        Elevated = Color.FromRgb(0xFF, 0xFF, 0xFF),
        Fill = Color.FromRgb(0xDD, 0xE7, 0xEC),
        SurfaceAlt = Color.FromRgb(0xF5, 0xF9, 0xFB),
        Text = Color.FromRgb(0x16, 0x2A, 0x33),
        TextDim = Color.FromRgb(0x5A, 0x71, 0x7C),
        TextTertiary = Color.FromRgb(0x8A, 0x9C, 0xA6),
        Hairline = Color.FromRgb(0xD3, 0xDF, 0xE5),
        Rim = Color.FromRgb(0xDA, 0xE4, 0xE9),
        Track = Color.FromRgb(0xDD, 0xE7, 0xEC),
        Accent = Color.FromRgb(0x0B, 0x7A, 0x8C),
        Selection = Color.FromRgb(0x0B, 0x7A, 0x8C),
        Warning = Color.FromRgb(0xB8, 0x74, 0x00),
        Up = Color.FromRgb(0xC0, 0x39, 0x39),
        Down = Color.FromRgb(0x17, 0x8C, 0x55),
    };

    /// <summary>海岛假日·夜晚：深青灰底 + 与白天同色系的强调（继承白天的强调色，只换表面与文字）。</summary>
    public static Palette IslandNight => IslandDay with
    {
        Surface = Color.FromRgb(0x14, 0x1E, 0x24),
        Elevated = Color.FromRgb(0x22, 0x32, 0x3B),
        Fill = Color.FromRgb(0x2A, 0x3B, 0x44),
        SurfaceAlt = Color.FromRgb(0x18, 0x24, 0x2B),
        Text = Color.FromRgb(0xE8, 0xF1, 0xF5),
        TextDim = Color.FromRgb(0x9A, 0xB0, 0xBA),
        TextTertiary = Color.FromRgb(0x6C, 0x83, 0x8D),
        Hairline = Color.FromRgb(0x32, 0x45, 0x50),
        Rim = Color.FromRgb(0x28, 0x3A, 0x44),
        Track = Color.FromRgb(0x2A, 0x3B, 0x44),
    };

    /// <summary>赛博朋克：边缘行者 —— 近黑底 + 电光青强调（保留其个性，但中性色阶重排）。</summary>
    public static Palette Cyberpunk => new()
    {
        Surface = Color.FromRgb(0x08, 0x0A, 0x0F),
        Elevated = Color.FromRgb(0x19, 0x1F, 0x28),
        Fill = Color.FromRgb(0x1B, 0x22, 0x2C),
        SurfaceAlt = Color.FromRgb(0x0C, 0x10, 0x16),
        Text = Color.FromRgb(0xE8, 0xEC, 0xF2),
        TextDim = Color.FromRgb(0x94, 0x9E, 0xAD),
        TextTertiary = Color.FromRgb(0x6A, 0x74, 0x84),
        Hairline = Color.FromRgb(0x24, 0x2E, 0x3A),
        Rim = Color.FromRgb(0x1A, 0x22, 0x2C),
        Track = Color.FromRgb(0x1B, 0x22, 0x2C),
        Accent = Color.FromRgb(0x00, 0xE5, 0xFF),
        Selection = Color.FromRgb(0x00, 0xE5, 0xFF),
        Warning = Color.FromRgb(0xFF, 0xC5, 0x3C),
        Up = Color.FromRgb(0xFF, 0x2E, 0x63),
        Down = Color.FromRgb(0x00, 0xE5, 0xFF),
    };

    /// <summary>像素方块：纯黑底 + 琥珀单色（Nothing OS 风格）。保留其单色个性。</summary>
    public static Palette Pixel => new()
    {
        Surface = Color.FromRgb(0x00, 0x00, 0x00),
        Elevated = Color.FromRgb(0x16, 0x16, 0x16),
        Fill = Color.FromRgb(0x1C, 0x1C, 0x1C),
        SurfaceAlt = Color.FromRgb(0x08, 0x08, 0x08),
        Text = Color.FromRgb(0xFF, 0xB3, 0x00),
        TextDim = Color.FromRgb(0xC4, 0x8A, 0x1A),
        TextTertiary = Color.FromRgb(0x8A, 0x63, 0x18),
        Hairline = Color.FromRgb(0x3A, 0x2C, 0x0A),
        Rim = Color.FromRgb(0x24, 0x1C, 0x06),
        Track = Color.FromRgb(0x1C, 0x1C, 0x1C),
        Accent = Color.FromRgb(0xFF, 0xB3, 0x00),
        Selection = Color.FromRgb(0xFF, 0xB3, 0x00),
        Warning = Color.FromRgb(0xFF, 0x7A, 0x00),
        Up = Color.FromRgb(0xFF, 0x44, 0x00),
        Down = Color.FromRgb(0x00, 0xE5, 0x5A),
    };

    /// <summary>海岛假日日夜切换时刻（本地时间）。</summary>
    public static readonly TimeSpan IslandDayStart = new(6, 30, 0);
    public static readonly TimeSpan IslandNightStart = new(18, 30, 0);

    /// <summary>按本地时间选海岛假日的日版或夜版。06:30–18:30 用日版，其余用夜版。</summary>
    public static Palette IslandAt(DateTime local) => IsIslandDaytime(local) ? IslandDay : IslandNight;

    public static bool IsIslandDaytime(DateTime local)
    {
        var t = local.TimeOfDay;
        return t >= IslandDayStart && t < IslandNightStart;
    }

    /// <summary>下次日夜切换的本地时刻（给排程器用）。</summary>
    public static DateTime NextIslandSwitch(DateTime local)
    {
        var today = local.Date;
        var day = today + IslandDayStart; var night = today + IslandNightStart;
        if (local < day) return day;
        if (local < night) return night;
        return day.AddDays(1);
    }

    /// <summary>当前时刻的海岛主题（默认视图用）。</summary>
    public static Palette Island => IslandAt(DateTime.Now);

    /// <summary>按名称取主题（托盘菜单用）。</summary>
    public static Palette? ByName(string name) => name switch
    {
        "Dark" => Dark, "Light" => Light, "OneDark" => OneDark,
        "GitHubDark" => GitHubDark, "Dracula" => Dracula, "NightOwl" => NightOwl,
        "Island" => Island, "Cyberpunk" => Cyberpunk, "Pixel" => Pixel,
        _ => null,
    };
}
