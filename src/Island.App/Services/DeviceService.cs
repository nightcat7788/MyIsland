using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;

namespace Island.App.Services;

/// <summary>
/// 无线设备电量（鼠标/键盘/耳机/手柄）+ 笔记本电池。
/// 数据源（零驱动）：
///  ① WMI root\CIMV2 Win32_Battery —— 笔记本内置电池（多电池聚合成一条）
///  ② WMI root\WMI MSBatteryClass —— Windows 主动上报电量的蓝牙设备
///  ③ BLE GATT 直读（Services/BleBatteryService.cs，2026-11-25）—— MSBatteryClass 不上报的
///     蓝牙设备（如本机的联想 BLE HID 设备）；缓存最多 6 分钟，读取失败即失效，不伪装
///  ④ XInput 手柄档位（Services/XInputService.cs，2026-11-25）—— 空/低/中/满，不换算百分比
/// ⚠️ 仍然没有的数据源：厂商自定义 HID 协议（2.4G 接收器，罗技 HID++ / ROG / 雷蛇等）——
///     每条都要按 VID/PID+接口+报文适配并实机回包验证（ai-periph-monitor 的做法），
///     没有对应硬件前不写。标准 HID 电池字段（Usage Page 0x85）同样没有，别按它们写代码。
/// 采样线程 30s 一轮（低电量提醒开着且没人看设备页时 5 分钟一轮，见 BackgroundWatch），
/// 失败静默（列表为空 UI 显示"未检测到"），失败原因写 thermal-diag.log。
/// Win32_Battery 查询失败后冷却 5 分钟（冷却期里沿用最近的读数），见 SampleWmiBattery。
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
    private static List<DeviceInfo> _nonBleDevices = new();

    static DeviceService()
    {
        BleBatteryService.Updated += PublishBleSnapshot;
    }

    private static void PublishBleSnapshot()
    {
        lock (_gate) _devices = _nonBleDevices.Concat(BleBatteryService.Snapshot()).ToList();
    }
    private static Timer? _timer;
    private static bool _wanted;

    /// <summary>
    /// 低电量提醒的后台慢轮询（2026-11-25）：设备视图没人看时也按 5 分钟一轮采样，
    /// 否则提醒功能永远没有新数据可看（本来是"谁要看才跑"的纯按需模型）。
    /// 与 SetWanted(true) 并存：视图在看就用正常的 30s 节拍，这个标志只是兜底。
    /// 由宿主按设置同步（IslandWindow 每次监控快照时写一次，值没变是空操作）。
    /// </summary>
    private static bool _backgroundWatch;

    public static bool BackgroundWatch
    {
        get { lock (_gate) return _backgroundWatch; }
        set
        {
            lock (_gate)
            {
                if (_backgroundWatch == value) return;
                _backgroundWatch = value;
                RetuneTimer();
            }
        }
    }

    /// <summary>当前设备电量快照（UI 线程直接读）。</summary>
    public static List<DeviceInfo> Snapshot
    {
        get
        {
            lock (_gate) return _devices.Where(d => d.ObservedAt is not { } at
                || DateTime.Now - at < BleBatteryService.FreshFor).ToList();
        }
    }

    /// <summary>是否正在轮询（诊断用）。</summary>
    public static bool Running { get { lock (_gate) return _timer is not null; } }

    /// <summary>
    /// 定时器节拍：视图要看 → 30s；只剩低电量提醒兜底 → 300s；谁都不要 → 停表。
    /// 以前 Start/SetWanted 里各写一份"建表 / 拆表"，现在收拢到这一处。
    /// </summary>
    private static void RetuneTimer()
    {
        if (!_wanted && !_backgroundWatch)
        {
            _timer?.Dispose();
            _timer = null;
            return;
        }
        int period = _wanted ? 30_000 : 300_000;
        if (_timer is null) _timer = new Timer(_ => Sample(), null, 0, period);
        else _timer.Change(0, period);
    }

    /// <summary>
    /// 启动轮询（幂等）。
    /// 顺带把 _wanted 置 true：否则宿主先 Start()、后 SetWanted(false) 时，
    /// SetWanted 会因"_wanted 没变"提前返回 → 定时器停不掉。
    /// </summary>
    public static void Start()
    {
        lock (_gate) { _wanted = true; RetuneTimer(); }
    }

    /// <summary>
    /// 停止轮询（视图消费者消失）。低电量提醒开着时退到 5 分钟慢轮询而不是全停。
    /// 停止后 <see cref="Start"/> 仍可重新拉起。
    /// </summary>
    public static void Stop()
    {
        lock (_gate)
        {
            _wanted = false;
            RetuneTimer();
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
            RetuneTimer();   // 节拍随消费者变化；两个消费者都消失时在 RetuneTimer 里停表
        }
    }

    /// <summary>
    /// 手动补一拍（托盘菜单打开时调用）：定时器停着的时候也能拿到一次新鲜读数；
    /// 定时器在跑就直接返回（等下一拍即可）。采样在后台线程，菜单先显示缓存值。
    /// </summary>
    private static bool _poking;

    public static void Poke()
    {
        lock (_gate)
        {
            if (_timer is not null || _poking) return;
            _poking = true;
        }
        System.Threading.Tasks.Task.Run(() =>
        {
            try { Sample(); }
            finally { lock (_gate) _poking = false; }
        });
    }

    /// <summary>设备类型单字图标（省空间）。"ble" = GATT 直读、类型没认出来的蓝牙设备。</summary>
    public static string Glyph(string kind) => kind switch
    {
        "mouse" => "鼠",
        "keyboard" => "键",
        "headset" => "耳",
        "gamepad" => "柄",
        "battery" => "电",
        "ble" => "电",
        _ => "?",
    };

    private static void Sample()
    {
        var list = new List<DeviceInfo>();
        try { list.AddRange(SampleWmiBattery()); } catch { }
        try { list.AddRange(SampleBluetoothBattery()); } catch { }
        // BLE GATT 直读（2026-11-25）：补 MSBatteryClass 不上报的蓝牙设备。扫描在后台异步跑，
        // 这里只触发并取缓存里已验证的读数（读不到的设备不进列表，不伪装）。
        try { BleBatteryService.Warm(); } catch { }
        // XInput 手柄档位（空/低/中/满）：没有手柄 / 没有 xinput dll 时是空列表，UI 自然不显示。
        try { list.AddRange(XInputService.Snapshot()); } catch { }
        lock (_gate)
        {
            _nonBleDevices = list;
            _devices = list.Concat(BleBatteryService.Snapshot()).ToList();
        }
    }

    /// <summary>
    /// 笔记本电池（WMI Win32_Battery，无额外依赖）。
    /// 多电池聚合成一条：取最低电量（最保守），充电态取"任一在充电"，
    /// 免得界面上出现两条同名的"笔记本电池"。
    ///
    /// 2026-11-24 修复：查询里原先 SELECT 了 InstanceName —— Win32_Battery 根本没有这个属性
    /// （那是 root\WMI MSBatteryClass 的属性），整条查询在本机恒报 "无效查询"：
    /// 每 30 秒往 thermal-diag.log 刷一行（已刷 70 多行），笔记本电池永远不显示。
    /// 去重键改成 Win32_Battery 真有的 DeviceID（每块电池唯一）。
    ///
    /// 加失败冷却（同 ThermalService.ReadWmiThermal）：查询抛异常后 5 分钟内不再碰 WMI，
    /// 失败日志一个冷却期只留一条 —— 否则持久失败的查询会每 30 秒唤醒一次 WMI provider host 并刷屏。
    /// 冷却期间沿用上一次成功的读数（同 WeatherService 失败保留上次数据的做法），但最多沿用一个冷却时长：
    /// 收起态 dash 视图是"有电池才画'电'那一组"，读数一消失岛体宽度就跳一下；太旧的读数则宁可不显示。
    /// 查询成功但 0 行（台式机没电池）不算失败：不进冷却、不记日志，行为同以前。
    /// </summary>
    private static IEnumerable<DeviceInfo> SampleWmiBattery()
    {
        DeviceInfo? battery;
        string? error = null;
        lock (_batteryGate)
        {
            if (DateTime.Now >= _batteryNextTry)                          // 冷却中：直接跳过，不再碰 WMI
            {
                try
                {
                    _lastBattery = ReadWmiBattery();
                    _lastBatteryAt = DateTime.Now;
                    _batteryNextTry = DateTime.MinValue;                  // 成功：清掉冷却
                }
                catch (Exception ex)
                {
                    _batteryNextTry = DateTime.Now + BatteryCooldown;      // 失败：进入冷却
                    error = $"{ex.GetType().Name}: {ex.Message}";
                }
            }
            battery = _lastBattery is not null && DateTime.Now - _lastBatteryAt < BatteryCooldown
                ? _lastBattery : null;
        }
        if (error is not null)
            Log($"Win32_Battery 查询失败（{(int)BatteryCooldown.TotalMinutes} 分钟内不再重试）：{error}");
        return battery is null ? Enumerable.Empty<DeviceInfo>() : new[] { battery };
    }

    /// <summary>
    /// Win32_Battery 查询失败的冷却时长。
    /// 本机旧查询恒报 "无效查询"，不冷却就是每 30 秒失败一次，反复唤醒 WMI provider host。
    /// </summary>
    private static readonly TimeSpan BatteryCooldown = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 电池查询冷却状态的锁：轮询在线程池线程上，冷却时间戳要线程安全地读写。
    /// 单独一把锁、不复用 _gate：WMI 查询要十几毫秒，而 _gate 还护着 UI 线程每次渲染都要读的 Snapshot。
    /// </summary>
    private static readonly object _batteryGate = new();

    /// <summary>下次允许再查 Win32_Battery 的时间；DateTime.MinValue = 不在冷却期。</summary>
    private static DateTime _batteryNextTry = DateTime.MinValue;

    /// <summary>上一次成功查询得到的电池读数（null = 没电池/固件报未知）及其时刻；冷却期间沿用。</summary>
    private static DeviceInfo? _lastBattery;
    private static DateTime _lastBatteryAt = DateTime.MinValue;

    /// <summary>
    /// 执行 Win32_Battery 查询并聚合成一条。没有可用电池（台式机 / 固件报未知）返回 null；查询失败抛异常。
    /// </summary>
    private static DeviceInfo? ReadWmiBattery()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? lowest = null;
        bool anyCharging = false;
        using var searcher = new System.Management.ManagementObjectSearcher(
            "root\\CIMV2", "SELECT DeviceID, EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
        foreach (var o in searcher.Get())
        {
            // 按设备标识去重：同一块电池在 WMI 里可能有多条记录（DeviceID 每块电池唯一）
            string id = GetProp(o, "DeviceID")?.ToString() ?? "";
            if (id.Length > 0 && !seen.Add(id)) continue;

            int? pct = ReadPercent(o, "EstimatedChargeRemaining");
            if (pct is null) continue;      // 255 = 固件报"未知"，不显示
            lowest = lowest is null ? pct : Math.Min(lowest.Value, pct.Value);

            if (IsChargingStatus(ToInt(GetProp(o, "BatteryStatus")))) anyCharging = true;
        }
        return lowest is null ? null : new DeviceInfo("笔记本电池", "battery", lowest.Value, anyCharging);
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
        return DeviceInfo.ParsePercent(GetProp(o, name));
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
        return "ble";   // 未知外设不能冒充本机电池
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

    /// <summary>给兄弟服务（BLE GATT）用的日志入口：同一个文件，设备电量的链路问题查在一处。</summary>
    internal static void LogLine(string text) => Log(text);

    // 节流日志的状态（BLE 扫描每 25s 一轮，持续失败时同一句话会刷屏）
    private static readonly object _throttleGate = new();
    private static readonly Dictionary<string, DateTime> _throttledAt = new();

    /// <summary>同一句话在 cooldown 内只写一次日志；超量的旧条目整表清掉（句子的总数很小）。</summary>
    internal static void LogLineThrottled(string text, TimeSpan cooldown)
    {
        lock (_throttleGate)
        {
            if (_throttledAt.TryGetValue(text, out var at) && DateTime.Now - at < cooldown) return;
            if (_throttledAt.Count > 64) _throttledAt.Clear();
            _throttledAt[text] = DateTime.Now;
        }
        Log(text);
    }

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
