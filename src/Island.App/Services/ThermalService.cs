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

    /// <summary>
    /// 是否允许走 AMD SMN 直读通道（默认关闭）。
    /// ⚠️ 2026-11-23：这条通道以管理员运行时会把未签名的 inpoutx64.sys 装进系统，
    /// 并映射 PCIe ECAM/MMIO 物理区，导致三次 0x1EA 蓝屏（详见 InpOutSmnService 注释）。
    /// 现在必须由用户在设置里显式开启才会走到。
    /// </summary>
    public bool AllowSmnDirectRead
    {
        get => _smn.AllowSmnDirectRead;
        // 置 false 会立刻释放已映射的物理区与已加载的 inpoutx64.dll（见 SmnChannel）
        set => _smn.AllowSmnDirectRead = value;
    }

    /// <summary>UI 声明需要温度数据（展开面板/收起显示）；false 时停止采样省资源。</summary>
    public void SetWanted(bool wanted)
    {
        lock (_gate)
        {
            if (_wanted == wanted) return;
            _wanted = wanted;
            if (wanted) EnsureStarted();
            else
            {
                // 必须把 _timer 置 null：EnsureStarted 用的是 _timer ??= new Timer(...)，
                // 只 Dispose 不置空 → 引用仍非 null → 永不重建 →
                // 面板展开一次再收起后温度/风扇/功耗永久停在最后一帧，高温告警随之静默失效。
                _timer?.Dispose();
                _timer = null;
            }
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
        // 重建路径说明：这里只重建"采样定时器"，不重复注册任何事件——
        // Updated 由宿主订阅一次（订阅关系与定时器生命周期无关）；
        // LHM Computer 也复用同一个实例（_computer 为 null 才会新建并 Open），
        // 所以 SetWanted(false)→(true) 来回切不会产生重复订阅或重复采样。
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
            // 默认不碰这条通道：它要加载未签名内核驱动 + 映射物理内存，代价是整机稳定性
            // SMN 通道由 SmnChannel 统一裁决：未显式开启 ⇒ Ensure() 返回 null
            // ⇒ 绝不构造服务、绝不 LoadLibrary、绝不装驱动、绝不映射物理内存。
            // 这条判断以前写在这里，而测试工程没链接 ThermalService.cs ⇒ 门禁测不到。
            var smnSvc = _smn.Ensure();
            double? smnCachedTemp = null;
            if (smnSvc is not null)
            {
                if (smnSvc.TryReadCpuTemperature(out double st)) smnCachedTemp = st;
                _smuProbeResult = smnSvc.Status;
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
                        // 多 GPU（核显+独显）：优先"正在耗电"的那块（独显），其次取先遇到的那块。
                        // 注意 0W 也是合法读数（空闲显卡），但拿 0W 当"独显"判据会把核显选上来，
                        // 所以判据用 p > 0，显示值仍允许 0。
                        var p = Pick(hw, SensorType.Power, "GPU Package", "GPU Chip Power", "GPU Core");
                        var f = FirstOf(hw, SensorType.Fan);
                        var memU = Pick(hw, SensorType.SmallData,
                            "GPU Memory Used", "D3D Dedicated Memory Used", "D3D Shared Memory Used");
                        var memT = Pick(hw, SensorType.SmallData,
                            "GPU Memory Total", "D3D Dedicated Memory Total", "D3D Shared Memory Total");
                        var clk = Pick(hw, SensorType.Clock, "GPU Core");
                        if (t is not null)
                        {
                            bool thisActive = p is not null && p > 0;
                            bool currentActive = gpuPower is not null && gpuPower > 0;
                            if (gpuTemp is null || (thisActive && !currentActive))
                            {
                                gpuTemp = t;
                                gpuPower = p;
                                // 选择了另一块 GPU 时，空传感器必须保持为空，不能沿用上一块卡的值。
                                gpuFan = f;
                                gpuMemUsed = memU;
                                gpuMemTotal = memT;
                                gpuClock = clk;
                            }
                        }
                        else
                        {
                            gpuFan ??= f;
                            gpuMemUsed ??= memU;
                            gpuMemTotal ??= memT;
                            gpuClock ??= clk;
                        }
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
                    string smnNote = AllowSmnDirectRead
                        ? $"SMN-inpoutx64: {_smuProbeResult ?? "not probed"}" +
                          (smnCachedTemp is { } probe ? $" ({probe:F1}C)" : " (read fail)")
                        : "SMN-inpoutx64: disabled (EnableSmnDirectRead=off)";
                    try
                    {
                        string dir = System.IO.Path.Combine(Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData), "MyIsland");
                        System.IO.Directory.CreateDirectory(dir);
                        string path = System.IO.Path.Combine(dir, "thermal-diag.log");
                        // 轮转：超过约 1MB 就删掉旧文件重开一份（同 App.WriteLog 处理 crash.log 的做法），
                        // 否则诊断日志一直追加会慢慢吃满磁盘
                        var info = new System.IO.FileInfo(path);
                        if (info.Exists && info.Length > DiagLogLimit) System.IO.File.Delete(path);
                        System.IO.File.AppendAllText(path,
                            $"[{DateTime.Now:HH:mm:ss}] {smnNote}\n{diag}\n");
                    }
                    catch { }
                }

            // CPU 温度兜底链：① LHM（PawnIO，微软签名的驱动，读不到就 null）
            //               ② SMN 直读（仅显式开启且 CPU 是 AMD；默认关闭——它会装未签名驱动，见 InpOutSmnService）
            //               ③ WMI 热区（很多笔记本不支持）
            if (cpuTemp is null && smnCachedTemp is { } smnTemp)
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
    // SMN 高风险通道的所有权集中在 SmnChannel（可被测试链接覆盖，见该文件说明）
    private readonly SmnChannel _smn = new();
    private string? _smuProbeResult;

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

    /// <summary>诊断日志上限（约 1MB）：超过就删掉旧文件重开一份，免得日志无限膨胀（同 App.WriteLog 的做法）。</summary>
    private const long DiagLogLimit = 1_048_576;

    /// <summary>
    /// 读数是否可信：
    /// - 风扇/功耗类接受 0：风扇停转 0 RPM、GPU 空闲 0 W 都是真实读数，
    ///   以前一律按 &gt; 0.01 过滤，会把合法的 0 当成"读不到"显示 "--"；
    /// - 其它传感器（温度等）保持原阈值：负值/近零值是无效读数。
    /// 无论哪种都必须 HasValue —— 没有读数与读数为 0 是两回事。
    /// </summary>
    private static bool IsValidReading(SensorType type, double? value)
    {
        if (!value.HasValue) return false;
        if (type is SensorType.Fan or SensorType.Power) return value.Value >= 0;
        return value.Value > 0.01;
    }

    private static double? Pick(IHardware hw, SensorType type, params string[] names)
    {
        var sensors = hw.Sensors.Where(s => s.SensorType == type && IsValidReading(type, s.Value));
        foreach (var n in names)
        {
            var hit = sensors.FirstOrDefault(s =>
                s.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit.Value;
        }
        return null;
    }

    private static double? FirstOf(IHardware hw, SensorType type) =>
        hw.Sensors.FirstOrDefault(s => s.SensorType == type && IsValidReading(type, s.Value))?.Value;

    public void Dispose()
    {
        // 停掉定时器并置 null：与 SetWanted(false) 同样把 _wanted 归零，
        // 防止已排队的回调在 Computer.Close() 之后再采样一次
        lock (_gate)
        {
            _wanted = false;
            _timer?.Dispose();
            _timer = null;
        }
        _smn.Dispose();   // 关闭开关并释放已映射的物理区 / 已加载的 dll
        try { _computer?.Close(); } catch { }
    }
}
