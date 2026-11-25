using System;

namespace Island.App.Controls;

/// <summary>
/// 收起态排版规格 —— 唯一来源。
///
/// 数值全部照抄 mac 版（行号以 src/Island.Avalonia/ 为准）：
///   combo 常量 src/Island.Avalonia/IslandCanvas.cs L554-L561
///   combo 宽度 src/Island.Avalonia/IslandCanvas.cs L595-L608
///   分隔线   src/Island.Avalonia/IslandCanvas.cs L671-L673（dash 同构副本 L1948-L1950）
///   dash 规格 src/Island.Avalonia/IslandCanvas.cs L1843 / L1863-L1866
///   环几何   src/Island.Avalonia/RenderKit.cs L185-L215
/// 目的：Windows 与 mac 两端收起态视觉一致。改这里必须同步改 mac 侧。
///
/// 本文件刻意只放「纯计算」（不碰 WPF 图形对象），Island.Tests 零依赖链接后可直接断言。
/// </summary>
public static class CompactSpec
{
    // ---------- 与 mac 版一一对应的常量 ----------
    /// <summary>combo 胶囊左右内边距（mac ComboPadX，IslandCanvas.cs L554）。</summary>
    public const double PadX = 10;
    /// <summary>dash 胶囊左右内边距（mac DashPadX，IslandCanvas.cs L1843）—— 与 combo 的 PadX 不同，别混用。</summary>
    public const double DashPadX = 14;
    /// <summary>数值字号（mac ComboFigureFont，IslandCanvas.cs L555）。</summary>
    public const double FigureFont = 13;
    /// <summary>环直径（mac ComboDial，IslandCanvas.cs L555）。</summary>
    public const double Dial = 20;
    /// <summary>环 → 数值间距（mac ComboDialGap，IslandCanvas.cs L555）。</summary>
    public const double DialGap = 5;
    /// <summary>同组内相邻环间距（mac ComboGaugeGap，IslandCanvas.cs L555）。</summary>
    public const double GaugeGap = 10;
    /// <summary>时间 → 第一组间距（mac ComboClockGap，IslandCanvas.cs L555）。</summary>
    public const double ClockGap = 12;
    /// <summary>组与组之间的**单侧**间距（mac CompactGroupGap，IslandCanvas.cs L561）。组间总带见 SeparatorBand。</summary>
    public const double GroupGap = 12;
    /// <summary>
    /// 组与组之间的**总**间距 = 2 × GroupGap = 24。
    /// mac 的 Gap() 连续推进两次 CompactGroupGap（IslandCanvas.cs L673 / L1950），
    /// 所以「组尾 → 下一组起点」整整一条带是 24px，分隔线画在这条带里。
    /// </summary>
    public const double SeparatorBand = GroupGap * 2;
    /// <summary>数值最小槽宽（mac CompactValueMinW，IslandCanvas.cs L559）—— 窄数值也占这么宽，避免抖动。</summary>
    public const double ValueMinW = 21;
    /// <summary>combo 里 ↓值 与 ↑ 之间（mac ComboLayout.NetGap，IslandCanvas.cs L595）。</summary>
    public const double NetGap = 7;
    /// <summary>dash 里 ↓值 与 ↑ 之间（mac DashLayout.NetGap，IslandCanvas.cs L1866）—— 与 combo 的 NetGap 不同。</summary>
    public const double DashNetGap = 10;
    /// <summary>方向箭头与数值之间的间距（mac ComboLayout.NetDownW / NetUpW 里的 +3，IslandCanvas.cs L596-L597）。</summary>
    public const double ArrowValueGap = 3;
    /// <summary>分隔线上下各多少（mac Divider 里的 cy ± 9，IslandCanvas.cs L672 / L1949）。</summary>
    public const double DividerHalfHeight = 9;
    /// <summary>360° 闭环的弧占比死区（mac RenderKit.Ring 的 f 大于 0.004 才画弧，RenderKit.cs L191）。</summary>
    public const double RingDeadZone = 0.004;

    /// <summary>
    /// 一组环的总宽：每环 = 环径 + 环→数值间距 + max(数值宽, 最小槽宽)，
    /// 组内相邻环之间再插一个 GaugeGap，末环后面不插。
    /// 对应 mac ComboLayout.GaugeWidth（IslandCanvas.cs L600-L601）。
    /// </summary>
    public static double GaugeGroupWidth(int count, Func<int, double> figureWidth)
    {
        if (count <= 0) return 0;
        double w = 0;
        for (int i = 0; i < count; i++)
            w += Dial + DialGap + Math.Max(figureWidth(i), ValueMinW) + GaugeGap;
        return w - GaugeGap;
    }

    /// <summary>
    /// combo 内容宽（不含胶囊左右内边距）。对应 mac ComboLayout.ContentW（IslandCanvas.cs L605-L607）。
    ///
    /// ⚠️ **定位：镜像 mac 公式、供对照断言用，不是生产路径的宽度计算。**
    /// 生产渲染（IslandCanvas.DrawCombo → DrawCompactGroups）现场按各组实画宽算内容宽，
    /// 不走这个函数 —— 两者是同一件事的两种写法（这里是"按规格复算"，那里是"按实际排版"）。
    /// 所以：① 别把它当唯一口径改生产路径；② 它抓不到生产宽度的漂移（那是离屏渲染断言的活）。
    /// 存在的意义是：断言"Windows 的 combo 内容宽公式与 mac 一致"，防止规格常量被改歪。
    /// </summary>
    public static double ComboContentWidth(double clockW, double gaugesW, double systemGaugesW,
                                           double netDownW, double netUpW)
        => clockW + ClockGap + gaugesW
         + (systemGaugesW > 0 ? SeparatorBand + systemGaugesW : 0)
         + SeparatorBand + netDownW + NetGap + netUpW;

    /// <summary>
    /// combo 胶囊总宽。对应 mac ComboLayout.CapsuleW（IslandCanvas.cs L608）。
    /// 同 <see cref="ComboContentWidth"/>：这是**镜像 mac 公式的对照口径**，不是生产路径的宽度计算。
    /// </summary>
    public static double ComboCapsuleWidth(double clockW, double gaugesW, double systemGaugesW,
                                           double netDownW, double netUpW)
        => ComboContentWidth(clockW, gaugesW, systemGaugesW, netDownW, netUpW) + PadX * 2;

    /// <summary>
    /// 分隔线的 x：**组尾 + 1.5 × 单侧组间距**（默认 +18px）。
    ///
    /// mac 的 Gap() 是「先把 x 推进一个 CompactGroupGap，再在当时的 x 上画线」，
    /// 而线画在 x + CompactGroupGap / 2 处（IslandCanvas.cs L671-L673 / L1948-L1950）：
    /// 组尾 100 → 推进后 112 → 线在 118 → 下一组从 124 开始。
    /// 即线落在组间总带（SeparatorBand = 24）里偏后 6px，**不在总带正中**。
    /// </summary>
    public static double DividerXAfter(double groupEndX, double gap = GroupGap) => groupEndX + gap * 1.5;

    /// <summary>
    /// 组到下一组的间距：组自带 TrailingGap 优先，否则用渲染层默认组间距。
    /// mac 的 combo 有两种边界：有分隔线的边界 = 24px 总带（SeparatorBand），
    /// 没有分隔线的边界（时间│占用组）= 12px（ClockGap）。
    /// </summary>
    public static double GroupAdvance(double? trailingGap, double defaultGap)
        => trailingGap ?? defaultGap;

    /// <summary>
    /// 收起态分组排版（纯计算）：决定能显示几组、「+N」画在哪、内容占多宽。
    ///
    /// 规则（与 2026-11-25 修复前的旧渲染层逐条一致，别顺手改）：
    ///   - 每组拥有它**后面**那一段组间距：组 i 与组 i+1 之间 = 组 i 的 TrailingGap（缺省 defaultGap）；
    ///   - 预算校验时「+N」跟在候选组**后面**，所以为它预留的空间要用**候选组自己**的 TrailingGap
    ///     （用上一组的会算错：异质间距时会把候选组错误地挤掉或错误地放进来）；
    ///   - 第一个放不下的候选组出现时就停，它后面的组全部合并进「+N」。
    ///
    /// moreWidth(n) = 「+n」这行字的宽度（n = 被折叠掉的组数）。
    /// </summary>
    /// <returns>可见组数 / 收起态内容总宽（不含胶囊左右内边距，含「+N」占位）/ 「+N」左边缘相对第一组左边缘的偏移。</returns>
    public static (int Shown, double ContentWidth, double MoreOffset) PlanCompactGroups(
        int count, Func<int, double> widthOf, Func<int, double?> trailingGapOf,
        double defaultGap, double available, Func<int, double> moreWidth)
    {
        double used = 0;        // 已显示内容的宽（不含最后一段组间距）
        int shown = 0;
        double prevGap = 0;     // 上一组到本组的间距
        double lastGap = 0;     // 最后一个可见组之后的间距
        for (int i = 0; i < count; i++)
        {
            double next = used + (shown > 0 ? prevGap : 0) + widthOf(i);
            int remaining = count - i - 1;
            double ownGap = GroupAdvance(trailingGapOf(i), defaultGap);
            // 「+N」跟在候选组后面 ⇒ 为它预留的空间用候选组自己的间距
            double suffix = remaining > 0 ? ownGap + moreWidth(remaining) : 0;
            if (next + suffix > available) break;
            used = next;
            prevGap = ownGap;
            lastGap = ownGap;
            shown++;
        }
        double moreOffset = used + (shown > 0 ? lastGap : 0);   // 「+N」的左边缘
        double contentWidth = shown < count ? moreOffset + moreWidth(count - shown) : used;
        return (shown, contentWidth, moreOffset);
    }

    /// <summary>
    /// dash 单行项宽：标签 + 箭头/数值间距 + 数值槽（窄数值按最小槽兜底）。
    ///
    /// 注意：这是 **Windows 侧**的组合，mac dash 里没有对应公式，别去 mac 找「同步对象」：
    /// mac dash 是「图标槽 + 图标间距 + max(数值宽, CompactValueMinW) + 温度间距」
    /// （IslandCanvas.cs L1863），既没有「标签 + 数值」这种行内排布，
    /// 它的占用值最小槽也是 34（L1864-L1865）而不是本文件的 ValueMinW = 21。
    /// 中间那个间距取自 mac 的箭头 → 数值间距（ArrowValueGap，IslandCanvas.cs L596-L597）。
    /// </summary>
    public static double InlineItemWidth(double labelWidth, double valueWidth)
        => labelWidth + ArrowValueGap + Math.Max(valueWidth, ValueMinW);

    /// <summary>
    /// dash 单行项**组**的内容宽：各项宽之和 + 组内间距（(n-1) × GaugeGap）。
    /// 各项宽由 <see cref="InlineItemWidth"/> 给定。
    ///
    /// ⚠️ valueWidth 必须传**实画文本宽**（不是"最宽样本"）：
    /// 组宽就是这一组真正画出来的宽度，组尾又是分隔线的定位基准（线在组尾 + 18）。
    /// 用样本宽预留会让组尾比最后一个字形多出一截空洞（分隔线整体右移、且随读数跳变）——
    /// 2026-11-25 复审 Must-fix 1 就是这个 bug（每项空出 7.1px）。
    /// mac 的 DashLayout.TempW / UsageW 同口径：量的是 max(实画文本宽, CompactValueMinW)。
    /// </summary>
    public static double InlineGroupWidth(int count, Func<int, double> labelWidth, Func<int, double> valueWidth)
    {
        if (count <= 0) return 0;
        double w = 0;
        for (int i = 0; i < count; i++)
            w += InlineItemWidth(labelWidth(i), valueWidth(i)) + GaugeGap;
        return w - GaugeGap;
    }

    /// <summary>
    /// 360° 闭环半径（mac RenderKit.Ring：radius = diameter / 2 - thickness / 2，RenderKit.cs L187）。
    /// 与 mac 一样**不设下限**：线宽等于直径时半径就是 0。
    /// </summary>
    public static double RingRadius(double diameter, double thickness)
        => diameter / 2 - thickness / 2;

    /// <summary>
    /// 360° 闭环的扫角（占比 * 2π，从 12 点顺时针）。
    /// NaN 按 0、越界夹到 0..1（与 mac RenderKit.Ring 里 f 的归一一致，RenderKit.cs L190）。
    /// 扫角超过 180° 要拆成两段弧画，那是**渲染层**的职责（Task 2 的 RenderKit.Ring），不在本类里。
    /// </summary>
    public static double RingSweep(double share)
    {
        double f = double.IsNaN(share) ? 0 : Math.Clamp(share, 0, 1);
        return f * 2 * Math.PI;
    }
}
