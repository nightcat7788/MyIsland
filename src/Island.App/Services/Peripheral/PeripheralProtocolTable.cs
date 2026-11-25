using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Island.App.Services.Peripheral;

/// <summary>协议表校验结论的严重级别。</summary>
public enum PeripheralProtocolSeverity
{
    /// <summary>说明性信息（例：这条用户条目覆盖了内置条目）。</summary>
    Info,

    /// <summary>不致命，但多半是用户想改的东西没改对。</summary>
    Warning,

    /// <summary>这条表项不合法（照它读不到电量，或者会读出假数据）。</summary>
    Error,
}

/// <summary>用户条目与同 key 内置条目之间的覆盖关系。</summary>
public enum PeripheralOverrideRelation
{
    /// <summary>顶不掉：用户条目匹配不上内置条目能匹配的设备（覆盖不会生效）。</summary>
    None,

    /// <summary>只顶掉一部分：用户条目的匹配条件比内置条目窄。</summary>
    Partial,

    /// <summary>全顶掉：用户条目的匹配条件覆盖了内置条目的全部设备。</summary>
    Covers,
}

/// <summary>一台设备的匹配身份（协议表匹配要用到的那几个字段）。</summary>
public readonly record struct PeripheralDeviceKey(
    int Vid, int Pid, ushort UsagePage, ushort Usage, int OutputReportLength)
{
    /// <summary>人读的一行（诊断用）。</summary>
    public string Describe() => Vid.ToString("X4") + ":" + Pid.ToString("X4")
        + "/" + UsagePage.ToString("X4") + ":" + Usage.ToString("X4")
        + " (out=" + OutputReportLength + ")";
}

/// <summary>
/// 一条校验结论：**哪个字段、哪个取值**不合法，以及为什么不合法。
/// 协议表是给人手填的，"解析失败" 这四个字没有任何帮助。
/// </summary>
public sealed record PeripheralProtocolIssue(
    string Field,
    string? Value,
    string Message,
    PeripheralProtocolSeverity Severity = PeripheralProtocolSeverity.Error)
{
    /// <summary>这条结论是不是错误（错误 ⇒ 整张表不合格）。</summary>
    public bool IsError => Severity == PeripheralProtocolSeverity.Error;

    /// <summary>人读的一行：字段 + 取值 + 说明。</summary>
    public string Describe()
    {
        string head = Field ?? "";
        if (Value is not null) head = head.Length == 0 ? Value : head + " = " + Value;
        return head.Length == 0 ? Message : head + "：" + Message;
    }

    public override string ToString() => Describe();
}

/// <summary>一张协议表（一个来源）的校验结果。</summary>
public sealed class PeripheralProtocolTableReport
{
    public PeripheralProtocolTableReport(string source, IReadOnlyList<PeripheralProtocolIssue> issues)
    {
        Source = source;
        Issues = issues;
    }

    /// <summary>这张表的来源（"内置表" / 用户 JSON 的路径）。</summary>
    public string Source { get; }

    /// <summary>全部结论（按发现顺序）。</summary>
    public IReadOnlyList<PeripheralProtocolIssue> Issues { get; }

    /// <summary>错误级结论的条数。</summary>
    public int ErrorCount => Issues.Count(i => i.IsError);

    /// <summary>警告级结论的条数。</summary>
    public int WarningCount => Issues.Count(i => i.Severity == PeripheralProtocolSeverity.Warning);

    /// <summary>信息级结论的条数。</summary>
    public int InfoCount => Issues.Count(i => i.Severity == PeripheralProtocolSeverity.Info);

    /// <summary>这张表是否合格（没有错误级结论）。</summary>
    public bool Ok => ErrorCount == 0;

    /// <summary>有没有错误级结论（与 <see cref="Ok"/> 互为反面）。</summary>
    public bool HasErrors => ErrorCount > 0;
}

/// <summary>
/// 外设协议表的**校验层** —— 纯逻辑、无 Win32 依赖，可链进零 NuGet 的测试工程。
///
/// 为什么要有这一层：协议表是**纯数据**且用户可覆盖
/// （%LOCALAPPDATA%\MyIsland\peripheral-protocols.json），但原来没有任何工具能回答：
///   ① 这张表本身合法吗（偏移越界 / 掩码不合理 / 键重复 / 覆盖规则歧义）？
///   ② 当前插着的设备命中了表的哪一条、走的哪一层判定？
/// 没有自检 ⇒ "加了一条表项" 只是盲写。
///
/// 两条底线：
///   · **不伪装**：读不到的量一律判为不合格/未知，绝不用 0%、100% 或默认值充数；
///   · **可定位**：每条结论都带"字段 + 取值 + 原因"，用户能照着改 JSON。
/// </summary>
public static class PeripheralProtocolTable
{
    /// <summary>用户协议表文件在 %LOCALAPPDATA% 下的相对路径。</summary>
    public const string UserFileRelativePath = @"MyIsland\peripheral-protocols.json";

    /// <summary>协议表认识的设备类型。</summary>
    public static readonly IReadOnlyList<string> KnownKinds =
        new[] { "mouse", "keyboard", "headset", "gamepad", "other" };

    /// <summary>用户协议表文件的默认完整路径。</summary>
    public static string DefaultUserFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyIsland", "peripheral-protocols.json");

    // ==================== 一、单条目校验 ====================

    /// <summary>
    /// 校验一条表项本身的合法性（不涉及别的表项、也不涉及具体设备）。
    /// 返回的结论里，Error 级 = 这条表项照着读不出电量或会读出假数据。
    /// </summary>
    public static IReadOnlyList<PeripheralProtocolIssue> ValidateEntry(PeripheralProtocol? protocol)
    {
        var issues = new List<PeripheralProtocolIssue>();
        if (protocol is null)
        {
            issues.Add(new PeripheralProtocolIssue("(表项)", null, "表项为 null"));
            return issues;
        }

        // ---- key：唯一键，也是覆盖内置条目的依据 ----
        if (string.IsNullOrWhiteSpace(protocol.Key))
        {
            issues.Add(new PeripheralProtocolIssue("key", protocol.Key ?? "",
                "表项必须有非空的 key（唯一键，也是覆盖内置条目的依据）"));
        }

        // ---- VID / PID：16 位 ----
        if (protocol.Vid < 0 || protocol.Vid > 0xFFFF)
            issues.Add(new PeripheralProtocolIssue("vid", protocol.Vid.ToString(CultureInfo.InvariantCulture),
                "vid 必须落在 0..0xFFFF（USB 厂商 ID 是 16 位）"));
        if (protocol.Pid < 0 || protocol.Pid > 0xFFFF)
            issues.Add(new PeripheralProtocolIssue("pid", protocol.Pid.ToString(CultureInfo.InvariantCulture),
                "pid 必须落在 0..0xFFFF（USB 产品 ID 是 16 位）"));
        if (protocol.Vid == 0 && protocol.Pid == 0)
            issues.Add(new PeripheralProtocolIssue("vid/pid", "0/0",
                "没有指定 vid/pid，这条表项匹配不到任何设备", PeripheralProtocolSeverity.Warning));

        // ---- 显示字段 ----
        if (!KnownKinds.Contains(protocol.Kind ?? "", StringComparer.OrdinalIgnoreCase))
            issues.Add(new PeripheralProtocolIssue("kind", protocol.Kind ?? "",
                "kind 应是 " + string.Join(" / ", KnownKinds) + " 之一（只影响图标与文案）",
                PeripheralProtocolSeverity.Warning));
        if (string.IsNullOrWhiteSpace(protocol.Vendor) && string.IsNullOrWhiteSpace(protocol.Model))
            issues.Add(new PeripheralProtocolIssue("vendor/model", "(都为空)",
                "既没有 vendor 也没有 model，界面会退回设备自身的名字",
                PeripheralProtocolSeverity.Warning));

        // ---- 报告长度下限 ----
        if (protocol.MinReportLength < 0)
            issues.Add(new PeripheralProtocolIssue("minReportLength",
                protocol.MinReportLength.ToString(CultureInfo.InvariantCulture),
                "minReportLength 不能为负"));

        // ---- 直读 Feature 报告 ----
        if (protocol.FeatureReportId < -1 || protocol.FeatureReportId > 255)
            issues.Add(new PeripheralProtocolIssue("featureReportId",
                protocol.FeatureReportId.ToString(CultureInfo.InvariantCulture),
                "featureReportId 必须是 -1（不直读 Feature 报告）或 0..255"));

        // ---- 电量字段 ----
        if (protocol.ValueOffset < -1)
            issues.Add(new PeripheralProtocolIssue("valueOffset",
                protocol.ValueOffset.ToString(CultureInfo.InvariantCulture),
                "valueOffset 必须是 -1（本协议不报电量）或 ≥ 0"));

        if (protocol.ValueOffset >= 0)
        {
            if (protocol.ValueMask < 0 || protocol.ValueMask > 0xFF)
                issues.Add(new PeripheralProtocolIssue("valueMask",
                    protocol.ValueMask.ToString(CultureInfo.InvariantCulture),
                    "valueMask 必须是 0..0xFF（电量字段取自一个字节）"));
            else if (protocol.ValueMask == 0)
                issues.Add(new PeripheralProtocolIssue("valueMask", "0",
                    "valueMask 不能为 0（取位后恒为 0，电量会永远报成 0%）"));

            if (protocol.ValueMin < 0 || protocol.ValueMin > 0xFF)
                issues.Add(new PeripheralProtocolIssue("valueMin",
                    protocol.ValueMin.ToString(CultureInfo.InvariantCulture),
                    "valueMin 必须落在 0..0xFF"));
            if (protocol.ValueMax < 0 || protocol.ValueMax > 0xFF)
                issues.Add(new PeripheralProtocolIssue("valueMax",
                    protocol.ValueMax.ToString(CultureInfo.InvariantCulture),
                    "valueMax 必须落在 0..0xFF"));
            if (protocol.ValueMax <= protocol.ValueMin)
                issues.Add(new PeripheralProtocolIssue("valueMax/valueMin",
                    protocol.ValueMax.ToString(CultureInfo.InvariantCulture) + " / "
                        + protocol.ValueMin.ToString(CultureInfo.InvariantCulture),
                    "valueMax 必须大于 valueMin（否则百分比换算的除数为 0 或负数）"));
        }

        // ---- 充电位 ----
        if (protocol.ChargingOffset is { } chargingOffset)
        {
            if (chargingOffset < 0)
                issues.Add(new PeripheralProtocolIssue("chargingOffset",
                    chargingOffset.ToString(CultureInfo.InvariantCulture),
                    "chargingOffset 必须是 null（不报充电状态）或 ≥ 0"));
            if (protocol.ChargingMask < 0 || protocol.ChargingMask > 0xFF)
                issues.Add(new PeripheralProtocolIssue("chargingMask",
                    protocol.ChargingMask.ToString(CultureInfo.InvariantCulture),
                    "chargingMask 必须是 0..0xFF"));
            else if (protocol.ChargingMask != 1)
                issues.Add(new PeripheralProtocolIssue("chargingMask",
                    protocol.ChargingMask.ToString(CultureInfo.InvariantCulture),
                    "chargingMask 必须是 1（0x01）：充电位只认明确的 0/1；0 会永远读成「未充电」（谎报），"
                        + "大于 1 会取到 2/3 让整帧作废"));
        }

        // ---- 电压字段 ----
        if (protocol.MilliVoltsOffset is { } mvOffset)
        {
            if (mvOffset < 0)
                issues.Add(new PeripheralProtocolIssue("milliVoltsOffset",
                    mvOffset.ToString(CultureInfo.InvariantCulture),
                    "milliVoltsOffset 必须是 null（不报电压）或 ≥ 0"));
            if (protocol.MilliVoltsMask < 0 || protocol.MilliVoltsMask > 0xFFFF)
                issues.Add(new PeripheralProtocolIssue("milliVoltsMask",
                    protocol.MilliVoltsMask.ToString(CultureInfo.InvariantCulture),
                    "milliVoltsMask 必须是 0..0xFFFF"));
            else if (protocol.MilliVoltsMask == 0)
                issues.Add(new PeripheralProtocolIssue("milliVoltsMask", "0",
                    "milliVoltsMask 不能为 0（电压会恒为 0，等于报假电压）"));
        }

        // 缩放系数无条件校验：0 会让电压恒为 0（等于报假电压），与有没有电压字段无关
        if (protocol.MilliVoltsScale <= 0)
            issues.Add(new PeripheralProtocolIssue("milliVoltsScale",
                protocol.MilliVoltsScale.ToString(CultureInfo.InvariantCulture),
                "milliVoltsScale 必须 ≥ 1（0 会让电压恒为 0，等于报假电压）"));

        // ---- 这条表项有没有"读法" ----
        // 读不到电量的表项 = 无效表项（Error）：留着只会让人以为"这条配了"。
        if (protocol.FeatureReportId < 0 && protocol.Command.Length == 0)
        {
            if (protocol.PreCommands.Length == 0)
                issues.Add(new PeripheralProtocolIssue("command/featureReportId", "(都没有)",
                    "既没有直读 Feature 报告，也没有电量查询命令 —— 这条表项永远读不到电量"));
            else
                issues.Add(new PeripheralProtocolIssue("command", "(只有握手命令)",
                    "只有握手命令（preCommands）、没有电量查询命令（command）—— 握手完了没人问，电量永远读不到"));
        }

        return issues;
    }

    // ==================== 二、整表校验 ====================

    /// <summary>
    /// 校验一整张表：条目自身 + 键唯一 + 匹配条件不歧义 + 与内置表的覆盖关系。
    /// </summary>
    /// <param name="table">要校验的表（用户表或内置表）。</param>
    /// <param name="source">来源标签（写进结论，便于用户定位）。</param>
    /// <param name="builtin">内置表（传 null 表示不查覆盖关系）。</param>
    public static PeripheralProtocolTableReport ValidateTable(
        IReadOnlyList<PeripheralProtocol> table,
        string source,
        IReadOnlyList<PeripheralProtocol>? builtin = null)
    {
        var issues = new List<PeripheralProtocolIssue>();
        if (table is null)
        {
            issues.Add(new PeripheralProtocolIssue("(表)", null, "表为 null"));
            return new PeripheralProtocolTableReport(source, issues);
        }

        for (int i = 0; i < table.Count; i++)
            issues.AddRange(ValidateEntry(table[i]));

        // ---- 键唯一（同一张表内）----
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < table.Count; i++)
        {
            string key = table[i]?.Key ?? "";
            if (key.Length == 0) continue;
            if (seen.TryGetValue(key, out int first))
                issues.Add(new PeripheralProtocolIssue("key", key,
                    "key 重复（第 " + (first + 1) + " 条与第 " + (i + 1)
                        + " 条）—— 同一张表里 key 必须唯一，否则命中哪一条只取决于顺序"));
            else
                seen[key] = i;
        }

        // ---- 匹配条件歧义：两条表项的匹配条件完全一样，key 却不同 ----
        for (int i = 0; i < table.Count; i++)
        {
            for (int j = i + 1; j < table.Count; j++)
            {
                PeripheralProtocol? a = table[i];
                PeripheralProtocol? b = table[j];
                if (a is null || b is null) continue;
                if (string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase)) continue;
                if (a.Vid != b.Vid || a.Pid != b.Pid) continue;
                if ((a.Vid == 0 && a.Pid == 0) || (b.Vid == 0 && b.Pid == 0)) continue;
                // usage 宽窄不同也算歧义：一条不限 usage、另一条限了具体 usage 时，
                // 两条能匹配同一台设备 ⇒ 命中哪一条只取决于顺序。
                bool pageOverlap = a.UsagePage is null || b.UsagePage is null
                    || a.UsagePage == b.UsagePage;
                bool usageOverlap = a.Usage is null || b.Usage is null || a.Usage == b.Usage;
                if (!pageOverlap || !usageOverlap) continue;
                bool identical = a.UsagePage == b.UsagePage && a.Usage == b.Usage;
                issues.Add(new PeripheralProtocolIssue("vid/pid/usagePage/usage",
                    "vid=" + a.Vid.ToString("X4") + " pid=" + a.Pid.ToString("X4")
                        + " usage=" + (a.UsagePage is { } ap ? ap.ToString("X4") : "----")
                        + ":" + (a.Usage is { } au ? au.ToString("X4") : "----"),
                    "表项 '" + a.Key + "'（第 " + (i + 1) + " 条）与 '" + b.Key + "'（第 " + (j + 1)
                        + " 条）"
                        + (identical ? "匹配条件完全相同" : "匹配条件有重叠（usage 宽窄不同：一条 "
                            + UsageText(a.Usage) + "、另一条 " + UsageText(b.Usage) + "）")
                        + "但 key 不同 —— 命中哪一条只取决于顺序，表意不清。"
                        + "要表达同一台设备的两种协议，请用不同的 usagePage/usage 区分"));
            }
        }

        // ---- 与内置表的覆盖关系 ----
        if (builtin is not null && builtin.Count > 0)
        {
            var builtinKeys = new Dictionary<string, PeripheralProtocol>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in builtin)
            {
                if (b?.Key is null || b.Key.Length == 0) continue;
                builtinKeys[b.Key] = b;
            }
            foreach (var u in table)
            {
                if (u?.Key is null || u.Key.Length == 0) continue;
                if (!builtinKeys.TryGetValue(u.Key, out PeripheralProtocol? b)) continue;
                issues.AddRange(DescribeOverride(u, b));
            }
        }

        return new PeripheralProtocolTableReport(source, issues);
    }

    // ==================== 三、用户 JSON 文件校验 ====================

    /// <summary>
    /// 校验用户协议表 JSON 文本。**格式错误要报具体字段**，不能只说"解析失败"。
    /// </summary>
    /// <param name="json">JSON 文本（根节点应当是数组）。</param>
    /// <param name="source">来源标签（一般是文件路径）。</param>
    /// <param name="builtin">内置表（用于查覆盖关系）；传 null 表示不查。</param>
    public static PeripheralProtocolTableReport ValidateUserJson(
        string json,
        string source,
        IReadOnlyList<PeripheralProtocol>? builtin = null)
    {
        var issues = new List<PeripheralProtocolIssue>();

        if (json is null)
        {
            issues.Add(new PeripheralProtocolIssue("(JSON 文本)", null, "JSON 文本为 null（没有可校验的内容）"));
            return new PeripheralProtocolTableReport(source, issues);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json ?? "");
        }
        catch (JsonException ex)
        {
            issues.Add(new PeripheralProtocolIssue("(JSON 语法)", ex.LineNumber is { } line
                    ? "第 " + (line + 1) + " 行第 " + (ex.BytePositionInLine + 1) + " 列"
                    : null,
                "JSON 语法错误：" + ex.Message));
            return new PeripheralProtocolTableReport(source, issues);
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                issues.Add(new PeripheralProtocolIssue("(根节点)", root.ValueKind.ToString(),
                    "用户协议表必须是一个数组（[ { … } ]），实际是 " + root.ValueKind));
                return new PeripheralProtocolTableReport(source, issues);
            }

            int index = 0;
            foreach (JsonElement element in root.EnumerateArray())
            {
                string path = "$[" + index + "]";
                if (element.ValueKind != JsonValueKind.Object)
                {
                    issues.Add(new PeripheralProtocolIssue(path, element.ValueKind.ToString(),
                        "每个表项必须是一个对象 { … }"));
                    index++;
                    continue;
                }
                issues.AddRange(CheckJsonEntry(element, path));
                index++;
            }

            // key 唯一性（同一张表内）：这条检查原先只在 ValidateTable 里有，
            // 用户 JSON 写重复 key 时连一条提示都没有 —— 而产品装载时同 key 只保留最后一条。
            var seenKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int keyIndex = 0;
            foreach (JsonElement element in root.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object
                    && TryGetProperty(element, "key", out JsonElement keyElement)
                    && keyElement.ValueKind == JsonValueKind.String)
                {
                    string key = keyElement.GetString() ?? "";
                    if (key.Length > 0)
                    {
                        if (seenKeys.TryGetValue(key, out int first))
                            issues.Add(new PeripheralProtocolIssue("$[" + keyIndex + "].key", key,
                                "key 重复（第 " + (first + 1) + " 条与第 " + (keyIndex + 1)
                                    + " 条）—— 同一张表里 key 必须唯一：产品装载时同 key **只保留最后一条**"
                                    + "（哪一条生效取决于书写顺序，很容易看错）"));
                        else
                            seenKeys[key] = keyIndex;
                    }
                }
                keyIndex++;
            }

            // 逐条反序列化：某一条写坏了（类型不对之类），不能连带把它的跨字段问题
            // 一起藏起来 —— 用户改 JSON 要一次看全，不是挤牙膏。
            int slot = 0;
            foreach (JsonElement element in root.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    try
                    {
                        PeripheralProtocols.LoadFromJson("[" + element.GetRawText() + "]");
                    }
                    catch (Exception)
                    {
                        issues.AddRange(CheckJsonCrossField(element, "$[" + slot + "]"));
                    }
                }
                slot++;
            }
        }

        // ---- 反序列化 + 语义校验（跨字段规则：量程、掩码、覆盖 …）----
        List<PeripheralProtocol>? parsed = null;
        try
        {
            parsed = PeripheralProtocols.LoadFromJson(json ?? "");
        }
        catch (Exception ex)
        {
            issues.Add(new PeripheralProtocolIssue("(表项)", ex.GetType().Name,
                "表项反序列化失败：" + ex.Message));
        }

        if (parsed is not null)
        {
            for (int i = 0; i < parsed.Count; i++)
            {
                string key = parsed[i]?.Key ?? "(无 key)";
                string path = "$[" + i + "]";
                foreach (var issue in ValidateEntry(parsed[i]))
                    issues.Add(new PeripheralProtocolIssue(path + " " + issue.Field, issue.Value,
                        issue.Message + "（表项 key=" + key + "）", issue.Severity));
            }

            if (builtin is not null && builtin.Count > 0)
            {
                var builtinKeys = new Dictionary<string, PeripheralProtocol>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in builtin)
                    if (b?.Key is not null && b.Key.Length > 0) builtinKeys[b.Key] = b;
                foreach (var u in parsed)
                {
                    if (u?.Key is null || u.Key.Length == 0) continue;
                    if (!builtinKeys.TryGetValue(u.Key, out PeripheralProtocol? b)) continue;
                    issues.AddRange(DescribeOverride(u, b));
                }
            }
        }

        return new PeripheralProtocolTableReport(source, issues);
    }

    /// <summary>
    /// 校验用户协议表文件。文件不存在 = 没有用户条目（不算错）。
    /// </summary>
    public static PeripheralProtocolTableReport ValidateUserFile(
        string path,
        IReadOnlyList<PeripheralProtocol>? builtin = null)
    {
        if (string.IsNullOrEmpty(path))
            return new PeripheralProtocolTableReport("(未指定路径)", new List<PeripheralProtocolIssue>());

        if (!File.Exists(path))
        {
            return new PeripheralProtocolTableReport(path, new List<PeripheralProtocolIssue>
            {
                new PeripheralProtocolIssue("(文件)", path,
                    "用户协议表文件不存在（当前没有用户条目，走内置表）",
                    PeripheralProtocolSeverity.Info),
            });
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return new PeripheralProtocolTableReport(path, new List<PeripheralProtocolIssue>
            {
                new PeripheralProtocolIssue("(文件)", path,
                    "读不了用户协议表文件：" + ex.GetType().Name + ": " + ex.Message),
            });
        }

        return ValidateUserJson(json, path, builtin);
    }

    // ==================== 四、与具体设备对账 ====================

    /// <summary>
    /// 把一条表项与**某台设备的实际报告长度**对账：
    /// 偏移 + 长度必须落在报告长度内 —— 越界的偏移在真机上就是"永远读不到"，
    /// 而且失败时表现为"读不到电量"，用户根本猜不到是偏移写错了。
    /// </summary>
    /// <param name="protocol">命中的表项。</param>
    /// <param name="featureReportLength">设备的 Feature 报告长度（直读模式用）。</param>
    /// <param name="outputReportLength">设备的 Output 报告长度（命令模式用）。</param>
    /// <param name="reportIds">设备能力表里出现过的报告 ID（可为空）。</param>
    public static IReadOnlyList<PeripheralProtocolIssue> ValidateAgainstDevice(
        PeripheralProtocol protocol,
        int featureReportLength,
        int outputReportLength,
        IReadOnlyList<byte>? reportIds = null)
    {
        var issues = new List<PeripheralProtocolIssue>();
        if (protocol is null) return issues;

        bool direct = protocol.FeatureReportId >= 0;

        if (direct)
        {
            if (featureReportLength <= 0)
            {
                issues.Add(new PeripheralProtocolIssue("featureReportLength", "0",
                    "表项声明直读 Feature 报告 " + protocol.FeatureReportId
                        + "，但这个集合没有 Feature 报告（长度 0）"));
            }
            else
            {
                if (protocol.ValueOffset >= featureReportLength)
                    issues.Add(new PeripheralProtocolIssue("valueOffset",
                        protocol.ValueOffset.ToString(CultureInfo.InvariantCulture),
                        "偏移越界：Feature 报告长度只有 " + featureReportLength
                            + " 字节（合法偏移 0.." + (featureReportLength - 1) + "）"));
                if (protocol.ChargingOffset is { } co && co >= featureReportLength)
                    issues.Add(new PeripheralProtocolIssue("chargingOffset",
                        co.ToString(CultureInfo.InvariantCulture),
                        "偏移越界：Feature 报告长度只有 " + featureReportLength + " 字节"));
                if (protocol.MilliVoltsOffset is { } mv && mv + 1 >= featureReportLength)
                    issues.Add(new PeripheralProtocolIssue("milliVoltsOffset",
                        mv.ToString(CultureInfo.InvariantCulture),
                        "偏移越界：电压字段要 2 字节，Feature 报告长度只有 " + featureReportLength + " 字节"));
                if (reportIds is { Count: > 0 }
                    && !reportIds.Contains((byte)protocol.FeatureReportId))
                {
                    issues.Add(new PeripheralProtocolIssue("featureReportId",
                        protocol.FeatureReportId.ToString(CultureInfo.InvariantCulture),
                        "这个集合的能力表里没有报告 ID "
                            + protocol.FeatureReportId + "（现有：" + string.Join(",", reportIds) + "）",
                        PeripheralProtocolSeverity.Warning));
                }
            }
        }
        else
        {
            if (protocol.Command.Length > 0 && outputReportLength > 0
                && protocol.Command.Length != outputReportLength)
            {
                issues.Add(new PeripheralProtocolIssue("command",
                    protocol.Command.Length.ToString(CultureInfo.InvariantCulture) + " 字节",
                    "命令长度与集合的 Output 报告长度（" + outputReportLength + " 字节）对不上",
                    PeripheralProtocolSeverity.Warning));
            }
        }

        return issues;
    }

    // ==================== 五、覆盖与生效表 ====================

    /// <summary>
    /// 生效的协议表：**用户条目在前，内置条目在后**，内置条目一条都不删。
    ///
    /// 这与产品 <see cref="PeripheralProtocols.Match"/> 的实际规则一致：
    /// 匹配按顺序走，先命中先用；用户条目匹配不上时，**同 key 的内置条目照样能用**
    /// （⇒ "覆盖"是逐设备成立的，不是"按 key 顶掉一张表项"）。
    /// 早先这里按 key 顶掉内置条目，与产品实际匹配规则对不上 —— 自检报告会出现
    /// "生效的是偏移 12、命中的是偏移 5"这种自相矛盾，已按产品规则更正。
    /// </summary>
    public static IReadOnlyList<PeripheralProtocol> EffectiveTable(
        IReadOnlyList<PeripheralProtocol> builtin,
        IReadOnlyList<PeripheralProtocol> user)
    {
        var result = new List<PeripheralProtocol>();
        foreach (var u in EffectiveUserEntries(user))
            result.Add(u);
        foreach (var b in builtin)
        {
            if (b is null) continue;
            result.Add(b);
        }
        return result;
    }

    /// <summary>
    /// 用户表按 key 去重后的条目：**同 key 只保留最后一条**，顺序按最后一条的位置。
    ///
    /// 这与产品 <see cref="PeripheralProtocols.AddUserProtocols"/> 的实际行为一致
    /// （它对每个条目先 RemoveAll 同 key、再 Add ⇒ 同 key 只剩最后一条）。
    /// 早先这里把全部用户条目都列进生效表，用户写重复 key 时与产品的 <c>All</c> 不一致。
    /// </summary>
    public static IReadOnlyList<PeripheralProtocol> EffectiveUserEntries(
        IReadOnlyList<PeripheralProtocol> user)
    {
        var result = new List<PeripheralProtocol>();
        if (user is null) return result;

        var lastIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < user.Count; i++)
        {
            string key = user[i]?.Key ?? "";
            lastIndex[key] = i;
        }
        for (int i = 0; i < user.Count; i++)
        {
            PeripheralProtocol? u = user[i];
            if (u is null) continue;
            string key = u.Key ?? "";
            if (!lastIndex.TryGetValue(key, out int last) || last != i) continue;
            result.Add(u);
        }
        return result;
    }

    /// <summary>
    /// 表项能否匹配某台设备 —— 判定条件与产品 <see cref="PeripheralProtocols.Match"/> 逐条一致。
    /// （测试里有断言把这两者钉在一起：产品改了匹配规则，这里必须跟着改。）
    /// </summary>
    public static bool Matches(PeripheralProtocol? protocol, int vid, int pid,
        ushort usagePage, ushort usage, int outputReportLength)
    {
        if (protocol is null) return false;
        if (protocol.Vid != vid || protocol.Pid != pid) return false;
        if (protocol.UsagePage is { } page && page != usagePage) return false;
        if (protocol.Usage is { } u && u != usage) return false;
        if (outputReportLength < protocol.MinReportLength) return false;
        return true;
    }

    /// <summary>
    /// 某台设备（VID:PID）的候选表项：vid/pid 对得上的**全部**条目
    /// （含没被选中的那条 —— 没命中的表项也要能对着真机校验偏移越界）。
    /// </summary>
    public static IReadOnlyList<PeripheralProtocol> CandidatesFor(
        IReadOnlyList<PeripheralProtocol> table, int vid, int pid)
    {
        var result = new List<PeripheralProtocol>();
        if (table is null) return result;
        foreach (var p in table)
        {
            if (p is null) continue;
            if (p.Vid != vid || p.Pid != pid) continue;
            result.Add(p);
        }
        return result;
    }

    /// <summary>
    /// 同 key 的用户条目与内置条目之间的覆盖关系（纯比较，不碰设备）。
    /// 用户表是整条替换：用户条目排在前面先命中先用，但**匹配不上就等于没写**。
    /// </summary>
    public static PeripheralOverrideRelation CompareOverride(
        PeripheralProtocol? user, PeripheralProtocol? builtin)
    {
        if (user is null || builtin is null) return PeripheralOverrideRelation.None;
        if (user.Vid == 0 && user.Pid == 0) return PeripheralOverrideRelation.None;
        if (user.Vid != builtin.Vid || user.Pid != builtin.Pid) return PeripheralOverrideRelation.None;

        bool pageOverlap = builtin.UsagePage is { } bp
            ? user.UsagePage is null || user.UsagePage == bp
            : true;
        bool usageOverlap = builtin.Usage is { } bu
            ? user.Usage is null || user.Usage == bu
            : true;
        if (!pageOverlap || !usageOverlap) return PeripheralOverrideRelation.None;

        bool pageCovers = builtin.UsagePage is { } bp2
            ? user.UsagePage is null || user.UsagePage == bp2
            : user.UsagePage is null;
        bool usageCovers = builtin.Usage is { } bu2
            ? user.Usage is null || user.Usage == bu2
            : user.Usage is null;
        bool minCovers = user.MinReportLength <= builtin.MinReportLength;

        return pageCovers && usageCovers && minCovers
            ? PeripheralOverrideRelation.Covers
            : PeripheralOverrideRelation.Partial;
    }

    /// <summary>用户条目与内置条目之间**具体哪里对不上**（给用户照着改）。</summary>
    public static string DescribeOverrideMismatch(PeripheralProtocol? user, PeripheralProtocol? builtin)
    {
        if (user is null || builtin is null) return "表项缺失";
        var parts = new List<string>();
        if (user.Vid == 0 && user.Pid == 0)
        {
            parts.Add("用户条目没有 vid/pid（匹配不上任何设备）");
        }
        else
        {
            if (user.Vid != builtin.Vid)
                parts.Add("vid 不一致（用户 " + user.Vid.ToString("X4")
                    + " vs 内置 " + builtin.Vid.ToString("X4") + "）");
            if (user.Pid != builtin.Pid)
                parts.Add("pid 不一致（用户 " + user.Pid.ToString("X4")
                    + " vs 内置 " + builtin.Pid.ToString("X4") + "）");
        }
        if (user.UsagePage != builtin.UsagePage)
            parts.Add("usagePage 不一致（用户 " + UsageText(user.UsagePage)
                + " vs 内置 " + UsageText(builtin.UsagePage) + "）");
        if (user.Usage != builtin.Usage)
            parts.Add("usage 不一致（用户 " + UsageText(user.Usage)
                + " vs 内置 " + UsageText(builtin.Usage) + "）");
        if (user.MinReportLength != builtin.MinReportLength)
            parts.Add("minReportLength 不一致（用户 " + user.MinReportLength
                + " vs 内置 " + builtin.MinReportLength + "）");
        return parts.Count == 0 ? "匹配条件一致" : string.Join("；", parts);
    }

    /// <summary>usage 字段的人读写法（null = 不限）。</summary>
    private static string UsageText(ushort? value)
        => value is { } v ? v.ToString("X4") : "不限";

    /// <summary>
    /// 拿**已连接的设备**逐条判定"覆盖是不是真的生效了"。
    ///
    /// 这是最重要的一条：用户以为改了、产品其实没改，是这类表最危险的静默失败
    /// （早先只查了 vid/pid 全为 0 这一种失配，usage 写错、vid 打错一位、minReportLength 写大
    /// 这类失配一律静默 —— 报告还写"覆盖内置条目（用户条目优先生效）"，看着像生效了）。
    /// </summary>
    public static IReadOnlyList<PeripheralProtocolIssue> ValidateOverrideAgainstDevices(
        IReadOnlyList<PeripheralProtocol> user,
        IReadOnlyList<PeripheralProtocol> builtin,
        IReadOnlyList<PeripheralDeviceKey> devices)
    {
        var issues = new List<PeripheralProtocolIssue>();
        if (user is null || builtin is null) return issues;

        var builtinByKey = new Dictionary<string, PeripheralProtocol>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in builtin)
            if (b?.Key is not null && b.Key.Length > 0) builtinByKey[b.Key] = b;

        foreach (var u in user)
        {
            if (u?.Key is null || u.Key.Length == 0) continue;
            if (!builtinByKey.TryGetValue(u.Key, out PeripheralProtocol? b)) continue;

            var shadowed = new List<PeripheralDeviceKey>();
            var userOnly = new List<PeripheralDeviceKey>();
            var builtinOnly = new List<PeripheralDeviceKey>();
            if (devices is not null)
            {
                foreach (PeripheralDeviceKey d in devices)
                {
                    bool mu = Matches(u, d.Vid, d.Pid, d.UsagePage, d.Usage, d.OutputReportLength);
                    bool mb = Matches(b, d.Vid, d.Pid, d.UsagePage, d.Usage, d.OutputReportLength);
                    if (mu && mb) shadowed.Add(d);
                    else if (mu) userOnly.Add(d);
                    else if (mb) builtinOnly.Add(d);
                }
            }

            if (shadowed.Count > 0)
            {
                issues.Add(new PeripheralProtocolIssue("key", u.Key,
                    "覆盖生效：设备 " + shadowed[0].Describe()
                    + (shadowed.Count > 1 ? " 等 " + shadowed.Count + " 个集合" : "")
                    + " 改用这条用户条目（内置条目被顶掉）", PeripheralProtocolSeverity.Info));
            }
            if (userOnly.Count > 0)
            {
                issues.Add(new PeripheralProtocolIssue("key", u.Key,
                    "用户条目额外匹配了设备 " + userOnly[0].Describe()
                    + "（内置条目匹配不上它 ⇒ 这不是顶替，是新增匹配）",
                    PeripheralProtocolSeverity.Info));
            }
            if (builtinOnly.Count > 0)
            {
                if (userOnly.Count == 0 && shadowed.Count == 0)
                {
                    // 这条用户条目**一条设备都匹配不上**：覆盖彻底落空 —— 这才是"用户以为改了、
                    // 产品其实没改"，必须拦门禁。
                    issues.Add(new PeripheralProtocolIssue("key", u.Key,
                        "覆盖**不生效**：设备 " + builtinOnly[0].Describe()
                        + " 能匹配内置条目，却匹配不上这条用户条目 ⇒ 该设备仍会用内置条目"
                        + "（用户以为改了、产品其实没改）。"
                        + "请把 vid/pid/usagePage/usage/minReportLength 与内置条目对齐"
                        + "（用户表是整条替换、不是字段级合并）"));
                }
                else
                {
                    // 这条用户条目对**某些**设备是生效的（只是没覆盖内置条目能匹配的全部设备）
                    // ⇒ 只生效一部分，不是失败。用中性措辞，别断言"用户以为改了"。
                    issues.Add(new PeripheralProtocolIssue("key", u.Key,
                        "覆盖**只生效一部分**：设备 " + builtinOnly[0].Describe()
                        + " 能匹配内置条目，但这条用户条目匹配不上它 ⇒ 该设备仍会用内置条目；"
                        + "这条用户条目对其它它能匹配的设备是生效的",
                        PeripheralProtocolSeverity.Warning));
                }
            }
            if (shadowed.Count == 0 && userOnly.Count == 0 && builtinOnly.Count == 0)
            {
                issues.Add(new PeripheralProtocolIssue("key", u.Key,
                    "覆盖效果无法在真机上验证：当前没有已连接设备能匹配这条用户条目或内置条目",
                    PeripheralProtocolSeverity.Info));
            }
        }
        return issues;
    }

    /// <summary>
    /// 同 key 的用户条目 vs 内置条目：覆盖关系结论（条件化 —— 不说无根据的话）。
    /// </summary>
    private static IReadOnlyList<PeripheralProtocolIssue> DescribeOverride(
        PeripheralProtocol user, PeripheralProtocol builtin)
    {
        var issues = new List<PeripheralProtocolIssue>();
        PeripheralOverrideRelation relation = CompareOverride(user, builtin);
        string mismatch = DescribeOverrideMismatch(user, builtin);
        switch (relation)
        {
            case PeripheralOverrideRelation.Covers:
                issues.Add(new PeripheralProtocolIssue("key", user.Key,
                    "覆盖内置条目 '" + user.Key + "' **生效**：用户条目的匹配条件与内置条目一致或更宽"
                    + " ⇒ 内置条目能被它顶掉（用户条目先命中先用）",
                    PeripheralProtocolSeverity.Info));
                break;
            case PeripheralOverrideRelation.Partial:
                issues.Add(new PeripheralProtocolIssue("key", user.Key,
                    "覆盖内置条目 '" + user.Key + "' **只生效一部分**：" + mismatch
                    + " ⇒ 只有用户条目也能匹配上的设备会改用用户条目，其余仍走内置条目",
                    PeripheralProtocolSeverity.Warning));
                break;
            default:
                issues.Add(new PeripheralProtocolIssue("key", user.Key,
                    "覆盖内置条目 '" + user.Key + "' **顶不掉**：" + mismatch
                    + " ⇒ 内置条目能匹配的设备仍会用内置条目"
                    + "（这条用户条目若匹配的是**别的**设备，那是新增匹配、不是顶替 —— 真机上会另行判定）"
                    + "（用户表是整条替换、不是字段级合并，要改内置条目请把匹配条件一起写上）",
                    PeripheralProtocolSeverity.Warning));
                break;
        }
        return issues;
    }

    /// <summary>表项的匹配条件摘要（给日志与自检报告看）。</summary>
    public static string MatchSummary(PeripheralProtocol? protocol)
    {
        if (protocol is null) return "(无)";
        string usage = (protocol.UsagePage is { } page ? page.ToString("X4") : "----")
            + ":" + (protocol.Usage is { } u ? u.ToString("X4") : "----");
        return "vid=" + protocol.Vid.ToString("X4")
            + " pid=" + protocol.Pid.ToString("X4")
            + " usage=" + usage
            + " minReportLength=" + protocol.MinReportLength
            + " transport=" + protocol.Transport;
    }

    // ==================== 六、JSON 字段级检查 ====================

    /// <summary>按名字取一个 JSON 属性（大小写不敏感，与装载选项一致）。</summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty prop in element.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = prop.Value;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>逐字段检查一个 JSON 表项（精确到"哪个字段、哪个取值"）。</summary>
    private static IReadOnlyList<PeripheralProtocolIssue> CheckJsonEntry(JsonElement entry, string path)
    {
        var issues = new List<PeripheralProtocolIssue>();
        bool hasKey = false;

        foreach (JsonProperty prop in entry.EnumerateObject())
        {
            string name = prop.Name;
            string field = path + "." + name;
            JsonElement value = prop.Value;

            switch (name.ToLowerInvariant())
            {
                case "key":
                    hasKey = true;
                    if (value.ValueKind != JsonValueKind.String)
                        issues.Add(new PeripheralProtocolIssue(field, value.GetRawText(),
                            "key 必须是字符串"));
                    else if (string.IsNullOrWhiteSpace(value.GetString()))
                        issues.Add(new PeripheralProtocolIssue(field, value.GetRawText(),
                            "key 不能为空（唯一键，也是覆盖内置条目的依据）"));
                    break;

                case "vendor":
                case "model":
                    if (value.ValueKind != JsonValueKind.String)
                        issues.Add(new PeripheralProtocolIssue(field, value.GetRawText(),
                            name + " 必须是字符串"));
                    break;

                case "kind":
                    if (value.ValueKind != JsonValueKind.String)
                        issues.Add(new PeripheralProtocolIssue(field, value.GetRawText(),
                            "kind 必须是字符串"));
                    else if (!KnownKinds.Contains(value.GetString() ?? "", StringComparer.OrdinalIgnoreCase))
                        issues.Add(new PeripheralProtocolIssue(field, value.GetRawText(),
                            "kind 应是 " + string.Join(" / ", KnownKinds) + " 之一",
                            PeripheralProtocolSeverity.Warning));
                    break;

                case "vid":
                    CheckInt(value, field, "vid", 0, 0xFFFF, issues);
                    break;
                case "pid":
                    CheckInt(value, field, "pid", 0, 0xFFFF, issues);
                    break;
                case "usagepage":
                    CheckInt(value, field, "usagePage", 0, 0xFFFF, issues);
                    break;
                case "usage":
                    CheckInt(value, field, "usage", 0, 0xFFFF, issues);
                    break;

                case "minreportlength":
                    CheckInt(value, field, "minReportLength", 0, int.MaxValue, issues);
                    break;

                case "transport":
                    CheckTransport(value, field, issues);
                    break;

                case "precommands":
                    CheckByteSequenceList(value, field, issues);
                    break;

                case "command":
                case "echo":
                    CheckBytes(value, field, issues);
                    break;

                case "featurereportid":
                    CheckInt(value, field, "featureReportId", -1, 255, issues);
                    break;

                case "valueoffset":
                    CheckInt(value, field, "valueOffset", -1, int.MaxValue, issues);
                    break;

                case "valuemask":
                    CheckInt(value, field, "valueMask", 0, 0xFF, issues);
                    break;

                case "valuemin":
                    CheckInt(value, field, "valueMin", 0, 0xFF, issues);
                    break;

                case "valuemax":
                    CheckInt(value, field, "valueMax", 0, 0xFF, issues);
                    break;

                case "chargingoffset":
                    if (value.ValueKind == JsonValueKind.Null) break;
                    CheckInt(value, field, "chargingOffset", 0, int.MaxValue, issues);
                    break;

                case "chargingmask":
                    CheckInt(value, field, "chargingMask", 0, 0xFF, issues);
                    break;

                case "chargingisone":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        issues.Add(new PeripheralProtocolIssue(field, value.GetRawText(),
                            "chargingIsOne 必须是 true / false"));
                    break;

                case "millivoltoffset":
                    if (value.ValueKind == JsonValueKind.Null) break;
                    CheckInt(value, field, "milliVoltsOffset", 0, int.MaxValue, issues);
                    break;

                case "millivoltsmask":
                    CheckInt(value, field, "milliVoltsMask", 0, 0xFFFF, issues);
                    break;

                case "millivoltsscale":
                    CheckInt(value, field, "milliVoltsScale", 1, int.MaxValue, issues);
                    break;

                default:
                    issues.Add(new PeripheralProtocolIssue(field, value.GetRawText(),
                        "未知字段（可能拼错了）—— 协议表不认这个字段，会被静默忽略",
                        PeripheralProtocolSeverity.Warning));
                    break;
            }
        }

        if (!hasKey)
            issues.Add(new PeripheralProtocolIssue(path + ".key", "(缺失)",
                "缺少必填字段 key"));

        return issues;
    }

    /// <summary>
    /// 跨字段检查（不依赖反序列化成功）：量程、掩码这类"两个字段放一起才看得出"的问题。
    /// </summary>
    private static IReadOnlyList<PeripheralProtocolIssue> CheckJsonCrossField(JsonElement entry, string path)
    {
        var issues = new List<PeripheralProtocolIssue>();
        var ints = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty prop in entry.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int v))
                ints[prop.Name] = v;
        }

        // 未出现的字段按记录默认值算（与 PeripheralProtocol 的默认值一致），
        // 否则"字段没写"会被误判成"字段写了非法值"（早期就踩过这个误报）。
        int valueOffset = ints.TryGetValue("valueOffset", out int vo) ? vo : -1;
        int valueMin = ints.TryGetValue("valueMin", out int vmin) ? vmin : 0;
        int valueMax = ints.TryGetValue("valueMax", out int vmax) ? vmax : 100;
        int valueMask = ints.TryGetValue("valueMask", out int vmask) ? vmask : 0xFF;
        bool hasChargingOffset = ints.TryGetValue("chargingOffset", out _);
        int chargingMask = ints.TryGetValue("chargingMask", out int cmask) ? cmask : 1;
        bool hasMilliVoltsOffset = ints.TryGetValue("milliVoltsOffset", out _);
        int mvMask = ints.TryGetValue("milliVoltsMask", out int mvm) ? mvm : 0xFFFF;
        int mvScale = ints.TryGetValue("milliVoltsScale", out int mvs) ? mvs : 1;

        // 电量字段（valueOffset ≥ 0 时才有意义；-1 = 本协议不报电量）
        if (valueOffset >= 0)
        {
            if (valueMax <= valueMin)
            {
                issues.Add(new PeripheralProtocolIssue(path + ".valueMax/valueMin",
                    valueMax.ToString(CultureInfo.InvariantCulture) + " / "
                        + valueMin.ToString(CultureInfo.InvariantCulture),
                    "valueMax 必须大于 valueMin（否则百分比换算的除数为 0 或负数）"));
            }
            if (valueMask == 0)
            {
                issues.Add(new PeripheralProtocolIssue(path + ".valueMask", "0",
                    "valueMask 不能为 0（取位后恒为 0，电量会永远报成 0%）"));
            }
        }

        // 充电位（只有声明了 chargingOffset 才谈得上掩码）
        if (hasChargingOffset && chargingMask != 1)
        {
            issues.Add(new PeripheralProtocolIssue(path + ".chargingMask",
                chargingMask.ToString(CultureInfo.InvariantCulture),
                "chargingMask 必须是 1（0x01）：充电位只认明确的 0/1；0 会永远读成「未充电」（谎报），"
                    + "大于 1 会取到 2/3 让整帧作废"));
        }

        // 电压字段（只有声明了 milliVoltsOffset 才谈得上掩码；缩放系数无条件校验）
        if (hasMilliVoltsOffset && mvMask == 0)
        {
            issues.Add(new PeripheralProtocolIssue(path + ".milliVoltsMask", "0",
                "milliVoltsMask 不能为 0（电压会恒为 0，等于报假电压）"));
        }
        if (mvScale <= 0)
        {
            issues.Add(new PeripheralProtocolIssue(path + ".milliVoltsScale",
                mvScale.ToString(CultureInfo.InvariantCulture),
                "milliVoltsScale 必须 ≥ 1（0 会让电压恒为 0，等于报假电压）"));
        }

        return issues;
    }

    private static void CheckInt(JsonElement value, string field, string name, int min, int max,
        List<PeripheralProtocolIssue> issues)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            issues.Add(new PeripheralProtocolIssue(field, value.GetRawText(),
                name + " 必须是整数（实际是 " + value.ValueKind + "）"));
            return;
        }
        if (!value.TryGetInt32(out int parsed))
        {
            issues.Add(new PeripheralProtocolIssue(field, value.GetRawText(),
                name + " 不是 32 位整数"));
            return;
        }
        if (parsed < min || parsed > max)
            issues.Add(new PeripheralProtocolIssue(field, value.GetRawText(),
                name + " = " + parsed.ToString(CultureInfo.InvariantCulture)
                    + " 超出允许范围 " + min + ".." + max));
    }

    private static void CheckTransport(JsonElement value, string field,
        List<PeripheralProtocolIssue> issues)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetInt32(out int parsed))
            {
                issues.Add(new PeripheralProtocolIssue(field, value.GetRawText(), "transport 不是 32 位整数"));
                return;
            }
            if (parsed < 0 || parsed > 2)
                issues.Add(new PeripheralProtocolIssue(field, value.GetRawText(),
                    "transport = " + parsed + " 不是合法的传输方式（0=Control / 1=Interrupt / 2=Feature）"));
            return;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            issues.Add(new PeripheralProtocolIssue(field, value.GetRawText(),
                "transport 必须是字符串名（Control / Interrupt / Feature）或整数 0..2"));
            return;
        }
        string text = value.GetString() ?? "";
        if (!Enum.TryParse(text, ignoreCase: true, out PeripheralTransport parsed2)
            || !Enum.IsDefined(typeof(PeripheralTransport), parsed2))
        {
            issues.Add(new PeripheralProtocolIssue(field, value.GetRawText(),
                "transport 不是合法的传输方式（Control / Interrupt / Feature）"));
        }
    }

    /// <summary>字节序列列表（preCommands 是 byte[][]）：每个元素都要是一条合法的字节序列。</summary>
    private static void CheckByteSequenceList(JsonElement value, string field,
        List<PeripheralProtocolIssue> issues)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new PeripheralProtocolIssue(field, value.GetRawText(),
                "这个字段要写成数组（每条命令一个十六进制字符串，如 [ \"05 01\" ]）"));
            return;
        }
        int index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            CheckBytes(item, field + "[" + index + "]", issues);
            index++;
        }
    }

    private static void CheckBytes(JsonElement value, string field,
        List<PeripheralProtocolIssue> issues)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number)
                {
                    issues.Add(new PeripheralProtocolIssue(field, item.GetRawText(),
                        "字节数组里只能放 0..255 的整数（实际是 " + item.ValueKind + "）"));
                    continue;
                }
                if (!item.TryGetInt32(out int b) || b < 0 || b > 255)
                    issues.Add(new PeripheralProtocolIssue(field, item.GetRawText(),
                        "字节值必须是 0..255"));
            }
            return;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            issues.Add(new PeripheralProtocolIssue(field, value.GetRawText(),
                "字节字段要写成十六进制字符串（如 「C1 02 3A」）或数字数组"));
            return;
        }
        string text = value.GetString() ?? "";
        foreach (string part in text.Split(new[] { ' ', ',', '-', ':' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b)
                || b < 0 || b > 0xFF)
            {
                issues.Add(new PeripheralProtocolIssue(field, part,
                    "不是合法的十六进制字节（每个字节都要是 00..FF）"));
            }
        }
    }
}
