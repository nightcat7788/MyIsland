using System;
using System.Collections.Generic;
using Island.App.Services.Peripheral;

namespace Island.Tests;

/// <summary>
/// 外设协议表**自检层**（PeripheralProtocolTable）的纯逻辑用例。
///
/// 守的是两件事：
///   ① 表的合法性必须能被机器判出来，而且结论要指出**哪个字段、哪个取值**不合法
///      —— 协议表是用户手填的 JSON，结论不定位就没法照着改；
///   ② 覆盖规则必须是明的：用户条目同 key 顶掉内置条目；整条替换不是字段级合并
///      —— 用户只写 \"key + valueOffset\" 想改内置偏移，是**不会生效**的，必须说破。
///
/// 同样守\"不伪装\"底线：掩码为 0、量程非法、缩放为 0 这类\"照着读会得到假数据\"的写法，
/// 必须是错误级结论，而不是放过。
/// </summary>
internal static class PeripheralProtocolTableTests
{
    /// <summary>一条合法的表项（直读 Feature 报告，电量在偏移 5）。</summary>
    private static PeripheralProtocol Good(string key = "good-entry") => new PeripheralProtocol
    {
        Key = key,
        Vendor = "示例",
        Model = "示例接收器",
        Kind = "other",
        Vid = 0x17EF,
        Pid = 0xF006,
        UsagePage = 0xFFCF,
        Usage = 0x00A0,
        MinReportLength = 61,
        FeatureReportId = 193,
        ValueOffset = 5,
        ValueMask = 0xFF,
        ValueMin = 0,
        ValueMax = 100,
    };

    /// <summary>结论里有没有某条字段的错误级结论。</summary>
    private static bool HasError(IReadOnlyList<PeripheralProtocolIssue> issues, string field)
    {
        foreach (var i in issues)
            if (i.IsError && i.Field.Contains(field, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>结论里有没有某条字段的警告级结论。</summary>
    private static bool HasWarning(IReadOnlyList<PeripheralProtocolIssue> issues, string field)
    {
        foreach (var i in issues)
            if (i.Severity == PeripheralProtocolSeverity.Warning
                && i.Field.Contains(field, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>某条结论里有没有提到某个取值（便于断言\"指出了哪个取值\"）。</summary>
    private static bool Mentions(IReadOnlyList<PeripheralProtocolIssue> issues, string needle)
    {
        foreach (var i in issues)
            if (i.Describe().Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static void Run(Action<string, bool> check)
    {
        // ==================== 一、单条目：合法与非法 ====================

        check("表项校验：合法条目（联想式直读表项）没有错误级结论",
            !HasError(PeripheralProtocolTable.ValidateEntry(Good()), ""));

        var noKey = Good() with { Key = "  " };
        check("表项校验：key 为空白 → 错误，结论点名 key",
            HasError(PeripheralProtocolTable.ValidateEntry(noKey), "key"));

        var badVid = Good() with { Vid = 70000 };
        check("表项校验：vid = 70000 越界 → 错误，结论写出取值 70000",
            HasError(PeripheralProtocolTable.ValidateEntry(badVid), "vid")
            && Mentions(PeripheralProtocolTable.ValidateEntry(badVid), "70000"));

        var badPid = Good() with { Pid = -1 };
        check("表项校验：pid = -1 → 错误",
            HasError(PeripheralProtocolTable.ValidateEntry(badPid), "pid"));

        var badOffset = Good() with { ValueOffset = -2 };
        check("表项校验：valueOffset = -2（只能是 -1 或 ≥ 0）→ 错误",
            HasError(PeripheralProtocolTable.ValidateEntry(badOffset), "valueOffset"));

        var zeroSpan = Good() with { ValueMin = 50, ValueMax = 50 };
        check("表项校验：valueMax = valueMin（除零）→ 错误",
            HasError(PeripheralProtocolTable.ValidateEntry(zeroSpan), "valueMax"));

        var inverted = Good() with { ValueMin = 100, ValueMax = 0 };
        check("表项校验：valueMax < valueMin → 错误",
            HasError(PeripheralProtocolTable.ValidateEntry(inverted), "valueMax"));

        var wideMask = Good() with { ValueMask = 0x100 };
        check("表项校验：valueMask = 0x100（超过一字节）→ 错误",
            HasError(PeripheralProtocolTable.ValidateEntry(wideMask), "valueMask"));

        var zeroMask = Good() with { ValueMask = 0 };
        check("表项校验：valueMask = 0（取位后恒为 0，电量会永远报 0%）→ 错误（不伪装）",
            HasError(PeripheralProtocolTable.ValidateEntry(zeroMask), "valueMask"));

        var badMin = Good() with { ValueMin = 300 };
        check("表项校验：valueMin = 300（超过一字节）→ 错误",
            HasError(PeripheralProtocolTable.ValidateEntry(badMin), "valueMin"));

        var badFeature = Good() with { FeatureReportId = 300 };
        check("表项校验：featureReportId = 300（报告 ID 是 1 字节）→ 错误",
            HasError(PeripheralProtocolTable.ValidateEntry(badFeature), "featureReportId"));

        var badMinLen = Good() with { MinReportLength = -1 };
        check("表项校验：minReportLength = -1 → 错误",
            HasError(PeripheralProtocolTable.ValidateEntry(badMinLen), "minReportLength"));

        var badChargeMask = Good() with { ChargingOffset = 6, ChargingMask = 2 };
        check("表项校验：chargingMask = 2（充电位只认明确的 0/1）→ 错误",
            HasError(PeripheralProtocolTable.ValidateEntry(badChargeMask), "chargingMask"));

        var zeroChargeMask = Good() with { ChargingOffset = 6, ChargingMask = 0 };
        check("表项校验：chargingMask = 0（永远读成未充电＝谎报）→ 错误",
            HasError(PeripheralProtocolTable.ValidateEntry(zeroChargeMask), "chargingMask"));

        var badChargeOffset = Good() with { ChargingOffset = -1 };
        check("表项校验：chargingOffset = -1（只能是 null 或 ≥ 0）→ 错误",
            HasError(PeripheralProtocolTable.ValidateEntry(badChargeOffset), "chargingOffset"));

        var zeroScale = Good() with { MilliVoltsOffset = 7, MilliVoltsScale = 0 };
        check("表项校验：milliVoltsScale = 0（电压恒为 0＝报假电压）→ 错误",
            HasError(PeripheralProtocolTable.ValidateEntry(zeroScale), "milliVoltsScale"));

        var zeroMvMask = Good() with { MilliVoltsOffset = 7, MilliVoltsMask = 0 };
        check("表项校验：milliVoltsMask = 0（电压恒为 0）→ 错误",
            HasError(PeripheralProtocolTable.ValidateEntry(zeroMvMask), "milliVoltsMask"));

        var badMvOffset = Good() with { MilliVoltsOffset = -3 };
        check("表项校验：milliVoltsOffset = -3 → 错误",
            HasError(PeripheralProtocolTable.ValidateEntry(badMvOffset), "milliVoltsOffset"));

        // 只有警告、没有错误的情形
        var noReadWay = new PeripheralProtocol { Key = "no-way", Vid = 0x1234, Pid = 0x5678 };
        check("表项校验：既没有直读报告也没有查询命令 → 警告（永远读不到电量），不是静默放过",
            HasWarning(PeripheralProtocolTable.ValidateEntry(noReadWay), "command"));

        var unknownKind = Good() with { Kind = "mouce" };
        check("表项校验：kind 拼错 → 警告（只影响图标）",
            HasWarning(PeripheralProtocolTable.ValidateEntry(unknownKind), "kind"));

        var noNames = Good() with { Vendor = "", Model = "" };
        check("表项校验：vendor/model 都为空 → 警告",
            HasWarning(PeripheralProtocolTable.ValidateEntry(noNames), "vendor/model"));

        // ==================== 二、整表：键唯一 + 匹配歧义 ====================

        var dupTable = new List<PeripheralProtocol> { Good("dup"), Good("dup") };
        var dupReport = PeripheralProtocolTable.ValidateTable(dupTable, "内置表");
        check("整表校验：同一张表里 key 重复 → 错误",
            dupReport.HasErrors && HasError(dupReport.Issues, "key"));

        var ambiguous = new List<PeripheralProtocol>
        {
            Good("entry-a"),
            Good("entry-b"),
        };
        var ambReport = PeripheralProtocolTable.ValidateTable(ambiguous, "内置表");
        check("整表校验：匹配条件完全相同但 key 不同 → 错误（命中哪条只取决于顺序）",
            ambReport.HasErrors && Mentions(ambReport.Issues, "entry-a"));

        var distinct = new List<PeripheralProtocol>
        {
            Good("entry-a"),
            Good("entry-b") with { Usage = 0x00A1 },
        };
        check("整表校验：匹配条件不同（usage 不一样）的两条表项不算歧义",
            !PeripheralProtocolTable.ValidateTable(distinct, "内置表").HasErrors);

        var okTable = new List<PeripheralProtocol> { Good("only-one") };
        check("整表校验：只有一条合法表项的表合格",
            PeripheralProtocolTable.ValidateTable(okTable, "内置表").Ok);

        // ==================== 三、覆盖规则 ====================

        var builtin = new List<PeripheralProtocol> { Good("lenovo-idea5003") };
        var userOverride = new List<PeripheralProtocol>
        {
            Good("lenovo-idea5003") with { ValueOffset = 9 },
        };
        var overrideReport = PeripheralProtocolTable.ValidateTable(userOverride, "用户表", builtin);
        check("整表校验：用户条目同 key 覆盖内置条目 → 信息级结论（不是错误）",
            !overrideReport.HasErrors
            && overrideReport.Issues.Any(i => i.Severity == PeripheralProtocolSeverity.Info
                && i.Value == "lenovo-idea5003"));

        var partialOverride = new List<PeripheralProtocol>
        {
            new PeripheralProtocol { Key = "lenovo-idea5003", ValueOffset = 12 },
        };
        var partialReport = PeripheralProtocolTable.ValidateTable(partialOverride, "用户表", builtin);
        check("整表校验：只写 key + valueOffset 的覆盖条目 → 警告（整条替换不是字段级合并，覆盖不会生效）",
            HasWarning(partialReport.Issues, "vid/pid"));

        var effective = PeripheralProtocolTable.EffectiveTable(builtin, userOverride);
        check("生效表：用户条目在前、同 key 的内置条目被顶掉（表里只剩 1 条）",
            effective.Count == 1 && effective[0].ValueOffset == 9);

        var effectiveBoth = PeripheralProtocolTable.EffectiveTable(builtin,
            new List<PeripheralProtocol> { Good("other-user") });
        check("生效表：不同 key 时用户条目在前、内置条目保留（2 条）",
            effectiveBoth.Count == 2 && effectiveBoth[0].Key == "other-user");

        // ==================== 四、用户 JSON：报具体字段 ====================

        const string GoodJson = """
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
        var goodReport = PeripheralProtocolTable.ValidateUserJson(GoodJson, "用户表");
        check("用户 JSON：合法表无错误级结论", goodReport.Ok);

        var noKeyJson = """[ { "vid": 1, "pid": 2, "valueOffset": 8 } ]""";
        var noKeyReport = PeripheralProtocolTable.ValidateUserJson(noKeyJson, "用户表");
        check("用户 JSON：缺少 key → 错误，结论点名 key",
            noKeyReport.HasErrors && HasError(noKeyReport.Issues, "key"));

        var strVidJson = """[ { "key": "k", "vid": "abc", "valueOffset": 8 } ]""";
        var strVidReport = PeripheralProtocolTable.ValidateUserJson(strVidJson, "用户表");
        check("用户 JSON：vid 写成字符串 → 错误，结论点名 vid 并写出取值",
            strVidReport.HasErrors && HasError(strVidReport.Issues, "vid")
            && Mentions(strVidReport.Issues, "abc"));

        var bigVidJson = """[ { "key": "k", "vid": 70000, "valueOffset": 8 } ]""";
        var bigVidReport = PeripheralProtocolTable.ValidateUserJson(bigVidJson, "用户表");
        check("用户 JSON：vid = 70000 → 错误，结论写出取值 70000",
            bigVidReport.HasErrors && Mentions(bigVidReport.Issues, "70000"));

        var unknownFieldJson = """[ { "key": "k", "valueOffset": 8, "valueOffest": 9 } ]""";
        var unknownFieldReport = PeripheralProtocolTable.ValidateUserJson(unknownFieldJson, "用户表");
        check("用户 JSON：拼错的字段名 → 警告（不静默忽略）",
            HasWarning(unknownFieldReport.Issues, "valueOffest"));

        var objectRootJson = """{ "key": "k" }""";
        var objectRootReport = PeripheralProtocolTable.ValidateUserJson(objectRootJson, "用户表");
        check("用户 JSON：根节点不是数组 → 错误",
            objectRootReport.HasErrors && Mentions(objectRootReport.Issues, "数组"));

        var brokenJson = """[ { "key": "k", ]""";
        var brokenReport = PeripheralProtocolTable.ValidateUserJson(brokenJson, "用户表");
        check("用户 JSON：语法错误 → 错误（带行号，不是\"解析失败\"四个字）",
            brokenReport.HasErrors && Mentions(brokenReport.Issues, "语法")
            && Mentions(brokenReport.Issues, "行"));

        var invertedJson = """[ { "key": "k", "vid": 1, "pid": 2, "valueOffset": 8, "valueMin": 100, "valueMax": 0 } ]""";
        var invertedJsonReport = PeripheralProtocolTable.ValidateUserJson(invertedJson, "用户表");
        check("用户 JSON：valueMax < valueMin → 错误（跨字段规则）",
            invertedJsonReport.HasErrors && HasError(invertedJsonReport.Issues, "valueMax"));

        var zeroMaskJson = """[ { "key": "k", "vid": 1, "pid": 2, "valueOffset": 8, "valueMask": 0 } ]""";
        var zeroMaskJsonReport = PeripheralProtocolTable.ValidateUserJson(zeroMaskJson, "用户表");
        check("用户 JSON：valueMask = 0 → 错误（照着读会永远报 0%）",
            zeroMaskJsonReport.HasErrors && HasError(zeroMaskJsonReport.Issues, "valueMask"));

        // 一条写坏了（类型不对）时，跨字段问题也要一次报全，不能被反序列化失败连带藏起来
        var mixedJson = """[ { "key": "k", "pid": "abc", "valueMin": 100, "valueMax": 0, "valueMask": 0, "chargingMask": 3 } ]""";
        var mixedReport = PeripheralProtocolTable.ValidateUserJson(mixedJson, "用户表");
        check("用户 JSON：一条写坏了也要一次报全（类型错 + 跨字段问题同时列出）",
            mixedReport.HasErrors && HasError(mixedReport.Issues, "pid")
            && HasError(mixedReport.Issues, "valueMax") && HasError(mixedReport.Issues, "valueMask")
            && HasError(mixedReport.Issues, "chargingMask"));

        var badTransportJson = """[ { "key": "k", "vid": 1, "pid": 2, "transport": "InterruptOUT" } ]""";
        var badTransportReport = PeripheralProtocolTable.ValidateUserJson(badTransportJson, "用户表");
        check("用户 JSON：transport 写错 → 错误，结论写出取值",
            badTransportReport.HasErrors && Mentions(badTransportReport.Issues, "InterruptOUT"));

        var badHexJson = """[ { "key": "k", "vid": 1, "pid": 2, "command": "05 ZZ" } ]""";
        var badHexReport = PeripheralProtocolTable.ValidateUserJson(badHexJson, "用户表");
        check("用户 JSON：command 里的十六进制字节非法 → 错误，结论指出是哪个字节",
            badHexReport.HasErrors && Mentions(badHexReport.Issues, "ZZ"));

        var overrideJson = """[ { "key": "lenovo-idea5003", "vid": 6127, "pid": 61446, "valueOffset": 9 } ]""";
        var overrideJsonReport = PeripheralProtocolTable.ValidateUserJson(overrideJson, "用户表", builtin);
        check("用户 JSON：同 key 覆盖内置条目 → 信息级结论、且不是错误",
            !overrideJsonReport.HasErrors
            && overrideJsonReport.Issues.Any(i => i.Severity == PeripheralProtocolSeverity.Info
                && i.Value == "lenovo-idea5003"));

        var partialOverrideJson = """[ { "key": "lenovo-idea5003", "valueOffset": 12 } ]""";
        var partialOverrideJsonReport =
            PeripheralProtocolTable.ValidateUserJson(partialOverrideJson, "用户表", builtin);
        check("用户 JSON：只写 key 的覆盖条目 → 警告（覆盖不会生效）",
            HasWarning(partialOverrideJsonReport.Issues, "vid/pid"));

        // ==================== 五、与真机对账：偏移必须落在报告长度内 ====================

        var oobIssues = PeripheralProtocolTable.ValidateAgainstDevice(
            Good() with { ValueOffset = 200 }, featureReportLength: 64, outputReportLength: 64);
        check("设备对账：valueOffset = 200 越过 Feature 报告长度 64 → 错误（真机上就是永远读不到）",
            oobIssues.Any(i => i.IsError && i.Field.Contains("valueOffset", StringComparison.OrdinalIgnoreCase)));

        var okIssues = PeripheralProtocolTable.ValidateAgainstDevice(
            Good(), featureReportLength: 64, outputReportLength: 64,
            reportIds: new byte[] { 193 });
        check("设备对账：偏移落在报告长度内且报告 ID 存在 → 没有错误",
            !okIssues.Any(i => i.IsError));

        var ridIssues = PeripheralProtocolTable.ValidateAgainstDevice(
            Good(), featureReportLength: 64, outputReportLength: 64,
            reportIds: new byte[] { 1, 2, 3 });
        check("设备对账：报告 ID 不在设备能力表里 → 警告",
            ridIssues.Any(i => i.Severity == PeripheralProtocolSeverity.Warning
                && i.Field.Contains("featureReportId", StringComparison.OrdinalIgnoreCase)));

        var noFeatureIssues = PeripheralProtocolTable.ValidateAgainstDevice(
            Good(), featureReportLength: 0, outputReportLength: 64);
        check("设备对账：集合没有 Feature 报告却声明直读 → 错误",
            noFeatureIssues.Any(i => i.IsError));

        // ==================== 六、严重级别统计 ====================

        var stats = PeripheralProtocolTable.ValidateTable(
            new List<PeripheralProtocol>
            {
                Good("s-ok"),
                Good("s-bad") with { ValueMask = 0, Key = "" },
            },
            "统计表");
        check("统计：错误级与警告级分开计数（ErrorCount / WarningCount / InfoCount）",
            stats.ErrorCount >= 2 && stats.WarningCount >= 0 && stats.InfoCount == 0 && !stats.Ok);
    }
}
