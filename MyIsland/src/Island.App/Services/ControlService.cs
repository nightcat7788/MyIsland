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

    // ---------- 锁屏 / 睡眠 ----------
    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    /// <summary>立即锁屏。</summary>
    public static void Lock()
    {
        try { LockWorkStation(); } catch { }
    }

    /// <summary>立即睡眠。</summary>
    public static void Sleep()
    {
        try { SetSuspendState(false, true, false); } catch { }
    }

    // ---------- 系统深色模式（HKCU 个人化，免管理员） ----------
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>系统应用是否为浅色模式。</summary>
    public static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PersonalizeKey, false);
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>设置系统应用深/浅色模式（即时生效，无需重启）。</summary>
    public static void SetSystemLightTheme(bool light)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PersonalizeKey, true);
            key?.SetValue("AppsUseLightTheme", light ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch { }
    }

    // ---------- 夜灯（夜间模式）：CloudStore 二进制状态位，免管理员 ----------
    // 社区验证布局：REG_BINARY "Data"，偏移 18 = 开关（1 开 / 0 关）
    private const string NightLightKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate";

    /// <summary>夜灯当前状态（null = 读不到/不支持）。</summary>
    public static bool? GetNightLightOn()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(NightLightKeyPath, false);
            if (key?.GetValue("Data") is byte[] data && data.Length > 18)
                return data[18] != 0;
        }
        catch { }
        return null;
    }

    /// <summary>设置夜灯开关；返回是否写入成功（不支持返回 false）。</summary>
    public static bool SetNightLight(bool on)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(NightLightKeyPath, true);
            if (key?.GetValue("Data") is byte[] data && data.Length > 18)
            {
                byte want = (byte)(on ? 1 : 0);
                if (data[18] != want)
                {
                    data[18] = want;
                    key.SetValue("Data", data, Microsoft.Win32.RegistryValueKind.Binary);
                }
                return true;
            }
        }
        catch { }
        return false;
    }

    // ---------- 系统设置快捷链接 ----------
    /// <summary>打开指定系统设置页（ms-settings: 协议）。</summary>
    public static void OpenSettings(string page)
    {
        try
        {
            string uri = page switch
            {
                "network" => "ms-settings:network",
                "sound" => "ms-settings:sound",
                "display" => "ms-settings:display",
                "power" => "ms-settings:powersleep",
                "nightlight" => "ms-settings:nightlight",
                _ => "ms-settings:",
            };
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = uri, UseShellExecute = true });
        }
        catch { }
    }
}
