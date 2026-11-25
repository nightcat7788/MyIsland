using System.Globalization;
using System.Text;
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
