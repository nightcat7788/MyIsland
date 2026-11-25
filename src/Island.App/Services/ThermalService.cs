using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace Island.App.Services;

/// <summary>
/// 温度/风扇/功耗：LibreHardwareMonitorLib（库引用，非驱动模式）。
/// - GPU（NVML/ADL 用户态）与主板 SuperIO 部分机器无需管理员即可读
/// - CPU MSR 温度需要 PawnIO/管理员，读不到时如实给 null，UI 显示 "--"，绝不伪造
/// - 驱动由程序自己带（2026-11-24）：以管理员运行且 PawnIO 缺失/过旧时，App 在后台调 PawnIoInstaller.EnsureInstalled 装好。
///   本类不参与安装，也不等安装；只在采样时发现"驱动到位了"就地重建 Computer（见 Sample 的自愈）
/// 采样节流：仅当有消费者（展开面板或开启显示）时才每 2s 更新一次。
/// </summary>
public sealed class ThermalService : IDisposable
{
    private Computer? _computer;
    private readonly object _gate = new();
    private ThermalSnapshot _last = new();
    private System.Threading.Timer? _timer;
    private volatile bool _wanted;
    // 打开 _computer 那一刻温度驱动（PawnIO）在不在。打开时不在、后来装上了 ⇒ 需要重建，见 Sample。
    private bool _openedWithDriver;

    public event Action<ThermalSnapshot>? Updated;
    public ThermalSnapshot Current => _last;

    /// <summary>
    /// 温度驱动（PawnIO）是否到位。每次都现读，不缓存：驱动可能在本次运行中被装上。
    /// 判定在 PawnIoInstaller.IsDriverReady：注册表版本够新，且本进程此刻没有在跑安装器
    ///（安装器可能先写注册表后装好驱动，那段时间不能算到位）。
    /// 它只说明"装了"，不说明"这个进程读得到"—— 打开驱动设备还要管理员权限。
    /// </summary>
    public static bool DriverPresent => PawnIoInstaller.IsDriverReady();

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
        // 快路径：值没变就不进锁。SetWanted 是宿主每一帧都调的（IslandWindow.Frame_Tick），
        // 而采样线程做"驱动到位后重建 Computer"时要持同一把锁几百毫秒 —— 不加这道判断，
        // 界面线程在那期间会被卡住等一次重建（2026-11-24）。
        // _wanted 是 volatile：这里读到的是采样线程/上一次调用刚写下的值；读旧了也只是多进一次锁，由锁内再判一遍。
        if (_wanted == wanted) return;
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
                _computer = OpenComputer();
                _openedWithDriver = DriverPresent;
            }
            if (_computer is null) return;
            SampleCore();
        }
    }

    /// <summary>
    /// 新建并打开 LHM Computer；打不开返回 null（无权限等）—— 保持无温度运行。
    /// 诊断首采、正式采样、驱动到位后的自愈重建都从这里建：开关只写这一份。
    /// </summary>
    private static Computer? OpenComputer()
    {
        try
        {
            // 只有核显的机器上，不让库去初始化 Intel 的 GCL 计算栈（见 IntelComputePolicy）：
            // 那一步耗 1.4–1.8 秒 CPU，还会造成 700 MB – 4.5 GB 的启动内存尖峰，却一个读数都不产生。
            // 必须在创建第一个 Computer 之前做（IntelGpuGroup 在 Computer.Open 时才读 IsAvailable）。
            // 探测结果 App 启动时已在后台算好并缓存（启动步骤"显卡探测"），这里不现探。
            if (IntelComputePolicy.ShouldSkipOnThisMachine()) IntelComputePolicy.Disable();

            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                // ⚠️ 2026-11-22 实机教训：LHM 存储传感器（NVMe SMART IOCTL）在本机
                // (Lenovo secured-core) 触发 SECURE_KERNEL_ERROR 0x1EA 蓝屏两次，
                // 与启用时间点精确相关。默认永久关闭；SSD 温度显示 "--"。
                IsMemoryEnabled = false, IsStorageEnabled = false, IsControllerEnabled = false,
                IsNetworkEnabled = false, IsPsuEnabled = false,
            };
            computer.Open();
            return computer;
        }
        catch
        {
            return null;
        }
    }

    private void EnsureStarted()
    {
        if (_computer is null)
        {
            _computer = OpenComputer();
            _openedWithDriver = DriverPresent;
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
            if (!_wanted) return;

            // 自愈（2026-11-24）：驱动是启动之后才在后台装好的。
            // LHM 0.9.6 里每个用到驱动的模块对象（IntelMsr / AmdFamily17 等）都在构造时就 CreateFile 驱动设备，
            // 拿到的句柄终身不变：Computer 打开时驱动不在，这些对象之后就一直读不到温度。
            // 所以"打开时没有驱动、现在有了"就地在采样线程上重建 —— 一个采样间隔内恢复读数，且没有任何线程在等安装。
            // 这里只判定不等待：驱动还没到位就继续用原来那份采样（CPU 温度为 null），下次采样再看。
            // 安装器还在跑的时候 DriverPresent 恒为 false（见它的说明），所以不会对着装了一半的驱动去重建。
            // 代价：只有"打开时没驱动"的进程才会每次采样读一次注册表；驱动到位、重建过之后就不再读了。
            //
            // 为什么不放进 PrimeOnce 也会走的 SampleCore：重建要新建并打开一整套硬件对象，不便宜，
            // 而 PrimeOnce 是在 UI 线程上采的，不能让它在 UI 线程上做。那 5 秒内驱动就装好的话，
            // PrimeOnce 新建 Computer 时驱动已经在了，本来就不需要重建；更晚装好的由定时器线程的第一次采样补上。
            if (PawnIoInstaller.NeedsReopen(_openedWithDriver, DriverPresent)) ReopenComputer();

            if (_computer is null) return;
            SampleCore();
        }
    }

    /// <summary>
    /// 驱动到位后重建 Computer（只在采样线程上、持 <see cref="_gate"/> 调用）。
    /// 顺序必须是先关旧的、再开新的：CPUID/RDTSC 那块可执行内存（OpCode）与总线互斥体（Mutexes）
    /// 是库里的静态资源，Computer.Close() 会把它们拆掉 —— 先开新的再关旧的，等于把新实例的这两样也拆了。
    /// （依据 LibreHardwareMonitorLib v0.9.6 源码，2026-11-24 核对。）
    /// 重建失败与启动时打不开是同一个结果：保持无温度运行，不在采样线程上反复重试。
    /// </summary>
    private void ReopenComputer()
    {
        try { _computer?.Close(); } catch { }
        _computer = OpenComputer();
        _openedWithDriver = DriverPresent;
        // 重建之后温度传感器才读得到值：下次采样重新 dump 一份诊断，留下带读数的那份
        _dumped = false;
    }

    private void SampleCore()
    {
        try
        {
            double? cpuTemp = null, gpuTemp = null, mbTemp = null, cpuPower = null,
                    gpuFan = null, cpuFan = null, gpuPower = null, ssdTemp = null,
                    gpuMemUsed = null, gpuMemTotal = null, gpuClock = null;

            // SMN 直读绝不在常规传感器读取前探测：这条通道会加载未签名内核驱动并映射物理内存，
            // 只有 LHM 已经读不到 CPU 温度、且用户明确开启时，才允许进入下面的 fallback。
            // 首次采样的诊断日志也只记录已有状态，不提前触发高风险通道。

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
                        // 取值顺序 = 优先级。AMD（LHM Amd17Cpu）的传感器名：Core (Tctl) / Core (Tdie) / Core (Tctl/Tdie)。
                        // 1600X/1700X/1800X/2700X 与初代 Threadripper 同时有 Tctl 和 Tdie，Tctl 比真实核心温度高 10–27°C；
                        // 以前 "Tctl" 排在前面会先命中它 ⇒ 读数虚高、容易误触高温告警。所以 "Tdie" 必须排在 "Tctl" 之前
                        // （其它 Zen 只暴露 Core (Tctl/Tdie)，同样被 "Tdie" 命中；Intel 的传感器名不含 Tdie，不受影响）。
                        // 依据：LibreHardwareMonitor v0.9.6 Hardware/Cpu/Amd17Cpu.cs（2026-11-24 核对）。
                        cpuTemp ??= Pick(hw, SensorType.Temperature, "Core Average", "CPU Package", "Package", "Tdie", "Tctl");
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
                        if (t is not null)
                        {
                            bool thisActive = p is not null && p > 0;
                            bool currentActive = gpuPower is not null && gpuPower > 0;
                            if (gpuTemp is null || (thisActive && !currentActive))
                            {
                                gpuTemp = t;
                                gpuPower = p;
                            }
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
                    string smnNote = AllowSmnDirectRead
                        ? $"SMN-inpoutx64: {_smuProbeResult ?? "not needed (LHM/WMI provided temperature or fallback not reached)"}"
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
            if (cpuTemp is null && AllowSmnDirectRead && _smn.Ensure() is { } readSvc)
            {
                if (readSvc.TryReadCpuTemperature(out double smnTemp))
                {
                    cpuTemp = smnTemp;
                    _smuProbeResult = $"fallback success ({smnTemp:F1}C)";
                }
                else
                {
                    _smuProbeResult = readSvc.Status;
                }
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
