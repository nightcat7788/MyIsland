using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace Island.App.Services;

/// <summary>一次温度/风扇读数的快照。</summary>
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
    /// <summary>GPU 核心频率 MHz。</summary>
    public double? GpuCoreClockMHz { get; init; }
    public DateTime UpdatedAt { get; init; } = DateTime.Now;
}

/// <summary>
/// 温度/风扇/功耗：LibreHardwareMonitorLib（库引用，非驱动模式）。
/// - GPU（NVML/ADL 用户态）与主板 SuperIO 部分机器无需管理员即可读
/// - CPU MSR 温度需要 PawnIO/管理员，读不到时如实给 null，UI 显示 "--"，绝不伪造
/// 采样节流：仅当有消费者（展开面板或开启显示）时才每 2s 更新一次。
/// </summary>
public sealed class ThermalService : IDisposable
{
    private Computer? _computer;
    private readonly object _gate = new();
    private ThermalSnapshot _last = new();
    private System.Threading.Timer? _timer;
    private bool _wanted;

    public event Action<ThermalSnapshot>? Updated;
    public ThermalSnapshot Current => _last;

    /// <summary>UI 声明需要温度数据（展开面板/收起显示）；false 时停止采样省资源。</summary>
    public void SetWanted(bool wanted)
    {
        lock (_gate)
        {
            if (_wanted == wanted) return;
            _wanted = wanted;
            if (wanted) EnsureStarted();
            else _timer?.Dispose();
        }
    }

    /// <summary>启动即采一次（诊断：确认无管理员下能读到什么）。不开启持续采样。</summary>
    public void PrimeOnce()
    {
        lock (_gate)
        {
            if (_computer is null)
            {
                try
                {
                    _computer = new Computer
                    {
                        IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true,
                        // ⚠️ 2026-11-22 实机教训：LHM 存储传感器（NVMe SMART IOCTL）在本机
                        // (Lenovo secured-core) 触发 SECURE_KERNEL_ERROR 0x1EA 蓝屏两次，
                        // 与启用时间点精确相关。默认永久关闭；SSD 温度显示 "--"。
                        IsMemoryEnabled = false, IsStorageEnabled = false, IsControllerEnabled = false,
                        IsNetworkEnabled = false, IsPsuEnabled = false,
                    };
                    _computer.Open();
                }
                catch { _computer = null; return; }
            }
            SampleCore();
        }
    }

    private void EnsureStarted()
    {
        if (_computer is null)
        {
            try
            {
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = false,
                    IsMotherboardEnabled = true,
                    // ⚠️ 存储传感器在本机引发蓝屏（0x1EA），永久关闭；SSD 温度显示 "--"
                    IsStorageEnabled = false,
                    IsControllerEnabled = false,
                    IsNetworkEnabled = false,
                    IsPsuEnabled = false,
                };
                _computer.Open();
            }
            catch
            {
                _computer = null; // 打不开（无权限等）——保持无温度运行
            }
        }
        _timer ??= new System.Threading.Timer(_ => Sample(), null, 0, 2000);
    }

    private void Sample()
    {
        lock (_gate)
        {
            if (!_wanted || _computer is null) return;
            SampleCore();
        }
    }

    private void SampleCore()
    {
        try
        {
            double? cpuTemp = null, gpuTemp = null, mbTemp = null, cpuPower = null,
                    gpuFan = null, cpuFan = null, gpuPower = null, ssdTemp = null,
                    gpuMemUsed = null, gpuMemTotal = null, gpuClock = null;

            // SMN 探测放最前（诊断块依赖 _smuProbeResult）
            if (_smu is null)
            {
                _smu = new InpOutSmnService();
                _smu.TryReadCpuTemperature(out _);   // 触发加载，填 Status
                _smuProbeResult = _smu.Status;
            }

                // 首次采样：把发现的硬件/传感器全写进诊断日志（定位"没有温度"）。
                // 诊断文本只在首次构建——此前每 2 秒拼完全部传感器再丢弃，纯浪费
                bool dump = !_dumped;
                System.Text.StringBuilder? diag = dump ? new System.Text.StringBuilder() : null;
                foreach (IHardware hw in _computer!.Hardware)
                {
                    hw.Update();
                    foreach (IHardware sub in hw.SubHardware) sub.Update();
                    diag?.AppendLine($"[{hw.HardwareType}] {hw.Name}");
                    if (dump)
                    {
                        foreach (ISensor s in hw.Sensors)
                            if (s.Value.HasValue)
                                diag!.AppendLine($"    {s.SensorType} {s.Name} = {Math.Round(s.Value.Value,1)}");
                        foreach (IHardware sub in hw.SubHardware)
                            foreach (ISensor s in sub.Sensors)
                                if (s.Value.HasValue)
                                    diag!.AppendLine($"    [sub {sub.Name}] {s.SensorType} {s.Name} = {Math.Round(s.Value.Value,1)}");
                    }

                    if (hw.HardwareType == HardwareType.Cpu)
                    {
                        cpuTemp ??= Pick(hw, SensorType.Temperature, "Core Average", "CPU Package", "Package", "Tctl");
                        cpuPower = Pick(hw, SensorType.Power, "CPU Package", "Package") ?? cpuPower;
                        cpuFan ??= FirstOf(hw, SensorType.Fan);
                    }
                    else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                    {
                        var t = Pick(hw, SensorType.Temperature, "GPU Core", "GPU Hot Spot", "GPU");
                        // 多 GPU（核显+独显）：优先已有功耗读数的那块（独显），其次温度更高者
                        var p = Pick(hw, SensorType.Power, "GPU Package", "GPU Chip Power", "GPU Core");
                        var f = FirstOf(hw, SensorType.Fan);
                        if (t is not null && (gpuTemp is null || p is not null))
                        {
                            gpuTemp = t;
                            gpuPower = p;
                        }
                        gpuFan ??= f;
                        // 显存（LiteMonitor 同款：NVIDIA 专用显存 / Intel·AMD 走 D3D 共享显存）
                        gpuMemUsed ??= Pick(hw, SensorType.SmallData,
                            "GPU Memory Used", "D3D Dedicated Memory Used", "D3D Shared Memory Used");
                        gpuMemTotal ??= Pick(hw, SensorType.SmallData,
                            "GPU Memory Total", "D3D Dedicated Memory Total", "D3D Shared Memory Total");
                        // GPU 核心频率
                        gpuClock ??= Pick(hw, SensorType.Clock, "GPU Core");
                    }
                    else if (hw.HardwareType == HardwareType.Storage)
                    {
                        // SSD 温度：NVMe 用户态 IOCTL 一般免提权；读不到保持 null 显示 "--"
                        ssdTemp ??= Pick(hw, SensorType.Temperature, "Temperature", "Composite Temperature", "NAND");
                    }
                    else if (hw.HardwareType == HardwareType.Motherboard)
                    {
                        foreach (IHardware sub in hw.SubHardware)
                        {
                            mbTemp ??= FirstOf(sub, SensorType.Temperature);
                            cpuFan ??= Pick(sub, SensorType.Fan, "CPU", "CPU #1");
                        }
                    }
                }
                if (dump)
                {
                    _dumped = true;
                    string smnNote = $"SMN-inpoutx64: {_smuProbeResult ?? "not probed"}" +
                        (_smu is not null && _smu.TryReadCpuTemperature(out double probe)
                            ? $" ({probe:F1}C)" : " (read fail)");
                    try
                    {
                        string dir = System.IO.Path.Combine(Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData), "MyIsland");
                        System.IO.Directory.CreateDirectory(dir);
                        System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "thermal-diag.log"),
                            $"[{DateTime.Now:HH:mm:ss}] {smnNote}\n{diag}\n");
                    }
                    catch { }
                }

            // CPU 温度兜底链：① inpoutx64 直读 SMN（TM 包驱动已在跑，非提权可开）
            //               ② LHM（需 PawnIO，本机未装） ③ WMI 热区（本机不支持）
            if (cpuTemp is null && _smu is not null && _smu.TryReadCpuTemperature(out double smnTemp))
            {
                cpuTemp = smnTemp;
            }
            if (cpuTemp is null)
            {
                cpuTemp = ReadWmiThermal();
            }

            _last = new ThermalSnapshot
            {
                CpuTemp = cpuTemp, GpuTemp = gpuTemp, MotherboardTemp = mbTemp,
                CpuPower = cpuPower, GpuFanRpm = gpuFan, CpuFanRpm = cpuFan,
                GpuPower = gpuPower, SsdTemp = ssdTemp,
                // LHM 显存/时钟单位是 MB/MHz，统一转 GB/GHz
                GpuMemoryUsedGB = gpuMemUsed / 1024.0,
                GpuMemoryTotalGB = gpuMemTotal / 1024.0,
                GpuCoreClockMHz = gpuClock,
            };
            Updated?.Invoke(_last);
        }
        catch { /* 单次采样失败保留上次 */ }
    }

    private static double? ReadWmiThermal()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "root\\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (var obj in searcher.Get())
            {
                var raw = Convert.ToDouble(obj["CurrentTemperature"]);
                double c = raw / 10.0 - 273.15;
                if (c is > 0 and < 120) return c;
            }
        }
        catch { /* 无权限/不支持：返回 null 显示 -- */ }
        return null;
    }

    private bool _dumped;
    private InpOutSmnService? _smu;
    private string? _smuProbeResult;

    private static double? Pick(IHardware hw, SensorType type, params string[] names)
    {
        var sensors = hw.Sensors.Where(s => s.SensorType == type && s.Value.HasValue && s.Value.Value > 0.01);
        foreach (var n in names)
        {
            var hit = sensors.FirstOrDefault(s =>
                s.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit.Value;
        }
        return null;
    }

    private static double? FirstOf(IHardware hw, SensorType type) =>
        hw.Sensors.FirstOrDefault(s => s.SensorType == type && s.Value.HasValue && s.Value.Value > 0.01)?.Value;

    public void Dispose()
    {
        _timer?.Dispose();
        _smu?.Dispose();
        try { _computer?.Close(); } catch { }
    }
}
