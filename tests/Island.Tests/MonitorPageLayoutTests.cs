using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Island.App.Controls;
using Island.App.Services;
using Island.Core.Capabilities;
using Island.Core.Monitoring;

namespace Island.Tests;

/// <summary>
/// 监控页**卡片分组**的规格用例（2026-11-26 task16）。
///
/// 两端监控页的卡片分组统一成 mac 版现有分组（占用 / 温度 / 上下速度 / 占用最高），
/// **两端唯一真相源**是 <see cref="MonitorPageLayoutSpec"/>（src\Island.Core\Capabilities）。
/// 本文件把那份规格当契约锁住：
///   · 规格自身完整性（四张卡 / 键唯一 / 每行有标签与字段 / 平台可用性合法 /
///     行槽读的字段名真的是 <see cref="MonitorSnapshot"/> 上的字段）；
///   · <c>CardsFor</c> / <c>RowsFor</c> 的过滤结果（Windows 的温度卡只有 CPU / GPU 两行，mac 有三行）；
///   · Windows 端**计划行带**（产品代码 <see cref="MonitorPage.PlannedCards"/> 算出来的）：
///     温度卡里没有 SSD 温度那一行 —— 平台不支持 ⇒ 整行不出现（不是 0、也不是"未知"）；
///   · 源码静态检查：Windows 端的卡片标题与行标签**取自规格**，不在代码里另写一份字符串。
/// </summary>
internal static class MonitorPageLayoutTests
{
    /// <summary>源码静态校验用的仓库根（找不到就跳过静态断言，不让测试因为路径找不到而红）。</summary>
    private static string? RepoRoot { get; } = FindRepoRoot();

    public static void Run(Action<string, bool> check)
    {
        CheckSpecItself(check);
        CheckPlatformFiltering(check);
        CheckWindowsLayout(check);
        CheckWindowsSource(check);
    }

    // ==================== 规格自身完整性 ====================

    private static void CheckSpecItself(Action<string, bool> check)
    {
        var cards = MonitorPageLayoutSpec.Cards;

        check("监控页分组：规格有四张卡", cards.Count == 4);
        check("监控页分组：卡片键唯一",
            cards.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() == cards.Count);
        check("监控页分组：卡片键与顺序是已知的四张（usage / temp / speed / topproc）",
            cards.Select(c => c.Key).SequenceEqual(new[] { "usage", "temp", "speed", "topproc" }));
        check("监控页分组：每张卡都有标题",
            cards.All(c => !string.IsNullOrWhiteSpace(c.Title)));
        check("监控页分组：每张卡至少有一行",
            cards.All(c => c.Rows.Count > 0));
        check("监控页分组：每张卡至少在一个平台出现",
            cards.All(c => c.Windows || c.MacOS));

        var rows = cards.SelectMany(c => c.Rows).ToList();
        check("监控页分组：每张卡里的行键唯一",
            cards.All(c => c.Rows.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() == c.Rows.Count));
        check("监控页分组：每行都有行标签（进程榜那一行是清单，本来就没有行标签）",
            rows.All(r => r.Label.Length > 0 || r.Key == "top-processes"));
        check("监控页分组：每行都写清了读的是哪个快照字段",
            rows.All(r => !string.IsNullOrWhiteSpace(r.Field)));
        check("监控页分组：每行至少在一个平台出现",
            rows.All(r => r.Windows || r.MacOS));

        // 行槽的 Field 必须是**真的存在**的字段（规格与快照对得上）：
        // 占用/速度类读数在 MonitorSnapshot，温度类在 ThermalSnapshot（主板温度只在后者）。
        var props = typeof(MonitorSnapshot).GetProperties().Select(p => p.Name)
            .Concat(typeof(ThermalSnapshot).GetProperties().Select(p => p.Name))
            .ToHashSet(StringComparer.Ordinal);
        check("监控页分组：每行读的字段在快照上真的存在（MonitorSnapshot / ThermalSnapshot）",
            rows.All(r => props.Contains(r.Field)));
    }

    // ==================== 平台过滤 ====================

    private static void CheckPlatformFiltering(Action<string, bool> check)
    {
        var win = MonitorPageLayoutSpec.CardsFor(macOS: false);
        var mac = MonitorPageLayoutSpec.CardsFor(macOS: true);
        check("监控页分组：Windows 与 mac 各有四张卡，顺序与规格一致",
            win.Count == 4 && mac.Count == 4
            && win.Select(c => c.Key).SequenceEqual(MonitorPageLayoutSpec.Cards.Select(c => c.Key))
            && mac.Select(c => c.Key).SequenceEqual(MonitorPageLayoutSpec.Cards.Select(c => c.Key)));

        var usage = MonitorPageLayoutSpec.Cards.First(c => c.Key == "usage");
        var temp = MonitorPageLayoutSpec.Cards.First(c => c.Key == "temp");
        var speed = MonitorPageLayoutSpec.Cards.First(c => c.Key == "speed");

        check("监控页分组：占用卡两端都是三行（CPU / 内存 / 磁盘）",
            MonitorPageLayoutSpec.RowsFor(usage, macOS: false).Select(r => r.Key)
                .SequenceEqual(new[] { "cpu-usage", "memory", "disk" })
            && MonitorPageLayoutSpec.RowsFor(usage, macOS: true).Select(r => r.Key)
                .SequenceEqual(new[] { "cpu-usage", "memory", "disk" }));

        var winTemp = MonitorPageLayoutSpec.RowsFor(temp, macOS: false);
        check("监控页分组：Windows 的温度卡三行（CPU / GPU / 主板）",
            winTemp.Select(r => r.Key).SequenceEqual(new[] { "cpu-temp", "gpu-temp", "board-temp" }));
        check("监控页分组：Windows 侧不出现 SSD 温度（平台不支持 ⇒ 整行不出现）",
            !winTemp.Any(r => r.Key == "storage-temp"));
        check("监控页分组：Windows 侧出现主板温度（平台专属补位）",
            winTemp.Any(r => r.Key == "board-temp"));
        var macTemp = MonitorPageLayoutSpec.RowsFor(temp, macOS: true);
        check("监控页分组：mac 的温度卡有三行（CPU / GPU / SSD）",
            macTemp.Select(r => r.Key).SequenceEqual(new[] { "cpu-temp", "gpu-temp", "storage-temp" }));
        check("监控页分组：mac 侧不出现主板温度（Windows 专属）",
            !macTemp.Any(r => r.Key == "board-temp"));
        check("监控页分组：两端温度卡行数一致（都是 3 行 —— 卡底不留空）",
            winTemp.Count == macTemp.Count && winTemp.Count == 3);

        check("监控页分组：上下速度卡两端都是下行 / 上行",
            MonitorPageLayoutSpec.RowsFor(speed, macOS: false).Select(r => r.Key)
                .SequenceEqual(new[] { "down", "up" })
            && MonitorPageLayoutSpec.RowsFor(speed, macOS: true).Select(r => r.Key)
                .SequenceEqual(new[] { "down", "up" }));

        // 两端一致的行标签与卡片标题（规格里的值就是屏幕上那串字）
        check("监控页分组：占用卡的行标签（CPU / 内存 / 磁盘）",
            MonitorPageLayoutSpec.RowsFor(usage, macOS: false).Select(r => r.Label)
                .SequenceEqual(new[] { "CPU", "内存", "磁盘" }));
        check("监控页分组：温度卡的行标签（CPU / GPU / SSD）",
            MonitorPageLayoutSpec.RowsFor(temp, macOS: true).Select(r => r.Label)
                .SequenceEqual(new[] { "CPU", "GPU", "SSD" }));
        check("监控页分组：Windows 温度卡的行标签（CPU / GPU / 主板）",
            MonitorPageLayoutSpec.RowsFor(temp, macOS: false).Select(r => r.Label)
                .SequenceEqual(new[] { "CPU", "GPU", "主板" }));
        check("监控页分组：卡片标题（占用 / 温度 / 上下速度 / 占用最高）",
            MonitorPageLayoutSpec.Cards.Select(c => c.Title)
                .SequenceEqual(new[] { "占用", "温度", "上下速度", "占用最高" }));
    }

    // ==================== Windows 端实际排版（产品代码算出来的行带） ====================

    private static void CheckWindowsLayout(Action<string, bool> check)
    {
        var cards = MonitorPage.PlannedCards(560, 420);
        var spec = MonitorPageLayoutSpec.CardsFor(macOS: false);

        check("监控页分组：Windows 端计划出四张卡，键与标题取自规格",
            cards.Count == 4
            && cards.Select(c => c.Key).SequenceEqual(spec.Select(c => c.Key))
            && cards.Select(c => c.Title).SequenceEqual(spec.Select(c => c.Title)));

        var usage = cards.First(c => c.Key == "usage");
        var temp = cards.First(c => c.Key == "temp");

        check("监控页分组：Windows 端占用卡三行（CPU / 内存 / 磁盘），行标签取自规格",
            usage.Rows.Where(r => r.Drawn).Select(r => r.Label).SequenceEqual(new[] { "CPU", "内存", "磁盘" }));

        check("监控页分组：Windows 端温度卡三行（CPU / GPU / 主板）",
            temp.Rows.Where(r => r.Drawn).Select(r => r.RowKey)
                .SequenceEqual(new[] { "cpu-temp", "gpu-temp", "board-temp" }));

        // 主板温度落在第三行（卡底不留空的那一行）
        check("监控页分组：Windows 端主板温度行在 GPU 温度行之下（第三行）",
            temp.Rows.First(r => r.RowKey == "board-temp").Rect.Top
            > temp.Rows.First(r => r.RowKey == "gpu-temp").Rect.Top);

        // SSD 温度在 Windows 上**没有位置**（那一格归主板温度）⇒ 不画、也没有行带
        var ssd = temp.Rows.Where(r => !r.Drawn).ToList();
        check("监控页分组：Windows 端温度卡里 SSD 温度没有行带（平台不支持 ⇒ 整行不出现）",
            ssd.Count == 1 && ssd[0].RowKey == "storage-temp" && ssd[0].Rect.IsEmpty);

        // 行带互不压住（每行一条带）
        bool disjoint = true;
        foreach (var card in cards)
        {
            var drawn = card.Rows.Where(r => r.Drawn).Select(r => r.Rect).ToList();
            for (int i = 1; i < drawn.Count; i++)
                disjoint &= drawn[i - 1].Bottom <= drawn[i].Top;
        }
        check("监控页分组：Windows 端每张卡的行带互不压住", disjoint);
    }

    // ==================== 源码静态检查：标题与行标签取自规格 ====================

    private static void CheckWindowsSource(Action<string, bool> check)
    {
        string? src = ReadSource("src", "Island.App", "Controls", "MonitorPage.cs");
        if (src is null)
        {
            check("监控页分组：源码静态检查（未定位到仓库根或源文件，已跳过）", true);
            return;
        }

        check("监控页分组：Windows 端引用了两端共享规格 MonitorPageLayoutSpec",
            src.Contains("MonitorPageLayoutSpec"));

        // 卡标题与行标签不许在代码里另写一份字符串。
        // 例外：排序分段控件的 "CPU" / "内存" 是**榜的排序项**，不是卡片标题/行标签。
        foreach (string literal in new[] { "\"占用\"", "\"温度\"", "\"磁盘\"", "\"GPU\"" })
        {
            check($"监控页分组：Windows 端源码里没有硬编码的 {literal}（必须取自规格）",
                !src.Contains(literal));
        }
    }

    // ==================== 小工具 ====================

    /// <summary>往上找仓库根（找带 Island.sln 的那一层）。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Island.sln"))) return dir.FullName;
        }
        return null;
    }

    /// <summary>读仓库里的一个源码文件（找不到返回 null）。</summary>
    private static string? ReadSource(params string[] parts)
    {
        if (RepoRoot is null) return null;
        string path = Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray());
        if (!File.Exists(path)) return null;
        return File.ReadAllText(path);
    }
}
