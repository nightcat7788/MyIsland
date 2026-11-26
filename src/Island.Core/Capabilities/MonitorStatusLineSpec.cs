using System;
using System.Collections.Generic;
using System.Linq;

namespace Island.Core.Capabilities;

/// <summary>展开页监控页**底部状态行**的一个字段位。</summary>
/// <param name="Key">对应 <see cref="CapabilityManifest.DataSources"/> 里的键（唯一真相源）。</param>
/// <param name="Label">显示标签。</param>
/// <param name="Note">为什么两端显示的可能不同（写清"为什么不同"，避免下次又靠猜）。</param>
public sealed record StatusLineSlot(string Key, string Label, string? Note = null);

/// <summary>
/// 展开页监控页**底部状态行**的两端唯一规格（2026-11-26 落地）。
///
/// 它是什么：2×2 卡片栅格**下方**的一条 9px 单行状态行 —— 放运行时长 / 今日流量 / 功耗 / 温度这类
/// 「不在卡片里、但值得一眼看到」的读数。
///
/// 为什么要有规格类：两端各写一份就会漂。这轮就抓到过一次 —— Windows 端注释写着「mac 的进程榜卡底同款」，
/// 实际 mac 根本没有那行。规格写在这里，两端都从这里取：
///   · 字段顺序与标签；
///   · 每个字段在本平台**是否出现**（平台不支持 ⇒ 直接不出现，不置灰、不留空位 —— 总纲 §1 的呈现规则）；
///   · 几何（字号 / 行高 / 间隔）。
///
/// 读不到 ≠ 不支持：支持但读不到时**显示「未知」**，绝不用 0 或默认值充数（项目硬红线）。
/// </summary>
public static class MonitorStatusLineSpec
{
    /// <summary>状态行字号（9px 一行）。</summary>
    public const double FontSize = 9;

    /// <summary>状态行行高（文字 + 上下留白）。</summary>
    public const double LineHeight = 14;

    /// <summary>字段位之间的间隔。</summary>
    public const double SlotGap = 14;

    /// <summary>状态行与上方卡片栅格底边之间的留白。</summary>
    public const double TopMargin = 6;

    /// <summary>
    /// 字段位清单（顺序即显示顺序）。
    /// 键对应 <see cref="CapabilityManifest.DataSources"/>，可用性由那份清单决定
    /// （平台专属项只在所属端出现；共享项两端都出现）。
    /// </summary>
    public static readonly IReadOnlyList<StatusLineSlot> Slots = new StatusLineSlot[]
    {
        new("cpu-power", "功耗", "Windows 走 LHM CPU 功耗传感器（需管理员）；mac 无对应数据源"),
        new("storage-temp", "SSD 温度", "Windows 侧 NVMe SMART IOCTL 有 0x1EA 蓝屏史 ⇒ 不出现；mac 走 IOHID NAND"),
        new("runtime", "运行时长", "两端同一实现：Environment.TickCount64（系统开机时长）"),
        new("today-traffic", "今日流量", "Windows 走 PDH 网络计数器累计（今日 ↓/↑）；mac 无每日流量口径"),
    };

    /// <summary>
    /// 某平台要显示的字段位（顺序同 <see cref="Slots"/>；平台不支持的直接不出现）。
    /// </summary>
    public static IReadOnlyList<StatusLineSlot> SlotsFor(bool macOS)
    {
        var byKey = CapabilityManifest.DataSources.ToDictionary(e => e.Key);
        return Slots
            .Where(s => byKey.TryGetValue(s.Key, out var entry) && CapabilityManifest.Available(entry, macOS))
            .ToList();
    }
}