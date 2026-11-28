using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Island.App.Services;

/// <summary>一台设备的电量读数（本机电池或蓝牙/BLE/无线外设）。</summary>
public sealed record DeviceInfo(string Name, string Kind, int Percent, bool Charging, string? Subtitle = null, bool PluggedIn = false);

/// <summary>
/// 本机与无线外设电量监控服务（零驱动、免管理员）：
///  ① Win32 GetSystemPowerStatus + WMI Win32_Battery（修正 DeviceID 字段）—— 笔记本内置电池百分比、充电/接电状态与剩余续航；
///  ② Win32 CfgMgr32 PnP 统一设备属性（DEVPKEY_Bluetooth_Battery {104EA319-6EE2-4701-BD47-8DDBF425BBE5},2）
///     —— Windows 10/11 原生蓝牙 HFP 耳机与 BLE GATT 0x180F 无线鼠标/键盘/耳塞/手柄真实电量（与系统设置同源）；
///  ③ 2.4GHz / USB HID 无线外设协议引擎（HidPeripheralBatteryService，参考 Sapd/HeadsetControl）
///     —— Logitech G / Lightspeed / Unifying / Bolt、Corsair Void/HS、SteelSeries Arctis/Nova、HyperX Cloud 等；
///  ④ XInput1_4 XInputGetBatteryInformation —— Xbox / XInput 2.4GHz 无线手柄电量兜底。
/// </summary>
public static class DeviceService
{
    private static readonly object _gate = new();
    internal static readonly object DiagLogGate = new();
    private static List<DeviceInfo> _devices = new();
    private static List<DeviceInfo> _cachedWireless = new();
    private static Timer? _timer;
    private static bool _wanted;
    private static volatile bool _highFreq;
    private static int _sampleCycle;
    private static int _sampling;

    /// <summary>每次后台采样刷新完成后触发（供 UI 立即重绘设备页、监控页与收起胶囊）。</summary>
    public static event Action? Updated;

    private static List<DeviceInfo>? _snapCache;
    private static DateTime _snapCacheAt = DateTime.MinValue;
    private static readonly TimeSpan SnapCacheTtl = TimeSpan.FromSeconds(2);

    /// <summary>当前设备电量快照（UI 线程直接读，2 秒缓存，调用方只读）。</summary>
    public static List<DeviceInfo> Snapshot
    {
        get
        {
            lock (_gate)
            {
                if (_snapCache is not null && DateTime.Now - _snapCacheAt < SnapCacheTtl)
                    return _snapCache;
                _snapCache = _devices.ToList();
                _snapCacheAt = DateTime.Now;
                return _snapCache;
            }
        }
    }

    public static bool Running { get { lock (_gate) return _timer is not null; } }

    /// <summary>启动时立即后台预采一次，确保首次打开面板即有电池与无线外设数据。</summary>
    public static void PrimeOnce() => Task.Run(Sample);

    /// <summary>立即触发一次后台刷新（用于用户手动刷新或设备热插拔）。</summary>
    public static void RefreshNow() => Task.Run(() => Sample(forceWirelessScan: true));

    /// <summary>声明是否处于高频查看状态（展开设备页时每 15s 扫全量；收起态且无外设时自动降频至 45s 省电省 CPU）。</summary>
    public static void SetHighFrequency(bool highFreq) => _highFreq = highFreq;

    private static void EnsureTimer()
    {
        lock (_gate)
        {
            if (_timer is not null) return;
            _timer = new Timer(_ => Sample(), null, 0, 15_000);
        }
    }

    public static void Start()
    {
        lock (_gate) { _wanted = true; }
        EnsureTimer();
    }

    public static void Stop()
    {
        lock (_gate)
        {
            _wanted = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    public static void SetWanted(bool wanted)
    {
        lock (_gate)
        {
            if (_wanted == wanted) return;
            _wanted = wanted;
            if (!wanted)
            {
                _timer?.Dispose();
                _timer = null;
                return;
            }
        }
        EnsureTimer();
    }

    public static string Glyph(string kind) => kind switch
    {
        "mouse" => "鼠",
        "keyboard" => "键",
        "headset" or "earbuds" => "耳",
        "gamepad" => "柄",
        "battery" => "电",
        _ => "电",
    };

    private static void Sample() => Sample(forceWirelessScan: false);

    private static void Sample(bool forceWirelessScan)
    {
        if (Interlocked.CompareExchange(ref _sampling, 1, 0) != 0) return;
        bool changed = false;
        try
        {
            var list = new List<DeviceInfo>();
            try
            {
                if (SampleBuiltinBattery() is { } bat)
                    list.Add(bat);
            }
            catch (Exception ex) { Log($"BuiltinBattery 异常：{ex.GetType().Name}: {ex.Message}"); }

            // 智能节流：若收起态且上轮未发现无线外设，每 3 轮（45s）才做一次全量 PnP+HID 扫描；
            // 有无线外设或用户打开设备页时每轮（15s）实时刷新，兼顾灵敏度与极低待机 CPU 占用。
            int cycle = Interlocked.Increment(ref _sampleCycle);
            bool hasKnownWireless;
            lock (_gate) hasKnownWireless = _cachedWireless.Count > 0;
            bool scanWireless = forceWirelessScan || _highFreq || hasKnownWireless || (cycle % 3 == 1);

            if (scanWireless)
            {
                var wireless = new List<DeviceInfo>();
                try { wireless.AddRange(SamplePnpBluetoothBatteries()); }
                catch (Exception ex) { Log($"PnP 蓝牙电量读取异常：{ex.GetType().Name}: {ex.Message}"); }

                try
                {
                    var hidDevs = HidPeripheralBatteryService.SampleAll(Log);
                    foreach (var hd in hidDevs)
                    {
                        bool dup = wireless.Any(existing =>
                            string.Equals(existing.Name.Trim(), hd.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (!dup) wireless.Add(hd);
                    }
                }
                catch (Exception ex) { Log($"HID 无线外设电量读取异常：{ex.GetType().Name}: {ex.Message}"); }

                try
                {
                    bool hasGamepad = wireless.Any(d => d.Kind == "gamepad");
                    if (!hasGamepad)
                        wireless.AddRange(SampleXInputGamepads());
                }
                catch { }

                lock (_gate) _cachedWireless = wireless;
                list.AddRange(wireless);
            }
            else
            {
                lock (_gate) list.AddRange(_cachedWireless);
            }

            lock (_gate)
            {
                changed = !SequenceEqual(_devices, list);
                _devices = list;
                _snapCache = null;
            }
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
        if (changed)
        {
            try { Updated?.Invoke(); } catch { }
        }
    }

    private static bool SequenceEqual(List<DeviceInfo> a, List<DeviceInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    // ==================== 1. 本机内置电池（Win32 GetSystemPowerStatus 主通道 + WMI 兜底） ====================

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 0=Offline, 1=Online, 255=Unknown
        public byte BatteryFlag;         // 8=Charging, 128=NoSystemBattery, 255=Unknown
        public byte BatteryLifePercent;  // 0..100, 255=Unknown
        public byte SystemStatusFlag;
        public int BatteryLifeTime;      // 剩余放电秒数，-1=未知
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    private static DeviceInfo? SampleBuiltinBattery()
    {
        if (GetSystemPowerStatus(out var sps))
        {
            // BatteryFlag 含 128 代表台式机无系统电池
            if (sps.BatteryFlag != 255 && (sps.BatteryFlag & 128) != 0)
                return null;

            if (sps.BatteryLifePercent <= 100)
            {
                int pct = sps.BatteryLifePercent;
                bool online = sps.ACLineStatus == 1;
                bool charging = (sps.BatteryFlag & 8) != 0 || (online && pct < 99);
                string? sub = charging ? "充电中"
                    : online ? (pct >= 99 ? "已充满 · 电源供电" : "已接通电源")
                    : sps.BatteryLifeTime > 60
                        ? $"剩余 {sps.BatteryLifeTime / 3600}h {(sps.BatteryLifeTime % 3600) / 60:D2}m"
                        : null;
                return new DeviceInfo("本机电池", "battery", pct, charging, sub, online);
            }
        }

        // WMI 兜底（注意 Win32_Battery 主键列名为 DeviceID 而非 InstanceName）
        int? lowest = null;
        bool anyCharging = false;
        bool anyOnline = false;
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "root\\CIMV2", "SELECT DeviceID, EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
            using var results = searcher.Get();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Management.ManagementBaseObject o in results)
            {
                using (o)
                {
                    string id = GetProp(o, "DeviceID")?.ToString() ?? "";
                    if (id.Length > 0 && !seen.Add(id)) continue;
                    int? p = ReadPercent(o, "EstimatedChargeRemaining");
                    if (p is null) continue;
                    lowest = lowest is null ? p : Math.Min(lowest.Value, p.Value);
                    int st = ToInt(GetProp(o, "BatteryStatus"));
                    if (st is 6 or 7 or 8 or 9) anyCharging = true;
                    if (st == 2) anyOnline = true;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Win32_Battery 兜底查询失败：{ex.GetType().Name}: {ex.Message}");
        }
        if (lowest is null) return null;
        string? wmiSub = anyCharging ? "充电中" : anyOnline ? "已接通电源" : null;
        return new DeviceInfo("本机电池", "battery", lowest.Value, anyCharging, wmiSub, anyOnline);
    }

    // ==================== 2. PnP 统一设备属性：蓝牙 HFP + BLE GATT 0x180F 无线外设 ====================

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
        public DEVPROPKEY(Guid fmtid, uint pid) { this.fmtid = fmtid; this.pid = pid; }
    }

    private const uint CR_SUCCESS = 0;
    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
    private const uint DEVPROP_TYPE_BYTE = 0x00000003;
    private const uint DEVPROP_TYPE_GUID = 0x0000000D;
    private const uint DEVPROP_TYPE_BOOLEAN = 0x00000011;
    private const uint DEVPROP_TYPE_STRING = 0x00000012;

    // Windows 10/11 系统「蓝牙和其他设备」页面读取无线电量的核心属性键
    private static readonly DEVPROPKEY PKEY_Bluetooth_Battery =
        new(new Guid(0x104EA319, 0x6EE2, 0x4701, 0xBD, 0x47, 0x8D, 0xDB, 0xF4, 0x25, 0xBB, 0xE5), 2);
    private static readonly DEVPROPKEY PKEY_Device_FriendlyName =
        new(new Guid(0xA45C254E, 0xDF1C, 0x4EFD, 0x80, 0x20, 0x67, 0xD1, 0x46, 0xA8, 0x50, 0xE0), 14);
    private static readonly DEVPROPKEY PKEY_Device_DeviceDesc =
        new(new Guid(0xA45C254E, 0xDF1C, 0x4EFD, 0x80, 0x20, 0x67, 0xD1, 0x46, 0xA8, 0x50, 0xE0), 2);
    private static readonly DEVPROPKEY PKEY_Device_Class =
        new(new Guid(0xA45C254E, 0xDF1C, 0x4EFD, 0x80, 0x20, 0x67, 0xD1, 0x46, 0xA8, 0x50, 0xE0), 9);
    private static readonly DEVPROPKEY PKEY_Device_ContainerId =
        new(new Guid(0x8C7ED206, 0x3F8A, 0x4827, 0xB3, 0xAB, 0xAE, 0x9E, 0x1F, 0xAE, 0xFC, 0x6C), 2);
    private static readonly DEVPROPKEY PKEY_Device_IsConnected =
        new(new Guid(0x83DA6326, 0x97A6, 0x4088, 0x94, 0x53, 0xA1, 0x92, 0x3F, 0x57, 0x3B, 0x29), 15);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string? pszFilter, char[] buffer, uint bufferLen, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    private static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(
        uint dnDevInst, ref DEVPROPKEY propertyKey, out uint propertyType,
        byte[]? propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    private static IEnumerable<DeviceInfo> SamplePnpBluetoothBatteries()
    {
        var results = new List<DeviceInfo>();
        if (CM_Get_Device_ID_List_SizeW(out uint len, null, CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS || len <= 1)
            return results;

        var buf = new char[len];
        if (CM_Get_Device_ID_ListW(null, buf, len, CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS)
            return results;

        var byContainer = new Dictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);

        int start = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            if (buf[i] != '\0') continue;
            int segLen = i - start;
            if (segLen == 0) break;
            string instanceId = new string(buf, start, segLen);
            start = i + 1;

            // 仅筛查蓝牙、BLE GATT 与 HID 外设节点，跳过数千个主板/PCI 系统节点
            if (!IsWirelessCandidate(instanceId)) continue;
            if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CR_SUCCESS) continue;

            byte? pct = ReadDevNodeByte(devInst, PKEY_Bluetooth_Battery);
            if (pct is null || pct.Value > 100) continue;

            // 沿父节点向上回溯最多 4 层，聚合真实设备名称、连接状态、设备分类与物理容器 ID
            string? friendly = null;
            string? desc = null;
            string? devClass = null;
            Guid? containerId = null;
            bool explicitlyDisconnected = false;

            uint cur = devInst;
            for (int depth = 0; depth < 4; depth++)
            {
                if (ReadDevNodeBool(cur, PKEY_Device_IsConnected) is false)
                {
                    explicitlyDisconnected = true;
                    break;
                }
                containerId ??= ReadDevNodeGuid(cur, PKEY_Device_ContainerId);
                string? fn = ReadDevNodeString(cur, PKEY_Device_FriendlyName);
                if (!string.IsNullOrWhiteSpace(fn) && !IsGenericServiceName(fn))
                    friendly ??= fn.Trim();
                string? dc = ReadDevNodeString(cur, PKEY_Device_DeviceDesc);
                if (!string.IsNullOrWhiteSpace(dc) && !IsGenericServiceName(dc))
                    desc ??= dc.Trim();
                string? cl = ReadDevNodeString(cur, PKEY_Device_Class);
                if (!string.IsNullOrWhiteSpace(cl) && !cl.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase)
                    && !cl.Equals("System", StringComparison.OrdinalIgnoreCase))
                    devClass ??= cl.Trim();

                if (CM_Get_Parent(out uint parent, cur, 0) != CR_SUCCESS || parent == 0 || parent == cur)
                    break;
                cur = parent;
            }

            if (explicitlyDisconnected) continue;

            string rawName = friendly ?? desc ?? "";
            string kind = ClassifyDevice(rawName, devClass ?? "", instanceId);
            string displayName = !string.IsNullOrWhiteSpace(rawName) ? rawName : PrettyName(instanceId, kind);
            string dedupKey = containerId is { } cid && cid != Guid.Empty && !IsSystemContainer(cid)
                ? cid.ToString("N")
                : ExtractId(instanceId) is { Length: > 0 } mac ? mac : displayName;

            var info = new DeviceInfo(displayName, kind, pct.Value, false, "蓝牙连接");
            if (!byContainer.TryGetValue(dedupKey, out var existing)
                || (existing.Kind == "battery" && info.Kind != "battery")
                || (friendly is not null && existing.Name.StartsWith("蓝牙", StringComparison.Ordinal)))
            {
                byContainer[dedupKey] = info;
            }
        }

        results.AddRange(byContainer.Values);
        return results;
    }

    private static bool IsWirelessCandidate(string id)
    {
        return id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenericServiceName(string name)
    {
        return name.Contains("Generic Attribute", StringComparison.OrdinalIgnoreCase)
            || name.Contains("通用属性", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Generic Access", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Battery Service", StringComparison.OrdinalIgnoreCase)
            || name.Contains("电池服务", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase)
            || name.Equals("符合 HID 标准的供应商定义设备", StringComparison.OrdinalIgnoreCase)
            || name.Equals("蓝牙 LE 设备", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Bluetooth LE Device", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemContainer(Guid g)
    {
        // {00000000-0000-0000-FFFF-FFFFFFFFFFFF} 表示本机内部容器
        byte[] b = g.ToByteArray();
        for (int i = 0; i < 8; i++) if (b[i] != 0) return false;
        return true;
    }

    private static byte? ReadDevNodeByte(uint devInst, DEVPROPKEY key)
    {
        uint size = 4;
        byte[] buf = new byte[4];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, buf, ref size, 0) == CR_SUCCESS
            && type == DEVPROP_TYPE_BYTE && size >= 1)
            return buf[0];
        return null;
    }

    private static bool? ReadDevNodeBool(uint devInst, DEVPROPKEY key)
    {
        uint size = 4;
        byte[] buf = new byte[4];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, buf, ref size, 0) == CR_SUCCESS
            && type == DEVPROP_TYPE_BOOLEAN && size >= 1)
            return buf[0] != 0;
        return null;
    }

    private static Guid? ReadDevNodeGuid(uint devInst, DEVPROPKEY key)
    {
        uint size = 16;
        byte[] buf = new byte[16];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, buf, ref size, 0) == CR_SUCCESS
            && type == DEVPROP_TYPE_GUID && size == 16)
            return new Guid(buf);
        return null;
    }

    private static string? ReadDevNodeString(uint devInst, DEVPROPKEY key)
    {
        uint size = 0;
        CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, null, ref size, 0);
        if (size < 2 || size > 1024) return null;
        byte[] buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(devInst, ref key, out type, buf, ref size, 0) != CR_SUCCESS
            || type != DEVPROP_TYPE_STRING)
            return null;
        return Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
    }

    // ==================== 3. XInput 2.4GHz / 无线手柄兜底 ====================

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetBatteryInformation(uint dwUserIndex, byte devType, out XINPUT_BATTERY_INFORMATION pBatteryInformation);

    private static IEnumerable<DeviceInfo> SampleXInputGamepads()
    {
        var list = new List<DeviceInfo>();
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                if (XInputGetBatteryInformation(i, 0 /* GAMEPAD */, out var info) != 0) continue;
                // 2=Alkaline, 3=NiMH（排除 0=Disconnected 与 1=Wired）
                if (info.BatteryType is not (2 or 3)) continue;
                int pct = info.BatteryLevel switch
                {
                    0 => 10,
                    1 => 30,
                    2 => 65,
                    _ => 100,
                };
                list.Add(new DeviceInfo(i == 0 ? "无线控制器" : $"无线控制器 #{i + 1}", "gamepad", pct, false, "无线接收器"));
            }
        }
        catch { /* xinput1_4 缺失或不可用时静默跳过 */ }
        return list;
    }

    // ==================== 辅助分类与格式化 ====================

    private static string ClassifyDevice(string name, string devClass, string instance)
    {
        string all = $"{name} {devClass} {instance}".ToLowerInvariant();
        if (all.Contains("airpods") || all.Contains("buds") || all.Contains("earbud") || all.Contains("wf-1000"))
            return "earbuds";
        if (all.Contains("headset") || all.Contains("headphone") || all.Contains("earphone")
            || all.Contains("wh-1000") || all.Contains("arctis") || all.Contains("audioendpoint")
            || all.Contains("0000111e") || all.Contains("0000110b") || all.Contains("耳机"))
            return "headset";
        if (all.Contains("mouse") || all.Contains("trackball") || all.Contains("mx master")
            || all.Contains("mx anywhere") || all.Contains("deathadder") || all.Contains("viper")
            || all.Contains("g pro") || all.Contains("鼠标"))
            return "mouse";
        if (all.Contains("keyboard") || all.Contains("keybrd") || all.Contains("keychron")
            || all.Contains("nuphy") || all.Contains("mx keys") || all.Contains("键盘"))
            return "keyboard";
        if (all.Contains("gamepad") || all.Contains("xbox") || all.Contains("dualsense")
            || all.Contains("dualshock") || all.Contains("controller") || all.Contains("手柄") || all.Contains("控制器"))
            return "gamepad";
        return "battery";
    }

    private static object? GetProp(System.Management.ManagementBaseObject o, string name)
    {
        try { return o[name]; } catch { return null; }
    }

    private static int? ReadPercent(System.Management.ManagementBaseObject o, string name)
    {
        object? raw = GetProp(o, name);
        if (raw is null) return null;
        int v = ToInt(raw);
        return v is >= 0 and <= 100 ? v : null;
    }

    private static int ToInt(object? raw)
    {
        try { return raw is null ? 0 : Convert.ToInt32(raw); } catch { return 0; }
    }

    private static string PrettyName(string instance, string kind)
    {
        string label = kind switch
        {
            "mouse" => "无线鼠标",
            "keyboard" => "无线键盘",
            "earbuds" => "无线耳塞",
            "headset" => "无线耳机",
            "gamepad" => "无线控制器",
            _ => "蓝牙设备",
        };
        string id = ExtractId(instance);
        return id.Length == 0 ? label : $"{label} ({id})";
    }

    private static string ExtractId(string instance)
    {
        int start = instance.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
        if (start < 0) start = instance.IndexOf("Dev_", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "";
        start += 4;
        int end = instance.IndexOfAny(new[] { '_', '&', '\\', '#' }, start);
        string id = end > start ? instance.Substring(start, end - start) : instance.Substring(start);
        var sb = new StringBuilder();
        foreach (char c in id)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
        string cleaned = sb.ToString();
        return cleaned.Length > 12 ? cleaned.Substring(cleaned.Length - 4) : cleaned;
    }

    private const long DiagLogLimit = 1_048_576;

    private static void Log(string text)
    {
        try
        {
            lock (DiagLogGate)
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyIsland");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "thermal-diag.log");
                var info = new System.IO.FileInfo(path);
                if (info.Exists && info.Length > DiagLogLimit) System.IO.File.Delete(path);
                System.IO.File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {text}\n");
            }
        }
        catch { }
    }
}
