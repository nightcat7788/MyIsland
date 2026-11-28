using Island.Core.Monitoring;

namespace Island.Tests;

/// <summary>硬件传感器过滤与系统时长格式化用例（参考 LiteMonitor SensorMap 保护规则）。</summary>
internal static class ThermalSensorGuardTests
{
    public static void Run(Action<string, bool> check)
    {
        check("Distance to TjMax 等差值传感器被排除，不误当 CPU 温度",
            ThermalSensorGuard.IsExcludedTempSensorName("P-Core #1 Distance to TjMax")
            && ThermalSensorGuard.IsExcludedTempSensorName("Thermal Limit")
            && ThermalSensorGuard.IsExcludedTempSensorName("")
            && !ThermalSensorGuard.IsExcludedTempSensorName("CPU Package")
            && !ThermalSensorGuard.IsExcludedTempSensorName("Core (Tctl/Tdie)"));

        check("温度边界校验拦截 0° / 负数 / ≥130° / NaN 坏值",
            ThermalSensorGuard.IsValidTemp(29.0)
            && ThermalSensorGuard.IsValidTemp(57.1)
            && ThermalSensorGuard.IsValidTemp(95.0)
            && !ThermalSensorGuard.IsValidTemp(0)
            && !ThermalSensorGuard.IsValidTemp(-10)
            && !ThermalSensorGuard.IsValidTemp(150)
            && !ThermalSensorGuard.IsValidTemp(double.NaN)
            && !ThermalSensorGuard.IsValidTemp(null));

        check("电压与核心频率边界校验拦截非法尖峰",
            ThermalSensorGuard.IsValidVoltage(0.85)
            && !ThermalSensorGuard.IsValidVoltage(0)
            && !ThermalSensorGuard.IsValidVoltage(5.0)
            && ThermalSensorGuard.IsValidClockMHz(2790)
            && !ThermalSensorGuard.IsValidClockMHz(99.6)
            && ThermalSensorGuard.IsExcludedCpuClockName("Bus Speed")
            && !ThermalSensorGuard.IsExcludedCpuClockName("P-Core #1"));

        check("多 GPU 优先级评分：活跃独显高于空闲核显",
            ThermalSensorGuard.GpuPriorityScore(true, true, true, true)
                > ThermalSensorGuard.GpuPriorityScore(false, false, false, true));

        check("系统已开机时长格式化对齐 LiteMonitor 风格",
            MonitorSnapshot.FormatUptime(0.5) == "30分"
            && MonitorSnapshot.FormatUptime(19.6) == "19时 36分"
            && MonitorSnapshot.FormatUptime(28.5) == "1天 4时 30分"
            && MonitorSnapshot.FormatUptime(0) == "0分");

        // 构造模拟 STORAGE_TEMPERATURE_DATA_DESCRIPTOR（24 字节头 + 2×16 字节传感器项：38°C 与 44°C）
        byte[] desc = new byte[56];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(desc.AsSpan(12, 2), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(desc.AsSpan(24 + 2, 2), 38);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(desc.AsSpan(40 + 2, 2), 44);
        check("NVMe STORAGE_TEMPERATURE_DATA_DESCRIPTOR 解析取最高有效摄氏度",
            ThermalSensorGuard.ParseStorageTemperatureDescriptor(desc) == 44.0);

        byte[] badDesc = new byte[40];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(badDesc.AsSpan(12, 2), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(badDesc.AsSpan(24 + 2, 2), -5);
        check("NVMe 描述符对截断/负值/超限温度安全返回 null",
            ThermalSensorGuard.ParseStorageTemperatureDescriptor(ReadOnlySpan<byte>.Empty) == null
            && ThermalSensorGuard.ParseStorageTemperatureDescriptor(desc.AsSpan(0, 20)) == null
            && ThermalSensorGuard.ParseStorageTemperatureDescriptor(badDesc) == null);
    }
}
