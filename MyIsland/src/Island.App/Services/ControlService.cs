using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Island.App.Services;

/// <summary>
/// 系统控制：亮度（WMI MonitorBrightness，外接显示器不支持则忽略）/
/// WLAN 开关（WlanFree…走 WMI MSNdis 或 netsh——不读 SSID 不触发位置）/蓝牙开关（WinRT Radio）。
///wisland 同款约束：不读 Wi-Fi 名称（netsh show interfaces 在 Win11 24H2 触发位置访问）。
/// </summary>
public static class ControlService
{
    // ---------- 亮度 ----------
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr monitor, ref uint min, ref uint cur, ref uint max);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint value);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR { public IntPtr hPhysicalMonitor; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string desc; }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    private static IntPtr _firstMonitor = IntPtr.Zero;

    private static void EnsureMonitor()
    {
        if (_firstMonitor != IntPtr.Zero) return;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdcMon, ref RECT rectMon, IntPtr dataMon) =>
            {
                _firstMonitor = hMon;
                return false;
            }, IntPtr.Zero);
    }

    /// <summary>当前亮度 0..100（外接显示器/驱动不支持返回 null）。</summary>
    public static uint? GetBrightness()
    {
        try
        {
            EnsureMonitor();
            if (_firstMonitor == IntPtr.Zero) return null;
            uint count = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(_firstMonitor, ref count) || count == 0) return null;
            var mons = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(_firstMonitor, count, mons)) return null;
            uint min = 0, cur = 0, max = 0;
            bool ok = GetMonitorBrightness(mons[0].hPhysicalMonitor, ref min, ref cur, ref max);
            DestroyPhysicalMonitors(count, mons);
            if (!ok || max == min) return null;
            return (cur - min) * 100 / (max - min);
        }
        catch { return null; }
    }

    public static bool SetBrightness(uint percent)
    {
        try
        {
            EnsureMonitor();
            if (_firstMonitor == IntPtr.Zero) return false;
            uint count = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(_firstMonitor, ref count)) return false;
            var mons = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(_firstMonitor, count, mons)) return false;
            uint min = 0, cur = 0, max = 0;
            bool ok = GetMonitorBrightness(mons[0].hPhysicalMonitor, ref min, ref cur, ref max);
            if (ok && max > min)
                ok = SetMonitorBrightness(mons[0].hPhysicalMonitor, min + (max - min) * percent / 100);
            DestroyPhysicalMonitors(count, mons);
            return ok;
        }
        catch { return false; }
    }

    // ---------- WLAN 开关（netsh，仅开关不读名称） ----------
    public static bool WlanEnabled
    {
        get
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netsh", "interface show interface")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi);
                string outp = p!.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                foreach (var line in outp.Split('\n'))
                    if (line.Contains("Wi-Fi") || line.Contains("WLAN"))
                        return line.Contains("已启用") || line.Contains("enabled", StringComparison.OrdinalIgnoreCase);
                return false;
            }
            catch { return true; }
        }
    }

    public static void SetWlan(bool enable)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netsh",
                $"interface set interface \"Wi-Fi\" {(enable ? "enable" : "disable")}")
            { UseShellExecute = true, Verb = "runas", CreateNoWindow = true };   // 需管理员
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* 无管理员权限：静默失败 */ }
    }

    // ---------- 蓝牙开关（服务级：bthserv） ----------
    public static bool BluetoothEnabled
    {
        get
        {
            try
            {
                using var svc = new System.ServiceProcess.ServiceController("bthserv");
                return svc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
            }
            catch { return false; }
        }
    }

    public static void SetBluetooth(bool enable)
    {
        try
        {
            using var svc = new System.ServiceProcess.ServiceController("bthserv");
            if (enable && svc.Status != System.ServiceProcess.ServiceControllerStatus.Running)
                svc.Start();
            else if (!enable && svc.Status == System.ServiceProcess.ServiceControllerStatus.Running)
            { svc.Stop(); }
        }
        catch { /* 无权限：静默失败（wisland 同款：点磁贴开系统设置兜底） */ }
    }

    public static void OpenBluetoothSettings() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        { FileName = "ms-settings:bluetooth", UseShellExecute = true });

    // ---------- 静音 ----------
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
    private const int WM_APPCOMMAND = 0x0319;
    private const int APPCOMMAND_VOLUME_MUTE = 0x800;
    public static void ToggleMute() =>
        SendMessageW(GetDesktopWindow(), WM_APPCOMMAND, IntPtr.Zero, (IntPtr)(APPCOMMAND_VOLUME_MUTE << 16));
    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
