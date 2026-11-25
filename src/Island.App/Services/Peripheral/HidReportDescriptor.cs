namespace Island.App.Services.Peripheral;

/// <summary>报告描述符里的一条字段。</summary>
/// <param name="ReportId">报告 ID；描述符里没声明 Report ID 的报告是 0。</param>
/// <param name="ReportType">0=Input，1=Output，2=Feature。</param>
/// <param name="IsRange">Usage 是范围（Usage Minimum/Maximum）时为 true。</param>
/// <param name="BitOffset">字段在报告数据里的起始位偏移（**相对报告数据，不含报告 ID 字节**）。</param>
/// <param name="BitSize">字段位宽。</param>
/// <param name="LogicalMin">逻辑最小值；描述符没声明时为 0。</param>
/// <param name="LogicalMax">逻辑最大值；描述符没声明时为 0（= 未知，不许猜）。</param>
public sealed record HidField(
    byte ReportId, byte ReportType,
    ushort UsagePage, ushort Usage, bool IsRange,
    ushort UsageMin, ushort UsageMax,
    int BitOffset, int BitSize, int LogicalMin, int LogicalMax);

/// <summary>
/// 最小 HID 报告描述符解析器 —— **纯逻辑，无 Win32 依赖**，Windows 与 macOS 两端共用同一份。
///
/// 为什么需要它：macOS 没有 <c>HidP_GetCaps</c> / <c>HidP_GetUsageValue</c>，
/// 只能自己解析报告描述符，找到 Feature 报告里的电量字段
/// （Usage Page 0x85 / Usage 0x0065 等），按位取出原始值，
/// 再交给 <see cref="StandardHidBattery.ToPercent"/> 按 LogicalMin/Max 换算百分比。
///
/// 编码依据 USB HID 规范 1.11 §6.2.2：
///  · 前缀字节 = bTag(bit7-4) | bType(bit3-2) | bSize(bit1-0)；
///    bType：0=Main、1=Global、2=Local；bSize：0=0 字节、1=1 字节、2=2 字节、3=4 字节。
///  · 长项：bTag=0xF 且 bType=3（保留）⇒ 前缀 0xFE / 0xFF，后跟 1 字节长度 + 该长度的数据，
///    整项跳过（长项不定义数据字段，不占报告位）。
///
/// Usage Page 有三种写法，都要认（否则字段会挂到上一个 Usage Page 上，
/// FindFeatureField 就找不到标准电量 usage）：
///  · Global tag 0x0 —— 持续生效，覆盖后续所有 Main 项；
///  · Local  tag 0x4 —— 只对下一个 Main 数据项生效；
///  · Main   tag 0x5 —— 只对下一个 Main 数据项生效。
///
/// 底线（与仓库其它共享层一致）：**不伪装**。
/// 描述符没声明 Logical Maximum ⇒ <see cref="HidField.LogicalMax"/> = 0（= 未知），
/// 上层 <c>ToPercent</c> 在 max &lt;= min 时返回 null ⇒ 这种字段不换算成百分比。
/// </summary>
public static class HidReportDescriptor
{
    /// <summary>报告类型：Input（设备 → 主机）。</summary>
    public const byte ReportTypeInput = 0;

    /// <summary>报告类型：Output（主机 → 设备）。</summary>
    public const byte ReportTypeOutput = 1;

    /// <summary>报告类型：Feature（配置，双向）。标准 HID 电量 usage 通常在这里。</summary>
    public const byte ReportTypeFeature = 2;

    // ---- bType（item 类别）----
    private const int TypeMain = 0;
    private const int TypeGlobal = 1;
    private const int TypeLocal = 2;
    private const int TypeReserved = 3;

    // ---- Main tag ----
    private const int TagMainUsagePage = 0x5;   // Main 写法的 Usage Page：只对下一个 Main 数据项生效
    private const int TagInput = 0x8;
    private const int TagOutput = 0x9;
    private const int TagCollection = 0xA;
    private const int TagFeature = 0xB;
    private const int TagEndCollection = 0xC;

    // ---- Global tag ----
    private const int TagGlobalUsagePage = 0x0;
    private const int TagGlobalLogicalMin = 0x1;
    private const int TagGlobalLogicalMax = 0x2;
    private const int TagGlobalReportSize = 0x7;
    private const int TagGlobalReportId = 0x8;
    private const int TagGlobalReportCount = 0x9;
    private const int TagGlobalPush = 0xA;
    private const int TagGlobalPop = 0xB;

    // ---- Local tag ----
    private const int TagLocalUsage = 0x0;
    private const int TagLocalUsageMin = 0x1;
    private const int TagLocalUsageMax = 0x2;
    private const int TagLocalUsagePage = 0x4;   // Local 写法的 Usage Page：只对下一个 Main 数据项生效

    /// <summary>长项标记：bTag 恒为 0xF，bType 为 3（保留）⇒ 前缀字节 0xFE / 0xFF。</summary>
    private const int TagLong = 0xF;

    /// <summary>解析 HID 报告描述符（原始字节），取出所有字段。</summary>
    public static IReadOnlyList<HidField> Parse(byte[] descriptor)
    {
        var fields = new List<HidField>();
        if (descriptor is null || descriptor.Length == 0) return fields;

        // ---- 全局状态：一旦声明就持续生效，直到被新的同类项覆盖 ----
        ushort usagePage = 0;
        int logicalMin = 0;
        int logicalMax = 0;   // 描述符没声明 Logical Maximum 时保持 0（= 未知，不猜）
        int reportSize = 0;
        int reportCount = 1;
        byte reportId = 0;

        // Push / Pop 用到的全局状态栈
        var stack = new Stack<(ushort Page, int Min, int Max, int Size, int Count, byte Id)>();

        // ---- 局部状态：只对**下一个** Main 项生效，用完即清 ----
        var usages = new List<ushort>();
        ushort? usageMin = null;
        ushort? usageMax = null;

        // 局部写法的 Usage Page（Local 0x4 / Main 0x5）：只对**下一个** Main 数据项生效，用完即清
        ushort? pendingUsagePage = null;

        // 位偏移按 (报告 ID, 报告类型) 分别累计：
        // Input / Output / Feature 是三个独立的报告缓冲区（Windows 的 HidP_GetCaps 也分别给长度），
        // 同一个报告 ID 下的 Feature 字段不能接着 Input 字段的偏移往下排。
        var offsets = new Dictionary<(byte Id, byte Type), int>();

        int i = 0;
        while (i < descriptor.Length)
        {
            byte prefix = descriptor[i];
            int tag = prefix >> 4;
            int type = (prefix >> 2) & 0x03;
            int sizeCode = prefix & 0x03;

            // 长项：2 字节头（前缀 + 长度）+ length 字节数据；不定义字段、不占报告位
            if (tag == TagLong && type == TypeReserved)
            {
                if (i + 1 >= descriptor.Length) break;
                int len = descriptor[i + 1];
                if (i + 2 + len > descriptor.Length) break;
                i += 2 + len;
                continue;
            }

            // bSize：0→0 字节、1→1 字节、2→2 字节、3→4 字节
            int dataSize = sizeCode == 3 ? 4 : sizeCode;
            if (i + 1 + dataSize > descriptor.Length) break;
            int data = i + 1;

            if (type == TypeGlobal)
            {
                switch (tag)
                {
                    case TagGlobalUsagePage:
                        // 4 字节 Usage Page 的高 16 位按规范应为 0，取低 16 位即页号
                        usagePage = (ushort)(ReadUInt(descriptor, data, dataSize) & 0xFFFF);
                        break;
                    case TagGlobalLogicalMin:
                        logicalMin = ReadInt(descriptor, data, dataSize);
                        break;
                    case TagGlobalLogicalMax:
                        logicalMax = ReadInt(descriptor, data, dataSize);
                        break;
                    case TagGlobalReportSize:
                        reportSize = (int)ReadUInt(descriptor, data, dataSize);
                        break;
                    case TagGlobalReportId:
                        reportId = (byte)ReadUInt(descriptor, data, dataSize);
                        break;
                    case TagGlobalReportCount:
                        reportCount = (int)ReadUInt(descriptor, data, dataSize);
                        break;
                    case TagGlobalPush:
                        stack.Push((usagePage, logicalMin, logicalMax, reportSize, reportCount, reportId));
                        break;
                    case TagGlobalPop:
                        if (stack.Count > 0)
                        {
                            var s = stack.Pop();
                            usagePage = s.Page;
                            logicalMin = s.Min;
                            logicalMax = s.Max;
                            reportSize = s.Size;
                            reportCount = s.Count;
                            reportId = s.Id;
                        }
                        break;
                }
            }
            else if (type == TypeLocal)
            {
                switch (tag)
                {
                    case TagLocalUsage:
                        usages.Add((ushort)(ReadUInt(descriptor, data, dataSize) & 0xFFFF));
                        break;
                    case TagLocalUsageMin:
                        usageMin = (ushort)(ReadUInt(descriptor, data, dataSize) & 0xFFFF);
                        break;
                    case TagLocalUsageMax:
                        usageMax = (ushort)(ReadUInt(descriptor, data, dataSize) & 0xFFFF);
                        break;
                    case TagLocalUsagePage:
                        // 局部写法：只覆盖下一个 Main 数据项的 Usage Page
                        pendingUsagePage = (ushort)(ReadUInt(descriptor, data, dataSize) & 0xFFFF);
                        break;
                }
            }
            else if (type == TypeMain)
            {
                if (tag == TagMainUsagePage)
                {
                    // Main 写法的 Usage Page：同样只对**下一个** Main 数据项生效。
                    // 它本身不产生字段、也不占报告位。
                    pendingUsagePage = (ushort)(ReadUInt(descriptor, data, dataSize) & 0xFFFF);

                    // 它是一个 Main 项 ⇒ 按"用完即清"消耗掉上一批局部 usage
                    usages.Clear();
                    usageMin = null;
                    usageMax = null;
                }
                else
                {
                    byte reportType = tag switch
                    {
                        TagInput => ReportTypeInput,
                        TagOutput => ReportTypeOutput,
                        TagFeature => ReportTypeFeature,
                        // 集合（Collection / End Collection）等非数据 Main 项：不产生字段，也不占报告位
                        _ => (byte)0xFF,
                    };

                    if (reportType != 0xFF)
                    {
                        // 局部写法（Local 0x4 / Main 0x5）的 Usage Page 覆盖全局 Usage Page，
                        // 但只作用于这一个 Main 数据项 ⇒ 用完即清。
                        ushort effectiveUsagePage = pendingUsagePage ?? usagePage;
                        pendingUsagePage = null;

                        AppendFields(fields, offsets, reportId, reportType, effectiveUsagePage, usages,
                            usageMin, usageMax, reportSize, reportCount, logicalMin, logicalMax);
                    }

                    // 局部状态只作用于一个 Main 项
                    usages.Clear();
                    usageMin = null;
                    usageMax = null;
                }
            }

            i += 1 + dataSize;
        }

        return fields;
    }

    /// <summary>在 Feature 报告的字段里找覆盖目标 usage 的那一条；找不到返回 null。</summary>
    public static HidField? FindFeatureField(IReadOnlyList<HidField> fields, ushort usagePage, ushort usage)
    {
        if (fields is null) return null;

        foreach (var f in fields)
        {
            // 只认 Feature 报告：Input 报告里同 usage 的字段是设备上报的状态，不是可读的配置值
            if (f.ReportType != ReportTypeFeature) continue;
            if (f.UsagePage != usagePage) continue;

            if (f.IsRange)
            {
                // 范围字段：落在 [UsageMin, UsageMax] 内就算覆盖
                if (usage >= f.UsageMin && usage <= f.UsageMax) return f;
            }
            else if (f.Usage == usage)
            {
                return f;
            }
        }

        return null;
    }

    /// <summary>
    /// 按字段从原始报告里取值。report[0] 是**报告 ID 字节**（Windows HidD_GetFeature 与
    /// macOS IOHIDDeviceGetReport 都是这个约定），所以字段位偏移要 +8 位再取。
    /// 报告太短 / 位宽不合理时返回 null。
    /// </summary>
    public static int? ReadValue(byte[] report, HidField field)
    {
        if (report is null || field is null) return null;

        int bitSize = field.BitSize;
        if (bitSize <= 0 || bitSize > 32) return null;

        // 报告第 0 字节是报告 ID ⇒ 字段第 0 位在 report 里的绝对位号是 8
        int startBit = 8 + field.BitOffset;
        if (startBit < 0) return null;
        if (startBit + bitSize > report.Length * 8) return null;

        // 小端序取位：低位在前（字节内 LSB 优先）
        uint value = 0;
        for (int k = 0; k < bitSize; k++)
        {
            int abs = startBit + k;
            uint bit = (uint)((report[abs / 8] >> (abs % 8)) & 1);
            value |= bit << k;
        }

        // 取的是原始位模式（不做符号扩展）：LogicalMin 为负的字段是有符号值域，
        // 但标准 HID 电量 usage 都是 0..100 这种无符号值域，符号解释交给上层，不在这里猜。
        return unchecked((int)value);
    }

    /// <summary>
    /// 把一个 Main 数据项（Input / Output / Feature）展开成若干字段。
    /// Report Count 决定字段个数，Report Size 决定每个字段的位宽与位偏移步进。
    /// </summary>
    private static void AppendFields(
        List<HidField> fields,
        Dictionary<(byte Id, byte Type), int> offsets,
        byte reportId, byte reportType, ushort usagePage,
        List<ushort> usages, ushort? usageMin, ushort? usageMax,
        int reportSize, int reportCount, int logicalMin, int logicalMax)
    {
        int count = reportCount > 0 ? reportCount : 1;
        bool isRange = usageMin.HasValue && usageMax.HasValue;

        var key = (reportId, reportType);
        int offset = offsets.TryGetValue(key, out int current) ? current : 0;

        for (int n = 0; n < count; n++)
        {
            ushort usage;
            if (isRange)
            {
                // Usage Min/Max 形式：范围展开成范围 —— 第 n 个字段取范围内第 n 个 usage，
                // 超出范围（范围比字段数还短）就沿用最小值，不越界编造 usage。
                int candidate = usageMin!.Value + n;
                usage = candidate <= usageMax!.Value ? (ushort)candidate : usageMin.Value;
            }
            else if (usages.Count > 0)
            {
                // 声明了 usage 列表：一一对应；只声明了一个而 Report Count > 1 时，
                // 后面的字段沿用同一个 usage（不猜别的 usage）。
                usage = n < usages.Count ? usages[n] : usages[usages.Count - 1];
            }
            else
            {
                usage = 0;   // 没声明 usage：留 0（= 未知），不硬安一个
            }

            fields.Add(new HidField(
                reportId,
                reportType,
                usagePage,
                usage,
                isRange,
                isRange ? usageMin!.Value : usage,
                isRange ? usageMax!.Value : usage,
                offset,
                reportSize,
                logicalMin,
                logicalMax));

            offset += reportSize;
        }

        offsets[key] = offset;
    }

    /// <summary>读无符号整数（小端）。</summary>
    private static uint ReadUInt(byte[] data, int offset, int size)
    {
        switch (size)
        {
            case 0:
                return 0;
            case 1:
                return data[offset];
            case 2:
                return (uint)(data[offset] | (data[offset + 1] << 8));
            default:
                return (uint)(data[offset]
                    | (data[offset + 1] << 8)
                    | (data[offset + 2] << 16)
                    | (data[offset + 3] << 24));
        }
    }

    /// <summary>读有符号整数（小端）—— 逻辑最小值可能是负数（如 -127）。</summary>
    private static int ReadInt(byte[] data, int offset, int size)
    {
        switch (size)
        {
            case 0:
                return 0;
            case 1:
                return unchecked((sbyte)data[offset]);
            case 2:
                return unchecked((short)(data[offset] | (data[offset + 1] << 8)));
            default:
                return unchecked(data[offset]
                    | (data[offset + 1] << 8)
                    | (data[offset + 2] << 16)
                    | (data[offset + 3] << 24));
        }
    }
}
