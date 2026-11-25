using System;

namespace Island.App.Controls;

/// <summary>
/// 收起态排版规格 —— 唯一来源。
///
/// 数值全部照抄 mac 版：
///   常量   src/Island.Avalonia/IslandCanvas.cs L554-L561
///   宽度   src/Island.Avalonia/IslandCanvas.cs L596-L608
///   环几何 src/Island.Avalonia/RenderKit.cs L184-L214
/// 目的：Windows 与 mac 两端收起态视觉一致。改这里必须同步改 mac 侧。
///
/// 本文件刻意只放「纯计算」（不碰 WPF 图形对象），Island.Tests 零依赖链接后可直接断言。
/// </summary>
public static class CompactSpec
{
    // ---------- 与 mac 版一一对应的常量 ----------
    /// <summary>胶囊左右内边距（mac ComboPadX）。</summary>
    public const double PadX = 10;
    /// <summary>数值字号（mac ComboFigureFont）。</summary>
    public const double FigureFont = 13;
    /// <summary>环直径（mac ComboDial）。</summary>
    public const double Dial = 20;
    /// <summary>环 → 数值间距（mac ComboDialGap）。</summary>
    public const double DialGap = 5;
    /// <summary>同组内相邻环间距（mac ComboGaugeGap）。</summary>
    public const double GaugeGap = 10;
    /// <summary>时间 → 第一组间距（mac ComboClockGap）。</summary>
    public const double ClockGap = 12;
    /// <summary>组与组之间（分隔线居中，两侧各留白）—— mac CompactGroupGap。</summary>
    public const double GroupGap = 12;
    /// <summary>数值最小槽宽（mac CompactValueMinW）—— 窄数值也占这么宽，避免抖动。</summary>
    public const double ValueMinW = 21;
    /// <summary>↓值 与 ↑ 之间（mac NetGap）。</summary>
    public const double NetGap = 7;
    /// <summary>分隔线上下各多少（mac Divider 里的 cy ± 9）。</summary>
    public const double DividerHalfHeight = 9;

    /// <summary>
    /// 一组环的总宽：每环 = 环径 + 环→数值间距 + max(数值宽, 最小槽宽)，
    /// 组内相邻环之间再插一个 GaugeGap，末环后面不插。
    /// 对应 mac ComboLayout.GaugeWidth。
    /// </summary>
    public static double GaugeGroupWidth(int count, Func<int, double> figureWidth)
    {
        if (count <= 0) return 0;
        double w = 0;
        for (int i = 0; i < count; i++)
            w += Dial + DialGap + Math.Max(figureWidth(i), ValueMinW) + GaugeGap;
        return w - GaugeGap;
    }

    /// <summary>combo 内容宽（不含胶囊左右内边距）。对应 mac ComboLayout.ContentW。</summary>
    public static double ComboContentWidth(double clockW, double gaugesW, double systemGaugesW,
                                           double netDownW, double netUpW)
        => clockW + ClockGap + gaugesW
         + (systemGaugesW > 0 ? GroupGap * 2 + systemGaugesW : 0)
         + GroupGap * 2 + netDownW + NetGap + netUpW;

    /// <summary>combo 胶囊总宽。对应 mac ComboLayout.CapsuleW。</summary>
    public static double ComboCapsuleWidth(double clockW, double gaugesW, double systemGaugesW,
                                           double netDownW, double netUpW)
        => ComboContentWidth(clockW, gaugesW, systemGaugesW, netDownW, netUpW) + PadX * 2;

    /// <summary>分隔线的 x：组尾再往前半个组间距（分隔线居中）。</summary>
    public static double DividerXAfter(double groupEndX, double gap = GroupGap) => groupEndX + gap / 2;

    /// <summary>dash 单行项宽：标签 + 间距 + 数值槽（窄数值按最小槽兜底）。</summary>
    public static double InlineItemWidth(double labelWidth, double valueWidth)
        => labelWidth + 3 + Math.Max(valueWidth, ValueMinW);

    /// <summary>360° 闭环半径（mac RenderKit.Ring：radius = diameter/2 - thickness/2）。</summary>
    public static double RingRadius(double diameter, double thickness)
        => Math.Max(0.5, diameter / 2 - thickness / 2);

    /// <summary>360° 闭环的扫角（占比 * 2π，从 12 点顺时针）。</summary>
    public static double RingSweep(double share)
    {
        double f = double.IsNaN(share) ? 0 : Math.Clamp(share, 0, 1);
        return f * 2 * Math.PI;
    }
}
