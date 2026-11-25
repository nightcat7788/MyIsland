using System.Globalization;
using System.Text;
using Island.App.Services;
using Island.App.Services.Peripheral;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace Island.Tools.HidProbe;

/// <summary>
/// 外设电量 · HID 侦察工具（开发期专用，不随产品发布）。
///
/// 用法：
///   hid-probe scan                 列出本机所有 HID 集合（usage / 报告长度 / 能力表 / 报告 ID）
///   hid-probe read &lt;序号&gt; &lt;毫秒&gt;   被动读某个集合的输入报告（只读，不发任何命令）
///   hid-probe probe &lt;序号&gt; &lt;hex&gt;  发一帧命令（控制端点 + 中断 OUT + Feature 各试一次）并打印回包
///   hid-probe sweep &lt;序号&gt;      批量试一批候选命令帧
///   hid-probe --selftest        协议表自检（表合法性校验 + 本机外设命中报告）
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 2;
        }
        string mode = args[0].ToLowerInvariant();
        List<HidInterface> all = HidBus.EnumerateFresh();
        switch (mode)
        {
            case "scan":
                Scan(all);
                return 0;
            case "read":
                if (args.Length < 3) { Usage(); return 2; }
                Read(all, int.Parse(args[1]), int.Parse(args[2]));
                return 0;
            case "probe":
                if (args.Length < 3) { Usage(); return 2; }
                Probe(all, int.Parse(args[1]), args[2]);
                return 0;
            case "feat":
                if (args.Length < 2) { Usage(); return 2; }
                Feature(all, int.Parse(args[1]));
                return 0;
            case "usage":
                if (args.Length < 2) { Usage(); return 2; }
                UsageDump(all, int.Parse(args[1]));
                return 0;
            case "watch":
                if (args.Length < 3) { Usage(); return 2; }
                Watch(all, int.Parse(args[1]), int.Parse(args[2]));
                return 0;
            case "off":
                if (args.Length < 2) { Usage(); return 2; }
                Offsets(all, int.Parse(args[1]));
                return 0;
            case "scanfeat":
                if (args.Length < 2) { Usage(); return 2; }
                ScanFeatureIds(all, int.Parse(args[1]));
                return 0;
            case "e2e":
                await EndToEnd();
                return 0;
            case "--selftest":
            case "selftest":
                return await SelfTest(all);
            case "json":
                JsonTry();
                return 0;
            case "sweep":
                if (args.Length < 2) { Usage(); return 2; }
                Sweep(all, int.Parse(args[1]));
                return 0;
            case "ble":
                await BleBattery();
                return 0;
            default:
                Usage();
                return 2;
        }
    }

    /// <summary>
    /// 读蓝牙 GATT 的标准电池服务（0x180F / 0x2A19）——
    /// 这是 dongle 之外的独立参照，用来核对 dongle 私有协议里哪个字节才是电量。
    /// </summary>
    private static async Task BleBattery()
    {
        var batteryService = new Guid("0000180F-0000-1000-8000-00805F9B34FB");
        var batteryLevel = new Guid("00002A19-0000-1000-8000-00805F9B34FB");
        var deviceInfo = new Guid("0000180A-0000-1000-8000-00805F9B34FB");
        var deviceName = new Guid("00002A00-0000-1000-8000-00805F9B34FB");
        var manufacturer = new Guid("00002A29-0000-1000-8000-00805F9B34FB");

        var infos = await DeviceInformation.FindAllAsync(
            GattDeviceService.GetDeviceSelectorFromUuid(batteryService));
        Console.WriteLine($"暴露电池服务的 GATT 服务项：{infos.Count}");
        foreach (var info in infos)
        {
            Console.WriteLine($"  {info.Id}");
            GattDeviceService? svc = null;
            try
            {
                svc = await GattDeviceService.FromIdAsync(info.Id);
                if (svc is null) { Console.WriteLine("    拿不到服务对象"); continue; }
                try
                {
                    var allChars = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    Console.WriteLine($"    全部特征 {allChars.Status} 共 {allChars.Characteristics.Count} 个");
                    foreach (var c in allChars.Characteristics)
                        Console.WriteLine($"      char uuid={c.Uuid} props={c.CharacteristicProperties}");
                }
                catch (Exception ex) { Console.WriteLine("    枚举特征失败：" + ex.Message); }
                foreach (var (uuid, label) in new[]
                {
                    (batteryLevel, "电量 2A19"), (deviceName, "设备名 2A00"),
                    (manufacturer, "厂商 2A29"),
                })
                {
                    var chars = await svc.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
                    foreach (var c in chars.Characteristics)
                    {
                        var v = await c.ReadValueAsync(BluetoothCacheMode.Uncached);
                        using var reader = DataReader.FromBuffer(v.Value);
                        byte[] bytes = new byte[v.Value.Length];
                        reader.ReadBytes(bytes);
                        Console.WriteLine($"    {label}: {BitConverter.ToString(bytes)}"
                            + (label.StartsWith("电量") && bytes.Length > 0 ? $"  => {bytes[0]}%" : ""));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("    读取失败：" + ex.Message);
            }
            finally { try { svc?.Dispose(); } catch { } }
        }

        var infoInfos = await DeviceInformation.FindAllAsync(
            GattDeviceService.GetDeviceSelectorFromUuid(deviceInfo));
        foreach (var info in infoInfos)
        {
            Console.WriteLine($"  设备信息服务 {info.Id} name={info.Name}");
        }
    }

    /// <summary>
    /// 端到端：直接跑生产的 PeripheralBatteryService（枚举 → 分组 → 匹配协议 → 读特征报告 → 解析），
    /// 把最终的 DeviceInfo 列表打出来。
    /// </summary>
    private static async Task EndToEnd()
    {
        PeripheralBatteryService.Warm();
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(200);
            var snap = PeripheralBatteryService.Snapshot();
            if (snap.Count > 0)
            {
                Console.WriteLine($"--- 快照（{snap.Count} 台）---");
                foreach (var d in snap)
                    Console.WriteLine($"  名称={d.Name} 类型={d.Kind} 电量={d.Percent}%"
                        + $" 档位={d.LevelText ?? "-"} 充电={d.Charging} 观测于={d.ObservedAt:HH:mm:ss}");
                return;
            }
        }
        Console.WriteLine("--- 快照为空（没读到任何外设电量）---");
    }

    /// <summary>用生产代码装载一份示例协议表，把真实异常打出来。</summary>
    private static void JsonTry()
    {
        string json = """
        [
          {
            "key": "demo-vendor",
            "vendor": "示例",
            "model": "示例接收器",
            "kind": "mouse",
            "vid": 4660,
            "pid": 62723,
            "usagePage": 65292,
            "usage": 2,
            "minReportLength": 64,
            "transport": "Interrupt",
            "preCommands": [ "05 01" ],
            "command": "05 02",
            "echo": "05 02",
            "valueOffset": 8,
            "valueMask": 255,
            "valueMin": 0,
            "valueMax": 100,
            "chargingOffset": 9,
            "chargingMask": 1,
            "chargingIsOne": true
          }
        ]
        """;
        try
        {
            var list = PeripheralProtocols.LoadFromJson(json);
            Console.WriteLine("装载 " + list.Count + " 条");
            foreach (var proto in list)
            {
                Console.WriteLine($"  key={proto.Key} vid={proto.Vid:X4} pid={proto.Pid:X4} "
                    + $"usage={proto.UsagePage:X4}:{proto.Usage:X4} transport={proto.Transport} "
                    + $"min={proto.MinReportLength} cmd=[{string.Join(" ", proto.Command.Select(b => b.ToString("X2")))}] "
                    + $"echo=[{string.Join(" ", proto.Echo.Select(b => b.ToString("X2")))}] "
                    + $"pre={proto.PreCommands.Length} valueOffset={proto.ValueOffset} "
                    + $"chargingOffset={proto.ChargingOffset}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("装载失败：" + ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine(ex.StackTrace);
        }
    }


    /// <summary>标准 HID 电量命中时的信息（读数 + 来源描述）。</summary>
    private sealed record StdHit(PeripheralReading Reading, string Source);

    /// <summary>厂商协议命中时的信息（读数 + 来源描述）。</summary>
    private sealed record VendorHit(PeripheralReading Reading, string Source);

    /// <summary>
    /// 自检模式：① 校验协议表本身是否合法 ② 报告当前连接的外设命中了表的哪一条、走的哪一层判定。
    ///
    /// 诚实性底线（与产品同一口径）：
    ///   · 读不到电量一律显示"未知"，绝不输出 0% / 100% 充数；
    ///   · L1（标准 HID usage）标"实测"，L3（协议表偏移）标"推断"，L4（兜底识别）标"未知"；
    ///   · 表校验有错误 ⇒ 退出码非 0（脚本/CI 可以直接拿它当门禁）。
    ///
    /// 说明：这里的 L1/L3 读法与产品 PeripheralBatteryService 是同一套共享实现
    /// （StandardHidBattery / PeripheralProtocols / PeripheralProtocolReader / HidChannel），
    /// 只有"先试哪一层、命中后怎么写报告"这层编排是本工具自己的。
    /// </summary>
    private static async Task<int> SelfTest(List<HidInterface> all)
    {
        Console.WriteLine("=== 外设协议表自检（hid-probe --selftest）===");
        Console.WriteLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Console.WriteLine();

        // ---------- 一、装载协议表 ----------
        // 进程刚起来时用户表是空的 ⇒ 这时的 All 就是内置表
        List<PeripheralProtocol> builtin = PeripheralProtocols.All.ToList();
        string userPath = PeripheralProtocolTable.DefaultUserFilePath;
        List<PeripheralProtocol> user = new List<PeripheralProtocol>();
        PeripheralProtocolTableReport userReport;
        string userLoadError = "";

        string? json = null;
        string? fileError = null;
        try
        {
            if (File.Exists(userPath)) json = File.ReadAllText(userPath);
        }
        catch (Exception ex)
        {
            fileError = ex.GetType().Name + ": " + ex.Message;
        }

        if (fileError is not null)
        {
            userReport = new PeripheralProtocolTableReport(userPath, new List<PeripheralProtocolIssue>
            {
                new PeripheralProtocolIssue("(文件)", userPath,
                    "读不了用户协议表文件：" + fileError),
            });
        }
        else if (json is null)
        {
            userReport = PeripheralProtocolTable.ValidateUserFile(userPath, builtin);
        }
        else
        {
            userReport = PeripheralProtocolTable.ValidateUserJson(json, userPath, builtin);
            try { user = PeripheralProtocols.LoadFromJson(json); }
            catch (Exception ex) { userLoadError = ex.GetType().Name + ": " + ex.Message; }
        }

        // 装进生产协议表（与产品同一套装载路径：用户条目在前，同 key 覆盖内置条目）
        PeripheralProtocols.ClearUserProtocols();
        if (user.Count > 0) PeripheralProtocols.AddUserProtocols(user);

        Console.WriteLine("---- 一、协议表校验 ----");
        var builtinReport = PeripheralProtocolTable.ValidateTable(builtin, "内置表");
        PrintTableReport(builtinReport);
        PrintTableReport(userReport);
        if (userLoadError.Length > 0)
            Console.WriteLine("  ! 用户表装载失败（产品会保留旧表）：" + userLoadError);

        Console.WriteLine();
        Console.WriteLine("---- 二、生效的协议表（用户条目在前：同 key 覆盖内置条目）----");
        foreach (PeripheralProtocol entry in PeripheralProtocolTable.EffectiveTable(builtin, user))
        {
            Console.WriteLine("  · " + entry.Key + "（" + SourceOf(entry, user) + "）  "
                + PeripheralProtocolTable.MatchSummary(entry)
                + "  电量偏移=" + entry.ValueOffset + " 掩码=0x" + entry.ValueMask.ToString("X2")
                + " 量程=" + entry.ValueMin + ".." + entry.ValueMax
                + " 直读报告=" + entry.FeatureReportId);
        }

        // ---------- 三、真机命中报告 ----------
        Console.WriteLine();
        Console.WriteLine("---- 三、真机命中报告（本机当前连接的外设）----");
        List<HidGroup> groups = HidBus.GroupByDevice(all);
        if (groups.Count == 0) Console.WriteLine("  （本机没有枚举到任何 HID 集合）");

        int l1Count = 0, l3Count = 0, l4Count = 0, noneCount = 0;
        foreach (HidGroup group in groups)
        {
            string displayName = PeripheralClassifier.DisplayName(group.Members);
            Console.WriteLine();
            Console.WriteLine("[设备] " + displayName);
            Console.WriteLine("  VID=" + group.Vid.ToString("X4") + " PID=" + group.Pid.ToString("X4")
                + "  集合数=" + group.Members.Count
                + "  类型=" + PeripheralClassifier.KindOf(group.Members[0].Product,
                    group.Members[0].DeviceDesc, group.Members[0].UsagePage, group.Members[0].Usage));
            Console.WriteLine("  集合：" + string.Join("  ", group.Members.Select(m =>
                m.UsagePage.ToString("X4") + ":" + m.Usage.ToString("X4")
                + "(in=" + m.InputReportLength + ",out=" + m.OutputReportLength
                + ",feat=" + m.FeatureReportLength + ")")));

            // ---- L1：标准 HID 电量 ----
            StdHit? std = null;
            foreach (HidInterface member in group.Members)
            {
                std = ReadStandardSelfTest(member);
                if (std is not null) break;
            }
            if (std is not null)
            {
                l1Count++;
                Console.WriteLine("  ─ L1 标准 HID 电量：命中");
                Console.WriteLine("      证据等级：实测（按设备报告描述符声明的电量 usage 读出，不猜字节偏移）");
                Console.WriteLine("      来源：" + std.Source);
                Console.WriteLine("      电量：" + std.Reading.Percent + "%");
                Console.WriteLine("      命中层：L1（标准 HID 电量）");
                continue;
            }
            Console.WriteLine("  ─ L1 标准 HID 电量：未命中（能力表里没有电量 usage："
                + string.Join(" / ", StandardHidBattery.PercentUsages.Select(u =>
                    u.Page.ToString("X4") + ":" + u.Usage.ToString("X4"))) + "）");

            // ---- L3：厂商私有协议表 ----
            PeripheralProtocol? protocol = null;
            HidInterface? protocolMember = null;
            foreach (HidInterface member in group.Members)
            {
                PeripheralProtocol? hit = PeripheralProtocols.Match(group.Vid, group.Pid,
                    member.UsagePage, member.Usage, member.OutputReportLength);
                if (hit is null) continue;
                protocol = hit;
                protocolMember = member;
                break;
            }
            if (protocol is not null && protocolMember is not null)
            {
                l3Count++;
                Console.WriteLine("  ─ L3 厂商私有协议表：命中「" + protocol.Key + "」（来源："
                    + SourceOf(protocol, user) + "）");
                Console.WriteLine("      表项：" + PeripheralProtocolTable.MatchSummary(protocol));
                foreach (PeripheralProtocolIssue issue in PeripheralProtocolTable.ValidateAgainstDevice(
                    protocol, protocolMember.FeatureReportLength, protocolMember.OutputReportLength,
                    protocolMember.ReportIds))
                    Console.WriteLine("      ! " + issue.Describe());

                VendorHit? vendor = ReadVendorSelfTest(protocolMember, protocol);
                if (vendor is not null)
                {
                    Console.WriteLine("      实测：" + vendor.Source);
                    Console.WriteLine("      推断：按表项 valueOffset=" + protocol.ValueOffset
                        + " / 掩码 0x" + protocol.ValueMask.ToString("X2")
                        + " / 量程 " + protocol.ValueMin + ".." + protocol.ValueMax
                        + " 换算 ⇒ " + vendor.Reading.Percent
                        + "%（**推断值**：该字节是不是电量由表项说了算，未经设备描述符确认）");
                    Console.WriteLine("      充电位：" + (protocol.ChargingOffset is null
                        ? "未知（本表项未声明 chargingOffset）"
                        : (vendor.Reading.Charging ? "在充电" : "未充电")));
                }
                else
                {
                    Console.WriteLine("      电量：未知（协议命中但本轮没读到 ⇒ 按未知显示，不填 0% 或 100%）");
                    Console.WriteLine("      充电位：未知");
                }
                Console.WriteLine("      命中层：L3（厂商私有协议表）");
                continue;
            }

            // ---- L4：通用识别兜底 ----
            if (PeripheralClassifier.LooksWireless(displayName))
            {
                l4Count++;
                Console.WriteLine("  ─ L4 通用识别兜底：命中（名称含无线/接收器特征）");
                Console.WriteLine("      电量：未知（没有任何可用的电量来源，不填 0% 或 100%）");
                Console.WriteLine("      命中层：L4（通用识别兜底）");
                continue;
            }

            noneCount++;
            Console.WriteLine("  ─ 命中层：未命中（没有标准 HID 电量 usage，也不在协议表里，名称也不像外设接收器）");
        }

        // ---------- 四、产品路径复核 ----------
        Console.WriteLine();
        Console.WriteLine("---- 四、产品路径复核（PeripheralBatteryService：产品自己那条路）----");
        PeripheralBatteryService.Warm();
        List<DeviceInfo> snapshot = new List<DeviceInfo>();
        for (int i = 0; i < 60 && snapshot.Count == 0; i++)
        {
            await Task.Delay(200);
            snapshot = PeripheralBatteryService.Snapshot();
        }
        if (snapshot.Count == 0)
        {
            Console.WriteLine("  （产品路径这一轮没有产出读数 —— 这不否定上面的命中报告，只说明产品这轮没读到）");
        }
        foreach (DeviceInfo d in snapshot)
        {
            string percent = d.LevelText == "未知" ? "未知" : d.Percent + "%";
            Console.WriteLine("  名称=" + d.Name + " 类型=" + d.Kind + " 电量=" + percent
                + " 档位=" + (d.LevelText ?? "-") + " 充电=" + d.Charging
                + " 观测于=" + (d.ObservedAt?.ToString("HH:mm:ss") ?? "-"));
        }

        // ---------- 汇总 ----------
        Console.WriteLine();
        Console.WriteLine("---- 汇总 ----");
        Console.WriteLine("  协议表：内置表 " + (builtinReport.Ok ? "合格" : "不合格")
            + "（错误 " + builtinReport.ErrorCount + "，警告 " + builtinReport.WarningCount + "）；用户表 "
            + (userReport.Ok ? "合格" : "不合格")
            + "（错误 " + userReport.ErrorCount + "，警告 " + userReport.WarningCount + "）");
        Console.WriteLine("  设备命中：共 " + groups.Count + " 台 —— L1 " + l1Count
            + " 台 / L3 " + l3Count + " 台 / L4 " + l4Count + " 台 / 未命中 " + noneCount + " 台");

        bool failed = builtinReport.HasErrors || userReport.HasErrors;
        Console.WriteLine(failed
            ? "  结论：表校验有错误 ⇒ 退出码 1"
            : "  结论：表校验通过 ⇒ 退出码 0");
        return failed ? 1 : 0;
    }

    /// <summary>打印一张表的校验结果（结论带字段与取值，用户能照着改 JSON）。</summary>
    private static void PrintTableReport(PeripheralProtocolTableReport report)
    {
        Console.WriteLine("[" + report.Source + "]  错误 " + report.ErrorCount
            + " / 警告 " + report.WarningCount + " / 信息 " + report.InfoCount
            + "  → " + (report.Ok ? "合格" : "不合格"));
        foreach (PeripheralProtocolIssue issue in report.Issues)
        {
            string tag = issue.Severity switch
            {
                PeripheralProtocolSeverity.Error => "错误",
                PeripheralProtocolSeverity.Warning => "警告",
                _ => "信息",
            };
            Console.WriteLine("   " + tag + "  " + issue.Describe());
        }
    }

    /// <summary>这条表项来自用户表还是内置表。</summary>
    private static string SourceOf(PeripheralProtocol protocol, List<PeripheralProtocol> user)
    {
        foreach (PeripheralProtocol u in user)
            if (string.Equals(u.Key, protocol.Key, StringComparison.OrdinalIgnoreCase)) return "用户表";
        return "内置表";
    }

    /// <summary>
    /// L1：按标准电量 usage 读（与产品同一套规则：Feature 报告主动读 / Input 报告等上报）。
    /// </summary>
    private static StdHit? ReadStandardSelfTest(HidInterface member)
    {
        foreach ((ushort page, ushort usage) in StandardHidBattery.PercentUsages)
        {
            HidUsageRange? caps = StandardHidBattery.FindUsage(member, page, usage);
            if (caps is null) continue;
            bool inFeature = member.FeatureUsages.Any(u => u.Covers(page, usage));
            if (inFeature)
            {
                try
                {
                    using var channel = new HidChannel(member, queryOnly: true);
                    byte[]? report = channel.GetFeature(caps.ReportId, member.FeatureReportLength);
                    if (report is null) continue;
                    if (!channel.TryReadUsageValue(HidNative.HIDP_FEATURE, page, usage, report, out int raw))
                        continue;
                    int? percent = StandardHidBattery.ToPercent(raw, caps.LogicalMin, caps.LogicalMax);
                    if (percent is null) continue;
                    return new StdHit(new PeripheralReading(percent.Value, false),
                        "Feature 报告 " + caps.ReportId + " 的 usage "
                            + page.ToString("X4") + ":" + usage.ToString("X4")
                            + " = " + raw + "（逻辑范围 " + caps.LogicalMin + ".." + caps.LogicalMax + "）");
                }
                catch
                {
                    // 打不开 / 读失败：换下一个集合，不影响其它设备
                }
            }
            else
            {
                try
                {
                    using var channel = new HidChannel(member);
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        byte[]? report = channel.Read(250);
                        if (report is null) return null;
                        if (!channel.TryReadUsageValue(HidNative.HIDP_INPUT, page, usage, report, out int raw))
                            continue;
                        int? percent = StandardHidBattery.ToPercent(raw, caps.LogicalMin, caps.LogicalMax);
                        if (percent is null) continue;
                        return new StdHit(new PeripheralReading(percent.Value, false),
                            "Input 报告的 usage " + page.ToString("X4") + ":" + usage.ToString("X4")
                                + " = " + raw + "（逻辑范围 " + caps.LogicalMin + ".." + caps.LogicalMax + "）");
                    }
                }
                catch
                {
                    // 同上
                }
            }
        }
        return null;
    }

    /// <summary>
    /// L3：按协议表读厂商私有协议（直读 Feature 报告 / 发命令收回包，与产品同一套解析器）。
    /// </summary>
    private static VendorHit? ReadVendorSelfTest(HidInterface member, PeripheralProtocol protocol)
    {
        try
        {
            if (protocol.FeatureReportId >= 0)
            {
                using var channel = new HidChannel(member, queryOnly: true);
                byte[]? report = channel.GetFeature((byte)protocol.FeatureReportId, member.FeatureReportLength);
                if (report is null) return null;
                if (!PeripheralProtocolReader.TryParse(protocol, report, out PeripheralReading? direct)
                    || direct is null) return null;
                int raw = protocol.ValueOffset >= 0 && protocol.ValueOffset < report.Length
                    ? report[protocol.ValueOffset] & protocol.ValueMask
                    : -1;
                return new VendorHit(direct,
                    "Feature 报告 " + protocol.FeatureReportId + " 的偏移 " + protocol.ValueOffset
                        + " 处读到 0x" + (raw < 0 ? "??" : raw.ToString("X2"))
                        + "（取掩码 0x" + protocol.ValueMask.ToString("X2") + " 后 = "
                        + (raw < 0 ? "??" : raw.ToString(CultureInfo.InvariantCulture)) + "）");
            }

            using var io = new HidChannel(member);
            foreach (byte[] pre in protocol.PreCommands) SendSelfTest(io, protocol.Transport, pre);
            if (protocol.Command.Length == 0) return null;
            SendSelfTest(io, protocol.Transport, protocol.Command);
            for (int i = 0; i < 4; i++)
            {
                byte[]? response = io.Read(300);
                if (response is null) break;
                if (PeripheralProtocolReader.TryParse(protocol, response, out PeripheralReading? reading)
                    && reading is not null)
                {
                    int raw = protocol.ValueOffset >= 0 && protocol.ValueOffset < response.Length
                        ? response[protocol.ValueOffset] & protocol.ValueMask
                        : -1;
                    return new VendorHit(reading,
                        "命令回包的偏移 " + protocol.ValueOffset + " 处读到 0x"
                            + (raw < 0 ? "??" : raw.ToString("X2")) + "（取掩码 0x"
                            + protocol.ValueMask.ToString("X2") + " 后 = "
                            + (raw < 0 ? "??" : raw.ToString(CultureInfo.InvariantCulture)) + "）");
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>按表项声明的传输方式发一帧命令。</summary>
    private static void SendSelfTest(HidChannel channel, PeripheralTransport transport, byte[] frame)
    {
        switch (transport)
        {
            case PeripheralTransport.Interrupt: channel.WriteInterrupt(frame); break;
            case PeripheralTransport.Feature: channel.WriteFeature(frame); break;
            default: channel.WriteControl(frame); break;
        }
    }

    private static void Usage()
    {
        Console.WriteLine("用法：");
        Console.WriteLine("  hid-probe scan");
        Console.WriteLine("  hid-probe read <序号> <毫秒>");
        Console.WriteLine("  hid-probe probe <序号> <hex字节，空格分隔>");
        Console.WriteLine("  hid-probe feat <序号>");
        Console.WriteLine("  hid-probe usage <序号>");
        Console.WriteLine("  hid-probe watch <序号> <毫秒>");
        Console.WriteLine("  hid-probe ble");
        Console.WriteLine("  hid-probe off <序号>");
        Console.WriteLine("  hid-probe scanfeat <序号>");
        Console.WriteLine("  hid-probe e2e");
        Console.WriteLine("  hid-probe --selftest");
        Console.WriteLine("  hid-probe sweep <序号>");
    }

    private static void Scan(List<HidInterface> all)
    {
        Console.WriteLine($"共 {all.Count} 个 HID 集合");
        for (int i = 0; i < all.Count; i++)
        {
            var m = all[i];
            Console.WriteLine($"[{i}] {m.Path}");
            Console.WriteLine($"     vid={m.Vid:X4} pid={m.Pid:X4} usage={m.UsagePage:X4}:{m.Usage:X4} "
                + $"in={m.InputReportLength} out={m.OutputReportLength} feat={m.FeatureReportLength}");
            Console.WriteLine($"     product='{m.Product}' mfr='{m.Manufacturer}' desc='{m.DeviceDesc}' serial='{m.Serial}'");
            Console.WriteLine($"     instance={m.InstanceId}");
            if (m.FeatureUsages.Count > 0)
                Console.WriteLine("     featureCaps: " + string.Join(", ", m.FeatureUsages.Select(Caps)));
            if (m.InputUsages.Count > 0)
                Console.WriteLine("     inputCaps:   " + string.Join(", ", m.InputUsages.Select(Caps)));
        }
    }

    private static string Caps(HidUsageRange u) =>
        $"[page={u.UsagePage:X4} usage={u.UsageMin:X4}-{(u.IsRange ? u.UsageMax.ToString("X4") : "-")} "
        + $"rid={u.ReportId} bits=? logical={u.LogicalMin}..{u.LogicalMax}]";

    private static void Read(List<HidInterface> all, int index, int millis)
    {
        if (index < 0 || index >= all.Count) { Console.WriteLine("序号越界"); return; }
        var m = all[index];
        Console.WriteLine($"读 {m.Path}（usage={m.UsagePage:X4}:{m.Usage:X4}）最多 {millis} ms");
        try
        {
            using var ch = new HidChannel(m);
            int deadline = Environment.TickCount + millis;
            int got = 0;
            while (Environment.TickCount < deadline)
            {
                byte[]? r = ch.Read(Math.Min(300, Math.Max(1, deadline - Environment.TickCount)));
                if (r is null) continue;
                got++;
                Console.WriteLine($"  [{got}] {Hex(r)}");
                if (got >= 64) break;
            }
            Console.WriteLine($"共读到 {got} 帧");
        }
        catch (Exception ex)
        {
            Console.WriteLine("打开失败：" + ex.Message);
        }
    }

    /// <summary>把集合里出现过的每个报告 ID 的 Feature 报告都读一遍（只读，不发命令）。</summary>
    private static void Feature(List<HidInterface> all, int index)
    {
        if (index < 0 || index >= all.Count) { Console.WriteLine("序号越界"); return; }
        var m = all[index];
        Console.WriteLine($"集合 {m.Key} feat={m.FeatureReportLength} 报告ID={string.Join(",", m.ReportIds)}");
        try
        {
            using var ch = new HidChannel(m);
            foreach (byte rid in m.ReportIds)
            {
                byte[]? f = ch.GetFeature(rid, m.FeatureReportLength);
                Console.WriteLine($"  feat rid={rid}: {(f is null ? "(读不到)" : Hex(f))}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("打开失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 用 HID 描述符解析 Feature 报告里的每个 usage（HidP_GetUsageValue）——
    /// 这是"设备自己认定的字段"，比人工猜字节偏移可靠。
    /// </summary>
    private static void UsageDump(List<HidInterface> all, int index)
    {
        if (index < 0 || index >= all.Count) { Console.WriteLine("序号越界"); return; }
        var m = all[index];
        Console.WriteLine($"集合 {m.Key} feat={m.FeatureReportLength}");
        try
        {
            using var ch = new HidChannel(m);
            foreach (byte rid in m.ReportIds)
            {
                byte[]? buf = ch.GetFeature(rid, m.FeatureReportLength);
                if (buf is null) { Console.WriteLine($"  rid={rid}: 读不到"); continue; }
                Console.WriteLine($"  rid={rid}: {Hex(buf)}");
                foreach (var u in m.FeatureUsages.Where(u => u.ReportId == rid))
                {
                    bool ok = ch.TryReadUsageValue(u.UsagePage, u.UsageMin, buf, out int v);
                    Console.WriteLine($"      usage {u.UsagePage:X4}:{u.UsageMin:X4} = {(ok ? v.ToString() : "解析失败")}"
                        + $"  (logical {u.LogicalMin}..{u.LogicalMax})");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("打开失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 反复读某个集合的 Feature 报告，只在内容变化时打印：
    /// 变的是计数器/状态，不变的是静态字段（电量、型号这类）。
    /// </summary>
    private static void Watch(List<HidInterface> all, int index, int millis)
    {
        if (index < 0 || index >= all.Count) { Console.WriteLine("序号越界"); return; }
        var m = all[index];
        Console.WriteLine($"监视 {m.Key} feat={m.FeatureReportLength} 共 {millis} ms");
        var last = new Dictionary<byte, string>();
        int deadline = Environment.TickCount + millis;
        try
        {
            using var ch = new HidChannel(m);
            while (Environment.TickCount < deadline)
            {
                foreach (byte rid in m.ReportIds)
                {
                    byte[]? f = ch.GetFeature(rid, m.FeatureReportLength);
                    if (f is null) continue;
                    string h = Hex(f);
                    if (!last.TryGetValue(rid, out string? prev) || prev != h)
                    {
                        Console.WriteLine($"  rid={rid} @{Environment.TickCount % 100000}: {h}");
                        last[rid] = h;
                    }
                }
                Thread.Sleep(150);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("打开失败：" + ex.Message);
        }
    }

    /// <summary>把特征报告按偏移逐字节列出（首字节是报告 ID，从偏移 0 开始标注）。</summary>
    private static void Offsets(List<HidInterface> all, int index)
    {
        if (index < 0 || index >= all.Count) { Console.WriteLine("序号越界"); return; }
        var m = all[index];
        try
        {
            using var ch = new HidChannel(m);
            foreach (byte rid in m.ReportIds)
            {
                byte[]? f = ch.GetFeature(rid, m.FeatureReportLength);
                if (f is null) { Console.WriteLine($"  rid={rid}: 读不到"); continue; }
                Console.WriteLine($"  rid={rid}（长度 {f.Length}）:");
                for (int i = 0; i < Math.Min(f.Length, 24); i++)
                    Console.WriteLine($"    off[{i,2}] = 0x{f[i]:X2} ({f[i]})");
            }
        }
        catch (Exception ex) { Console.WriteLine("打开失败：" + ex.Message); }
    }

    /// <summary>把 0..255 的每个 Feature 报告 ID 都读一遍，只打印非空/存在的内容。</summary>
    private static void ScanFeatureIds(List<HidInterface> all, int index)
    {
        if (index < 0 || index >= all.Count) { Console.WriteLine("序号越界"); return; }
        var m = all[index];
        Console.WriteLine($"扫描 {m.Key} 的全部 Feature 报告 ID（长度 {m.FeatureReportLength}）");
        try
        {
            using var ch = new HidChannel(m);
            for (int rid = 0; rid <= 255; rid++)
            {
                byte[]? f = ch.GetFeature((byte)rid, m.FeatureReportLength);
                if (f is null) continue;
                bool any = f.Skip(1).Any(b => b != 0);
                if (!any) continue;
                Console.WriteLine($"  rid={rid}: {Hex(f)}");
            }
        }
        catch (Exception ex) { Console.WriteLine("打开失败：" + ex.Message); }
    }

    private static void Probe(List<HidInterface> all, int index, string hex)
    {
        if (index < 0 || index >= all.Count) { Console.WriteLine("序号越界"); return; }
        var m = all[index];
        byte[] frame = ParseHex(hex);
        Console.WriteLine($"集合 {m.Key} out={m.OutputReportLength} feat={m.FeatureReportLength}");
        try
        {
            using var ch = new HidChannel(m);
            foreach (string transport in new[] { "control", "interrupt", "feature" })
            {
                bool sent = transport switch
                {
                    "control" => ch.WriteControl(frame),
                    "interrupt" => ch.WriteInterrupt(frame),
                    _ => ch.WriteFeature(frame),
                };
                Console.WriteLine($"  {transport}: sent={sent}");
                for (int i = 0; i < 4; i++)
                {
                    byte[]? r = ch.Read(200);
                    if (r is null) break;
                    Console.WriteLine($"    <- {Hex(r)}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("打开失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 候选帧批量试探：只发"读状态"类的查询帧，把所有回包打印出来供人工判读。
    /// 绝不写配置、绝不升级固件。
    /// </summary>
    private static void Sweep(List<HidInterface> all, int index)
    {
        if (index < 0 || index >= all.Count) { Console.WriteLine("序号越界"); return; }
        var m = all[index];
        Console.WriteLine($"集合 {m.Key} out={m.OutputReportLength} feat={m.FeatureReportLength}");

        var candidates = new List<byte[]>();
        // 常见"读状态"命令：单字节 0x01..0x0C，报告 ID 用集合里出现过的第一个报告 ID
        byte[] reportIds = m.ReportIds.Count > 0 ? new[] { m.ReportIds[0] } : new byte[] { 0 };
        foreach (byte rid in reportIds)
        {
            for (byte cmd = 1; cmd <= 0x0C; cmd++)
            {
                var f = new byte[8];
                f[0] = rid;
                f[1] = cmd;
                candidates.Add(f);
            }
        }
        // 再试一些"报告 ID 在第 0 字节、命令在第 1 字节、参数全 0"的整包帧
        for (byte cmd = 1; cmd <= 0x0C; cmd++)
        {
            var f = new byte[8];
            f[0] = cmd;
            candidates.Add(f);
        }

        try
        {
            using var ch = new HidChannel(m);
            foreach (byte[] frame in candidates)
            {
                foreach (string transport in new[] { "control", "interrupt", "feature" })
                {
                    bool sent = transport switch
                    {
                        "control" => ch.WriteControl(frame),
                        "interrupt" => ch.WriteInterrupt(frame),
                        _ => ch.WriteFeature(frame),
                    };
                    if (!sent) continue;
                    for (int i = 0; i < 3; i++)
                    {
                        byte[]? r = ch.Read(150);
                        if (r is null) break;
                        Console.WriteLine($"{transport} {Hex(frame)} -> {Hex(r)}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("打开失败：" + ex.Message);
        }
    }

    private static byte[] ParseHex(string text)
    {
        var parts = text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var bytes = new List<byte>();
        foreach (string p in parts)
            bytes.Add(byte.Parse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return bytes.ToArray();
    }

    private static string Hex(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 3);
        foreach (byte b in data) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
        return sb.ToString().TrimEnd();
    }
}
