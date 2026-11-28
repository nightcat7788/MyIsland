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
        OperatingSystem.IsMacOS() ? new MacSampler(intervalMs)
        : OperatingSystem.IsWindows() ? new WinSampler(intervalMs)
        : new NullSampler();
}

internal sealed class NullSampler : ISampler
{
    public event Action<MonitorSnapshot>? Snapshot;
    public void Start() => Snapshot?.Invoke(new MonitorSnapshot { Status = ReadingStatus.Empty });
    public void Dispose() { }
}

/// <summary>Windows：直接复用 Island.Native.SystemMonitor（PDH 全套）+ WMI 电池 + LHM 温度。</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WinSampler : ISampler
{
    private readonly Island.Native.SystemMonitor _inner;
    private readonly Island.App.Services.ThermalService _thermal = new();
    public event Action<MonitorSnapshot>? Snapshot;

    private DateTime _battChecked = DateTime.MinValue;
    private double? _battPct;
    private bool _battCharging;
    private SamplerDemand _demand = SamplerDemand.Collapsed;

    public WinSampler(int intervalMs)
    {
        _inner = new Island.Native.SystemMonitor(intervalMs);
        _thermal.SetWanted(true);
        _inner.Snapshot += s =>
        {
            if (_demand.HasFlag(SamplerDemand.Battery)) SampleBattery();
            var th = _thermal.Current;
            var battReading = !_demand.HasFlag(SamplerDemand.Battery)
                ? Reading<double>.NotRequested
                : _battPct is { } bp ? Reading<double>.Of(bp) : Reading<double>.Unavailable("无内置电池");
            var wantTemp = _demand.HasFlag(SamplerDemand.Temperatures);
            var st = s.Status with
            {
                Battery = battReading,
                CpuTemp = !wantTemp ? Reading<double>.NotRequested : th?.CpuTemp is { } ct ? Reading<double>.Of(ct) : Reading<double>.Unavailable("无传感器读数"),
                GpuTemp = !wantTemp ? Reading<double>.NotRequested : th?.GpuTemp is { } gt ? Reading<double>.Of(gt) : Reading<double>.Unavailable("无传感器读数"),
                SsdTemp = !wantTemp ? Reading<double>.NotRequested : th?.SsdTemp is { } stt ? Reading<double>.Of(stt) : Reading<double>.Unavailable("无传感器读数"),
            };
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
                Status = st,
            });
        };
    }

    public void SetDemand(SamplerDemand demand)
    {
        _demand = demand;
        _inner.SetDemand(demand);
        _thermal.SetWanted(demand.HasFlag(SamplerDemand.Temperatures));
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
            using var results = q.Get();
            foreach (System.Management.ManagementBaseObject o in results)
            {
                using (o)
                {
                    _battPct = Convert.ToDouble(o["EstimatedChargeRemaining"]);
                    _battCharging = Convert.ToInt32(o["BatteryStatus"]) is 6 or 7 or 8 or 9;
                    return;
                }
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
/// GPU/温度：Apple Silicon 需要 IOReport/SMC（受限），GPU 走 ioreg AGXAccelerator；温度走 IOHID + SMC 探针。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class MacSampler : ISampler
{
    private readonly System.Timers.Timer _timer;
    private uint _lastCpuUser, _lastCpuSystem, _lastCpuIdle, _lastCpuNice;
    private bool _cpuInit;
    private long _lastIn, _lastOut;
    private DateTime _lastNetTime;
    private double _smoothDown, _smoothUp;
    private bool _netInit;

    public event Action<MonitorSnapshot>? Snapshot;

    private readonly SamplerReentrancyGuard _sampleGuard = new();
    private volatile bool _disposed;

    public MacSampler(int intervalMs)
    {
        _timer = new System.Timers.Timer(intervalMs);
        _timer.Elapsed += (_, _) => Sample();
    }

    public void Start()
    {
        if (_disposed) return;
        _timer.Start();
        // 首帧也离开 UI 线程；探针超时可能超过一秒，不能阻塞窗口启动。
        _ = Task.Run(Sample);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }

    private SamplerDemand _demand = SamplerDemand.Collapsed;
    public void SetDemand(SamplerDemand demand) => _demand = demand;

    private void Sample()
    {
        if (_disposed || !_sampleGuard.TryEnter()) return;
        try { SampleCore(); }
        catch (Exception ex)
        {
            // 单次失败保留下一轮，并留下可诊断日志。
            Console.Error.WriteLine($"[sampler] {ex.GetType().Name}: {ex.Message}");
        }
        finally { _sampleGuard.Exit(); }
    }

    private void SampleCore()
    {
        if (_disposed) return;
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

        if (!_disposed) Snapshot?.Invoke(new MonitorSnapshot
        {
            CpuUsage = cpu.GetValueOrDefault(0),
            CpuFrequencyGHz = freq,
            MemoryUsagePercent = memPct.GetValueOrDefault(0),
            MemoryUsedGB = memUsed,
            MemoryTotalGB = memTotal,
            DiskUsagePercent = disk.GetValueOrDefault(0),
            DriveUsedGB = _driveUsedGB,
            DriveTotalGB = _driveTotalGB,
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
        // 成功、超时和异常都按同一周期节流；失败不能每秒重启一个慢探针。
        _tempChecked = DateTime.Now;
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

    [DllImport("libSystem.dylib", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName([MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr oldp, ref nuint oldlenp, IntPtr newp, nuint newlen);

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
    private DateTime _gpuChecked = DateTime.MinValue;
    private double _gpuUsage;
    private double? _gpuMemGB;

    private (double usage, double? memGB) SampleGpuIoreg()
    {
        if ((DateTime.Now - _gpuChecked).TotalSeconds < 2) return (_gpuUsage, _gpuMemGB);
        // 无论成功或失败都节流 2 秒，避免非 Apple Silicon 或无读数时每秒拉起 ioreg 子进程
        _gpuChecked = DateTime.Now;
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
            long? util = FindIoregNumber(output, "Device Utilization %");
            long? alloc = FindIoregNumber(output, "Alloc system memory");
            long? inUse = FindIoregNumber(output, "In use system memory");
            bool any = util is not null || alloc is not null || inUse is not null;
            if (any)
            {
                if (util is { } u) _gpuUsage = u;
                if (inUse is { } used) _gpuMemGB = used / 1073741824.0;   // 主值
                else if (alloc is { } a) _gpuMemGB = a / 1073741824.0;    // 上限兜底
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
        var tops = new List<(TopProcess Item, int Pid)>(64);
        var byMem = new List<(TopProcess Item, int Pid)>(256);
        var pidMap = new Dictionary<int, System.Diagnostics.Process>(256);
        int coresN = Math.Max(1, Environment.ProcessorCount);

        try
        {
            foreach (var proc in System.Diagnostics.Process.GetProcesses())
            {
                pidMap[proc.Id] = proc;
                try
                {
                    string name = proc.ProcessName;
                    if (name is "Idle" or "System" or "kernel_task" or "launchd") continue;
                    DateTime start = DateTime.MinValue;
                    try { start = proc.StartTime; } catch { }
                    long ws = 0;
                    try { ws = proc.WorkingSet64; } catch { }
                    double pct = 0;
                    try
                    {
                        var cpu = proc.TotalProcessorTime;
                        next[proc.Id] = (name, cpu, start);
                        if (wall > 0.5 && prev.TryGetValue(proc.Id, out var old) && IsSameProcess(old, name, start))
                        {
                            pct = Math.Clamp((cpu - old.Cpu).TotalSeconds / wall / coresN * 100, 0, 100);
                            if (pct >= 0.01)
                            {
                                tops.Add((new TopProcess { Name = name, CpuPercent = pct, MemoryMB = ws / 1048576.0 }, proc.Id));
                            }
                        }
                    }
                    catch { /* 无权限读 CPU 时间：仍允许参与内存榜 */ }

                    if (ws > 0)
                        byMem.Add((new TopProcess { Name = name, CpuPercent = pct, MemoryMB = ws / 1048576.0 }, proc.Id));
                }
                catch { /* 进程已退出 */ }
            }

            _procPrev = next;

            // 先按指标降序排序再去重（确保同名多进程保留占用最高的那条），最后仅对前 5 名取 ExePath 图标路径
            TopProcess[] DedupWithIcons(IEnumerable<(TopProcess Item, int Pid)> src, Func<TopProcess, double> key)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var winners = new List<TopProcess>(5);
                foreach (var (item, pid) in src.OrderByDescending(x => key(x.Item)))
                {
                    if (!seen.Add(item.Name)) continue;
                    string exe = "";
                    if (pidMap.TryGetValue(pid, out var p))
                    {
                        try { exe = p.MainModule?.FileName ?? ""; } catch { }
                    }
                    winners.Add(exe.Length > 0 ? item with { ExePath = exe } : item);
                    if (winners.Count >= 5) break;
                }
                return winners.ToArray();
            }

            _topProcesses = tops.Count > 0 ? DedupWithIcons(tops, t => t.CpuPercent) : Array.Empty<TopProcess>();
            _topProcessesByMem = byMem.Count > 0 ? DedupWithIcons(byMem, t => t.MemoryMB) : Array.Empty<TopProcess>();
        }
        finally
        {
            foreach (var p in pidMap.Values) p.Dispose();
        }
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
            if (list == IntPtr.Zero) return (null, false);
            nint n = CFArrayGetCount(list);
            for (nint i = 0; i < n; i++)
            {
                IntPtr ps = CFArrayGetValueAtIndex(list, i);
                if (ps == IntPtr.Zero) continue;
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
        try { return dict == IntPtr.Zero || k == IntPtr.Zero ? IntPtr.Zero : CFDictionaryGetValue(dict, k); }
        finally { if (k != IntPtr.Zero) CFRelease(k); }
    }

    private static long CfNumber(IntPtr num)
        => num != IntPtr.Zero && CFNumberGetValue(num, 4 /*kCFNumberSInt64Type*/, out long v) ? v : 0;

    private static bool CfBool(IntPtr b) => b != IntPtr.Zero && CFBooleanGetValue(b);

    private static string? CfString(IntPtr s)
    {
        if (s == IntPtr.Zero) return null;
        var buf = new byte[256];
        if (!CFStringGetCString(s, buf, buf.Length, 0x08000100)) return null;
        int nul = Array.IndexOf(buf, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buf, 0, nul >= 0 ? nul : buf.Length);
    }

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern IntPtr IOPSCopyPowerSourcesInfo();
    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern IntPtr IOPSCopyPowerSourcesList(IntPtr blob);
    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern IntPtr IOPSGetPowerSourceDescription(IntPtr blob, IntPtr ps);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint idx);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr str, byte[] buffer, nint bufferSize, uint encoding);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetValue(IntPtr number, nint theType, out long valuePtr);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFBooleanGetValue(IntPtr boolean);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    // 缓存 mach_host_self() 句柄与页大小：每次调用 mach_host_self() 都会递增内核 Mach port send-right 引用计数，
    // 若不缓存在每秒多次采样下会造成内核端口泄漏。
    private static readonly uint HostPort = mach_host_self();
    private static readonly uint CachedPageSize = InitPageSize();
    private static uint InitPageSize()
    {
        try { return host_page_size(HostPort, out uint ps) == 0 && ps > 0 ? ps : 16384u; }
        catch { return 16384u; }
    }

    // ---------- CPU：host_statistics(HOST_CPU_LOAD_INFO=3)，4×u32 ----------
    private Reading<double> SampleCpu()
    {
        var buf = Marshal.AllocHGlobal(16);
        try
        {
            int count = 4;   // HOST_CPU_LOAD_INFO_COUNT
            int kr = host_statistics(HostPort, 3, buf, ref count);
            if (kr != 0) return Reading<double>.Unavailable($"host_statistics 失败（kr={kr}）");
            uint user = (uint)Marshal.ReadInt32(buf, 0);
            uint system = (uint)Marshal.ReadInt32(buf, 4);
            uint idle = (uint)Marshal.ReadInt32(buf, 8);
            uint nice = (uint)Marshal.ReadInt32(buf, 12);
            if (!_cpuInit)
            {
                _lastCpuUser = user; _lastCpuSystem = system; _lastCpuIdle = idle; _lastCpuNice = nice;
                _cpuInit = true;
                return Reading<double>.Unavailable("首个采样周期");
            }
            // 无符号 32 位自然回绕差分：避免长运行后计数器溢出导致负值
            ulong dUser = unchecked(user - _lastCpuUser);
            ulong dSys = unchecked(system - _lastCpuSystem);
            ulong dIdle = unchecked(idle - _lastCpuIdle);
            ulong dNice = unchecked(nice - _lastCpuNice);
            _lastCpuUser = user; _lastCpuSystem = system; _lastCpuIdle = idle; _lastCpuNice = nice;
            ulong total = dUser + dSys + dIdle + dNice;
            if (total == 0) return Reading<double>.Unavailable("计数器无变化");
            return Reading<double>.Of(Math.Clamp((total - dIdle) * 100.0 / total, 0, 100));
        }
        catch (Exception ex) { return Reading<double>.Unavailable(ex.Message); }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ---------- 内存：HOST_VM_INFO64=4（vm_statistics64_data_t） ----------
    private (Reading<double> pct, double used, double total) SampleMemory()
    {
        var buf = Marshal.AllocHGlobal(256);
        try
        {
            int count = 64;   // 256 / 4：足够覆盖 160 字节的 vm_statistics64_data_t
            int kr = host_statistics64(HostPort, 4, buf, ref count);
            if (kr != 0) return (Reading<double>.Unavailable($"host_statistics64 失败（kr={kr}）"), 0, 0);
            uint pageSize = CachedPageSize;
            ulong active = (uint)Marshal.ReadInt32(buf, 4);
            ulong wire = (uint)Marshal.ReadInt32(buf, 12);
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
    [DllImport("libSystem.dylib")] private static extern uint mach_host_self();
    [DllImport("libSystem.dylib")] private static extern int host_statistics64(uint host, int flavor, IntPtr info, ref int count);
    [DllImport("libSystem.dylib")] private static extern int host_statistics(uint host, int flavor, IntPtr info, ref int count);

    // ---------- 磁盘：statfs("/System/Volumes/Data")（无子进程） ----------
    [DllImport("libSystem.dylib", EntryPoint = "statfs", SetLastError = true)]
    private static extern int statfs([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buf);

    private DateTime _diskChecked = DateTime.MinValue;
    private Reading<double> _disk = Reading<double>.NotRequested;
    private double _driveUsedGB, _driveTotalGB;

    /// <summary>根卷用量百分比与容量（GB）；30 秒一采。</summary>
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
            uint bsize = BitConverter.ToUInt32(buf, 0);
            ulong blocks = BitConverter.ToUInt64(buf, 8);
            ulong bfree = BitConverter.ToUInt64(buf, 16);
            if (blocks == 0) { _disk = Reading<double>.Unavailable("statfs 返回 0 块"); return _disk; }
            ulong used = blocks >= bfree ? blocks - bfree : 0;
            if (bsize > 0)
            {
                _driveTotalGB = blocks * (double)bsize / 1073741824.0;
                _driveUsedGB = used * (double)bsize / 1073741824.0;
            }
            _disk = Reading<double>.Of(Math.Clamp(used * 100.0 / blocks, 0, 100));
        }
        catch (Exception ex) { _disk = Reading<double>.Unavailable(ex.Message); }
        return _disk;
    }
    [DllImport("libSystem.dylib")] private static extern int host_page_size(uint host, out uint pageSize);
    [DllImport("libSystem.dylib")] private static extern int sysctlbyname([MarshalAs(UnmanagedType.LPUTF8Str)] string name, out ulong oldp, ref IntPtr oldlenp, IntPtr newp, IntPtr newlen);
}
