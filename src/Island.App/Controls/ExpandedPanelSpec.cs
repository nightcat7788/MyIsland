using System;

namespace Island.App.Controls;

/// <summary>
/// 展开面板的**规格常量** —— 以 mac 版现有实现为基准抄下来的数值。
///
/// 纯计算、**无 WPF 依赖**（可链进零 NuGet 的测试工程），像阶段 1 的
/// <c>CompactSpec</c> / <c>CompactLayout</c> 那样：规格不是文档里的数字，
/// 而是**代码里被门禁守着的数字** —— 实现引用这里，测试断言这里。
///
/// 来源（2026-11-26 逐行核对 mac 源码）：
///  · 顶栏页签：mac `IslandCanvas.cs` 的 `DrawExpanded` / `Tab(...)` 局部函数
///  · 监控页：mac `IslandCanvas.cs` 的 `DrawMonitorPage`（卡片栅格与行规格）
///  · 设备页：mac `IslandCanvas.cs` 的 `DrawDevicesPage`（卡片网格）
///
/// 三条取舍（见规格 §2）：
///  ① 共享 UI 对齐**几何与结构**，**保留 Windows 自己的配色身份**（暗色科技）；
///  ② 平台专属能力不互相搬运（Windows 的齿轮 / 收起 / 文件列 / 控制页保留）；
///  ③ 不合并两套 RenderKit，只保证**调用点参数**一致。
/// </summary>
public static class ExpandedPanelSpec
{
    // ================= 顶栏 =================

    /// <summary>页签图标边长（mac：14）。</summary>
    public const double TabIconSize = 14;

    /// <summary>图标与页签文字之间的间距（mac：5）。</summary>
    public const double TabIconGap = 5;

    /// <summary>页签文字字号（mac：12）。</summary>
    public const double TabFontSize = 12;

    /// <summary>页签热区左右各留白（mac：7）。</summary>
    public const double TabPadX = 7;

    /// <summary>页签热区上下各留白（mac：5）。</summary>
    public const double TabPadY = 5;

    /// <summary>相邻页签之间的间距（mac：6）。</summary>
    public const double TabGap = 6;

    /// <summary>顶栏相对面板上边缘的偏移（mac：14）。</summary>
    public const double TabTopOffset = 14;

    /// <summary>顶栏左侧起始位置相对面板左边缘（mac：20）。</summary>
    public const double TabLeftOffset = 20;

    /// <summary>激活页签底色的不透明度（mac：Accent 20%）。</summary>
    public const double TabActiveFillAlpha = 0.20;

    /// <summary>激活页签底色的圆角（mac：8）。</summary>
    public const double TabActiveCorner = 8;

    /// <summary>顶栏时钟字号（mac：12，比例字体、Regular）。</summary>
    public const double ClockFontSize = 12;

    /// <summary>顶栏时钟右侧留白（mac：24）。</summary>
    public const double ClockRightMargin = 24;

    /// <summary>顶栏到内容区的垂直距离（mac：24 + 16 = 40）。</summary>
    public const double ContentTopOffset = 40;

    // ================= 面板外框 =================

    /// <summary>面板圆角（两端已一致：20）。</summary>
    public const double PanelCorner = 20;

    /// <summary>内容区左右内边距（mac：18）。</summary>
    public const double ContentPadX = 18;

    /// <summary>内容区上内边距（mac：14）。</summary>
    public const double ContentPadTop = 14;

    /// <summary>内容区下内边距（mac：18）。</summary>
    public const double ContentPadBottom = 18;

    // ================= 监控页字号阶梯 =================

    /// <summary>主读数字号（mac：14）。</summary>
    public const double MetricValueFont = 14;

    /// <summary>卡片标题字号（mac：12 SemiBold）。</summary>
    public const double CardTitleFont = 12;

    /// <summary>次级文字字号（进程名 / GPU 角标，mac：11）。</summary>
    public const double SecondaryFont = 11;

    /// <summary>行标签字号（mac：10）。</summary>
    public const double RowLabelFont = 10;

    /// <summary>底部状态行字号（mac：9）。</summary>
    public const double StatusFont = 9;

    /// <summary>列表空态字号（mac：10.5）。</summary>
    public const double EmptyListFont = 10.5;

    // ================= 监控页零件 =================

    /// <summary>进度条高度（mac：4）。</summary>
    public const double MeterHeight = 4;

    /// <summary>进度条圆角（mac：2）。</summary>
    public const double MeterCorner = 2;

    /// <summary>走势曲线笔宽（mac：1.4）。</summary>
    public const double SparkStroke = 1.4;

    /// <summary>走势曲线框高（mac：14）。</summary>
    public const double SparkHeight = 14;

    /// <summary>卡片标题图标边长（mac：12）。</summary>
    public const double CardTitleIconSize = 12;

    /// <summary>卡片标题分隔线左右各内缩（mac：15）。</summary>
    public const double CardTitleRuleInset = 15;

    /// <summary>进程榜行距（mac：21）。</summary>
    public const double ProcessRowGap = 21;

    /// <summary>
    /// 进程榜卡的**卡底信息行**（进程/线程数 + 今日流量）基线离卡底的距离。
    /// 这行是 2026-11-26 补回来的（2×2 栅格重排时一度被删，删了等于功能回退），
    /// 所以它必须留在卡里 —— 排版要给它**预留位置**，而不是让它去挤榜体。
    /// </summary>
    public const double ProcessFooterBaselineInset = 8;

    /// <summary>
    /// 进程榜卡内给**卡底信息行**预留的高度（文字 + 上方留白 + 余量）。
    /// 榜体的行数与行距都从这里倒推 —— 固定行距 + 固定行数会撞上卡底那行
    /// （2026-11-26 离屏渲染实测到的压字缺陷：最后一行与卡底行叠在一起）。
    /// </summary>
    public const double ProcessFooterReserve = 18;

    /// <summary>进程榜行距下限：15px 图标 + 3px 间隔，再小图标就连成一片。</summary>
    public const double ProcessRowGapMin = 18;

    /// <summary>进程榜在给定可用高度里排得下的行数（不超过上限，至少 1 行）。</summary>
    public static int ProcessRowsFor(double availH, int maxRows)
    {
        if (availH <= 0) return 1;
        int fit = (int)Math.Floor(availH / ProcessRowGapMin);
        return Math.Max(1, Math.Min(maxRows, fit));
    }

    /// <summary>进程榜行距：可用高度 ÷ 行数，向下取整 —— 保证最后一行不压到卡底信息行。</summary>
    public static double ProcessRowGapFor(double availH, int rows)
    {
        if (rows <= 0) return ProcessRowGap;
        double gap = Math.Floor(availH / rows);
        return Math.Max(1, Math.Min(ProcessRowGap, Math.Max(ProcessRowGapMin, gap)));
    }

    /// <summary>进程榜最多显示的行数（mac：5）。</summary>
    public const int ProcessMaxRows = 5;

    /// <summary>进程榜图标边长（mac：15）。</summary>
    public const double ProcessIconSize = 15;

    /// <summary>进程名字号（mac：11，比例字体）。</summary>
    public const double ProcessNameFont = 11;

    /// <summary>进程榜数值字号（mac：11 SemiBold）。</summary>
    public const double ProcessValueFont = 11;

    /// <summary>排序分段控件宽度（mac：96）。</summary>
    public const double SegmentedWidth = 96;

    /// <summary>排序分段控件高度（mac：20）。</summary>
    public const double SegmentedHeight = 20;

    /// <summary>排序分段控件圆角（mac：6）。</summary>
    public const double SegmentedCorner = 6;

    // ================= 设备页 =================

    /// <summary>设备卡片宽（mac：132）。</summary>
    public const double DeviceCardWidth = 132;

    /// <summary>设备卡片高（mac：150）。</summary>
    public const double DeviceCardHeight = 150;

    /// <summary>设备卡片之间的间距（mac：12）。</summary>
    public const double DeviceCardGap = 12;

    /// <summary>设备页大号读数字号（mac：26）。</summary>
    public const double DevicePercentFont = 26;

    /// <summary>设备页状态行字号（mac：10）。</summary>
    public const double DeviceStatusFont = 10;

    /// <summary>设备页空态字号（mac：11.5）。</summary>
    public const double DeviceEmptyFont = 11.5;

    // ================= 小形象款式 =================

    /// <summary>
    /// mac 的 8 款小形象（以 mac 为准，Windows 要补齐后 3 款）。
    /// 设置项 `Settings.PetStyleValues` 已经是这 8 款的并集 ✓。
    /// </summary>
    public static readonly string[] PetStyles =
    {
        "classic", "mono", "cat", "robot", "pixel", "alien", "ghost", "frog",
    };
}
