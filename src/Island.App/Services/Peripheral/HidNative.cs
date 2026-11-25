using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Island.App.Services.Peripheral;

/// <summary>
/// SetupAPI / hid.dll / kernel32 的原生互操作声明 —— 外设电量的 HID 访问层底座。
///
/// 三个必须记住的 Windows HID 事实（都是实机踩出来的，不是从文档抄的）：
///  ① 顶层集合（鼠标 / 键盘 / 笔 / 触摸屏）被系统**独占**：用 GENERIC_READ|GENERIC_WRITE
///     打不开（CreateFile 报 ERROR_ACCESS_DENIED=5）。但用 dwDesiredAccess=0 打开仍能读到
///     属性、字符串、能力表与 Feature Report —— 所以枚举阶段一律先用 0 权限打开。
///  ② HIDP_VALUE_CAPS 是 72 字节、结尾是 8 个 USHORT 的 union（Range 与 NotRange 共用同一块内存），
///     按 60 字节遍历会越界读。这里用显式偏移声明，避开 padding 手算。
///  ③ 读独占集合的 Input Report 必须 FILE_FLAG_OVERLAPPED + CancelIoEx：
///     阻塞读叠上并发写会把进程直接杀掉。
///
/// 方法论参考 KaBanei/peripheral-battery（MIT）的 HidNative/HidBus/HidChannel，
/// 本文件按 MyIsland 的口径重写；结构布局以 Windows SDK 与 HID Usage Tables 为准。
/// </summary>
internal static class HidNative
{
    /// <summary>GUID_DEVINTERFACE_HID：枚举 HID 接口用。</summary>
    public static readonly Guid GuidDevInterfaceHid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    public const int DIGCF_PRESENT = 0x02;
    public const int DIGCF_DEVICEINTERFACE = 0x10;

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    public const int ERROR_IO_PENDING = 997;
    public const int WAIT_OBJECT_0 = 0;
    public const int WAIT_TIMEOUT = 258;

    public const int HIDP_STATUS_SUCCESS = 0x00110000;
    public const int HIDP_INPUT = 0;
    public const int HIDP_OUTPUT = 1;
    public const int HIDP_FEATURE = 2;

    public static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>
    /// HIDP_VALUE_CAPS 的字节数（**必须是 72**）—— HidP_GetValueCaps 按这个尺寸铺数组。
    /// 显式布局的尺寸 = 最后一个字段偏移+大小；union 少写一个 USHORT 就会变成 60，
    /// 驱动按 72 字节写就会把堆写坏（实机踩过：直接 STATUS_HEAP_CORRUPTION）。
    /// </summary>
    public static int ValueCapsSize => Marshal.SizeOf<HidValueCaps>();

    /// <summary>HIDD_ATTRIBUTES 的字节数（12）—— 必须原样写回 attributes.Size。</summary>
    public static int AttributesSize => Marshal.SizeOf<HidAttributes>();

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDevInfoData
    {
        public int CbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HidAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    /// <summary>HIDP_CAPS：显式偏移，跳过中间 34 字节的保留区（手算 padding 容易错）。</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct HidCaps
    {
        [FieldOffset(0)] public ushort Usage;
        [FieldOffset(2)] public ushort UsagePage;
        [FieldOffset(4)] public ushort InputReportByteLength;
        [FieldOffset(6)] public ushort OutputReportByteLength;
        [FieldOffset(8)] public ushort FeatureReportByteLength;
        [FieldOffset(44)] public ushort NumberLinkCollectionNodes;
        [FieldOffset(46)] public ushort NumberInputButtonCaps;
        [FieldOffset(48)] public ushort NumberInputValueCaps;
        [FieldOffset(50)] public ushort NumberInputDataIndices;
        [FieldOffset(52)] public ushort NumberOutputButtonCaps;
        [FieldOffset(54)] public ushort NumberOutputValueCaps;
        [FieldOffset(56)] public ushort NumberOutputDataIndices;
        [FieldOffset(58)] public ushort NumberFeatureButtonCaps;
        [FieldOffset(60)] public ushort NumberFeatureValueCaps;
        [FieldOffset(62)] public ushort NumberFeatureDataIndices;
    }

    /// <summary>
    /// HIDP_VALUE_CAPS：72 字节；结尾 union 占 8 个 USHORT。
    /// IsRange=0 时 union 的第一个 USHORT 就是单个 Usage（不是范围下界）。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct HidValueCaps
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportId;
        [FieldOffset(3)] public byte IsAlias;
        [FieldOffset(4)] public ushort BitField;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(8)] public ushort LinkUsage;
        [FieldOffset(10)] public ushort LinkUsagePage;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(13)] public byte IsStringRange;
        [FieldOffset(14)] public byte IsDesignatorRange;
        [FieldOffset(15)] public byte IsAbsolute;
        [FieldOffset(16)] public byte HasNull;
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(20)] public ushort ReportCount;
        [FieldOffset(32)] public uint UnitsExp;
        [FieldOffset(36)] public uint Units;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(48)] public int PhysicalMin;
        [FieldOffset(52)] public int PhysicalMax;
        [FieldOffset(56)] public ushort UsageMin;
        [FieldOffset(58)] public ushort UsageMax;
        [FieldOffset(60)] public ushort StringMin;
        [FieldOffset(62)] public ushort StringMax;
        [FieldOffset(64)] public ushort DesignatorMin;
        [FieldOffset(66)] public ushort DesignatorMax;
        [FieldOffset(68)] public ushort DataIndexMin;
        [FieldOffset(70)] public ushort DataIndexMax;
    }

    // ---------------- setupapi ----------------

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, int memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize, ref int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData,
        StringBuilder deviceInstanceId, int deviceInstanceIdSize, ref int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    // ---------------- hid.dll ----------------

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetAttributes(IntPtr hidDeviceObject, ref HidAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, ref IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetFeature(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetFeature(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetOutputReport(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool HidD_GetProductString(IntPtr hidDeviceObject, StringBuilder buffer, int bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool HidD_GetManufacturerString(IntPtr hidDeviceObject, StringBuilder buffer, int bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool HidD_GetSerialNumberString(IntPtr hidDeviceObject, StringBuilder buffer, int bufferLength);

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr preparsedData, ref HidCaps capabilities);

    [DllImport("hid.dll")]
    public static extern int HidP_GetValueCaps(int reportType, IntPtr valueCaps, ref int valueCapsLength,
        IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection,
        ushort usage, out uint usageValue, IntPtr preparsedData, byte[] report, uint reportLength);

    // ---------------- kernel32 ----------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(IntPtr file, IntPtr buffer, int bytesToRead, out int bytesRead,
        ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(IntPtr file, IntPtr buffer, int bytesToWrite, out int bytesWritten,
        ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetOverlappedResult(IntPtr file, ref NativeOverlapped overlapped,
        out uint bytesTransferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(IntPtr file, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateEvent(IntPtr eventAttributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int WaitForSingleObject(IntPtr handle, uint milliseconds);

    // ---------------- 辅助 ----------------

    /// <summary>只查询打开（访问权限 0）：即使设备被独占也能读属性/字符串/能力表。</summary>
    public static IntPtr OpenForQuery(string path)
        => CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>读写打开：独占集合会失败（ERROR_ACCESS_DENIED），这是预期行为不是异常。</summary>
    public static IntPtr OpenForIo(string path)
        => CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);

    /// <summary>去掉字符串尾部的 NUL 与空白；空串视作"没有"。</summary>
    public static string? CleanString(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        int end = text.IndexOf('\0');
        if (end >= 0) text = text.Substring(0, end);
        text = text.Trim();
        return text.Length == 0 ? null : text;
    }

    public static string? GetProductString(IntPtr handle)
    {
        var sb = new StringBuilder(256);
        return HidD_GetProductString(handle, sb, sb.Capacity * 2) ? CleanString(sb.ToString()) : null;
    }

    public static string? GetManufacturerString(IntPtr handle)
    {
        var sb = new StringBuilder(256);
        return HidD_GetManufacturerString(handle, sb, sb.Capacity * 2) ? CleanString(sb.ToString()) : null;
    }

    public static string? GetSerialNumberString(IntPtr handle)
    {
        var sb = new StringBuilder(256);
        return HidD_GetSerialNumberString(handle, sb, sb.Capacity * 2) ? CleanString(sb.ToString()) : null;
    }

    /// <summary>从实例 ID 里抠出 VID/PID（打不开设备的节点只能靠这个认厂商）。</summary>
    public static bool TryParseVidPid(string? instanceId, out int vid, out int pid)
    {
        vid = 0;
        pid = 0;
        if (string.IsNullOrEmpty(instanceId)) return false;
        int vi = instanceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        int pi = instanceId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (vi < 0 || pi < 0) return false;
        if (vi + 8 > instanceId.Length || pi + 8 > instanceId.Length) return false;
        if (!int.TryParse(instanceId.Substring(vi + 4, 4), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out vid)) return false;
        if (!int.TryParse(instanceId.Substring(pi + 4, 4), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out pid)) return false;
        return vid != 0 || pid != 0;
    }

    /// <summary>
    /// 读某个报告类型的全部 value caps。
    /// HidP_GetValueCaps 的返回值是"取到的条数"而不是状态码（很多示例写错了），
    /// 所以这里只看写回的长度参数，长度为 0 才当失败。
    /// </summary>
    public static List<HidUsageRange> ReadValueCaps(IntPtr preparsedData, int reportType, int count)
    {
        var list = new List<HidUsageRange>();
        if (preparsedData == IntPtr.Zero || count <= 0) return list;

        int size = ValueCapsSize;
        int capacity = count;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size * capacity);
                int length = capacity;
                HidP_GetValueCaps(reportType, buffer, ref length, preparsedData);
                if (length <= 0) return list;
                if (length > capacity)
                {
                    capacity = length;   // 驱动报回来的条数比预估多：铺大一点再来一次
                    continue;
                }
                for (int i = 0; i < length; i++)
                {
                    var caps = Marshal.PtrToStructure<HidValueCaps>(
                        new IntPtr(buffer.ToInt64() + (long)i * size));
                    list.Add(new HidUsageRange(
                        caps.UsagePage,
                        caps.UsageMin,
                        caps.IsRange != 0 ? caps.UsageMax : caps.UsageMin,
                        caps.ReportId,
                        caps.IsRange != 0,
                        caps.LogicalMin,
                        caps.LogicalMax));
                }
                return list;
            }
            return list;
        }
        catch
        {
            return list;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>按 HID 描述符解析某个 usage 的数值（不假设字节偏移）。</summary>
    public static bool TryGetUsageValue(IntPtr preparsedData, int reportType, ushort usagePage, ushort usage,
        byte[] report, out int value)
    {
        value = 0;
        if (preparsedData == IntPtr.Zero || report == null || report.Length == 0) return false;
        if (HidP_GetUsageValue(reportType, usagePage, 0, usage, out uint raw, preparsedData, report,
                (uint)report.Length) != HIDP_STATUS_SUCCESS)
            return false;
        value = unchecked((int)raw);
        return true;
    }
}
