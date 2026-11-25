using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Island.App.Services.Peripheral;

/// <summary>
/// 外设电量（2.4G 接收器 / 无线键鼠耳机）—— HID 访问层的使用者。
///
/// 分层探测，顺序固定，先命中先返回：
///   L1 标准 HID 电量 —— 按 HID Usage 表的电量 usage 读，不猜字节偏移
///   L3 厂商私有协议 —— 数据驱动的协议表（新增品牌 = 填表，不用改架构）
///   L4 通用识别兜底 —— 只把设备列出来，电量显示"未知"
/// （L2 系统数据源在 DeviceService 那边：WMI / MSBatteryClass / BLE GATT / XInput。）
///
/// 诚实性（与既有 BLE / XInput 数据源同一口径）：
///   · 读不到 / 校验不过 → 显示"未知"，绝不用 0 或默认值充数；
///   · 只发只读查询命令，不写配置、不升级固件；
///   · 不需要管理员、不装驱动、不联网。
///
/// 节流：扫描在后台异步跑（与 BLE 同款），UI 只读缓存快照；
/// 由 DeviceService 的采样节拍（设备页 30s / 低电量兜底 300s）调 <see cref="Warm"/> 触发。
/// </summary>
internal static class PeripheralBatteryService
{
    /// <summary>缓存读数的有效期：超过就不显示（过期读数比没有更糟）。</summary>
    internal static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(6);

    /// <summary>两轮扫描之间的最小间隔（防止频繁枚举 HID 设备树）。</summary>
    private static readonly TimeSpan ScanThrottle = TimeSpan.FromSeconds(20);

    /// <summary>L1 读 Input 报告的等待上限（毫秒）：设备迟迟不上报就当这轮没读到。</summary>
    private const int InputBatteryReadMs = 250;

    /// <summary>用户自带的协议表（填表即可新增品牌，不用改代码）。</summary>
    internal static string UserProtocolFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyIsland", "peripheral-protocols.json");

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DeviceInfo Info, DateTime At)> Cache = new();
    private static Task? _scan;
    private static DateTime _lastStart = DateTime.MinValue;
    /// <summary>
    /// 用户协议表文件的"指纹"（是否存在 + 最后写入时间 + 长度）：
    /// 指纹变了就重新装载（用户改完 JSON 不用重启进程）。
    /// </summary>
    private static (bool Exists, DateTime Written, long Length)? _userTableStamp;
    private static string _lastSummary = "";

    /// <summary>读数更新时触发（DeviceService 借此刷新快照）。</summary>
    public static event Action? Updated;

    /// <summary>触发一轮后台扫描（扫描在途 / 距上次不足节流间隔时直接返回）。</summary>
    public static void Warm()
    {
        lock (Gate)
        {
            if (_scan is { IsCompleted: false }) return;
            if (DateTime.Now - _lastStart < ScanThrottle) return;
            _lastStart = DateTime.Now;
            _scan = Task.Run(Scan);
        }
    }

    /// <summary>当前可信的读数（缓存里且未过期的）。</summary>
    public static List<DeviceInfo> Snapshot()
    {
        lock (Gate)
            return Cache.Values
                .Where(v => DateTime.Now - v.At < FreshFor)
                .Select(v => v.Info)
                .ToList();
    }

    /// <summary>清空缓存（设备拔了 / 读数过期）。</summary>
    public static void Clear()
    {
        lock (Gate) Cache.Clear();
        Updated?.Invoke();
    }

    private static void Scan()
    {
        try
        {
            LoadUserTable();
            var groups = HidBus.GroupByDevice(HidBus.Enumerate());
            var found = new Dictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);
            var notes = new List<string>();

            foreach (var group in groups)
            {
                DeviceInfo? device = ProbeGroup(group, notes);
                if (device is null) continue;
                found[$"{group.Vid:X4}:{group.Pid:X4}"] = device;
            }

            lock (Gate)
            {
                Cache.Clear();
                var now = DateTime.Now;
                foreach (var kv in found) Cache[kv.Key] = (kv.Value, now);
            }
            Updated?.Invoke();

            // 只在读数**变化**时记一行：设备页每 30 秒扫一次，逐轮记日志会把诊断日志刷爆
            string summary = string.Join(" / ",
                found.Values.Select(d => $"{d.Name} {d.LevelText ?? d.Percent.ToString() + "%"}"));
            if (summary != _lastSummary)
            {
                _lastSummary = summary;
                DeviceService.LogLine("HID 外设电量：" + summary);
            }
            foreach (string note in notes) DeviceService.LogLineThrottled(note, TimeSpan.FromMinutes(30));
        }
        catch (Exception ex)
        {
            DeviceService.LogLineThrottled("HID 外设电量扫描失败：" + ex.GetType().Name + ": " + ex.Message,
                TimeSpan.FromMinutes(30));
        }
    }

    /// <summary>
    /// 探测一台物理设备（同一 VID:PID 的全部集合）。命中即返回；什么都没读到返回 null。
    /// </summary>
    private static DeviceInfo? ProbeGroup(HidGroup group, List<string> notes)
    {
        string displayName = PeripheralClassifier.DisplayName(group.Members);

        // ---- L1：标准 HID 电量 ----
        foreach (var m in group.Members)
        {
            PeripheralReading? reading = ReadStandardBattery(m);
            if (reading is null) continue;
            return new DeviceInfo(
                displayName,
                PeripheralClassifier.KindOf(m.Product, m.DeviceDesc, m.UsagePage, m.Usage),
                reading.Percent,
                reading.Charging) { ObservedAt = DateTime.Now };
        }

        // ---- L3：厂商私有协议表 ----
        foreach (var m in group.Members)
        {
            var protocol = PeripheralProtocols.Match(group.Vid, group.Pid, m.UsagePage, m.Usage,
                m.OutputReportLength);
            if (protocol is null) continue;
            string name = protocol.Vendor.Length > 0 && protocol.Model.Length > 0
                ? protocol.Vendor + " " + protocol.Model
                : protocol.Model.Length > 0 ? protocol.Model : displayName;
            PeripheralReading? reading = ReadVendor(m, protocol);
            if (reading is not null)
                return new DeviceInfo(name, protocol.Kind, reading.Percent, reading.Charging)
                { ObservedAt = DateTime.Now };

            // 协议对上了但这次没读到：如实显示"未知"，别假装没有这台设备
            notes.Add($"HID 外设 {protocol.Key}（{group.Vid:X4}:{group.Pid:X4}）本轮没读到电量，按未知显示");
            return new DeviceInfo(name, protocol.Kind, 0, false, "未知") { ObservedAt = DateTime.Now };
        }

        // ---- L4：通用识别兜底（只认名称里带"2.4G / 接收器"特征的） ----
        if (!PeripheralClassifier.LooksWireless(displayName)) return null;
        foreach (var m in group.Members)
        {
            if (!PeripheralClassifier.LooksWireless(m.Product) && !PeripheralClassifier.LooksWireless(m.DeviceDesc))
                continue;
            return new DeviceInfo(displayName,
                PeripheralClassifier.KindOf(m.Product, m.DeviceDesc, m.UsagePage, m.Usage),
                0, false, "未知") { ObservedAt = DateTime.Now };
        }
        return null;
    }

    /// <summary>
    /// L1：按标准电量 usage 读报告（用 HidP_GetUsageValue 让系统按描述符解析）。
    ///
    /// 报告类型按能力表区分（这是此前的盲区 —— 硬编码 HIDP_FEATURE 时，
    /// 只在 Input 能力表里声明电量 usage 的设备永远读不到）：
    ///  · usage 出现在 **Feature** 能力表里 → 读 Feature 报告（可以主动读）。
    ///    独占集合用 0 权限打开即可（打不开就换下一个集合）。
    ///  · usage 只出现在 **Input** 能力表里 → 只能等设备主动上报：
    ///    得用读写权限打开并读输入报告（只查句柄上的读是同步的，不能用）。
    /// </summary>
    private static PeripheralReading? ReadStandardBattery(HidInterface member)
    {
        foreach (var (page, usage) in StandardHidBattery.PercentUsages)
        {
            HidUsageRange? caps = StandardHidBattery.FindUsage(member, page, usage);
            if (caps is null) continue;
            bool inFeature = member.FeatureUsages.Any(u => u.Covers(page, usage));
            PeripheralReading? reading = inFeature
                ? ReadStandardBatteryFeature(member, caps, page, usage)
                : ReadStandardBatteryInput(member, caps, page, usage);
            if (reading is not null) return reading;
        }
        return null;
    }

    /// <summary>L1 的 Feature 报告读法（描述符解析按 HIDP_FEATURE 走）。</summary>
    private static PeripheralReading? ReadStandardBatteryFeature(HidInterface member, HidUsageRange caps,
        ushort page, ushort usage)
    {
        try
        {
            using var channel = new HidChannel(member, queryOnly: true);
            byte[]? report = channel.GetFeature(caps.ReportId, member.FeatureReportLength);
            if (report is null) return null;
            if (!channel.TryReadUsageValue(HidNative.HIDP_FEATURE, page, usage, report, out int raw))
                return null;
            int? percent = StandardHidBattery.ToPercent(raw, caps.LogicalMin, caps.LogicalMax);
            return percent is null ? null : new PeripheralReading(percent.Value, false);
        }
        catch
        {
            // 打不开 / 读失败：换下一个集合，不影响其它设备
            return null;
        }
    }

    /// <summary>
    /// L1 的 Input 报告读法（描述符解析按 HIDP_INPUT 走）：
    /// 只在 Input 能力表里声明电量 usage 的设备走这条路 —— 只能等设备主动上报。
    /// </summary>
    private static PeripheralReading? ReadStandardBatteryInput(HidInterface member, HidUsageRange caps,
        ushort page, ushort usage)
    {
        try
        {
            // 读 Input 报告必须有读权限（只查句柄上的 Read 是同步的，会把扫描线程堵死）
            using var channel = new HidChannel(member);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                byte[]? report = channel.Read(InputBatteryReadMs);
                if (report is null) return null;   // 设备没上报：这轮就没有电量
                if (!channel.TryReadUsageValue(HidNative.HIDP_INPUT, page, usage, report, out int raw))
                    continue;   // 这一帧里没这个 usage：再等一帧
                int? percent = StandardHidBattery.ToPercent(raw, caps.LogicalMin, caps.LogicalMax);
                if (percent is null) continue;
                return new PeripheralReading(percent.Value, false);
            }
            return null;
        }
        catch
        {
            // 打不开（独占集合多半打不开）/ 读失败：换下一个集合
            return null;
        }
    }

    /// <summary>
    /// L3：按协议表读厂商私有协议。
    /// 两种模式：直读 Feature 报告（FeatureReportId ≥ 0），或者发命令收回包。
    /// </summary>
    private static PeripheralReading? ReadVendor(HidInterface member, PeripheralProtocol protocol)
    {
        try
        {
            if (protocol.FeatureReportId >= 0)
            {
                using var channel = new HidChannel(member, queryOnly: true);
                byte[]? report = channel.GetFeature((byte)protocol.FeatureReportId, member.FeatureReportLength);
                if (report is null) return null;
                return PeripheralProtocolReader.TryParse(protocol, report, out PeripheralReading? direct)
                    && direct is not null ? direct : null;
            }

            using var io = new HidChannel(member);
            foreach (byte[] pre in protocol.PreCommands) Send(io, protocol.Transport, pre);
            if (protocol.Command.Length == 0) return null;
            Send(io, protocol.Transport, protocol.Command);
            for (int i = 0; i < 4; i++)
            {
                byte[]? response = io.Read(300);
                if (response is null) break;
                if (PeripheralProtocolReader.TryParse(protocol, response, out PeripheralReading? reading)
                    && reading is not null)
                    return reading;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static void Send(HidChannel channel, PeripheralTransport transport, byte[] frame)
    {
        switch (transport)
        {
            case PeripheralTransport.Interrupt: channel.WriteInterrupt(frame); break;
            case PeripheralTransport.Feature: channel.WriteFeature(frame); break;
            default: channel.WriteControl(frame); break;
        }
    }

    /// <summary>
    /// 装载用户自带的协议表（%LOCALAPPDATA%\MyIsland\peripheral-protocols.json）。
    ///
    /// 按文件"指纹"（是否存在 + 最后写入时间 UTC + 长度）判断要不要重读：
    /// 用户改完 JSON 不重启进程也能生效 —— 协议表本来就是给人改的纠错通道。
    /// 文件不存在时也要记下指纹（Exists=false），
    /// 这样之后用户新建了文件能被认出来（而不是被"已经装载过了"挡住）。
    /// 指纹取的是**读文件之前**的时刻：读的过程中又改了文件，下一轮会再读一次。
    /// </summary>
    private static void LoadUserTable()
    {
        string path = UserProtocolFile;
        bool exists;
        DateTime written;
        long length;
        try
        {
            var info = new FileInfo(path);
            exists = info.Exists;
            written = exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            length = exists ? info.Length : 0L;
        }
        catch
        {
            return;   // 路径取不到（权限/环境问题）：下轮再说
        }

        lock (Gate)
        {
            if (_userTableStamp is { } last
                && last.Exists == exists && last.Written == written && last.Length == length)
                return;   // 文件没动：不重读
        }

        if (!exists)
        {
            // 文件不存在 = 没有用户条目：记下指纹（之后新建文件能被认出来）
            lock (Gate) _userTableStamp = (exists, written, length);
            PeripheralProtocols.ClearUserProtocols();
            return;
        }

        try
        {
            string json = File.ReadAllText(path);
            var protocols = PeripheralProtocols.LoadFromJson(json);
            // 整表重装：文件里删掉的条目也该消失
            PeripheralProtocols.ClearUserProtocols();
            if (protocols.Count > 0) PeripheralProtocols.AddUserProtocols(protocols);
            lock (Gate) _userTableStamp = (exists, written, length);
            DeviceService.LogLine($"HID 外设协议表：已装载用户条目 {protocols.Count} 条（{path}）");
        }
        catch (Exception ex)
        {
            // 装载失败：保留旧表（不清空），并且**不更新指纹** —— 下轮重试
            DeviceService.LogLineThrottled("HID 外设协议表装载失败：" + ex.Message, TimeSpan.FromMinutes(30));
        }
    }
}
