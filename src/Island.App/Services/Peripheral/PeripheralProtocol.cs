using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Island.App.Services.Peripheral;

/// <summary>
/// 一条表项的**验证状态** —— 这条协议到底有没有在真机上验过。
///
/// 为什么要有这个字段：协议表是纯数据，谁都能往里加一条。加错一条的后果是
/// 向用户输出一个**假读数**（本项目最硬的红线：不伪装）。所以每条表项都必须
/// 自报家门 —— 验过的写"已实机验证"并留下证据（设备 / 日期 / 说明），
/// 没验过的老实写"未验证"，绝不能冒充已支持。
/// </summary>
public enum PeripheralVerification
{
    /// <summary>未验证：没有在真机上回包确认过（**内置表不允许**出现这种条目）。</summary>
    Unverified = 0,

    /// <summary>已实机验证：真机回过包、读数对得上（内置表只收这种条目）。</summary>
    Verified = 1,
}

/// <summary>
/// 验证状态的 JSON 转换：字符串名（**大小写不敏感**，也认中文"已实机验证" / "未验证"）或整数都认。
/// 未知取值直接抛异常（带出写错的值）—— 用户手填的表里一个错值不该被静默当成
/// "未验证"，那会让一条没验证过的表项悄悄混进用户表。
/// </summary>
public sealed class PeripheralVerificationConverter : JsonConverter<PeripheralVerification>
{
    public override PeripheralVerification Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            int raw = reader.GetInt32();
            if (raw == 0) return PeripheralVerification.Unverified;
            if (raw == 1) return PeripheralVerification.Verified;
            throw new JsonException("未知的验证状态数值：" + raw);
        }
        string? text = reader.GetString();
        if (!string.IsNullOrEmpty(text))
        {
            string t = text.Trim();
            if (t.Equals("verified", StringComparison.OrdinalIgnoreCase)
                || t == "已实机验证" || t == "已验证" || t == "实机验证")
                return PeripheralVerification.Verified;
            if (t.Equals("unverified", StringComparison.OrdinalIgnoreCase)
                || t == "未验证" || t == "未实机验证")
                return PeripheralVerification.Unverified;
            throw new JsonException("未知的验证状态：" + text);
        }
        throw new JsonException("验证状态不能为空（写 \"verified\" 或 \"unverified\"）");
    }

    public override void Write(Utf8JsonWriter writer, PeripheralVerification value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value == PeripheralVerification.Verified ? "verified" : "unverified");
    }
}

/// <summary>
/// 一家厂商/型号的私有电量协议描述 —— **纯数据**，不写任何设备操作。
///
/// 新增一个品牌 = 往表里加一条记录，不写新的 Provider、不改 UI。
/// 字段语义：
///  · Echo            回包里必须出现的字节序列（回显校验）。空 = 不校验。
///  · ValueOffset     电量字段在回包里的偏移（相对回包首字节，**首字节是报告 ID**）。
///  · ValueMask/Range 取值后落在 [ValueMin, ValueMax] 才算有效（范围校验）。
///  · ChargingOffset  充电位所在偏移；只认明确的 0/1，其它值当"未知"（按未充电显示）。
///  · Verification    这条协议**有没有在真机上验过**（见 <see cref="PeripheralVerification"/>）。
///    没验过的条目不得进内置表（内置表只装载已实机验证的条目）—— 绝不能输出没验证过的读数。
/// </summary>
public sealed record PeripheralProtocol
{
    /// <summary>表的唯一键（也用作诊断日志里的标识）。</summary>
    public required string Key { get; init; }

    /// <summary>厂商名（界面显示用）。</summary>
    public string Vendor { get; init; } = "";

    /// <summary>型号名（界面显示用）。</summary>
    public string Model { get; init; } = "";

    /// <summary>设备类型：mouse / keyboard / headset / gamepad / other。</summary>
    public string Kind { get; init; } = "other";

    /// <summary>USB 厂商 ID。</summary>
    public int Vid { get; init; }

    /// <summary>USB 产品 ID。</summary>
    public int Pid { get; init; }

    /// <summary>控制集合的 usage page；null = 不限。</summary>
    public ushort? UsagePage { get; init; }

    /// <summary>控制集合的 usage；null = 不限。</summary>
    public ushort? Usage { get; init; }

    /// <summary>集合 output report 的最小长度（不够长就不可能是这个协议）。</summary>
    public int MinReportLength { get; init; }

    /// <summary>用哪种传输方式发命令。</summary>
    public PeripheralTransport Transport { get; init; } = PeripheralTransport.Control;

    /// <summary>正式查询前要先发的握手命令（可为空）。</summary>
    public byte[][] PreCommands { get; init; } = Array.Empty<byte[]>();

    /// <summary>电量查询命令（完整帧，首字节是报告 ID）。</summary>
    public byte[] Command { get; init; } = Array.Empty<byte>();

    /// <summary>回包里必须出现的字节序列（回显校验）。</summary>
    public byte[] Echo { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// 直读模式：≥ 0 表示"直接读这个 Feature 报告并解析"，不发电量命令。
    /// 不少厂商把配对设备的状态挂在 Feature 报告里（联想 Idea5003 就是这样），
    /// 这类设备不需要握手也不回包 —— 没有这个字段就永远读不到。
    /// </summary>
    public int FeatureReportId { get; init; } = -1;

    /// <summary>电量字段偏移；负数 = 这条协议不报电量。</summary>
    public int ValueOffset { get; init; } = -1;

    /// <summary>电量字段的掩码（取位后再比范围）。</summary>
    public int ValueMask { get; init; } = 0xFF;

    /// <summary>电量原始值的下界。</summary>
    public int ValueMin { get; init; }

    /// <summary>电量原始值的上界。</summary>
    public int ValueMax { get; init; } = 100;

    /// <summary>充电位偏移；null = 协议不报充电状态。</summary>
    public int? ChargingOffset { get; init; }

    /// <summary>充电位的掩码（通常是 0x01）。</summary>
    public int ChargingMask { get; init; } = 0x01;

    /// <summary>充电位为 1 时算"在充电"（反过来就是 0 时算在充电）。</summary>
    public bool ChargingIsOne { get; init; } = true;

    /// <summary>电压字段偏移（大端 16 位）；null = 不报电压。</summary>
    public int? MilliVoltsOffset { get; init; }

    /// <summary>电压字段掩码。</summary>
    public int MilliVoltsMask { get; init; } = 0xFFFF;

    /// <summary>电压换算系数（原始值 × 系数 = 毫伏）。</summary>
    public int MilliVoltsScale { get; init; } = 1;

    // ==================== 验证状态（2026-11-26 task6 新增）====================
    // 表是纯数据，谁都能加一条。加错一条 = 向用户输出假读数。
    // 所以每条表项都必须自报家门：验过没验过、什么时候在哪台设备上验的。

    /// <summary>
    /// 验证状态：这条协议**有没有在真机上验过**（见 <see cref="PeripheralVerification"/>）。
    /// 默认"未验证" —— 没验过就必须这么写，绝不能默认成"已验证"。
    /// 内置表只装载"已实机验证"的条目（见 <see cref="PeripheralProtocols.IsBuiltInEligible"/>）。
    /// </summary>
    public PeripheralVerification Verification { get; init; } = PeripheralVerification.Unverified;

    /// <summary>验证证据：什么时候、在哪台设备上、回包读到什么（写给后来人看）。</summary>
    public string VerificationNote { get; init; } = "";

    /// <summary>验证日期（YYYY-MM-DD）；空 = 没记。</summary>
    public string VerifiedOn { get; init; } = "";

    /// <summary>验证时用的设备型号（在哪台机器上验的）。</summary>
    public string VerifiedDevice { get; init; } = "";

    // ==================== 表达能力扩展（2026-11-26 task6 新增）====================
    // 原来表达不了的两种常见协议写法，现在能表达了（详见 docs/peripheral-protocols.md）。

    /// <summary>
    /// 回显字节必须出现在回包的**这个偏移**（-1 = 不限位置）。
    /// 有些协议（罗技 HID++ 这类）回包里在固定位置回显特征号 / 功能号；
    /// 不限定位置的话，回包里别处碰巧出现同样的字节会被当成回显 ⇒ 认错回包。
    /// </summary>
    public int EchoOffset { get; init; } = -1;

    /// <summary>
    /// 充电位在 <see cref="ChargingOffset"/> 那一字节里的**位号**（0..7；-1 = 走 chargingMask 老路）。
    /// 充电位常常不是字节最低位（HID++ 的统一电池状态里充电标志在第 4 位），
    /// 没有这个字段就表达不出来（chargingMask 只认 0x01 那一位）。
    /// </summary>
    public int ChargingBitOffset { get; init; } = -1;
}

/// <summary>
/// 协议表：内置条目 + 用户从 %LOCALAPPDATA%\MyIsland\peripheral-protocols.json 追加的条目。
/// **用户条目优先**（同 Key 时覆盖内置条目）—— 用户表是唯一的纠错通道，不能是死路。
/// </summary>
public static class PeripheralProtocols
{
    private static readonly List<PeripheralProtocol> Builtin = new();
    private static readonly List<PeripheralProtocol> DeclaredBuiltin = new();
    private static readonly List<PeripheralProtocol> User = new();
    private static readonly object Gate = new();

    static PeripheralProtocols()
    {
        foreach (PeripheralProtocol entry in BuiltinEntries())
        {
            DeclaredBuiltin.Add(entry);
            // 内置表**只装载已实机验证的条目**：没验过的协议即便字段都写对了，
            // 偏移也可能只是猜的 ⇒ 假读数（违反"不伪装"）。
            // 这里**不装载**（设备回落到 L4 兜底、显示"未知"），而不是"照用再说"。
            if (IsBuiltInEligible(entry)) Builtin.Add(entry);
        }
    }

    /// <summary>内置表**声明**的全部条目（含没通过装载资格的）—— 自检与门禁看这个。</summary>
    public static IReadOnlyList<PeripheralProtocol> DeclaredBuiltinTable => DeclaredBuiltin;

    /// <summary>内置表**实际装载**的条目（只含已实机验证的）。</summary>
    public static IReadOnlyList<PeripheralProtocol> BuiltinTable => Builtin;

    /// <summary>
    /// 一条内置表项能不能装载：**必须已实机验证**。
    /// 没验证过的协议即便字段都写对了，电量偏移也可能只是猜的 ⇒ 假读数。
    /// </summary>
    public static bool IsBuiltInEligible(PeripheralProtocol? protocol)
        => protocol is not null && protocol.Verification == PeripheralVerification.Verified;

    /// <summary>
    /// 生效的协议表：**用户条目在前**，内置条目在后。
    ///
    /// 顺序就是覆盖规则（见 <see cref="Match"/>）：用户条目先被匹配到 ⇒ 用户表覆盖内置表。
    /// </summary>
    public static IReadOnlyList<PeripheralProtocol> All
    {
        get { lock (Gate) return User.Concat(Builtin).ToList(); }
    }

    /// <summary>
    /// 追加用户条目（同 Key 的覆盖旧的**用户**条目）。
    /// 内置条目不删：用户条目匹配不上时还要回退到内置条目（见 <see cref="Match"/>）。
    /// </summary>
    public static void AddUserProtocols(IEnumerable<PeripheralProtocol> protocols)
    {
        lock (Gate)
        {
            foreach (var p in protocols)
            {
                User.RemoveAll(x => string.Equals(x.Key, p.Key, StringComparison.OrdinalIgnoreCase));
                User.Add(p);
            }
        }
    }

    /// <summary>清空用户条目（内置条目不动）。</summary>
    public static void ClearUserProtocols()
    {
        lock (Gate) User.Clear();
    }

    /// <summary>
    /// 按 VID/PID/集合/报告长度匹配一条协议。匹配不上返回 null（走 L4 兜底）。
    ///
    /// **覆盖规则（设计文档 §8.5 的纠错通道，靠这条顺序兑现）**：
    ///  · 匹配顺序是 用户表 → 内置表（<see cref="All"/> 里用户条目在前）：
    ///    所以同 Key 的用户条目会被先匹配到 ⇒ **用户条目覆盖内置条目**。
    ///    协议表是"偏移 5 推断错了就改成 12，不用改代码、不用发版"的那条通道，
    ///    顺序反了这条承诺就是死的。
    ///  · 用户条目字段不全（比如只写了 key 与 valueOffset，VID/PID 还是 0）导致匹配不上时，
    ///    仍然回退内置条目 —— 不因为一条写歪的用户条目就把设备从列表里弄丢。
    /// </summary>
    public static PeripheralProtocol? Match(int vid, int pid, ushort usagePage, ushort usage,
        int outputReportLength)
    {
        foreach (var p in All)
        {
            if (p.Vid != vid || p.Pid != pid) continue;
            if (p.UsagePage is { } page && page != usagePage) continue;
            if (p.Usage is { } u && u != usage) continue;
            if (outputReportLength < p.MinReportLength) continue;
            return p;
        }
        return null;
    }

    /// <summary>
    /// 协议命中时界面显示的名字：厂商 + 型号；缺哪个用哪个，都缺就退回设备本身的名字。
    /// 两端共用（Windows 与 mac 显示同一个名字，列表才对得上）。
    /// </summary>
    public static string DisplayName(PeripheralProtocol protocol, string fallback)
    {
        if (protocol is null) return fallback;
        if (protocol.Vendor.Length > 0 && protocol.Model.Length > 0) return protocol.Vendor + " " + protocol.Model;
        if (protocol.Model.Length > 0) return protocol.Model;
        if (protocol.Vendor.Length > 0) return protocol.Vendor;
        return fallback;
    }

    /// <summary>
    /// 从用户文件重新装载协议表（%LOCALAPPDATA%\MyIsland\peripheral-protocols.json 等）。
    /// 先解析再替换：文件坏了就**保留旧表**，不能把能用的配置弄没。
    /// 返回本次装载的条数（0 = 没有文件 / 文件为空）。
    /// </summary>
    public static int ReloadUserTableFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        try
        {
            if (!File.Exists(path)) return 0;
            string json = File.ReadAllText(path);
            var protocols = LoadFromJson(json);
            if (protocols.Count == 0) return 0;
            ClearUserProtocols();
            AddUserProtocols(protocols);
            return protocols.Count;
        }
        catch
        {
            // 文件坏了：保留旧表（ClearUserProtocols 不该在解析失败时执行）
            return 0;
        }
    }

    /// <summary>从 JSON 文本装载协议表（用户填表用的入口）。</summary>
    public static List<PeripheralProtocol> LoadFromJson(string json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters =
            {
                new HexBytesConverter(),
                new PeripheralTransportConverter(),
                new PeripheralVerificationConverter(),
            },
        };
        return JsonSerializer.Deserialize<List<PeripheralProtocol>>(json, options) ?? new List<PeripheralProtocol>();
    }

    /// <summary>内置表。新增品牌就往这里加一条。</summary>
    private static IEnumerable<PeripheralProtocol> BuiltinEntries()
    {
        yield return new PeripheralProtocol
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
            Transport = PeripheralTransport.Feature,
            // 直读特征报告 193（0xC1）：偏移 0 是报告 ID，偏移 5 是电量百分比。
            FeatureReportId = 193,
            ValueOffset = 5,
            ValueMask = 0xFF,
            ValueMin = 0,
            ValueMax = 100,
            // ---- 验证状态：已实机验证（2026-11-26）----
            // 证据等级是**推断值**：偏移 5 读出 58%，依据是同型号蓝牙鼠标系统缓存 59%、
            // 差 1pp、闲置衰减一致；**未做同刻交叉验证**（没在同一时刻对两个来源）。
            Verification = PeripheralVerification.Verified,
            VerifiedOn = "2026-11-26",
            VerifiedDevice = "联想 Idea5003 2.4G 接收器（VID 17EF / PID F006）",
            VerificationNote = "实机读出 58%（**推断值**：偏移 5，依据同型号蓝牙鼠标系统缓存 59%、"
                + "差 1pp、闲置衰减一致；未做同刻交叉验证）。",
        };
    }
}

/// <summary>
/// **候选表项** —— 别人家的 2.4G 接收器怎么读电量。
///
/// 这些条目**不是"内置已支持"**：本机只有一台联想 Idea5003 接收器，
/// 罗技 / 雷蛇 / VGN 等接收器**没有在本机实机回包验证过**，所以它们一律是
/// "未验证"状态，**不参与匹配、不参与读数**
/// （<see cref="PeripheralProtocols.All"/> 与 <see cref="PeripheralProtocols.BuiltinTable"/> 里都没有它们）。
///
/// 它们的用途是给用户当**模板**：照着填成自己的用户表 JSON，
/// 在**有那台设备**的机器上用 <c>hid-probe --selftest</c> 实机验证，
/// 验证通过再把 verification 改成 "verified"。
///
/// 表项里的 vid / pid / 偏移都是**探测起点，不是结论** —— 必须实机确认。
/// 填写与验证流程见 docs/peripheral-protocols.md。
/// </summary>
public static class PeripheralProtocolCandidates
{
    /// <summary>候选表项（一律"未验证"，不进匹配表）。</summary>
    public static IReadOnlyList<PeripheralProtocol> All => Candidates;

    private static readonly List<PeripheralProtocol> Candidates = new()
    {
        // ---- 罗技：HID++ 2.0 统一电池状态（vendor feature report 0x11）----
        new PeripheralProtocol
        {
            Key = "logitech-hidpp-battery",
            Vendor = "罗技",
            Model = "HID++ 接收器（Unifying / Bolt 等）",
            Kind = "other",
            Vid = 0x046D,
            Pid = 0,          // ← 占位：接收器型号不同 PID 不同，必须用 hid-probe scan 填真实值
            UsagePage = null,
            Usage = null,
            MinReportLength = 16,
            Transport = PeripheralTransport.Feature,
            // HID++ 短报告（16 字节）：报告 ID 0x11 + 设备地址 + 特征索引 + 功能号 0x0F（电池状态）。
            // 这是 HID++ 公开协议里**请求帧**的写法；回包里电池字节的位置**没有确认**（见下面说明）。
            Command = new byte[]
            {
                0x11, 0x03, 0x00, 0x0F, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            },
            // 回包在固定位置回显特征索引 + 功能号 ⇒ 用 echoOffset 精确卡住位置。
            Echo = new byte[] { 0x00, 0x0F },
            EchoOffset = 2,
            // 电量字节位置**待实机探测**：-1 = 还没定（照着填会读到"未知"，不会读出假读数）。
            ValueOffset = -1,
            ValueMask = 0x0F,
            ValueMin = 0,
            ValueMax = 4,
            Verification = PeripheralVerification.Unverified,
            VerificationNote = "候选：HID++ 2.0 公开协议的请求帧写法（**未实机验证**）。"
                + "回包里的电池字节位置与档位量程（这里按 0..4 档位写）必须用 hid-probe 实机确认；"
                + "不同固件的特征索引也可能不同。",
        },
        // ---- 雷蛇：8 字节私有报告（骨架，命令帧与电量偏移都要靠探测）----
        new PeripheralProtocol
        {
            Key = "razer-8byte-battery",
            Vendor = "雷蛇",
            Model = "2.4G 接收器（8 字节私有报告）",
            Kind = "other",
            Vid = 0x1532,
            Pid = 0,          // ← 占位：必须用 hid-probe scan 填真实值
            UsagePage = null,
            Usage = null,
            MinReportLength = 8,
            Transport = PeripheralTransport.Control,
            // 命令帧与回包偏移**都还没探测出来**：空 = 这条表项现在读不到任何东西
            // （照抄进用户表也不会读出假读数，只会显示"未知"）。
            Command = Array.Empty<byte>(),
            ValueOffset = -1,
            ValueMask = 0xFF,
            ValueMin = 0,
            ValueMax = 100,
            Verification = PeripheralVerification.Unverified,
            VerificationNote = "候选：**纯骨架**，命令帧与电量偏移都未确认。"
                + "先用 hid-probe probe / watch 把回包原样抓下来，确认哪个字节是电量后再填。",
        },
    };
}

/// <summary>
/// 协议回包解析器：三层校验（回显 → 范围 → 充电位），任何一层不过就当"读不到"。
/// 校验不过绝不返回 0 或默认值充数。
/// </summary>
public static class PeripheralProtocolReader
{
    /// <summary>
    /// 解析一个回包。成功返回 true 并给出读数；任何校验不过返回 false。
    /// </summary>
    public static bool TryParse(PeripheralProtocol protocol, byte[] response, out PeripheralReading? reading)
    {
        reading = null;
        if (protocol is null || response is null || response.Length == 0) return false;

        // ① 回显匹配：回包里必须出现协议声明的字节序列
        //    指定了 echoOffset 就必须**精确落在那个位置**（别处碰巧出现同样的字节不算回显）。
        if (protocol.Echo.Length > 0)
        {
            if (protocol.EchoOffset >= 0)
            {
                if (protocol.EchoOffset + protocol.Echo.Length > response.Length) return false;
                for (int j = 0; j < protocol.Echo.Length; j++)
                    if (response[protocol.EchoOffset + j] != protocol.Echo[j]) return false;
            }
            else if (!ContainsSequence(response, protocol.Echo)) return false;
        }

        // ② 电量字段：偏移必须在回包内，取值要落在声明的范围内
        if (protocol.ValueOffset < 0 || protocol.ValueOffset >= response.Length) return false;
        if (protocol.ValueMax <= protocol.ValueMin) return false;
        int raw = response[protocol.ValueOffset] & protocol.ValueMask;
        if (raw < protocol.ValueMin || raw > protocol.ValueMax) return false;
        int percent = (int)((raw - protocol.ValueMin) * 100L / (protocol.ValueMax - protocol.ValueMin));
        if (percent < 0 || percent > 100) return false;

        // ③ 充电位：只认明确的 0/1，其它值当未知（按未充电显示，不猜）
        bool charging = false;
        if (protocol.ChargingOffset is { } chargingOffset)
        {
            if (chargingOffset < 0 || chargingOffset >= response.Length) return false;
            int bit;
            if (protocol.ChargingBitOffset >= 0)
            {
                // 位号模式：只认这一位 ⇒ 取值必是 0/1，不存在"猜充电状态"的问题。
                if (protocol.ChargingBitOffset > 7) return false;
                bit = (response[chargingOffset] >> protocol.ChargingBitOffset) & 1;
            }
            else
            {
                bit = response[chargingOffset] & protocol.ChargingMask;
                if (bit is not (0 or 1)) return false;
            }
            charging = protocol.ChargingIsOne ? bit == 1 : bit == 0;
        }

        int? milliVolts = null;
        if (protocol.MilliVoltsOffset is { } mvOffset)
        {
            if (mvOffset < 0 || mvOffset + 1 >= response.Length) return false;
            milliVolts = (((response[mvOffset] << 8) | response[mvOffset + 1]) & protocol.MilliVoltsMask)
                * protocol.MilliVoltsScale;
        }

        reading = new PeripheralReading(percent, charging, milliVolts);
        return true;
    }

    /// <summary>子序列匹配（回显校验）。</summary>
    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return true;
        if (needle.Length > haystack.Length) return false;
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { hit = false; break; }
            }
            if (hit) return true;
        }
        return false;
    }
}

/// <summary>
/// 传输方式的 JSON 转换：字符串名（**大小写不敏感**）或整数都认。
/// 系统自带的枚举转换器读字符串是大小写敏感的，用户手写表里一个 "interrupt" 就会整表装载失败 ——
/// 协议表是给人手填的，不能这样。
/// </summary>
public sealed class PeripheralTransportConverter : JsonConverter<PeripheralTransport>
{
    public override PeripheralTransport Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            int raw = reader.GetInt32();
            if (Enum.IsDefined(typeof(PeripheralTransport), raw)) return (PeripheralTransport)raw;
            throw new JsonException("未知的传输方式数值：" + raw);
        }
        string? text = reader.GetString();
        if (!string.IsNullOrEmpty(text)
            && Enum.TryParse(text, ignoreCase: true, out PeripheralTransport parsed)
            && Enum.IsDefined(typeof(PeripheralTransport), parsed))
            return parsed;
        throw new JsonException("未知的传输方式：" + text);
    }

    public override void Write(Utf8JsonWriter writer, PeripheralTransport value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// byte[] ↔ 十六进制字符串互转，让协议表 JSON 人能读能改
/// （"C1 02 3A" 与 [193, 2, 58] 两种写法都认）。
/// </summary>
public sealed class HexBytesConverter : JsonConverter<byte[]>
{
    public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<byte>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                list.Add((byte)reader.GetInt32());
            return list.ToArray();
        }
        string text = reader.GetString() ?? "";
        var bytes = new List<byte>();
        foreach (string part in text.Split(new[] { ' ', ',', '-', ':' }, StringSplitOptions.RemoveEmptyEntries))
            bytes.Add(byte.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return bytes.ToArray();
    }

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(string.Join(" ", value.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))));
    }
}

/// <summary>
/// HID 能力表（value caps）的"能不能认"判定 —— 纯逻辑，无 Win32 依赖，
/// 原生层（<c>HidNative.ReadValueCaps</c>）与单元测试共用同一套规则。
///
/// 为什么单独拎出来（都是实机踩出来的）：
///  · <c>HidP_GetValueCaps</c> 的返回值是 **NTSTATUS**（实测：成功 0x00110000、
///    失败 0xC0110004），**不是**"取到的条数"；条数由 length 出参回写。
///  · 失败时驱动**可能不回写 length** ⇒ length 保持调用方给的 capacity。
///    若照着 capacity 解析，就会把 <c>Marshal.AllocHGlobal</c>（**不清零**）的内存
///    当成 caps；里面若恰好有一条命中电量 usage，它的 LogicalMin/Max 就是垃圾值
///    ⇒ 可能算出**假百分比**（违反"不伪装"底线）。
/// 所以判定顺序必须是：状态码成功 → 条数合理（0 &lt; length ≤ capacity）→ 才解析。
/// </summary>
public static class HidValueCapsGuard
{
    /// <summary><c>HidP_GetValueCaps</c> 的成功状态码（NTSTATUS 0x00110000）。</summary>
    public const int StatusSuccess = 0x00110000;

    /// <summary>状态码是否表示成功。</summary>
    public static bool IsSuccess(int status) => status == StatusSuccess;

    /// <summary>回写的条数是否在合理区间（必须落在缓冲容量之内）。</summary>
    public static bool IsUsableLength(int length, int capacity) => length > 0 && length <= capacity;

    /// <summary>
    /// 可以放心解析的条数：状态码失败、或条数不合理（没回写 / 超出容量）一律返回 0
    /// （一条都不认 —— 宁可不报，也不拿未初始化内存充数）。
    /// </summary>
    public static int UsableLength(int status, int length, int capacity)
        => IsSuccess(status) && IsUsableLength(length, capacity) ? length : 0;
}
