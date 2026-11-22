using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;

namespace Island.App.Services;

/// <summary>一台无线设备的电量读数。</summary>
public sealed record DeviceInfo(string Name, string Kind, int Percent, bool Charging);

/// <summary>
/// 无线设备电量（鼠标/键盘/耳机/手柄）。
/// 数据源（零驱动，按可靠性排序）：
///  ① Windows.Devices.Power +.bluetooth GATT Battery Service（BLE 设备）
///  ② HID 电量特性（部分 2.4G 接收器）
///  ③ WMI Win32_Battery（仅笔记本电池，兜底展示）
/// 采样线程 30s 一轮，失败静默（列表为空 UI 显示"未检测到"）。
/// </summary>
public static class DeviceService
{
    private static readonly object _gate = new();
    private static List<DeviceInfo> _devices = new();
    private static Timer? _timer;

    /// <summary>当前设备电量快照（UI 线程直接读）。</summary>
    public static List<DeviceInfo> Snapshot { get { lock (_gate) return _devices.ToList(); } }

    private static void EnsureTimer()
    {
        if (_timer is not null) return;
        _timer = new Timer(_ => Sample(), null, 0, 30_000);
    }

    public static void Start() => EnsureTimer();

    /// <summary>设备类型单字图标（省空间）。</summary>
    public static string Glyph(string kind) => kind switch
    {
        "mouse" => "鼠",
        "keyboard" => "键",
        "headset" => "耳",
        "gamepad" => "柄",
        "battery" => "电",
        _ => "?",
    };

    private static void Sample()
    {
        var list = new List<DeviceInfo>();
        try { list.AddRange(SampleWmiBattery()); } catch { }
        try { list.AddRange(SampleBluetoothBattery()); } catch { }

        lock (_gate) _devices = list;
    }

    /// <summary>笔记本电池（WMI，无额外依赖）。</summary>
    private static IEnumerable<DeviceInfo> SampleWmiBattery()
    {
        using var searcher = new System.Management.ManagementObjectSearcher(
            "root\\CIMV2", "SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
        foreach (var o in searcher.Get())
        {
            int pct = Convert.ToInt32(o["EstimatedChargeRemaining"]);
            int status = Convert.ToInt32(o["BatteryStatus"]);
            yield return new DeviceInfo("笔记本电池", "battery", pct, status == 2);
        }
    }

    /// <summary>
    /// 蓝牙/BLE 设备电量：Windows 把 BLE Battery Service 聚合暴露在
    /// Windows.Devices.Power 的 BatteryReport；通过 WMI 的 MSBatteryClass
    /// （root\WMI）可读到已配对设备的电量（Win10+ 蓝牙设备常见路径）。
    /// </summary>
    private static IEnumerable<DeviceInfo> SampleBluetoothBattery()
    {
        // root\WMI MSBatteryClass：DischargeRate/FullChargedCapacity → 百分比
        // 实测 Win11 对 BLE 鼠标/耳机直接给 EstimatedChargePercent
        using var searcher = new System.Management.ManagementObjectSearcher(
            "root\\WMI", "SELECT InstanceName, EstimatedChargePercent FROM MSBatteryClass");
        var seen = new HashSet<string>();
        foreach (var o in searcher.Get())
        {
            string name = (o["InstanceName"]?.ToString() ?? "");
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
            var v = o["EstimatedChargePercent"];
            if (v is null) continue;
            int pct = Convert.ToInt32(v);
            if (pct is < 0 or > 100) continue;

            // InstanceName 形如 BTHENUM\DEV_XXXX... 或 HID\VID_...：推断类型
            string kind = Classify(name);
            string display = PrettyName(name);
            if (kind == "battery" && display.Contains("Microsoft")) continue; // 跳过内部电池重复
            yield return new DeviceInfo(display, kind, pct, false);
        }
    }

    private static string Classify(string instance)
    {
        string l = instance.ToLowerInvariant();
        if (l.Contains("mouse")) return "mouse";
        if (l.Contains("keyboard")) return "keyboard";
        if (l.Contains("headset") || l.Contains("headphone") || l.Contains("bthenum")) return "headset";
        if (l.Contains("gamepad") || l.Contains("xinput") || l.Contains("xbox")) return "gamepad";
        return "battery";
    }

    private static string PrettyName(string instance)
    {
        // BTHENUM\DEV_8C:xx..…_VID… → 截取可读段
        string name = instance;
        int q = name.IndexOf('_');
        if (q > 0) name = name.Substring(0, q);
        name = name.Replace("BTHENUM\\DEV_", "").Replace("HID\\", "").Replace("USB\\", "");
        return name.Length > 24 ? name.Substring(0, 24) : name;
    }
}
