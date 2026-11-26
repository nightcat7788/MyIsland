using System;
using System.Linq;
using System.Reflection;
using Island.App.Controls;
using Island.Avalonia;
using Island.Tools.AvaloniaProbe;

namespace Island.Tests;

/// <summary>
/// **macOS 端离屏渲染视觉回归**的断言（2026-11-26 task-11）。
///
/// 解决的是和 Windows 端同一个盲区：mac 端此前**没有任何视觉验证** ——
/// 五轮跨端一致性对齐都以 mac 为基准，而基准端恰恰是唯一看不见的那一端，
/// 只能确认"编译通过"，看不到画出来是什么样。
///
/// 判定逻辑与 tools\avalonia-probe 是**同一份源码**（AvaloniaVisualCore.cs），
/// 免得"工具里过了、门禁里是另一套标准"这类漂移。
///
/// ⚠️ 为什么门禁里判的是**几何 + 判定逻辑**，而不是 mac 的真实像素：
///   tests\Island.Tests 是**零 NuGet 工程**（不能引入 Avalonia 包），
///   而 mac 的画布（IslandCanvas）是 Avalonia 控件 ⇒ 门禁进程**渲染不出** mac 的像素。
///   所以这里断言两件门禁**能**判的事：
///     ① **几何规格**（mac 与 Windows 两端逐项一致 —— 这正是"跨端一致性"本身）；
///     ② **判定逻辑本身有牙齿**（空画面必须判红、正常画面必须判绿）——
///        少了 ②，判定函数写成"永远通过"也能过门禁，探针就成了摆设。
///   真实像素的判定由探针（tools\avalonia-probe）执行，它跑同一份判定源码。
/// </summary>
internal static class AvaloniaVisualTests
{
    public static void Run(Action<string, bool> check)
    {
        CheckCrossEndGeometry(check);
        CheckGeometryInvariants(check);
        CheckJudgeHasTeeth(check);
    }

    // ==================== ① 跨端几何一致性（两端逐项比对） ====================

    /// <summary>
    /// mac 端的 <see cref="MonitorLayoutSpec"/> 与 Windows 端的
    /// <c>MonitorPage</c> 私有常量 / <see cref="ExpandedPanelSpec"/> 逐项比对。
    ///
    /// 为什么必须比：两端各写一份常量时，改了一边另一边不会知道 ——
    /// 2026-11-26 这一轮的对齐工作就是靠这种"抄一份再逐项核对"完成的，
    /// 手工核对过一次之后就该由机器守着。
    /// </summary>
    private static void CheckCrossEndGeometry(Action<string, bool> check)
    {
        // 与 Windows 端 ExpandedPanelSpec（两端共享的那份规格）比对
        check("跨端：内容区左右内边距一致（mac 18 = Windows 18）",
            MonitorLayoutSpec.ContentPadX == ExpandedPanelSpec.ContentPadX);
        check("跨端：内容区下内边距一致（mac 18 = Windows 18）",
            MonitorLayoutSpec.ContentPadBottom == ExpandedPanelSpec.ContentPadBottom);
        check("跨端：顶栏上边缘偏移一致（mac 14 = Windows 14）",
            MonitorLayoutSpec.TabTopOffset == ExpandedPanelSpec.TabTopOffset);
        check("跨端：顶栏到内容区距离一致（mac 40 = Windows 40）",
            MonitorLayoutSpec.ContentTopOffset == ExpandedPanelSpec.ContentTopOffset);
        check("跨端：设备卡片尺寸一致（132×150 / 间距 12）",
            MonitorLayoutSpec.DeviceCardWidth == ExpandedPanelSpec.DeviceCardWidth &&
            MonitorLayoutSpec.DeviceCardHeight == ExpandedPanelSpec.DeviceCardHeight &&
            MonitorLayoutSpec.DeviceCardGap == ExpandedPanelSpec.DeviceCardGap);

        // 与 Windows 端 MonitorPage 的私有常量比对（用反射读，跟 Windows 探针同一手法）
        // mac 端展开面板的实际尺寸（IslandWindow 用的）必须等于规格里的值 ——
        // 探针按规格渲染，窗口按自己的常量布局；两者不一致 ⇒ 探针验的不是真机上的那个面板。
        check("mac：展开面板尺寸与规格一致（560×455）",
            MonitorLayoutSpec.ExpandedPanelWidth == 560 &&
            MonitorLayoutSpec.MonitorPanelHeight == 455);

        check("跨端：监控页栅格常量一致（列间距 / 卡头 / 行高 / 行距 / 上下排卡高）",
            ConstOf(typeof(MonitorPage), "GridPad") == MonitorLayoutSpec.ContentPadX &&
            ConstOf(typeof(MonitorPage), "GridGap") == MonitorLayoutSpec.GridGap &&
            ConstOf(typeof(MonitorPage), "CardHeadH") == MonitorLayoutSpec.CardHeadH &&
            ConstOf(typeof(MonitorPage), "RowH") == MonitorLayoutSpec.RowH &&
            ConstOf(typeof(MonitorPage), "RowGap") == MonitorLayoutSpec.RowGap &&
            ConstOf(typeof(MonitorPage), "SpeedRowH") == MonitorLayoutSpec.SpeedRowH &&
            ConstOf(typeof(MonitorPage), "TopCardH") == MonitorLayoutSpec.TopCardH &&
            ConstOf(typeof(MonitorPage), "BotCardH") == MonitorLayoutSpec.BottomCardH);
    }

    /// <summary>读一个 const 字段的真实值（跟 tools\visual-probe 同一手法）。</summary>
    private static double ConstOf(Type type, string name)
    {
        FieldInfo? field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        if (field == null) throw new InvalidOperationException($"{type.Name} 里找不到常量 {name}");
        object? value = field.GetRawConstantValue();
        if (value is not double d) throw new InvalidOperationException($"{type.Name}.{name} 不是 double");
        return d;
    }

    // ==================== ② 几何不变量（纯算术，门禁直接跑） ====================

    /// <summary>
    /// 跑共享判定核心里的**几何**判定（不碰像素），断言全部成立。
    /// 这与探针跑的是同一份代码。
    /// </summary>
    private static void CheckGeometryInvariants(Action<string, bool> check)
    {
        var report = new ProbeReport();
        AvaloniaVisualCore.CheckMonitorGeometry(report, MonitorLayoutSpec.ExpandedPanelWidth,
                                               MonitorLayoutSpec.MonitorPanelHeight);
        AvaloniaVisualCore.CheckDevicesGeometry(report, MonitorLayoutSpec.ExpandedPanelWidth, 5);
        foreach (var c in report.Checks)
        {
            check(c.Name, c.Passed);
            if (!c.Passed) Console.WriteLine("      " + c.Detail);
        }
    }

    // ==================== ③ 判定逻辑本身有牙齿 ====================

    /// <summary>
    /// 用**合成出来的画面**喂给共享判定核心，断言它该判红的判红、该判绿的判绿。
    ///
    /// 为什么必须做：判定函数要是写成"永远通过"，几何断言照样全绿，
    /// 探针也永远退出 0 ⇒ 探针就成了一件摆设（Windows 端当初为了防这个，
    /// 才把"画面非全黑"做成硬断言）。这里用同一个道理把它钉死。
    /// </summary>
    private static void CheckJudgeHasTeeth(Action<string, bool> check)
    {
        var palette = new ProbeColor(0x1C, 0x1C, 0x1E);   // 面板底色（Dark 主题）
        var elevated = new ProbeColor(0x2A, 0x2A, 0x2D);  // 卡片底色
        var text = new ProbeColor(0xF2, 0xF2, 0xF7);      // 文字色

        double w = MonitorLayoutSpec.ExpandedPanelWidth;
        double h = MonitorLayoutSpec.MonitorPanelHeight;

        // ---- 空画面：必须判"全黑 / 无内容 / 没有卡片 / 没有状态行" ----
        //
        // 只挑**应当**判红的那几条断言，不要求"每一条都红"：
        // 间隙检查（"间隙里没有卡片底色"）在空画面上本来就成立 —— 那是它的语义，
        // 不是它失效了。把"每条都红"当断言反而会逼着人写出错误的断言。
        var blank = ProbeImage.Solid((int)w, (int)h, palette);
        var blankReport = new ProbeReport();
        AvaloniaVisualCore.CheckMonitorPage(blankReport, blank,
            new ProbePalette(palette, elevated, text), w, h);
        foreach (var keyword in new[]
                 {
                     "渲染结果非全黑",
                     "有实际内容",
                     "张卡片画在规格位置",
                     "底部状态行存在",
                     "底部状态行左缘",
                 })
        {
            var c = blankReport.Checks.FirstOrDefault(x => x.Name.Contains(keyword));
            check("判定逻辑：空画面被判红（" + keyword + "）", c is { Passed: false });
        }

        // ---- 正常画面：必须全绿 ----
        var good = PaintSyntheticMonitorPage(w, h, palette, elevated, text);
        var goodReport = new ProbeReport();
        AvaloniaVisualCore.CheckMonitorPage(goodReport, good,
            new ProbePalette(palette, elevated, text), w, h);
        foreach (var c in goodReport.Checks)
        {
            check("判定逻辑：正常画面被判绿（" + c.Name + "）", c.Passed);
            if (!c.Passed) Console.WriteLine("      " + c.Detail);
        }

        // ---- 状态行压栅格：必须被判红 ----
        var overlap = PaintSyntheticMonitorPage(w, h, palette, elevated, text);
        double statusTop = MacVisualGeometry.MonitorStatusTop(w, h);
        Fill(overlap, 0, (int)statusTop, (int)w, (int)(statusTop + 14), elevated);
        var overlapReport = new ProbeReport();
        AvaloniaVisualCore.CheckMonitorPage(overlapReport, overlap,
            new ProbePalette(palette, elevated, text), w, h);
        var pressCheck = overlapReport.Checks.FirstOrDefault(c => c.Name.Contains("不压栅格"));
        check("判定逻辑：状态行压到栅格时被判红", pressCheck is { Passed: false });
    }

    /// <summary>
    /// 按共享规格画一张"看起来正常"的监控页（合成画面，不是产品渲染）。
    /// 画什么：面板底色 + 四张卡片底色 + 卡内文字条 + 底部状态行文字。
    /// </summary>
    private static ProbeImage PaintSyntheticMonitorPage(double w, double h, ProbeColor palette,
                                                        ProbeColor elevated, ProbeColor text)
    {
        var img = ProbeImage.Solid((int)w, (int)h, palette);
        var cards = MacVisualGeometry.MonitorCards(w);
        foreach (var card in cards)
        {
            Fill(img, (int)card.X, (int)card.Y, (int)card.Right, (int)card.Bottom, elevated);
            // 卡内"文字条"：每张卡画三条，模拟三行读数（也提供足够的内容像素）
            for (int row = 0; row < 3; row++)
            {
                int y = (int)(card.Y + 50 + row * 40);
                Fill(img, (int)(card.X + 30), y, (int)(card.X + 30 + card.Width - 60), y + 5, text);
            }
        }
        // 底部状态行：一行文字，从内容区左内缩处起画（与产品一致）
        double statusTop = MacVisualGeometry.MonitorStatusTop(w, h);
        Fill(img, (int)MonitorLayoutSpec.ContentPadX, (int)statusTop,
             (int)MonitorLayoutSpec.ContentPadX + 120, (int)statusTop + 9, text);
        return img;
    }

    /// <summary>填一块矩形。</summary>
    private static void Fill(ProbeImage img, int x0, int y0, int x1, int y1, ProbeColor color)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(img.Height, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(img.Width, x1); x++)
                img.Set(x, y, color);
    }
}
