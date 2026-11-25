using System;
using Island.App.Services.Peripheral;

namespace Island.Tests;

/// <summary>
/// 最小 HID 报告描述符解析器的用例（2026-11-26 macOS 外设电量对齐）。
///
/// 被测文件：<c>src/Island.App/Services/Peripheral/HidReportDescriptor.cs</c>（纯逻辑，无 Win32 依赖，
/// 所以能链进零 NuGet 的测试工程，也能被 mac 工程链接）。
///
/// 这里的描述符字节**全部手工拼**（按 USB HID 规范 1.11 §6.2.2 的 item 编码），不从真机抓 ——
/// 目的就是把"位偏移 / 值域 / usage 范围 / Usage Page 写法"这些最容易写错的地方钉死。
///
/// item 前缀字节 = bTag(bit7-4) | bType(bit3-2) | bSize(bit1-0)
///   bType：0=Main、1=Global、2=Local；bSize：0=0 字节、1=1 字节、2=2 字节、3=4 字节。
///
/// 底线：不许为了让用例通过而放宽断言；解析器读不出来的东西，用例就该断言"读不出来"。
/// </summary>
internal static class HidDescriptorTests
{
    // ================= 描述符拼装工具 =================

    /// <summary>拼一个 item：前缀字节 + 数据。</summary>
    private static byte[] Item(int tag, int type, params byte[] data)
    {
        int sizeCode = data.Length switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            4 => 3,
            _ => throw new ArgumentException("item 数据只支持 0/1/2/4 字节"),
        };

        var result = new byte[1 + data.Length];
        result[0] = (byte)((tag << 4) | (type << 2) | sizeCode);
        Array.Copy(data, 0, result, 1, data.Length);
        return result;
    }

    /// <summary>拼接若干 item。</summary>
    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;

        var result = new byte[total];
        int at = 0;
        foreach (var p in parts)
        {
            Array.Copy(p, 0, result, at, p.Length);
            at += p.Length;
        }
        return result;
    }

    // ---- Usage Page 的三种写法 ----

    /// <summary>Usage Page（Global tag 0）：持续生效，覆盖后续所有 Main 项。</summary>
    private static byte[] UsagePage(byte page) => Item(0x0, 1, page);

    /// <summary>Usage Page（Local tag 4）：只对下一个 Main 数据项生效。</summary>
    private static byte[] UsagePageLocal(byte page) => Item(0x4, 2, page);

    /// <summary>Usage Page（Main tag 5）：只对下一个 Main 数据项生效。</summary>
    private static byte[] UsagePageMain(byte page) => Item(0x5, 0, page);

    // ---- Global 项（作用于后续所有 Main 项）----

    /// <summary>Report ID（Global tag 8），1 字节。</summary>
    private static byte[] ReportId(byte id) => Item(0x8, 1, id);

    /// <summary>Report Size（Global tag 7），1 字节（单位：位）。</summary>
    private static byte[] ReportSize(byte bits) => Item(0x7, 1, bits);

    /// <summary>Report Count（Global tag 9），1 字节（单位：字段个数）。</summary>
    private static byte[] ReportCount(byte count) => Item(0x9, 1, count);

    /// <summary>Logical Minimum（Global tag 1），1 字节有符号。</summary>
    private static byte[] LogicalMin(int value) => Item(0x1, 1, unchecked((byte)(sbyte)value));

    /// <summary>Logical Maximum（Global tag 2），1 字节有符号。</summary>
    private static byte[] LogicalMax(int value) => Item(0x2, 1, unchecked((byte)(sbyte)value));

    // ---- Local 项（只作用于下一个 Main 项）----

    /// <summary>Usage（Local tag 0），2 字节。</summary>
    private static byte[] Usage(ushort usage)
        => Item(0x0, 2, (byte)(usage & 0xFF), (byte)(usage >> 8));

    /// <summary>Usage Minimum（Local tag 1），2 字节。</summary>
    private static byte[] UsageMin(ushort usage)
        => Item(0x1, 2, (byte)(usage & 0xFF), (byte)(usage >> 8));

    /// <summary>Usage Maximum（Local tag 2），2 字节。</summary>
    private static byte[] UsageMax(ushort usage)
        => Item(0x2, 2, (byte)(usage & 0xFF), (byte)(usage >> 8));

    // ---- Main 项 ----

    /// <summary>Input（Main tag 8）：Data, Variable, Absolute。</summary>
    private static byte[] Input() => Item(0x8, 0, 0x02);

    /// <summary>Feature（Main tag 11）：Data, Variable, Absolute。</summary>
    private static byte[] Feature() => Item(0xB, 0, 0x02);

    /// <summary>Collection（Main tag 10）：Application。</summary>
    private static byte[] Collection() => Item(0xA, 0, 0x01);

    /// <summary>End Collection（Main tag 12）。</summary>
    private static byte[] EndCollection() => Item(0xC, 0);

    // ---- 长项 ----

    /// <summary>长项：bTag=0xF、bType=3（保留）⇒ 前缀 0xFE，后跟 1 字节长度 + 该长度的数据。</summary>
    private static byte[] LongItem(params byte[] data)
    {
        var result = new byte[2 + data.Length];
        result[0] = 0xFE;
        result[1] = (byte)data.Length;
        Array.Copy(data, 0, result, 2, data.Length);
        return result;
    }

    /// <summary>长项的另一种写法：前缀 0xFF（bSize 字段为 3）。结构一样（前缀 + 长度 + 数据）。</summary>
    private static byte[] LongItemSize3(params byte[] data)
    {
        var result = new byte[2 + data.Length];
        result[0] = 0xFF;
        result[1] = (byte)data.Length;
        Array.Copy(data, 0, result, 2, data.Length);
        return result;
    }

    public static void Run(Action<string, bool> check)
    {
        // ---------- 一、Feature 报告里的标准电量字段（Usage Page 0x85 / Usage 0x0065）----------

        // 描述符：Usage Page(0x85) → Usage(0x0065) → Report ID(3) → Report Size(8)
        //        → Report Count(1) → Logical 0..100 → Feature
        byte[] desc = Concat(
            UsagePage(0x85),
            Usage(0x0065),
            ReportId(3),
            ReportSize(8),
            ReportCount(1),
            LogicalMin(0),
            LogicalMax(100),
            Feature());

        var fields = HidReportDescriptor.Parse(desc);

        // 为什么测这个：位偏移与值域是"能不能读出正确电量"的全部依据，错一位就全错。
        check("描述符解析：只解析出 1 个字段", fields.Count == 1);
        check("描述符解析：Feature 字段的报告 ID = 3", fields.Count > 0 && fields[0].ReportId == 3);
        check("描述符解析：Feature 字段的报告类型 = Feature(2)", fields.Count > 0 && fields[0].ReportType == 2);
        check("描述符解析：Usage Page = 0x85", fields.Count > 0 && fields[0].UsagePage == 0x85);
        check("描述符解析：Usage = 0x0065", fields.Count > 0 && fields[0].Usage == 0x0065);
        check("描述符解析：单个 usage 不是范围", fields.Count > 0 && !fields[0].IsRange);
        check("描述符解析：位宽 = 8", fields.Count > 0 && fields[0].BitSize == 8);
        check("描述符解析：位偏移 = 0", fields.Count > 0 && fields[0].BitOffset == 0);
        check("描述符解析：逻辑最小值 = 0", fields.Count > 0 && fields[0].LogicalMin == 0);
        check("描述符解析：逻辑最大值 = 100", fields.Count > 0 && fields[0].LogicalMax == 100);

        var feature = HidReportDescriptor.FindFeatureField(fields, 0x85, 0x0065);
        check("查找电量字段：FindFeatureField(0x85, 0x0065) 命中", feature is not null);
        check("查找电量字段：命中的是 Feature 字段且逻辑值域 0..100",
            feature is not null && feature.ReportType == 2 && feature.LogicalMax == 100);

        // 为什么测这个：忘了"报告 ID 字节"就会整体错位一字节 —— 这是 mac 端最容易踩的坑。
        check("按位取值：报告 {0x03, 0x64} 读出 100（0x64 = 100，首字节是报告 ID）",
            feature is not null && HidReportDescriptor.ReadValue(new byte[] { 0x03, 0x64 }, feature) == 100);

        // ---------- 二、Input 报告里的同 usage 字段不能被当成 Feature 电量 ----------

        // 同一个描述符里既有 Input 报告也有 Feature 报告，且用的是同一个 usage。
        // Local 项（Usage）只作用于下一个 Main 项，所以两段各声明一次 Usage。
        byte[] mixed = Concat(
            UsagePage(0x85),
            Usage(0x0065), ReportId(1), ReportSize(8), ReportCount(1), Input(),
            Usage(0x0065), ReportId(3), ReportSize(8), ReportCount(1),
            LogicalMin(0), LogicalMax(100), Feature());

        var mixedFields = HidReportDescriptor.Parse(mixed);

        check("只认 Feature：Input 字段的报告类型 = 0",
            mixedFields.Count == 2 && mixedFields[0].ReportType == 0);
        check("只认 Feature：Feature 字段的报告类型 = 2",
            mixedFields.Count == 2 && mixedFields[1].ReportType == 2);

        var mixedHit = HidReportDescriptor.FindFeatureField(mixedFields, 0x85, 0x0065);
        check("只认 Feature：同一 usage 的 Input 字段不被命中，命中的是 Feature 字段",
            mixedHit is not null && mixedHit.ReportType == 2 && mixedHit.ReportId == 3);

        // 只有 Input 报告时，FindFeatureField 必须返回 null（找不到就是找不到，不许退而求其次）
        byte[] inputOnly = Concat(
            UsagePage(0x85),
            Usage(0x0065), ReportId(1), ReportSize(8), ReportCount(1), Input());
        var inputFields = HidReportDescriptor.Parse(inputOnly);
        check("只认 Feature：描述符里只有 Input 报告时 FindFeatureField 返回 null",
            inputFields.Count == 1
            && inputFields[0].ReportType == 0
            && HidReportDescriptor.FindFeatureField(inputFields, 0x85, 0x0065) is null);

        // ---------- 三、Report Count = 2：位偏移必须连续 ----------

        byte[] count2 = Concat(
            UsagePage(0x85),
            Usage(0x0065),
            ReportId(3),
            ReportSize(8),
            ReportCount(2),
            LogicalMin(0),
            LogicalMax(100),
            Feature());

        var count2Fields = HidReportDescriptor.Parse(count2);

        check("Report Count=2：解析出 2 个字段", count2Fields.Count == 2);
        check("Report Count=2：第一个字段位偏移 = 0", count2Fields.Count == 2 && count2Fields[0].BitOffset == 0);
        check("Report Count=2：第二个字段位偏移 = 8", count2Fields.Count == 2 && count2Fields[1].BitOffset == 8);
        check("Report Count=2：两个字段的位宽都是 8",
            count2Fields.Count == 2 && count2Fields[0].BitSize == 8 && count2Fields[1].BitSize == 8);

        // 为什么测这个：位偏移算错时，这里会两个都读到同一个字节。
        check("Report Count=2：第一个字段取到第 1 个数据字节（10）",
            count2Fields.Count == 2
            && HidReportDescriptor.ReadValue(new byte[] { 0x03, 0x0A, 0x64 }, count2Fields[0]) == 10);
        check("Report Count=2：第二个字段取到第 2 个数据字节（100）",
            count2Fields.Count == 2
            && HidReportDescriptor.ReadValue(new byte[] { 0x03, 0x0A, 0x64 }, count2Fields[1]) == 100);

        // ---------- 四、Usage 范围（Usage Minimum / Maximum 形式）----------

        byte[] rangeDesc = Concat(
            UsagePage(0x85),
            UsageMin(0x0065),
            UsageMax(0x0066),
            ReportId(4),
            ReportSize(8),
            ReportCount(1),
            LogicalMin(0),
            LogicalMax(100),
            Feature());

        var rangeFields = HidReportDescriptor.Parse(rangeDesc);

        // 为什么测这个：范围字段的 usage 是"一段"，只记一个端点就会把区间外的 usage 也算命中。
        check("Usage 范围：IsRange = true", rangeFields.Count == 1 && rangeFields[0].IsRange);
        check("Usage 范围：UsageMin = 0x0065", rangeFields.Count == 1 && rangeFields[0].UsageMin == 0x0065);
        check("Usage 范围：UsageMax = 0x0066", rangeFields.Count == 1 && rangeFields[0].UsageMax == 0x0066);
        check("Usage 范围：范围内的 usage 0x0065 命中",
            HidReportDescriptor.FindFeatureField(rangeFields, 0x85, 0x0065) is not null);
        check("Usage 范围：范围内的 usage 0x0066 命中",
            HidReportDescriptor.FindFeatureField(rangeFields, 0x85, 0x0066) is not null);
        check("Usage 范围：范围外的 usage 0x0067 不命中",
            HidReportDescriptor.FindFeatureField(rangeFields, 0x85, 0x0067) is null);

        // ---------- 五、长项要整项跳过，且不占报告位 ----------

        byte[] longDesc = Concat(
            UsagePage(0x85),
            Usage(0x0065),
            ReportId(5),
            ReportSize(8),
            ReportCount(1),
            LogicalMin(0),
            LogicalMax(100),
            LongItem(0xAA, 0xBB, 0xCC),
            Feature());

        var longFields = HidReportDescriptor.Parse(longDesc);

        // 为什么测这个：长项的数据字节如果被当成 item 解析，后面所有位偏移都会漂。
        check("长项 0xFE：只解析出 1 个字段", longFields.Count == 1);
        check("长项 0xFE：后面的字段位偏移不受影响（仍为 0)",
            longFields.Count == 1 && longFields[0].BitOffset == 0);

        byte[] longDesc2 = Concat(
            UsagePage(0x85),
            Usage(0x0065),
            ReportId(5),
            ReportSize(8),
            ReportCount(1),
            LogicalMin(0),
            LogicalMax(100),
            LongItemSize3(0x11, 0x22),
            Feature());

        var longFields2 = HidReportDescriptor.Parse(longDesc2);
        check("长项 0xFF（bSize=3）：只解析出 1 个字段", longFields2.Count == 1);
        check("长项 0xFF（bSize=3）：后面的字段位偏移不受影响",
            longFields2.Count == 1 && longFields2[0].BitOffset == 0);

        // ---------- 六、没声明 Logical Maximum ⇒ 值域未知，不许换算 ----------

        byte[] noMaxDesc = Concat(
            UsagePage(0x85),
            Usage(0x0065),
            ReportId(6),
            ReportSize(8),
            ReportCount(1),
            LogicalMin(0),
            Feature());

        var noMaxFields = HidReportDescriptor.Parse(noMaxDesc);

        // 为什么测这个："没声明值域"不等于"值域是 0..0"，也不等于"大概是 0..100"——
        // 猜值域就会把任意原始值显示成假百分比。
        check("值域未知：没声明 Logical Maximum 时 LogicalMax = 0",
            noMaxFields.Count == 1 && noMaxFields[0].LogicalMax == 0);
        check("值域未知：换算百分比返回 null（不伪装）",
            noMaxFields.Count == 1
            && StandardHidBattery.ToPercent(50, noMaxFields[0].LogicalMin, noMaxFields[0].LogicalMax) is null);

        // ---------- 七、ReadValue 的边界：报告太短 / 位宽不合理 ----------

        check("取值边界：报告字节不足时返回 null",
            feature is not null && HidReportDescriptor.ReadValue(new byte[] { 0x03 }, feature) is null);
        check("取值边界：空报告返回 null",
            feature is not null && HidReportDescriptor.ReadValue(Array.Empty<byte>(), feature) is null);

        var zeroWidth = feature! with { BitSize = 0 };
        var tooWide = feature! with { BitSize = 33 };
        check("取值边界：位宽为 0 时返回 null",
            HidReportDescriptor.ReadValue(new byte[] { 0x03, 0x64 }, zeroWidth) is null);
        check("取值边界：位宽大于 32 时返回 null",
            HidReportDescriptor.ReadValue(new byte[] { 0x03, 0x64 }, tooWide) is null);

        // ---------- 八、没声明 Report ID ⇒ 报告 ID 是 0 ----------

        byte[] noIdDesc = Concat(
            UsagePage(0x85),
            Usage(0x0065),
            ReportSize(8),
            ReportCount(1),
            LogicalMin(0),
            LogicalMax(100),
            Feature());

        var noIdFields = HidReportDescriptor.Parse(noIdDesc);

        // 为什么测这个：mac 端按报告 ID 去取原始报告，ID 取错就永远取不到数据。
        check("没声明 Report ID：字段的 ReportId = 0",
            noIdFields.Count == 1 && noIdFields[0].ReportId == 0);

        // ---------- 九、usage 列表 + Report Count 一一对应 ----------

        byte[] listDesc = Concat(
            UsagePage(0x85),
            Usage(0x0065),
            Usage(0x0066),
            ReportId(7),
            ReportSize(8),
            ReportCount(2),
            LogicalMin(0),
            LogicalMax(100),
            Feature());

        var listFields = HidReportDescriptor.Parse(listDesc);

        check("usage 列表：解析出 2 个字段", listFields.Count == 2);
        check("usage 列表：第一个字段 usage = 0x0065", listFields.Count == 2 && listFields[0].Usage == 0x0065);
        check("usage 列表：第二个字段 usage = 0x0066", listFields.Count == 2 && listFields[1].Usage == 0x0066);
        check("usage 列表：位偏移分别是 0 与 8",
            listFields.Count == 2 && listFields[0].BitOffset == 0 && listFields[1].BitOffset == 8);

        // ---------- 十、跨字节的多字节字段按小端序取值 ----------

        byte[] wideDesc = Concat(
            UsagePage(0x85),
            Usage(0x0065),
            ReportId(8),
            ReportSize(16),
            ReportCount(1),
            LogicalMin(0),
            LogicalMax(100),
            Feature());

        var wideFields = HidReportDescriptor.Parse(wideDesc);

        // 为什么测这个：HID 报告里的多字节值是低字节在前，写成大端就会读出 0x3412。
        check("多字节字段：16 位字段的位宽 = 16", wideFields.Count == 1 && wideFields[0].BitSize == 16);
        check("多字节字段：按小端序跨字节取值得 0x1234",
            wideFields.Count == 1
            && HidReportDescriptor.ReadValue(new byte[] { 0x08, 0x34, 0x12 }, wideFields[0]) == 0x1234);

        // ---------- 十一、集合不占数据位 ----------

        byte[] collDesc = Concat(
            UsagePage(0x85),
            Collection(),
            Usage(0x0065),
            ReportId(9),
            ReportSize(8),
            ReportCount(1),
            LogicalMin(0),
            LogicalMax(100),
            Feature(),
            EndCollection());

        var collFields = HidReportDescriptor.Parse(collDesc);

        check("集合：只解析出 1 个字段", collFields.Count == 1);
        check("集合：位偏移不受影响", collFields.Count == 1 && collFields[0].BitOffset == 0);

        // ---------- 十二、同一个报告里的多个 Feature 字段，位偏移依次排 ----------

        byte[] twoFeatureDesc = Concat(
            UsagePage(0x85),
            Usage(0x0065), ReportId(10), ReportSize(8), ReportCount(1),
            LogicalMin(0), LogicalMax(100), Feature(),
            Usage(0x0066), ReportSize(8), ReportCount(1),
            LogicalMin(0), LogicalMax(100), Feature());

        var twoFeatureFields = HidReportDescriptor.Parse(twoFeatureDesc);

        check("同一报告里的多个 Feature 字段：解析出 2 个字段", twoFeatureFields.Count == 2);
        check("同一报告里的多个 Feature 字段：位偏移依次为 0 与 8",
            twoFeatureFields.Count == 2
            && twoFeatureFields[0].BitOffset == 0
            && twoFeatureFields[1].BitOffset == 8);

        // ---------- 十三、Usage Page 的另外两种写法（Local 0x4 / Main 0x5）----------

        // 为什么测这个：真实描述符里 "Usage Page 紧跟 Main Feature" 的写法很常见。
        // 只认 Global 写法时，这个 Feature 字段会挂到上一个 Usage Page（或 0）上，
        // FindFeatureField(0x85, 0x0065) 就找不到 ⇒ mac 侧 L1 读不到标准电量。
        byte[] mainPageDesc = Concat(
            UsagePageMain(0x85),
            Usage(0x0065),
            ReportId(3),
            ReportSize(8),
            ReportCount(1),
            LogicalMin(0),
            LogicalMax(100),
            Feature());

        var mainPageFields = HidReportDescriptor.Parse(mainPageDesc);

        check("Main 写法的 Usage Page：字段的 Usage Page = 0x85",
            mainPageFields.Count == 1 && mainPageFields[0].UsagePage == 0x85);
        check("Main 写法的 Usage Page：FindFeatureField(0x85, 0x0065) 命中",
            HidReportDescriptor.FindFeatureField(mainPageFields, 0x85, 0x0065) is not null);

        // 局部写法只覆盖**下一个** Main 数据项：第二个 Feature 回落全局 Usage Page（0x01）。
        byte[] localPageDesc = Concat(
            UsagePage(0x01),
            UsagePageLocal(0x85),
            Usage(0x0065),
            ReportId(3),
            ReportSize(8),
            ReportCount(1),
            LogicalMin(0),
            LogicalMax(100),
            Feature(),
            Usage(0x0066),
            ReportSize(8),
            ReportCount(1),
            LogicalMin(0),
            LogicalMax(100),
            Feature());

        var localPageFields = HidReportDescriptor.Parse(localPageDesc);

        check("Local 写法的 Usage Page：解析出 2 个字段", localPageFields.Count == 2);
        check("Local 写法的 Usage Page：只影响下一个 Main 项 —— 第一个字段的 Usage Page = 0x85",
            localPageFields.Count == 2 && localPageFields[0].UsagePage == 0x85);
        check("Local 写法的 Usage Page：之后的字段回落全局 Usage Page = 0x01",
            localPageFields.Count == 2 && localPageFields[1].UsagePage == 0x01);
        check("Local 写法的 Usage Page：局部页里的字段按 0x85 能找到",
            HidReportDescriptor.FindFeatureField(localPageFields, 0x85, 0x0065) is not null);
        check("Local 写法的 Usage Page：回落后的字段按全局页 0x01 能找到",
            HidReportDescriptor.FindFeatureField(localPageFields, 0x01, 0x0066) is not null);
    }
}
