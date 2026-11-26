using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Island.Core.Capabilities;

namespace Island.Tests;

/// <summary>
/// **跨端语义 token 用法漂移检查**（2026-11-26 task14）。
///
/// 背景：E3 已经把"两端 12 个同名 token 指同一语义角色"写进了 `ThemeTokenManifest`，
/// `ThemeTokenTests` 也守住了"两端 Palette **声明**了清单里的每个 token"（不缺项）。
/// 但那两条都只管 **Palette 的声明**，管不到**渲染代码实际引用了哪个 token**：
/// Windows 把卡片标题画成 `pal.Text`、mac 画成 `pal.TextDim` ⇒ 清单不缺项 ⇒ 旧门禁全绿，
/// 可同一个界面元素在两端用了不同的语义角色（看起来就是"两端不一样"）。
///
/// 本门禁守的是**用法**：把"关键界面元素 → 两端统一语义 token"登记进清单
/// （`ThemeTokenManifest.ElementTokens`），再回读两端渲染源码，按**绘制调用点**定位
/// （不是数 `pal.` 出现次数 —— 计数法太脆），断言两端用的是同一个语义 token。
///
/// 范式沿用 `CapabilityManifestTests.CheckMacSide`：mac 端代码链不进零依赖测试工程 ⇒ 读源码做静态断言。
/// </summary>
internal static class ThemeTokenUsageTests
{
    /// <summary>源码静态校验用的仓库根（找不到就跳过静态断言，不让测试因为路径找不到而红）。</summary>
    private static string? RepoRoot { get; } = FindRepoRoot();

    public static void Run(Action<string, bool> check)
    {
        CheckManifestItself(check);
        CheckBothSides(check);
    }

    // ==================== 清单自身 ====================

    private static void CheckManifestItself(Action<string, bool> check)
    {
        var elements = ThemeTokenManifest.ElementTokens;

        check("用法清单：元素表非空", elements.Count > 0);
        check("用法清单：元素键唯一",
            elements.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() == elements.Count);
        check("用法清单：每个元素都有显示名",
            elements.All(e => !string.IsNullOrWhiteSpace(e.Title)));
        check("用法清单：每个元素都登记了语义 token 键",
            elements.All(e => !string.IsNullOrWhiteSpace(e.TokenKey)));

        // 元素登记的必须是**两端共享**的 token：平台专属 token（elevated / fill / selection）
        // 两端根本不存在同一个角色，拿来当跨端口径是自欺欺人。
        check("用法清单：元素登记的 token 必须是两端共享 token",
            elements.All(e => ThemeTokenManifest.SharedTokens.Any(t => t.Key == e.TokenKey)));
        check("用法清单：共享 token 两端属性同名（跨端比较的前提）",
            elements.All(e =>
            {
                var t = ThemeTokenManifest.SharedTokens.FirstOrDefault(x => x.Key == e.TokenKey);
                return t is not null && t.WindowsProperty == t.MacProperty;
            }));

        check("用法清单：每个元素都登记了两端绘制点的定位规则",
            elements.All(e => !string.IsNullOrWhiteSpace(e.WindowsLocator)
                      && !string.IsNullOrWhiteSpace(e.MacLocator)));
        check("用法清单：定位规则都能编译成正则",
            elements.All(e => TryCompile(e.WindowsLocator) is not null && TryCompile(e.MacLocator) is not null));

        // 已知的元素全集（防止悄悄少登记一个元素 ⇒ 门禁覆盖面缩水）。
        check("用法清单：覆盖了已知的 8 个关键界面元素",
            KnownKeys("card-title", "row-label", "status-line", "empty-state",
                      "process-name", "process-value",
                      "sort-segment-selected-bg", "sort-segment-selected-text")
                .All(k => elements.Any(e => e.Key == k)));
    }

    // ==================== 两端渲染源码 ====================

    private static void CheckBothSides(Action<string, bool> check)
    {
        string? win = ReadSource("src", "Island.App", "Controls", "MonitorPage.cs");
        string? mac = ReadSource("src", "Island.Avalonia", "IslandCanvas.cs");
        if (win is null || mac is null)
        {
            check("跨端 token 用法：源码静态漂移检查（未定位到仓库根或源文件，已跳过）", true);
            return;
        }

        foreach (var e in ThemeTokenManifest.ElementTokens)
        {
            var token = ThemeTokenManifest.SharedTokens.FirstOrDefault(t => t.Key == e.TokenKey);
            string expected = token?.WindowsProperty ?? "?";
            var w = Extract(win, e.WindowsLocator);
            var m = Extract(mac, e.MacLocator);

            // 定位不到绘制点 ⇒ 红（不是跳过）：绘制点改名 / 挪位了，门禁必须跟着更新。
            check($"跨端 token 用法：{e.Title} —— 两端都定位到绘制点（{e.Key}）",
                w.Count > 0 && m.Count > 0);

            // 核心断言：两端用的都是清单登记的那个语义 token（不是同一个 hex，是同一个语义角色）。
            check($"跨端 token 用法：{e.Title} —— 两端用同一个语义 token（清单 {e.TokenKey} → {expected}）"
                  + $"｜Windows 用 {Describe(w)}｜mac 用 {Describe(m)}",
                w.Count > 0 && m.Count > 0
                && w.All(x => x.Token == expected)
                && m.All(x => x.Token == expected)
                && w.Select(x => x.Token).ToHashSet(StringComparer.Ordinal)
                      .SetEquals(m.Select(x => x.Token)));
        }
    }

    // ==================== 小工具 ====================

    /// <summary>一个绘制点命中：用的 Palette 属性名 + 源码行号（行号用于失败时定位）。</summary>
    private readonly record struct Hit(string Token, int Line);

    /// <summary>一组已知的元素键。</summary>
    private static string[] KnownKeys(params string[] keys) => keys;

    /// <summary>把定位正则跑一遍，收集所有命中（属性名 + 行号）。编译失败返回空列表。</summary>
    private static List<Hit> Extract(string source, string locator)
    {
        var hits = new List<Hit>();
        Regex? rx = TryCompile(locator);
        if (rx is null) return hits;
        foreach (Match m in rx.Matches(source))
        {
            // 只有恰好一个捕获组时，组 1 才是"该绘制点用的属性名"。
            if (m.Groups.Count < 2) continue;
            hits.Add(new Hit(m.Groups[1].Value, LineOf(source, m.Index)));
        }
        return hits;
    }

    /// <summary>编译定位正则（编译不了就返回 null ⇒ 门禁红）。</summary>
    private static Regex? TryCompile(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        try
        {
            return new Regex(pattern);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>把命中列表说成人话：<c>Text@354 / Text@391</c>。</summary>
    private static string Describe(List<Hit> hits) =>
        hits.Count == 0 ? "（没定位到）" : string.Join(" / ", hits.Select(h => $"{h.Token}@{h.Line}"));

    /// <summary>源码里某个下标所在的行号（从 1 开始）。</summary>
    private static int LineOf(string source, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < source.Length; i++)
            if (source[i] == '\n') line++;
        return line;
    }

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
}
