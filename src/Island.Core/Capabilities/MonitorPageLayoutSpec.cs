using System;
using System.Collections.Generic;
using System.Linq;

namespace Island.Core.Capabilities;

/// <summary>监控页某个卡片里的一个读数行。</summary>
/// <param name="Key">稳定键（测试与文档引用用）。</param>
/// <param name="Label">行标签（**两端一致**）。</param>
/// <param name="Field">读的是快照里的哪个字段（文档与静态核对用）。</param>
/// <param name="Windows">该行在 Windows 端是否出现。</param>
/// <param name="MacOS">该行在 macOS 端是否出现。</param>
/// <param name="Note">为什么两端可能不同（写清"为什么不同"，避免下次又靠猜）。</param>
public sealed record MonitorRowSlot(
    string Key,
    string Label,
    string Field,
    bool Windows = true,
    bool MacOS = true,
    string? Note = null);

/// <summary>监控页的一个卡片。</summary>
public sealed record MonitorCardSlot(
    string Key,
    string Title,
    IReadOnlyList<MonitorRowSlot> Rows,
    bool Windows = true,
    bool MacOS = true,
    string? Note = null);

/// <summary>
/// **展开页监控页的卡片分组 —— 两端唯一规格**（2026-11-26 统一分组时落地）。
///
/// 为什么要有它：此前两端的监控页是**各写一套**的 —— mac 是「占用 / 温度 / 上下速度 / 占用最高」，
/// Windows 是「CPU / 内存 / 上下速度 / 占用最高」。信息量接近，但**同一块页面看起来像两个产品**：
/// 卡片分组不同、每张卡装的东西不同。基准以 mac 现有实现为准，所以**统一成 mac 的分组**。
///
/// 统一之后，两端监控页的骨架完全一致：
///   卡1「占用」   CPU / 内存 / 磁盘      （卡头右上角可挂一个**平台专属**附加读数）
///   卡2「温度」   CPU / GPU / 存储      （存储温度 mac 专属：Windows 的 NVMe SMART IOCTL 有 0x1EA 蓝屏史）
///   卡3「上下速度」下行 / 上行
///   卡4「占用最高」进程榜
///
/// **平台专属读数保留**，且都挂在同一类位置：
///   · 卡头右上角的附加读数：Windows 挂 CPU 频率、mac 挂 GPU 占用（mac 原本就这么做）；
///   · 行内的附加读数：Windows 的 GPU 显存并进 GPU 温度那一行（mac 无显存可读）；
///   · 存储温度这一行：mac 有、Windows 不出现（总纲 §1：平台不支持 ⇒ 直接不出现）。
///
/// 部件形态（环表盘 vs 进度条 + 走势曲线）与配色身份**各端保留** —— 那是视觉身份，不是分组。
/// </summary>
public static class MonitorPageLayoutSpec
{
    /// <summary>卡片清单（顺序即显示顺序）。</summary>
    public static readonly IReadOnlyList<MonitorCardSlot> Cards = new MonitorCardSlot[]
    {
        new("usage", "占用", new MonitorRowSlot[]
        {
            new("cpu-usage", "CPU", "CpuUsage"),
            new("memory", "内存", "MemoryUsagePercent"),
            new("disk", "磁盘", "DiskUsagePercent"),
        }, Note: "卡头右上角可挂平台专属附加读数：Windows = CPU 频率，mac = GPU 占用"),

        new("temp", "温度", new MonitorRowSlot[]
        {
            new("cpu-temp", "CPU", "CpuTemp"),
            new("gpu-temp", "GPU", "GpuTemp"),
            new("storage-temp", "SSD", "SsdTemp", Windows: false, MacOS: true,
                Note: "Windows 不支持：LHM 的 NVMe SMART IOCTL 在本机触发 0x1EA 蓝屏两次 ⇒ 该行不出现"),
        }, Note: "Windows 的 GPU 显存并进 GPU 那一行（mac 无显存可读）"),

        new("speed", "上下速度", new MonitorRowSlot[]
        {
            new("down", "下行", "DownloadSpeed"),
            new("up", "上行", "UploadSpeed"),
        }, Note: "Windows 卡底另有「网络 / 电池」一行（平台专属，保留）"),

        new("topproc", "占用最高", new MonitorRowSlot[]
        {
            new("top-processes", "", "TopProcesses"),
        }, Note: "标题随排序切换：占用最高 · CPU / 占用最高 · 内存；Windows 卡底另有「进程 · 线程」"),
    };

    /// <summary>某平台要画的卡片（顺序同 <see cref="Cards"/>）。</summary>
    public static IReadOnlyList<MonitorCardSlot> CardsFor(bool macOS) =>
        Cards.Where(c => macOS ? c.MacOS : c.Windows).ToList();

    /// <summary>某张卡在某平台要画的行（顺序同 <see cref="MonitorCardSlot.Rows"/>）。</summary>
    public static IReadOnlyList<MonitorRowSlot> RowsFor(MonitorCardSlot card, bool macOS) =>
        card.Rows.Where(r => macOS ? r.MacOS : r.Windows).ToList();
}