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

        // 读不到电量的表项 = 无效表项（错误级），不是"警告一下就放过"
        var noReadWay = new PeripheralProtocol { Key = "no-way", Vid = 0x1234, Pid = 0x5678 };
        check("表项校验：既没有直读报告也没有查询命令 → 错误（永远读不到电量，这条表项无效）",
            HasError(PeripheralProtocolTable.ValidateEntry(noReadWay), "command"));

        var preOnly = new PeripheralProtocol
        {
            Key = "pre-only",
            Vid = 0x1234,
            Pid = 0x5678,
            PreCommands = new[] { new byte[] { 0x05, 0x01 } },
        };
        check("表项校验：只有握手命令（preCommands）没有查询命令 → 错误（握手完了没人问）",
            HasError(PeripheralProtocolTable.ValidateEntry(preOnly), "command"));

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
        check("生效表：用户条目在前、内置条目**不删**（2 条）—— 与产品 Match 的规则一致",
            effective.Count == 2 && effective[0].ValueOffset == 9 && effective[1].ValueOffset == 5);

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
        var mixedJson = """[ { "key": "k", "pid": "abc", "valueOffset": 8, "valueMin": 100, "valueMax": 0, "valueMask": 0, "chargingOffset": 6, "chargingMask": 3 } ]""";
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

        var overrideJson = """[ { "key": "lenovo-idea5003", "vid": 6127, "pid": 61446, "featureReportId": 193, "valueOffset": 9 } ]""";
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

        // ==================== 七、覆盖语义：与产品 Match 同一套规则 ====================

        // Matches 与产品 PeripheralProtocols.Match 必须逐条一致（漂移守卫：产品改了匹配规则这里要跟着改）
        var probe = Good("drift-probe");
        bool matchAgrees = true;
        try
        {
            PeripheralProtocols.AddUserProtocols(new[] { probe });
            foreach (var (vid, pid, page, usage, len) in new (int, int, ushort, ushort, int)[]
            {
                (0x17EF, 0xF006, 0xFFCF, 0x00A0, 61),
                (0x17EF, 0xF006, 0xFFCF, 0x00A1, 61),
                (0x17EF, 0xF007, 0xFFCF, 0x00A0, 61),
                (0x17EF, 0xF006, 0x0001, 0x00A0, 61),
                (0x17EF, 0xF006, 0xFFCF, 0x00A0, 60),
            })
            {
                bool mine = PeripheralProtocolTable.Matches(probe, vid, pid, page, usage, len);
                bool theirs = ReferenceEquals(
                    PeripheralProtocols.Match(vid, pid, page, usage, len), probe);
                if (mine != theirs) matchAgrees = false;
            }
        }
        finally
        {
            PeripheralProtocols.ClearUserProtocols();
        }
        check("覆盖语义：Matches 与产品 PeripheralProtocols.Match 判定一致（漂移守卫）", matchAgrees);

        // 覆盖回退：用户条目匹配不上时，仍由内置条目顶上（设备不会被弄丢）
        PeripheralProtocol? fallbackHit = null;
        PeripheralProtocol? userHit = null;
        try
        {
            PeripheralProtocols.AddUserProtocols(new List<PeripheralProtocol>
            {
                Good("lenovo-idea5003") with { Vid = 0x1111, Pid = 0x2222, ValueOffset = 12 },
            });
            fallbackHit = PeripheralProtocols.Match(0x17EF, 0xF006, 0xFFCF, 0x00A0, 61);
            PeripheralProtocols.ClearUserProtocols();
            PeripheralProtocols.AddUserProtocols(new List<PeripheralProtocol>
            {
                Good("lenovo-idea5003") with { ValueOffset = 12 },
            });
            userHit = PeripheralProtocols.Match(0x17EF, 0xF006, 0xFFCF, 0x00A0, 61);
        }
        finally
        {
            PeripheralProtocols.ClearUserProtocols();
        }
        check("覆盖回退：用户条目匹配不上（vid/pid 不符）时，仍由内置条目顶上（偏移 5）",
            fallbackHit is not null && fallbackHit.ValueOffset == 5);
        check("覆盖回退：用户条目匹配得上时，命中的是用户条目（偏移 12）",
            userHit is not null && userHit.ValueOffset == 12);

        // 覆盖关系三态
        var sameCond = Good("lenovo-idea5003") with { ValueOffset = 9 };
        var wrongVid = Good("lenovo-idea5003") with { Vid = 0x1111 };
        var noVidEntry = new PeripheralProtocol { Key = "lenovo-idea5003", ValueOffset = 12 };
        check("覆盖关系：匹配条件一致 → Covers（能顶掉内置条目）",
            PeripheralProtocolTable.CompareOverride(sameCond, builtin[0]) == PeripheralOverrideRelation.Covers);
        check("覆盖关系：vid 不符 → None（顶不掉）",
            PeripheralProtocolTable.CompareOverride(wrongVid, builtin[0]) == PeripheralOverrideRelation.None);
        check("覆盖关系：没有 vid/pid → None（顶不掉）",
            PeripheralProtocolTable.CompareOverride(noVidEntry, builtin[0]) == PeripheralOverrideRelation.None);
        var wideBuiltin = Good("wide") with { UsagePage = null, Usage = null };
        var narrower = Good("wide") with { UsagePage = null, Usage = 0x00A0 };
        check("覆盖关系：内置不限 usage、用户限了具体 usage → Partial（只顶掉一部分设备）",
            PeripheralProtocolTable.CompareOverride(narrower, wideBuiltin) == PeripheralOverrideRelation.Partial);
        check("覆盖关系：说清楚哪里对不上（vid 不一致要写出两个取值）",
            PeripheralProtocolTable.DescribeOverrideMismatch(wrongVid, builtin[0]).Contains("vid 不一致")
            && PeripheralProtocolTable.DescribeOverrideMismatch(wrongVid, builtin[0]).Contains("1111"));

        // 覆盖是否**真的**生效：拿已连接设备判定
        var deviceKeys = new List<PeripheralDeviceKey>
        {
            new PeripheralDeviceKey(0x17EF, 0xF006, 0xFFCF, 0x00A0, 137),
        };
        var effectiveUser = new List<PeripheralProtocol> { Good("lenovo-idea5003") with { ValueOffset = 9 } };
        var overrideOkIssues = PeripheralProtocolTable.ValidateOverrideAgainstDevices(effectiveUser, builtin, deviceKeys);
        check("覆盖生效性：用户条目能匹配已连接设备 → 信息级（覆盖确实生效）",
            overrideOkIssues.Any(i => i.Severity == PeripheralProtocolSeverity.Info && i.Value == "lenovo-idea5003")
            && !overrideOkIssues.Any(i => i.IsError));

        var mismatchUser = new List<PeripheralProtocol>
        {
            Good("lenovo-idea5003") with { Vid = 0x1111, ValueOffset = 12 },
        };
        var mismatchIssues = PeripheralProtocolTable.ValidateOverrideAgainstDevices(mismatchUser, builtin, deviceKeys);
        check("覆盖生效性：内置能匹配设备、用户条目匹配不上 → 错误（用户以为改了、产品其实没改）",
            mismatchIssues.Any(i => i.IsError && i.Message.Contains("不生效")));

        var noDeviceIssues = PeripheralProtocolTable.ValidateOverrideAgainstDevices(
            mismatchUser, builtin, new List<PeripheralDeviceKey>());
        check("覆盖生效性：当前没有可验证的设备 → 信息级（无法验证 ≠ 失效）",
            !noDeviceIssues.Any(i => i.IsError)
            && noDeviceIssues.Any(i => i.Severity == PeripheralProtocolSeverity.Info));

        // ==================== 八、歧义、缩放、候选表项 ====================

        // usage 宽窄不同（有重叠）也算歧义
        var widthAmbiguous = new List<PeripheralProtocol>
        {
            Good("wide-a") with { Usage = null },
            Good("narrow-b") with { Usage = 0x00A0 },
        };
        var widthReport = PeripheralProtocolTable.ValidateTable(widthAmbiguous, "用户表");
        check("整表校验：同一 usagePage 下 usage 宽窄不同（有重叠）→ 歧义错误",
            widthReport.HasErrors && Mentions(widthReport.Issues, "宽窄不同"));

        // 缩放系数为 0：即使没写电压字段也不合法（电压会恒为 0）
        var zeroScaleNoMv = Good() with { MilliVoltsScale = 0 };
        check("表项校验：milliVoltsScale = 0（没有电压字段也不合法）→ 错误",
            HasError(PeripheralProtocolTable.ValidateEntry(zeroScaleNoMv), "milliVoltsScale"));
        var zeroScaleJson = """[ { "key": "k", "vid": 1, "pid": 2, "featureReportId": 193, "milliVoltsScale": 0 } ]""";
        var zeroScaleJsonReport = PeripheralProtocolTable.ValidateUserJson(zeroScaleJson, "用户表");
        check("用户 JSON：milliVoltsScale = 0 → 错误（电压恒为 0＝报假电压）",
            zeroScaleJsonReport.HasErrors && HasError(zeroScaleJsonReport.Issues, "milliVoltsScale"));

        // 没声明 chargingOffset 时，chargingMask 不参与判定（早期误报）
        var chargeMaskNoOffsetJson =
            """[ { "key": "k", "vid": 1, "pid": 2, "featureReportId": 193, "chargingMask": 3 } ]""";
        var chargeMaskNoOffsetReport = PeripheralProtocolTable.ValidateUserJson(chargeMaskNoOffsetJson, "用户表");
        check("用户 JSON：没声明 chargingOffset 时 chargingMask 不参与判定（不误报）",
            !HasError(chargeMaskNoOffsetReport.Issues, "chargingMask"));

        // 候选表项：没被选中的那条也要能捞出来对着真机校验
        var shadowTable = PeripheralProtocolTable.EffectiveTable(builtin,
            new List<PeripheralProtocol> { Good("lenovo-idea5003") with { ValueOffset = 12 } });
        var candidates = PeripheralProtocolTable.CandidatesFor(shadowTable, 0x17EF, 0xF006);
        check("候选表项：vid/pid 对得上的全部条目都能被捞出来（含没被选中的那条）",
            candidates.Count == 2 && candidates.Any(c => c.ValueOffset == 12) && candidates.Any(c => c.ValueOffset == 5));
        var shadowBad = candidates.First(c => c.ValueOffset == 12) with { ValueOffset = 200 };
        var shadowBadIssues = PeripheralProtocolTable.ValidateAgainstDevice(shadowBad, 64, 64);
        check("候选表项：没被选中的那条同样要过偏移越界校验（200 > 64 → 错误）",
            shadowBadIssues.Any(i => i.IsError && i.Field.Contains("valueOffset")));
    }
}
