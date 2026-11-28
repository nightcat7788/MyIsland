using Island.Core.Monitoring;

namespace Island.Tests;

internal static class HidPeripheralProtocolTests
{
    public static void Run(Action<string, bool> check)
    {
        // 1. 档案表匹配（Logitech / Corsair / SteelSeries / HyperX）
        var gpro = HidPeripheralProtocol.FindProfile(0x046D, 0x0AAA, 0xFF43, 0x0202);
        check("HID 匹配 Logitech G PRO X", gpro is not null && gpro.Protocol == HidProtocolKind.LogitechHidppVoltage);

        var wrongUsage = HidPeripheralProtocol.FindProfile(0x046D, 0x0AAA, 0x0001, 0x0002);
        check("HID UsagePage 不匹配时拒绝命中", wrongUsage is null);

        var corsair = HidPeripheralProtocol.FindProfile(0x1B1C, 0x0A55, 0xFFC5, 0x0001);
        check("HID 匹配 Corsair Void/HS", corsair is not null && corsair.Protocol == HidProtocolKind.CorsairVoid);

        var nova7Gen2 = HidPeripheralProtocol.FindProfile(0x1038, 0x227E, 0xFFC0, 0x0001);
        check("HID 匹配 SteelSeries Nova 7 Gen2", nova7Gen2 is not null && nova7Gen2.Protocol == HidProtocolKind.SteelSeriesNova7Percent);

        var flight = HidPeripheralProtocol.FindProfile(0x0951, 0x16C4, 0xFF00, 0x0001);
        check("HID 通配 UsagePage 匹配 HyperX Cloud Flight", flight is not null && flight.Protocol == HidProtocolKind.HyperXCloudFlight);

        check("HID++ 2.0 通用接口判定", HidPeripheralProtocol.IsLogitechHidpp20Interface(0x046D, 0xFF00, 0x0001));
        check("非 Logitech 拒绝 HID++ 2.0", !HidPeripheralProtocol.IsLogitechHidpp20Interface(0x1038, 0xFF00, 0x0001));
        check("HID 路径快速预筛命中 Logitech/Corsair/SteelSeries",
            HidPeripheralProtocol.IsSupportedVendorInPath(@"\\?\hid#vid_046d&pid_0aaa&mi_03#7&123")
            && HidPeripheralProtocol.IsSupportedVendorInPath(@"\\?\hid#VID_1B1C&pid_0a55")
            && HidPeripheralProtocol.IsSupportedVendorInPath(@"\\?\hid#vid_1038&pid_227e"));
        check("HID 路径快速预筛跳过无关系统设备",
            !HidPeripheralProtocol.IsSupportedVendorInPath(@"\\?\hid#vid_8086&pid_0001")
            && !HidPeripheralProtocol.IsSupportedVendorInPath(null));

        // 2. Logitech HID++ 电压解析（正常 / 充电 / 离线 / 过低电压）
        byte[] gproResp = { 0x11, 0xFF, 0x06, 0x0D, 0x0E, 0xF6 /* 3830mV = 50% */, 0x01 };
        var rLogi = HidPeripheralProtocol.ParseSingleResponse(gpro!, gproResp);
        check("Logitech G PRO 3830mV 映射 50%", rLogi.Status == HidBatteryStatus.Available && rLogi.Percent == 50 && rLogi.VoltageMv == 3830);

        byte[] gproChg = { 0x11, 0xFF, 0x06, 0x0D, 0x10, 0x36 /* 4150mV = 100% */, 0x03 };
        var rLogiChg = HidPeripheralProtocol.ParseSingleResponse(gpro!, gproChg);
        check("Logitech G PRO 充电中标志", rLogiChg.Status == HidBatteryStatus.Charging && rLogiChg.Percent == 100);

        byte[] gproOff = { 0x11, 0xFF, 0xFF, 0x0D, 0x00, 0x00, 0x00 };
        var rLogiOff = HidPeripheralProtocol.ParseSingleResponse(gpro!, gproOff);
        check("Logitech 0xFF 离线返回 Unavailable", !rLogiOff.IsValid && rLogiOff.Status == HidBatteryStatus.Unavailable);

        // 3. Logitech HID++ 2.0 通用键鼠电池（0x1004 Unified Battery）
        byte[] hidpp20 = { 0x11, 0x01, 0x07, 0x1A, 78, 0x04, 0x00 };
        var rH20 = HidPeripheralProtocol.ParseLogitechHidpp20Battery(hidpp20, true);
        check("Logitech HID++ 2.0 78% 放电", rH20.IsValid && rH20.Percent == 78 && rH20.Status == HidBatteryStatus.Available);

        byte[] hidpp20Chg = { 0x11, 0x01, 0x07, 0x1A, 64, 0x04, 0x01 };
        var rH20Chg = HidPeripheralProtocol.ParseLogitechHidpp20Battery(hidpp20Chg, true);
        check("Logitech HID++ 2.0 64% 充电中", rH20Chg.IsValid && rH20Chg.Percent == 64 && rH20Chg.Status == HidBatteryStatus.Charging);

        // 4. Corsair Void 报文（含 bit7 麦克风上翻静音位 + 充电 + 离线）
        byte[] corNormal = { 100, 0, (byte)(82 | 0x80), 177, 1 };
        var rCor = HidPeripheralProtocol.ParseCorsairVoid(corNormal);
        check("Corsair Void 剥离 bit7 麦克风位得 82%", rCor.IsValid && rCor.Percent == 82 && rCor.MicMuted == true && rCor.Status == HidBatteryStatus.Available);

        byte[] corChg = { 100, 0, 55, 177, 4 };
        var rCorChg = HidPeripheralProtocol.ParseCorsairVoid(corChg);
        check("Corsair Void status=4 充电中", rCorChg.IsValid && rCorChg.Percent == 55 && rCorChg.Status == HidBatteryStatus.Charging);

        byte[] corOff = { 100, 0, 0, 177, 0 };
        check("Corsair Void status=0 离线", !HidPeripheralProtocol.ParseCorsairVoid(corOff).IsValid);

        // 5. SteelSeries Arctis 7+ / Nova 7 (Discrete 0..4 & Gen2 %) / Nova 5 / Nova Pro
        var arctis7p = HidPeripheralProtocol.FindProfile(0x1038, 0x220E, 0xFFC0, 0x0001)!;
        var rA7p = HidPeripheralProtocol.ParseSingleResponse(arctis7p, new byte[] { 0xB0, 0x03, 0x03, 0x00 });
        check("SteelSeries Arctis 7+ 档位 3/4 -> 75%", rA7p.IsValid && rA7p.Percent == 75 && rA7p.Status == HidBatteryStatus.Available);

        var rN7g2 = HidPeripheralProtocol.ParseSingleResponse(nova7Gen2!, new byte[] { 0xB0, 0x00, 91, 0x01 });
        check("SteelSeries Nova 7 Gen2 91% 充电中", rN7g2.IsValid && rN7g2.Percent == 91 && rN7g2.Status == HidBatteryStatus.Charging);

        var rN7off = HidPeripheralProtocol.ParseSingleResponse(nova7Gen2!, new byte[] { 0xB0, 0x00, 0, 0x00 });
        check("SteelSeries Nova 7 离线拒绝伪造", !rN7off.IsValid);

        // 6. HyperX Cloud Flight / Cloud II Wireless / Cloud Alpha Wireless
        byte[] flightChg = new byte[20];
        flightChg[3] = 0x10; flightChg[4] = 0x20; // > 0x100B => Charging (percent=-1 不伪造)
        var rFlChg = HidPeripheralProtocol.ParseHyperXCloudFlight(flightChg);
        check("HyperX Cloud Flight 充电中不伪造百分比", rFlChg.IsValid && rFlChg.Status == HidBatteryStatus.Charging && rFlChg.Percent == -1);

        byte[] c2Lvl = { 0x06, 0xFF, 0xBB, 0x02, 0x00, 0x0F, 0x3C, 88 };
        byte[] c2Chg = { 0x06, 0xFF, 0xBB, 0x03, 0x01 };
        var rC2 = HidPeripheralProtocol.ParseHyperXCloud2Wireless(c2Lvl, c2Chg);
        check("HyperX Cloud II Wireless 88% 充电中", rC2.IsValid && rC2.Percent == 88 && rC2.Status == HidBatteryStatus.Charging && rC2.VoltageMv == 0x0F3C);

        var rAlpha = HidPeripheralProtocol.ParseHyperXCloudAlpha(
            new byte[] { 0x21, 0xBB, 0x03, 0x02 },
            new byte[] { 0x21, 0xBB, 0x0C, 0x00 },
            new byte[] { 0x21, 0xBB, 0x0B, 67 });
        check("HyperX Cloud Alpha Wireless 67%", rAlpha.IsValid && rAlpha.Percent == 67 && rAlpha.Status == HidBatteryStatus.Available);
    }
}
