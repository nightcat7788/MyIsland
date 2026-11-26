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
/// 硬件详情窗口（2026-11-26 task4）：本类同时是**全部硬件传感器**的枚举源（<see cref="Sensors"/>）——
/// 只读窗口列的就是这份枚举结果，不另起一套 LHM 实例（重复实例会重复吃资源，还会互相拆掉静态资源）。
/// </summary>
public sealed class ThermalService : IDisposable, IHardwareSensorSource
{
    private Computer? _computer;
    private readonly object _gate = new();
    private ThermalSnapshot _last = new();
    private System.Threading.Timer? _timer;
    private volatile bool _wanted;
    // 打开 _computer 那一刻温度驱动（PawnIO）在不在。打开时不在、后来装上了 ⇒ 需要重建，见 Sample。
    private bool _openedWithDriver;

    // 全部已枚举到的传感器（不只是挑出来的那几个）：硬件详情窗口要列的就是这份枚举结果。
    private IReadOnlyList<HardwareSensorEntry> _sensors = Array.Empty<HardwareSensorEntry>();

    // 请求刷新的排队闸：上一次刷新还没做完时不再排新的（窗口每秒请求一次，采样慢时会堆积）。
    private int _refreshBusy;

    public event Action<ThermalSnapshot>? Updated;
    public ThermalSnapshot Current => _last;

    /// <summary>
    /// 进程内当前存活的实例（Windows 端只有一个岛体窗口 ⇒ 只有一个）。
    /// 用途：只读的硬件传感器窗口要**复用同一份 LHM 枚举结果**，不能自己再开一个 Computer ——
    /// LHM 的 OpCode 与总线互斥体是库里的静态资源（见 <see cref="ReopenComputer"/> 的说明），
    /// 两个 Computer 实例同时开着会互相拆掉。
    /// </summary>
    public static ThermalService? Instance { get; private set; }

    /// <summary>是否以管理员身份运行（决定"读不到"显示"需管理员"还是"未知"）。</summary>
    public bool Elevated { get; }

    /// <summary>最近一次枚举到的全部硬件传感器（顺序 = 枚举顺序）。</summary>
    public IReadOnlyList<HardwareSensorEntry> Sensors => _sensors;

    /// <summary>枚举结果更新后触发（**在采样线程上**触发；订阅方要自己切回 UI 线程）。</summary>
    public event Action? SensorsChanged;

    public ThermalService()
    {
        Instance = this;
        Elevated = ComputeElevated();
    }

    /// <summary>当前进程是否以管理员身份运行（读不出主板 / SuperIO / CPU MSR 时用得上）。</summary>
    private static bool ComputeElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

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
            // 显存口径（"vendor" / "dedicated" / "shared" / ""=未知）：与显存数值同源，取自同一个传感器选取
            string gpuMemUsedKind = "", gpuMemTotalKind = "";
            // CPU 温度是从哪个传感器、按什么口径选出来的（诊断日志用）
            string? cpuTempSource = null;

            // SMN 直读绝不在常规传感器读取前探测：这条通道会加载未签名内核驱动并映射物理内存，
            // 只有 LHM 已经读不到 CPU 温度、且用户明确开启时，才允许进入下面的 fallback。
            // 首次采样的诊断日志也只记录已有状态，不提前触发高风险通道。

                // 首次采样：把发现的硬件/传感器全写进诊断日志（定位"没有温度"）。
                // 诊断文本只在首次构建——此前每 2 秒拼完全部传感器再丢弃，纯浪费
                bool dump = !_dumped;
                System.Text.StringBuilder? diag = dump ? new System.Text.StringBuilder() : null;
                // 本轮枚举到的全部传感器（硬件详情窗口的清单）：跟诊断日志无关，每次采样都收。
                var sensorEntries = new List<HardwareSensorEntry>();
                foreach (IHardware hw in _computer!.Hardware)
                {
                    hw.Update();
                    foreach (IHardware sub in hw.SubHardware) sub.Update();
                    // **包括读不到的那些**（它们的值是 null）—— 完整清单才是"枚举到了却看不见"的那扇门。
                    CollectEntries(hw, sensorEntries);
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
                        // 取值走规则类（HardwareSensorRules），不再用"名字白名单 + Contains"。
                        // 分层口径与 Services/CpuTemperatureRule.cs 完全一致（那里是既有结论）：
                        //   package → tdie（不含 tctl）→ tctl/tdie → tctl → ccd → core max → 逐核取最大 → average 兜底，
                        // 同层取最大值（多核 / 多 CCD 时取最热的那一个）。
                        // Tctl 比真实核心温度高 10–27°C，绝不能让它排在 Tdie 前面
                        // （依据：LibreHardwareMonitor v0.9.6 Hardware/Cpu/Amd17Cpu.cs，2026-11-24 核对）。
                        // "Distance to TjMax"（离最高温还有多远，不是温度）等干扰项由规则类统一排除。
                        if (cpuTemp is null)
                        {
                            var cpuTempPick = HardwareSensorRules.Pick(HardwareSensorKind.CpuTemperature, Collect(hw, SensorType.Temperature));
                            cpuTemp = cpuTempPick?.Value;
                            cpuTempSource = cpuTempPick?.SensorName;   // 诊断日志：这次温度是从哪个传感器读出来的
                        }
                        cpuPower = HardwareSensorRules.Pick(HardwareSensorKind.CpuPower, Collect(hw, SensorType.Power))?.Value ?? cpuPower;
                        cpuFan ??= HardwareSensorRules.Pick(HardwareSensorKind.CpuFan, Collect(hw, SensorType.Fan))?.Value;
                    }
                    else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                    {
                        // 名字 → 语义的判定交给规则类：GPU Core → 温度，GPU Package / Chip Power → 功耗。
                        var t = HardwareSensorRules.Pick(HardwareSensorKind.GpuTemperature, Collect(hw, SensorType.Temperature))?.Value;
                        // 多 GPU（核显+独显）：优先"正在耗电"的那块（独显），其次取先遇到的那块。
                        // 注意 0W 也是合法读数（空闲显卡），但拿 0W 当"独显"判据会把核显选上来，
                        // 所以判据用 p > 0，显示值仍允许 0。
                        var p = HardwareSensorRules.Pick(HardwareSensorKind.GpuPower, Collect(hw, SensorType.Power))?.Value;
                        var f = HardwareSensorRules.Pick(HardwareSensorKind.GpuFan, Collect(hw, SensorType.Fan))?.Value;
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
                        // 语义判定交给规则类：厂商显存（GPU Memory Used）→ D3D 专用显存 → D3D 共享显存。
                        // 口径与数值**同源**：规则类一次给出"值 + 命中层对应的口径"（厂商/专用/共享），
                        // 共享口径下那个"总量"是共享内存池大小（≈ 系统内存），不是显存容量 —— 显示层靠它决定要不要显示总量。
                        var memUsed = HardwareSensorRules.PickGpuMemory(HardwareSensorKind.GpuMemoryUsed, Collect(hw, SensorType.SmallData));
                        var memTotal = HardwareSensorRules.PickGpuMemory(HardwareSensorKind.GpuMemoryTotal, Collect(hw, SensorType.SmallData));
                        if (gpuMemUsed is null && memUsed is not null)
                        {
                            gpuMemUsed = memUsed.Value;
                            gpuMemUsedKind = memUsed.Kind;
                        }
                        if (gpuMemTotal is null && memTotal is not null)
                        {
                            gpuMemTotal = memTotal.Value;
                            gpuMemTotalKind = memTotal.Kind;
                        }
                        // GPU 核心频率
                        gpuClock ??= HardwareSensorRules.Pick(HardwareSensorKind.GpuClock, Collect(hw, SensorType.Clock))?.Value;
                    }
                    else if (hw.HardwareType == HardwareType.Storage)
                    {
                        // SSD 温度：NVMe 用户态 IOCTL 一般免提权；读不到保持 null 显示 "--"
                        // 语义判定交给规则类：SMART 合成温度（Composite）优先，其次驱动给的温度，最后 NAND。
                        ssdTemp ??= HardwareSensorRules.Pick(HardwareSensorKind.StorageTemperature, Collect(hw, SensorType.Temperature))?.Value;
                    }
                    else if (hw.HardwareType == HardwareType.Motherboard)
                    {
                        // 主板/SuperIO 的温度与风扇：逐个子硬件收集，语义判定交给规则类。
                        var mbTemps = new List<HardwareSensor>();
                        foreach (IHardware sub in hw.SubHardware)
                        {
                            mbTemps.AddRange(Collect(sub, SensorType.Temperature));
                            // CPU 风扇挂在主板 SuperIO 上（"CPU Fan" / "CPU #1"）；机箱风扇不算 CPU 风扇。
                            cpuFan ??= HardwareSensorRules.Pick(HardwareSensorKind.CpuFan, Collect(sub, SensorType.Fan))?.Value;
                        }
                        mbTemp ??= HardwareSensorRules.Pick(HardwareSensorKind.MotherboardTemperature, mbTemps)?.Value;
                    }
                }
                // 交给窗口的一份不可变快照（外面拿到的是拷贝，不会看到半截状态）。
                _sensors = sensorEntries.ToArray();
                SensorsChanged?.Invoke();

                if (dump)
                {
                    _dumped = true;
                    diag?.AppendLine($"CPU 温度来源：{cpuTempSource ?? "未选到"}");
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
                // 已用与总量的口径一致才认；不一致（跨 GPU 混采）⇒ 未知，不猜
                GpuMemoryKind = GpuMemoryDisplay.CombineKind(gpuMemUsedKind, gpuMemTotalKind),
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

    /// <summary>
    /// 把某块硬件（含它的子硬件）的**全部**传感器收进清单。
    ///
    /// 与 <see cref="Collect"/> 的分工：那个只收"某种类型 + 读数有效"的候选（给规则类挑代表值用）；
    /// 这里收**全部**（硬件详情窗口要的是完整清单），读不到的传感器照样在列，只是值为 null。
    /// </summary>
    private static void CollectEntries(IHardware hw, List<HardwareSensorEntry> into)
    {
        // 主板 / SuperIO 的读数要管理员权限（Ring0 驱动）。
        bool needsElevation = hw.HardwareType == HardwareType.Motherboard;
        AddEntries(hw, hw.Name, needsElevation, into);
        foreach (IHardware sub in hw.SubHardware)
            AddEntries(sub, sub.Name, needsElevation, into);
    }

    /// <summary>
    /// 收一块硬件上的全部传感器（子硬件的硬件名用子硬件自己的名字）。
    /// </summary>
    private static void AddEntries(IHardware hw, string hardwareName, bool needsElevation, List<HardwareSensorEntry> into)
    {
        foreach (ISensor s in hw.Sensors)
        {
            var category = CategoryOf(s.SensorType);
            // 提权要求：主板 / SuperIO 一律要；CPU 的温度 / 功耗走 MSR，也要。
            bool elevation = needsElevation
                || (hw.HardwareType == HardwareType.Cpu
                    && category is HardwareSensorCategory.Temperature or HardwareSensorCategory.Power);
            // 读数有效性沿用 <see cref="IsValidReading"/>：温度类的 0 是坏值（读不到），
            // 风扇 / 功耗的 0 是合法读数（停转 / 空闲）—— 两者不能混。
            // 占用率的 0% 也是合法读数（空闲），IsValidReading 是给温度挑选用的，这里单独放行。
            double? value = IsValidReading(s.SensorType, s.Value)
                ? s.Value
                : (s.SensorType == SensorType.Load && s.Value is { } load && load >= 0 ? load : null);
            into.Add(new HardwareSensorEntry(hardwareName, s.Name, category, value, elevation));
        }
    }

    /// <summary>
    /// LibreHardwareMonitor 的传感器类别 → 我们的类别（纯枚举映射）。
    /// SensorType.Data（字节）不并进 Data（我们那边是 MB，显存口径）—— 宁可归"其它"，不乱配单位。
    /// </summary>
    private static HardwareSensorCategory CategoryOf(SensorType type) => type switch
    {
        SensorType.Temperature => HardwareSensorCategory.Temperature,
        SensorType.Power => HardwareSensorCategory.Power,
        SensorType.Fan => HardwareSensorCategory.Fan,
        SensorType.Clock => HardwareSensorCategory.Clock,
        SensorType.SmallData => HardwareSensorCategory.Data,
        SensorType.Load => HardwareSensorCategory.Load,
        SensorType.Voltage => HardwareSensorCategory.Voltage,
        SensorType.Control => HardwareSensorCategory.Control,
        _ => HardwareSensorCategory.Other,
    };

    /// <summary>
    /// 立刻重新枚举一次（后台线程）。
    ///
    /// 为什么需要它：服务自己的定时器只在"有人要温度"时才跑（见 <see cref="SetWanted"/>），
    /// 面板收起时它是停的 —— 硬件详情窗口不能因此读到旧值。
    /// 复用同一个 Computer（只有 _computer 为 null 才新建），**不另起一套 LHM 实例**。
    /// </summary>
    public void RequestRefresh()
    {
        // 上一次刷新还没做完就不再排队（窗口每秒请求一次，采样慢时会堆积）。
        if (Interlocked.Exchange(ref _refreshBusy, 1) == 1) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (_computer is null)
                    {
                        _computer = OpenComputer();
                        _openedWithDriver = DriverPresent;
                    }
                    if (_computer is null) return;
                    // 驱动是后台装上的 ⇒ 这里也要能自愈重建（与 Sample 同一套判断）
                    if (PawnIoInstaller.NeedsReopen(_openedWithDriver, DriverPresent)) ReopenComputer();
                    if (_computer is null) return;
                    SampleCore();
                }
            }
            catch { /* 单次刷新失败保留上次 */ }
            finally { Interlocked.Exchange(ref _refreshBusy, 0); }
        });
    }

    /// <summary>
    /// 把某块硬件上某种类型的传感器收集成规则类要的候选列表（名字 + 读数）。
    /// 读数有效性沿用 <see cref="IsValidReading"/>：风扇/功耗允许 0（停转/空闲是真实读数），
    /// 其余必须 &gt; 0.01；没有读数与读数为 0 是两回事。
    /// 选哪个传感器由 HardwareSensorRules 决定，这里只负责"把候选收齐"。
    /// </summary>
    private static List<HardwareSensor> Collect(IHardware hw, SensorType type)
    {
        var list = new List<HardwareSensor>();
        foreach (ISensor s in hw.Sensors)
            if (s.SensorType == type && IsValidReading(type, s.Value))
                list.Add(new HardwareSensor(s.Name, s.Value!.Value));
        return list;
    }

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
        // 进程内的实例指针：服务没了就别让窗口拿着它刷新（那时 Computer 已经关掉了）。
        if (ReferenceEquals(Instance, this)) Instance = null;
    }
}
