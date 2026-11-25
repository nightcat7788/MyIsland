using System;
using System.IO;
using Island.App.Services;

namespace Island.Tests;

/// <summary>
/// 温度通道的回归用例（2026-11-23 三次 0x1EA THREAD_STUCK_IN_DEVICE_DRIVER 蓝屏）。
///
/// 事故链：以管理员运行 → 首次温度采样 → LoadLibrary("inpoutx64.dll")
///        → 该 dll 安装未签名的 inpoutx64.sys 内核驱动
///        → MapPhysToLin 把 PCIe ECAM/MMIO 物理区映射进进程并读
///        → 线程卡死在驱动里 → 蓝屏整机重启。
/// 而这条 SMN 直读是 **AMD 专有**的，本机是 Intel —— 在 Intel 上它永远读不到温度，
/// 属于"零收益、整机风险"。
///
/// 修复后的规矩：**先判 CPU 厂商，再碰驱动**。非 AMD 必须直接拒绝，
/// 绝不 LoadLibrary、绝不装驱动、绝不映射物理内存。
/// </summary>
internal static class ThermalChannelTests
{
    public static void Run(Action<string, bool> check)
    {
        // 独立读一次注册表，作为"真值"来比对厂商判定
        bool? registrySaysAmd = null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            string? vendor = key?.GetValue("VendorIdentifier") as string;
            if (!string.IsNullOrEmpty(vendor)) registrySaysAmd = vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase);
        }
        catch { /* 读不到就跳过比对 */ }

        bool serviceSaysAmd = InpOutSmnService.IsAmdCpu();
        if (registrySaysAmd is { } truth)
            check("SMN 厂商判定与注册表一致（AuthenticAMD/GenuineIntel）", serviceSaysAmd == truth);

        if (serviceSaysAmd)
        {
            // AMD 机器：只验证判定，不去真的加载驱动/映射物理内存（那正是会蓝屏的动作）
            check("AMD 机器上 SMN 通道被判定为可用（默认仍由 EnableSmnDirectRead 关闭）", serviceSaysAmd);
            return;
        }

        // 非 AMD：必须在这里就停住 —— 不加载 dll、不装驱动、不映射
        string driver = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "inpoutx64.sys");
        DateTime before = File.Exists(driver) ? File.GetLastWriteTime(driver) : DateTime.MinValue;

        using var service = new InpOutSmnService();
        bool read = service.TryReadCpuTemperature(out double celsius);
        string status = service.Status;

        check("非 AMD 机器上 SMN 直读不返回读数", !read && celsius == 0);
        check("非 AMD 机器上 SMN 直读在加载驱动之前就拒绝（状态里能看到 not AMD）",
            status.Contains("not AMD", StringComparison.OrdinalIgnoreCase));
        DateTime after = File.Exists(driver) ? File.GetLastWriteTime(driver) : DateTime.MinValue;
        check("非 AMD 机器上 SMN 直读没有落地/改动 inpoutx64.sys 驱动文件", after == before);
    }
}
