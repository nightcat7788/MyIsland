using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Island.App.Services.Peripheral;

/// <summary>
/// 枚举 Windows 上所有 HID 顶层集合（GUID_DEVINTERFACE_HID）。
///
/// 为什么不能只靠 WMI：注册表里读不到 HID 的 usage / 报告长度，
/// 必须走 SetupAPI + HidP_GetCaps 才能知道"哪个集合是厂商私有通道"。
///
/// 两个关键取舍：
///  · 枚举阶段一律用 **dwDesiredAccess=0** 打开：系统独占的键鼠集合用普通权限根本打不开，
///    用 0 权限仍然能拿到属性、字符串、能力表。真正要发命令时才用读写权限重开（多半失败）。
///  · 单个集合读失败只跳过它自己，绝不影响整轮枚举（一个坏设备不该让整页设备消失）。
/// </summary>
internal static class HidBus
{
    /// <summary>枚举预算：超过就提前收手（设备多的机器上枚举本身要几十毫秒）。</summary>
    private const int EnumerateBudgetMs = 1400;

    /// <summary>接口数上限：防止异常驱动刷出成千上万个接口。</summary>
    private const int MaxInterfaces = 512;

    private static readonly object Gate = new();
    private static List<HidInterface>? _cache;
    private static DateTime _cacheAt;

    /// <summary>枚举结果缓存时长：同一轮采样里多个数据源共用一次枚举。</summary>
    public static int CacheTtlMs = 2500;

    /// <summary>带缓存的枚举。</summary>
    public static List<HidInterface> Enumerate()
    {
        lock (Gate)
        {
            if (_cache is not null && (DateTime.UtcNow - _cacheAt).TotalMilliseconds < CacheTtlMs)
                return new List<HidInterface>(_cache);
        }
        List<HidInterface> fresh = EnumerateFresh();
        lock (Gate)
        {
            _cache = fresh;
            _cacheAt = DateTime.UtcNow;
            return new List<HidInterface>(fresh);
        }
    }

    /// <summary>丢弃缓存（设备插拔后要重新枚举）。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _cache = null;
            _cacheAt = DateTime.MinValue;
        }
    }

    /// <summary>强制重新枚举（不走缓存）。</summary>
    public static List<HidInterface> EnumerateFresh()
    {
        var result = new List<HidInterface>();
        var sw = Stopwatch.StartNew();
        IntPtr set = IntPtr.Zero;
        try
        {
            Guid guid = HidNative.GuidDevInterfaceHid;
            set = HidNative.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero,
                HidNative.DIGCF_PRESENT | HidNative.DIGCF_DEVICEINTERFACE);
            if (set == HidNative.InvalidHandleValue || set == IntPtr.Zero)
                return result;

            for (int index = 0; index < MaxInterfaces; index++)
            {
                if (sw.ElapsedMilliseconds > EnumerateBudgetMs) break;

                var ifaceData = new HidNative.SpDeviceInterfaceData
                {
                    CbSize = Marshal.SizeOf<HidNative.SpDeviceInterfaceData>(),
                };
                if (!HidNative.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref ifaceData))
                    break;

                try
                {
                    HidInterface? info = ReadInterface(set, ref ifaceData);
                    if (info is not null) result.Add(info);
                }
                catch
                {
                    // 单个集合读失败：跳过它，不能拖垮整轮
                }
            }
        }
        catch
        {
            // 枚举整体失败：当作"这一轮没有 HID 设备"，不伪装
        }
        finally
        {
            if (set != IntPtr.Zero && set != HidNative.InvalidHandleValue)
                HidNative.SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    /// <summary>读一个接口的完整描述（属性 / 字符串 / 能力表）。</summary>
    private static HidInterface? ReadInterface(IntPtr set, ref HidNative.SpDeviceInterfaceData ifaceData)
    {
        int required = 0;
        HidNative.SetupDiGetDeviceInterfaceDetail(set, ref ifaceData, IntPtr.Zero, 0, ref required, IntPtr.Zero);
        if (required <= 4) return null;

        IntPtr detail = IntPtr.Zero;
        IntPtr devInfoPtr = IntPtr.Zero;
        IntPtr handle = IntPtr.Zero;
        IntPtr preparsed = IntPtr.Zero;
        try
        {
            var devInfo = new HidNative.SpDevInfoData
            {
                CbSize = Marshal.SizeOf<HidNative.SpDevInfoData>(),
            };
            devInfoPtr = Marshal.AllocHGlobal(devInfo.CbSize);
            Marshal.StructureToPtr(devInfo, devInfoPtr, false);

            detail = Marshal.AllocHGlobal(required);
            // x64 下 cbSize = 8（x86 为 6），DevicePath 从偏移 4 开始
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!HidNative.SetupDiGetDeviceInterfaceDetail(set, ref ifaceData, detail, required, ref required,
                    devInfoPtr))
                return null;

            devInfo = Marshal.PtrToStructure<HidNative.SpDevInfoData>(devInfoPtr);
            string? path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
            if (string.IsNullOrEmpty(path)) return null;

            var sbInstance = new StringBuilder(1024);
            int instanceNeeded = 0;
            string? instanceId = null;
            if (HidNative.SetupDiGetDeviceInstanceId(set, ref devInfo, sbInstance, sbInstance.Capacity,
                    ref instanceNeeded))
                instanceId = HidNative.CleanString(sbInstance.ToString());

            string friendly = ReadEnumValue(instanceId, "FriendlyName") ?? "";
            string deviceDesc = ReadEnumValue(instanceId, "DeviceDesc") ?? "";
            if (deviceDesc.Length == 0) deviceDesc = friendly;

            int vid = 0, pid = 0;
            string product = "", manufacturer = "", serial = "";
            ushort usagePage = 0, usage = 0;
            int inputLen = 0, outputLen = 0, featureLen = 0;
            var featureUsages = new List<HidUsageRange>();
            var inputUsages = new List<HidUsageRange>();

            // 只查询打开：被系统独占的集合也能读属性与能力表
            handle = HidNative.OpenForQuery(path);
            if (handle != HidNative.InvalidHandleValue && handle != IntPtr.Zero)
            {
                var attributes = new HidNative.HidAttributes { Size = HidNative.AttributesSize };
                if (HidNative.HidD_GetAttributes(handle, ref attributes))
                {
                    vid = attributes.VendorId;
                    pid = attributes.ProductId;
                }
                product = HidNative.GetProductString(handle) ?? "";
                manufacturer = HidNative.GetManufacturerString(handle) ?? "";
                serial = HidNative.GetSerialNumberString(handle) ?? "";

                if (HidNative.HidD_GetPreparsedData(handle, ref preparsed))
                {
                    var caps = new HidNative.HidCaps();
                    if (HidNative.HidP_GetCaps(preparsed, ref caps) == HidNative.HIDP_STATUS_SUCCESS)
                    {
                        usagePage = caps.UsagePage;
                        usage = caps.Usage;
                        inputLen = caps.InputReportByteLength;
                        outputLen = caps.OutputReportByteLength;
                        featureLen = caps.FeatureReportByteLength;
                        featureUsages.AddRange(HidNative.ReadValueCaps(preparsed, HidNative.HIDP_FEATURE,
                            caps.NumberFeatureValueCaps));
                        inputUsages.AddRange(HidNative.ReadValueCaps(preparsed, HidNative.HIDP_INPUT,
                            caps.NumberInputValueCaps));
                    }
                }
            }
            else
            {
                handle = IntPtr.Zero;
                if (!HidNative.TryParseVidPid(instanceId, out vid, out pid))
                    return null;   // 连 VID/PID 都认不出来的节点（虚拟总线）不进设备列表
            }

            if (product.Length == 0) product = friendly.Length > 0 ? friendly : deviceDesc;

            return new HidInterface
            {
                Path = path,
                InstanceId = instanceId ?? "",
                DeviceDesc = deviceDesc,
                Product = product,
                Manufacturer = manufacturer,
                Serial = serial,
                Vid = vid,
                Pid = pid,
                UsagePage = usagePage,
                Usage = usage,
                InputReportLength = inputLen,
                OutputReportLength = outputLen,
                FeatureReportLength = featureLen,
            }.WithUsages(featureUsages, inputUsages);
        }
        finally
        {
            if (preparsed != IntPtr.Zero) HidNative.HidD_FreePreparsedData(preparsed);
            if (handle != IntPtr.Zero && handle != HidNative.InvalidHandleValue) HidNative.CloseHandle(handle);
            if (detail != IntPtr.Zero) Marshal.FreeHGlobal(detail);
            if (devInfoPtr != IntPtr.Zero) Marshal.FreeHGlobal(devInfoPtr);
        }
    }

    /// <summary>读 HKLM\SYSTEM\CurrentControlSet\Enum\&lt;实例 ID&gt; 下的字符串值。</summary>
    private static string ReadEnumValue(string? instanceId, string valueName)
    {
        if (string.IsNullOrEmpty(instanceId)) return "";
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + instanceId);
            if (key is null) return "";
            return HidNative.CleanString(key.GetValue(valueName) as string) ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>把能力表挂到接口描述上（HidInterface 的集合属性只读，改用复制法）。</summary>
    private static HidInterface WithUsages(this HidInterface info,
        List<HidUsageRange> feature, List<HidUsageRange> input)
    {
        info.FeatureUsages.AddRange(feature);
        info.InputUsages.AddRange(input);
        return info;
    }

    /// <summary>
    /// 按 VID:PID 分组：一台 2.4G 接收器会露出多个集合，设备列表里应该只占一行。
    /// </summary>
    public static List<HidGroup> GroupByDevice(List<HidInterface> interfaces)
    {
        var groups = new List<HidGroup>();
        var map = new Dictionary<string, HidGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in interfaces)
        {
            if (it.Vid == 0 && it.Pid == 0) continue;
            string key = $"{it.Vid:X4}:{it.Pid:X4}";
            if (!map.TryGetValue(key, out HidGroup? group))
            {
                group = new HidGroup { Vid = it.Vid, Pid = it.Pid };
                map[key] = group;
                groups.Add(group);
            }
            group.Members.Add(it);
        }
        return groups;
    }
}

/// <summary>同一个 VID:PID 下的所有 HID 集合（一台物理外设）。</summary>
internal sealed class HidGroup
{
    public int Vid { get; init; }
    public int Pid { get; init; }
    public List<HidInterface> Members { get; } = new();

    /// <summary>组里最像"产品名"的那个字符串（产品名 > 设备描述 > 厂商名）。</summary>
    public string Name
    {
        get
        {
            foreach (var m in Members) if (m.Product.Length > 0) return m.Product;
            foreach (var m in Members) if (m.DeviceDesc.Length > 0) return m.DeviceDesc;
            return "HID 设备";
        }
    }

    /// <summary>厂商名（组内第一个非空的）。</summary>
    public string Manufacturer
    {
        get
        {
            foreach (var m in Members) if (m.Manufacturer.Length > 0) return m.Manufacturer;
            return "";
        }
    }

    /// <summary>组里的厂商自定义集合（私有协议的入口）。</summary>
    public IEnumerable<HidInterface> VendorMembers => Members.Where(m => m.IsVendorDefined);

    /// <summary>组里所有集合的 usage 描述（给人看的）。</summary>
    public string UsageSummary =>
        string.Join(", ", Members.Select(m => $"{m.UsagePage:X4}:{m.Usage:X4}"));
}
