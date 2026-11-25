namespace Island.App.Services;

/// <summary>
/// 一台无线设备的电量读数。
/// 2026-11-25 从 DeviceService.cs 挪到独立文件（与 ThermalSnapshot 同一个理由）：
/// 测试工程要链接它（档位显示 / 低电量提醒的用例），而 DeviceService 本体带着
/// System.Management 依赖不能进零 NuGet 的测试工程。
/// </summary>
public sealed record DeviceInfo(string Name, string Kind, int Percent, bool Charging, string? LevelText = null)
{
    /// <summary>
    /// 档位文字（XInput 手柄的「空 / 低 / 中 / 满 / 有线」，2026-11-25 起）。
    /// 非 null 时界面显示这段文字、<see cref="Percent"/> 只是画环的近似比例 ——
    /// 系统只给档位不给百分比，换算成数字就是伪装（ai-periph-monitor 同一原则）。
    /// 档位设备也不参与低电量提醒。
    /// </summary>
    public string? LevelText { get; init; } = LevelText;

    /// <summary>BLE 实际读到电量的时刻；有缓存读数不代表设备当前仍连接。</summary>
    public System.DateTime? ObservedAt { get; init; }

    /// <summary>
    /// 数据来源族（wmi / msbattery / ble / xinput / hid）。
    /// 用途只有一个：**去重时区分"同一台设备被两个数据源报上来"与"两台同型号的真设备"**。
    /// 只按显示名去重会把两台同型号设备吃掉一台（同名的真设备会静默消失）；
    /// 只在"名字相同**且**来源族不同"时才折叠，才是安全的那一种。
    /// 空串 = 来源未知 ⇒ 不参与折叠。
    /// </summary>
    public string Source { get; init; } = "";

    internal static int? ParsePercent(object? raw)
    {
        if (raw is null) return null;
        try
        {
            int value = System.Convert.ToInt32(raw);
            return value is >= 0 and <= 100 ? value : null;
        }
        catch { return null; }
    }
}
