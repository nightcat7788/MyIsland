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
}
