using System;
using Island.App.Controls;

namespace Island.Tests;

/// <summary>
/// 收起态排版规格断言 —— 这些数字是「Windows 收起态与 mac 一致」的唯一契约。
/// 源：src/Island.Avalonia/IslandCanvas.cs L554-L561（常量）、L595-L608（宽度公式）、
///     L671-L673（分隔线）、L1843 / L1863-L1866（dash 规格）、
///     src/Island.Avalonia/RenderKit.cs L185-L215（Ring 几何）。
/// 改任何一个都要同步改 mac 侧，否则两端会漂移。
/// </summary>
internal static class CompactSpecTests
{
    public static void Run(Action<string, bool> check)
    {
        // ---- 常量逐项锁死 ----
        check("规格：combo 胶囊左右内边距 = 10", CompactSpec.PadX == 10);
        check("规格：dash 胶囊左右内边距 = 14", CompactSpec.DashPadX == 14);
        check("规格：数值字号 = 13", CompactSpec.FigureFont == 13);
        check("规格：环直径 = 20", CompactSpec.Dial == 20);
        check("规格：环→数值间距 = 5", CompactSpec.DialGap == 5);
        check("规格：同组内相邻环间距 = 10", CompactSpec.GaugeGap == 10);
        check("规格：时间→第一组间距 = 12", CompactSpec.ClockGap == 12);
        check("规格：组间单侧间距 = 12", CompactSpec.GroupGap == 12);
        check("规格：组间总带 = 24（mac Gap() 推进两次 GroupGap）",
            CompactSpec.SeparatorBand == 24 && CompactSpec.SeparatorBand == CompactSpec.GroupGap * 2);
        check("规格：数值最小槽宽 = 21", CompactSpec.ValueMinW == 21);
        check("规格：combo 里 ↓值与↑之间 = 7", CompactSpec.NetGap == 7);
        check("规格：dash 里 ↓值与↑之间 = 10", CompactSpec.DashNetGap == 10);
        check("规格：方向箭头与数值间距 = 3", CompactSpec.ArrowValueGap == 3);
        check("规格：分隔线上下各 9px", CompactSpec.DividerHalfHeight == 9);
        check("规格：闭环弧占比死区 = 0.004", CompactSpec.RingDeadZone == 0.004);

        // ---- 一组环的宽度 ----
        // 3 个环，每个数值宽 30（>最小槽 21）：3*(20+5+30) + 2*10(组内间距) = 165+20 = 185
        check("环组宽度：3 个环各 30 宽 = 185", CompactSpec.GaugeGroupWidth(3, _ => 30) == 185);
        // 数值窄于最小槽时按 21 算：3*(20+5+21) + 2*10 = 138+20 = 158
        check("环组宽度：窄数值按最小槽 21 兜底 = 158", CompactSpec.GaugeGroupWidth(3, _ => 10) == 158);
        check("环组宽度：空组 = 0", CompactSpec.GaugeGroupWidth(0, _ => 30) == 0);

        // ---- combo 内容宽（对应 mac ComboLayout.ContentW）----
        double expectContent = 40 + 12 + 185 + (12 * 2 + 158) + 12 * 2 + 60 + 7 + 60;
        check("combo 内容宽与 mac 公式一致",
            CompactSpec.ComboContentWidth(40, 185, 158, 60, 60) == expectContent);
        check("combo 胶囊宽 = 内容宽 + 2*PadX",
            CompactSpec.ComboCapsuleWidth(40, 185, 158, 60, 60) == expectContent + 20);
        double expectNoSystem = 40 + 12 + 185 + 12 * 2 + 60 + 7 + 60;
        check("combo 无系统组时不留该段空位",
            CompactSpec.ComboContentWidth(40, 185, 0, 60, 60) == expectNoSystem);

        // ---- 分隔线位置：mac 的 Gap() 先推进一次组间距再画线 ----
        // 组尾 100 → 推进后 112 → 线在 112 + 6 = 118 → 下一组从 124 开始
        check("分隔线 x = 组尾 + 18（mac：Gap() 推进两次 GroupGap，线在 1.5×处）",
            CompactSpec.DividerXAfter(100) == 118);
        check("分隔线 x 可用自定义组间距（gap=16 → 组尾 + 24）",
            CompactSpec.DividerXAfter(100, 16) == 124);
        check("分隔线落在组间总带内（组尾 + 18 < 组尾 + 24）",
            CompactSpec.DividerXAfter(100) - 100 < CompactSpec.SeparatorBand);

        // ---- dash 单行项宽（Windows 侧组合，mac dash 没有对应公式）----
        check("dash 单行项宽 = 标签 + 箭头数值间距 3 + 数值槽", CompactSpec.InlineItemWidth(28, 30) == 61);
        check("dash 单行项宽窄值按最小槽 21 兜底", CompactSpec.InlineItemWidth(28, 10) == 52);

        // ---- 360° 闭环几何 ----
        check("环半径 = 直径/2 - 线宽/2", CompactSpec.RingRadius(20, 2.8) == 8.6);
        check("环半径不设下限（mac 公式：线宽=直径时半径为 0）", CompactSpec.RingRadius(4, 4) == 0);
        check("环扫角 = 占比 * 2π", Math.Abs(CompactSpec.RingSweep(0.25) - Math.PI / 2) < 1e-9);
        check("环占比夹到 0..1", CompactSpec.RingSweep(2) == 2 * Math.PI);
        check("环 NaN 占比按 0 处理", CompactSpec.RingSweep(double.NaN) == 0);

        // ---- 组到下一组的间距：组自带 TrailingGap 优先，否则用渲染层默认组间距 ----
        check("组间距默认取渲染层默认值", CompactSpec.GroupAdvance(null, 24) == 24);
        check("组自带 TrailingGap 时优先用它（combo 的 时间│占用组 = ClockGap 12）",
            CompactSpec.GroupAdvance(12, 24) == 12);

        // ---- 截断决策（2026-11-25 修复轮）：可见组数必须与旧算法一致 ----
        // 排版决策是纯计算（CompactSpec.PlanCompactGroups），渲染层只负责画 ⇒ 「外观不变」由这里守住。
        // 「+N」的文字宽度用固定值 20 代入（真实宽度随字体而变；这组用例锁的是判据规则本身）。
        const double MoreW = 20;
        (int Shown, double ContentWidth, double MoreOffset) Plan(double[] widths, double?[] gaps, double gap, double avail)
            => CompactSpec.PlanCompactGroups(widths.Length, i => widths[i], i => gaps[i], gap, avail, _ => MoreW);

        // 评审反例 1：4 组各宽 30、组间距 16 —— 旧算法可用 46…61 时一组都放不下（画面只有「+4」）
        check("截断：4 组各宽 30、间距 16、可用 46 → 旧算法显示 0 组",
            Plan(new[] { 30.0, 30, 30, 30 }, new double?[] { null, null, null, null }, 16, 46).Shown == 0);
        check("截断：4 组各宽 30、间距 16、可用 61 → 旧算法显示 0 组",
            Plan(new[] { 30.0, 30, 30, 30 }, new double?[] { null, null, null, null }, 16, 61).Shown == 0);
        check("截断：4 组各宽 30、间距 16、可用 66 → 旧算法显示 1 组",
            Plan(new[] { 30.0, 30, 30, 30 }, new double?[] { null, null, null, null }, 16, 66).Shown == 1);
        check("截断：4 组各宽 30、间距 16、可用 111 → 旧算法显示 1 组",
            Plan(new[] { 30.0, 30, 30, 30 }, new double?[] { null, null, null, null }, 16, 111).Shown == 1);
        check("截断：4 组各宽 30、间距 16、可用 112 → 旧算法显示 2 组",
            Plan(new[] { 30.0, 30, 30, 30 }, new double?[] { null, null, null, null }, 16, 112).Shown == 2);
        check("截断：4 组各宽 30、间距 16、可用 158 → 旧算法显示 3 组",
            Plan(new[] { 30.0, 30, 30, 30 }, new double?[] { null, null, null, null }, 16, 158).Shown == 3);
        check("截断：4 组各宽 30、间距 16、可用 168 → 4 组全显示",
            Plan(new[] { 30.0, 30, 30, 30 }, new double?[] { null, null, null, null }, 16, 168).Shown == 4);

        // 评审反例 2：组宽 [13, 12]、组间距 16 —— 旧算法可用 41…48 时显示 0 组（画面只有「+2」）
        check("截断：组宽 13/12、间距 16、可用 41 → 旧算法显示 0 组",
            Plan(new[] { 13.0, 12 }, new double?[] { null, null }, 16, 41).Shown == 0);
        check("截断：组宽 13/12、间距 16、可用 44 → 旧算法显示 0 组",
            Plan(new[] { 13.0, 12 }, new double?[] { null, null }, 16, 44).Shown == 0);
        check("截断：组宽 13/12、间距 16、可用 48 → 旧算法显示 0 组",
            Plan(new[] { 13.0, 12 }, new double?[] { null, null }, 16, 48).Shown == 0);
        check("截断：组宽 13/12、间距 16、可用 49 → 旧算法显示 2 组",
            Plan(new[] { 13.0, 12 }, new double?[] { null, null }, 16, 49).Shown == 2);

        // 「+N」的位置：跟在最后一个可见组之后，且保留一个组间距（旧行为，不能塌成紧贴）
        var trunc = Plan(new[] { 30.0, 30, 30, 30 }, new double?[] { null, null, null, null }, 16, 100);
        check("截断：「+N」落在最后一个可见组之后并留一个组间距（组尾 30 + 间距 16 = 46）",
            trunc.Shown == 1 && trunc.MoreOffset == 46);
        check("截断：内容宽 = 已显示内容 + 组间距 + 「+N」（30 + 16 + 20 = 66）",
            trunc.ContentWidth == 66);

        // 异质组间距：为「+N」预留的空间用**候选组自己**的间距，组间距用**上一组自己**的间距
        var hetero = Plan(new[] { 30.0, 30 }, new double?[] { 12, null }, 16, 62);
        check("截断：为「+N」预留用候选组自己的间距（组 0 自带 12 ⇒ 30 + 12 + 20 = 62 放得下）",
            hetero.Shown == 1 && hetero.MoreOffset == 42);
        check("截断：组间距用上一组自己的间距（组 0 自带 12 ⇒ 组 1 起点 42、终点 72）",
            Plan(new[] { 30.0, 30 }, new double?[] { 12, null }, 16, 72).Shown == 2);

        // ---- combo 的组结构：时间│占用组│温度磁盘组│网速（对应 mac LayoutCombo）----
        // 环数口径：占用组 = C/M 恒画 + G（仅当有独显读数）⇒ 2~3 环；系统组 = C/G/D（可被显示项开关裁掉）⇒ 0~3 环。
        // 3 环的宽度（185）已由上面的「环组宽度：3 个环各 30 宽」锁住，这里补 1 环 / 2 环两个真实形态。
        check("combo 环组宽度：1 环 = 55", CompactSpec.GaugeGroupWidth(1, _ => 30) == 55);
        check("combo 环组宽度：2 环（C/M，无独显）= 120", CompactSpec.GaugeGroupWidth(2, _ => 30) == 120);
        // mac 的 combo 只有 **两处** 分隔线（占用组│温度磁盘组、温度磁盘组│网速）；
        // 时间│占用组之间没有线，只有 ClockGap=12 的留白。
        double content = CompactSpec.ComboContentWidth(40, 185, 185, 60, 60);
        check("combo 内容宽 = 时钟12 + 占用组 + (24+系统组) + 24 + 网速",
            content == 40 + 12 + 185 + (24 + 185) + 24 + 60 + 7 + 60);
        // 两处分隔线的边界口径：时钟│占用组 = 12（无线），其余组间 = 24（有线，总带里画线）。
        check("combo 边界：时钟│占用组 = 12（无线）",
            CompactSpec.GroupAdvance(CompactSpec.ClockGap, CompactSpec.SeparatorBand) == 12);
        check("combo 边界：其余组间 = 24（带分隔线）",
            CompactSpec.GroupAdvance(null, CompactSpec.SeparatorBand) == 24);
    }
}
