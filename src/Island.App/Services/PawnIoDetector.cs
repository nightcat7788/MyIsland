using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Island.App.Services;

/// <summary>PawnIO 官方微软签名硬件传感器驱动状态快照。</summary>
public sealed record PawnIoStatus(
    bool IsInstalled,
    bool IsRunning,
    bool IsDisabled,
    string Summary,
    string? ImagePath = null);

/// <summary>
/// 检测系统是否已注册/运行 PawnIO 驱动服务（供 LibreHardwareMonitorLib 0.9.6 统一读取 Intel MSR 与 AMD Ryzen SMN 温度）。
/// 纯只读注册表查询（无需管理员权限、不加载驱动、绝不抛异常）。
/// </summary>
public static class PawnIoDetector
{
    public const string DownloadUrl = "https://pawnio.eu/";
    private const string ServiceSubKey = @"SYSTEM\CurrentControlSet\Services\PawnIO";

    /// <summary>纯状态分类逻辑（供单元测试覆盖全部驱动状态分支）。</summary>
    internal static PawnIoStatus Classify(bool serviceKeyExists, int? startValue, bool isRunning, string? imagePath = null)
    {
        if (!serviceKeyExists)
            return new PawnIoStatus(false, false, false, "未检测到 PawnIO 驱动", null);

        // Windows 服务 Start=4 表示已禁用（SERVICE_DISABLED）
        if (startValue == 4)
            return new PawnIoStatus(true, false, true, "PawnIO 驱动服务已被禁用", imagePath);

        if (isRunning)
            return new PawnIoStatus(true, true, false, "PawnIO 驱动已就绪（运行中）", imagePath);

        return new PawnIoStatus(true, false, false, "PawnIO 驱动已安装（按需调用）", imagePath);
    }

    /// <summary>实时探测本机 PawnIO 驱动状态。</summary>
    public static PawnIoStatus Probe()
    {
        if (!OperatingSystem.IsWindows())
            return new PawnIoStatus(false, false, false, "非 Windows 平台无需 PawnIO");
        return ProbeWindows();
    }

    [SupportedOSPlatform("windows")]
    private static PawnIoStatus ProbeWindows()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceSubKey);
            if (key is null)
                return Classify(false, null, false, null);

            int? start = key.GetValue("Start") switch
            {
                int i => i,
                long l => (int)l,
                _ => null
            };
            string? imagePath = key.GetValue("ImagePath") as string;
            bool running = TryCheckRunning();
            return Classify(true, start, running, imagePath);
        }
        catch
        {
            return new PawnIoStatus(false, false, false, "未检测到 PawnIO 驱动");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryCheckRunning()
    {
        try
        {
            // 仅检查注册表动态控制项或设备节点，无需引用 System.ServiceProcess（零依赖、测试工程可直接链接）
            using var enumKey = Registry.LocalMachine.OpenSubKey(ServiceSubKey + @"\Enum");
            if (enumKey?.GetValue("Count") is int count && count > 0) return true;
        }
        catch { }
        return false;
    }
}
