using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Island.App.Controls;
using Island.Core.Capabilities;

namespace Island.Tests;

/// <summary>
/// **时钟视图的内容对齐门禁**（2026-11-26）。
///
/// 背景：两端默认收起视图统一成 clock 之后，暴露出「同一个视图、两端内容不同」——
/// mac 的时间胶囊是 **时间 + 秒 + 日期 + 天气**，Windows 此前只有 **时间 + 天气 + 网速**。
/// 用户拍板：Windows 补上秒与日期。
///
/// 本文件把这件事钉住：
///   · 文本格式（"11月27日·周五"、秒补零）由两端共用的 <see cref="ClockTextSpec"/> 产出 ——
///     **两端必须都从它取**（各写一份就会漂）；
///   · 两端渲染源码里**必须真的用到**秒与日期（源码静态校验，删掉调用即红）；
///   · 时钟视图的宽度上限**必须装得下**这些内容 —— 这个上限同时是排版预算，
///     给小了 DrawCompactGroups 会把装不下的组折成 "+N"，日期会**整个消失**（不是变窄）。
/// </summary>
internal static class ClockViewTests
{
    /// <summary>源码静态校验用的仓库根（找不到就跳过静态断言，不让测试因为路径找不到而红）。</summary>
    private static string? RepoRoot { get; } = FindRepoRoot();

    public static void Run(Action<string, bool> check)
    {
        // ---------- 文本规格本身 ----------
        check("时钟日期文本：2026-11-27 → 11月27日·周五",
            ClockTextSpec.DateText(new DateTime(2026, 11, 27)) == "11月27日·周五");
        // 只断言日期部分（星期由 WeekdayShort 决定，这里不重复钉）
        check("时钟日期文本：月/日不补零（11月7日）",
            ClockTextSpec.DateText(new DateTime(2026, 11, 7)).StartsWith("11月7日·",
                StringComparison.Ordinal));
        check("时钟日期文本：星期部分与 WeekdayShort 一致",
            ClockTextSpec.DateText(new DateTime(2026, 11, 7))
                == $"11月7日·{ClockTextSpec.WeekdayShort(new DateTime(2026, 11, 7).DayOfWeek)}");
        check("时钟日期文本：用的是间隔点不是空格（不能出现 '11月27日 周五'）",
            !ClockTextSpec.DateText(new DateTime(2026, 11, 27)).Contains(' '));
        check("时钟秒文本：两位补零",
            ClockTextSpec.SecondsText(new DateTime(2026, 1, 1, 0, 0, 7)) == "07");
        check("时钟星期缩写：七天全覆盖，未知值返回空",
            Enum.GetValues<DayOfWeek>().All(d => ClockTextSpec.WeekdayShort(d).Length == 2)
            && ClockTextSpec.WeekdayShort((DayOfWeek)99) == "");

        // ---------- 宽度上限：装得下 ----------
        var (cap, height) = CompactLayout.Size("clock");
        // 实测：时间+秒 ≈62、日期 ≈70、天气 ≈48、网速 ≈73、组间距 12×3、内边距 16×2 ⇒ 约 260（无天气）。
        // 含天气 ≈308；网速读数变长（"999.9M"）再 +20 ⇒ 上限至少 320。
        check($"时钟视图宽度上限 ≥ 320（给小了日期会被折成 +N 而整个消失）：当前 {cap}",
            cap >= 320);
        check($"时钟视图高度仍是 44：当前 {height}",
            height == 44);

        // ---------- 两端渲染源码都必须用同一份规格 ----------
        if (RepoRoot is null)
        {
            check("时钟视图两端源码校验（未定位到仓库根，已跳过）", true);
            return;
        }

        string? win = ReadSource("src", "Island.App", "Controls", "IslandCanvas.cs");
        string? mac = ReadSource("src", "Island.Avalonia", "IslandCanvas.cs");

        check("Windows：时钟视图从共用规格取日期",
            win?.Contains("ClockTextSpec.DateText") == true);
        check("Windows：时钟视图从共用规格取秒",
            win?.Contains("ClockTextSpec.SecondsText") == true);
        check("macOS：时钟视图从共用规格取日期",
            mac?.Contains("ClockTextSpec.DateText") == true);
        check("macOS：时钟视图从共用规格取秒",
            mac?.Contains("ClockTextSpec.SecondsText") == true);

        // 两端都不许自己再写一份星期缩写（写了就会漂）
        check("Windows：没有自己写星期缩写表（统一走 ClockTextSpec）",
            win?.Contains("private static string WeekdayShort") != true);
        check("macOS：没有自己写星期缩写表（统一走 ClockTextSpec）",
            mac?.Contains("private static string WeekdayShort") != true);
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
