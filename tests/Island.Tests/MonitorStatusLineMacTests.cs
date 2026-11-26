using System;
using System.Linq;
using Island.Core.Capabilities;

namespace Island.Tests;

/// <summary>
/// **mac 端底部状态行**（2026-11-26 task-e2-mac）。
///
/// 断言三件事，全部落在**纯逻辑**上（零 NuGet 测试工程不链 Avalonia 工程，只能断言这些）：
///   1. mac 端显示的字段位清单与顺序（SSD 温度 · 运行时长）—— 平台不支持的**直接不出现**，
///      不置灰、不留空位（总纲 §1 的呈现规则）；
///   2. 几何常量（字号 9 / 行高 14 / 间隔 14 / 上留白 6）；
///   3. 「读不到 ⇒ 未知」的格式化规则 —— **绝不用 0 / 默认值 / 换算百分比充数**（项目硬红线）。
/// </summary>
internal static class MonitorStatusLineMacTests
{
    public static void Run(Action<string, bool> check)
    {
        CheckMacSlots(check);
        CheckGeometry(check);
        CheckUnknownRule(check);
    }

    // ==================== 字段位：mac 端 ====================

    private static void CheckMacSlots(Action<string, bool> check)
    {
        var mac = MonitorStatusLineSpec.SlotsFor(macOS: true);

        // mac 端只显示 SSD 温度 · 运行时长，顺序即显示顺序
        check("mac 状态行字段位 = SSD 温度 · 运行时长（顺序一致）",
            mac.Count == 2 && mac[0].Key == "storage-temp" && mac[1].Key == "runtime");

        check("mac 状态行标签 = SSD 温度 · 运行时长",
            mac[0].Label == "SSD 温度" && mac[1].Label == "运行时长");

        // 平台专属项在 mac 没有数据源 ⇒ 直接不出现（不是置灰、不是留空位）
        check("mac 状态行不出现 Windows 专属字段位（功耗 / 主板温度 / 今日流量）",
            !mac.Any(s => s.Key is "cpu-power" or "board-temp" or "today-traffic"));

        // 两端共有的字段位两端都在
        check("运行时长在两端都出现",
            MonitorStatusLineSpec.SlotsFor(macOS: false).Any(s => s.Key == "runtime")
            && mac.Any(s => s.Key == "runtime"));

        // Windows 端不显示 SSD 温度（NVMe SMART IOCTL 蓝屏史）—— 两端差异由清单决定，不是各写一份
        check("Windows 端状态行不出现 SSD 温度",
            !MonitorStatusLineSpec.SlotsFor(macOS: false).Any(s => s.Key == "storage-temp"));
    }

    // ==================== 几何常量 ====================

    private static void CheckGeometry(Action<string, bool> check)
    {
        check("状态行字号 = 9", MonitorStatusLineSpec.FontSize == 9);
        check("状态行行高 = 14", MonitorStatusLineSpec.LineHeight == 14);
        check("字段位间隔 = 14", MonitorStatusLineSpec.SlotGap == 14);
        check("状态行与栅格底边留白 = 6", MonitorStatusLineSpec.TopMargin == 6);
    }

    // ==================== 「读不到 ⇒ 未知」 ====================

    private static void CheckUnknownRule(Action<string, bool> check)
    {
        // SSD 温度
        check("SSD 温度读不到 = 未知", MonitorStatusLineText.SsdTemp(null) == "未知");
        check("SSD 温度 0 视为读不到（不用 0 充数）", MonitorStatusLineText.SsdTemp(0) == "未知");
        check("SSD 温度负值视为读不到", MonitorStatusLineText.SsdTemp(-3) == "未知");
        check("SSD 温度 42.4 = 42°C", MonitorStatusLineText.SsdTemp(42.4) == "42°C");
        check("SSD 温度 42 = 42°C", MonitorStatusLineText.SsdTemp(42) == "42°C");

        // 运行时长
        check("运行时长读不到 = 未知", MonitorStatusLineText.Uptime(null) == "未知");
        check("运行时长 0 = 未知（不用 0 充数）", MonitorStatusLineText.Uptime(0) == "未知");
        check("运行时长负值 = 未知", MonitorStatusLineText.Uptime(-1) == "未知");
        check("运行时长 3.2 = 3.2 小时", MonitorStatusLineText.Uptime(3.2) == "3.2 小时");
        check("运行时长 0.5 = 0.5 小时", MonitorStatusLineText.Uptime(0.5) == "0.5 小时");
        check("运行时长 27 = 1 天 3 小时", MonitorStatusLineText.Uptime(27) == "1 天 3 小时");
        check("运行时长 24 = 1 天 0 小时", MonitorStatusLineText.Uptime(24) == "1 天 0 小时");
        check("运行时长 49.5 = 2 天 1 小时", MonitorStatusLineText.Uptime(49.5) == "2 天 1 小时");
    }
}
