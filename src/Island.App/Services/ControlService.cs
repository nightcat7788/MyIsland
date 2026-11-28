using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Island.App.Services;

/// <summary>
/// 系统控制：亮度（WMI MonitorBrightness，外接显示器不支持则忽略）/
/// WLAN 开关（WlanFree…走 WMI MSNdis 或 netsh——不读 SSID 不触发位置）/蓝牙开关（WinRT Radio）。
///wisland 同款约束：不读 Wi-Fi 名称（netsh show interfaces 在 Win11 24H2 触发位置访问）。
/// </summary>
public static class ControlService
{
    // ---------- 亮度与多显示器 DDC/CI 控制（对齐 LunarCN 串行总线与防抖管线） ----------
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr monitor, ref uint min, ref uint cur, ref uint max);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint value);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorContrast(IntPtr monitor, ref uint min, ref uint cur, ref uint max);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorContrast(IntPtr monitor, uint value);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte bVCPCode, IntPtr pvct, out uint pdwCurrentValue, out uint pdwMaximumValue);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetVCPFeature(IntPtr monitor, byte bVCPCode, uint dwNewValue);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

    private const byte VcpColorTempPreset = 0x14;
    private const byte VcpRedGain = 0x16;
    private const byte VcpGreenGain = 0x18;
    private const byte VcpBlueGain = 0x1A;
    private const byte VcpAudioSpeakerVolume = 0x62;
    private const byte VcpAudioMute = 0x8D;

    public enum DisplayControlKind { Brightness, Contrast, Volume, Mute, Red, Green, Blue, ColorTempPreset }

    public sealed record DisplayScreenInfo
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public bool Builtin { get; init; }
        public int? Brightness { get; init; }
        public int? Contrast { get; init; }
        public int? Volume { get; init; }
        public int? Mute { get; init; }
        public int? Red { get; init; }
        public int? Green { get; init; }
        public int? Blue { get; init; }
        public int? ColorTempPreset { get; init; }
    }

    public sealed record DisplayPref
    {
        public int Min { get; init; } = 0;
        public int Max { get; init; } = 100;
        public bool FollowBuiltin { get; init; }
        public int? PresetBrightness { get; init; }
        public int? PresetContrast { get; init; }
        public int? PresetVolume { get; init; }
    }

    private static readonly Dictionary<string, (int Min, int Max)> _brightnessRanges = new(StringComparer.Ordinal);
    private static int _lastAutoFollowBuiltin = -1;

    public static Dictionary<string, DisplayPref> ParseDisplayPrefs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.Ordinal);
        try
        {
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DisplayPref>>(json);
            return dict is not null
                ? new Dictionary<string, DisplayPref>(dict, StringComparer.Ordinal)
                : new Dictionary<string, DisplayPref>(StringComparer.Ordinal);
        }
        catch { return new(StringComparer.Ordinal); }
    }

    public static string SerializeDisplayPrefs(IReadOnlyDictionary<string, DisplayPref> prefs)
    {
        try { return System.Text.Json.JsonSerializer.Serialize(prefs); }
        catch { return ""; }
    }

    public static void SetBrightnessRange(string screenKey, int min, int max)
    {
        min = Math.Clamp(min, 0, 90);
        max = Math.Clamp(max, min + 10, 100);
        lock (_writeGate) _brightnessRanges[screenKey] = (min, max);
    }

    public static (int Min, int Max) GetBrightnessRange(string screenKey)
    {
        lock (_writeGate)
            return _brightnessRanges.TryGetValue(screenKey, out var r) ? r : (0, 100);
    }

    public static void SyncDisplayRanges(IReadOnlyDictionary<string, DisplayPref> prefs)
    {
        lock (_writeGate)
        {
            _brightnessRanges.Clear();
            foreach (var (k, v) in prefs)
                _brightnessRanges[k] = (Math.Clamp(v.Min, 0, 90), Math.Clamp(v.Max, Math.Clamp(v.Min, 0, 90) + 10, 100));
        }
    }

    /// <summary>外接屏自动跟随内建屏亮度（按每屏设定的亮度范围线性映射并去重写入）。</summary>
    public static void SyncExternalFollowBrightness(IReadOnlyDictionary<string, DisplayPref> prefs, bool force = false)
    {
        var targets = prefs.Where(kv => kv.Value.FollowBuiltin && kv.Key.StartsWith("ddc:", StringComparison.Ordinal)).ToList();
        if (targets.Count == 0) return;
        if (!TryGetWmiBrightness(out uint builtinPct)) return;
        int cur = (int)builtinPct;
        if (!force && cur == _lastAutoFollowBuiltin) return;
        _lastAutoFollowBuiltin = cur;
        foreach (var (key, pref) in targets)
        {
            int min = Math.Clamp(pref.Min, 0, 90);
            int max = Math.Clamp(pref.Max, min + 10, 100);
            int mapped = (int)Math.Round(min + (max - min) * (cur / 100.0));
            QueueDisplayWrite(key, DisplayControlKind.Brightness, mapped);
        }
    }

    /// <summary>重置外接屏 RGB 三通道增益为 100/100/100（对齐 macOS 屏幕页重置颜色）。</summary>
    public static void ResetDisplayColor(string screenKey)
    {
        QueueDisplayWrite(screenKey, DisplayControlKind.Red, 100);
        QueueDisplayWrite(screenKey, DisplayControlKind.Green, 100);
        QueueDisplayWrite(screenKey, DisplayControlKind.Blue, 100);
    }

    /// <summary>安全切换内置笔记本屏幕开关（仅在检测到外接屏时允许关闭，调用系统 DisplaySwitch.exe）。</summary>
    public static bool SwitchBuiltinDisplay(bool turnOff)
    {
        try
        {
            string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string tool = System.IO.Path.Combine(sys32, "DisplaySwitch.exe");
            if (!System.IO.File.Exists(tool)) tool = "DisplaySwitch.exe";
            var psi = new System.Diagnostics.ProcessStartInfo(tool, turnOff ? "/external" : "/extend")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            return p is not null;
        }
        catch { return false; }
    }

    // I2C / DDC 总线串行锁 + 最新目标合并队列（拖动滑杆零阻塞，后台串行写入硬件）
    private static readonly System.Threading.SemaphoreSlim _ddcGate = new(1, 1);
    private static readonly object _writeGate = new();
    private static readonly Dictionary<(string Key, DisplayControlKind Kind), int> _pendingWrites = new();
    private static bool _writeDraining;

    /// <summary>枚举全部可控显示器（笔记本内建屏 WMI + 外接屏 DDC/CI 亮度/对比度/音量）。</summary>
    public static List<DisplayScreenInfo> ListDisplays()
    {
        var result = new List<DisplayScreenInfo>();
        _ddcGate.Wait();
        try
        {
            bool hasBuiltin = TryGetWmiBrightness(out uint wmiPct);
            if (hasBuiltin)
            {
                result.Add(new DisplayScreenInfo
                {
                    Key = "wmi:builtin",
                    Name = "内置显示器（系统调光）",
                    Builtin = true,
                    Brightness = (int)wmiPct,
                });
            }

            var hMonitors = new List<IntPtr>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hMon, IntPtr hdcMon, ref RECT rectMon, IntPtr dataMon) =>
                {
                    hMonitors.Add(hMon);
                    return true;
                }, IntPtr.Zero);

            int extIdx = 0;
            for (int m = 0; m < hMonitors.Count; m++)
            {
                uint count = 0;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitors[m], ref count) || count == 0)
                    continue;
                var mons = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMonitors[m], count, mons))
                    continue;
                try
                {
                    for (uint i = 0; i < count; i++)
                    {
                        IntPtr hp = mons[i].hPhysicalMonitor;
                        string desc = string.IsNullOrWhiteSpace(mons[i].desc) ? $"显示器 {extIdx + 1}" : mons[i].desc.Trim();
                        // 若已有 WMI 内建屏且该物理句柄不支持任何 DDC 项，跳过重复内建屏条目
                        int? bri = ReadDdcBrightness(hp);
                        int? con = ReadDdcContrast(hp);
                        int? vol = ReadDdcVolume(hp);
                        if (hasBuiltin && bri is null && con is null && vol is null)
                            continue;

                        int? mute = ReadDdcRaw(hp, VcpAudioMute);
                        int? red = ReadDdcPercent(hp, VcpRedGain);
                        int? green = ReadDdcPercent(hp, VcpGreenGain);
                        int? blue = ReadDdcPercent(hp, VcpBlueGain);
                        int? colorPreset = ReadDdcRaw(hp, VcpColorTempPreset);

                        extIdx++;
                        result.Add(new DisplayScreenInfo
                        {
                            Key = $"ddc:{m}:{i}",
                            Name = desc,
                            Builtin = false,
                            Brightness = bri,
                            Contrast = con,
                            Volume = vol,
                            Mute = mute,
                            Red = red,
                            Green = green,
                            Blue = blue,
                            ColorTempPreset = colorPreset,
                        });
                    }
                }
                finally
                {
                    DestroyPhysicalMonitors(count, mons);
                }
            }
        }
        catch { }
        finally
        {
            _ddcGate.Release();
        }
        return result;
    }

    /// <summary>无级滑杆实时合并写入：UI 线程立即返回，后台按最新目标值串行写硬件（亮度自动受范围钳制）。</summary>
    public static void QueueDisplayWrite(string screenKey, DisplayControlKind kind, int value)
    {
        int clamped = kind switch
        {
            DisplayControlKind.Brightness => ClampBrightnessToRange(screenKey, value),
            DisplayControlKind.Mute or DisplayControlKind.ColorTempPreset => Math.Clamp(value, 0, 255),
            _ => Math.Clamp(value, 0, 100),
        };
        lock (_writeGate)
        {
            _pendingWrites[(screenKey, kind)] = clamped;
            if (_writeDraining) return;
            _writeDraining = true;
        }
        _ = Task.Run(DrainDisplayWritesAsync);
    }

    private static int ClampBrightnessToRange(string screenKey, int value)
    {
        var (min, max) = GetBrightnessRange(screenKey);
        return Math.Clamp(value, min, max);
    }

    private static async Task DrainDisplayWritesAsync()
    {
        while (true)
        {
            KeyValuePair<(string Key, DisplayControlKind Kind), int> next;
            lock (_writeGate)
            {
                if (_pendingWrites.Count == 0)
                {
                    _writeDraining = false;
                    return;
                }
                next = _pendingWrites.First();
                _pendingWrites.Remove(next.Key);
            }

            await _ddcGate.WaitAsync().ConfigureAwait(false);
            try
            {
                ApplyDisplayWriteCore(next.Key.Key, next.Key.Kind, (uint)next.Value);
                // DDC/CI 规范要求两次写指令之间至少留 40ms 总线恢复窗口
                await Task.Delay(45).ConfigureAwait(false);
            }
            catch { }
            finally
            {
                _ddcGate.Release();
            }
        }
    }

    private static void ApplyDisplayWriteCore(string screenKey, DisplayControlKind kind, uint pct)
    {
        if (screenKey == "wmi:builtin")
        {
            if (kind == DisplayControlKind.Brightness) TrySetWmiBrightness(pct);
            return;
        }
        if (!screenKey.StartsWith("ddc:", StringComparison.Ordinal)) return;
        string[] parts = screenKey.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[1], out int monIdx) || !uint.TryParse(parts[2], out uint subIdx))
            return;

        var hMonitors = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdcMon, ref RECT rectMon, IntPtr dataMon) =>
            {
                hMonitors.Add(hMon);
                return true;
            }, IntPtr.Zero);
        if (monIdx < 0 || monIdx >= hMonitors.Count) return;

        uint count = 0;
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitors[monIdx], ref count) || subIdx >= count)
            return;
        var mons = new PHYSICAL_MONITOR[count];
        if (!GetPhysicalMonitorsFromHMONITOR(hMonitors[monIdx], count, mons))
            return;
        try
        {
            IntPtr hp = mons[subIdx].hPhysicalMonitor;
            switch (kind)
            {
                case DisplayControlKind.Brightness:
                {
                    uint min = 0, cur = 0, max = 100;
                    if (GetMonitorBrightness(hp, ref min, ref cur, ref max) && max > min)
                        SetMonitorBrightness(hp, min + (max - min) * pct / 100);
                    break;
                }
                case DisplayControlKind.Contrast:
                {
                    uint min = 0, cur = 0, max = 100;
                    if (GetMonitorContrast(hp, ref min, ref cur, ref max) && max > min)
                        SetMonitorContrast(hp, min + (max - min) * pct / 100);
                    break;
                }
                case DisplayControlKind.Volume:
                {
                    if (GetVCPFeatureAndVCPFeatureReply(hp, VcpAudioSpeakerVolume, IntPtr.Zero, out _, out uint max) && max > 0)
                        SetVCPFeature(hp, VcpAudioSpeakerVolume, max * pct / 100);
                    break;
                }
                case DisplayControlKind.Mute:
                    SetVCPFeature(hp, VcpAudioMute, pct);
                    break;
                case DisplayControlKind.Red:
                    WriteDdcPercent(hp, VcpRedGain, pct);
                    break;
                case DisplayControlKind.Green:
                    WriteDdcPercent(hp, VcpGreenGain, pct);
                    break;
                case DisplayControlKind.Blue:
                    WriteDdcPercent(hp, VcpBlueGain, pct);
                    break;
                case DisplayControlKind.ColorTempPreset:
                    SetVCPFeature(hp, VcpColorTempPreset, pct);
                    break;
            }
        }
        finally
        {
            DestroyPhysicalMonitors(count, mons);
        }
    }

    private static void WriteDdcPercent(IntPtr hp, byte vcp, uint pct)
    {
        if (GetVCPFeatureAndVCPFeatureReply(hp, vcp, IntPtr.Zero, out _, out uint max) && max > 0)
            SetVCPFeature(hp, vcp, max * pct / 100);
        else
            SetVCPFeature(hp, vcp, pct);
    }

    private static int? ReadDdcPercent(IntPtr hp, byte vcp)
    {
        try
        {
            if (!GetVCPFeatureAndVCPFeatureReply(hp, vcp, IntPtr.Zero, out uint cur, out uint max) || max == 0)
                return null;
            return (int)Math.Min(100u, cur * 100 / max);
        }
        catch { return null; }
    }

    private static int? ReadDdcRaw(IntPtr hp, byte vcp)
    {
        try
        {
            if (!GetVCPFeatureAndVCPFeatureReply(hp, vcp, IntPtr.Zero, out uint cur, out _))
                return null;
            return (int)cur;
        }
        catch { return null; }
    }

    private static int? ReadDdcBrightness(IntPtr hp)
    {
        try
        {
            uint min = 0, cur = 0, max = 0;
            if (!GetMonitorBrightness(hp, ref min, ref cur, ref max) || max <= min || cur < min) return null;
            return (int)Math.Min(100u, (cur - min) * 100 / (max - min));
        }
        catch { return null; }
    }

    private static int? ReadDdcContrast(IntPtr hp)
    {
        try
        {
            uint min = 0, cur = 0, max = 0;
            if (!GetMonitorContrast(hp, ref min, ref cur, ref max) || max <= min || cur < min) return null;
            return (int)Math.Min(100u, (cur - min) * 100 / (max - min));
        }
        catch { return null; }
    }

    private static int? ReadDdcVolume(IntPtr hp)
    {
        try
        {
            if (!GetVCPFeatureAndVCPFeatureReply(hp, VcpAudioSpeakerVolume, IntPtr.Zero, out uint cur, out uint max) || max == 0)
                return null;
            return (int)Math.Min(100u, cur * 100 / max);
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR { public IntPtr hPhysicalMonitor; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string desc; }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    private static IntPtr GetPrimaryMonitorHandle()
    {
        IntPtr found = IntPtr.Zero;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdcMon, ref RECT rectMon, IntPtr dataMon) =>
            {
                found = hMon;
                return false;
            }, IntPtr.Zero);
        return found;
    }

    /// <summary>当前亮度 0..100（优先笔记本内建屏 WMI，再退外接屏 DDC/CI；均不支持返回 null）。</summary>
    public static uint? GetBrightness()
    {
        if (TryGetWmiBrightness(out uint wmiPct)) return wmiPct;
        try
        {
            IntPtr mon = GetPrimaryMonitorHandle();
            if (mon == IntPtr.Zero) return null;
            uint count = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(mon, ref count) || count == 0) return null;
            var mons = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(mon, count, mons)) return null;
            try
            {
                uint min = 0, cur = 0, max = 0;
                bool ok = GetMonitorBrightness(mons[0].hPhysicalMonitor, ref min, ref cur, ref max);
                if (!ok || max <= min || cur < min) return null;
                return Math.Min(100u, (cur - min) * 100 / (max - min));
            }
            finally
            {
                DestroyPhysicalMonitors(count, mons);
            }
        }
        catch { return null; }
    }

    public static bool SetBrightness(uint percent)
    {
        percent = Math.Min(100u, percent);
        if (TrySetWmiBrightness(percent)) return true;
        try
        {
            IntPtr mon = GetPrimaryMonitorHandle();
            if (mon == IntPtr.Zero) return false;
            uint count = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(mon, ref count) || count == 0) return false;
            var mons = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(mon, count, mons)) return false;
            try
            {
                uint min = 0, cur = 0, max = 0;
                bool ok = GetMonitorBrightness(mons[0].hPhysicalMonitor, ref min, ref cur, ref max);
                if (ok && max > min)
                    return SetMonitorBrightness(mons[0].hPhysicalMonitor, min + (max - min) * percent / 100);
                return false;
            }
            finally
            {
                DestroyPhysicalMonitors(count, mons);
            }
        }
        catch { return false; }
    }

    private static bool TryGetWmiBrightness(out uint percent)
    {
        percent = 0;
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            using var results = searcher.Get();
            foreach (System.Management.ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    if (obj["CurrentBrightness"] is not null)
                    {
                        uint v = Convert.ToUInt32(obj["CurrentBrightness"]);
                        if (v <= 100) { percent = v; return true; }
                    }
                }
            }
        }
        catch { }
        return false;
    }

    private static bool TrySetWmiBrightness(uint percent)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            using var results = searcher.Get();
            foreach (System.Management.ManagementObject obj in results)
            {
                using (obj)
                {
                    obj.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)Math.Min(100u, percent) });
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    // ---------- WLAN 开关（netsh，仅开关不读名称） ----------
    // 接口名不能硬编码：本机无线接口实测叫 "WLAN"，写死 "Wi-Fi" 会让 netsh 报"找不到接口"，
    // 磁贴点了只弹 UAC 不生效，用户反复点反复弹。所以先枚举网卡找出无线适配器的真实名字再操作。

    /// <summary>当前在用的无线适配器名（NetworkInterfaceType == Wireless80211）。
    /// 本机可能同时挂着多个无线适配器（"本地连接* 2" 这类是历史残留、状态 Down），
    /// 必须挑状态为 Up 的那个，否则 netsh 会切到没人用的残留网卡上，界面状态也不会变。
    /// 一个都没有时返回 null。</summary>
    public static string? GetWirelessAdapterName()
    {
        try
        {
            string? fallback = null;
            foreach (var nic in GetNetworkAdapters())
            {
                if (nic.Type != System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211) continue;
                if (nic.Status == System.Net.NetworkInformation.OperationalStatus.Up) return nic.Name;
                fallback ??= nic.Name;
            }
            return fallback;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 一次 netsh 查询的结果：适配器管理状态（开/关）与连接状态（连/未连）。
    /// 两者都可能为 null（这一列读不到）；外层 null 表示 netsh 整体失败/超时。
    /// </summary>
    private readonly record struct WlanSnapshot(bool? AdapterEnabled, bool? Connected);

    /// <summary>无线状态缓存：UI 每次刷新要读两个值，不缓存就是两次 netsh 进程。</summary>
    private static WlanSnapshot? _wlanCache;
    private static DateTime _wlanCacheAt = DateTime.MinValue;
    private static readonly object _wlanGate = new();
    private const int WlanCacheMs = 1000;

    /// <summary>
    /// 无线适配器开关状态（true=已启用 / false=已禁用 / null=读不到）。
    /// 失败时不再编造：netsh 超时/失败、找不到无线接口一律 null
    /// （旧代码两条失败路径一边返回 false 一边返回 true，界面随机显示开/关）。
    /// </summary>
    public static bool? WlanAdapterEnabled => GetWlanState().AdapterEnabled;

    /// <summary>
    /// 无线连接状态（true=已连接 / false=未连接 / null=读不到）。
    /// 这是 netsh 的"连接状态"列，不是"管理状态"列：
    /// Wi-Fi 开着但没连热点时这里是 false（未连接）。
    /// </summary>
    public static bool? WlanConnected => GetWlanState().Connected;

    /// <summary>一次拿到两个状态（UI 刷新用，省一次 netsh 进程）。</summary>
    public static (bool? AdapterEnabled, bool? Connected) GetWlanState()
    {
        var snap = ReadWlanState();
        return (snap?.AdapterEnabled, snap?.Connected);
    }

    /// <summary>
    /// 兼容旧调用点：无线适配器开关状态（读不到时按"关闭"处理）。
    /// 新代码请用 <see cref="WlanAdapterEnabled"/> / <see cref="WlanConnected"/>：
    /// 那两个是可空值，能区分"关闭"与"读不到"。
    /// </summary>
    public static bool WlanEnabled => WlanAdapterEnabled == true;

    /// <summary>
    /// 读无线状态（1 秒内复用上一次结果）。
    /// netsh 超时/失败/找不到无线接口 → null（未知），不再编造 true/false。
    /// </summary>
    private static WlanSnapshot? ReadWlanState()
    {
        lock (_wlanGate)
        {
            if (_wlanCache.HasValue && (DateTime.Now - _wlanCacheAt).TotalMilliseconds < WlanCacheMs)
                return _wlanCache;
        }
        var snap = QueryWlanState();
        lock (_wlanGate)
        {
            _wlanCache = snap;
            _wlanCacheAt = DateTime.Now;
        }
        return snap;
    }

    /// <summary>真正跑一次 netsh 并解析（失败返回 null = 未知）。</summary>
    private static WlanSnapshot? QueryWlanState()
    {
        try
        {
            string? outp = RunNetsh("interface show interface");
            if (outp is null) return null;
            // netsh 的列顺序随语言/版本不同（中文：接口名在最前；英文：接口名在最后），
            // 所以按"词"定位接口名，再从其余词里判状态
            string? nic = GetWirelessAdapterName();
            foreach (var raw in outp.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;
                if (nic is not null)
                {
                    int idx = Array.FindIndex(tokens,
                        t => string.Equals(t, nic, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                        return ParseState(tokens.Where((_, i) => i != idx));
                    // 接口名里带空格时按整行包含匹配（名字本身不会是状态词，混进去不影响判断）
                    if (line.Contains(nic, StringComparison.OrdinalIgnoreCase))
                        return ParseState(tokens);
                    continue;
                }
                // 兜底：按关键字找无线网卡（老办法）
                if (!line.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("WLAN", StringComparison.OrdinalIgnoreCase)) continue;
                return ParseState(tokens);
            }
            return null;   // 找不到无线接口行：未知，不编造
        }
        catch { return null; }
    }

    /// <summary>从一行 tokens 里分别取"管理状态"和"连接状态"。</summary>
    private static WlanSnapshot ParseState(IEnumerable<string> tokens)
    {
        var list = tokens.ToList();
        return new WlanSnapshot(StateEnabled(list), StateConnected(list));
    }

    /// <summary>开关 WLAN（netsh，需管理员，保留原有提权流程）。
    /// 返回 (是否成功, 提示文案)：找不到无线适配器时不做任何操作，只给提示。</summary>
    public static (bool Ok, string Message) SetWlan(bool enable)
    {
        try
        {
            string? nic = GetWirelessAdapterName();
            if (string.IsNullOrWhiteSpace(nic))
                return (false, "未找到无线适配器，无法开关无线网络");

            var psi = new System.Diagnostics.ProcessStartInfo("netsh",
                $"interface set interface name=\"{nic}\" {(enable ? "enable" : "disable")}")
            { UseShellExecute = true, Verb = "runas", CreateNoWindow = true };   // 需管理员
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(4000);
            lock (_wlanGate) { _wlanCache = null; _wlanCacheAt = DateTime.MinValue; }
            return (true, enable ? $"已请求开启无线网络（{nic}）" : $"已请求关闭无线网络（{nic}）");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "需要管理员权限才能开关无线网络");   // 用户取消了 UAC
        }
        catch { return (false, "开关无线网络失败"); }
    }

    /// <summary>判断 netsh 接口的"管理状态"（已启用/已禁用、Enabled/Disabled）：
    /// 这才是 netsh set interface enable/disable 改的那个字段——
    /// 无线开着但没连上热点时，状态列是"已断开"而管理状态仍是"已启用"。
    /// 没有这一列时退回连接状态；两列都读不到 → null（未知），不再返回 false 编造"已关闭"。
    /// </summary>
    private static bool? StateEnabled(IEnumerable<string> tokens)
    {
        bool? admin = null, state = null;
        foreach (string t in tokens)
        {
            switch (t.ToLowerInvariant())
            {
                case "已启用": case "enabled": admin = true; break;
                case "已禁用": case "disabled": admin = false; break;
                case "已连接": case "connected": case "up": state = true; break;
                case "已断开": case "已断开连接": case "disconnected": case "down": state = false; break;
            }
        }
        if (admin.HasValue) return admin;
        return state;
    }

    /// <summary>
    /// 判断 netsh 接口的"连接状态"列（已连接 / 已断开连接；Connected / Disconnected）。
    /// ⚠️ 必须整词匹配："disconnected" 里也含 "connected"，
    /// 用 Contains 会把"未连接"判成"已连接"——这正是旧代码把管理状态当连接状态显示的根因。
    /// 读不到这一列 → null（未知）。
    /// </summary>
    private static bool? StateConnected(IEnumerable<string> tokens)
    {
        bool? conn = null;
        foreach (string t in tokens)
        {
            switch (t.ToLowerInvariant())
            {
                case "已断开连接": case "已断开": case "disconnected": case "down": conn = false; break;
                case "已连接": case "connected": case "up": conn = true; break;
            }
        }
        return conn;
    }

    /// <summary>跑一条 netsh 命令并取回输出（带超时）；超时/失败返回 null。
    /// 先启动异步读取再 WaitForExit：同步 ReadToEnd 会把超时架空，管道写满时还会互相死等。</summary>
    private static string? RunNetsh(string args, int timeoutMs = 3000)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netsh", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = ConsoleEncoding,
                StandardErrorEncoding = ConsoleEncoding,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.WaitForExit(1000);
                return null;    // 超时：当作没拿到输出
            }
            string outp = outTask.Wait(1000) ? outTask.Result : "";
            _ = errTask.Wait(500) ? errTask.Result : "";   // stderr 只为及时收干管道
            return outp.Length == 0 ? null : outp;
        }
        catch { return null; }
    }

    // ---------- 蓝牙服务（服务级：bthserv） ----------
    /// <summary>
    /// 蓝牙服务（bthserv）是否在运行。
    /// ⚠️ 这不是 Windows 11 的"蓝牙无线电开关"：无线电关掉时 bthserv 照样在跑。
    /// 工程没引用 WinRT 投影，读不到真正的无线电状态，所以这里只报服务状态，
    /// 界面文案也按"蓝牙服务"说，别宣称是无线电开关。
    /// </summary>
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

    /// <summary>
    /// 开关蓝牙服务（bthserv）。返回 (是否成功, 提示文案)。
    /// 以前失败被静默吞掉，用户以为按钮坏了；现在明确回报成功/失败原因。
    /// ⚠️ 停服务要管理员权限，普通权限下会 Access Denied——
    /// 这时引导用户用管理员模式运行，或点"打开蓝牙设置"交给系统设置处理。
    /// </summary>
    public static (bool Ok, string Message) SetBluetooth(bool enable)
    {
        try
        {
            using var svc = new System.ServiceProcess.ServiceController("bthserv");
            bool running = svc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
            if (enable && !running)
            {
                svc.Start();
                try { svc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(3)); } catch { }
                return (true, "蓝牙服务已启动");
            }
            if (!enable && running)
            {
                svc.Stop();
                try { svc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(3)); } catch { }
                return (true, "蓝牙服务已停止");
            }
            return (true, enable ? "蓝牙服务已在运行" : "蓝牙服务已停止");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "需要管理员权限才能开关蓝牙服务（可改用系统设置）");
        }
        catch (Exception ex)
        {
            return (false, "开关蓝牙服务失败：" + ex.Message);
        }
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

    /// <summary>立即睡眠（非强制临界挂起，允许应用正常处理挂起通知）。</summary>
    public static void Sleep()
    {
        try { SetSuspendState(false, false, false); } catch { }
    }

    // ---------- 系统深色模式（HKCU 个人化，免管理员） ----------
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint Msg, IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

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

    /// <summary>设置系统深/浅色模式并广播主题切换消息（即时生效，无需重启）。</summary>
    public static void SetSystemLightTheme(bool light)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PersonalizeKey, true);
            if (key is null) return;
            int val = light ? 1 : 0;
            key.SetValue("AppsUseLightTheme", val, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", val, Microsoft.Win32.RegistryValueKind.DWord);
            SendMessageTimeoutW(new IntPtr(0xFFFF), 0x001A /*WM_SETTINGCHANGE*/,
                IntPtr.Zero, "ImmersiveColorSet", 0x0002 /*SMTO_ABORTIFHUNG*/, 200, out _);
        }
        catch { }
    }

    // ---------- 夜灯（夜间模式）：CloudStore Bond CompactBinary 记录，免管理员 ----------
    private const string NightLightKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

    /// <summary>夜灯当前状态（null = 读不到/不支持）。</summary>
    public static bool? GetNightLightOn()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(NightLightKeyPath, false);
            if (key?.GetValue("Data") is byte[] data && data.Length >= 23)
            {
                if (data[18] == 0x15 && data.Length >= 25 && data[23] == 0x10 && data[24] == 0x00)
                    return true;
                if (data[18] == 0x13)
                    return false;
            }
        }
        catch { }
        return null;
    }

    /// <summary>设置夜灯开关（遵循 CloudStore Bond 变长结构：开=0x15 含 0x10 0x00，关=0x13）。</summary>
    public static bool SetNightLight(bool on)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(NightLightKeyPath, true);
            if (key?.GetValue("Data") is not byte[] data || data.Length < 23) return false;
            bool? cur = data[18] == 0x15 ? true : data[18] == 0x13 ? false : null;
            if (cur is null) return false;
            if (cur.Value == on) return true;

            byte[] updated;
            if (on)
            {
                // OFF(41B, 0x13) -> ON(43B, 0x15): 在偏移 23 插入 { 0x10, 0x00 }
                updated = new byte[data.Length + 2];
                Array.Copy(data, 0, updated, 0, 23);
                updated[18] = 0x15;
                updated[23] = 0x10;
                updated[24] = 0x00;
                Array.Copy(data, 23, updated, 25, data.Length - 23);
            }
            else
            {
                if (data.Length < 25 || data[23] != 0x10 || data[24] != 0x00) return false;
                // ON(43B, 0x15) -> OFF(41B, 0x13): 移除偏移 23..24 的 { 0x10, 0x00 }
                updated = new byte[data.Length - 2];
                Array.Copy(data, 0, updated, 0, 23);
                updated[18] = 0x13;
                Array.Copy(data, 25, updated, 23, data.Length - 25);
            }

            // 递增偏移 10..14 的 CloudStore 版本戳，通知 Shell Experience Host 立即应用
            for (int i = 10; i < 15; i++)
            {
                if (updated[i] != 0xFF) { updated[i]++; break; }
            }
            key.SetValue("Data", updated, Microsoft.Win32.RegistryValueKind.Binary);
            return true;
        }
        catch { return false; }
    }

    // ---------- 系统设置快捷链接 ----------
    /// <summary>打开指定系统设置页（ms-settings: 协议）。</summary>
    // ---------- 电源计划（YASB PowerPlan 同款：powercfg 枚举/切换，免管理员） ----------

    // ---------- 控制台输出编码（powercfg 的中文输出按本机代码页，不是 UTF-8） ----------

    /// <summary>
    /// 本机控制台输出编码（ANSI/OEM 代码页，如 936=GBK）。
    /// powercfg /LIST 走管道时输出的是本机代码页字节，.NET 默认按 UTF-8 解码
    /// → 中文电源计划名（"平衡"）全变乱码。
    /// .NET 8 不内置 GBK：Encoding.GetEncoding(936) 会抛 ArgumentException，
    /// 必须先注册 CodePagesEncodingProvider（见 csproj 里的 System.Text.Encoding.CodePages）。
    /// </summary>
    internal static readonly Encoding ConsoleEncoding = ResolveConsoleEncoding();

    /// <summary>取本机 ANSI 代码页：注册表 ACP → 当前 UI 语言的 ANSICodePage → Encoding.Default。</summary>
    private static Encoding ResolveConsoleEncoding()
    {
        // 只注册一次：重复注册无副作用，但放在这里保证后面 GetEncoding(936) 能用
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch { /* 已注册或本机不支持 */ }

        // ① 注册表里的 ANSI 代码页（HKLM\SYSTEM\CurrentControlSet\Control\Nls\CodePage\ACP）
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Nls\CodePage", writable: false);
            if (key?.GetValue("ACP") is string s && int.TryParse(s, out int cp) && cp > 0)
                return Encoding.GetEncoding(cp);
        }
        catch { /* 取不到就退下一档 */ }

        // ② 当前 UI 语言的 ANSI 代码页
        try
        {
            int cp = System.Globalization.CultureInfo.InstalledUICulture.TextInfo.ANSICodePage;
            if (cp > 0) return Encoding.GetEncoding(cp);
        }
        catch { }

        // ③ 兜底
        try { return Encoding.Default; } catch { return Encoding.UTF8; }
    }

    /// <summary>所有电源计划（Guid + 名称）+ 当前激活项索引。</summary>
    public static (List<(string Id, string Name)> Plans, int ActiveIndex) GetPowerPlans()
    {
        var plans = new List<(string Id, string Name)>();
        int active = -1;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powercfg.exe", "/LIST")
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true,
                // 按本机代码页解码，否则"平衡"变成乱码
                StandardOutputEncoding = ConsoleEncoding,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return (plans, active);
            // 先启动异步读取再等退出：同步 ReadToEnd 会把 WaitForExit 的超时架空，
            // 管道被写满时还会互相死等（同 RunNetsh 的做法）。
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.WaitForExit(1000);
            }
            string output = outTask.Wait(1000) ? outTask.Result : "";
            // 行格式：电源方案 GUID: xxxx  (平衡) *   —— 中英文系统都兼容
            var re = new System.Text.RegularExpressions.Regex(
                @"GUID:\s*([0-9a-fA-F-]{36})\s*\(([^)]*)\)\s*(\*)?");
            foreach (System.Text.RegularExpressions.Match m in re.Matches(output))
            {
                plans.Add((m.Groups[1].Value, m.Groups[2].Value.Trim()));
                if (m.Groups[3].Success && m.Groups[3].Value == "*")
                    active = plans.Count - 1;
            }
        }
        catch { }
        return (plans, active);
    }

    public static bool SetPowerPlan(string guid)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powercfg.exe", $"/SETACTIVE {guid}")
            { CreateNoWindow = true, UseShellExecute = false };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    // ---------- 内存清理（LiteMonitor 同款：本进程 GC + 尽量清空各进程工作集） ----------

    /// <summary>
    /// 清理结果"未知"的哨兵值：超时（后台还没跑完）或任务失败时返回它。
    /// UI 必须把它和 0 区分开：0 = "清完了但内存压力本来就不高"，
    /// CleanMemoryUnknown = "还在进行/结果未知"，不能报成"已清理（内存压力本就不高）"。
    /// </summary>
    public const long CleanMemoryUnknown = long.MinValue;

    /// <summary>
    /// 立即清理内存（同步兼容版，签名与旧调用点一致）。
    /// 重的部分（扫全系统进程 + EmptyWorkingSet）搬到后台线程执行，UI 线程最多等 waitMs 毫秒取结果；
    /// 超时不算失败，后台继续跑完，结果经 onDone 回调回传。
    /// 返回值：&gt;= 0 = 实际释放的可用内存增量（MB）；<see cref="CleanMemoryUnknown"/> = 结果未知（超时/失败）。
    /// 建议宿主改调 <see cref="CleanMemoryAsync()"/>（不阻塞 UI）。
    /// </summary>
    public static long CleanMemory(Action<long>? onDone = null, int waitMs = 1500)
    {
        Task<long> task = CleanMemoryAsync();
        if (onDone is not null)
        {
            // 给了回调就完全不等：结果回来再通知（回调在后台线程，调用方需自行切回 UI 线程）
            _ = task.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion) onDone(t.Result);
            }, TaskScheduler.Default);
            return CleanMemoryUnknown;   // 结果还没出来：未知，不是 0
        }
        try
        {
            // 超时不再返回 0 冒充"没清到"：后台其实清完了，返回 0 会让界面误报"内存压力本就不高"
            if (!task.Wait(waitMs)) return CleanMemoryUnknown;
            return task.Result;
        }
        catch { return CleanMemoryUnknown; }
    }

    /// <summary>立即清理内存（async 版，不阻塞 UI）：返回释放的可用内存增量（MB，可能为 0）。
    /// 宿主可改调 async 版本。</summary>
    public static Task<long> CleanMemoryAsync() => Task.Run(RunMemoryCleanup);

    /// <summary>
    /// 立即清理内存（async + 回调版）：跑完后在完成线程回调实际释放的 MB（不会返回哨兵值）。
    /// 回调在后台线程，调用方需自行切回 UI 线程再更新界面。
    /// </summary>
    public static async Task CleanMemoryAsync(Action<long> onDone)
    {
        long freed = await CleanMemoryAsync().ConfigureAwait(false);
        onDone?.Invoke(freed);
    }

    private static int _cleaning;

    /// <summary>
    /// 轻量自瘦身（不扫描其它进程）：修剪瞬态渲染缓存 + 压缩 LOH/Gen2 GC + 收缩本进程工作集。
    /// 启动稳定后与长时收起待机时定期调用，可将常驻内存稳定控制在极低水位。
    /// </summary>
    public static void TrimSelfWorkingSet()
    {
        try
        {
            Island.App.Controls.RenderKit.TrimCaches();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            IntPtr self = System.Diagnostics.Process.GetCurrentProcess().Handle;
            EmptyWorkingSet(self);
            SetProcessWorkingSetSizeEx(self, new IntPtr(-1), new IntPtr(-1), 0);
        }
        catch { }
    }

    /// <summary>真正的清理动作（跑在后台线程）：并发防重入 + 自瘦身 + 尽量清空各用户进程工作集。</summary>
    private static long RunMemoryCleanup()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _cleaning, 1, 0) != 0)
            return 0;
        try
        {
            long before = AvailableMB();
            TrimSelfWorkingSet();
            int selfId = Environment.ProcessId;
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    // 跳过 System Idle(0) / System(4) 与已清理的本进程，避免无意义的权限异常开销
                    if (p.Id <= 4 || p.Id == selfId) continue;
                    EmptyWorkingSet(p.Handle);
                }
                catch { /* 系统/受保护进程无权限：跳过 */ }
                finally { p.Dispose(); }
            }
            return Math.Max(0, AvailableMB() - before);
        }
        finally
        {
            System.Threading.Volatile.Write(ref _cleaning, 0);
        }
    }

    /// <summary>零分配 Win32 GlobalMemoryStatusEx 读取当前可用物理内存（MB），替代高开销的 PerformanceCounter。</summary>
    private static long AvailableMB()
    {
        try
        {
            var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref ms))
                return (long)(ms.ullAvailPhys / (1024UL * 1024UL));
        }
        catch { }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(
        IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize, uint flags);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    // ---------- 网卡枚举（设置中心手动选网卡用） ----------

    /// <summary>本机物理网卡列表（含接口类型与状态：WLAN 开关靠它认出当前在用的无线适配器）。</summary>
    public static List<(string Id, string Name, System.Net.NetworkInformation.NetworkInterfaceType Type, System.Net.NetworkInformation.OperationalStatus Status)> GetNetworkAdapters()
    {
        var list = new List<(string, string, System.Net.NetworkInformation.NetworkInterfaceType, System.Net.NetworkInformation.OperationalStatus)>
        {
            ("", "自动（聚合所有物理网卡）",
                System.Net.NetworkInformation.NetworkInterfaceType.Unknown,
                System.Net.NetworkInformation.OperationalStatus.Unknown)
        };
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                list.Add((nic.Id, nic.Name, nic.NetworkInterfaceType, nic.OperationalStatus));
            }
        }
        catch { }
        return list;
    }

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
