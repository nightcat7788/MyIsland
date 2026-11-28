using System;
using System.Collections.Generic;
using System.Linq;
using Island.Core.Monitoring;
using LibreHardwareMonitor.Hardware;

namespace Island.App.Services;

/// <summary>一次温度/风扇/功耗/真实硬件频率与电压快照（参考 LiteMonitor SensorMap）。</summary>
public sealed record ThermalSnapshot
{
    public double? CpuTemp { get; init; }
    public double? GpuTemp { get; init; }
    public double? SsdTemp { get; init; }     // SSD 温度（LHM 存储传感器，读不到为 null）
    public double? MotherboardTemp { get; init; }
    public double? CpuPower { get; init; }
    public double? PlatformPower { get; init; }
    public double? GpuPower { get; init; }
    public double? CpuClockGHz { get; init; } // LHM 真实核心主频（优先于 PDH 估算频率）
    public double? CpuVoltage { get; init; }  // CPU 核心电压 V
    public double? GpuFanRpm { get; init; }
    public double? CpuFanRpm { get; init; }
    public double? GpuLoad { get; init; }     // LHM GPU 负载兜底（D3D / GPU Core）
    /// <summary>显存（GPU 专用/共享，GB，LHM SmallData 传感器）。</summary>
    public double? GpuMemoryUsedGB { get; init; }
    public double? GpuMemoryTotalGB { get; init; }
    /// <summary>GPU 核心频率 MHz。</summary>
    public double? GpuCoreClockMHz { get; init; }
    /// <summary>探测到的硬件名称摘要（供设置中心诊断显示）。</summary>
    public string CpuHardwareName { get; init; } = "";
    public string GpuHardwareName { get; init; } = "";
    public string StorageHardwareName { get; init; } = "";
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
    private readonly object _stateGate = new();
    private static readonly object DiagLogGate = new();
    private ThermalSnapshot _last = new();
    private System.Threading.Timer? _timer;
    private bool _wanted;
    private bool _disposed;

    public event Action<ThermalSnapshot>? Updated;
    public ThermalSnapshot Current => _last;

    /// <summary>生成已识别硬件与实时温度诊断摘要（供设置中心实时核验）。</summary>
    public string GetHardwareSummary()
    {
        var s = _last;
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(s.CpuHardwareName))
            parts.Add(s.CpuTemp is { } ct ? $"{s.CpuHardwareName} ({ct:F1}°C)" : s.CpuHardwareName);
        if (!string.IsNullOrWhiteSpace(s.GpuHardwareName))
            parts.Add(s.GpuTemp is { } gt ? $"{s.GpuHardwareName} ({gt:F0}°C)" : s.GpuHardwareName);
        if (!string.IsNullOrWhiteSpace(s.StorageHardwareName))
            parts.Add(s.SsdTemp is { } st ? $"{s.StorageHardwareName} ({st:F0}°C)" : s.StorageHardwareName);
        else if (s.SsdTemp is { } stOnly)
            parts.Add($"NVMe SSD ({stOnly:F0}°C)");
        return parts.Count > 0 ? "已识别硬件：" + string.Join(" · ", parts) : "等待首次硬件传感器采样…";
    }

    /// <summary>
    /// 睡眠/休眠唤醒恢复（参考 LiteMonitor #277/#512/#555）：
    /// 唤醒后显卡/主板/PawnIO 句柄可能失效导致温度冻结或归零，延迟 2 秒重建 LHM Computer 并重新预热。
    /// </summary>
    public void NotifySystemResume()
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await System.Threading.Tasks.Task.Delay(2000).ConfigureAwait(false); } catch { return; }
            lock (_gate)
            {
                lock (_stateGate) { if (_disposed) return; }
                try { _computer?.Close(); } catch { }
                _computer = null;
                _cachedSsdTemp = null;
                _sampleCount = 0;
                if (EnsureComputerOpenLocked()) SampleCore();
            }
        });
    }

    /// <summary>UI 声明需要温度数据（快速非阻塞，Computer.Open 全部在后台采样线程执行，绝不卡 UI 线程）。</summary>
    public void SetWanted(bool wanted)
    {
        lock (_stateGate)
        {
            if (_disposed || _wanted == wanted) return;
            _wanted = wanted;
            if (wanted)
            {
                _timer ??= new System.Threading.Timer(_ => Sample(), null, 0, 2000);
            }
            else
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
    }

    /// <summary>启动后台预采样一次（不阻塞 UI 线程）。</summary>
    public void PrimeOnce()
    {
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            lock (_gate)
            {
                lock (_stateGate) { if (_disposed) return; }
                if (!EnsureComputerOpenLocked()) return;
                SampleCore();
            }
        });
    }

    private bool EnsureComputerOpenLocked()
    {
        if (_computer is not null) return true;
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = false,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                IsControllerEnabled = false,
                IsNetworkEnabled = false,
                IsPsuEnabled = false,
            };
            _computer.Open();
            // 参考 LiteMonitor HardwareMonitor：Open 后执行一次预热 Update，激活部分延迟初始化的 Intel Arc / 独显传感器
            foreach (IHardware hw in _computer.Hardware)
            {
                try
                {
                    hw.Update();
                    foreach (IHardware sub in hw.SubHardware)
                    {
                        try { sub.Update(); } catch { }
                    }
                }
                catch { }
            }
            return true;
        }
        catch
        {
            _computer = null;
            return false;
        }
    }

    private void Sample()
    {
        lock (_gate)
        {
            lock (_stateGate)
            {
                if (_disposed || !_wanted) return;
            }
            if (!EnsureComputerOpenLocked()) return;
            SampleCore();
        }
    }

    private void SampleCore()
    {
        try
        {
            double? cpuTemp = null, gpuTemp = null, mbTemp = null, cpuPower = null, platformPower = null,
                    cpuClockMHz = null, cpuVoltage = null,
                    gpuFan = null, cpuFan = null, gpuPower = null, gpuLoad = null, ssdTemp = null,
                    gpuMemUsed = null, gpuMemTotal = null, gpuClock = null;
            string cpuHwName = "", gpuHwName = "", storageHwName = "";
            int bestGpuScore = -1;

                // 首次采样：把发现的硬件/传感器全写进诊断日志（定位"没有温度"）。
                // 诊断文本只在首次构建——此前每 2 秒拼完全部传感器再丢弃，纯浪费
                bool dump = !_dumped;
                // SSD 温度变化慢：首帧立采，之后每 5 轮（约 10s）更新一次，减少不必要的 SMART 轮询
                bool sampleStorage = dump || (_sampleCount % 5 == 0);
                _sampleCount++;
                System.Text.StringBuilder? diag = dump ? new System.Text.StringBuilder() : null;
                foreach (IHardware hw in _computer!.Hardware)
                {
                    if (hw.HardwareType == HardwareType.Storage && !sampleStorage)
                    {
                        if (string.IsNullOrEmpty(storageHwName)) storageHwName = hw.Name;
                        ssdTemp ??= _cachedSsdTemp;
                        continue;
                    }
                    try
                    {
                        hw.Update();
                        foreach (IHardware sub in hw.SubHardware)
                        {
                            try { sub.Update(); } catch { }
                        }
                    }
                    catch { continue; }
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
                        if (string.IsNullOrEmpty(cpuHwName)) cpuHwName = hw.Name;
                        // 统一通过 LHM 0.9.6 + PawnIO 签名驱动读取：覆盖 AMD Ryzen (Tctl/Tdie/CCD) 与 Intel (CPU Package/Core Average/P-Core)，
                        // 显式排除 Distance to TjMax 差值传感器（参考 LiteMonitor SensorMap）
                        cpuTemp ??= Pick(hw, SensorType.Temperature,
                            "Core (Tctl/Tdie)", "Tctl/Tdie", "Tctl", "Tdie", "CPU Die",
                            "CPU Package", "Core Average", "Package", "Core Max", "P-Core", "CCD1")
                            ?? FirstOf(hw, SensorType.Temperature);
                        cpuPower = Pick(hw, SensorType.Power, "CPU Package", "Package", "Core + SoC", "Cores") ?? cpuPower;
                        platformPower = Pick(hw, SensorType.Power, "CPU Platform", "Platform") ?? platformPower;
                        cpuFan ??= FirstOf(hw, SensorType.Fan);

                        // 真实 CPU 核心主频（取 P-Core / Core 有效主频最高值，排除 Bus Speed）
                        double maxClk = 0;
                        foreach (ISensor s in hw.Sensors)
                        {
                            if (s.SensorType != SensorType.Clock || !s.Value.HasValue) continue;
                            if (ThermalSensorGuard.IsExcludedCpuClockName(s.Name)) continue;
                            double cv = s.Value.Value;
                            if (ThermalSensorGuard.IsValidClockMHz(cv) && cv > maxClk) maxClk = cv;
                        }
                        if (maxClk > 0) cpuClockMHz = maxClk;

                        // CPU 核心电压（优先 CPU Core/Vcore，否则取有效核心电压均值）
                        double? directV = Pick(hw, SensorType.Voltage, "CPU Core", "Vcore", "Core (SVI");
                        if (ThermalSensorGuard.IsValidVoltage(directV))
                        {
                            cpuVoltage = directV;
                        }
                        else
                        {
                            double sumV = 0; int cntV = 0;
                            foreach (ISensor s in hw.Sensors)
                            {
                                if (s.SensorType == SensorType.Voltage && s.Value.HasValue && ThermalSensorGuard.IsValidVoltage(s.Value.Value))
                                {
                                    sumV += s.Value.Value;
                                    cntV++;
                                }
                            }
                            if (cntV > 0) cpuVoltage = sumV / cntV;
                        }
                    }
                    else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                    {
                        var t = Pick(hw, SensorType.Temperature, "GPU Core", "GPU Hot Spot", "GPU VR", "GPU");
                        var p = Pick(hw, SensorType.Power, "GPU Package", "GPU Chip Power", "GPU Core", "GPU Power");
                        var f = FirstOf(hw, SensorType.Fan);
                        var memU = Pick(hw, SensorType.SmallData,
                            "GPU Memory Used", "D3D Dedicated Memory Used", "D3D Shared Memory Used");
                        var memT = Pick(hw, SensorType.SmallData,
                            "GPU Memory Total", "D3D Dedicated Memory Total", "D3D Shared Memory Total");
                        var clk = Pick(hw, SensorType.Clock, "GPU Core");
                        // LHM GPU 负载兜底：优先 GPU Core，其次取 D3D 各引擎最大值（适配 Intel Arc / 核显）
                        double? ld = Pick(hw, SensorType.Load, "GPU Core", "D3D 3D");
                        if (ld is null)
                        {
                            double maxD3d = -1;
                            foreach (ISensor s in hw.Sensors)
                            {
                                if (s.SensorType == SensorType.Load && s.Value.HasValue
                                    && s.Name.Contains("D3D", StringComparison.OrdinalIgnoreCase)
                                    && s.Value.Value >= 0 && s.Value.Value <= 100
                                    && s.Value.Value > maxD3d)
                                    maxD3d = s.Value.Value;
                            }
                            if (maxD3d >= 0) ld = maxD3d;
                        }

                        // 多 GPU 综合优先级评分（参考 LiteMonitor SensorMap）：独显/在用功耗/有温度/高负载优先
                        bool isDiscrete = hw.HardwareType is HardwareType.GpuNvidia
                            || (hw.HardwareType == HardwareType.GpuAmd && !hw.Name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase));
                        int score = ThermalSensorGuard.GpuPriorityScore(
                            isDiscrete,
                            t is not null,
                            p is not null && p > 0.5,
                            ld is not null && ld > 1.0);
                        if (score > bestGpuScore)
                        {
                            bestGpuScore = score;
                            gpuHwName = hw.Name;
                            gpuTemp = t;
                            gpuPower = p;
                            gpuFan = f;
                            gpuMemUsed = memU;
                            gpuMemTotal = memT;
                            gpuClock = clk;
                            gpuLoad = ld;
                        }
                    }
                    else if (hw.HardwareType == HardwareType.Storage)
                    {
                        if (string.IsNullOrEmpty(storageHwName)) storageHwName = hw.Name;
                        // SSD/NVMe 温度：优先 Composite/主温度，多盘取较高或首个有效读数
                        var st = Pick(hw, SensorType.Temperature,
                            "Composite Temperature", "Temperature", "Drive Temperature", "NAND", "Temperature 1")
                            ?? FirstOf(hw, SensorType.Temperature);
                        if (st is { } sv)
                            ssdTemp = ssdTemp is { } prev ? Math.Max(prev, sv) : sv;
                    }
                    else if (hw.HardwareType == HardwareType.Motherboard)
                    {
                        foreach (IHardware sub in hw.SubHardware)
                        {
                            mbTemp ??= FirstOf(sub, SensorType.Temperature);
                            cpuFan ??= Pick(sub, SensorType.Fan, "CPU", "CPU #1", "Fan #1");
                        }
                    }
                }
                if (dump)
                {
                    _dumped = true;
                    string driverNote = $"PawnIO: {PawnIoDetector.Probe().Summary}";
                    try
                    {
                        lock (DiagLogGate)
                        {
                            string dir = System.IO.Path.Combine(Environment.GetFolderPath(
                                Environment.SpecialFolder.LocalApplicationData), "MyIsland");
                            System.IO.Directory.CreateDirectory(dir);
                            string path = System.IO.Path.Combine(dir, "thermal-diag.log");
                            var info = new System.IO.FileInfo(path);
                            if (info.Exists && info.Length > DiagLogLimit) System.IO.File.Delete(path);
                            System.IO.File.AppendAllText(path,
                                $"[{DateTime.Now:HH:mm:ss}] {driverNote}\n{diag}\n");
                        }
                    }
                    catch { }
                }

            if (sampleStorage)
            {
                ssdTemp ??= ReadStorageTempIoctlFallback();
                _cachedSsdTemp = ssdTemp;
            }
            else
            {
                ssdTemp ??= _cachedSsdTemp;
            }

            // CPU 温度读取链：① LHM 0.9.6（通过微软签名 PawnIO 驱动统一支持 Intel MSR 与 AMD Ryzen SMN）
            //               ② WMI 热区兜底（部分笔记本不支持，失败后自动冷却 5 分钟）
            if (cpuTemp is null)
            {
                cpuTemp = ReadWmiThermal();
            }

            _last = new ThermalSnapshot
            {
                CpuTemp = cpuTemp, GpuTemp = gpuTemp, MotherboardTemp = mbTemp,
                CpuPower = cpuPower, PlatformPower = platformPower,
                CpuClockGHz = cpuClockMHz is { } cm ? cm / 1000.0 : null,
                CpuVoltage = cpuVoltage,
                GpuFanRpm = gpuFan, CpuFanRpm = cpuFan,
                GpuPower = gpuPower, GpuLoad = gpuLoad, SsdTemp = ssdTemp,
                // LHM 显存/时钟单位是 MB/MHz，统一转 GB/MHz
                GpuMemoryUsedGB = gpuMemUsed / 1024.0,
                GpuMemoryTotalGB = gpuMemTotal / 1024.0,
                GpuCoreClockMHz = gpuClock,
                CpuHardwareName = cpuHwName,
                GpuHardwareName = gpuHwName,
                StorageHardwareName = storageHwName,
            };
            Updated?.Invoke(_last);
        }
        catch { /* 单次采样失败保留上次 */ }
    }

    /// <summary>
    /// WMI 热区兜底（很多笔记本不支持）。
    /// 加失败冷却：探测失败（类不存在/无权限/无有效值）后 5 分钟内不再试，
    /// 避免每 2 秒失败一次反复激活 WMI provider 烧 CPU；成功一次就恢复正常探测。
    /// </summary>
    private double? ReadWmiThermal()
    {
        lock (_wmiGate)
        {
            if (DateTime.Now < _wmiNextTry) return null;   // 冷却中：直接跳过，不再碰 WMI
            double? v = ReadWmiThermalCore();
            // 失败则进入冷却；成功则清掉冷却（下次可正常探测）
            _wmiNextTry = v is null ? DateTime.Now + WmiCooldown : DateTime.MinValue;
            return v;
        }
    }

    private static double? ReadWmiThermalCore()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return ReadWmiThermalWindows();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static double? ReadWmiThermalWindows()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "root\\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            using var results = searcher.Get();
            foreach (System.Management.ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    var raw = Convert.ToDouble(obj["CurrentTemperature"]);
                    double c = raw / 10.0 - 273.15;
                    if (c is > 0 and < 120) return c;
                }
            }
        }
        catch { /* 无权限/不支持：返回 null 显示 -- */ }
        return null;
    }

    private bool _dumped;
    private int _sampleCount;
    private double? _cachedSsdTemp;

    /// <summary>
    /// WMI 热区兜底的失败冷却时长。
    /// 本机 root\WMI\MSAcpi_ThermalZoneTemperature 并不存在，单次探测实测 335ms；
    /// 不冷却就会每 2 秒失败一次，反复激活 WMI provider 持续烧 CPU。
    /// </summary>
    private static readonly TimeSpan WmiCooldown = TimeSpan.FromMinutes(5);

    /// <summary>WMI 冷却状态的锁：探测可能在采样线程上进行，冷却时间戳要线程安全地读写。</summary>
    private readonly object _wmiGate = new();

    /// <summary>下次允许再试 WMI 兜底的时间；DateTime.MinValue = 不在冷却期。</summary>
    private DateTime _wmiNextTry = DateTime.MinValue;
    private DateTime _storageIoctlNextTry = DateTime.MinValue;

    private double? ReadStorageTempIoctlFallback()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (DateTime.Now < _storageIoctlNextTry) return null;
        double? v = ReadStorageTempIoctlWindows();
        _storageIoctlNextTry = v is null ? DateTime.Now + WmiCooldown : DateTime.MinValue;
        return v;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
        byte[] lpInBuffer, int nInBufferSize, byte[] lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static double? ReadStorageTempIoctlWindows()
    {
        const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        const uint FILE_SHARE_READ_WRITE = 0x3;
        const uint OPEN_EXISTING = 3;
        string sysDrive = (System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
        string[] targets = [$"\\\\.\\{sysDrive}", @"\\.\PhysicalDrive0", @"\\.\PhysicalDrive1"];
        byte[] query = new byte[12];
        byte[] output = new byte[512];

        foreach (string path in targets)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = CreateFileW(path, 0, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) continue;
                foreach (int propId in new[] { 50 /* StorageDeviceTemperatureProperty */, 49 /* StorageAdapterTemperatureProperty */ })
                {
                    BitConverter.TryWriteBytes(query.AsSpan(0, 4), propId);
                    if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, output, output.Length, out int ret, IntPtr.Zero)
                        && ret >= 40)
                    {
                        var parsed = ThermalSensorGuard.ParseStorageTemperatureDescriptor(output.AsSpan(0, ret));
                        if (parsed is not null) return parsed;
                    }
                }
            }
            catch { }
            finally
            {
                if (h != IntPtr.Zero && h != new IntPtr(-1)) CloseHandle(h);
            }
        }
        return null;
    }

    /// <summary>诊断日志上限（约 1MB）：超过就删掉旧文件重开一份，免得日志无限膨胀（同 App.WriteLog 的做法）。</summary>
    private const long DiagLogLimit = 1_048_576;

    /// <summary>
    /// 读数是否可信：
    /// - 风扇/功耗类接受 0：风扇停转 0 RPM、GPU 空闲 0 W 都是真实读数，
    ///   以前一律按 &gt; 0.01 过滤，会把合法的 0 当成"读不到"显示 "--"；
    /// - 其它传感器（温度等）保持原阈值：负值/近零值是无效读数。
    /// 无论哪种都必须 HasValue —— 没有读数与读数为 0 是两回事。
    /// </summary>
    private static bool IsValidReading(SensorType type, double? value, string? sensorName = null)
    {
        if (!value.HasValue || !double.IsFinite(value.Value)) return false;
        if (type == SensorType.Temperature)
            return !ThermalSensorGuard.IsExcludedTempSensorName(sensorName)
                && ThermalSensorGuard.IsValidTemp(value.Value);
        if (type == SensorType.Voltage)
            return ThermalSensorGuard.IsValidVoltage(value.Value);
        if (type == SensorType.Clock)
            return ThermalSensorGuard.IsValidClockMHz(value.Value);
        if (type is SensorType.Fan or SensorType.Power or SensorType.Load) return value.Value >= 0;
        return value.Value > 0.01;
    }

    private static double? Pick(IHardware hw, SensorType type, params string[] names)
    {
        var sensors = hw.Sensors.Where(s => s.SensorType == type && IsValidReading(type, s.Value, s.Name));
        foreach (var n in names)
        {
            var hit = sensors.FirstOrDefault(s =>
                s.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit.Value;
        }
        return null;
    }

    private static double? FirstOf(IHardware hw, SensorType type) =>
        hw.Sensors.FirstOrDefault(s => s.SensorType == type && IsValidReading(type, s.Value, s.Name))?.Value;

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            _wanted = false;
            _timer?.Dispose();
            _timer = null;
        }
        lock (_gate)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
    }
}
