using System;
using System.Collections.Generic;

namespace Island.Core.Monitoring;

/// <summary>无线外设 HID 电池状态（对齐 Sapd/HeadsetControl 语义）。</summary>
public enum HidBatteryStatus
{
    Unavailable = 0,
    Available = 1,
    Charging = 2,
}

/// <summary>单次 HID 电池解析结果（Percent 为 -1 表示充电中但固件未回报具体百分比，绝不伪造）。</summary>
public sealed record HidBatteryReading(
    int Percent,
    HidBatteryStatus Status,
    int? VoltageMv = null,
    bool? MicMuted = null)
{
    public bool IsValid => Status != HidBatteryStatus.Unavailable && (Percent is >= 0 and <= 100 || Status == HidBatteryStatus.Charging);
}

/// <summary>无线外设 HID 协议族（参考 Sapd/HeadsetControl 开源协议定义）。</summary>
public enum HidProtocolKind
{
    LogitechHidppVoltage,
    LogitechHidpp20Unified,
    CorsairVoid,
    SteelSeriesArctis7,
    SteelSeriesArctis1,
    SteelSeriesArctis9,
    SteelSeriesArctis7Plus,
    SteelSeriesNova7Discrete,
    SteelSeriesNova7Percent,
    SteelSeriesNova5,
    SteelSeriesNovaPro,
    HyperXCloudFlight,
    HyperXCloud2Wireless,
    HyperXCloudAlphaWireless,
}

/// <summary>单个已验证的 2.4G / USB HID 无线外设协议档案。</summary>
public sealed record HidDeviceProfile(
    ushort VendorId,
    ushort ProductId,
    string DisplayName,
    string DeviceKind,
    HidProtocolKind Protocol,
    ushort UsagePage,
    ushort UsageId,
    byte[] CommandBytes,
    int RequestLength,
    int ResponseLength,
    int CalibrationIndex = 0);

/// <summary>
/// 跨平台 2.4GHz / USB HID 无线外设电量协议表与报文解析器（纯 .NET，零 OS 依赖）。
/// 协议字节、VID/PID 与电压放电曲线参考开源项目 Sapd/HeadsetControl (GPL-3.0 协议研究参考实现)。
/// 支持：Logitech G 系列 / HID++ 2.0、Corsair Void/HS 系列、SteelSeries Arctis/Nova 系列、HyperX Cloud 系列。
/// </summary>
public static class HidPeripheralProtocol
{
    public const ushort VendorLogitech = 0x046D;
    public const ushort VendorSteelSeries = 0x1038;
    public const ushort VendorCorsair = 0x1B1C;
    public const ushort VendorHyperXKingston = 0x0951;
    public const ushort VendorHyperXHp = 0x03F0;

    // Logitech 电压放电曲线（mV -> %，降序排列，参考 HeadsetControl logitech_calibrations.hpp）
    private static readonly (int[] Pct, int[] Mv)[] LogitechCalibrations =
    {
        // 0: Default
        (new[] { 100, 50, 30, 20, 5, 0 }, new[] { 4100, 3850, 3780, 3730, 3650, 3300 }),
        // 1: G533
        (new[] { 100, 50, 30, 20, 5, 0 }, new[] { 4200, 3850, 3790, 3750, 3680, 3330 }),
        // 2: G535
        (new[] { 100, 50, 30, 20, 5, 0 }, new[] { 4175, 3817, 3766, 3730, 3664, 3310 }),
        // 3: G633 / G635 / G733 / G933 / G935
        (new[] { 100, 80, 60, 40, 20, 10, 5, 0 }, new[] { 4100, 3950, 3850, 3750, 3650, 3500, 3300, 3150 }),
        // 4: G Pro / G Pro X / G Pro X2
        (new[] { 100, 50, 30, 20, 5, 0 }, new[] { 4150, 3830, 3780, 3740, 3670, 3320 }),
    };

    private static readonly HidDeviceProfile[] Profiles = BuildProfiles();

    public static IReadOnlyList<HidDeviceProfile> AllProfiles => Profiles;

    private static HidDeviceProfile[] BuildProfiles()
    {
        var list = new List<HidDeviceProfile>();

        // 1. Logitech G 系列无线耳机（HID++ 电压协议）
        list.Add(new(VendorLogitech, 0x0A66, "Logitech G533", "headset", HidProtocolKind.LogitechHidppVoltage, 0xFF43, 0x0202, new byte[] { 0x07, 0x01 }, 20, 7, 1));
        list.Add(new(VendorLogitech, 0x0AC4, "Logitech G535", "headset", HidProtocolKind.LogitechHidppVoltage, 0x000C, 0x0001, new byte[] { 0x05, 0x0D }, 20, 7, 2));

        foreach (var (pid, name) in new (ushort, string)[]
        {
            (0x0A5C, "Logitech G633"), (0x0A89, "Logitech G635"),
            (0x0A5B, "Logitech G933"), (0x0A87, "Logitech G935"),
            (0x0AB5, "Logitech G733"), (0x0AFE, "Logitech G733"), (0x0B1F, "Logitech G733"),
        })
        {
            list.Add(new(VendorLogitech, pid, name, "headset", HidProtocolKind.LogitechHidppVoltage, 0xFF43, 0x0202, new byte[] { 0x08, 0x0A }, 20, 7, 3));
        }

        foreach (var (pid, name) in new (ushort, string)[]
        {
            (0x0AA7, "Logitech G PRO"), (0x0AAA, "Logitech G PRO X"), (0x0ABA, "Logitech G PRO X"),
            (0x0AFB, "Logitech G PRO X2"), (0x0AFC, "Logitech G PRO X2"),
        })
        {
            list.Add(new(VendorLogitech, pid, name, "headset", HidProtocolKind.LogitechHidppVoltage, 0xFF43, 0x0202, new byte[] { 0x06, 0x0D }, 20, 7, 4));
        }

        // 2. Corsair Void / Void Pro / Void Elite / HS 系列
        foreach (ushort pid in new ushort[]
        {
            0x1B1C, 0x1B27, 0x0A14, 0x0A16, 0x0A17, 0x0A1D, 0x0A1A,
            0x1B2A, 0x1B23, 0x1B29, 0x0A55, 0x0A51, 0x0A52, 0x0A38,
            0x0A4F, 0x0A2B, 0x0A75, 0x0A56,
        })
        {
            list.Add(new(VendorCorsair, pid, "Corsair Wireless Headset", "headset", HidProtocolKind.CorsairVoid, 0xFFC5, 0x0001, new byte[] { 0xC9, 0x64 }, 2, 5));
        }

        // 3. SteelSeries Arctis Legacy 系列
        foreach (var (pid, name) in new (ushort, string)[]
        {
            (0x1260, "SteelSeries Arctis 7"), (0x12AD, "SteelSeries Arctis 7 (2019)"),
            (0x1252, "SteelSeries Arctis Pro"), (0x1280, "SteelSeries Arctis Pro GameDAC"),
        })
        {
            list.Add(new(VendorSteelSeries, pid, name, "headset", HidProtocolKind.SteelSeriesArctis7, 0xFF43, 0x0202, new byte[] { 0x06, 0x18 }, 31, 8));
        }

        foreach (var (pid, name) in new (ushort, string)[]
        {
            (0x12B3, "SteelSeries Arctis 1"), (0x12B6, "SteelSeries Arctis 1 Xbox"),
            (0x12D7, "SteelSeries Arctis 7X"), (0x12D5, "SteelSeries Arctis 7P"),
        })
        {
            list.Add(new(VendorSteelSeries, pid, name, "headset", HidProtocolKind.SteelSeriesArctis1, 0xFF43, 0x0202, new byte[] { 0x06, 0x12 }, 31, 8));
        }

        foreach (ushort pid in new ushort[] { 0x12C2, 0x12C4 })
        {
            list.Add(new(VendorSteelSeries, pid, "SteelSeries Arctis 9", "headset", HidProtocolKind.SteelSeriesArctis9, 0xFF43, 0x0202, new byte[] { 0x00, 0x20 }, 31, 12));
        }

        // 4. SteelSeries Arctis 7+ & Nova 系列
        foreach (var (pid, name) in new (ushort, string)[]
        {
            (0x220E, "SteelSeries Arctis 7+"), (0x2212, "SteelSeries Arctis 7+ PS5"),
            (0x2216, "SteelSeries Arctis 7+ Xbox"), (0x2236, "SteelSeries Arctis 7+"),
        })
        {
            list.Add(new(VendorSteelSeries, pid, name, "headset", HidProtocolKind.SteelSeriesArctis7Plus, 0xFFC0, 0x0001, new byte[] { 0x00, 0xB0 }, 64, 64));
        }

        foreach (var (pid, name) in new (ushort, string)[]
        {
            (0x2202, "SteelSeries Arctis Nova 7"), (0x2206, "SteelSeries Arctis Nova 7X"),
            (0x220A, "SteelSeries Arctis Nova 7P"), (0x223A, "SteelSeries Arctis Nova 7"),
            (0x227A, "SteelSeries Arctis Nova 7"), (0x22A4, "SteelSeries Arctis Nova 7X"),
        })
        {
            list.Add(new(VendorSteelSeries, pid, name, "headset", HidProtocolKind.SteelSeriesNova7Discrete, 0xFFC0, 0x0001, new byte[] { 0x00, 0xB0 }, 64, 64));
        }

        foreach (var (pid, name) in new (ushort, string)[]
        {
            (0x22A1, "SteelSeries Arctis Nova 7"), (0x227E, "SteelSeries Arctis Nova 7 Gen 2"),
            (0x2258, "SteelSeries Arctis Nova 7X"), (0x229E, "SteelSeries Arctis Nova 7X"),
            (0x22AD, "SteelSeries Arctis Nova 7X"), (0x22A9, "SteelSeries Arctis Nova 7"),
            (0x22A5, "SteelSeries Arctis Nova 7X"), (0x22A7, "SteelSeries Arctis Nova 7P"),
        })
        {
            list.Add(new(VendorSteelSeries, pid, name, "headset", HidProtocolKind.SteelSeriesNova7Percent, 0xFFC0, 0x0001, new byte[] { 0x00, 0xB0 }, 64, 64));
        }

        foreach (var (pid, name) in new (ushort, string)[]
        {
            (0x2232, "SteelSeries Arctis Nova 5"), (0x2253, "SteelSeries Arctis Nova 5X"),
        })
        {
            list.Add(new(VendorSteelSeries, pid, name, "headset", HidProtocolKind.SteelSeriesNova5, 0xFFC0, 0x0001, new byte[] { 0x00, 0xB0 }, 64, 64));
        }

        foreach (var (pid, name) in new (ushort, string)[]
        {
            (0x12E0, "SteelSeries Arctis Nova Pro"), (0x12E5, "SteelSeries Arctis Nova Pro X"),
        })
        {
            list.Add(new(VendorSteelSeries, pid, name, "headset", HidProtocolKind.SteelSeriesNovaPro, 0xFFC0, 0x0001, new byte[] { 0x06, 0xB0 }, 31, 64));
        }

        // 5. HyperX Cloud 系列无线耳机
        foreach (ushort pid in new ushort[] { 0x16C4, 0x1723 })
        {
            list.Add(new(VendorHyperXKingston, pid, "HyperX Cloud Flight", "headset", HidProtocolKind.HyperXCloudFlight, 0, 0, new byte[] { 0x21, 0xFF, 0x05 }, 20, 20));
        }

        foreach (ushort pid in new ushort[] { 0x0696, 0x018B })
        {
            list.Add(new(VendorHyperXHp, pid, "HyperX Cloud II Wireless", "headset", HidProtocolKind.HyperXCloud2Wireless, 0xFF90, 0x0303, new byte[] { 0x06, 0xFF, 0xBB, 0x02 }, 52, 20));
        }

        list.Add(new(VendorHyperXHp, 0x098D, "HyperX Cloud Alpha Wireless", "headset", HidProtocolKind.HyperXCloudAlphaWireless, 0, 0, new byte[] { 0x21, 0xBB, 0x0B }, 31, 31));

        return list.ToArray();
    }

    /// <summary>按 VID/PID 与 UsagePage/UsageId 匹配精确外设协议（UsagePage=0 表示通配）。</summary>
    public static HidDeviceProfile? FindProfile(ushort vendorId, ushort productId, ushort usagePage, ushort usageId)
    {
        foreach (var p in Profiles)
        {
            if (p.VendorId != vendorId || p.ProductId != productId) continue;
            if (p.UsagePage != 0 && p.UsagePage != usagePage) continue;
            if (p.UsageId != 0 && p.UsageId != usageId) continue;
            return p;
        }
        return null;
    }

    /// <summary>判断是否为 Logitech HID++ 2.0 通用接收器/无线键鼠接口（VID 0x046D, UsagePage 0xFF00, Usage 1/2）。</summary>
    public static bool IsLogitechHidpp20Interface(ushort vendorId, ushort usagePage, ushort usageId)
        => vendorId == VendorLogitech && usagePage == 0xFF00 && (usageId == 0x0001 || usageId == 0x0002);

    /// <summary>
    /// 快速路径预筛：Windows HID 设备路径恒含 `vid_XXXX`。
    /// 在调用 CreateFileW / HidD_GetPreparsedData 之前先做纯内存字符串匹配，可跳过 95%+ 无关系统 HID 节点，大幅降低 CPU 与内核态切换开销。
    /// </summary>
    public static bool IsSupportedVendorInPath(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return false;
        return devicePath.Contains("vid_046d", StringComparison.OrdinalIgnoreCase)
            || devicePath.Contains("vid_1038", StringComparison.OrdinalIgnoreCase)
            || devicePath.Contains("vid_1b1c", StringComparison.OrdinalIgnoreCase)
            || devicePath.Contains("vid_0951", StringComparison.OrdinalIgnoreCase)
            || devicePath.Contains("vid_03f0", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>构造发送给设备的完整 HID 请求报文（自动填充目标长度）。</summary>
    public static byte[] BuildRequest(HidDeviceProfile profile, int outputReportLength = 0)
    {
        int len = Math.Max(profile.RequestLength, outputReportLength);
        var buf = new byte[len];
        if (profile.Protocol == HidProtocolKind.LogitechHidppVoltage)
        {
            buf[0] = 0x11; // HIDPP_LONG_MESSAGE
            buf[1] = 0xFF; // HIDPP_DEVICE_RECEIVER
            Array.Copy(profile.CommandBytes, 0, buf, 2, Math.Min(profile.CommandBytes.Length, len - 2));
        }
        else
        {
            Array.Copy(profile.CommandBytes, 0, buf, 0, Math.Min(profile.CommandBytes.Length, len));
        }
        return buf;
    }

    /// <summary>构造 HID++ 2.0 请求帧（ReportId 0x11 长帧，20 字节）。</summary>
    public static byte[] BuildHidpp20LongRequest(byte deviceIndex, byte featureIndex, byte functionAndSwId, byte p0 = 0, byte p1 = 0, int outputReportLength = 20)
    {
        int len = Math.Max(20, outputReportLength);
        var buf = new byte[len];
        buf[0] = 0x11;
        buf[1] = deviceIndex;
        buf[2] = featureIndex;
        buf[3] = functionAndSwId;
        buf[4] = p0;
        buf[5] = p1;
        return buf;
    }

    /// <summary>解析单包响应协议（Logitech HID++ 电压 / Corsair Void / SteelSeries 全系 / HyperX Cloud Flight）。</summary>
    public static HidBatteryReading ParseSingleResponse(HidDeviceProfile profile, ReadOnlySpan<byte> response)
    {
        return profile.Protocol switch
        {
            HidProtocolKind.LogitechHidppVoltage => ParseLogitechHidppVoltage(response, profile.CalibrationIndex),
            HidProtocolKind.CorsairVoid => ParseCorsairVoid(response),
            HidProtocolKind.SteelSeriesArctis7 => ParseSteelSeriesDirect(response, batteryByte: 2, statusByte: -1, offlineVal: 0, chargingVal: 0xFF, min: 0, max: 100),
            HidProtocolKind.SteelSeriesArctis1 => ParseSteelSeriesDirect(response, batteryByte: 3, statusByte: 2, offlineVal: 0x01, chargingVal: 0xFF, min: 0, max: 100),
            HidProtocolKind.SteelSeriesArctis9 => ParseSteelSeriesDirect(response, batteryByte: 3, statusByte: 4, offlineVal: 0xFF, chargingVal: 0x01, min: 0x64, max: 0x9A),
            HidProtocolKind.SteelSeriesArctis7Plus => ParseSteelSeriesNova(response, offlineByte: 1, offlineVal: 0x01, chargingByte: 3, chargingVal1: 0x01, chargingVal2: 0x01, batteryByte: 2, maxRaw: 4),
            HidProtocolKind.SteelSeriesNova7Discrete => ParseSteelSeriesNova(response, offlineByte: 3, offlineVal: 0x00, chargingByte: 3, chargingVal1: 0x01, chargingVal2: 0x02, batteryByte: 2, maxRaw: 4),
            HidProtocolKind.SteelSeriesNova7Percent => ParseSteelSeriesNova(response, offlineByte: 3, offlineVal: 0x00, chargingByte: 3, chargingVal1: 0x01, chargingVal2: 0x02, batteryByte: 2, maxRaw: 100),
            HidProtocolKind.SteelSeriesNova5 => ParseSteelSeriesNova(response, offlineByte: 1, offlineVal: 0x02, chargingByte: 4, chargingVal1: 0x01, chargingVal2: 0x01, batteryByte: 3, maxRaw: 100),
            HidProtocolKind.SteelSeriesNovaPro => ParseSteelSeriesNovaPro(response),
            HidProtocolKind.HyperXCloudFlight => ParseHyperXCloudFlight(response),
            _ => new HidBatteryReading(-1, HidBatteryStatus.Unavailable),
        };
    }

    public static HidBatteryReading ParseLogitechHidppVoltage(ReadOnlySpan<byte> response, int calibrationIndex = 0)
    {
        if (response.Length < 7) return new(-1, HidBatteryStatus.Unavailable);
        if (response[2] == 0xFF) return new(-1, HidBatteryStatus.Unavailable); // 设备离线

        int voltageMv = (response[4] << 8) | response[5];
        var (pcts, mvs) = LogitechCalibrations[Math.Clamp(calibrationIndex, 0, LogitechCalibrations.Length - 1)];
        if (voltageMv < mvs[^1]) return new(-1, HidBatteryStatus.Unavailable, voltageMv);

        var status = response[6] == 0x03 ? HidBatteryStatus.Charging : HidBatteryStatus.Available;
        int pct = InterpolateVoltage(pcts, mvs, voltageMv);
        return new(pct, status, voltageMv);
    }

    public static HidBatteryReading ParseCorsairVoid(ReadOnlySpan<byte> response)
    {
        if (response.Length < 5) return new(-1, HidBatteryStatus.Unavailable);
        byte st = response[4];
        if (st == 0) return new(-1, HidBatteryStatus.Unavailable);

        bool charging = st is 4 or 5;
        if (st is not (1 or 2 or 4 or 5)) return new(-1, HidBatteryStatus.Unavailable);

        byte raw = response[2];
        bool micUp = (raw & 0x80) != 0;
        int pct = Math.Clamp(raw & 0x7F, 0, 100);
        return new(pct, charging ? HidBatteryStatus.Charging : HidBatteryStatus.Available, MicMuted: micUp);
    }

    public static HidBatteryReading ParseSteelSeriesDirect(
        ReadOnlySpan<byte> response, int batteryByte, int statusByte,
        byte offlineVal, byte chargingVal, int min, int max)
    {
        if (batteryByte < 0 || batteryByte >= response.Length)
            return new(-1, HidBatteryStatus.Unavailable);
        if (statusByte >= 0 && statusByte < response.Length && response[statusByte] == offlineVal)
            return new(-1, HidBatteryStatus.Unavailable);

        var status = (statusByte >= 0 && statusByte < response.Length && response[statusByte] == chargingVal)
            ? HidBatteryStatus.Charging
            : HidBatteryStatus.Available;

        int pct = MapRange(response[batteryByte], min, max, 0, 100);
        return new(pct, status);
    }

    public static HidBatteryReading ParseSteelSeriesNova(
        ReadOnlySpan<byte> response, int offlineByte, byte offlineVal,
        int chargingByte, byte chargingVal1, byte chargingVal2,
        int batteryByte, int maxRaw)
    {
        int need = Math.Max(Math.Max(offlineByte, chargingByte), batteryByte) + 1;
        if (response.Length < need) return new(-1, HidBatteryStatus.Unavailable);
        if (response[offlineByte] == offlineVal) return new(-1, HidBatteryStatus.Unavailable);

        byte ch = response[chargingByte];
        var status = (ch == chargingVal1 || ch == chargingVal2) ? HidBatteryStatus.Charging : HidBatteryStatus.Available;
        int pct = maxRaw == 100 ? Math.Clamp((int)response[batteryByte], 0, 100) : MapRange(response[batteryByte], 0, maxRaw, 0, 100);
        return new(pct, status);
    }

    public static HidBatteryReading ParseSteelSeriesNovaPro(ReadOnlySpan<byte> response)
    {
        if (response.Length < 16) return new(-1, HidBatteryStatus.Unavailable);
        byte st = response[15];
        if (st == 0x01) return new(-1, HidBatteryStatus.Unavailable);
        var status = st == 0x02 ? HidBatteryStatus.Charging : HidBatteryStatus.Available;
        int pct = MapRange(response[6], 0, 8, 0, 100);
        return new(pct, status);
    }

    public static HidBatteryReading ParseHyperXCloudFlight(ReadOnlySpan<byte> response)
    {
        if (response.Length < 15) return new(-1, HidBatteryStatus.Unavailable);
        int voltage = (response[3] << 8) | response[4];
        if (voltage == 0) return new(-1, HidBatteryStatus.Unavailable);
        if (voltage > 0x100B) return new(-1, HidBatteryStatus.Charging, voltage);

        double est;
        if (voltage <= 3648)
            est = 0.00125 * voltage;
        else if (voltage > 3975)
            est = 100.0;
        else
        {
            double v = voltage;
            est = 0.00000002547505 * Math.Pow(v, 4) - 0.0003900299 * Math.Pow(v, 3)
                + 2.238321 * Math.Pow(v, 2) - 5706.256 * v + 5452299.0;
        }
        return new(Math.Clamp((int)Math.Round(est), 0, 100), HidBatteryStatus.Available, voltage);
    }

    public static HidBatteryReading ParseHyperXCloud2Wireless(ReadOnlySpan<byte> levelResp, ReadOnlySpan<byte> chargingResp)
    {
        if (levelResp.Length < 8 || chargingResp.Length < 5) return new(-1, HidBatteryStatus.Unavailable);
        if (levelResp[0] != 0x06 || levelResp[1] != 0xFF || levelResp[2] != 0xBB || levelResp[3] != 0x02)
            return new(-1, HidBatteryStatus.Unavailable);
        int pct = Math.Clamp((int)levelResp[7], 0, 100);
        int mv = (levelResp[5] << 8) | levelResp[6];
        bool charging = chargingResp[4] == 0x01;
        return new(pct, charging ? HidBatteryStatus.Charging : HidBatteryStatus.Available, mv > 0 ? mv : null);
    }

    public static HidBatteryReading ParseHyperXCloudAlpha(ReadOnlySpan<byte> connResp, ReadOnlySpan<byte> chargeResp, ReadOnlySpan<byte> batResp)
    {
        if (connResp.Length < 4 || connResp[3] == 0x01) return new(-1, HidBatteryStatus.Unavailable);
        if (chargeResp.Length >= 4 && chargeResp[3] == 0x01) return new(-1, HidBatteryStatus.Charging);
        if (batResp.Length < 4) return new(-1, HidBatteryStatus.Unavailable);
        return new(Math.Clamp((int)batResp[3], 0, 100), HidBatteryStatus.Available);
    }

    /// <summary>
    /// 解析 Logitech HID++ 2.0 通用电池响应：
    ///  - Feature 0x1004 (Unified Battery, func 0x10): resp[4]=soc(0..100), resp[6]=status (0=discharging, 1=recharging, 2=almost full, 3=full)
    ///  - Feature 0x1000 (Battery Status, func 0x00): resp[4]=level(0..100), resp[6]=status (0=discharging, 1=recharging, 2=almost full, 3=full)
    /// </summary>
    public static HidBatteryReading ParseLogitechHidpp20Battery(ReadOnlySpan<byte> response, bool isUnifiedBattery)
    {
        if (response.Length < 7) return new(-1, HidBatteryStatus.Unavailable);
        if (response[2] == 0xFF || response[2] == 0x8F) return new(-1, HidBatteryStatus.Unavailable);
        int pct = response[4];
        if (pct > 100) return new(-1, HidBatteryStatus.Unavailable);
        byte st = response[6];
        bool charging = st is 1 or 2 or 3;
        return new(pct, charging ? HidBatteryStatus.Charging : HidBatteryStatus.Available);
    }

    /// <summary>分段线性插值将电压（mV，降序表）映射为 0..100 电量百分比。</summary>
    public static int InterpolateVoltage(int[] percentages, int[] voltagesMv, int mv)
    {
        if (percentages.Length == 0 || percentages.Length != voltagesMv.Length) return 0;
        if (mv >= voltagesMv[0]) return percentages[0];
        if (mv <= voltagesMv[^1]) return percentages[^1];

        for (int i = 0; i < voltagesMv.Length - 1; i++)
        {
            int vHi = voltagesMv[i];
            int vLo = voltagesMv[i + 1];
            if (mv <= vHi && mv >= vLo && vHi > vLo)
            {
                double t = (double)(mv - vLo) / (vHi - vLo);
                double p = percentages[i + 1] + t * (percentages[i] - percentages[i + 1]);
                return Math.Clamp((int)Math.Round(p), 0, 100);
            }
        }
        return 0;
    }

    public static int MapRange(int value, int inMin, int inMax, int outMin, int outMax)
    {
        if (inMax <= inMin) return outMin;
        double ratio = (double)(value - inMin) / (inMax - inMin);
        int mapped = (int)Math.Round(outMin + ratio * (outMax - outMin));
        return Math.Clamp(mapped, Math.Min(outMin, outMax), Math.Max(outMin, outMax));
    }
}
