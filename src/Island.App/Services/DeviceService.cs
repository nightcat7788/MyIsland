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
/// 数据源（零驱动，实际只有 WMI 这两条）：
///  ① WMI root\CIMV2 Win32_Battery —— 笔记本内置电池（多电池聚合成一条）
///  ② WMI root\WMI MSBatteryClass —— 蓝牙/BLE 设备（不是每个实例都有电量属性）
/// ⚠️ 本工程没有 Windows.Devices.Power / BLE GATT / HID 电量特性这几条数据源，别再按它们写代码。
/// 采样线程 30s 一轮，失败静默（列表为空 UI 显示"未检测到"），失败原因写 thermal-diag.log。
///
/// ⚠️ 宿主接线（留给主代理，本文件不碰 IslandWindow.xaml.cs）：
///  · 窗口关闭时调用 <see cref="Stop"/>：否则静态轮询会一直跑到进程退出，
///    每 30 秒两次 WMI 搜索（实测 13ms + 19ms）持续唤醒 WMI provider host。
///  · 需要设备视图时调用 <see cref="SetWanted"/>(true)、不需要时调用 (false)，
///    做到"按需启停"：没消费者就别轮询。
/// </summary>
public static class DeviceService
{
    private static readonly object _gate = new();
    private static List<DeviceInfo> _devices = new();
    private static Timer? _timer;
    private static bool _wanted;

    // ---------- 快照缓存（2026-11-23 复审 R14 落地）----------
    // 渲染层每帧读 Snapshot（dash 视图画电池、设备页重绘），以前每次访问都 lock + ToList 整表：
    // 30s 才采一轮的数据，一秒内被拷贝几十次纯属浪费。改为"采样后失效"的只读副本缓存：
    // 2 秒内重复访问直接返回同一份 List（调用方只读，绝不能改——文档写明）。
    private static List<DeviceInfo>? _snapCache;
    private static DateTime _snapCacheAt = DateTime.MinValue;
    private static readonly TimeSpan SnapCacheTtl = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 当前设备电量快照（UI 线程直接读，2 秒缓存）。
    /// ⚠️ 返回的列表是共享只读副本，调用方不得增删改（需要可写副本请自行 ToList）。
    /// </summary>
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

    /// <summary>是否正在轮询（诊断用）。</summary>
    public static bool Running { get { lock (_gate) return _timer is not null; } }

    private static void EnsureTimer()
    {
        lock (_gate)
        {
            if (_timer is not null) return;
            _timer = new Timer(_ => Sample(), null, 0, 30_000);
        }
    }

    /// <summary>
    /// 启动轮询（幂等）。
    /// 顺带把 _wanted 置 true：否则宿主先 Start()、后 SetWanted(false) 时，
    /// SetWanted 会因"_wanted 没变"提前返回 → 定时器停不掉。
    /// </summary>
    public static void Start()
    {
        lock (_gate) { _wanted = true; }
        EnsureTimer();
    }

    /// <summary>
    /// 停止轮询：释放定时器。
    /// 以前这个类只有 Start()，没有停止路径 —— 窗口关了也照样每 30 秒两次 WMI 搜索，
    /// 持续唤醒 WMI provider host 白烧电。停止后 <see cref="Start"/> 仍可重新拉起。
    /// </summary>
    public static void Stop()
    {
        lock (_gate)
        {
            _wanted = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>
    /// 按需启停：有消费者（设备视图可见）时 true，不需要时 false。
    /// 宿主接线建议（IslandWindow.xaml.cs，由主代理接）：
    ///   展开面板切到"设备"页 → SetWanted(true)；
    ///   离开设备页 / 窗口关闭   → SetWanted(false)。
    /// </summary>
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

        lock (_gate)
        {
            _devices = list;
            _snapCache = null;   // 新一轮数据落地：快照缓存随之失效（下次访问重建）
        }
    }

    /// <summary>
    /// 笔记本电池（WMI Win32_Battery，无额外依赖）。
    /// 多电池聚合成一条：取最低电量（最保守），充电态取"任一在充电"，
    /// 免得界面上出现两条同名的"笔记本电池"。
    /// </summary>
    private static IEnumerable<DeviceInfo> SampleWmiBattery()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? lowest = null;
        bool anyCharging = false;
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "root\\CIMV2", "SELECT InstanceName, EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
            foreach (var o in searcher.Get())
            {
                // 按设备标识去重：同一块电池在 WMI 里可能有多条记录
                string id = GetProp(o, "InstanceName")?.ToString() ?? "";
                if (id.Length > 0 && !seen.Add(id)) continue;

                int? pct = ReadPercent(o, "EstimatedChargeRemaining");
                if (pct is null) continue;      // 255 = 固件报"未知"，不显示
                lowest = lowest is null ? pct : Math.Min(lowest.Value, pct.Value);

                if (IsChargingStatus(ToInt(GetProp(o, "BatteryStatus")))) anyCharging = true;
            }
        }
        catch (Exception ex)
        {
            Log($"Win32_Battery 查询失败：{ex.GetType().Name}: {ex.Message}");
        }
        if (lowest is null) yield break;
        yield return new DeviceInfo("笔记本电池", "battery", lowest.Value, anyCharging);
    }

    /// <summary>
    /// Win32_Battery.BatteryStatus 判定"是否正在充电"：
    /// 6=Charging、7=Charging and High、8=Charging and Low、9=Charging and Critical。
    /// 2 = "系统接着交流电所以电池不在放电"——只是接了电，不一定在充电。
    /// 以前按 status == 2 判充电，结果插着电源却没在充电反而显示"充电中"。
    /// </summary>
    private static bool IsChargingStatus(int status) => status is 6 or 7 or 8 or 9;

    /// <summary>
    /// 蓝牙/BLE 设备电量。数据源只有一条：WMI root\WMI 的 MSBatteryClass。
    /// ⚠️ 事实（别被旧注释误导）：本工程没有 Windows.Devices.Power / BLE GATT / HID 电量特性这些数据源；
    /// MSBatteryClass 的实例在 Win11 上多半是内部电池（ACPI\PNP0C0A\0_0），
    /// 而且不是每个实例都有 EstimatedChargePercent 属性 —— 固定 SELECT 那个属性会直接报 "Invalid query"。
    /// 所以这里 SELECT * 再按属性存在性过滤；查询失败写诊断日志，不再静默吞异常。
    /// </summary>
    private static IEnumerable<DeviceInfo> SampleBluetoothBattery()
    {
        var list = new List<DeviceInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "root\\WMI", "SELECT * FROM MSBatteryClass");
            foreach (var o in searcher.Get())
            {
                string instance = GetProp(o, "InstanceName")?.ToString() ?? "";
                if (instance.Length == 0) continue;
                if (!seen.Add(instance)) continue;                      // 按设备标识去重
                if (IsInternalBattery(instance)) continue;             // 内部电池：与 Win32_Battery 重复
                if (!HasProp(o, "EstimatedChargePercent")) continue;    // 该实例没有电量属性
                int? pct = ReadPercent(o, "EstimatedChargePercent");
                if (pct is null) continue;
                string kind = Classify(instance);
                list.Add(new DeviceInfo(PrettyName(instance, kind), kind, pct.Value, false));
            }
        }
        catch (Exception ex)
        {
            // 以前 catch { } 一句带过：这个查询在 Win11 上必然失败，失败原因永远查不到
            Log($"MSBatteryClass 查询失败：{ex.GetType().Name}: {ex.Message}");
        }
        foreach (var d in list) yield return d;
    }

    /// <summary>读 WMI 属性（属性不存在/读取失败时返回 null，不抛）。</summary>
    private static object? GetProp(System.Management.ManagementBaseObject o, string name)
    {
        try { return o[name]; } catch { return null; }
    }

    /// <summary>该实例是否有这个属性（MSBatteryClass 各机型属性表不同，不能假定存在）。</summary>
    private static bool HasProp(System.Management.ManagementBaseObject o, string name)
    {
        try
        {
            foreach (var p in o.Properties)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 读电量百分比：EstimatedChargeRemaining / EstimatedChargePercent 都是 uint16，
    /// 固件常用 255 表示"未知"，超出 0..100 一律当未知（null = 不显示）。
    /// </summary>
    private static int? ReadPercent(System.Management.ManagementBaseObject o, string name)
    {
        object? raw = GetProp(o, name);
        if (raw is null) return null;
        int v = ToInt(raw);
        if (v is < 0 or > 100) return null;
        return v;
    }

    /// <summary>安全转 int（WMI 里可能是 uint / ushort / string）。</summary>
    private static int ToInt(object? raw)
    {
        try { return raw is null ? 0 : Convert.ToInt32(raw); } catch { return 0; }
    }

    /// <summary>
    /// 内部电池识别：实例名含 Microsoft / ACPI / PNP0C0A 的都是笔记本内置电池，
    /// 显示在设备页会和 Win32_Battery 那条重复（旧规则只认 "Microsoft"，
    /// 本机实例名是 ACPI\PNP0C0A\0_0，漏判了）。
    /// </summary>
    private static bool IsInternalBattery(string instance)
    {
        string l = instance.ToLowerInvariant();
        return l.Contains("microsoft") || l.Contains("acpi") || l.Contains("pnp0c0a");
    }

    /// <summary>
    /// 按实例名关键词判设备类型。
    /// 旧规则"BTHENUM 一律 headset"是错的：BLE 鼠标/键盘的实例名里同样有 BTHENUM，
    /// 结果鼠标耳机全都显示成"耳"。现在只认真正的类型关键词。
    /// </summary>
    private static string Classify(string instance)
    {
        string l = instance.ToLowerInvariant();
        if (l.Contains("headset") || l.Contains("headphone")
            || l.Contains("earphone") || l.Contains("earbud")) return "headset";
        if (l.Contains("mouse") || l.Contains("trackball")) return "mouse";
        if (l.Contains("keyboard") || l.Contains("keybrd")) return "keyboard";
        if (l.Contains("gamepad") || l.Contains("xinput") || l.Contains("xbox")
            || l.Contains("controller")) return "gamepad";
        return "battery";   // 认不出来的 BLE 设备：只报电量，类型不硬猜
    }

    /// <summary>
    /// 显示名：不再直接截第一个 "_" 之前的原始实例 ID
    /// （BTHENUM\DEV_8C:xx:xx_VID… 截出来是一串没意义的码），
    /// 改成"类型名 + 可读标识"：从实例名里抠出 DEV_ / VID_ 段。
    /// </summary>
    private static string PrettyName(string instance, string kind)
    {
        string label = kind switch
        {
            "mouse" => "蓝牙鼠标",
            "keyboard" => "蓝牙键盘",
            "headset" => "蓝牙耳机",
            "gamepad" => "蓝牙手柄",
            _ => "蓝牙设备",
        };
        string id = ExtractId(instance);
        return id.Length == 0 ? label : $"{label} {id}";
    }

    /// <summary>从实例名里取出可读标识（BTHENUM\DEV_8C:B9:EB.11_VID… → 8C-B9-EB-11）。</summary>
    private static string ExtractId(string instance)
    {
        string s = instance;
        int start = s.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
        if (start < 0) start = s.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "";
        start += 4;   // 跳过 DEV_ / VID_
        int end = s.IndexOfAny(new[] { '_', '&', '\\' }, start);
        string id = end > start ? s.Substring(start, end - start) : s.Substring(start);
        var sb = new System.Text.StringBuilder();
        foreach (char c in id)
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        string cleaned = sb.ToString().Trim('-');
        while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");
        return cleaned.Length > 24 ? cleaned.Substring(0, 24) : cleaned;
    }

    /// <summary>诊断日志上限（约 1MB）：超过就删掉旧文件重开一份（同 ThermalService 的做法）。</summary>
    private const long DiagLogLimit = 1_048_576;

    /// <summary>
    /// 诊断日志（%LOCALAPPDATA%\MyIsland\thermal-diag.log）：WMI 查询失败时留痕，
    /// 否则"这个功能永远是空的"没法自诊断。写失败只吞掉，不影响运行。
    /// </summary>
    private static void Log(string text)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyIsland");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "thermal-diag.log");
            var info = new System.IO.FileInfo(path);
            if (info.Exists && info.Length > DiagLogLimit) System.IO.File.Delete(path);
            System.IO.File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {text}\n");
        }
        catch { /* 日志写失败不影响运行 */ }
    }
}
