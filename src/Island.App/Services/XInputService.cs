using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Island.App.Services;

/// <summary>
/// XInput 手柄电量（2026-11-25，数据源与显示原则参考 ai-periph-monitor）：
/// Windows 只给「空 / 低 / 中 / 满」四档 —— **不换算成百分比**，换算就是伪装
///（档位边界因机型而异，33% 这种数字读起来像实测值，其实不是）。
/// 界面上画档位文字（LevelText），电量环只按档位近似取比例（0 / 1/3 / 2/3 / 1）。
/// 有线连接报「有线」，不参与低电量提醒（没有可换算的百分比）。
/// 没有手柄 / 没有 xinput1_4.dll（Win8+ 系统自带）时返回空列表，UI 不显示这一行。
/// 不建线程：由 DeviceService 的采样节拍（设备视图 30s / 低电量慢轮询 300s）带动。
/// </summary>
public static class XInputService
{
    private const byte BATTERY_DEVTYPE_GAMEPAD = 0;
    private const byte BATTERY_TYPE_DISCONNECTED = 0x00;
    private const byte BATTERY_TYPE_WIRED = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [DllImport("xinput1_4.dll", SetLastError = false)]
    private static extern int XInputGetBatteryInformation(uint dwUserIndex, byte devType, out XINPUT_BATTERY_INFORMATION battery);

    /// <summary>当前连接的手柄（0–4 个）。设备名用槽位号：系统没有给手柄的友好名可读。</summary>
    public static List<DeviceInfo> Snapshot()
    {
        var list = new List<DeviceInfo>();
        for (uint slot = 0; slot < 4; slot++)
        {
            if (!TryGet(slot, out var battery)) continue;
            if (MapBattery(slot, battery.BatteryType, battery.BatteryLevel) is { } device)
                list.Add(device);
        }
        return list;
    }

    /// <summary>原始 XInput 电池信息的纯映射；有线连接不代表正在充电。</summary>
    internal static DeviceInfo? MapBattery(uint slot, byte batteryType, byte batteryLevel)
    {
        if (batteryType == BATTERY_TYPE_DISCONNECTED) return null;
        bool wired = batteryType == BATTERY_TYPE_WIRED;
        bool known = batteryType is 0x02 or 0x03 && batteryLevel <= 3;
        string text = wired ? "有线" : known ? LevelText(batteryLevel) : "未知";
        return new DeviceInfo(slot == 0 ? "手柄" : $"手柄 {slot + 1}", "gamepad",
            known ? LevelToPercent(batteryLevel) : 0, false, text);
    }

    /// <summary>档位 → 文字。未知档位显示「未知」，不猜。</summary>
    internal static string LevelText(byte level) => level switch
    {
        0 => "空", 1 => "低", 2 => "中", 3 => "满", _ => "未知",
    };

    /// <summary>档位 → 电量环的近似比例（仅供画环用；界面上的数字永远是档位文字）。</summary>
    internal static int LevelToPercent(byte level) => level switch
    {
        0 => 0, 1 => 33, 2 => 66, 3 => 100, _ => 0,
    };

    private static bool TryGet(uint slot, out XINPUT_BATTERY_INFORMATION battery)
    {
        try
        {
            // ERROR_SUCCESS(0) 才算查到；ERROR_DEVICE_NOT_CONNECTED(1167) 等一律当没手柄
            return XInputGetBatteryInformation(slot, BATTERY_DEVTYPE_GAMEPAD, out battery) == 0;
        }
        catch (DllNotFoundException) { battery = default; return false; }   // 没有 xinput 的精简系统
        catch (EntryPointNotFoundException) { battery = default; return false; }
    }
}
