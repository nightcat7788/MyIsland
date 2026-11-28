using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Island.Core.Monitoring;
using Microsoft.Win32.SafeHandles;

namespace Island.App.Services;

/// <summary>
/// Windows 原生 2.4GHz / USB HID 无线外设电量采样器（纯 Win32 setupapi + hid.dll，零外部驱动、零 NuGet 依赖）。
/// 参考 Sapd/HeadsetControl 开源协议，支持：
///  - Logitech G 系列无线耳机（HID++ 电压曲线）+ Logitech Lightspeed / Unifying / Bolt 无线键鼠（HID++ 2.0 0x1004/0x1000）
///  - Corsair Void / Void Pro / Void Elite / HS 系列无线耳机
///  - SteelSeries Arctis 1/7/7+/9/Pro 与 Nova 5/7/Pro 系列无线耳机
///  - HyperX Cloud Flight / Cloud II Wireless / Cloud Alpha Wireless
/// </summary>
internal static class HidPeripheralBatteryService
{
    private static readonly Dictionary<string, (int Failures, DateTime BackoffUntil)> _backoff = new(StringComparer.OrdinalIgnoreCase);
    private const int ReadTimeoutMs = 450;

    /// <summary>扫描当前所有已连接的 2.4GHz / USB HID 无线外设并读取真实电量（读不到不输出）。</summary>
    public static List<DeviceInfo> SampleAll(Action<string>? log = null)
    {
        var results = new List<DeviceInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr devInfoSet = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
            return results;

        try
        {
            var ifaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint idx = 0; SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, ref hidGuid, idx, ref ifaceData); idx++)
            {
                string? path = GetDevicePath(devInfoSet, ref ifaceData);
                if (string.IsNullOrEmpty(path)) continue;

                // 快速路径预筛：非受支持厂商 VID 直接跳过，避免对数十个主板/触控板/键鼠 HID 节点执行 CreateFileW 系统调用
                if (!HidPeripheralProtocol.IsSupportedVendorInPath(path)) continue;

                if (_backoff.TryGetValue(path, out var bo) && DateTime.UtcNow < bo.BackoffUntil)
                    continue;

                if (!TryReadMetadata(path, out ushort vid, out ushort pid, out ushort usagePage, out ushort usage,
                        out int inputLen, out int outputLen, out string? productName))
                {
                    continue;
                }

                // 1) 优先匹配精确的 HeadsetControl 无线耳机协议档案
                var profile = HidPeripheralProtocol.FindProfile(vid, pid, usagePage, usage);
                if (profile is not null)
                {
                    string key = $"HID:{vid:X4}:{pid:X4}";
                    if (!seen.Add(key)) continue;

                    try
                    {
                        var reading = QueryProfile(path, profile, inputLen, outputLen);
                        if (reading is { IsValid: true })
                        {
                            ClearBackoff(path);
                            string name = !string.IsNullOrWhiteSpace(productName) ? productName! : profile.DisplayName;
                            string sub = FormatSubtitle(reading);
                            results.Add(new DeviceInfo(
                                name, profile.DeviceKind, reading.Percent,
                                reading.Status == HidBatteryStatus.Charging, sub));
                        }
                    }
                    catch (Exception ex)
                    {
                        RecordFailure(path);
                        log?.Invoke($"HID 外设查询异常 ({profile.DisplayName}): {ex.GetType().Name}: {ex.Message}");
                    }
                    continue;
                }

                // 2) Logitech HID++ 2.0 通用接收器 / 无线键鼠（Lightspeed / Unifying / Bolt）
                if (HidPeripheralProtocol.IsLogitechHidpp20Interface(vid, usagePage, usage) && outputLen >= 20 && inputLen >= 7)
                {
                    string key = $"HIDPP20:{vid:X4}:{pid:X4}";
                    if (!seen.Add(key)) continue;

                    try
                    {
                        var logiDevices = QueryLogitechHidpp20(path, productName, inputLen, outputLen);
                        if (logiDevices.Count > 0)
                        {
                            ClearBackoff(path);
                            results.AddRange(logiDevices);
                        }
                    }
                    catch (Exception ex)
                    {
                        RecordFailure(path);
                        log?.Invoke($"Logitech HID++ 2.0 查询异常 ({vid:X4}:{pid:X4}): {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        return results;
    }

    private static string FormatSubtitle(HidBatteryReading r)
    {
        if (r.Status == HidBatteryStatus.Charging)
            return r.VoltageMv is > 0 ? $"充电中 · {r.VoltageMv / 1000.0:F2}V" : "2.4G · 充电中";
        if (r.MicMuted == true)
            return "2.4G · 麦克风静音";
        if (r.VoltageMv is > 2500 and < 4600)
            return $"2.4G · {r.VoltageMv / 1000.0:F2}V";
        return "2.4G 无线";
    }

    private static void ClearBackoff(string path) => _backoff.Remove(path);

    private static void RecordFailure(string path)
    {
        _backoff.TryGetValue(path, out var cur);
        int fails = cur.Failures + 1;
        DateTime until = fails >= 3 ? DateTime.UtcNow.AddSeconds(60) : DateTime.MinValue;
        _backoff[path] = (fails, until);
    }

    private static HidBatteryReading? QueryProfile(string path, HidDeviceProfile profile, int inputLen, int outputLen)
    {
        using var handle = OpenIoHandle(path);
        if (handle is null || handle.IsInvalid) return null;

        int readSize = Math.Max(inputLen, profile.ResponseLength);

        if (profile.Protocol == HidProtocolKind.HyperXCloud2Wireless)
        {
            var reqLvl = BuildPadded(new byte[] { 0x06, 0xFF, 0xBB, 0x02 }, Math.Max(52, outputLen));
            var respLvl = Transceive(handle, reqLvl, readSize, ReadTimeoutMs);
            if (respLvl is null) return null;

            var reqChg = BuildPadded(new byte[] { 0x06, 0xFF, 0xBB, 0x03 }, Math.Max(52, outputLen));
            var respChg = Transceive(handle, reqChg, readSize, ReadTimeoutMs);
            if (respChg is null) return null;

            return HidPeripheralProtocol.ParseHyperXCloud2Wireless(respLvl, respChg);
        }

        if (profile.Protocol == HidProtocolKind.HyperXCloudAlphaWireless)
        {
            int reqLen = Math.Max(31, outputLen);
            var connResp = Transceive(handle, BuildPadded(new byte[] { 0x21, 0xBB, 0x03 }, reqLen), readSize, ReadTimeoutMs);
            if (connResp is null || connResp.Length < 4 || connResp[3] == 0x01)
                return new HidBatteryReading(-1, HidBatteryStatus.Unavailable);

            var chargeResp = Transceive(handle, BuildPadded(new byte[] { 0x21, 0xBB, 0x0C }, reqLen), readSize, ReadTimeoutMs);
            if (chargeResp is { Length: >= 4 } && chargeResp[3] == 0x01)
                return new HidBatteryReading(-1, HidBatteryStatus.Charging);

            var batResp = Transceive(handle, BuildPadded(new byte[] { 0x21, 0xBB, 0x0B }, reqLen), readSize, ReadTimeoutMs);
            if (batResp is null) return null;
            return HidPeripheralProtocol.ParseHyperXCloudAlpha(connResp, chargeResp ?? Array.Empty<byte>(), batResp);
        }

        var req = HidPeripheralProtocol.BuildRequest(profile, outputLen);
        var resp = Transceive(handle, req, readSize, ReadTimeoutMs);
        if (resp is null) return null;
        return HidPeripheralProtocol.ParseSingleResponse(profile, resp);
    }

    private static List<DeviceInfo> QueryLogitechHidpp20(string path, string? productName, int inputLen, int outputLen)
    {
        var list = new List<DeviceInfo>();
        using var handle = OpenIoHandle(path);
        if (handle is null || handle.IsInvalid) return list;

        // 0xFF = 直连/单设备接收器，0x01..0x06 = Unifying / Bolt / Lightspeed 接收器槽位
        byte[] slots = { 0xFF, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        const byte swId = 0x0A;

        foreach (byte devIdx in slots)
        {
            // 先查 Feature 0x1004 (Unified Battery)，再回退 Feature 0x1000 (Battery Status)
            bool isUnified = true;
            byte? featIdx = QueryHidpp20FeatureIndex(handle, devIdx, 0x10, 0x04, swId, inputLen, outputLen);
            if (featIdx is null)
            {
                isUnified = false;
                featIdx = QueryHidpp20FeatureIndex(handle, devIdx, 0x10, 0x00, swId, inputLen, outputLen);
            }
            if (featIdx is null)
            {
                if (devIdx == 0xFF) continue;
                continue;
            }

            byte func = isUnified ? (byte)(0x10 | swId) : (byte)(0x00 | swId);
            var req = HidPeripheralProtocol.BuildHidpp20LongRequest(devIdx, featIdx.Value, func, 0, 0, outputLen);
            var resp = TransceiveHidpp20(handle, req, devIdx, featIdx.Value, inputLen);
            if (resp is null) continue;

            var reading = HidPeripheralProtocol.ParseLogitechHidpp20Battery(resp, isUnified);
            if (!reading.IsValid) continue;

            string name = !string.IsNullOrWhiteSpace(productName) ? productName! : "Logitech Wireless";
            if (devIdx != 0xFF && list.Count > 0) name = $"{name} #{devIdx}";
            string kind = InferLogitechKind(name);
            list.Add(new DeviceInfo(name, kind, reading.Percent, reading.Status == HidBatteryStatus.Charging, FormatSubtitle(reading)));

            // 若直连槽位 0xFF 已命中，避免在同一单设备接收器上重复扫描 1..6
            if (devIdx == 0xFF) break;
        }

        return list;
    }

    private static byte? QueryHidpp20FeatureIndex(
        SafeFileHandle handle, byte devIdx, byte featHi, byte featLo, byte swId, int inputLen, int outputLen)
    {
        var req = HidPeripheralProtocol.BuildHidpp20LongRequest(devIdx, 0x00, swId, featHi, featLo, outputLen);
        var resp = TransceiveHidpp20(handle, req, devIdx, 0x00, inputLen);
        if (resp is null || resp.Length < 5) return null;
        if (resp[2] == 0xFF || resp[2] == 0x8F) return null;
        return resp[4] == 0 ? null : resp[4];
    }

    private static byte[]? TransceiveHidpp20(SafeFileHandle handle, byte[] request, byte expectedDevIdx, byte expectedFeatIdx, int inputLen)
    {
        if (!WriteOverlapped(handle, request, 200)) return null;

        var deadline = Environment.TickCount64 + ReadTimeoutMs;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            int rem = (int)(deadline - Environment.TickCount64);
            if (rem <= 0) break;
            var resp = ReadOverlapped(handle, Math.Max(inputLen, 20), rem);
            if (resp is null || resp.Length < 7) break;
            if ((resp[0] == 0x10 || resp[0] == 0x11) && resp[1] == expectedDevIdx)
            {
                if (resp[2] == expectedFeatIdx || resp[2] == 0xFF || resp[2] == 0x8F)
                    return resp;
            }
        }
        return null;
    }

    private static string InferLogitechKind(string name)
    {
        string low = name.ToLowerInvariant();
        if (low.Contains("headset") || low.Contains("g733") || low.Contains("g535") || low.Contains("g533")
            || low.Contains("g935") || low.Contains("g933") || low.Contains("g435") || low.Contains("astro"))
            return "headset";
        if (low.Contains("keyboard") || low.Contains("mx keys") || low.Contains("g915") || low.Contains("g715") || low.Contains("k380"))
            return "keyboard";
        return "mouse";
    }

    private static byte[] BuildPadded(byte[] prefix, int totalLength)
    {
        var buf = new byte[Math.Max(prefix.Length, totalLength)];
        Array.Copy(prefix, buf, prefix.Length);
        return buf;
    }

    private static byte[]? Transceive(SafeFileHandle handle, byte[] request, int readSize, int timeoutMs)
    {
        if (!WriteOverlapped(handle, request, 250))
            return null;
        return ReadOverlapped(handle, readSize, timeoutMs);
    }

    // ==================== Win32 Overlapped HID I/O ====================

    private static SafeFileHandle? OpenIoHandle(string path)
    {
        var h = CreateFileW(
            path,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);
        return h.IsInvalid ? null : h;
    }

    private static bool WriteOverlapped(SafeFileHandle handle, byte[] data, int timeoutMs)
    {
        using var ev = new ManualResetEvent(false);
        var ov = new NativeOverlapped { EventHandle = ev.SafeWaitHandle.DangerousGetHandle() };
        bool ok = WriteFile(handle, data, (uint)data.Length, out _, ref ov);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != ERROR_IO_PENDING) return false;
            if (!ev.WaitOne(timeoutMs))
            {
                CancelIoEx(handle, IntPtr.Zero);
                return false;
            }
            if (!GetOverlappedResult(handle, ref ov, out _, false))
                return false;
        }
        return true;
    }

    private static byte[]? ReadOverlapped(SafeFileHandle handle, int length, int timeoutMs)
    {
        if (length <= 0) return null;
        var buf = new byte[length];
        using var ev = new ManualResetEvent(false);
        var ov = new NativeOverlapped { EventHandle = ev.SafeWaitHandle.DangerousGetHandle() };
        bool ok = ReadFile(handle, buf, (uint)buf.Length, out uint bytesRead, ref ov);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != ERROR_IO_PENDING) return null;
            if (!ev.WaitOne(timeoutMs))
            {
                CancelIoEx(handle, IntPtr.Zero);
                return null;
            }
            if (!GetOverlappedResult(handle, ref ov, out bytesRead, false))
                return null;
        }
        if (bytesRead == 0) return null;
        if (bytesRead < buf.Length) Array.Resize(ref buf, (int)bytesRead);
        return buf;
    }

    private static bool TryReadMetadata(
        string path, out ushort vid, out ushort pid, out ushort usagePage, out ushort usage,
        out int inputLen, out int outputLen, out string? productName)
    {
        vid = pid = usagePage = usage = 0;
        inputLen = outputLen = 0;
        productName = null;

        using var h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return false;

        var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(h, ref attrs)) return false;
        vid = attrs.VendorID;
        pid = attrs.ProductID;

        if (HidD_GetPreparsedData(h, out IntPtr preparsed) && preparsed != IntPtr.Zero)
        {
            try
            {
                if (HidP_GetCaps(preparsed, out var caps) == HIDP_STATUS_SUCCESS)
                {
                    usagePage = caps.UsagePage;
                    usage = caps.Usage;
                    inputLen = caps.InputReportByteLength;
                    outputLen = caps.OutputReportByteLength;
                }
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        var strBuf = new byte[256];
        if (HidD_GetProductString(h, strBuf, (uint)strBuf.Length))
        {
            string s = Encoding.Unicode.GetString(strBuf);
            int nul = s.IndexOf('\0');
            if (nul >= 0) s = s[..nul];
            s = s.Trim();
            if (s.Length > 0) productName = s;
        }

        return usagePage != 0 || inputLen > 0;
    }

    private static string? GetDevicePath(IntPtr devInfoSet, ref SP_DEVICE_INTERFACE_DATA ifaceData)
    {
        SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
        if (reqSize < 6 || reqSize > 4096) return null;

        IntPtr buf = Marshal.AllocHGlobal((int)reqSize);
        try
        {
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData, buf, reqSize, out _, IntPtr.Zero))
                return null;
            return Marshal.PtrToStringUni(IntPtr.Add(buf, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // ==================== Win32 P/Invoke 声明 ====================

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int ERROR_IO_PENDING = 997;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetProductString(SafeFileHandle hidDeviceObject, byte[] buffer, uint bufferLength);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
        out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle hFile, ref NativeOverlapped lpOverlapped,
        out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);
}
