using System;
using System.Collections.Generic;
using System.Linq;
using Island.Avalonia;
using Island.Core.Capabilities;

namespace Island.Tools.AvaloniaProbe;

/// <summary>
/// 一条渲染不变量的判定结果（名字 / 是否通过 / 实测数值）。
/// </summary>
public sealed record ProbeCheck(string Name, bool Passed, string Detail);

/// <summary>
/// 探针产物：若干条不变量判定。
/// </summary>
public sealed class ProbeReport
{
    public List<ProbeCheck> Checks { get; } = new();

    /// <summary>全部不变量都通过（探针退出码 / 门禁都看它）。</summary>
    public bool AllPassed => Checks.All(c => c.Passed);
}

/// <summary>一个颜色（纯字节三元组，避开 Avalonia 的 Color —— 本文件要能链进零 NuGet 的测试工程）。</summary>
public readonly record struct ProbeColor(byte R, byte G, byte B)
{
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>探针用到的几个主题色（由宿主从产品主题取，探针自己不该知道主题长什么样）。</summary>
public sealed record ProbePalette(ProbeColor Surface, ProbeColor Elevated, ProbeColor Text);

/// <summary>
/// 一张离屏渲染出来的位图（BGRA 字节缓冲，**不依赖 Avalonia**）。
///
/// 为什么自己包一层而不是直接用 Avalonia 的 RenderTargetBitmap：
/// 本文件是**探针与门禁共用的判定源码**，而 tests\Island.Tests 是零 NuGet 工程
/// （不能引用 Avalonia 包）。所以判定逻辑只认"像素长什么样"，不认"像素从哪来"——
/// 探针用 Avalonia 渲出来喂给它，门禁用自己拼的缓冲喂给它。
/// </summary>
public sealed class ProbeImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>BGRA，每像素 4 字节，行优先。</summary>
    public byte[] Pixels { get; }

    public ProbeImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height * 4)
            throw new ArgumentException($"像素缓冲长度 {pixels.Length} 与尺寸 {width}×{height} 不符");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>整张图填成同一个颜色。</summary>
    public static ProbeImage Solid(int width, int height, ProbeColor color)
    {
        var px = new byte[width * height * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = color.B; px[i + 1] = color.G; px[i + 2] = color.R; px[i + 3] = 255;
        }
        return new ProbeImage(width, height, px);
    }

    /// <summary>取一个像素（B,G,R,A）。</summary>
    public (byte B, byte G, byte R) At(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (Pixels[i], Pixels[i + 1], Pixels[i + 2]);
    }

    /// <summary>写一个像素（B,G,R,A）。</summary>
    public void Set(int x, int y, ProbeColor color, byte alpha = 255)
    {
        int i = (y * Width + x) * 4;
        Pixels[i] = color.B; Pixels[i + 1] = color.G; Pixels[i + 2] = color.R; Pixels[i + 3] = alpha;
    }
}

/// <summary>
/// macOS 端展开面板 / 收起态的**几何规格**（纯计算）。
///
/// 数值全部取自 <see cref="MonitorLayoutSpec"/>（产品绘制代码用的同一份），
/// 所以探针断言的是"产品真实画出来的布局"，不是探针自己抄的一份。
/// </summary>
public static class MacVisualGeometry
{
    /// <summary>监控页内容区顶边（相对面板上边缘）。</summary>
    public static double ContentTop => MonitorLayoutSpec.ContentTop;

    /// <summary>监控页 2×2 栅格的四张卡片（顺序：左上 / 右上 / 左下 / 右下）。</summary>
    public static LayoutRect[] MonitorCards(double panelWidth)
        => MonitorLayoutSpec.MonitorCards(panelWidth, ContentTop);

    /// <summary>监控页栅格底边（卡片区结束处）。</summary>
    public static double MonitorGridBottom => MonitorLayoutSpec.MonitorGridBottom(ContentTop);

    /// <summary>
    /// 监控页底部状态行的顶边。
    ///
    /// 与产品实现同式：<c>min(栅格底边 + TopMargin, 面板底边 - 内缩 - LineHeight)</c>。
    /// </summary>
    public static double MonitorStatusTop(double panelWidth, double panelHeight)
        => Math.Min(MonitorGridBottom + MonitorStatusLineSpec.TopMargin,
                    panelHeight - MonitorLayoutSpec.ContentPadX - MonitorStatusLineSpec.LineHeight);

    /// <summary>设备页的卡片（顺序：逐行从左到右）。</summary>
    public static LayoutRect[] DeviceCards(double panelWidth, int deviceCount)
        => MonitorLayoutSpec.DeviceCards(panelWidth, ContentTop, deviceCount);
}

/// <summary>
/// **离屏渲染视觉回归的判定核心** —— 探针（tools\avalonia-probe）与门禁（tests\Island.Tests）
/// 共用的**同一份源码**。
///
/// 为什么要共用：探针自己判、自己说了算的话，"工具里过了、门禁里是另一套标准"这类漂移
/// 就会发生（Windows 端 <c>tools\visual-probe\VisualProbeCore.cs</c> 就是这么被
/// tests\Island.Tests 链进去的）。本文件因此**不依赖 Avalonia**（只有纯 .NET），
/// 于是零 NuGet 的测试工程可以直接 <c>Compile Include</c> 它。
///
/// 分工：
///   · 探针用 Avalonia 真渲染出像素 → 喂给本文件判定（PNG 产物 + 退出码）；
///   · 门禁把本文件链进去 → 断言几何规格、以及"判定逻辑本身没被写废"
///     （拿自己拼的缓冲喂它，看它该判过就判过、该判红就判红）。
///
/// 设计要点（都是踩过的坑，别改回去）：
///   · **"有绘制"与"有内容"是两个阈值**：macOS 的卡片底色（Elevated #2A2A2D）离面板底色
///     （Surface #1C1C1E）只有 14，用"离底色差 ≥ 2"当"有绘制"会把整张卡片算成画上了，
///     判不出"空壳页"。所以另设一个"内容"阈值（离底色差 ≥ 40）：卡片底色、卡片分隔线、
///     进度槽都不算内容，文字 / 图标 / 仪表填充才算。
///   · **卡片用底色认，不靠描边**：macOS 是 Apple 风格，卡片**没有描边**（只有浅色主题才有
///     极淡轮廓），所以判卡片位置只能靠它的底色块 —— 描边检测在 macOS 上是行不通的。
///   · 状态行画在**面板底色**上（不是卡片上），所以判它存在要看"离底色的差"，
///     同时要确认它**没压在卡片上**（带内无卡片底色）。
/// </summary>
public static class AvaloniaVisualCore
{
    // ---------- 判定阈值（实测值都留了余量） ----------

    /// <summary>"有绘制"阈值：与面板底色的通道最大差 ≥ 2。</summary>
    public const int PaintedDelta = 2;

    /// <summary>
    /// "有内容"阈值：与面板底色的通道最大差 ≥ 40。
    /// 卡片底色差 14、卡片分隔线差 25、进度槽差 30 —— 全都低于它；
    /// 文字（差 87+）、图标与仪表填充（差 100+）高于它。
    /// </summary>
    public const int ContentDelta = 40;

    /// <summary>整体不变量：非背景像素占比下限（防"整张全黑"）。</summary>
    public const double MinPaintedRatio = 0.02;

    /// <summary>整体不变量：内容像素占比下限（防"只画出卡片底、没有文字"）。</summary>
    public const double MinContentRatio = 0.005;

    /// <summary>卡片底色占比下限（判"这块确实是一张卡片"）。</summary>
    public const double MinCardFillRatio = 0.40;

    /// <summary>间隙里允许的卡片底色占比上限（判"卡片之间确实留了空"）。</summary>
    public const double MaxGapFillRatio = 0.02;

    /// <summary>状态行墨迹簇的最小横向间隔（把两个字段位分开、但同一字段位内部的空格不算断开）。</summary>
    public const int StatusSlotMinGap = 8;

    // ==================== 通用像素统计 ====================

    /// <summary>像素与参考色的通道最大差。</summary>
    public static int Delta(ProbeImage img, int x, int y, ProbeColor reference)
    {
        var (b, g, r) = img.At(x, y);
        return Math.Max(Math.Abs(r - reference.R), Math.Max(Math.Abs(g - reference.G), Math.Abs(b - reference.B)));
    }

    /// <summary>像素与参考色的通道最大差（区域版，区域外的像素算 0）。</summary>
    public static int MaxDelta(ProbeImage img, LayoutRect rect, ProbeColor reference)
    {
        int max = 0;
        int x0 = Math.Max(0, (int)Math.Floor(rect.X));
        int y0 = Math.Max(0, (int)Math.Floor(rect.Y));
        int x1 = Math.Min(img.Width - 1, (int)Math.Ceiling(rect.Right) - 1);
        int y1 = Math.Min(img.Height - 1, (int)Math.Ceiling(rect.Bottom) - 1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                max = Math.Max(max, Delta(img, x, y, reference));
        return max;
    }

    /// <summary>区域里"有绘制"（离参考色差 ≥ PaintedDelta）的像素占比。</summary>
    public static double PaintedRatio(ProbeImage img, LayoutRect rect, ProbeColor reference, int minDelta = PaintedDelta)
    {
        int total = 0, painted = 0;
        int x0 = Math.Max(0, (int)Math.Floor(rect.X));
        int y0 = Math.Max(0, (int)Math.Floor(rect.Y));
        int x1 = Math.Min(img.Width - 1, (int)Math.Ceiling(rect.Right) - 1);
        int y1 = Math.Min(img.Height - 1, (int)Math.Ceiling(rect.Bottom) - 1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                total++;
                if (Delta(img, x, y, reference) >= minDelta) painted++;
            }
        return total == 0 ? 0 : (double)painted / total;
    }

    /// <summary>区域里"有内容"（离参考色差 ≥ ContentDelta）的像素占比。</summary>
    public static double ContentRatio(ProbeImage img, LayoutRect rect, ProbeColor reference)
        => PaintedRatio(img, rect, reference, ContentDelta);

    /// <summary>整张图的"有绘制"占比。</summary>
    public static double PaintedRatio(ProbeImage img, ProbeColor reference)
        => PaintedRatio(img, new LayoutRect(0, 0, img.Width, img.Height), reference);

    /// <summary>整张图的"有内容"占比。</summary>
    public static double ContentRatio(ProbeImage img, ProbeColor reference)
        => ContentRatio(img, new LayoutRect(0, 0, img.Width, img.Height), reference);

    /// <summary>区域里"近似卡片底色"的像素占比（判卡片块用）。</summary>
    public static double CardFillRatio(ProbeImage img, LayoutRect rect, ProbeColor elevated, int tolerance = 6)
    {
        int total = 0, fill = 0;
        int x0 = Math.Max(0, (int)Math.Floor(rect.X));
        int y0 = Math.Max(0, (int)Math.Floor(rect.Y));
        int x1 = Math.Min(img.Width - 1, (int)Math.Ceiling(rect.Right) - 1);
        int y1 = Math.Min(img.Height - 1, (int)Math.Ceiling(rect.Bottom) - 1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                total++;
                if (Delta(img, x, y, elevated) <= tolerance) fill++;
            }
        return total == 0 ? 0 : (double)fill / total;
    }

    /// <summary>
    /// 把区域里的"内容"像素按横向切成若干簇（相邻簇之间至少隔 <paramref name="minGap"/> 像素）。
    /// 返回每簇的 (左, 右, 顶, 底)。
    /// </summary>
    public static List<(int X0, int X1, int Y0, int Y1)> ContentClusters(
        ProbeImage img, LayoutRect rect, ProbeColor reference, int minGap, int minDelta = ContentDelta)
    {
        int x0 = Math.Max(0, (int)Math.Floor(rect.X));
        int y0 = Math.Max(0, (int)Math.Floor(rect.Y));
        int x1 = Math.Min(img.Width - 1, (int)Math.Ceiling(rect.Right) - 1);
        int y1 = Math.Min(img.Height - 1, (int)Math.Ceiling(rect.Bottom) - 1);

        // 逐列：这一列里有没有内容像素
        var colHasContent = new bool[Math.Max(0, x1 - x0 + 1)];
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                if (Delta(img, x, y, reference) >= minDelta) { colHasContent[x - x0] = true; break; }

        var clusters = new List<(int, int, int, int)>();
        int i = 0;
        while (i < colHasContent.Length)
        {
            if (!colHasContent[i]) { i++; continue; }
            int start = i;
            int end = i;
            // 簇的右边界：往后找，连续有内容的列；允许小于 minGap 的空洞留在簇内
            int gap = 0;
            int j = i;
            while (j + 1 < colHasContent.Length)
            {
                if (colHasContent[j + 1]) { end = j + 1; gap = 0; j++; continue; }
                gap++;
                if (gap >= minGap) break;      // 空洞够宽 ⇒ 换一簇
                j++;
            }
            // 簇的上下边界
            int top = int.MaxValue, bottom = int.MinValue;
            for (int x = x0 + start; x <= x0 + end; x++)
                for (int y = y0; y <= y1; y++)
                    if (Delta(img, x, y, reference) >= minDelta)
                    {
                        if (y < top) top = y;
                        if (y > bottom) bottom = y;
                    }
            if (top <= bottom) clusters.Add((x0 + start, x0 + end, top, bottom));
            i = end + 1;
        }
        return clusters;
    }

    // ==================== 几何判定（不需要像素，探针与门禁都能跑） ====================

    /// <summary>
    /// 监控页 2×2 卡片栅格的几何不变量：四张卡互不重叠、都在内容区内。
    /// 这条是纯算术 —— 门禁直接跑它，探针也跑它（两边同一份）。
    /// </summary>
    public static void CheckMonitorGeometry(ProbeReport report, double panelWidth, double panelHeight)
    {
        var cards = MacVisualGeometry.MonitorCards(panelWidth);
        var content = new LayoutRect(MonitorLayoutSpec.ContentPadX, MacVisualGeometry.ContentTop,
                                     panelWidth - 2 * MonitorLayoutSpec.ContentPadX,
                                     panelHeight - MacVisualGeometry.ContentTop - MonitorLayoutSpec.ContentPadBottom);

        bool disjoint = true;
        for (int i = 0; i < cards.Length; i++)
            for (int j = i + 1; j < cards.Length; j++)
                if (cards[i].Intersects(cards[j])) disjoint = false;

        report.Checks.Add(new ProbeCheck(
            "监控页：2×2 卡片栅格几何（四卡互不重叠、都在内容区内）",
            disjoint && cards.All(content.Contains),
            $"卡片 {string.Join(" / ", cards)}；内容区 {content}"));

        double cardW = cards[0].Width;
        report.Checks.Add(new ProbeCheck(
            "监控页：卡片宽度 = (面板宽 - 2×内缩 - 列间距)/2",
            Math.Abs(cardW - MonitorLayoutSpec.MonitorCardWidth(panelWidth)) < 0.001,
            $"面板宽 {panelWidth} ⇒ 卡片宽 {cardW}"));

        double statusTop = MacVisualGeometry.MonitorStatusTop(panelWidth, panelHeight);
        bool statusBelowGrid = statusTop >= MacVisualGeometry.MonitorGridBottom;
        bool statusInsidePanel = statusTop + MonitorStatusLineSpec.LineHeight <= panelHeight;

        report.Checks.Add(new ProbeCheck(
            "监控页：底部状态行在栅格之下、且不越出面板",
            statusBelowGrid && statusInsidePanel,
            $"栅格底边 {MacVisualGeometry.MonitorGridBottom:F0}；状态行顶边 {statusTop:F0}，" +
            $"行高 {MonitorStatusLineSpec.LineHeight} ⇒ 底边 {statusTop + MonitorStatusLineSpec.LineHeight:F0}；面板高 {panelHeight:F0}"));
    }

    /// <summary>
    /// 设备页卡片的几何不变量：卡片互不重叠、间距正好是规格值、都在内容区内。
    /// </summary>
    public static void CheckDevicesGeometry(ProbeReport report, double panelWidth, int deviceCount)
    {
        var cards = MacVisualGeometry.DeviceCards(panelWidth, deviceCount);
        bool disjoint = true;
        double minGap = double.MaxValue;
        for (int i = 0; i < cards.Length; i++)
            for (int j = i + 1; j < cards.Length; j++)
            {
                if (cards[i].Intersects(cards[j])) disjoint = false;
                double gap = Math.Min(Math.Abs(cards[i].X - cards[j].Right), Math.Abs(cards[j].X - cards[i].Right));
                if (gap >= 0 && gap < minGap) minGap = gap;
            }

        report.Checks.Add(new ProbeCheck(
            "设备页：卡片互不重叠",
            disjoint,
            $"{cards.Length} 张卡片：{string.Join(" / ", cards)}"));

        report.Checks.Add(new ProbeCheck(
            "设备页：卡片间距 = 12",
            cards.Length < 2 || Math.Abs(minGap - MonitorLayoutSpec.DeviceCardGap) < 0.001,
            $"实测最小间距 {minGap:F0}（规格 {MonitorLayoutSpec.DeviceCardGap}）"));
    }

    // ==================== 像素判定（探针跑；门禁拿合成缓冲验证判定逻辑本身） ====================

    /// <summary>
    /// 监控页的像素不变量（非全黑 / 有内容 / 四张卡片落在规格位置 / 卡片间留白 /
    /// 底部状态行存在且不压栅格 / 状态行左缘对齐）。
    /// </summary>
    public static void CheckMonitorPage(ProbeReport report, ProbeImage img, ProbePalette pal,
                                        double panelWidth, double panelHeight)
    {
        var full = new LayoutRect(0, 0, img.Width, img.Height);

        double painted = PaintedRatio(img, full, pal.Surface);
        report.Checks.Add(new ProbeCheck(
            "监控页：渲染结果非全黑（非背景像素占比 > 2%）",
            painted > MinPaintedRatio,
            $"非背景像素占比 {painted:P2}（下限 {MinPaintedRatio:P0}）"));

        double content = ContentRatio(img, full, pal.Surface);
        report.Checks.Add(new ProbeCheck(
            "监控页：有实际内容（文字/图标/仪表像素占比 > 0.5%）",
            content > MinContentRatio,
            $"内容像素占比 {content:P3}（下限 {MinContentRatio:P1}）"));

        var cards = MacVisualGeometry.MonitorCards(panelWidth);
        for (int i = 0; i < cards.Length; i++)
        {
            double fill = CardFillRatio(img, cards[i], pal.Elevated);
            report.Checks.Add(new ProbeCheck(
                $"监控页：第 {i + 1} 张卡片画在规格位置（卡片底色占比 > 40%）",
                fill > MinCardFillRatio,
                $"规格 {cards[i]} ⇒ 卡片底色占比 {fill:P1}（下限 {MinCardFillRatio:P0}）"));
        }

        // 卡片之间的留白：列间与行间都必须是面板底色（不能有卡片底色漏出来）
        double colGapFill = CardFillRatio(img,
            new LayoutRect(cards[0].Right, cards[1].X, cards[1].X - cards[0].Right, cards[0].Height),
            pal.Elevated);
        report.Checks.Add(new ProbeCheck(
            "监控页：两列之间留白 12 像素（间隙里没有卡片底色）",
            colGapFill <= MaxGapFillRatio,
            $"列间隙 x∈[{cards[0].Right:F0},{cards[1].X:F0}) ⇒ 卡片底色占比 {colGapFill:P2}（上限 {MaxGapFillRatio:P0}）"));

        double rowGapFill = CardFillRatio(img,
            new LayoutRect(cards[0].X, cards[0].Bottom, cards[0].Width, cards[2].Y - cards[0].Bottom),
            pal.Elevated);
        report.Checks.Add(new ProbeCheck(
            "监控页：两排之间留白 12 像素（间隙里没有卡片底色）",
            rowGapFill <= MaxGapFillRatio,
            $"排间隙 y∈[{cards[0].Bottom:F0},{cards[2].Y:F0}) ⇒ 卡片底色占比 {rowGapFill:P2}（上限 {MaxGapFillRatio:P0}）"));

        // 底部状态行
        double statusTop = MacVisualGeometry.MonitorStatusTop(panelWidth, panelHeight);
        var statusBand = new LayoutRect(MonitorLayoutSpec.ContentPadX, statusTop,
                                         panelWidth - 2 * MonitorLayoutSpec.ContentPadX,
                                         MonitorStatusLineSpec.LineHeight);
        double statusContent = ContentRatio(img, statusBand, pal.Surface);
        report.Checks.Add(new ProbeCheck(
            "监控页：底部状态行存在（状态行带里有文字像素）",
            statusContent > 0,
            $"状态行带 y∈[{statusTop:F0},{statusTop + MonitorStatusLineSpec.LineHeight:F0}) ⇒ 内容像素占比 {statusContent:P3}"));

        double statusFill = CardFillRatio(img, statusBand, pal.Elevated);
        report.Checks.Add(new ProbeCheck(
            "监控页：底部状态行不压栅格（状态行带里没有卡片底色）",
            statusFill <= MaxGapFillRatio && statusTop >= MacVisualGeometry.MonitorGridBottom,
            $"状态行带内卡片底色占比 {statusFill:P2}（上限 {MaxGapFillRatio:P0}）；" +
            $"状态行顶边 {statusTop:F0} ≥ 栅格底边 {MacVisualGeometry.MonitorGridBottom:F0}"));

        // 状态行左缘与栅格左缘对齐（它从内容区左内缩处起画）
        //
        // ⚠️ 必须取**整条带里最靠左的墨迹**，不能取"按扫描顺序第一个遇到的墨迹"：
        // 抗锯齿会在某些高字形（如数字 4 / 温度符号）的顶部留下零散像素，
        // 按行优先扫到的第一个墨迹可能落在 x=49 这种"中间"位置（2026-11-26 实测），
        // 判成"没对齐"——那是误判，不是真的没对齐。
        int minInkX = int.MaxValue;
        int x0 = (int)statusBand.X, x1 = (int)statusBand.Right;
        int y0 = (int)statusBand.Y, y1 = (int)statusBand.Bottom;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (Delta(img, x, y, pal.Surface) >= ContentDelta && x < minInkX) minInkX = x;
        bool aligned = minInkX != int.MaxValue &&
                       minInkX >= MonitorLayoutSpec.ContentPadX &&
                       minInkX <= MonitorLayoutSpec.ContentPadX + 6;
        report.Checks.Add(new ProbeCheck(
            "监控页：底部状态行左缘与栅格左缘对齐",
            aligned,
            minInkX == int.MaxValue
                ? "状态行带里没找到任何文字像素"
                : $"状态行最左墨迹像素 x={minInkX}（期望 ≈{MonitorLayoutSpec.ContentPadX}）"));
    }

    /// <summary>
    /// 设备页的像素不变量（非全黑 / 有内容 / 每张卡片落在规格位置 / 卡片间留白 / 卡内有绘制）。
    /// </summary>
    public static void CheckDevicesPage(ProbeReport report, ProbeImage img, ProbePalette pal,
                                        double panelWidth, int deviceCount)
    {
        var full = new LayoutRect(0, 0, img.Width, img.Height);

        double painted = PaintedRatio(img, full, pal.Surface);
        report.Checks.Add(new ProbeCheck(
            "设备页：渲染结果非全黑（非背景像素占比 > 2%）",
            painted > MinPaintedRatio,
            $"非背景像素占比 {painted:P2}（下限 {MinPaintedRatio:P0}）"));

        double content = ContentRatio(img, full, pal.Surface);
        report.Checks.Add(new ProbeCheck(
            "设备页：有实际内容（电量环/读数像素占比 > 0.5%）",
            content > MinContentRatio,
            $"内容像素占比 {content:P3}（下限 {MinContentRatio:P1}）"));

        var cards = MacVisualGeometry.DeviceCards(panelWidth, deviceCount);
        for (int i = 0; i < cards.Length; i++)
        {
            double fill = CardFillRatio(img, cards[i], pal.Elevated);
            report.Checks.Add(new ProbeCheck(
                $"设备页：第 {i + 1} 张卡片画在规格位置（卡片底色占比 > 40%）",
                fill > MinCardFillRatio,
                $"规格 {cards[i]} ⇒ 卡片底色占比 {fill:P1}（下限 {MinCardFillRatio:P0}）"));

            // 卡内确有绘制：电量环 + 读数（内容像素）
            var inner = new LayoutRect(cards[i].X + 8, cards[i].Y + 8, cards[i].Width - 16, cards[i].Height - 16);
            double innerContent = ContentRatio(img, inner, pal.Elevated);
            report.Checks.Add(new ProbeCheck(
                $"设备页：第 {i + 1} 张卡片内确有绘制（电量环 + 读数）",
                innerContent > 0.002,
                $"卡内内容像素占比 {innerContent:P3}"));
        }

        for (int i = 0; i + 1 < cards.Length && i % MonitorLayoutSpec.DevicesPerRow(panelWidth) < MonitorLayoutSpec.DevicesPerRow(panelWidth) - 1; i++)
        {
            var gapRect = new LayoutRect(cards[i].Right, cards[i].Y, cards[i + 1].X - cards[i].Right, cards[i].Height);
            double gapFill = CardFillRatio(img, gapRect, pal.Elevated);
            report.Checks.Add(new ProbeCheck(
                $"设备页：第 {i + 1} 与 {i + 2} 张卡片之间留白 12 像素",
                gapFill <= MaxGapFillRatio,
                $"间隙 x∈[{cards[i].Right:F0},{cards[i + 1].X:F0}) ⇒ 卡片底色占比 {gapFill:P2}（上限 {MaxGapFillRatio:P0}）"));
        }
    }

    /// <summary>
    /// 收起态 dash 的像素不变量（读数元素确有绘制 / 图标盒确有绘制 / 读数并排）。
    /// </summary>
    public static void CheckDash(ProbeReport report, ProbeImage img, ProbePalette pal)
    {
        var full = new LayoutRect(0, 0, img.Width, img.Height);

        double content = ContentRatio(img, full, pal.Surface);
        report.Checks.Add(new ProbeCheck(
            "收起态 dash：读数元素确有绘制（内容像素占比 > 0.5%）",
            content > MinContentRatio,
            $"内容像素占比 {content:P3}（下限 {MinContentRatio:P1}）"));

        var clusters = ContentClusters(img, full, pal.Surface, StatusSlotMinGap);
        report.Checks.Add(new ProbeCheck(
            "收起态 dash：读数元素并排（至少 3 段分离的内容）",
            clusters.Count >= 3,
            $"内容簇 {clusters.Count} 段：{string.Join(" / ", clusters)}"));

        // 图标盒：收起态的图标盒边长 12×0.9 = 10.8 ⇒ 墨迹簇高度应落在 [7,14]
        var iconSized = clusters.Where(c => c.Y1 - c.Y0 + 1 >= 7 && c.Y1 - c.Y0 + 1 <= 14).ToList();
        report.Checks.Add(new ProbeCheck(
            "收起态 dash：图标盒确有绘制（存在高度接近图标盒边长 10.8 的墨迹簇）",
            iconSized.Count > 0,
            $"高度在 [7,14]（图标盒边长 12×0.9=10.8）的墨迹簇 {iconSized.Count} 段；全部簇高：{string.Join(", ", clusters.Select(c => c.Y1 - c.Y0 + 1))}"));
    }
}
