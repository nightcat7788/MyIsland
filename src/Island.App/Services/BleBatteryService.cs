using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace Island.App.Services;

/// <summary>
/// 蓝牙 BLE 设备电量直读（GATT Battery Service 0x180F / Battery Level 0x2A19）
///（2026-11-25，数据源思路参考 ai-periph-monitor 的「蓝牙系统电量 + 不伪装」原则）。
///
/// 为什么在 MSBatteryClass 之外还有这条：Windows 只给**一部分**蓝牙设备把电量放进
/// MSBatteryClass（root\WMI）。本机的联想 BLE HID 设备（BTHLEDEVICE，HID over GATT）就没有 ——
/// MyIsland 此前因此显示不出它的电量。GATT 直读是系统 API（Windows.Devices.Bluetooth，零驱动），
/// 能把这一类补上。
///
/// 节流与诚实：
///  · 每轮采样进行非缓存读取，确认电量服务仍可访问；
///  · 枚举消失或读取失败立即移除；缓存最长 6 分钟，覆盖后台 5 分钟采样间隔；
///  · 读不到 / 回包非法（&gt;100）/ 服务打不开：不进列表、不伪装，失败原因写 thermal-diag.log。
///
/// 不建线程：DeviceService 的采样节拍调 <see cref="Warm"/>，扫描在蓝牙栈的异步链上完成。
/// ⚠️ 本文件引用 Windows Runtime 类型（TFM 的 windows10.0.19041.0 才有），**不链接进测试工程**。
/// </summary>
public static class BleBatteryService
{
    private static readonly Guid BatteryServiceUuid = new("0000180F-0000-1000-8000-00805F9B34FB");
    private static readonly Guid BatteryLevelUuid = new("00002A19-0000-1000-8000-00805F9B34FB");
    internal static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(6);
    public static event Action? Updated;
    private static readonly TimeSpan ScanThrottle = TimeSpan.FromSeconds(25);

    private static readonly object _gate = new();
    private static readonly Dictionary<string, (DeviceInfo Info, DateTime At)> _cache = new();
    private static Task? _scan;
    private static DateTime _lastScanStart = DateTime.MinValue;

    /// <summary>触发一轮后台扫描（有节流：扫描在途 / 距上次启动不足 25 秒时直接返回）。</summary>
    public static void Warm()
    {
        lock (_gate)
        {
            if (_scan is { IsCompleted: false }) return;
            if (DateTime.Now - _lastScanStart < ScanThrottle) return;
            _lastScanStart = DateTime.Now;
            _scan = Task.Run(ScanAsync);
        }
    }

    /// <summary>当前可信的 BLE 电量读数（缓存的、未过期的）。</summary>
    public static List<DeviceInfo> Snapshot()
    {
        lock (_gate) return _cache.Values.Where(v => DateTime.Now - v.At < FreshFor).Select(v => v.Info).ToList();
    }

    private static async Task ScanAsync()
    {
        try
        {
            string selector = GattDeviceService.GetDeviceSelectorFromUuid(BatteryServiceUuid);
            // 顺带要父设备 Id：AEP 服务条目自己往往没有名字，友好名在父容器（BLE 设备）上。
            // ⚠️ 实机（Win11 26300）带这个额外属性的查询会抛 COMException 0x80070490"找不到元素"——
            //    属性查询失败就退回不带属性的查询：电量读数比名字重要，名字走兜底（ResolveNameAsync）。
            DeviceInformationCollection infos;
            try
            {
                infos = await DeviceInformation.FindAllAsync(selector,
                    new List<string> { "System.Devices.AepService.Parent", "System.ItemNameDisplay" });
            }
            catch
            {
                infos = await DeviceInformation.FindAllAsync(selector);
            }
            var present = infos.Select(info => info.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            lock (_gate)
                foreach (string id in _cache.Keys.Where(id => !present.Contains(id)).ToList())
                    _cache.Remove(id);
            Updated?.Invoke();
            int found = 0;
            var names = new List<string>();
            foreach (var info in infos)
            {
                int? pct = info.IsEnabled ? await ReadBatteryAsync(info) : null;
                if (pct is null)
                {
                    lock (_gate) _cache.Remove(info.Id);
                    Updated?.Invoke();
                    continue;
                }
                var now = DateTime.Now;
                string name = await ResolveNameAsync(info);
                var row = new DeviceInfo(
                    string.IsNullOrWhiteSpace(name) ? "蓝牙设备" : name,
                    KindOf(name), pct.Value, false) { ObservedAt = now };
                lock (_gate) _cache[info.Id] = (row, now);
                Updated?.Invoke();
                found++;
                names.Add(row.Name);
            }
            if (found > 0)
                DeviceService.LogLine($"BLE GATT 电量：本轮新读 {found} 台（共 {infos.Count} 台暴露电量服务）: {string.Join(" / ", names)}");
        }
        catch (Exception ex)
        {
            lock (_gate) _cache.Clear();
            Updated?.Invoke();
            // 枚举失败时无法确认设备仍可访问，旧读数不能继续冒充当前状态。
            DeviceService.LogLineThrottled($"BLE GATT 电量扫描失败：{ex.GetType().Name}: {ex.Message}", TimeSpan.FromMinutes(30));
        }
    }

    /// <summary>读一台设备的 Battery Level。任何一步不成功都返回 null（不伪装）。</summary>
    private static async Task<int?> ReadBatteryAsync(DeviceInformation info)
    {
        GattDeviceService? service = null;
        try
        {
            service = await GattDeviceService.FromIdAsync(info.Id);
            if (service is null) return null;   // 无权访问（多半是系统占用），不重试轰炸
            var result = await service.GetCharacteristicsForUuidAsync(BatteryLevelUuid, BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0) return null;
            var read = await result.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
            if (read.Status != GattCommunicationStatus.Success || read.Value.Length != 1) return null;
            using var reader = Windows.Storage.Streams.DataReader.FromBuffer(read.Value);
            int pct = reader.ReadByte();
            return pct is >= 0 and <= 100 ? pct : null;
        }
        catch (Exception ex)
        {
            DeviceService.LogLineThrottled($"BLE GATT 电量：{info.Name} 读取失败（不显示）：{ex.Message}", TimeSpan.FromMinutes(30));
            return null;
        }
        finally { try { service?.Dispose(); } catch { } }
    }

    /// <summary>
    /// 设备名：AEP 服务条目自己往往没有名字（本机的联想设备 Name 就是空的）。
    /// 依次试：条目自己的 System.ItemNameDisplay → 父容器（BLE 设备）的 Name → 空着落"蓝牙设备"。
    /// </summary>
    private static async Task<string> ResolveNameAsync(DeviceInformation info)
    {
        try
        {
            if (info.Properties.TryGetValue("System.ItemNameDisplay", out var display)
                && display is string displayName && !string.IsNullOrWhiteSpace(displayName))
                return displayName;
        }
        catch { }
        try
        {
            if (info.Properties.TryGetValue("System.Devices.AepService.Parent", out var parentIdObj)
                && parentIdObj is string parentId && parentId.Length > 0)
            {
                var parent = await DeviceInformation.CreateFromIdAsync(parentId);
                if (!string.IsNullOrWhiteSpace(parent.Name)) return parent.Name;
            }
        }
        catch { /* 属性不存在 / 容器查不到：走兜底名 */ }
        return info.Name ?? "";
    }

    /// <summary>按设备名猜类型（只影响图标与文案）。猜不出给 "ble"：显示"蓝牙设备"，不硬安成别的。</summary>
    private static string KindOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return "ble";
        string lower = name.ToLowerInvariant();
        if (lower.Contains("鼠标") || lower.Contains("mouse")) return "mouse";
        if (lower.Contains("键盘") || lower.Contains("keyboard")) return "keyboard";
        if (lower.Contains("耳机") || lower.Contains("headset") || lower.Contains("headphone") || lower.Contains("earbuds")) return "headset";
        if (lower.Contains("手柄") || lower.Contains("controller") || lower.Contains("gamepad")) return "gamepad";
        return "ble";
    }
}
