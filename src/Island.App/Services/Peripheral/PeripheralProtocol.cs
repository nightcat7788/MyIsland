using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Island.App.Services.Peripheral;

/// <summary>
/// 一家厂商/型号的私有电量协议描述 —— **纯数据**，不写任何设备操作。
///
/// 新增一个品牌 = 往表里加一条记录，不写新的 Provider、不改 UI。
/// 字段语义：
///  · Echo            回包里必须出现的字节序列（回显校验）。空 = 不校验。
///  · ValueOffset     电量字段在回包里的偏移（相对回包首字节，**首字节是报告 ID**）。
///  · ValueMask/Range 取值后落在 [ValueMin, ValueMax] 才算有效（范围校验）。
///  · ChargingOffset  充电位所在偏移；只认明确的 0/1，其它值当"未知"（按未充电显示）。
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
}

/// <summary>
/// 协议表：内置条目 + 用户从 %LOCALAPPDATA%\MyIsland\peripheral-protocols.json 追加的条目。
/// </summary>
public static class PeripheralProtocols
{
    private static readonly List<PeripheralProtocol> Builtin = new();
    private static readonly List<PeripheralProtocol> User = new();
    private static readonly object Gate = new();

    static PeripheralProtocols()
    {
        Builtin.AddRange(BuiltinEntries());
    }

    /// <summary>表里所有条目（内置在前，用户追加的在后）。</summary>
    public static IReadOnlyList<PeripheralProtocol> All
    {
        get { lock (Gate) return Builtin.Concat(User).ToList(); }
    }

    /// <summary>追加用户条目（同 Key 的覆盖旧的）。</summary>
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

    /// <summary>从 JSON 文本装载协议表（用户填表用的入口）。</summary>
    public static List<PeripheralProtocol> LoadFromJson(string json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new HexBytesConverter(), new PeripheralTransportConverter() },
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
        };
    }
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
        if (protocol.Echo.Length > 0 && !ContainsSequence(response, protocol.Echo)) return false;

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
            int bit = response[chargingOffset] & protocol.ChargingMask;
            if (bit is not (0 or 1)) return false;
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
