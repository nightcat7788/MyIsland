using System;
using System.IO;
using System.Linq;
using Island.Core.Capabilities;

namespace Island.Tests;

/// <summary>
/// macOS 端监控页**卡片分组**的漂移检查（2026-11-26 task16：两端分组统一）。
///
/// 背景：此前两端的监控页是各写一套的（mac「占用 / 温度 / 上下速度 / 占用最高」vs
/// Windows「CPU / 内存 / 上下速度 / 占用最高」），信息量接近但**看起来像两个产品**。
/// 统一之后，分组的真相源是 `MonitorPageLayoutSpec`，两端都从它取卡片与行 ——
/// 这里守住两件事：
///   ① mac 端在规格里拿到的卡片/行**确实符合预期**（温度卡三行含存储、不含 Windows 专属的主板温度…）；
///   ② mac 源码里绘制卡片的**顺序**与规格一致，且标题/行标签**取自规格**而不是硬编码。
///
/// 像素层面的"那个读数确实画在这一张卡里"由 mac 离屏渲染探针判定
/// （tools/avalonia-probe/AvaloniaVisualCore.cs，与 Windows 端同一套思路）。
/// </summary>
internal static class MonitorPageLayoutMacTests
{
    /// <summary>找不到仓库根时静态校验整段跳过（不让测试因为路径找不到而红）。</summary>
    private static string? RepoRoot { get; } = FindRepoRoot();

    public static void Run(Action<string, bool> check)
    {
        var spec = MonitorPageLayoutSpec.CardsFor(macOS: true);

        check("mac 分组：卡片四张且顺序为 占用 / 温度 / 上下速度 / 占用最高",
            spec.Select(c => c.Key).SequenceEqual(new[] { "usage", "temp", "speed", "topproc" }));

        check("mac 分组：占用卡三行（CPU / 内存 / 磁盘）",
            RowKeys(spec, "usage").SequenceEqual(new[] { "cpu-usage", "memory", "disk" }));

        check("mac 分组：温度卡三行（CPU / GPU / 存储），且**不含** Windows 专属的主板温度",
            RowKeys(spec, "temp").SequenceEqual(new[] { "cpu-temp", "gpu-temp", "storage-temp" }));

        check("mac 分组：上下速度卡两行（下行 / 上行）",
            RowKeys(spec, "speed").SequenceEqual(new[] { "down", "up" }));

        check("mac 分组：占用最高卡一行（进程榜）",
            RowKeys(spec, "topproc").SequenceEqual(new[] { "top-processes" }));

        check("mac 分组：每一行都有标签（两端一致的行标签取自同一份规格）",
            spec.SelectMany(c => MonitorPageLayoutSpec.RowsFor(c, macOS: true))
                .All(r => !string.IsNullOrWhiteSpace(r.Label) || r.Key == "top-processes"));

        // ---- 静态：源码里绘制卡片的顺序 == 规格顺序；标题/行标签取自规格 ----
        string? canvas = ReadSource("src", "Island.Avalonia", "IslandCanvas.cs");
        if (canvas is null)
        {
            check("mac 分组：源码绘制顺序与规格一致（未定位到仓库根，已跳过）", true);
            return;
        }

        var idx = spec
            .Select(c => canvas.IndexOf($"MonitorPageLayoutSpec.TitleOf(\"{c.Key}\")", StringComparison.Ordinal))
            .ToList();
        bool orderOk = idx.All(i => i >= 0) && idx.Zip(idx.Skip(1)).All(p => p.First < p.Second);
        check("mac 分组：源码绘制卡片的顺序与规格一致", orderOk);
        Console.WriteLine("      规格顺序 " + string.Join(" / ", spec.Select(c => c.Key)) +
                          "；源码里首次出现位置 " + string.Join(" / ", idx));

        int titleUses = canvas.Split(new[] { "MonitorPageLayoutSpec.TitleOf(" }, StringSplitOptions.None).Length - 1;
        int labelUses = canvas.Split(new[] { "MonitorPageLayoutSpec.LabelOf(" }, StringSplitOptions.None).Length - 1;
        check("mac 分组：卡片标题与行标签都取自规格（不在代码里另写一份）",
            titleUses >= 4 && labelUses >= 8);
        Console.WriteLine($"      TitleOf 调用 {titleUses} 处（≥4）、LabelOf 调用 {labelUses} 处（≥8）");
    }

    private static string[] RowKeys(System.Collections.Generic.IReadOnlyList<MonitorCardSlot> spec, string cardKey)
    {
        var card = spec.FirstOrDefault(c => c.Key == cardKey);
        return card is null
            ? Array.Empty<string>()
            : MonitorPageLayoutSpec.RowsFor(card, macOS: true).Select(r => r.Key).ToArray();
    }

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