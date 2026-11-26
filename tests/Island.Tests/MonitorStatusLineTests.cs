using System;
using System.Linq;
using Island.App.Controls;
using Island.Core.Capabilities;

namespace Island.Tests;

/// <summary>
/// 监控页**底部状态行**的规格用例（2026-11-26 task-e2）。
///
/// 状态行是 2×2 卡片栅格**下方**的一行 9px 读数（Windows 侧：功耗 / 主板温度 / 运行时长 / 今日流量）。
/// 两端共享的唯一规格是 <see cref="MonitorStatusLineSpec"/>（src\Island.Core\Capabilities），
/// 本文件把它当契约锁住：
///   · 字段位清单与顺序（顺序即显示顺序）；
///   · 平台可用性（Windows 显示功耗 / 主板温度 / 运行时长 / 今日流量；SSD 温度只在 mac 出现 ——
///     Windows 侧 NVMe SMART IOCTL 有 0x1EA 蓝屏史）；
///   · 几何常量（字号 9 / 行高 14 / 字段位间隔 14 / 与栅格的留白 6）；
///   · 取值格式规则，尤其是"读不到 ⇒ 未知"（绝不用 0 / 默认值 / 换算百分比充数 —— 项目硬红线）。
/// </summary>
internal static class MonitorStatusLineTests
{
    public static void Run(Action<string, bool> check)
    {
        CheckSlotList(check);
        CheckPlatformAvailability(check);
        CheckGeometry(check);
        CheckFormatting(check);
    }

    // ==================== 字段位清单 ====================

    private static void CheckSlotList(Action<string, bool> check)
    {
        var keys = MonitorStatusLineSpec.Slots.Select(s => s.Key).ToList();
        check("状态行：字段位清单与顺序（功耗 / 主板温度 / SSD 温度 / 运行时长 / 今日流量）",
            keys.SequenceEqual(new[] { "cpu-power", "board-temp", "storage-temp", "runtime", "today-traffic" }));

        var labels = MonitorStatusLineSpec.Slots.Select(s => s.Label).ToList();
        check("状态行：字段位标签",
            labels.SequenceEqual(new[] { "功耗", "主板温度", "SSD 温度", "运行时长", "今日流量" }));

        // 字段位的键必须都在能力清单里（CapabilityManifest.DataSources 是可用性的唯一真相源）
        var dataSourceKeys = CapabilityManifest.DataSources.Select(e => e.Key).ToHashSet();
        check("状态行：每个字段位的键都在能力清单 DataSources 里",
            MonitorStatusLineSpec.Slots.All(s => dataSourceKeys.Contains(s.Key)));

        // 字段位不许重复（重复 ⇒ 同一读数出现两次）
        check("状态行：字段位键不重复",
            keys.Distinct(StringComparer.Ordinal).Count() == keys.Count);
    }

    // ==================== 平台可用性 ====================

    private static void CheckPlatformAvailability(Action<string, bool> check)
    {
        var win = MonitorStatusLineSpec.SlotsFor(macOS: false).Select(s => s.Key).ToList();
        check("状态行：Windows 显示的字段位（功耗 / 主板温度 / 运行时长 / 今日流量）",
            win.SequenceEqual(new[] { "cpu-power", "board-temp", "runtime", "today-traffic" }));

        // SSD 温度在 Windows 侧不出现（NVMe SMART IOCTL 的 0x1EA 蓝屏史）
        check("状态行：Windows 侧不出现 SSD 温度",
            !win.Contains("storage-temp"));

        var mac = MonitorStatusLineSpec.SlotsFor(macOS: true).Select(s => s.Key).ToList();
        check("状态行：mac 显示的字段位（SSD 温度 / 运行时长）",
            mac.SequenceEqual(new[] { "storage-temp", "runtime" }));

        check("状态行：mac 侧不出现 Windows 专属项（功耗 / 主板温度 / 今日流量）",
            !mac.Contains("cpu-power") && !mac.Contains("board-temp") && !mac.Contains("today-traffic"));

        // 两端的显示顺序都必须是共享清单的子序列（顺序即显示顺序，不许各端自己排）
        var order = MonitorStatusLineSpec.Slots.Select(s => s.Key).ToList();
        check("状态行：两端的显示顺序都取自共享清单的顺序",
            IsSubSequence(win, order) && IsSubSequence(mac, order));
    }

    // ==================== 几何 ====================

    private static void CheckGeometry(Action<string, bool> check)
    {
        check("状态行：字号 9",
            MonitorStatusLineSpec.FontSize == 9);
        check("状态行：行高 14",
            MonitorStatusLineSpec.LineHeight == 14);
        check("状态行：字段位间隔 14",
            MonitorStatusLineSpec.SlotGap == 14);
        check("状态行：与栅格底边的留白 6",
            MonitorStatusLineSpec.TopMargin == 6);
        check("状态行：总高 = 留白 6 + 行高 14 = 20（高度预算要按它扣）",
            MonitorStatusLineSpec.TopMargin + MonitorStatusLineSpec.LineHeight == 20);
    }

    // ==================== 取值格式（读不到 ⇒ 未知） ====================

    private static void CheckFormatting(Action<string, bool> check)
    {
        check("状态行：未知状态词是「未知」",
            ExpandedPanelSpec.StatusUnknown == "未知");

        // 功耗（瓦，1 位小数）
        check("状态行：功耗 12.4 W",
            ExpandedPanelSpec.StatusPowerText(12.4) == "12.4 W");
        check("状态行：功耗读不到 ⇒ 未知（不用 0 充数）",
            ExpandedPanelSpec.StatusPowerText(null) == "未知"
            && ExpandedPanelSpec.StatusPowerText(0) == "未知"
            && ExpandedPanelSpec.StatusPowerText(double.NaN) == "未知");

        // 主板温度（摄氏度，取整）
        check("状态行：主板温度 45°C",
            ExpandedPanelSpec.StatusTempText(45) == "45°C"
            && ExpandedPanelSpec.StatusTempText(45.6) == "46°C");
        check("状态行：主板温度读不到 ⇒ 未知",
            ExpandedPanelSpec.StatusTempText(null) == "未知"
            && ExpandedPanelSpec.StatusTempText(0) == "未知");

        // 运行时长（不足 1 天按小时；够 1 天按"天 + 小时"）
        check("状态行：运行时长 6.5 小时",
            ExpandedPanelSpec.StatusUptimeText(6.5) == "6.5 小时");
        check("状态行：运行时长 ≥ 24 小时显示「1 天 3 小时」",
            ExpandedPanelSpec.StatusUptimeText(27) == "1 天 3 小时"
            && ExpandedPanelSpec.StatusUptimeText(24) == "1 天 0 小时"
            && ExpandedPanelSpec.StatusUptimeText(23.9) == "23.9 小时");
        check("状态行：运行时长读不到 ⇒ 未知",
            ExpandedPanelSpec.StatusUptimeText(null) == "未知"
            && ExpandedPanelSpec.StatusUptimeText(-1) == "未知");

        // 今日流量（↓ 下载 / ↑ 上传）
        check("状态行：今日流量 ↓40.0 GB ↑5.0 GB",
            ExpandedPanelSpec.StatusTrafficText(42_949_672_960, 5_368_709_120) == "↓40.0 GB ↑5.0 GB");
        check("状态行：今日流量读不到 ⇒ 未知",
            ExpandedPanelSpec.StatusTrafficText(null, null) == "未知"
            && ExpandedPanelSpec.StatusTrafficText(1024, null) == "未知");
        check("状态行：今日流量 0 字节是真实读数（照常显示，不是未知）",
            ExpandedPanelSpec.StatusTrafficText(0, 0) == "↓0 B ↑0 B");
    }

    // ==================== 小工具 ====================

    /// <summary>sub 是否是 seq 的子序列（顺序保持）。</summary>
    private static bool IsSubSequence(System.Collections.Generic.IEnumerable<string> sub,
        System.Collections.Generic.IEnumerable<string> seq)
    {
        var it = seq.GetEnumerator();
        foreach (var s in sub)
        {
            if (!it.MoveNext()) return false;
            while (!string.Equals(it.Current, s, StringComparison.Ordinal))
            {
                if (!it.MoveNext()) return false;
            }
        }
        return true;
    }
}
