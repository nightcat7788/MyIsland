using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Island.Core.Monitoring;

namespace Island.Avalonia;

/// <summary>平台采样器：产出与 Windows 版相同的 MonitorSnapshot。</summary>
public interface ISampler : IDisposable
{
    event Action<MonitorSnapshot>? Snapshot;
    void Start();

    /// <summary>告诉采样器 UI 当前需要哪些读数（按需采样）；不要求的项不读。</summary>
    void SetDemand(SamplerDemand demand) { }
}

public static class Samplers
{
    public static ISampler Create(int intervalMs) =>
        OperatingSystem.IsMacOS() ? new MacSampler(intervalMs) : new WinSampler(intervalMs);
}

/// <summary>Windows：直接复用 Island.Native.SystemMonitor（PDH 全套）+ WMI 电池 + LHM 温度。</summary>
public sealed class WinSampler : ISampler
{
    private readonly Island.Native.SystemMonitor _inner;
    private readonly Island.App.Services.ThermalService _thermal = new();
    public event Action<MonitorSnapshot>? Snapshot;

    private DateTime _battChecked = DateTime.MinValue;
    private double? _battPct;
    private bool _battCharging;

    public WinSampler(int intervalMs)
    {
        _inner = new Island.Native.SystemMonitor(intervalMs);
        _thermal.SetWanted(true);   // 常驻采样 CPU/GPU/SSD 温度（2s）
        _inner.Snapshot += s =>
        {
            SampleBattery();
            var th = _thermal.Current;
            Snapshot?.Invoke(s with
            {
                BatteryPercent = _battPct,
                BatteryCharging = _battCharging,
                CpuTemp = th?.CpuTemp,
                GpuTemp = th?.GpuTemp,
                SsdTemp = th?.SsdTemp,
                MotherboardTemp = th?.MotherboardTemp,
                GpuMemoryUsedGB = th?.GpuMemoryUsedGB,
                GpuMemoryTotalGB = th?.GpuMemoryTotalGB,
                GpuCoreClockMHz = th?.GpuCoreClockMHz,
            });
        };
    }

    /// <summary>本机电池（WMI Win32_Battery，30 秒一采；台式机无电池给 null）。</summary>
    private void SampleBattery()
    {
        if ((DateTime.Now - _battChecked).TotalSeconds < 30) return;
        _battChecked = DateTime.Now;
        try
        {
            using var q = new System.Management.ManagementObjectSearcher(
                "SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
            foreach (var o in q.Get())
            {
                _battPct = Convert.ToDouble(o["EstimatedChargeRemaining"]);
                // 充电判定：6=Charging 7=Charging and High 8=Charging and Low 9=Charging and Critical。
                // 2 = "接着交流电所以不在放电"，满电插着电源显示"充电中"是错的
                //（主工程 DeviceService.IsChargingStatus 已修，这里是与它对齐的副本 —— 2026-11-23 复审 F-2）。
                _battCharging = Convert.ToInt32(o["BatteryStatus"]) is 6 or 7 or 8 or 9;
                return;
            }
            _battPct = null;   // 无电池
        }
        catch { /* 保留上次 */ }
    }

    public void Start() => _inner.Start();
    public void Dispose() { _inner.Dispose(); _thermal.Dispose(); }
}

/// <summary>
/// macOS 采样器：
/// - CPU：mach host_statistics(HOST_CPU_LOAD_INFO) 差分（32 位 ticks；与 GetSystemTimes 同原理）
/// - 内存：host_statistics64(HOST_VM_INFO64) + sysctlbyname("hw.memsize")
/// - 网速：.NET NetworkInterface（跨平台，与 Windows 版同口径）
/// - 磁盘：statfs(Data 卷)，无子进程
/// GPU/温度：Apple Silicon 需要 IOReport/SMC（受限），GPU v0.1 给 0/"--"；温度走 IOHID 探针。
/// </summary>
public sealed class MacSampler : ISampler
{
    private readonly System.Timers.Timer _timer;
    private long _lastCpuUser, _lastCpuSystem, _lastCpuIdle, _lastCpuNice;
    private bool _cpuInit;
    private long _lastIn, _lastOut;
    private DateTime _lastNetTime;
    private double _smoothDown, _smoothUp;
    private bool _netInit;

    public event Action<MonitorSnapshot>? Snapshot;

    public MacSampler(int intervalMs)
    {
        _timer = new System.Timers.Timer(intervalMs);
        _timer.Elapsed += (_, _) =>
        {
            try { Sample(); }
            catch (Exception ex)
            {
                // 采样器整体异常：保留上一帧，但必须留日志——之前这里静默吞掉，采样崩了 UI 只会"卡住"看不出原因
                Console.Error.WriteLine($"[sampler] {ex.GetType().Name}: {ex.Message}");
            }
        };
    }

    public void Start() { _timer.Start(); Sample(); }
    public void Dispose() { _timer.Stop(); _timer.Dispose(); }

    private SamplerDemand _demand = SamplerDemand.Collapsed;
    public void SetDemand(SamplerDemand demand) => _demand = demand;

    private void Sample()
    {
        SamplerDemand d = _demand;

        Reading<double> cpu = d.HasFlag(SamplerDemand.Cpu) ? SampleCpu() : Reading<double>.NotRequested;
        Reading<double> memPct = Reading<double>.NotRequested; double memUsed = 0, memTotal = 0;
        if (d.HasFlag(SamplerDemand.Memory)) (memPct, memUsed, memTotal) = SampleMemory();
        Reading<double> down = Reading<double>.NotRequested, up = Reading<double>.NotRequested;
        if (d.HasFlag(SamplerDemand.Network)) (down, up) = ReadNetwork();
        Reading<double> batt = d.HasFlag(SamplerDemand.Battery) ? SampleBattery() : Reading<double>.NotRequested;
        if (d.HasFlag(SamplerDemand.Processes)) SampleTopProcesses();
        var (gpu, gpuMem) = d.HasFlag(SamplerDemand.Gpu) ? SampleGpuIoreg() : (0, (double?)null);
        double freq = d.HasFlag(SamplerDemand.CpuFrequency) ? SampleCpuFreq() : 0;
        var temps = d.HasFlag(SamplerDemand.Temperatures) ? SampleTemps() : Temps.NotRequested;
        Reading<double> disk = d.HasFlag(SamplerDemand.Disk) ? SampleDisk() : Reading<double>.NotRequested;

        Snapshot?.Invoke(new MonitorSnapshot
        {
            CpuUsage = cpu.GetValueOrDefault(0),
            CpuFrequencyGHz = freq,
            MemoryUsagePercent = memPct.GetValueOrDefault(0),
            MemoryUsedGB = memUsed,
            MemoryTotalGB = memTotal,
            DiskUsagePercent = disk.GetValueOrDefault(0),
            GpuUsage = gpu,
            GpuName = gpu > 0 || _gpuMemGB is not null ? "Apple GPU" : "",
            GpuMemoryUsedGB = gpuMem,
            CpuTemp = temps.Cpu.TryGet(out var ct) ? ct : null,
            GpuTemp = temps.Gpu.TryGet(out var gt) ? gt : null,
            SsdTemp = temps.Ssd.TryGet(out var stt) ? stt : null,
            ThermalState = SampleThermalState(),
            BatteryPercent = batt.TryGet(out var bp) ? bp : null,
            BatteryCharging = _battCharging,
            TopProcesses = d.HasFlag(SamplerDemand.Processes) ? _topProcesses : null,
        TopProcessesByMem = d.HasFlag(SamplerDemand.Processes) ? _topProcessesByMem : null,
            DownloadSpeed = down.GetValueOrDefault(0),
            UploadSpeed = up.GetValueOrDefault(0),
            // 运行时长：与 Windows 端（Island.Native.SystemMonitor）**同一口径、同一实现** ——
            // Environment.TickCount64（系统开机时长）。mac 之前没填 ⇒ 底部状态行取不到值。
            // 注意：Windows 侧 SystemMonitor 里那段 PDH "System Up Time" 是**死代码**（从未被调用），
            // 两端都以这一行为准。
            UptimeHours = Environment.TickCount64 / 3600000.0,
            Status = new ReadingStatus
            {
                Cpu = cpu, Memory = memPct, Disk = disk, NetDown = down, NetUp = up,
                CpuTemp = temps.Cpu, GpuTemp = temps.Gpu, SsdTemp = temps.Ssd, Battery = batt,
                Gpu = d.HasFlag(SamplerDemand.Gpu) ? Reading<double>.Of(gpu) : Reading<double>.NotRequested,
            },
        });
    }

    private readonly record struct Temps(Reading<double> Cpu, Reading<double> Gpu, Reading<double> Ssd)
    {
        public static readonly Temps NotRequested = new(Reading<double>.NotRequested, Reading<double>.NotRequested, Reading<double>.NotRequested);
    }

    // ---------- 温度数值（IOHID 温度传感器；随包 island-thermal 探针，无 root） ----------
    // macOS 27.2 实测：SMC/IOReport/PMU UserClient 全部关闭，但 IOHID 温度传感器通道可用
    // （macmon/Stats 同款）。探针走 IOHIDServiceClientCopyEvent，输出 "CPU=x GPU=x SSD=x"。
    // 3 秒一采（子进程约 80ms）。传感器名映射见 tools/thermal-probe/thermal.m。
    private Temps _temps = Temps.NotRequested;
    private DateTime _tempChecked = DateTime.MinValue;
    private string? _thermalProbePath;
    private string _tempError = "";

    private Temps SampleTemps()
    {
        if ((DateTime.Now - _tempChecked).TotalSeconds < 3) return _temps;
        try
        {
            if (_thermalProbePath is null)
            {
                _thermalProbePath = LocateThermalProbe();
                if (_thermalProbePath is null)
                {
                    _tempError = "温度探针缺失（island-thermal）";
                    _tempChecked = DateTime.Now;
                    return _temps = UnavailableTemps(_tempError);
                }
            }
            var psi = new System.Diagnostics.ProcessStartInfo(_thermalProbePath)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) { _tempChecked = DateTime.Now; return _temps = UnavailableTemps("探针无法启动"); }
            // stderr 必须异步排空：探针若向 stderr 写超过管道缓冲(~4KB)会阻塞，导致永远超时。
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            bool exited = p.WaitForExit(1500);
            if (!exited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.WaitForExit(500);   // Kill 后必须二次等待：回收进程 + 关闭 stdout 管道
            }
            string line = outTask.Wait(500) ? outTask.Result : "";
            errTask.Wait(100);        // 排空 stderr，避免管道残留；内容不用于解析
            // 解析 "CPU=48.4 GPU=60.1 SSD=32.0"
            double? cpu = ParseTempField(line, "CPU=");
            double? gpu = ParseTempField(line, "GPU=");
            double? ssd = ParseTempField(line, "SSD=");
            if (cpu is not null || gpu is not null || ssd is not null)
            {
                _temps = new Temps(
                    cpu is { } c ? Reading<double>.Of(c) : Reading<double>.Unavailable("传感器无 CPU 读数"),
                    gpu is { } g ? Reading<double>.Of(g) : Reading<double>.Unavailable("传感器无 GPU 读数"),
                    ssd is { } s ? Reading<double>.Of(s) : Reading<double>.Unavailable("传感器无 SSD 读数"));
                _tempChecked = DateTime.Now;   // 只在成功时更新时间戳，失败后 3 秒内可重试
            }
            else
            {
                string err = errTask.IsCompleted ? errTask.Result.Trim() : "";
                _tempError = string.IsNullOrEmpty(line) ? (string.IsNullOrEmpty(err) ? "探针无输出" : err.Split('\n')[0]) : "输出无法解析";
                _temps = UnavailableTemps(_tempError);
            }
        }
        catch (Exception ex) { _tempError = ex.Message; _temps = UnavailableTemps(ex.Message); }
        return _temps;
    }

    private static Temps UnavailableTemps(string why) => new(
        Reading<double>.Unavailable(why), Reading<double>.Unavailable(why), Reading<double>.Unavailable(why));

    private static double? ParseTempField(string line, string key)
    {
        int i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        i += key.Length;
        int end = i;
        while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '.' || line[end] == '-')) end++;
        return end > i && double.TryParse(line.AsSpan(i, end - i),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out double v) ? v : null;
    }

    private static string? LocateThermalProbe()
    {
        // 探针随 publish 输出到应用目录（与 Island.Avalonia 可执行文件同级）
        string? baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            string candidate = Path.Combine(baseDir, "island-thermal");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ---------- 热状态档位（NSProcessInfo.thermalState：官方公开 API，无 root） ----------
    // 温度数值在 macOS 无 root 不可读（SMC/IOReport/PMU UserClient/IOHID 全部实测关闭），
    // 但热压力档位是 Apple 官方暴露的降频信号 —— 对"该不该降负载"比绝对温度更直接。
    // 通过 Objective-C runtime 直接调 [[NSProcessInfo processInfo] thermalState]：
    // 无子进程、无 root、无私有 API。实测（macOS 27.2）返回 0=Nominal。
    // 注意 objc_msgSend 是可变参数函数，必须按返回类型分别声明 P/Invoke 签名。
    private string _thermalState = "";
    private DateTime _thermalChecked = DateTime.MinValue;

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr ObjcGetClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr SelRegisterName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjcMsgPtr(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern long ObjcMsgLong(IntPtr receiver, IntPtr selector);

    private string SampleThermalState()
    {
        if ((DateTime.Now - _thermalChecked).TotalSeconds < 15) return _thermalState;
        _thermalChecked = DateTime.Now;
        try
        {
            var cls = ObjcGetClass("NSProcessInfo");
            if (cls == IntPtr.Zero) return _thermalState;
            var procInfo = ObjcMsgPtr(cls, SelRegisterName("processInfo"));
            if (procInfo == IntPtr.Zero) return _thermalState;
            long state = ObjcMsgLong(procInfo, SelRegisterName("thermalState"));
            _thermalState = state switch
            {
                0 => "正常", 1 => "偏高", 2 => "较高", 3 => "过高",
                _ => "",
            };
        }
        catch { /* 非 macOS 或 runtime 不可用：保持空串，UI 显示 "--" */ }
        return _thermalState;
    }

    // ---------- CPU 频率：sysctlbyname("hw.cpufrequency")（无子进程；Apple Silicon 常读不到→隐藏） ----------
    private double _cpuFreqGHz;
    private DateTime _freqChecked = DateTime.MinValue;

    [DllImport("libSystem.dylib")]
    private static extern int SysctlByName(string name, IntPtr oldp, ref nuint oldlenp, IntPtr newp, nuint newlen);

    private double SampleCpuFreq()
    {
        if ((DateTime.Now - _freqChecked).TotalSeconds < 10) return _cpuFreqGHz;
        _freqChecked = DateTime.Now;
        try
        {
            IntPtr buf = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt64(buf, 0);
                nuint len = 8;
                if (SysctlByName("hw.cpufrequency", buf, ref len, IntPtr.Zero, 0) == 0)
                {
                    long hz = Marshal.ReadInt64(buf);
                    if (hz > 1_000_000_000L) _cpuFreqGHz = hz / 1e9;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { /* 读不到就保持 0，画布自动隐藏频率位 */ }
        return _cpuFreqGHz;
    }

    // ---------- GPU：ioreg AGXAccelerator PerformanceStatistics（无 root 官方通道） ----------
    // 实测（2026-11-23, macOS 27.2 Apple Silicon）：ioreg -r -c AGXAccelerator 的
    // PerformanceStatistics 含 "Device Utilization %"（=任务管理器 GPU%）、
    // "Alloc system memory"/"In use system memory"（字节）。
    // 每 2 秒读一次（起 ioreg 子进程约 80-150ms，不值得每秒）。
    private DateTime _gpuChecked = DateTime.MinValue;
    private double _gpuUsage;
    private double? _gpuMemGB;

    private (double usage, double? memGB) SampleGpuIoreg()
    {
        if ((DateTime.Now - _gpuChecked).TotalSeconds < 2) return (_gpuUsage, _gpuMemGB);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ioreg", "-r -c AGXAccelerator -l")
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return (_gpuUsage, _gpuMemGB);
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();   // 排空 stderr，防管道阻塞
            bool exited = p.WaitForExit(2000);
            if (!exited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.WaitForExit(500);
            }
            string output = outTask.Wait(1000) ? outTask.Result : "";
            errTask.Wait(100);
            // 只解析关心的三个键；解析失败时保留上次值，避免超时把 GPU% 清零
            // （否则会出现"GPU 0% 但仍有显存占用"的不一致状态）
            long? util = FindIoregNumber(output, "Device Utilization %");
            long? alloc = FindIoregNumber(output, "Alloc system memory");
            long? inUse = FindIoregNumber(output, "In use system memory");
            bool any = util is not null || alloc is not null || inUse is not null;
            if (any)
            {
                if (util is { } u) _gpuUsage = u;
                if (inUse is { } used) _gpuMemGB = used / 1073741824.0;   // 主值
                else if (alloc is { } a) _gpuMemGB = a / 1073741824.0;    // 上限兜底
                _gpuChecked = DateTime.Now;   // 只在成功时更新时间戳
            }
        }
        catch { /* ioreg 不可用（非 Apple Silicon / 权限变化）：保留上次 */ }
        return (_gpuUsage, _gpuMemGB);
    }

    /// <summary>从 ioreg 的 PerformanceStatistics 文本里取 "key"=value（取第一次出现）。</summary>
    private static long? FindIoregNumber(string hay, string key)
    {
        int i = hay.IndexOf($"\"{key}\"=", StringComparison.Ordinal);
        if (i < 0) return null;
        int start = i + key.Length + 3;
        int end = start;
        while (end < hay.Length && (char.IsDigit(hay[end]))) end++;
        return end > start && long.TryParse(hay[start..end], out long v) ? v : null;
    }

    // ---------- 高占用进程（TotalProcessorTime 差分，跨平台 .NET API，3 秒一采） ----------
    private DateTime _procChecked = DateTime.MinValue;
    // 进程身份：PID 会被系统复用，差分时必须校验"还是不是同一个进程"
    //（主工程 SystemMonitor.IsSameProcess 同款：进程名 + 启动时间 —— 2026-11-23 复审 N-4 对齐）
    private Dictionary<int, (string Name, TimeSpan Cpu, DateTime Start)> _procPrev = new();
    private TopProcess[] _topProcesses = Array.Empty<TopProcess>();

    private TopProcess[] _topProcessesByMem = Array.Empty<TopProcess>();

    private void SampleTopProcesses()
    {
        var now = DateTime.Now;
        if ((now - _procChecked).TotalSeconds < 3) return;
        bool first = _procChecked == DateTime.MinValue;
        double wall = first ? 0 : (now - _procChecked).TotalSeconds;
        _procChecked = now;

        var prev = _procPrev;
        var next = new Dictionary<int, (string, TimeSpan, DateTime)>(256);
        var tops = new List<TopProcess>(64);
        var byMem = new List<TopProcess>(256);
        int coresN = Math.Max(1, Environment.ProcessorCount);

        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            // Process 持有进程句柄，不释放会攒到 GC 终结器（主工程同款）
            using var proc = p;
            try
            {
                string name = proc.ProcessName;
                if (name is "Idle" or "System" or "kernel_task" or "launchd") continue;
                var cpu = proc.TotalProcessorTime;
                DateTime start = DateTime.MinValue;
                try { start = proc.StartTime; } catch { /* 无权限：退化为只按进程名判身份 */ }
                long ws = 0;
                try { ws = proc.WorkingSet64; } catch { }
                next[proc.Id] = (name, cpu, start);
                string exe = "";
                double pct = 0;
                if (wall > 0.5 && prev.TryGetValue(proc.Id, out var old) && IsSameProcess(old, name, start))
                {
                    pct = (cpu - old.Cpu).TotalSeconds / wall / coresN * 100;
                    // 门槛从 0.05 降到 0.01：18 核机器上单进程占比被除以核数后很小，
                    // 0.05 的门槛会让榜几乎永远是空的（实测最高的也只有 0.06%）。
                    if (pct >= 0.01)
                    {
                        // 可执行路径：应用榜取图标用（NSWorkspace icon(forFile:)）。无权限时为空，榜里显示首字母兜底
                        exe = "";
                        try { exe = proc.MainModule?.FileName ?? ""; } catch { }
                        tops.Add(new TopProcess { Name = name, CpuPercent = Math.Min(pct, 100), MemoryMB = ws / 1048576.0, ExePath = exe });
                    }
                }
                // 内存榜：所有进程都参与（内存占用与 CPU 忙闲无关）
                if (ws > 0) byMem.Add(new TopProcess { Name = name, CpuPercent = Math.Min(pct, 100), MemoryMB = ws / 1048576.0, ExePath = exe });
            }
            catch { /* 进程已退出/无权限 */ }
            finally { proc.Dispose(); }
        }
        _procPrev = next;
        // 本轮为空也要赋空数组：否则界面会一直显示已经退出的进程和旧数值（主工程同款修复）
        // 同名进程（同一个 App 的多个实例）按可执行路径去重，只留占用最高的那条——
        // 否则应用榜会出现三个 "WorkBuddy Helper"（BrimDeck 同样按进程聚合）。
        static TopProcess[] Dedup(IEnumerable<TopProcess> src, Func<TopProcess, double> key)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<TopProcess>();
            foreach (var t in src)
            {
                // 按**进程名**去重：同一个 App 的多实例可能路径不同（如 Helper 塞在
                // 不同版本的 .app 里），按路径去重会留下好几条同名条目。
                string id = t.Name;
                if (seen.Add(id)) list.Add(t);
            }
            return list.OrderByDescending(key).Take(5).ToArray();
        }
        _topProcesses = tops.Count > 0
            ? Dedup(tops, t => t.CpuPercent)
            : Array.Empty<TopProcess>();
        _topProcessesByMem = byMem.Count > 0
            ? Dedup(byMem, t => t.MemoryMB)
            : Array.Empty<TopProcess>();
    }

    /// <summary>
    /// PID 会被系统复用：同一个 PID 前后可能是两个毫不相干的进程，
    /// 不校验身份就做差分会把"新进程的累计 CPU"当成"本轮增量"算出假峰值。
    /// 身份 = 进程名 + 启动时间（取不到启动时间时退化为只看进程名）。
    /// </summary>
    private static bool IsSameProcess((string Name, TimeSpan Cpu, DateTime Start) old, string name, DateTime start)
    {
        if (!old.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return false;
        if (old.Start == DateTime.MinValue || start == DateTime.MinValue) return true;
        return old.Start == start;
    }

    // ---------- 电池：IOKit IOPSCopyPowerSourcesInfo（30 秒一采；台式 Mac 无电池给 null） ----------
    private DateTime _battChecked = DateTime.MinValue;
    private Reading<double> _batt = Reading<double>.NotRequested;
    private bool _battCharging;

    private Reading<double> SampleBattery()
    {
        if ((DateTime.Now - _battChecked).TotalSeconds >= 30)
        {
            _battChecked = DateTime.Now;
            try
            {
                var (pct, charging) = ReadBatteryIOKit();
                _battCharging = charging;
                // null = 这台机器没有内置电池（台式 Mac），不是错误
                _batt = pct is { } p ? Reading<double>.Of(p) : Reading<double>.Unavailable("无内置电池");
            }
            catch (Exception ex) { _batt = Reading<double>.Unavailable(ex.Message); }
        }
        return _batt;
    }

    private static (double? pct, bool charging) ReadBatteryIOKit()
    {
        IntPtr blob = IOPSCopyPowerSourcesInfo();
        if (blob == IntPtr.Zero) return (null, false);
        IntPtr list = IntPtr.Zero;
        try
        {
            list = IOPSCopyPowerSourcesList(blob);
            int n = CFArrayGetCount(list);
            for (int i = 0; i < n; i++)
            {
                IntPtr ps = CFArrayGetValueAtIndex(list, i);
                IntPtr desc = IOPSGetPowerSourceDescription(blob, ps);
                if (desc == IntPtr.Zero) continue;
                if (CfString(CfDictGet(desc, "Type")) != "InternalBattery") continue;
                long cur = CfNumber(CfDictGet(desc, "Current Capacity"));
                long max = CfNumber(CfDictGet(desc, "Max Capacity"));
                bool charging = CfBool(CfDictGet(desc, "Is Charging"));
                if (max <= 0) return (null, false);
                return (Math.Clamp(cur * 100.0 / max, 0, 100), charging);
            }
            return (null, false);   // 无内置电池
        }
        finally
        {
            if (list != IntPtr.Zero) CFRelease(list);
            CFRelease(blob);
        }
    }

    private static IntPtr CfDictGet(IntPtr dict, string key)
    {
        IntPtr k = CFStringCreateWithCString(IntPtr.Zero, key, 0x08000100);
        try { return dict == IntPtr.Zero ? IntPtr.Zero : CFDictionaryGetValue(dict, k); }
        finally { if (k != IntPtr.Zero) CFRelease(k); }
    }

    private static long CfNumber(IntPtr num)
        => num != IntPtr.Zero && CFNumberGetValue(num, 4 /*kCFNumberSInt64Type*/, out long v) ? v : 0;

    private static bool CfBool(IntPtr b) => b != IntPtr.Zero && CFBooleanGetValue(b);

    private static string? CfString(IntPtr s)
    {
        if (s == IntPtr.Zero) return null;
        var buf = new byte[256];
        return CFStringGetCString(s, buf, buf.Length, 0x08000100)
            ? System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0') : null;
    }

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern IntPtr IOPSCopyPowerSourcesInfo();
    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern IntPtr IOPSCopyPowerSourcesList(IntPtr blob);
    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern IntPtr IOPSGetPowerSourceDescription(IntPtr blob, IntPtr ps);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern int CFArrayGetCount(IntPtr array);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, int idx);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, uint encoding);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern bool CFStringGetCString(IntPtr str, byte[] buffer, long bufferSize, uint encoding);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern bool CFNumberGetValue(IntPtr number, int theType, out long valuePtr);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern bool CFBooleanGetValue(IntPtr boolean);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    // ---------- CPU：host_statistics(HOST_CPU_LOAD_INFO=3)，4×u32 ----------
    private Reading<double> SampleCpu()
    {
        // HOST_CPU_LOAD_INFO(3) 返回 host_cpu_load_info = 4 × integer_t（32 位），共 16 字节。
        // 必须用 host_statistics（非 64）+ 按 32 位读。
        // 旧实现用 host_statistics64 + ReadInt64：把 (user,system) 拼成一个 64 位数、
        // (idle,nice) 拼成另一个，而 idle/nice 读到的是未初始化堆内存（恒 0）→ Δidle=0 → CPU 恒 100%。
        var buf = Marshal.AllocHGlobal(16);
        try
        {
            int count = 4;   // HOST_CPU_LOAD_INFO_COUNT
            int kr = host_statistics(mach_host_self(), 3, buf, ref count);
            if (kr != 0) return Reading<double>.Unavailable($"host_statistics 失败（kr={kr}）");
            long user = (uint)Marshal.ReadInt32(buf, 0);
            long system = (uint)Marshal.ReadInt32(buf, 4);
            long idle = (uint)Marshal.ReadInt32(buf, 8);
            long nice = (uint)Marshal.ReadInt32(buf, 12);
            if (!_cpuInit)
            {
                _lastCpuUser = user; _lastCpuSystem = system; _lastCpuIdle = idle; _lastCpuNice = nice;
                _cpuInit = true;
                return Reading<double>.Unavailable("首个采样周期");
            }
            long dUser = user - _lastCpuUser, dSys = system - _lastCpuSystem,
                 dIdle = idle - _lastCpuIdle, dNice = nice - _lastCpuNice;
            _lastCpuUser = user; _lastCpuSystem = system; _lastCpuIdle = idle; _lastCpuNice = nice;
            long total = dUser + dSys + dIdle + dNice;
            if (total <= 0) return Reading<double>.Unavailable("计数器无变化");
            return Reading<double>.Of(Math.Clamp((total - dIdle) * 100.0 / total, 0, 100));
        }
        catch (Exception ex) { return Reading<double>.Unavailable(ex.Message); }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---------- 内存：HOST_VM_INFO64=4（vm_statistics64_data_t） ----------
    // 布局要点（vm_statistics64_data_t，XNU osfmk/mach/vm_statistics.h）：
    //   开头 4 个是 32 位 natural_t（free/active/inactive/wire），
    //   之后是 64 位计数器，但中间还夹着 6 个 32 位 natural_t
    //   （purgeable/speculative/compressor/throttled/external/internal）——
    //   整个结构体 160 字节，不能假设"第 5 个起就全是 64 位"。
    //   本函数只读 active@4 与 wire@12（这两个偏移是对的）。
    //
    // ⚠️ 2026-11-23 修复：原来只给了 8*15=120 字节缓冲、count=15。
    //   host_statistics64 的 count 以 4 字节为单位，最少要覆盖 160 字节的结构体；
    //   count 不足时内核直接返回 KERN_INVALID_ARGUMENT ⇒ Mac 上内存面板永远显示 0%。
    //   现在缓冲 256 字节、count=64（=256/4），两边都够。
    private (Reading<double> pct, double used, double total) SampleMemory()
    {
        var buf = Marshal.AllocHGlobal(256);
        try
        {
            int count = 64;   // 256 / 4：足够覆盖 160 字节的 vm_statistics64_data_t
            int kr = host_statistics64(mach_host_self(), 4, buf, ref count);
            if (kr != 0) return (Reading<double>.Unavailable($"host_statistics64 失败（kr={kr}）"), 0, 0);
            if (host_page_size(mach_host_self(), out uint pageSize) != 0 || pageSize == 0) pageSize = 16384;
            // natural_t 是 32 位无符号整数：free@0 / active@4 / inactive@8 / wire@12
            ulong active = (uint)Marshal.ReadInt32(buf, 4);
            ulong wire = (uint)Marshal.ReadInt32(buf, 12);
            // 近似口径：已用 ≈ 活跃页 + 联动页（未含压缩页，与活动监视器口径略有差异）
            double usedGb = (active + wire) * (double)pageSize / 1073741824.0;

            ulong memsize = 0;
            try
            {
                IntPtr len = (IntPtr)8;
                if (sysctlbyname("hw.memsize", out memsize, ref len, IntPtr.Zero, (IntPtr)0) != 0) memsize = 0;
            }
            catch { /* sysctl 缺失：总量按 0 处理 */ }
            double totalGb = memsize / 1073741824.0;
            if (totalGb <= 0) return (Reading<double>.Unavailable("hw.memsize 读取失败"), usedGb, 0);
            double pct = Math.Clamp(usedGb / totalGb * 100, 0, 100);
            return (Reading<double>.Of(pct), usedGb, totalGb);
        }
        catch (Exception ex) { return (Reading<double>.Unavailable(ex.Message), 0, 0); }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---------- 网速：.NET NetworkInterface 差分（与 Windows 版同口径） ----------
    private (Reading<double> down, Reading<double> up) ReadNetwork()
    {
        long inBytes = 0, outBytes = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                inBytes += stats.BytesReceived;
                outBytes += stats.BytesSent;
            }
        }
        catch (Exception ex) { return (Reading<double>.Unavailable(ex.Message), Reading<double>.Unavailable(ex.Message)); }

        var now = DateTime.Now;
        if (!_netInit) { _lastIn = inBytes; _lastOut = outBytes; _lastNetTime = now; _netInit = true; return (Reading<double>.Unavailable("首个采样周期"), Reading<double>.Unavailable("首个采样周期")); }
        double sec = (now - _lastNetTime).TotalSeconds;
        if (sec <= 0) return (Reading<double>.Unavailable("时间间隔无效"), Reading<double>.Unavailable("时间间隔无效"));
        if (inBytes < _lastIn || outBytes < _lastOut)
        { _lastIn = inBytes; _lastOut = outBytes; _lastNetTime = now; return (Reading<double>.Unavailable("计数器重置"), Reading<double>.Unavailable("计数器重置")); }

        double rawDown = (inBytes - _lastIn) / sec, rawUp = (outBytes - _lastOut) / sec;
        _lastIn = inBytes; _lastOut = outBytes; _lastNetTime = now;
        _smoothDown = _smoothDown * 0.6 + rawDown * 0.4;
        _smoothUp = _smoothUp * 0.6 + rawUp * 0.4;
        return (Reading<double>.Of(_smoothDown), Reading<double>.Of(_smoothUp));
    }

    // ---------- libSystem P/Invoke ----------
    [DllImport("libSystem.dylib")] private static extern IntPtr mach_host_self();
    [DllImport("libSystem.dylib")] private static extern int host_statistics64(IntPtr host, int flavor, IntPtr info, ref int count);
    [DllImport("libSystem.dylib")] private static extern int host_statistics(IntPtr host, int flavor, IntPtr info, ref int count);

    // ---------- 磁盘：statfs("/")（无子进程；Data 卷与 / 同一 APFS 容器，用量一致） ----------
    // 旧版 Mac 采样器从未给 DiskUsagePercent 赋值 → 面板"磁盘"恒 0/--。
    // 用 byte buffer + 显式偏移读，避免手写 struct 对齐出错。macOS x86_64/arm64 的 struct statfs 布局：
    //   0  f_bsize(u32) 4 f_iosize(i32) 8 f_blocks(u64) 16 f_bfree(u64) 24 f_bavail(u64) …
    [DllImport("libSystem.dylib", EntryPoint = "statfs", SetLastError = true)]
    private static extern int statfs(string path, byte[] buf);

    private DateTime _diskChecked = DateTime.MinValue;
    private Reading<double> _disk = Reading<double>.NotRequested;

    /// <summary>根卷用量百分比；30 秒一采（磁盘用量变化慢）。失败保留上次读数并带原因。</summary>
    private Reading<double> SampleDisk()
    {
        if ((DateTime.Now - _diskChecked).TotalSeconds < 30) return _disk;
        _diskChecked = DateTime.Now;
        try
        {
            var buf = new byte[4096];   // struct statfs 在 arm64 为 ~2168 字节，留足
            int r = statfs("/System/Volumes/Data", buf);
            if (r != 0) r = statfs("/", buf);
            if (r != 0) { _disk = Reading<double>.Unavailable($"statfs 失败（errno={Marshal.GetLastWin32Error()}）"); return _disk; }
            ulong blocks = BitConverter.ToUInt64(buf, 8);
            ulong bfree = BitConverter.ToUInt64(buf, 16);
            if (blocks == 0) { _disk = Reading<double>.Unavailable("statfs 返回 0 块"); return _disk; }
            ulong used = blocks - bfree;
            _disk = Reading<double>.Of(Math.Clamp(used * 100.0 / blocks, 0, 100));
        }
        catch (Exception ex) { _disk = Reading<double>.Unavailable(ex.Message); }
        return _disk;
    }
    [DllImport("libSystem.dylib")] private static extern int host_page_size(IntPtr host, out uint pageSize);
    [DllImport("libSystem.dylib")] private static extern int sysctlbyname(string name, out ulong oldp, ref IntPtr oldlenp, IntPtr newp, IntPtr newlen);
}
