using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Island.Core.Capabilities;

namespace Island.Tests;

/// <summary>
/// **主题可读性门禁**（2026-11-26）。
///
/// 背景（实测）：Windows 端有逐主题的对比度用例（DesignSystemTests），**macOS 端一条都没有** ——
/// 测试工程只链了 Windows 的 Theme.cs，mac 的 Themes.cs 依赖 Avalonia 进不了零依赖测试工程。
/// 结果是 mac 的 9 套主题里，三级文字最差只有 2.50:1（Light），而三级文字正是
/// **底部状态行（9px）**的颜色 —— 没人守、也没人知道。
///
/// 做法：**静态解析**两端主题源码（不引 Avalonia，零依赖测试工程照样能跑），
/// 逐套主题逐 token 算 WCAG 对比度，与 <see cref="ThemeContrastGate"/> 里**按平台各设**的门槛比对。
/// 门槛是"当前设计的实测基线"，所以：mac 端现在全绿，以后谁把某个颜色调暗到基线以下 ⇒ 立刻红。
///
/// 另外顺带守住一件事：**两端的 ByName 主题集合必须一致**（此前只有"两端恰好一样"，
/// 没有任何门禁 —— 给一边加主题，用户就会发现两台机器的托盘菜单不一样）。
/// </summary>
internal static class ThemeContrastTests
{
    /// <summary>源码静态校验用的仓库根（找不到就跳过静态断言，不让测试因为路径找不到而红）。</summary>
    private static string? RepoRoot { get; } = FindRepoRoot();

    public static void Run(Action<string, bool> check)
    {
        if (RepoRoot is null)
        {
            check("主题可读性门禁（未定位到仓库根，已跳过）", true);
            return;
        }

        string? winSrc = ReadSource("src", "Island.App", "Themes", "Theme.cs");
        string? macSrc = ReadSource("src", "Island.Avalonia", "Themes.cs");

        check("主题可读性门禁：找到了两端主题源码", winSrc is not null && macSrc is not null);
        if (winSrc is null || macSrc is null) return;

        var winPalettes = ParsePalettes(winSrc);
        var macPalettes = ParsePalettes(macSrc);

        check("主题可读性门禁：解析出 Windows 端主题（≥9 套）", winPalettes.Count >= 9);
        check("主题可读性门禁：解析出 macOS 端主题（≥9 套，含 IslandDay / IslandNight）", macPalettes.Count >= 9);

        CheckPlatform(check, "Windows", winPalettes, macOS: false);
        CheckPlatform(check, "macOS", macPalettes, macOS: true);

        // ---- 两端主题名集合必须一致 ----
        var winNames = ParseThemeNames(winSrc);
        var macNames = ParseThemeNames(macSrc);
        check("主题名：两端 ByName 的主题集合完全一致",
            winNames is not null && macNames is not null && winNames.SetEquals(macNames));
        check("主题名：两端各 9 套内置主题",
            winNames?.Count == 9 && macNames?.Count == 9);
    }

    // ==================== 逐平台逐主题比对 ====================

    private static void CheckPlatform(
        Action<string, bool> check,
        string label,
        Dictionary<string, Dictionary<string, Color4>> palettes,
        bool macOS)
    {
        foreach (var name in palettes.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var props = palettes[name];
            if (!props.TryGetValue("Surface", out var surface))
            {
                check($"{label} {name}：能取到底色", false);
                continue;
            }

            foreach (var rule in ThemeContrastGate.Rules)
            {
                var token = ThemeTokenManifest.Find(rule.TokenKey);
                string? propName = token is null
                    ? null
                    : (macOS ? token.MacProperty : token.WindowsProperty);

                if (token is null || propName is null || !props.TryGetValue(propName, out var c))
                {
                    check($"{label} {name}：{rule.Role} 有色值可测", false);
                    continue;
                }

                // 轨道 / 发丝线是半透明或需要"叠在底色上"看 ⇒ 先与底色合成再算。
                var over = (c.A == 255)
                    ? c
                    : Color4.From(ThemeContrastGate.Over(c.R, c.G, c.B, c.A, surface.R, surface.G, surface.B));
                double ratio = ThemeContrastGate.Ratio(over.R, over.G, over.B, surface.R, surface.G, surface.B);
                double min = ThemeContrastGate.MinFor(rule, macOS);

                check($"{label} {name}：{rule.Role} ≥ {min:0.00}:1",
                    ratio >= min);
            }
        }
    }

    // ==================== 源码解析 ====================

    /// <summary>一个颜色（带 alpha）。</summary>
    private readonly record struct Color4(byte R, byte G, byte B, byte A)
    {
        public static Color4 From((byte R, byte G, byte B) t) => new(t.R, t.G, t.B, 255);
    }

    /// <summary>
    /// 解析主题源码里所有 <c>public static Palette X =&gt; new() { ... }</c> 块。
    /// 也支持 <c>=&gt; Y with { ... }</c>（mac 的 IslandNight 继承 IslandDay）。
    /// </summary>
    private static Dictionary<string, Dictionary<string, Color4>> ParsePalettes(string source)
    {
        var result = new Dictionary<string, Dictionary<string, Color4>>(StringComparer.Ordinal);

        // ⚠️ 记录上的**属性级默认值**也是一层底：Windows 的 Palette 记录上有一批
        // "public Color Track { get; init; } = Color.FromArgb(...)"，没显式写的主题就吃这层值
        // （Track / Hairline / Warning / TextTertiary 等）。不解析它 ⇒ 那些主题看起来"缺色值"。
        var defaults = ParseRecordDefaults(source);

        var rx = new Regex(@"public\s+static\s+Palette\s+(\w+)\s*=>\s*(new\(\)|(\w+)\s+with)\s*\{",
            RegexOptions.Compiled);

        foreach (Match m in rx.Matches(source))
        {
            string name = m.Groups[1].Value;
            string? baseName = m.Groups[3].Success ? m.Groups[3].Value : null;

            int open = m.Index + m.Length - 1;
            int close = FindMatchingBrace(source, open);
            if (close < 0) continue;

            string body = source.Substring(open + 1, close - open - 1);
            var props = new Dictionary<string, Color4>(StringComparer.Ordinal);

            // 底层：记录上的属性级默认值。
            foreach (var kv in defaults) props[kv.Key] = kv.Value;

            // 继承：先铺 base 的属性，再用本块覆盖。
            if (baseName is not null && result.TryGetValue(baseName, out var baseProps))
            {
                foreach (var kv in baseProps) props[kv.Key] = kv.Value;
            }

            foreach (Match p in Regex.Matches(body, @"(\w+)\s*=\s*Color\.From(Rgb|Argb)\(([^)]*)\)"))
            {
                var c = ParseColor(p.Groups[2].Value, p.Groups[3].Value);
                if (c is not null) props[p.Groups[1].Value] = c.Value;
            }

            result[name] = props;
        }

        return result;
    }

    /// <summary>
    /// 解析 Palette **记录上的属性级默认值**（形如
    /// <c>public Color Track { get; init; } = Color.FromArgb(...);</c>）。
    /// 没显式写这个属性的主题就吃这层值 —— 不解析它，这些主题会看起来"缺色值"。
    /// </summary>
    private static Dictionary<string, Color4> ParseRecordDefaults(string source)
    {
        var d = new Dictionary<string, Color4>(StringComparer.Ordinal);
        foreach (Match p in Regex.Matches(
                     source, @"public\s+Color\s+(\w+)\s*\{\s*get;\s*init;\s*\}\s*=\s*Color\.From(Rgb|Argb)\(([^)]*)\)"))
        {
            var c = ParseColor(p.Groups[2].Value, p.Groups[3].Value);
            if (c is not null) d[p.Groups[1].Value] = c.Value;
        }
        return d;
    }

    /// <summary>解析 <c>FromRgb</c> / <c>FromArgb</c> 的实参（十六进制字面量）。</summary>
    private static Color4? ParseColor(string kind, string argsText)
    {
        var args = argsText.Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToInt32(s.Substring(2), 16)
                : int.Parse(s))
            .ToList();

        if (args.Count == 3)
            return new Color4((byte)args[0], (byte)args[1], (byte)args[2], 255);
        if (args.Count == 4)
        {
            // FromArgb(a, r, g, b)
            return new Color4((byte)args[1], (byte)args[2], (byte)args[3], (byte)args[0]);
        }
        if (args.Count == 1)
            return new Color4((byte)args[0], (byte)args[0], (byte)args[0], (byte)args[0]);
        return null;
    }

    /// <summary>取出 <c>ByName</c> switch 里列出的主题名。</summary>
    private static HashSet<string>? ParseThemeNames(string source)
    {
        int i = source.IndexOf("ByName(string name)", StringComparison.Ordinal);
        if (i < 0) return null;
        int open = source.IndexOf('{', i);
        if (open < 0) return null;
        int close = FindMatchingBrace(source, open);
        if (close < 0) return null;

        string body = source.Substring(open, close - open + 1);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(body, @"""([A-Za-z0-9_]+)""\s*=>"))
            names.Add(m.Groups[1].Value);
        return names;
    }

    /// <summary>从 '{' 开始找配对的 '}'（源码里的主题块不含字符串花括号，直接计数即可）。</summary>
    private static int FindMatchingBrace(string source, int openIndex)
    {
        int depth = 0;
        for (int i = openIndex; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    // ==================== 小工具 ====================

    /// <summary>往上找仓库根（找带 Island.sln 的那一层）。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
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
