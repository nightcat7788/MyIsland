using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Island.Tests;

/// <summary>
/// **发布文档与版本号同步门禁**（2026-11-27）。
///
/// 起因（用户实拍）：README 顶部的「当前版本：1.1.5」已经更新，
/// 但下面的「下载」段还指着 **v1.1.3** 的地址与文件名 —— 版本号升级时漏改了一处，
/// 而仓库里没有任何检查能发现它。
///
/// 本文件守住：凡是**面向用户**写着下载链接的地方，链接与文件名里的版本号
/// 必须等于 `Directory.Build.props` 里声明的当前版本。
/// （`docs/改进记录.md` 与历史计划文档里出现的旧版本号是**历史记录**，不在本门禁范围内。）
/// </summary>
internal static class ReleaseDocTests
{
    /// <summary>源码静态校验用的仓库根（找不到就跳过静态断言，不让测试因为路径找不到而红）。</summary>
    private static string? RepoRoot { get; } = FindRepoRoot();

    public static void Run(Action<string, bool> check)
    {
        if (RepoRoot is null)
        {
            check("发布文档版本同步（未定位到仓库根，已跳过）", true);
            return;
        }

        string? props = ReadRepoFile("Directory.Build.props");
        string? readme = ReadRepoFile("README.md");
        string? verReadme = ReadRepoFile(Path.Combine("docs", "verification", "README.md"));

        var m = props is null ? null : Regex.Match(props, "<Version>([^<]+)</Version>");
        string version = m?.Success == true ? m.Groups[1].Value.Trim() : "";
        check("能从 Directory.Build.props 读出当前版本号", version.Length > 0);
        if (version.Length == 0) return;

        // ① README：版本行、下载段链接、两个产物文件名，都必须是当前版本
        check("README 的「当前版本」行与 Directory.Build.props 一致",
            readme?.Contains($"当前版本：{version}") == true);
        check("README 的下载链接指向当前版本的 Release",
            readme?.Contains($"releases/tag/v{version}") == true);
        check("README 的安装包文件名是当前版本",
            readme?.Contains($"MyIsland-Setup-{version}.exe") == true);
        check("README 的便携包文件名是当前版本",
            readme?.Contains($"MyIsland-{version}-portable.zip") == true);

        // ② README 里不允许残留**别的**版本的 Release 链接（这是这次漏改的那一处）
        var tags = Regex.Matches(readme ?? "", @"releases/tag/(v[0-9.]+)").Select(x => x.Groups[1].Value).Distinct().ToList();
        check($"README 里没有残留其它版本的 Release 链接（实际：{string.Join(" / ", tags)}）",
            tags.Count == 0 || (tags.Count == 1 && tags[0] == $"v{version}"));

        // ③ 验证目录的版本锚点同样要跟着走
        check("docs/verification/README.md 的版本锚点与当前版本一致",
            verReadme?.Contains($"releases/tag/v{version}") == true);
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

    /// <summary>读仓库里的一个文件（读不到返回 null）。</summary>
    private static string? ReadRepoFile(params string[] parts)
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
