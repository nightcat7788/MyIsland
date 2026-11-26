namespace Island.Avalonia;

/// <summary>
/// 一个矩形（纯数值，无 Avalonia 依赖 —— 探针与零 NuGet 的测试工程都要链它）。
/// </summary>
public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    /// <summary>两个矩形是否相交（相接不算相交）。</summary>
    public bool Intersects(LayoutRect other)
        => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    /// <summary>this 是否完整包含 other。</summary>
    public bool Contains(LayoutRect other)
        => other.X >= X && other.Right <= Right && other.Y >= Y && other.Bottom <= Bottom;

    public override string ToString() => $"({X:F0},{Y:F0} {Width:F0}×{Height:F0})";
}

/// <summary>
/// 展开面板（监控页 / 设备页）的**布局规格** —— mac 端唯一真相源。
///
/// 纯常量 + 纯计算，**无 Avalonia 依赖**：所以它能被三处同时链进去 ——
///   · 产品绘制代码（<c>IslandCanvas.DrawMonitorPage</c> / <c>DrawDevicesPage</c>、
///     <c>IslandWindow</c> 的面板尺寸）；
///   · 离屏渲染探针（tools\avalonia-probe）；
///   · 零 NuGet 的测试工程（tests\Island.Tests 直接链这个源文件）。
///
/// 为什么要有它：2026-11-26 建 mac 端离屏渲染探针时，"卡片多宽 / 列间距多少 / 状态行落在哪"
/// 这些数字原本散落在 <c>IslandCanvas</c> 的方法局部 const 里。探针若自己抄一份，
/// 产品改了探针不会知道 ⇒ 探针的断言会悄悄失效（Windows 端当年就吃过"抄一份副本"的亏，
/// 所以那边有 <c>Island.App.Controls.ExpandedPanelSpec</c>）。提到这里之后，
/// 产品改一处，探针与门禁同时变。
///
/// 口径对齐：这些数值与 Windows 端的 <c>Island.App.Controls.ExpandedPanelSpec</c>
/// 与 <c>Island.App.Controls.MonitorPage</c> 的私有常量同值（两端以 mac 为基准对齐过）。
/// 门禁（tests\Island.Tests\AvaloniaVisualTests.cs）会**反射 Windows 端那份**逐项比对，
/// 两端漂了就红。
/// </summary>
public static class MonitorLayoutSpec
{
    // ==================== 面板尺寸 ====================

    /// <summary>展开面板宽度（mac：560）。</summary>
    public const double ExpandedPanelWidth = 560;

    // ==================== 顶栏 ====================

    /// <summary>顶栏相对面板上边缘的偏移（mac：14）。</summary>
    public const double TabTopOffset = 14;

    /// <summary>顶栏到内容区的垂直距离（mac：24 + 16 = 40）。</summary>
    public const double ContentTopOffset = 40;

    /// <summary>顶栏区总高度（面板高度公式里用的那一项，mac：42）。</summary>
    public const double TabAreaH = 42;

    // ==================== 内容区 ====================

    /// <summary>内容区左右内边距（mac：18）。</summary>
    public const double ContentPadX = 18;

    /// <summary>内容区下内边距（mac：18）。</summary>
    public const double ContentPadBottom = 18;

    // ==================== 监控页 2×2 卡片栅格 ====================

    /// <summary>栅格列间距（两列之间，mac：12）。</summary>
    public const double GridGap = 12;

    /// <summary>卡片头高度（mac：42）。</summary>
    public const double CardHeadH = 42;

    /// <summary>卡片内容起始 y 相对卡片顶边（mac：44 —— 标题 + 分隔线之下）。</summary>
    public const double CardContentTop = 44;

    /// <summary>读数行行高（mac：44）。</summary>
    public const double RowH = 44;

    /// <summary>读数行之间的额外间距（mac：7）。</summary>
    public const double RowGap = 7;

    /// <summary>上下速度卡的行高（mac：46）。</summary>
    public const double SpeedRowH = 46;

    /// <summary>上下速度卡底部留给"网络 / 电池"信息位的高度（mac：22）。</summary>
    public const double SpeedTailH = 22;

    /// <summary>上排卡片高度（卡头 + 3 行 × 行高 + 间距，mac：181）。</summary>
    public const double TopCardH = CardHeadH + 3 * RowH + RowGap;

    /// <summary>下排卡片高度的第一项（卡头 + 2 行 × 速度行高 + 尾部信息位，mac：156）。</summary>
    public const double BottomCardH = CardHeadH + 2 * SpeedRowH + SpeedTailH;

    /// <summary>下排卡片高度的第二项（原实现 <c>Math.Max</c> 的备选值，mac：128）。</summary>
    public const double BottomCardHAlt = CardHeadH + 86;

    /// <summary>下排卡片实际高度（取两者较大者，mac：156）。</summary>
    public static double BottomCardHeight => Math.Max(BottomCardH, BottomCardHAlt);

    // ==================== 面板高度 ====================

    /// <summary>底部状态行区高度（mac：30）。</summary>
    public const double StatusAreaH = 30;

    /// <summary>面板底部余量（mac：34）。</summary>
    public const double BottomAreaH = 34;

    /// <summary>监控页展开面板高度（mac：455）。</summary>
    public const double MonitorPanelHeight =
        TabAreaH + TopCardH + GridGap + BottomCardH + StatusAreaH + BottomAreaH;

    // ==================== 设备页 ====================

    /// <summary>设备页左右内边距（mac：20）。</summary>
    public const double DevicePagePadX = 20;

    /// <summary>设备卡片宽（mac：132）。</summary>
    public const double DeviceCardWidth = 132;

    /// <summary>设备卡片高（mac：150）。</summary>
    public const double DeviceCardHeight = 150;

    /// <summary>设备卡片之间的间距（mac：12）。</summary>
    public const double DeviceCardGap = 12;

    // ==================== 收起态图标盒 ====================

    /// <summary>
    /// 收起态（dash / combo）读数项前的**图标盒**边长（mac：12 × 0.9 = 10.8）。
    /// </summary>
    public const double CompactIconBox = 12 * 0.9;

    // ==================== 布局求解 ====================

    /// <summary>内容区顶边相对面板上边缘（mac：54）。</summary>
    public static double ContentTop => TabTopOffset + ContentTopOffset;

    /// <summary>监控页单张卡片宽度。</summary>
    public static double MonitorCardWidth(double panelWidth)
        => (panelWidth - 2 * ContentPadX - GridGap) / 2;

    /// <summary>
    /// 监控页 2×2 栅格的四张卡片（顺序：左上 / 右上 / 左下 / 右下）。
    /// 坐标系：面板左上角为原点（探针渲染时把 BodyRect 摆到原点）。
    /// </summary>
    public static LayoutRect[] MonitorCards(double panelWidth, double contentTop)
    {
        double cardW = MonitorCardWidth(panelWidth);
        double x1 = ContentPadX;
        double x2 = x1 + cardW + GridGap;
        double y1 = contentTop;
        double y2 = y1 + TopCardH + GridGap;
        return new[]
        {
            new LayoutRect(x1, y1, cardW, TopCardH),
            new LayoutRect(x2, y1, cardW, TopCardH),
            new LayoutRect(x1, y2, cardW, BottomCardHeight),
            new LayoutRect(x2, y2, cardW, BottomCardHeight),
        };
    }

    /// <summary>监控页栅格底边（卡片区结束、底部状态行开始的位置）。</summary>
    public static double MonitorGridBottom(double contentTop)
        => contentTop + TopCardH + GridGap + BottomCardHeight;

    /// <summary>设备页可用宽度（面板宽减去左右内边距）。</summary>
    public static double DevicesContentWidth(double panelWidth)
        => panelWidth - 2 * DevicePagePadX;

    /// <summary>设备页每行卡片数。</summary>
    public static int DevicesPerRow(double panelWidth)
        => Math.Max(1, (int)((DevicesContentWidth(panelWidth) + DeviceCardGap) / (DeviceCardWidth + DeviceCardGap)));

    /// <summary>
    /// 设备页卡片位置（顺序：逐行从左到右）。
    /// </summary>
    public static LayoutRect[] DeviceCards(double panelWidth, double contentTop, int count)
    {
        int perRow = DevicesPerRow(panelWidth);
        var result = new LayoutRect[Math.Max(0, count)];
        for (int i = 0; i < result.Length; i++)
        {
            double x = DevicePagePadX + (i % perRow) * (DeviceCardWidth + DeviceCardGap);
            double y = contentTop + (i / perRow) * (DeviceCardHeight + DeviceCardGap);
            result[i] = new LayoutRect(x, y, DeviceCardWidth, DeviceCardHeight);
        }
        return result;
    }
}
