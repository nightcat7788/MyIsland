using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Island.App.Controls;
using Island.App.Services;
using Island.Core.Capabilities;

namespace Island.Tests;

/// <summary>
/// **能力清单漂移检查**（2026-11-26 阶段 4/5）。
///
/// 背景：这轮实测推翻了一致性总纲里的三条记录（收起视图集两端其实不一样、主题 token 不同源、
/// 小形象是两份独立实现），而且几份计划文档的复选框大面积漂移（工作做完框没勾）。
/// 根因是"两端各写一份、靠人记"。解法是：**能力清单当唯一真相源 + 本文件当漂移检查**。
///
/// 本文件把"清单改了、两端没跟着改"变成门禁能看见的红：
///   · 清单自身的完整性（键唯一、无空键、小形象款式与共享白名单一致）；
///   · 可用性与归属正确（共享项两端都可用、平台专属项只在本端可用）；
///   · 两端的收起视图 / 展开页签**确实取自清单**：
///       —— Windows 收起视图用**运行时**断言（CompactLayout 链进测试工程）；
///       —— Windows 展开页签与 mac 两端清单在 Avalonia 工程里，链不进来
///          ⇒ 用**源码静态校验**（读源码，断言它引用了清单、且覆盖的键与清单一致）。
/// </summary>
internal static class CapabilityManifestTests
{
    /// <summary>源码静态校验用的仓库根（找不到就跳过静态断言，不让测试因为路径找不到而红）。</summary>
    private static string? RepoRoot { get; } = FindRepoRoot();

    public static void Run(Action<string, bool> check)
    {
        CheckManifestItself(check);
        CheckAvailability(check);
        CheckWindowsSide(check);
        CheckMacSide(check);
    }

    // ==================== 清单自身 ====================

    private static void CheckManifestItself(Action<string, bool> check)
    {
        // ---- 键的唯一性 ----
        // 清单里同一个键可以出现在**不同分组**（例如 monitor 既是收起视图、也是展开页签），
        // 所以身份是 (分组, 键)；组内键必须唯一，否则"按键取能力"会取到两样东西。
        check("清单：CollapsedViews 的 Key 组内唯一",
            Distinct(CapabilityManifest.CollapsedViews.Select(e => e.Key)));
        check("清单：ExpandedTabs 的 Key 组内唯一",
            Distinct(CapabilityManifest.ExpandedTabs.Select(e => e.Key)));
        check("清单：DataSources 的 Key 组内唯一",
            Distinct(CapabilityManifest.DataSources.Select(e => e.Key)));
        check("清单：(分组, 键) 全局唯一",
            Distinct(CapabilityManifest.All.Select(e => e.Group + "/" + e.Key)));

        // ---- 不许有空键 / 空标题 ----
        check("清单：CollapsedViews 没有空 Key",
            CapabilityManifest.CollapsedViews.All(e => !string.IsNullOrWhiteSpace(e.Key)));
        check("清单：ExpandedTabs 没有空 Key",
            CapabilityManifest.ExpandedTabs.All(e => !string.IsNullOrWhiteSpace(e.Key)));
        check("清单：DataSources 没有空 Key",
            CapabilityManifest.DataSources.All(e => !string.IsNullOrWhiteSpace(e.Key)));
        check("清单：所有条目都有显示名",
            CapabilityManifest.All.All(e => !string.IsNullOrWhiteSpace(e.Title)));

        // ---- 小形象款式：清单与共享白名单必须完全一致 ----
        // Settings.PetStyleValues 是读盘白名单（坏值退回默认），清单是唯一真相源：
        // 两边不一致 ⇒ 一端认得的款式另一端不认（或反过来），小形象就会"一边有一边没有"。
        check("清单：小形象款式恰好 8 款",
            CapabilityManifest.PetStyles.Count == 8);
        check("清单：小形象款式与 Settings.PetStyleValues 完全一致（含顺序）",
            CapabilityManifest.PetStyles.SequenceEqual(Settings.PetStyleValues));
    }

    // ==================== 可用性与归属 ====================

    private static void CheckAvailability(Action<string, bool> check)
    {
        // 共享项：两端都必须可用（否则一端少了能力，却没人在清单里说清）。
        check("清单：共享项两端都可用",
            CapabilityManifest.All
                .Where(e => e.Availability == CapabilityAvailability.Shared)
                .All(e => CapabilityManifest.Available(e, macOS: true)
                       && CapabilityManifest.Available(e, macOS: false)));

        // 平台专属项：WindowsOnly 只有 Windows 可用，MacOnly 只有 mac 可用。
        check("清单：WindowsOnly 项只有 Windows 可用",
            CapabilityManifest.All
                .Where(e => e.Owner == CapabilityOwner.WindowsOnly)
                .All(e => CapabilityManifest.Available(e, macOS: false)
                       && !CapabilityManifest.Available(e, macOS: true)));
        check("清单：MacOnly 项只有 mac 可用",
            CapabilityManifest.All
                .Where(e => e.Owner == CapabilityOwner.MacOnly)
                .All(e => CapabilityManifest.Available(e, macOS: true)
                       && !CapabilityManifest.Available(e, macOS: false)));

        // Unsupported 的项两端都不可用（清单里出现这种项就意味着两端都不该有它）。
        check("清单：Unsupported 项两端都不可用",
            CapabilityManifest.All
                .Where(e => e.Availability == CapabilityAvailability.Unsupported)
                .All(e => !CapabilityManifest.Available(e, macOS: true)
                       && !CapabilityManifest.Available(e, macOS: false)));
    }

    // ==================== Windows 端 ====================

    private static void CheckWindowsSide(Action<string, bool> check)
    {
        // ---- 收起视图：运行时断言（CompactLayout 是纯数据类，链进了测试工程）----
        check("Windows：收起视图集合 == 清单 CollapsedViewsFor(Windows)",
            CompactLayout.Views.SequenceEqual(CapabilityManifest.CollapsedViewsFor(macOS: false)));

        // ---- 源码静态校验 ----
        if (RepoRoot is null)
        {
            check("Windows：源码静态漂移检查（未定位到仓库根，已跳过）", true);
            return;
        }

        string? compactLayout = ReadSource("src", "Island.App", "Controls", "CompactLayout.cs");
        string? settingsWindow = ReadSource("src", "Island.App", "Controls", "SettingsWindow.cs");
        string? expandedPanel = ReadSource("src", "Island.App", "Controls", "ExpandedPanel.xaml.cs");

        check("Windows：CompactLayout.cs 引用了能力清单",
            compactLayout?.Contains("CapabilityManifest") == true);
        check("Windows：CompactLayout.cs 不再写死收起视图键列表",
            compactLayout?.Contains("\"clock\", \"dash\"") != true);

        check("Windows：SettingsWindow.cs 的小形象款式取自能力清单",
            settingsWindow?.Contains("CapabilityManifest.PetStyles") == true);
        check("Windows：SettingsWindow.cs 不再写死小形象款式表",
            settingsWindow?.Contains("(\"classic\", \"经典双眼\")") != true);

        check("Windows：ExpandedPanel.xaml.cs 引用了能力清单",
            expandedPanel?.Contains("CapabilityManifest") == true);
        check("Windows：ExpandedPanel.xaml.cs 的页签顺序取自清单 ExpandedTabsFor(Windows)",
            expandedPanel?.Contains("CapabilityManifest.ExpandedTabsFor(macOS: false)") == true);
        check("Windows：ExpandedPanel.xaml.cs 不再写死页签按钮数组",
            expandedPanel?.Contains("new[] { btnMonitor") != true);

        // 收起态装饰（刘海融合造型）：清单里登记为 mac 专属 ⇒ Windows 端**不应有实现**。
        // 真在 Windows 端画出来了，说明清单该改（而不是让差异偷偷溜进来）。
        // 注意：RepoRoot 是属性，编译器不会对它做 null 收窄 ⇒ 先取到局部变量再用。
        string root = RepoRoot ?? "";
        bool windowsHasNotchImpl = false;
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "src", "Island.App"), "*.cs", SearchOption.AllDirectories))
        {
            try
            {
                if (File.ReadAllText(file).Contains("DrawNotchBody")) { windowsHasNotchImpl = true; break; }
            }
            catch { /* 读不到就当没有 */ }
        }
        check("Windows：src/Island.App 没有刘海融合造型的实现（该装饰在清单里登记为 mac 专属）",
            !windowsHasNotchImpl);
        check("清单：刘海融合造型登记为 mac 专属",
            CapabilityManifest.Decorations.Any(e =>
                e.Key == "notch-fusion"
                && e.Availability == CapabilityAvailability.PlatformSpecific
                && e.Owner == CapabilityOwner.MacOnly));

        // 页签键覆盖：清单里的 Windows 页签，展开面板必须都有对应控件（少一个 ⇒ 点了没反应）。
        var manifestKeys = CapabilityManifest.ExpandedTabsFor(macOS: false).ToHashSet();
        var mappedKeys = ParseSwitchKeys(expandedPanel, "\"([a-z0-9-]+)\"\\s*=>");
        check("Windows：展开面板覆盖了清单里的每个 Windows 页签",
            mappedKeys is not null && manifestKeys.SetEquals(mappedKeys));
    }

    // ==================== mac 端 ====================

    private static void CheckMacSide(Action<string, bool> check)
    {
        if (RepoRoot is null)
        {
            check("mac：源码静态漂移检查（未定位到仓库根，已跳过）", true);
            return;
        }

        string? macWindow = ReadSource("src", "Island.Avalonia", "IslandWindow.cs");
        string? macCanvas = ReadSource("src", "Island.Avalonia", "IslandCanvas.cs");

        // ---- 收起视图 ----
        check("mac：IslandWindow.cs 的收起视图取自清单 CollapsedViewsFor(mac)",
            macWindow?.Contains("CapabilityManifest.CollapsedViewsFor(macOS: true)") == true);
        check("mac：IslandWindow.cs 不再写死收起视图键列表",
            macWindow?.Contains("\"dash\", \"clock\", \"pet\", \"combo\"") != true);

        // ---- 展开页签 ----
        check("mac：IslandCanvas.cs 的页签顺序取自清单 ExpandedTabsFor(mac)",
            macCanvas?.Contains("CapabilityManifest.ExpandedTabsFor(macOS: true)") == true);
        var manifestKeys = CapabilityManifest.ExpandedTabsFor(macOS: true).ToHashSet();
        var drawnKeys = ParseCaseKeys(macCanvas);
        check("mac：展开面板画了清单里的每个 mac 页签",
            drawnKeys is not null && manifestKeys.SetEquals(drawnKeys));
        check("mac：IslandCanvas.cs 不再写死页签标题与顺序",
            macCanvas?.Contains("Tab(\"屏幕\"") != true);
    }

    // ==================== 小工具 ====================

    /// <summary>一组值里没有重复（Key 的唯一性判定）。</summary>
    private static bool Distinct(IEnumerable<string> keys)
    {
        var list = keys.ToList();
        return list.Count == list.Distinct(StringComparer.Ordinal).Count();
    }

    /// <summary>
    /// 往上找仓库根（找带 Island.sln 的那一层）。
    /// 找不到就返回 null ⇒ 静态校验整段跳过（不让测试因为路径找不到而红）。
    /// </summary>
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
    /// 取出"能力清单页签区"里 switch 分支覆盖的键（形如 "monitor" =&gt; ...）。
    /// 区域用注释标记圈住（见两端源码里的"能力清单页签区"），避免读到别处的字符串。
    /// </summary>
    private static HashSet<string>? ParseSwitchKeys(string? source, string keyPattern)
    {
        string? region = ExtractRegion(source);
        if (region is null) return null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(region, keyPattern))
            keys.Add(m.Groups[1].Value);
        return keys;
    }

    /// <summary>取出"能力清单页签区"里 case 分支覆盖的键（形如 case "monitor":）。</summary>
    private static HashSet<string>? ParseCaseKeys(string? source)
    {
        string? region = ExtractRegion(source);
        if (region is null) return null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(region, "case\\s+\"([a-z0-9-]+)\"\\s*:"))
            keys.Add(m.Groups[1].Value);
        return keys;
    }

    /// <summary>截取"能力清单页签区：开始"到"能力清单页签区：结束"之间的源码。</summary>
    private static string? ExtractRegion(string? source)
    {
        if (source is null) return null;
        const string begin = "能力清单页签区：开始";
        const string end = "能力清单页签区：结束";
        int b = source.IndexOf(begin, StringComparison.Ordinal);
        if (b < 0) return null;
        int e = source.IndexOf(end, b, StringComparison.Ordinal);
        if (e < 0) return null;
        return source.Substring(b, e - b);
    }
}
