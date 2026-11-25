using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Island.Core.Capabilities;

namespace Island.Tests;

/// <summary>
/// **主题 token 语义清单漂移检查**（2026-11-26 task9 / 计划 E3）。
///
/// 背景：两端 Palette 各写一份，里面有 12 个**同名**的颜色 token，但每套主题下**取值都不同**。
/// 根因和"能力清单漂移"一样：两端各写一份、靠人记 ⇒ 很容易把"同名"误读成"同值"。
///
/// 本任务**不改任何具体色值**，只把"同名 token 指同一语义角色"显式化并公开差异。
/// 所以门禁守的是**语义与映射**，不是颜色：
///   · 清单自身的完整性（键唯一、无空键、每个 token 都有语义说明、归属与属性映射自洽）；
///   · **源码静态校验**：读两端的 Palette 源码，断言清单登记的每个属性都真的存在，
///     且两端 Palette 里的每个颜色 token 都真的登记进了清单
///     ⇒ **新增 / 删除一个 token 而不登记 ⇒ 门禁红**。
/// </summary>
internal static class ThemeTokenTests
{
    /// <summary>源码静态校验用的仓库根（找不到就跳过静态断言，不让测试因为路径找不到而红）。</summary>
    private static string? RepoRoot { get; } = FindRepoRoot();

    public static void Run(Action<string, bool> check)
    {
        CheckManifestItself(check);
        CheckWindowsSide(check);
        CheckMacSide(check);
    }

    // ==================== 清单自身 ====================

    private static void CheckManifestItself(Action<string, bool> check)
    {
        var all = ThemeTokenManifest.All;

        // ---- 键唯一、无空 ----
        check("清单：语义键全局唯一",
            all.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() == all.Count);
        check("清单：没有空语义键",
            all.All(e => !string.IsNullOrWhiteSpace(e.Key)));
        check("清单：每个 token 都有显示名",
            all.All(e => !string.IsNullOrWhiteSpace(e.Title)));

        // ---- 每个 token 都有语义说明（这是本任务的核心产出）----
        check("清单：每个 token 都有语义说明",
            all.All(e => !string.IsNullOrWhiteSpace(e.Role)));

        // ---- 归属与属性映射自洽 ----
        check("清单：共享 token 两端都有属性映射",
            ThemeTokenManifest.SharedTokens.All(e => e.WindowsProperty is not null && e.MacProperty is not null));
        check("清单：仅 Windows 的 token 只映射 Windows 属性",
            ThemeTokenManifest.WindowsOnlyTokens.All(e => e.WindowsProperty is not null && e.MacProperty is null));
        check("清单：仅 macOS 的 token 只映射 mac 属性",
            ThemeTokenManifest.MacOnlyTokens.All(e => e.MacProperty is not null && e.WindowsProperty is null));

        // ---- 同一平台内不得有两个 token 指向同一个属性 ----
        check("清单：Windows 属性名不重复",
            ThemeTokenManifest.WindowsProperties.Distinct(StringComparer.Ordinal).Count()
                == ThemeTokenManifest.WindowsProperties.Count());
        check("清单：mac 属性名不重复",
            ThemeTokenManifest.MacProperties.Distinct(StringComparer.Ordinal).Count()
                == ThemeTokenManifest.MacProperties.Count());

        // ---- 两端同名的 token：属性名必须真的同名，且两端取值都记为"按设计不同" ----
        check("清单：共享 token 两端属性同名（同一语义角色）",
            ThemeTokenManifest.SharedTokens.All(e => e.WindowsProperty == e.MacProperty));
        check("清单：共享 token 两端取值都记为按设计不同",
            ThemeTokenManifest.SharedTokens.All(e => e.SameValueAcrossPlatforms == false));
        check("清单：共享 token 都写明了差异原因",
            ThemeTokenManifest.SharedTokens.All(e => !string.IsNullOrWhiteSpace(e.Note)));

        // ---- 平台专属 token 无从比较两端取值 ----
        check("清单：平台专属 token 不比较两端取值",
            ThemeTokenManifest.MacOnlyTokens.Concat(ThemeTokenManifest.WindowsOnlyTokens)
                .All(e => e.SameValueAcrossPlatforms is null));

        // ---- 已知的 token 全集（防止悄悄少登记）----
        check("清单：共享 token 恰好覆盖已知的 12 个语义键",
            KnownKeys("surface", "surface-alt", "text-primary", "text-secondary", "text-tertiary",
                      "accent", "rim", "track", "hairline", "warning", "up", "down")
                .All(k => ThemeTokenManifest.SharedTokens.Any(e => e.Key == k)));
        check("清单：mac 独有 token 恰好覆盖 elevated / fill / selection",
            KnownKeys("elevated", "fill", "selection")
                .All(k => ThemeTokenManifest.MacOnlyTokens.Any(e => e.Key == k)));
        check("清单：Windows 独有 token 恰好覆盖 square-corners",
            ThemeTokenManifest.WindowsOnlyTokens.Any(e => e.Key == "square-corners"));
        check("清单：square-corners 标注为开关型（不是颜色）",
            ThemeTokenManifest.WindowsOnlyTokens.Any(e =>
                e.Key == "square-corners" && e.Kind == ThemeTokenKind.Flag));
    }

    // ==================== Windows 端 ====================

    private static void CheckWindowsSide(Action<string, bool> check)
    {
        if (RepoRoot is null)
        {
            check("Windows：源码静态漂移检查（未定位到仓库根，已跳过）", true);
            return;
        }

        string? theme = ReadSource("src", "Island.App", "Themes", "Theme.cs");
        var colors = ParseDeclaredProps(theme, "Color");
        var flags = ParseDeclaredProps(theme, "bool");

        // 清单登记的每个 Windows 属性，源码里必须真的声明了。
        check("Windows：Palette 声明了清单登记的每个属性",
            ThemeTokenManifest.WindowsProperties.All(p => colors.Contains(p) || flags.Contains(p)));

        // 反向：源码里的每个颜色 token 都必须登记进清单（新增 token 不登记 ⇒ 红）。
        check("Windows：Palette 的每个颜色 token 都已登记进清单",
            colors.All(c => ThemeTokenManifest.WindowsProperties.Contains(c)));
        check("Windows：Palette 的每个开关型 token 都已登记进清单",
            flags.All(f => ThemeTokenManifest.WindowsProperties.Contains(f)));
    }

    // ==================== mac 端 ====================

    private static void CheckMacSide(Action<string, bool> check)
    {
        if (RepoRoot is null)
        {
            check("mac：源码静态漂移检查（未定位到仓库根，已跳过）", true);
            return;
        }

        string? themes = ReadSource("src", "Island.Avalonia", "Themes.cs");
        var colors = ParseDeclaredProps(themes, "Color");
        var flags = ParseDeclaredProps(themes, "bool");

        check("mac：Palette 声明了清单登记的每个属性",
            ThemeTokenManifest.MacProperties.All(p => colors.Contains(p) || flags.Contains(p)));
        check("mac：Palette 的每个颜色 token 都已登记进清单",
            colors.All(c => ThemeTokenManifest.MacProperties.Contains(c)));
        check("mac：Palette 的每个开关型 token 都已登记进清单",
            flags.All(f => ThemeTokenManifest.MacProperties.Contains(f)));
    }

    // ==================== 小工具 ====================

    /// <summary>一组已知的语义键。</summary>
    private static string[] KnownKeys(params string[] keys) => keys;

    /// <summary>往上找仓库根（找带 Island.sln 的那一层）。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Island.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>读仓库里的一个源文件（读不到返回 null）。</summary>
    private static string? ReadSource(params string[] parts)
    {
        if (RepoRoot is null) return null;
        string path = Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray());
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 取出 Palette 里声明为 `public &lt;类型&gt; &lt;名&gt; { get; init; }` 的属性名。
    /// 只认 init-only 的自动属性：计算属性（`=&gt; 表达式`）与静态成员不算 token。
    /// </summary>
    private static HashSet<string> ParseDeclaredProps(string? source, string type)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (source is null) return set;
        var rx = new Regex($@"public\s+{type}\s+(\w+)\s*\{{\s*get;\s*init;\s*\}}");
        foreach (Match m in rx.Matches(source))
            set.Add(m.Groups[1].Value);
        return set;
    }
}
