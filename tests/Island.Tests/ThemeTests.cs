using System.Windows.Media;
using Island.App.Themes;

namespace Island.Tests;

/// <summary>主题调色板用例：按名查找的命中/未命中行为与色板属性。</summary>
internal static class ThemeTests
{
    /// <summary>设置中心里可选的全部内置主题名。</summary>
    private static readonly string[] Builtins =
        { "Dark", "Light", "OneDark", "GitHubDark", "Dracula", "NightOwl", "Island", "Cyberpunk", "Pixel" };

    public static void Run(Action<string, bool> check)
    {
        check("ByName 命中已知主题",
            Palette.ByName("Dracula") == Palette.Dracula && Palette.ByName("Pixel") == Palette.Pixel);
        check("ByName 未命中返回 null",
            Palette.ByName("NoSuchTheme") == null && Palette.ByName("") == null);
        check("ByName 区分大小写（dark 不等于 Dark）",
            Palette.ByName("dark") == null && Palette.ByName("Dark") != null);
        check("全部内置主题都能按名查到",
            Builtins.All(n => Palette.ByName(n) != null));
        check("深色主题判定为深色、浅色主题判定为浅色",
            Palette.Dark.IsDark && Palette.Light.IsDark == false && Palette.Dracula.IsDark);
        check("像素主题是直角，其余内置主题不是",
            Palette.Pixel.SquareCorners && !Palette.Dark.SquareCorners && !Palette.Light.SquareCorners);
        check("每个内置主题都有主色与文本色",
            Builtins.All(n =>
            {
                var p = Palette.ByName(n)!;
                return p.Accent.A > 0 && p.Text.A > 0 && p.Surface.A > 0 && p.Rim.A > 0;
            }));
        var brush = Palette.Dark.Brush(Color.FromRgb(0x12, 0x34, 0x56));
        check("Brush 返回对应颜色的纯色画刷",
            brush.Color == Color.FromRgb(0x12, 0x34, 0x56) && brush.Opacity == 1);
        check("主题色板带分级色（Warning/Track）",
            Palette.Dark.Warning.A > 0 && Palette.Dark.Track.A > 0 && Palette.Light.Warning.A > 0);
    }
}
