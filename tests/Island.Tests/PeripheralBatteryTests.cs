using System;
using System.Collections.Generic;
using Island.App.Services.Peripheral;

namespace Island.Tests;

/// <summary>
/// Windows 外设电量（2.4G 接收器 / 无线键鼠耳机）的纯逻辑用例。
///
/// 被测的 5 个文件（PeripheralReading / PeripheralProtocol / StandardHidBattery /
/// PeripheralClassifier / HidModels）没有 Win32 / WinRT 依赖，可以链进零 NuGet 测试工程；
/// 真正发命令的 HidBus / HidChannel 不在此列（要真设备才能验）。
///
/// 这里守的是「不伪装」底线：协议解析任何一层校验不过都必须返回 false + null，
/// 绝不能返回一个 0% 的假读数充数；识别兜底猜不出类型就 "other"，不硬安成别的类型。
/// </summary>
internal static class PeripheralBatteryTests
{
    /// <summary>测试用协议骨架：电量在偏移 1（首字节是报告 ID），取值范围 0..100。</summary>
    private static PeripheralProtocol Proto(string key = "test-proto") => new PeripheralProtocol
    {
        Key = key,
        ValueOffset = 1,
        ValueMask = 0xFF,
        ValueMin = 0,
        ValueMax = 100,
    };

    public static void Run(Action<string, bool> check)
    {
        // ================= 一、协议解析：三层校验（回显 → 范围 → 充电位）=================

        // 1. 正常解析：偏移处是合法百分比
        var okProto = Proto("ok-proto");
        byte[] okResp = { 0xC1, 58, 0x00 };
        bool okParsed = PeripheralProtocolReader.TryParse(okProto, okResp, out PeripheralReading? okRead);
        check("协议解析：偏移处是合法百分比 → 解析成功且读数正确（58%）",
            okParsed && okRead is { Percent: 58, Charging: false, MilliVolts: null });

        // 2. 回显不匹配：协议声明的回显字节不在回包里
        var echoProto = Proto("echo-proto") with { Echo = new byte[] { 0x05, 0x02 } };
        bool echoParsed = PeripheralProtocolReader.TryParse(echoProto, new byte[] { 0xC1, 58 }, out PeripheralReading? echoRead);
        check("协议解析：回显字节不在回包里 → 失败",
            !echoParsed && echoRead is null);

        // 3. 电量越界：原始值超出 [ValueMin, ValueMax]
        bool rangeParsed = PeripheralProtocolReader.TryParse(Proto(), new byte[] { 0xC1, 150 }, out PeripheralReading? rangeRead);
        check("协议解析：电量原始值超出声明范围（150 > 100）→ 失败",
            !rangeParsed && rangeRead is null);

        // 4. 电量字段偏移越界：ValueOffset ≥ 回包长度
        var oobProto = Proto() with { ValueOffset = 8 };
        bool oobParsed = PeripheralProtocolReader.TryParse(oobProto, new byte[] { 0xC1, 58, 0x00 }, out PeripheralReading? oobRead);
        check("协议解析：电量字段偏移越界（8 ≥ 回包长度）→ 失败",
            !oobParsed && oobRead is null);

        // 5. 充电位取到非 0/1（掩码较宽时）→ 失败，不猜充电状态
        var chargeProto = Proto() with { ChargingOffset = 2, ChargingMask = 0x03 };
        bool chargeParsed = PeripheralProtocolReader.TryParse(chargeProto, new byte[] { 0xC1, 58, 0x02 }, out PeripheralReading? chargeRead);
        check("协议解析：充电位不是明确的 0/1（掩码较宽取到 2）→ 失败",
            !chargeParsed && chargeRead is null);

        // 6. ChargingIsOne = false：充电语义反过来（0 = 在充电）
        var invProto = Proto() with { ChargingOffset = 2, ChargingMask = 0x01, ChargingIsOne = false };
        bool invZero = PeripheralProtocolReader.TryParse(invProto, new byte[] { 0xC1, 58, 0x00 }, out PeripheralReading? invZeroRead);
        bool invOne = PeripheralProtocolReader.TryParse(invProto, new byte[] { 0xC1, 58, 0x01 }, out PeripheralReading? invOneRead);
        check("协议解析：ChargingIsOne=false 时 0 表示在充电、1 表示不在充电",
            invZero && invZeroRead is { Charging: true } && invOne && invOneRead is { Charging: false });

        // 7. 电压：大端 16 位 + MilliVoltsScale；电压字段越界 → 失败
        var mvProto = Proto() with { MilliVoltsOffset = 2, MilliVoltsMask = 0xFFFF };
        bool mvOk = PeripheralProtocolReader.TryParse(mvProto, new byte[] { 0xC1, 58, 0x0F, 0xA0 }, out PeripheralReading? mvRead);
        check("协议解析：电压字段按大端 16 位读出（0x0FA0 = 4000mV）",
            mvOk && mvRead is { MilliVolts: 4000 });

        var mvScaled = mvProto with { MilliVoltsScale = 2 };
        bool mvScaledOk = PeripheralProtocolReader.TryParse(mvScaled, new byte[] { 0xC1, 58, 0x0F, 0xA0 }, out PeripheralReading? mvScaledRead);
        check("协议解析：电压乘 MilliVoltsScale（×2 = 8000mV）",
            mvScaledOk && mvScaledRead is { MilliVolts: 8000 });

        var mvOob = mvProto with { MilliVoltsOffset = 3 };
        bool mvOobOk = PeripheralProtocolReader.TryParse(mvOob, new byte[] { 0xC1, 58, 0x0F, 0xA0 }, out PeripheralReading? mvOobRead);
        check("协议解析：电压字段偏移越界（末尾少一个字节）→ 失败",
            !mvOobOk && mvOobRead is null);

        // 8. ValueOffset = -1：这条协议不报电量 → 失败
        var noValue = Proto() with { ValueOffset = -1 };
        bool noValueOk = PeripheralProtocolReader.TryParse(noValue, new byte[] { 0xC1, 58, 0x00 }, out PeripheralReading? noValueRead);
        check("协议解析：ValueOffset=-1（协议不报电量）→ 失败",
            !noValueOk && noValueRead is null);

        // ================= 二、协议表匹配 =================

        // 9. VID / PID 不匹配 → null
        check("协议表：VID/PID 不匹配 → 匹配不上（null）",
            PeripheralProtocols.Match(0x17EF, 0xF007, 0xFFCF, 0x00A0, 61) is null
            && PeripheralProtocols.Match(0x1234, 0xF006, 0xFFCF, 0x00A0, 61) is null);

        // 10. UsagePage / Usage 不匹配（表项限定了集合）→ null
        check("协议表：UsagePage/Usage 与表项不符 → 匹配不上（null）",
            PeripheralProtocols.Match(0x17EF, 0xF006, 0x0001, 0x00A0, 61) is null
            && PeripheralProtocols.Match(0x17EF, 0xF006, 0xFFCF, 0x00A1, 61) is null);

        // 11. UsagePage / Usage 为 null（不限集合）→ 任意集合都能命中
        var anyCollection = new PeripheralProtocol { Key = "any-collection", Vid = 0x2A2A, Pid = 0x2B2B, ValueOffset = 1 };
        bool anyHit = false;
        try
        {
            PeripheralProtocols.AddUserProtocols(new[] { anyCollection });
            anyHit = PeripheralProtocols.Match(0x2A2A, 0x2B2B, 0x0001, 0x0002, 4) is not null;
        }
        finally
        {
            PeripheralProtocols.ClearUserProtocols();
        }
        check("协议表：UsagePage/Usage 为 null（不限集合）→ 任意集合都命中", anyHit);

        // 12. OutputReportLength < MinReportLength → null
        check("协议表：输出报告长度不够（60 < 61）→ 匹配不上（null）",
            PeripheralProtocols.Match(0x17EF, 0xF006, 0xFFCF, 0x00A0, 60) is null);

        // 13. 命中时返回的那一条的 Key 与预期一致
        var matched = PeripheralProtocols.Match(0x17EF, 0xF006, 0xFFCF, 0x00A0, 61);
        check("协议表：命中时返回的条目 Key 正确",
            matched is not null && matched.Key == "lenovo-idea5003");

        // 14. 用户追加的条目能命中，ClearUserProtocols 后不再命中（测完恢复状态）
        var userProto = new PeripheralProtocol { Key = "user-demo", Vid = 0x3C3C, Pid = 0x4D4D, ValueOffset = 1 };
        PeripheralProtocol? userHit = null;
        try
        {
            PeripheralProtocols.AddUserProtocols(new[] { userProto });
            userHit = PeripheralProtocols.Match(0x3C3C, 0x4D4D, 0x0001, 0x0002, 8);
        }
        finally
        {
            PeripheralProtocols.ClearUserProtocols();
        }
        var afterClear = PeripheralProtocols.Match(0x3C3C, 0x4D4D, 0x0001, 0x0002, 8);
        check("协议表：用户追加条目能命中，ClearUserProtocols 后不再命中（状态已还原）",
            userHit is not null && userHit.Key == "user-demo" && afterClear is null);

        // 15. 内置联想条目：能命中，且电量偏移/量程符合表里写死的值
        var lenovo = PeripheralProtocols.Match(0x17EF, 0xF006, 0xFFCF, 0x00A0, 61);
        check("协议表：内置联想条目（0x17EF/0xF006）命中，ValueOffset=5、ValueMax=100",
            lenovo is not null && lenovo.ValueOffset == 5 && lenovo.ValueMax == 100);

        // 16. 用户表覆盖内置条目（同 Key）：纠错通道必须是真的
        //     设计文档 §8.5 承诺"偏移 5 不对就改成 12，不用改代码、不用发版" ——
        //     这条承诺靠的就是"用户表优先于内置表"。
        var overrideProto = new PeripheralProtocol
        {
            Key = "lenovo-idea5003",
            Vendor = "联想",
            Model = "Idea5003 2.4G 接收器",
            Kind = "other",
            Vid = 0x17EF,
            Pid = 0xF006,
            UsagePage = 0xFFCF,
            Usage = 0x00A0,
            MinReportLength = 61,
            FeatureReportId = 193,
            ValueOffset = 9,
        };
        PeripheralProtocol? overridden = null;
        bool userFirstInTable = false;
        try
        {
            PeripheralProtocols.AddUserProtocols(new[] { overrideProto });
            overridden = PeripheralProtocols.Match(0x17EF, 0xF006, 0xFFCF, 0x00A0, 61);
            userFirstInTable = PeripheralProtocols.All.Count > 0
                && PeripheralProtocols.All[0].Key == "lenovo-idea5003"
                && PeripheralProtocols.All[0].ValueOffset == 9;
        }
        finally
        {
            PeripheralProtocols.ClearUserProtocols();
        }
        check("协议表：同 Key 的用户条目覆盖内置条目（偏移 5 → 9 真的生效）",
            overridden is not null && overridden.ValueOffset == 9);
        check("协议表：生效表里用户条目排在前面（覆盖顺序不是摆设）", userFirstInTable);

        // 17. 清空用户表后恢复内置条目（纠错通道是可逆的）
        var restored = PeripheralProtocols.Match(0x17EF, 0xF006, 0xFFCF, 0x00A0, 61);
        check("协议表：ClearUserProtocols 后又回到内置条目（偏移回到 5）",
            restored is not null && restored.ValueOffset == 5);

        // 18. 真实工作流：从 JSON 装载的同 Key 用户条目同样覆盖内置条目
        const string OverrideJson = """
        [
          {
            "key": "lenovo-idea5003",
            "vendor": "联想",
            "model": "Idea5003 2.4G 接收器",
            "vid": 6127,
            "pid": 61446,
            "usagePage": 65487,
            "usage": 160,
            "minReportLength": 61,
            "featureReportId": 193,
            "valueOffset": 9
          }
        ]
        """;
        var overrideLoaded = TryLoad(OverrideJson, out string? overrideErr);
        PeripheralProtocol? jsonOverrideHit = null;
        try
        {
            if (overrideLoaded is { Count: 1 })
            {
                PeripheralProtocols.AddUserProtocols(overrideLoaded);
                jsonOverrideHit = PeripheralProtocols.Match(0x17EF, 0xF006, 0xFFCF, 0x00A0, 61);
            }
        }
        finally
        {
            PeripheralProtocols.ClearUserProtocols();
        }
        check("协议表：从 JSON 装载的同 Key 用户条目也覆盖内置条目（偏移 9 生效）"
            + (overrideErr is null ? "" : "（装载异常：" + overrideErr + "）"),
            overrideErr is null && jsonOverrideHit is not null && jsonOverrideHit.ValueOffset == 9);

        // 19. 用户表条目字段不全时仍回退内置条目（不能因为一条写歪的用户条目把设备弄丢）
        var partialUser = new PeripheralProtocol { Key = "lenovo-idea5003", ValueOffset = 12 };
        PeripheralProtocol? fallback = null;
        try
        {
            PeripheralProtocols.AddUserProtocols(new[] { partialUser });
            fallback = PeripheralProtocols.Match(0x17EF, 0xF006, 0xFFCF, 0x00A0, 61);
        }
        finally
        {
            PeripheralProtocols.ClearUserProtocols();
        }
        check("协议表：用户条目匹配不上时回退内置条目（设备不会被弄丢）",
            fallback is not null && fallback.ValueOffset == 5);

        // ================= 三、JSON 装载 =================

        const string DemoJson = """
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

        var loaded = TryLoad(DemoJson, out string? loadErr);
        // 装载出错时把异常原文带进用例名（失败行直接写明原因，不用再猜是哪一环炸了）
        check("JSON 装载：示例表装载成 1 条" + (loadErr is null ? "" : "（装载异常：" + loadErr + "）"),
            loadErr is null && loaded is { Count: 1 });

        var d = loaded is { Count: 1 } ? loaded[0] : null;
        check("JSON 装载：字段逐个对上（Key/Vid/Pid/UsagePage/Usage/MinReportLength）",
            d is not null && d.Key == "demo-vendor" && d.Vid == 4660 && d.Pid == 62723
            && d.UsagePage == 65292 && d.Usage == 2 && d.MinReportLength == 64);
        check("JSON 装载：Transport 用字符串名解析成 Interrupt",
            d is not null && d.Transport == PeripheralTransport.Interrupt);
        check("JSON 装载：Command / Echo / ValueOffset / ChargingOffset 对上",
            d is not null && d.Command.Length == 2 && d.Command[0] == 0x05 && d.Command[1] == 0x02
            && d.Echo.Length == 2 && d.Echo[0] == 0x05 && d.Echo[1] == 0x02
            && d.ValueOffset == 8 && d.ChargingOffset == 9
            && d.ValueMin == 0 && d.ValueMax == 100 && d.ChargingMask == 1 && d.ChargingIsOne);
        check("JSON 装载：preCommands 装载成 1 条握手命令",
            d is not null && d.PreCommands.Length == 1 && d.PreCommands[0].Length == 2
            && d.PreCommands[0][0] == 0x05 && d.PreCommands[0][1] == 0x01);
        check("JSON 装载：厂商 / 型号 / 类型字段对上",
            d is not null && d.Vendor == "示例" && d.Model == "示例接收器" && d.Kind == "mouse");

        // 17. Command 写成十六进制字符串
        var hexLoaded = TryLoad("""[ { "key": "hex-cmd", "command": "05 02", "valueOffset": 8 } ]""", out string? hexErr);
        check("JSON 装载：Command 用十六进制字符串（「05 02」）解出 0x05 0x02",
            hexErr is null && hexLoaded is { Count: 1 } && hexLoaded[0].Command.Length == 2
            && hexLoaded[0].Command[0] == 0x05 && hexLoaded[0].Command[1] == 0x02);

        // 18. Command 写成数字数组 → 同样的字节
        var arrLoaded = TryLoad("""[ { "key": "arr-cmd", "command": [ 5, 2 ], "valueOffset": 8 } ]""", out string? arrErr);
        check("JSON 装载：Command 用数字数组（[5, 2]）解出同样的字节",
            arrErr is null && arrLoaded is { Count: 1 } && arrLoaded[0].Command.Length == 2
            && arrLoaded[0].Command[0] == 0x05 && arrLoaded[0].Command[1] == 0x02);

        // Transport 用整数（0/1/2）也要能解析
        var intLoaded = TryLoad("""[ { "key": "int-transport", "transport": 1, "valueOffset": 8 } ]""", out string? intErr);
        check("JSON 装载：Transport 用整数（1）解析成 Interrupt",
            intErr is null && intLoaded is { Count: 1 }
            && intLoaded[0].Transport == PeripheralTransport.Interrupt);

        // 19. 非法 JSON → 抛异常（不让整个测试进程崩掉）
        bool badJsonThrew = false;
        try
        {
            PeripheralProtocols.LoadFromJson("{ 这不是 JSON ");
        }
        catch (Exception)
        {
            badJsonThrew = true;
        }
        check("JSON 装载：非法 JSON → 抛异常而不是静默返回空表", badJsonThrew);

        // 缺必填字段（Key）同样抛异常：required 属性缺失不静默吞掉
        bool missingKeyThrew = false;
        try
        {
            PeripheralProtocols.LoadFromJson("""[ { "vid": 1, "pid": 2 } ]""");
        }
        catch (Exception)
        {
            missingKeyThrew = true;
        }
        check("JSON 装载：缺少必填字段 key → 抛异常", missingKeyThrew);

        // ================= 四、标准 HID 电量换算 =================

        check("标准 HID 电量：ToPercent(58, 0, 100) = 58", StandardHidBattery.ToPercent(58, 0, 100) == 58);
        check("标准 HID 电量：一般换算 ToPercent(50, 0, 200) = 25", StandardHidBattery.ToPercent(50, 0, 200) == 25);
        check("标准 HID 电量：原始值超出逻辑范围 → null（不猜 0%）",
            StandardHidBattery.ToPercent(201, 0, 200) is null && StandardHidBattery.ToPercent(-1, 0, 100) is null);
        check("标准 HID 电量：逻辑范围非法（max ≤ min）→ null",
            StandardHidBattery.ToPercent(50, 100, 100) is null && StandardHidBattery.ToPercent(50, 200, 100) is null);

        // 24. 百分比 usage 白名单：只认电池百分比，不认时间 / 位域
        bool hasGdc = false, hasBatteryPct = false, hasTime = false, hasStatus = false;
        foreach (var (page, usage) in StandardHidBattery.PercentUsages)
        {
            if (page == 0x0006 && usage == 0x0020) hasGdc = true;
            if (page == 0x0085 && usage == 0x0065) hasBatteryPct = true;
            if (page == 0x0085 && usage == 0x0066) hasTime = true;
            if (page == 0x0085 && usage == 0x0001) hasStatus = true;
        }
        check("标准 HID 电量：白名单含 (0x0006,0x0020) 与 (0x0085,0x0065)，不含时间(0x0066)/位域(0x0001)",
            hasGdc && hasBatteryPct && !hasTime && !hasStatus);

        // 25. FindUsage：在能力表里找覆盖目标 usage 的条目
        var iface = new HidInterface
        {
            Vid = 0x17EF,
            Pid = 0xF006,
            UsagePage = 0x0001,
            Usage = 0x0006,
            InputReportLength = 64,
            OutputReportLength = 64,
        };
        iface.FeatureUsages.Add(new HidUsageRange(0x0006, 0x0020, 0x0020, 0x01, false, 0, 100));
        iface.InputUsages.Add(new HidUsageRange(0x0085, 0x0060, 0x006A, 0x02, true, 0, 100));
        var featureHit = StandardHidBattery.FindUsage(iface, 0x0006, 0x0020);
        var inputHit = StandardHidBattery.FindUsage(iface, 0x0085, 0x0065);
        var missHit = StandardHidBattery.FindUsage(iface, 0x0085, 0x0001);
        check("标准 HID 电量：FindUsage 能在 Feature/Input 能力表里找到覆盖目标 usage 的条目",
            featureHit is not null && featureHit.LogicalMin == 0 && featureHit.LogicalMax == 100
            && inputHit is not null && inputHit.ReportId == 0x02 && inputHit.IsRange);
        check("标准 HID 电量：FindUsage 找不到覆盖范围的 usage → null", missHit is null);

        // ================= 五、通用识别兜底 =================

        check("识别兜底：含 2.4G / Wireless / Receiver / Dongle / 无线 / 接收器 的文本判为无线",
            PeripheralClassifier.LooksWireless("2.4G Receiver")
            && PeripheralClassifier.LooksWireless("Wireless Mouse")
            && PeripheralClassifier.LooksWireless("USB Receiver")
            && PeripheralClassifier.LooksWireless("Wireless Adapter")
            && PeripheralClassifier.LooksWireless("无线键鼠接收器"));
        check("识别兜底：无关文本 / null / 空串判为非无线",
            !PeripheralClassifier.LooksWireless("USB Keyboard")
            && !PeripheralClassifier.LooksWireless("HID-compliant mouse")
            && !PeripheralClassifier.LooksWireless(null)
            && !PeripheralClassifier.LooksWireless("")
            && !PeripheralClassifier.LooksWireless("   "));

        check("识别兜底：名称里的关键词决定类型（鼠标/键盘/耳机/手柄）",
            PeripheralClassifier.KindOf("Wireless Mouse", "", 0x0001, 0x0002) == "mouse"
            && PeripheralClassifier.KindOf("", "USB Keyboard", 0x0001, 0x0006) == "keyboard"
            && PeripheralClassifier.KindOf("BT Headset", "", 0x0001, 0x0002) == "headset"
            && PeripheralClassifier.KindOf("无线手柄", "", 0x0001, 0x0002) == "gamepad");
        check("识别兜底：认不出名称时按顶层 usage 兜（0x0001:0x0002=鼠标，0x0001:0x0006=键盘）",
            PeripheralClassifier.KindOf("", "", 0x0001, 0x0002) == "mouse"
            && PeripheralClassifier.KindOf("", "", 0x0001, 0x0006) == "keyboard");
        check("识别兜底：认不出的落「other」，不硬安成别的类型",
            PeripheralClassifier.KindOf("", "", 0x0001, 0x0099) == "other"
            && PeripheralClassifier.KindOf("", "", 0xFF00, 0x0001) == "other");

        check("识别兜底：DisplayName 优先取 Product，其次 DeviceDesc，都没有则「HID 设备」",
            PeripheralClassifier.DisplayName(new[] { MakeIface(product: "Product A"), MakeIface(product: "Product B") }) == "Product A"
            && PeripheralClassifier.DisplayName(new[] { MakeIface(product: ""), MakeIface(desc: "Device Desc") }) == "Device Desc"
            && PeripheralClassifier.DisplayName(new[] { MakeIface() }) == "HID 设备");

        // ================= 六、「不伪装」原则的显式断言 =================

        // 所有失败路径都必须给出 null 读数，绝不返回 0% 的假读数
        var brokenCases = new (PeripheralProtocol Proto, byte[] Resp)[]
        {
            (Proto() with { Echo = new byte[] { 0x05, 0x02 } }, new byte[] { 0xC1, 58, 0x02, 0x00 }),   // 回显不匹配
            (Proto(), new byte[] { 0xC1, 150, 0x02, 0x00 }),                                           // 电量越界（150）
            (Proto() with { ValueOffset = 8 }, new byte[] { 0xC1, 58, 0x02, 0x00 }),                   // 偏移越界
            (Proto() with { ChargingOffset = 2, ChargingMask = 0x03 }, new byte[] { 0xC1, 58, 0x02, 0x00 }), // 充电位非 0/1
            (Proto() with { MilliVoltsOffset = 3 }, new byte[] { 0xC1, 58, 0x02, 0x00 }),               // 电压字段越界
            (Proto() with { ValueOffset = -1 }, new byte[] { 0xC1, 58, 0x02, 0x00 }),                   // 协议不报电量
        };
        bool allNull = true;
        foreach (var (bp, resp) in brokenCases)
        {
            bool parsed = PeripheralProtocolReader.TryParse(bp, resp, out PeripheralReading? br);
            if (parsed || br is not null) allNull = false;
        }
        check("不伪装：所有失败路径都返回 false 且读数为 null（没有 0% 的假读数）", allNull);

        // ================= 七、此前没覆盖到的分支 =================

        // 20. 回显命中 → 解析成功（ContainsSequence 的命中分支）
        var echoHitProto = Proto("echo-hit") with { Echo = new byte[] { 0x02, 0x03 } };
        bool echoHit = PeripheralProtocolReader.TryParse(echoHitProto,
            new byte[] { 0xC1, 58, 0x02, 0x03 }, out PeripheralReading? echoHitRead);
        check("协议解析：回显字节命中 → 解析成功（58%）",
            echoHit && echoHitRead is { Percent: 58 });

        // 21. 回显比回包还长 → 失败（ContainsSequence 的"needle 过长"分支）
        var longEchoProto = Proto("long-echo") with { Echo = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 } };
        bool longEchoOk = PeripheralProtocolReader.TryParse(longEchoProto,
            new byte[] { 0xC1, 58 }, out PeripheralReading? longEchoRead);
        check("协议解析：回显比回包还长 → 失败", !longEchoOk && longEchoRead is null);

        // 22. 空回包 → 失败（绝不能返回 0% 假读数）
        bool emptyOk = PeripheralProtocolReader.TryParse(Proto(), Array.Empty<byte>(),
            out PeripheralReading? emptyRead);
        check("协议解析：空回包 → 失败（没有 0% 的假读数）", !emptyOk && emptyRead is null);

        // 23. ValueMask 取位后再比范围
        var maskProto = Proto("mask-proto") with { ValueMask = 0x0F };
        bool maskOk = PeripheralProtocolReader.TryParse(maskProto, new byte[] { 0xC1, 0x5A },
            out PeripheralReading? maskRead);
        check("协议解析：电量按 ValueMask 取位（0x5A & 0x0F = 10 → 10%）",
            maskOk && maskRead is { Percent: 10 });

        // 24. 取位后落到范围外 → 失败（掩码不是"随便取个位就报"）
        var maskLowProto = Proto("mask-low") with { ValueMask = 0x01, ValueMin = 50, ValueMax = 100 };
        bool maskLowOk = PeripheralProtocolReader.TryParse(maskLowProto, new byte[] { 0xC1, 0x01 },
            out PeripheralReading? maskLowRead);
        check("协议解析：取位后落在范围外（掩码后为 1 < 50）→ 失败", !maskLowOk && maskLowRead is null);

        // 25. 量程非法（ValueMax ≤ ValueMin）→ 失败，不换算百分比
        var badRangeProto = Proto("bad-range") with { ValueMin = 100, ValueMax = 100 };
        bool badRangeOk = PeripheralProtocolReader.TryParse(badRangeProto, new byte[] { 0xC1, 58 },
            out PeripheralReading? badRangeRead);
        var invertedRangeProto = Proto("inverted-range") with { ValueMin = 100, ValueMax = 0 };
        bool invertedRangeOk = PeripheralProtocolReader.TryParse(invertedRangeProto,
            new byte[] { 0xC1, 58 }, out PeripheralReading? invertedRangeRead);
        check("协议解析：ValueMax ≤ ValueMin（量程非法）→ 失败，不换算百分比",
            !badRangeOk && badRangeRead is null && !invertedRangeOk && invertedRangeRead is null);

        // 26. 充电位偏移越界 → 失败（不猜充电状态）
        var chargeOobProto = Proto("charge-oob") with { ChargingOffset = 9 };
        bool chargeOobOk = PeripheralProtocolReader.TryParse(chargeOobProto,
            new byte[] { 0xC1, 58, 0x00 }, out PeripheralReading? chargeOobRead);
        check("协议解析：充电位偏移越界 → 失败（不猜充电状态）",
            !chargeOobOk && chargeOobRead is null);

        // 27. 电压字段偏移为负 → 失败（不是"没有电压"就跳过，而是判失败）
        var mvNegProto = Proto("mv-neg") with { MilliVoltsOffset = -1 };
        bool mvNegOk = PeripheralProtocolReader.TryParse(mvNegProto, new byte[] { 0xC1, 58, 0x0F, 0xA0 },
            out PeripheralReading? mvNegRead);
        check("协议解析：电压字段偏移为负 → 失败", !mvNegOk && mvNegRead is null);

        // 28. 能力表判定：HidP_GetValueCaps 返回的是 NTSTATUS（实测 0x00110000 / 0xC0110004）
        check("能力表判定：只有 0x00110000 算成功，0xC0110004 是失败",
            HidValueCapsGuard.IsSuccess(0x00110000)
            && !HidValueCapsGuard.IsSuccess(unchecked((int)0xC0110004)));

        // 29. 能力表判定：失败且驱动没回写 length（仍是 capacity）→ 一条都不认
        //     （否则会把 AllocHGlobal 里未清零的内存当 caps，可能算出假百分比）
        check("能力表判定：状态码失败且 length 未回写 → 解析 0 条（不解析未初始化内存）",
            HidValueCapsGuard.UsableLength(unchecked((int)0xC0110004), 8, 8) == 0);

        // 30. 能力表判定：条数为 0 或超出容量 → 一条都不认；条数正常 → 按条数解析
        check("能力表判定：条数为 0 / 超出容量 → 解析 0 条；条数在容量内 → 按条数解析",
            HidValueCapsGuard.UsableLength(0x00110000, 0, 8) == 0
            && HidValueCapsGuard.UsableLength(0x00110000, 9, 8) == 0
            && HidValueCapsGuard.UsableLength(0x00110000, 3, 8) == 3);
    }

    /// <summary>
    /// 装载 JSON：失败只记错误不抛，避免一条坏数据把后面的用例全跳过
    /// （harness 的原则是"失败要继续跑"，不是"失败就崩"）。
    /// </summary>
    private static List<PeripheralProtocol>? TryLoad(string json, out string? error)
    {
        error = null;
        try { return PeripheralProtocols.LoadFromJson(json); }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>造一个只带名字的集合实例（显示名相关用例用）。</summary>
    private static HidInterface MakeIface(string product = "", string desc = "") =>
        new HidInterface { Product = product, DeviceDesc = desc };
}
