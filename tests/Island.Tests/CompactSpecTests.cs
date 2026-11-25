using System;
using Island.App.Controls;

namespace Island.Tests;

/// <summary>
/// 收起态排版规格断言 —— 这些数字是「Windows 收起态与 mac 一致」的唯一契约。
/// 源：src/Island.Avalonia/IslandCanvas.cs L554-L561（常量）、L596-L608（宽度公式）、
///     src/Island.Avalonia/RenderKit.cs L184-L214（Ring 几何）。
/// 改任何一个都要同步改 mac 侧，否则两端会漂移。
/// </summary>
public static class CompactSpecTests
{
    public static void Run(Action<string, bool> check)
    {
        // ---- 常量逐项锁死 ----
        check("规格：胶囊左右内边距 = 10", CompactSpec.PadX == 10);
        check("规格：数值字号 = 13", CompactSpec.FigureFont == 13);
        check("规格：环直径 = 20", CompactSpec.Dial == 20);
        check("规格：环→数值间距 = 5", CompactSpec.DialGap == 5);
        check("规格：同组内相邻环间距 = 10", CompactSpec.GaugeGap == 10);
        check("规格：时间→第一组间距 = 12", CompactSpec.ClockGap == 12);
        check("规格：组间距 = 12（分隔线居中）", CompactSpec.GroupGap == 12);
        check("规格：数值最小槽宽 = 21", CompactSpec.ValueMinW == 21);
        check("规格：↓值与↑之间 = 7", CompactSpec.NetGap == 7);
        check("规格：分隔线上下各 9px", CompactSpec.DividerHalfHeight == 9);

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

        // ---- 分隔线落在组间距正中 ----
        check("分隔线 x = 组尾 + 组间距/2", CompactSpec.DividerXAfter(100) == 106);
        check("分隔线 x 可用自定义组间距", CompactSpec.DividerXAfter(100, 16) == 108);

        // ---- dash 单行项宽 ----
        check("dash 单行项宽 = 标签 + 3 + 数值槽", CompactSpec.InlineItemWidth(28, 30) == 61);
        check("dash 单行项宽窄值按最小槽 21 兜底", CompactSpec.InlineItemWidth(28, 10) == 52);

        // ---- 360° 闭环几何 ----
        check("环半径 = 直径/2 - 线宽/2", CompactSpec.RingRadius(20, 2.8) == 8.6);
        check("环扫角 = 占比 * 2π", Math.Abs(CompactSpec.RingSweep(0.25) - Math.PI / 2) < 1e-9);
        check("环占比夹到 0..1", CompactSpec.RingSweep(2) == 2 * Math.PI);
        check("环 NaN 占比按 0 处理", CompactSpec.RingSweep(double.NaN) == 0);
    }
}
