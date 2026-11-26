namespace Island.App.Services;

/// <summary>
/// 一次温度/风扇读数的快照。
/// 2026-11-24 从 ThermalService.cs 挪到独立文件：它自己没有任何依赖，测试工程要链接它
///（监控页"温度变了没有"的判断见 Controls/ThermalDisplay，那组用例要用到这个类型）。
/// </summary>
public sealed record ThermalSnapshot
{
    public double? CpuTemp { get; init; }
    public double? GpuTemp { get; init; }
    public double? SsdTemp { get; init; }     // SSD 温度（LHM 存储传感器，读不到为 null）
    public double? MotherboardTemp { get; init; }
    public double? CpuPower { get; init; }
    public double? GpuPower { get; init; }
    public double? GpuFanRpm { get; init; }
    public double? CpuFanRpm { get; init; }
    /// <summary>显存（GPU 专用/共享，GB，LHM SmallData 传感器）。</summary>
    public double? GpuMemoryUsedGB { get; init; }
    public double? GpuMemoryTotalGB { get; init; }
    /// <summary>
    /// 显存读数的**口径**（"vendor" / "dedicated" / "shared" / ""=未知）。
    /// 与数值同源：同一个 LHM 传感器选出来的，取值见 Island.App.Services.GpuMemoryKinds。
    /// </summary>
    public string GpuMemoryKind { get; init; } = "";
    /// <summary>GPU 核心频率 MHz。</summary>
    public double? GpuCoreClockMHz { get; init; }
    public DateTime UpdatedAt { get; init; } = DateTime.Now;
}
