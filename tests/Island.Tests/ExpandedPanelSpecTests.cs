using System;
using Island.App.Controls;

namespace Island.Tests;

/// <summary>
/// 展开面板规格的用例（2026-11-26，阶段 2：Windows 展开面板对齐 mac 版）。
///
/// 这些断言守的是「**规格本身**」—— 数值必须与 mac 版现有实现一致。
/// 阶段 1 的做法同款（CompactSpecTests 守收起态的常量）。
/// 数值来源：mac `src/Island.Avalonia/IslandCanvas.cs` 的
/// `DrawExpanded` / `DrawMonitorPage` / `DrawDevicesPage`（2026-11-26 逐行核对）。
/// </summary>
internal static class ExpandedPanelSpecTests
{
    public static void Run(Action<string, bool> check)
    {
        // ---------- 顶栏 ----------
        check("顶栏：页签图标 14、图标与文字间距 5、文字 12px",
            ExpandedPanelSpec.TabIconSize == 14
            && ExpandedPanelSpec.TabIconGap == 5
            && ExpandedPanelSpec.TabFontSize == 12);
        check("顶栏：页签热区留白 7/5、页签间距 6",
            ExpandedPanelSpec.TabPadX == 7
            && ExpandedPanelSpec.TabPadY == 5
            && ExpandedPanelSpec.TabGap == 6);
        check("顶栏：顶栏偏移 14/20，激活底 Accent 20% + 圆角 8",
            ExpandedPanelSpec.TabTopOffset == 14
            && ExpandedPanelSpec.TabLeftOffset == 20
            && ExpandedPanelSpec.TabActiveFillAlpha == 0.20
            && ExpandedPanelSpec.TabActiveCorner == 8);
        check("顶栏：时钟 12px、右侧留白 24、顶栏到内容区 40",
            ExpandedPanelSpec.ClockFontSize == 12
            && ExpandedPanelSpec.ClockRightMargin == 24
            && ExpandedPanelSpec.ContentTopOffset == 40);

        // ---------- 面板 ----------
        check("面板：圆角 20，内容区内边距 18/14/18",
            ExpandedPanelSpec.PanelCorner == 20
            && ExpandedPanelSpec.ContentPadX == 18
            && ExpandedPanelSpec.ContentPadTop == 14
            && ExpandedPanelSpec.ContentPadBottom == 18);

        // ---------- 监控页字号阶梯 ----------
        check("监控页：字号阶梯 14 / 12 / 11 / 10 / 9（mac 的口径）",
            ExpandedPanelSpec.MetricValueFont == 14
            && ExpandedPanelSpec.CardTitleFont == 12
            && ExpandedPanelSpec.SecondaryFont == 11
            && ExpandedPanelSpec.RowLabelFont == 10
            && ExpandedPanelSpec.StatusFont == 9);
        check("监控页：进度条 4px/圆角 2、走势 1.4px、框高 14",
            ExpandedPanelSpec.MeterHeight == 4
            && ExpandedPanelSpec.MeterCorner == 2
            && ExpandedPanelSpec.SparkStroke == 1.4
            && ExpandedPanelSpec.SparkHeight == 14);
        check("监控页：卡片标题图标 12、标题分隔线内缩 15",
            ExpandedPanelSpec.CardTitleIconSize == 12
            && ExpandedPanelSpec.CardTitleRuleInset == 15);
        check("监控页：进程榜行距 21、最多 5 行、图标 15、名称 11、数值 11",
            ExpandedPanelSpec.ProcessRowGap == 21
            && ExpandedPanelSpec.ProcessMaxRows == 5
            && ExpandedPanelSpec.ProcessIconSize == 15
            && ExpandedPanelSpec.ProcessNameFont == 11
            && ExpandedPanelSpec.ProcessValueFont == 11);
        check("监控页：排序分段控件 96×20 圆角 6",
            ExpandedPanelSpec.SegmentedWidth == 96
            && ExpandedPanelSpec.SegmentedHeight == 20
            && ExpandedPanelSpec.SegmentedCorner == 6);

        // ---------- 设备页 ----------
        check("设备页：卡片 132×150、间距 12、大号读数 26、状态行 10、空态 11.5",
            ExpandedPanelSpec.DeviceCardWidth == 132
            && ExpandedPanelSpec.DeviceCardHeight == 150
            && ExpandedPanelSpec.DeviceCardGap == 12
            && ExpandedPanelSpec.DevicePercentFont == 26
            && ExpandedPanelSpec.DeviceStatusFont == 10
            && ExpandedPanelSpec.DeviceEmptyFont == 11.5);

        // ---------- 小形象款式 ----------
        check("小形象：mac 的 8 款（Windows 补齐后 3 款）",
            ExpandedPanelSpec.PetStyles.Length == 8
            && ExpandedPanelSpec.PetStyles[0] == "classic"
            && ExpandedPanelSpec.PetStyles[4] == "pixel"
            && ExpandedPanelSpec.PetStyles[5] == "alien"
            && ExpandedPanelSpec.PetStyles[6] == "ghost"
            && ExpandedPanelSpec.PetStyles[7] == "frog");
        check("小形象：款式名与共享设置项一致（Settings.PetStyleValues）",
            ExpandedPanelSpec.PetStyles.Length == Island.App.Services.Settings.PetStyleValues.Length);
    }
}
