using System;

namespace Island.Core.Monitoring;

/// <summary>
/// 硬件传感器清洗与筛选纯逻辑（参考 Diorser/LiteMonitor SensorMap）：
/// - 剔除 Intel Core Ultra / Ryzen 的 "Distance to TjMax" 差值传感器，避免误把剩余裕度当真实温度；
/// - 过滤 NaN / 0° / 异常尖峰（≥130°C、非法电压/频率），防止偶发坏值触发误告警；
/// - 多 GPU 优先级评分（独显/在用/有温度 > 空闲核显）。
/// </summary>
public static class ThermalSensorGuard
{
    public static bool IsExcludedTempSensorName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        return name.Contains("TjMax", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Distance", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Limit", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Threshold", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Delta", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsExcludedCpuClockName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        return name.Contains("Bus", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Effective", StringComparison.OrdinalIgnoreCase) == false && name.Contains("FSB", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsValidTemp(double? celsius)
        => celsius is { } v && double.IsFinite(v) && v > 0.5 && v < 130.0;

    public static bool IsValidVoltage(double? volts)
        => volts is { } v && double.IsFinite(v) && v >= 0.2 && v <= 2.5;

    public static bool IsValidClockMHz(double? mhz)
        => mhz is { } v && double.IsFinite(v) && v >= 100.0 && v <= 9500.0;

    public static int GpuPriorityScore(bool isDiscrete, bool hasTemp, bool hasActivePower, bool hasActiveLoad)
    {
        int score = 0;
        if (isDiscrete) score += 40;
        if (hasActivePower) score += 25;
        if (hasTemp) score += 20;
        if (hasActiveLoad) score += 10;
        return score;
    }

    /// <summary>
    /// 解析 Win32 STORAGE_TEMPERATURE_DATA_DESCRIPTOR 原始缓冲区（IOCTL_STORAGE_QUERY_PROPERTY）：
    /// Header 24 字节（偏移 12 为 USHORT InfoCount），随后每项 16 字节（偏移 +2 为 SHORT Temperature 摄氏度）。
    /// 多传感器时返回最高有效温度；空/截断/越界缓冲区安全返回 null。
    /// </summary>
    public static double? ParseStorageTemperatureDescriptor(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 40) return null;
        ushort count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(12, 2));
        if (count == 0 || count > 16) return null;

        double? best = null;
        for (int i = 0; i < count; i++)
        {
            int offset = 24 + i * 16;
            if (offset + 4 > buffer.Length) break;
            short tempC = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(buffer.Slice(offset + 2, 2));
            if (tempC is > 0 and < 120)
            {
                best = best is { } prev ? Math.Max(prev, tempC) : tempC;
            }
        }
        return best;
    }
}
