using System.IO;
using System.Text;
using Island.App.Themes;
using Island.Tools.VisualProbe;

namespace Island.Tools.VisualProbe;

/// <summary>
/// 离屏渲染视觉回归探针（开发期专用，不随产品发布）。
///
/// 用法：
///   visual-probe [--out &lt;目录&gt;] [--palette Dark|OneDark|...]
///
/// 做什么：把展开面板的监控页 / 设备页**离屏**渲染成 PNG，并对渲染结果判定一组
/// 可自动断言的不变量（非全黑、2×2 卡片栅格几何、卡片不重叠不越界、文本不越出卡片……）。
/// 全部通过 ⇒ 退出码 0；任一失败 ⇒ 退出码非 0。
///
/// 为什么需要它：本机屏幕捕获在锁屏会话里只能拍到全黑，没法做真机视觉确认。
/// 离屏渲染（不建窗口，元素 Measure/Arrange 后直接 Render）不依赖桌面，
/// 于是"画面是不是空的"变成了机器能判的门禁。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string? outDir = null;
        string paletteName = "Dark";
        string? appDll = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("--out 需要目录参数"); return 2; }
                    outDir = args[++i];
                    break;
                case "--palette":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("--palette 需要主题名"); return 2; }
                    paletteName = args[++i];
                    break;
                case "--app-dll":
                    if (i + 1 >= args.Length) { Console.Error.WriteLine("--app-dll 需要程序集路径"); return 2; }
                    appDll = args[++i];
                    break;
                case "--help":
                case "-h":
                    Usage();
                    return 0;
                default:
                    Console.Error.WriteLine("未知参数：" + args[i]);
                    Usage();
                    return 2;
            }
        }

        var palette = Palette.ByName(paletteName) ?? Palette.Dark;
        outDir ??= DefaultOutputDirectory();

        Console.WriteLine("离屏渲染视觉探针");
        Console.WriteLine($"  主题：{paletteName}    输出：{outDir}");
        Console.WriteLine();

        ProbeReport report;
        try
        {
            report = VisualProbeCore.Run(new ProbeOptions
            {
                Palette = palette,
                OutputDirectory = outDir,
            });

            // 展开面板整体：可选（需要 Island.App 程序集才能解析它的 XAML 资源 URI）。
            // 渲染不出来就如实报告"没渲染出来"，不拿全黑图充数，也不把它算成失败
            // （它本来就不在门禁覆盖范围内 —— 门禁只覆盖两个纯绘制页面）。
            if (appDll is not null)
            {
                var panelBitmap = VisualProbeCore.TryRenderExpandedPanel(appDll, 540, 400, palette, out string? panelError);
                if (panelBitmap is null)
                {
                    Console.WriteLine("SKIP: 展开面板整体未渲染 —— " + (panelError ?? "原因未知"));
                }
                else
                {
                    // 落盘（Run 内部的保存循环已经跑完了，这里单独存）
                    Directory.CreateDirectory(outDir);
                    string panelPath = Path.Combine(outDir, "expanded-panel.png");
                    VisualProbeCore.SavePng(panelBitmap, panelPath);

                    double ratio = VisualProbeCore.PaintedRatioOf(panelBitmap, palette.Surface);
                    Console.WriteLine((ratio > 0.02 ? "PASS: " : "FAIL: ")
                        + $"展开面板整体：渲染结果非全黑（非背景像素占比 = {ratio:P2}）");
                    if (ratio <= 0.02) report.Checks.Add(new ProbeCheck(
                        "展开面板整体：渲染结果非全黑", false, $"非背景像素占比 {ratio:P2}"));
                    Console.WriteLine($"  PNG: {panelPath}");
                }
            }
        }
        catch (Exception ex)
        {
            // 渲染失败就是失败：不许用默认值凑一张"看起来正常"的图。
            Console.Error.WriteLine("FAIL: 离屏渲染抛异常 —— " + ex);
            return 1;
        }

        int failed = 0;
        foreach (var check in report.Checks)
        {
            Console.WriteLine((check.Passed ? "PASS: " : "FAIL: ") + check.Name);
            Console.WriteLine("      " + check.Detail);
            if (!check.Passed) failed++;
        }

        Console.WriteLine();
        foreach (var (name, _) in report.Images)
            Console.WriteLine($"  PNG: {Path.Combine(outDir, name + ".png")}");

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"全部通过：{report.Checks.Count} 条"
            : $"失败 {failed} 条（共 {report.Checks.Count} 条）");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 默认输出目录：用户临时目录下的 MyIsland-visual。
    /// 为什么不放仓库里：输出的是 PNG 产物（每次跑都变），放进仓库会变成一堆未跟踪文件。
    /// 想放到别处用 --out 指定。
    /// </summary>
    private static string DefaultOutputDirectory()
        => Path.Combine(Path.GetTempPath(), "MyIsland-visual");

    private static void Usage()
    {
        Console.WriteLine("用法：visual-probe [--out <目录>] [--palette <主题名>]");
        Console.WriteLine("  --out      PNG 输出目录（默认：%TEMP%\\MyIsland-visual）");
        Console.WriteLine("  --palette  主题名（Dark / OneDark / Dracula / Pixel …，默认 Dark）");
    }
}
