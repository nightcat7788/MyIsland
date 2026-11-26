using System;
using System.IO;
using System.Linq;
using Island.Core.Capabilities;

namespace Island.Tests;

/// <summary>
/// **鼠标穿透门禁**（2026-11-27）。
///
/// 起因（用户实测）：开着「鼠标穿透」时岛体整体对鼠标透明 ⇒
/// 展开面板**看得见却点不动**，连面板里那个开关都点不回来（鸡生蛋），
/// 而托盘右键菜单里没有穿透开关 ⇒ 只能绕到设置中心才能退出这个状态。
///
/// 本文件守住三件事：
///   ① **规则**：穿透只对收起态生效；展开面板必须可交互；
///   ② **逃生口**：托盘右键菜单里必须有「鼠标穿透」项（不许被删掉）；
///   ③ **单一出处**：WS_EX_TRANSPARENT 只能经由规则设置，不能在别处直接写死。
/// </summary>
internal static class ClickThroughTests
{
    /// <summary>源码静态校验用的仓库根（找不到就跳过静态断言，不让测试因为路径找不到而红）。</summary>
    private static string? RepoRoot { get; } = FindRepoRoot();

    public static void Run(Action<string, bool> check)
    {
        // ---------- ① 规则 ----------
        check("穿透规则：开着穿透且收起态 ⇒ 鼠标穿透（游戏点击落到下层应用）",
            ClickThroughRule.ShouldBeTransparent(clickThroughEnabled: true, expanded: false));
        check("穿透规则：开着穿透但面板展开 ⇒ **不**穿透（展开就要能点）",
            !ClickThroughRule.ShouldBeTransparent(clickThroughEnabled: true, expanded: true));
        check("穿透规则：没开穿透 ⇒ 永远不穿透",
            !ClickThroughRule.ShouldBeTransparent(clickThroughEnabled: false, expanded: false)
            && !ClickThroughRule.ShouldBeTransparent(clickThroughEnabled: false, expanded: true));

        // ---------- ② 逃生口 ----------
        if (RepoRoot is null)
        {
            check("鼠标穿透逃生口（未定位到仓库根，已跳过）", true);
            return;
        }

        string? app = ReadSource("src", "Island.App", "App.xaml.cs");
        check("托盘右键菜单里有「鼠标穿透」项（穿透状态下唯一的退出入口）",
            app?.Contains("鼠标穿透（游戏时不拦截点击）") == true);
        check("托盘的穿透项可勾选且与设置状态同步（打开菜单时刷新）",
            app?.Contains("IsCheckable = true") == true && app?.Contains("RefreshClickThroughItem") == true);

        // ---------- ③ 单一出处 ----------
        string? win = ReadSource("src", "Island.App", "Controls", "IslandWindow.xaml.cs");
        check("窗口样式统一走 ApplyClickThroughStyle（判定只留一处）",
            win?.Contains("ApplyClickThroughStyle") == true);
        // 判定前先剥掉注释：注释里提到 WS_EX_TRANSPARENT 不算"写死"。
        string winCode = StripComments(win ?? "");
        int start = winCode.IndexOf("public void SetClickThrough", StringComparison.Ordinal);
        string body = start < 0 ? "" : winCode[start..];
        int end = body.IndexOf("private void ApplyClickThroughStyle", StringComparison.Ordinal);
        if (end >= 0) body = body[..end];
        check("WS_EX_TRANSPARENT 不在 SetClickThrough 里直接写死（必须经由规则）",
            !body.Contains("WS_EX_TRANSPARENT"));
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

    /// <summary>剥掉注释（块注释 + 行注释），只留代码。</summary>
    private static string StripComments(string source)
    {
        source = System.Text.RegularExpressions.Regex.Replace(source, @"/\*.*?\*/", " ",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return System.Text.RegularExpressions.Regex.Replace(source, @"//[^\n]*", " ");
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
