using System;
using Island.App.Services;

namespace Island.Tests;

/// <summary>
/// 设备电量的规则用例（2026-11-25 设备电量增强，参考 ai-periph-monitor 的「不伪装」原则）：
/// XInput 手柄只有档位 → 显示文字不换算百分比；类型图标；低电量提醒的默认值与阈值夹紧。
/// DeviceService / BleBatteryService 本体带着 System.Management / WinRT 依赖，进不了零包测试工程；
/// 它们的用例靠实机验证（thermal-diag.log 与设备页截图），这里守的是纯规则。
/// </summary>
internal static class DeviceBatteryTests
{
    public static void Run(Action<string, bool> check)
    {
        // ---------- XInput 档位 ----------
        check("XInput 档位 → 文字：空/低/中/满", XInputService.LevelText(0) == "空"
            && XInputService.LevelText(1) == "低" && XInputService.LevelText(2) == "中"
            && XInputService.LevelText(3) == "满");
        check("XInput 未知档位显示「未知」，不猜一个百分比出来", XInputService.LevelText(9) == "未知"
            && XInputService.LevelText(255) == "未知");
        check("XInput 档位 → 环比例 0/33/66/100（只供画环；界面数字永远是文字）",
            XInputService.LevelToPercent(0) == 0 && XInputService.LevelToPercent(1) == 33
            && XInputService.LevelToPercent(2) == 66 && XInputService.LevelToPercent(3) == 100);

        // ---------- DeviceInfo 的档位语义 ----------
        var pctDevice = new DeviceInfo("测试设备", "mouse", 73, false);
        var levelDevice = new DeviceInfo("手柄", "gamepad", 33, false, "低");
        check("百分比设备的 LevelText 默认 null（界面显示数字）", pctDevice.LevelText is null);
        check("档位设备的 Percent 只是画环的近似比例，不参与低电量提醒判断（LevelText 非 null 即档位）",
            levelDevice.LevelText == "低" && levelDevice.Percent == 33);
        var wired = XInputService.MapBattery(0, 1, 3);
        check("有线手柄真实映射不伪装成充电中或满电",
            wired is { LevelText: "有线", Charging: false, Percent: 0 });
        check("断开手柄真实映射不生成设备", XInputService.MapBattery(0, 0, 3) is null);
        check("无线手柄真实映射保留档位与槽位名",
            XInputService.MapBattery(1, 2, 1) is { Name: "手柄 2", LevelText: "低", Percent: 33, Charging: false });
        check("未知电池类型或档位不伪装满环",
            XInputService.MapBattery(0, 255, 3) is { LevelText: "未知", Percent: 0 }
            && XInputService.MapBattery(0, 2, 255) is { LevelText: "未知", Percent: 0 }
            && XInputService.LevelToPercent(255) == 0);

        check("电量转换失败、空值、越界均返回未知而非零电量",
            DeviceInfo.ParsePercent("无效") is null && DeviceInfo.ParsePercent(new object()) is null
            && DeviceInfo.ParsePercent(null) is null && DeviceInfo.ParsePercent(255) is null
            && DeviceInfo.ParsePercent(-1) is null && DeviceInfo.ParsePercent(ulong.MaxValue) is null);
        check("真实零电量与满电量仍有效",
            DeviceInfo.ParsePercent(0) == 0 && DeviceInfo.ParsePercent("100") == 100);

        // ---------- 类型图标 ----------
        // DeviceService.Glyph（鼠/键/耳/柄/电）带 System.Management 依赖进不了测试工程，
        // 由设备页/收起态/托盘菜单三处实际使用守着；这里不重复。

        // ---------- 低电量提醒的设置 ----------
        var s = new Settings();
        check("低电量提醒默认开启、阈值 20%", s.BatteryAlertEnabled && s.BatteryAlertThreshold == 20);
        s.BatteryAlertThreshold = 0;
        s.ClampValues();
        check("低电量阈值下限 5%（0 会被夹回去）", s.BatteryAlertThreshold == 5);
        s.BatteryAlertThreshold = 999;
        s.ClampValues();
        check("低电量阈值上限 50%（999 会被夹回去）", s.BatteryAlertThreshold == 50);
    }
}
