using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Timers;
using Island.Core.Monitoring;

namespace Island.Native;

/// <summary>
/// 纯系统 API 的系统监控采样器，不依赖任何内核驱动。
/// - CPU 使用率：PerformanceCounter（PDH 的 .NET 封装），失败回退到 GetSystemTimes
/// - 内存：GlobalMemoryStatusEx
/// - 网速：IP Helper API GetIfTable 的字节计数差值（参考 TrafficMonitor）
/// - GPU 使用率：PDH \"GPU Engine\" 计数器聚合
/// 采样在独立 Timer 线程完成，通过 Snapshot 事件推给 UI（UI 只做渲染）。
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly object _gate = new();

    // 采样互斥：同一时刻只允许一个 Sample 在跑（后台首帧采样与计时器 tick 可能撞车，
    // 而 PDH 缓冲、_nicPrev、_driveSpace 都是无锁共享状态）。
    private int _sampling;
    // 关闭标志：在途 tick 看到它就安静退出，不再写"误导性"的失败日志。
    private volatile bool _stopping;
    // 采样就绪标志（0=未就绪，1=已就绪）：Volatile 读写，保证后台初始化与计时线程的可见性。
    private int _initState;
    private System.Threading.Tasks.Task? _initTask;
    private bool _disposed;

    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter[]? _coreCounters;
    private PdhWildcardQuery? _coreQuery;   // 核心柱的快路径：一次通配符查询读全部逻辑核
    private bool _cpuOk;

    private long _lastIn, _lastOut;
    private DateTime _lastNetTime;
    private double _smoothDown, _smoothUp;
    private bool _netInit;

    // GPU 计数器缓存：构造它要打开 PDH 通配符查询 + 跑一次 WMI 查显卡名（本机实测 ~1.4s），
    // 所以不能在构造函数（UI 线程）里建 —— 挪到 Start() 的后台初始化里。
    private CounterSampleCache? _gpuCache;

    public MonitorSnapshot Current { get; private set; } = new();

    /// <summary>每次采样完成触发，参数是不可变快照。</summary>
    public event Action<MonitorSnapshot>? Snapshot;

    private PerformanceCounter? _procCounter, _threadCounter;

    public SystemMonitor(int intervalMs = 1000)
    {
        // 只做字段初始化：打开 PDH 查询 / WMI 查询在本机实测要 ~1.5s，
        // 而构造函数跑在 UI 线程上（IslandWindow 里 new + Start）⇒ 重活挪到后台。
        _timer = new System.Timers.Timer(intervalMs);
        _timer.Elapsed += OnTick;
    }

    /// <summary>进程数/线程数计数器（PDH "System" 分类）—— 后台初始化时建。</summary>
    private void InitProcCounters()
    {
        try
        {
            _procCounter = new PerformanceCounter("System", "Processes", true);
            _threadCounter = new PerformanceCounter("System", "Threads", true);
            _procCounter.NextValue(); _threadCounter.NextValue();
        }
        catch { _procCounter = _threadCounter = null; }
    }

    /// <summary>运行时调整采样间隔（设置中心"监控刷新率"）。</summary>
    public void SetInterval(int intervalMs) => _timer.Interval = Math.Clamp(intervalMs, 250, 10000);

    /// <summary>
    /// 启动采样。计数器初始化（PDH/WMI）+ 首帧采样都在后台线程做，
    /// 调用线程（UI 线程）只负责起表 —— 原来这里要阻塞 1519ms。
    /// </summary>
    public void Start()
    {
        if (_stopping || _initTask is not null) return;
        _timer.Start();
        _initTask = System.Threading.Tasks.Task.Run(BackgroundInit);
    }

    /// <summary>后台初始化：恢复今日累计 → 建计数器 → 首帧采样。</summary>
    private void BackgroundInit()
    {
        try
        {
            LoadTraffic();               // 恢复今日累计（重启不归零）
            InitNetworkBaseline();
            InitProcCounters();
            InitCpu();
            InitDisk();
            _gpuCache = new CounterSampleCache();   // 打开 PDH 查询 + WMI 查显卡名（最慢的一步）
        }
        catch { /* 初始化失败：交给采样里的兜底路径（CPU 走 GetSystemTimes 等） */ }
        finally { System.Threading.Volatile.Write(ref _initState, 1); }
        if (_stopping) return;
        try { Sample(); } catch { /* 首帧失败：下一轮 tick 继续 */ }   // 首帧
    }

    public void Stop() => _timer.Stop();

    /// <summary>
    /// 停止采样并等在途采样结束（最多 timeoutMs 毫秒）后再释放资源。
    /// 关闭时不等在途 tick，PDH 句柄会被 Sample 撞上 ⇒
    /// run.log 里留下一条误导性的 "sample-failed ObjectDisposedException"。
    /// </summary>
    public void StopAndWait(int timeoutMs = 500)
    {
        _stopping = true;
        try { _timer.Stop(); } catch { }
        // 等后台初始化结束（初始化里也会做一次首帧采样）
        var init = _initTask;
        if (init is not null)
        {
            try { init.Wait(Math.Max(0, timeoutMs)); } catch { }
        }
        // 再等在途采样退出
        var sw = Stopwatch.StartNew();
        while (System.Threading.Volatile.Read(ref _sampling) != 0 && sw.ElapsedMilliseconds < timeoutMs)
            System.Threading.Thread.Sleep(2);
    }

    private void OnTick(object? sender, ElapsedEventArgs e)
    {
        if (_stopping) return;                                             // 正在关闭：安静退出
        if (System.Threading.Volatile.Read(ref _initState) == 0) return;    // 初始化未完成：本轮跳过
        try { Sample(); }
        catch (Exception ex)
        {
            // 采样失败保留上一帧，绝不让采样线程崩掉；但必须留痕，
            // 否则"读数永久停更"这件事既看不出来也查不到
            if (!_stopping) LogSampleFailure(ex);
        }
    }

    private DateTime _lastSampleError = DateTime.MinValue;

    // 同进程内有多处往 run.log 追加（采样失败 + 关闭路径）：没有锁时两行会交错成乱码。
    // 只做**进程内**互斥：run.log 是本机单用户应用的一次性日志，跨进程并发写概率极低，
    // 为它引入文件级锁（Mutex / FileShare）反而会在异常路径上多出新的失败点。
    private static readonly object _logLock = new();

    /// <summary>采样失败记一行到 run.log（每分钟最多一条，避免刷屏）。</summary>
    private void LogSampleFailure(Exception ex)
    {
        try
        {
            var now = DateTime.Now;
            if ((now - _lastSampleError).TotalSeconds < 60) return;
            _lastSampleError = now;
            AppendRunLog($"[{now:HH:mm:ss}] sample-failed {ex.GetType().Name}: {ex.Message}\n");
        }
        catch { }
    }

    /// <summary>往 run.log 追加一行（进程内互斥）。同进程其它写入者请走这里，避免两行交错。</summary>
    internal static void AppendRunLog(string line)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyIsland");
            lock (_logLock)
            {
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "run.log"), line);
            }
        }
        catch { }
    }

    private void InitCpu()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            _cpuCounter.NextValue(); // 预热
            int cores = Environment.ProcessorCount;
            // 核心柱：一次通配符查询读全部逻辑核（旧的逐核 PerformanceCounter 每秒 14 次 PDH collect）。
            // 通配符开不起来时再退回逐核计数器。
            try
            {
                var query = new PdhWildcardQuery();
                if (query.Open(@"\Processor(*)\% Processor Time")) _coreQuery = query;
                else query.Dispose();
            }
            catch { _coreQuery = null; }
            if (_coreQuery is null)
            {
                _coreCounters = new PerformanceCounter[cores];
                for (int i = 0; i < cores; i++)
                {
                    _coreCounters[i] = new PerformanceCounter("Processor", "% Processor Time", i.ToString(), true);
                    _coreCounters[i].NextValue();
                }
            }
            _cpuOk = true;
        }
        catch
        {
            _cpuOk = false;
        }
        // CPU 实际频率：Processor Performance(% of 最大值) × 基频
        try
        {
            _freqCounter = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
            _freqCounter.NextValue();
            // 基频从注册表取（MHz）
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            _baseFreqMHz = key?.GetValue("~MHz") is int mhz && mhz > 0 ? mhz : 0;
        }
        catch { _freqCounter = null; }
    }

    private PerformanceCounter? _freqCounter;
    private int _baseFreqMHz;

    // ---------- 磁盘（LiteMonitor 同款：PDH PhysicalDisk；_Total + 逐盘实例） ----------
    private PerformanceCounter? _diskActiveCounter, _diskReadCounter, _diskWriteCounter;

    /// <summary>一块物理盘的计数器组（实例名形如 "0 C: D:"）。</summary>
    private sealed class DiskChannel
    {
        public string Name = "";
        public PerformanceCounter? Active, Read, Write;
        public void Dispose() { try { Active?.Dispose(); } catch { } try { Read?.Dispose(); } catch { } try { Write?.Dispose(); } catch { } }
    }
    private DiskChannel[] _diskChannels = Array.Empty<DiskChannel>();

    private void InitDisk()
    {
        try
        {
            _diskActiveCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", true);
            _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true);
            _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);
            _diskActiveCounter.NextValue(); _diskReadCounter.NextValue(); _diskWriteCounter.NextValue(); // 预热
        }
        catch
        {
            // 建到一半失败：已建好的计数器句柄必须释放，否则每次初始化都漏一批句柄
            _diskActiveCounter?.Dispose(); _diskReadCounter?.Dispose(); _diskWriteCounter?.Dispose();
            _diskActiveCounter = _diskReadCounter = _diskWriteCounter = null;   // 计数器缺失则显示 0，不影响其余读数
        }
        // 逐盘实例（多硬盘监控页每盘一行）
        var channels = new List<DiskChannel>();
        try
        {
            var cat = new PerformanceCounterCategory("PhysicalDisk");
            foreach (string inst in cat.GetInstanceNames())
            {
                if (inst.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
                // 实例名里的卷符："0 C: D:" → ["C:","D:"]（跨区卷取最后一个作显示名）
                string letter = "";
                for (int i = 0; i + 1 < inst.Length; i++)
                    if (char.IsLetter(inst[i]) && inst[i + 1] == ':')
                        letter = char.ToUpperInvariant(inst[i]) + ":";
                if (letter.Length == 0) continue;   // 无挂载卷的物理盘跳过
                DiskChannel? ch = null;
                try
                {
                    ch = new DiskChannel { Name = letter };
                    ch.Active = new PerformanceCounter("PhysicalDisk", "% Disk Time", inst, true);
                    ch.Read = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", inst, true);
                    ch.Write = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", inst, true);
                    ch.Active.NextValue(); ch.Read.NextValue(); ch.Write.NextValue();
                    channels.Add(ch);
                }
                catch
                {
                    // 单实例不可读跳过：但已经建好的那几个计数器要释放，否则句柄一路漏下去
                    ch?.Dispose();
                }
            }
            _diskChannels = channels.ToArray();
        }
        catch
        {
            foreach (var ch in channels) ch.Dispose();   // 整体失败：已建的逐盘计数器也要释放
            _diskChannels = Array.Empty<DiskChannel>();
        }
    }

    /// <summary>磁盘活动时间%（0..100 封顶，PDH 瞬时可能超 100）与读/写速度。</summary>
    private (double active, double read, double write) SampleDisk()
    {
        if (_diskActiveCounter is null) return (0, 0, 0);
        try
        {
            return (Math.Clamp(_diskActiveCounter.NextValue(), 0, 100),
                    Math.Max(0, _diskReadCounter?.NextValue() ?? 0),
                    Math.Max(0, _diskWriteCounter?.NextValue() ?? 0));
        }
        catch { return (0, 0, 0); }
    }

    // 各盘空间：30 秒查一次（DriveInfo 便宜但没必要每秒）
    private DateTime _drivesChecked = DateTime.MinValue;
    private readonly Dictionary<string, (double used, double total)> _driveSpace = new(StringComparer.OrdinalIgnoreCase);

    private void SampleDriveSpaces()
    {
        if ((DateTime.Now - _drivesChecked).TotalSeconds < 30) return;
        _drivesChecked = DateTime.Now;
        try
        {
            foreach (var d in System.IO.DriveInfo.GetDrives())
            {
                if (d.DriveType != System.IO.DriveType.Fixed || !d.IsReady) continue;
                _driveSpace[d.Name.TrimEnd('\\')] =
                    ((d.TotalSize - d.AvailableFreeSpace) / 1073741824.0, d.TotalSize / 1073741824.0);
            }
        }
        catch { /* 保留上次 */ }
    }

    /// <summary>逐盘快照（最多 4 块，按盘符排序）。</summary>
    private DiskStat[] SampleDisks()
    {
        if (_diskChannels.Length == 0) return Array.Empty<DiskStat>();
        var list = new List<DiskStat>(_diskChannels.Length);
        foreach (var ch in _diskChannels)
        {
            double a = 0, rd = 0, wr = 0;
            try
            {
                a = Math.Clamp(ch.Active?.NextValue() ?? 0, 0, 100);
                rd = Math.Max(0, ch.Read?.NextValue() ?? 0);
                wr = Math.Max(0, ch.Write?.NextValue() ?? 0);
            }
            catch { /* 单次失败给 0 */ }
            _driveSpace.TryGetValue(ch.Name, out var sp);
            list.Add(new DiskStat
            {
                Name = ch.Name, ActivePercent = a, ReadSpeed = rd, WriteSpeed = wr,
                UsedGB = sp.used, TotalGB = sp.total,
            });
        }
        return list.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
    }

    // ---------- 高占用进程（TotalProcessorTime 差分，3 秒一采；不为每进程建 PDH 计数器） ----------
    private DateTime _procChecked = DateTime.MinValue;
    // 进程身份：PID 会被复用，差分时必须校验"还是不是同一个进程"
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
        var mems = new List<TopProcess>(64);
        int coresN = Math.Max(1, Environment.ProcessorCount);

        foreach (var proc in Process.GetProcesses())
        {
            // Process 持有进程句柄，不释放会一直攒到 GC 终结器（几百个进程 × 每 3 秒）
            using var p = proc;
            try
            {
                string name = p.ProcessName;
                if (name is "Idle" or "System" or "Registry" or "Memory Compression") continue;
                var cpu = p.TotalProcessorTime;
                DateTime start = DateTime.MinValue;
                try { start = p.StartTime; } catch { /* 无权限：退化为只按进程名判身份 */ }
                long ws = 0;
                try { ws = p.WorkingSet64; } catch { /* 权限不足：内存显示 0 */ }
                next[p.Id] = (name, cpu, start);
                string exePath = "";
                if (wall > 0.5 && prev.TryGetValue(p.Id, out var old) && IsSameProcess(old, name, start))
                {
                    double pct = (cpu - old.Cpu).TotalSeconds / wall / coresN * 100;
                    if (pct >= 0.05)
                    {
                        try { exePath = p.MainModule?.FileName ?? ""; } catch { }
                        tops.Add(new TopProcess
                        {
                            Name = name,
                            CpuPercent = Math.Min(pct, 100),
                            MemoryMB = ws / 1048576.0,
                            ExePath = exePath,
                        });
                    }
                }
                // 内存榜：独立采集（与 CPU 无关，工作集前 5）
                if (ws > 200 * 1048576)   // 200MB 门槛，过滤小进程
                {
                    if (exePath.Length == 0) { try { exePath = p.MainModule?.FileName ?? ""; } catch { } }
                    mems.Add(new TopProcess { Name = name, MemoryMB = ws / 1048576.0, ExePath = exePath });
                }
            }
            catch { /* 进程已退出/无权限：跳过 */ }
            finally { p.Dispose(); }
        }
        _procPrev = next;
        // 本轮为空也要更新（赋空数组）：否则界面会一直显示已经退出的进程和旧数值
        _topProcesses = tops.Count > 0
            ? tops.OrderByDescending(t => t.CpuPercent).Take(5).ToArray()
            : Array.Empty<TopProcess>();
        _topProcessesByMem = mems.Count > 0
            ? mems.OrderByDescending(t => t.MemoryMB).Take(5).ToArray()
            : Array.Empty<TopProcess>();
    }

    /// <summary>
    /// PID 会被系统复用：同一个 PID 前后可能是两个毫不相干的进程。
    /// 不校验身份就做差分，会把"新进程开机以来的累计 CPU"当成"本轮增量"，
    /// 算出一个巨大的假峰值。身份 = 进程名 + 启动时间（取不到启动时间时退化为只看进程名）。
    /// </summary>
    private static bool IsSameProcess((string Name, TimeSpan Cpu, DateTime Start) old, string name, DateTime start)
    {
        if (!old.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return false;
        if (old.Start == DateTime.MinValue || start == DateTime.MinValue) return true;
        return old.Start == start;
    }

    /// <summary>当前 CPU 平均实际频率 GHz（PDH % Performance × 基频）。</summary>
    private double SampleFreqGHz()
    {
        if (_freqCounter is null || _baseFreqMHz == 0) return 0;
        try
        {
            float pct = _freqCounter.NextValue();
            if (pct <= 0 || pct > 1000) return 0;
            return pct * _baseFreqMHz / 1000.0 / 100.0;
        }
        catch { return 0; }
    }

    private void InitNetworkBaseline()
    {
        ReadNetworkBytes(out _lastIn, out _lastOut, out _, out _);
        _lastNetTime = DateTime.Now;
        _netInit = true;
    }

    /// <summary>
    /// 采样入口：保证同一时刻只有一个采样在跑。
    /// 后台首帧采样与计时器 tick 可能撞在一起，而 PDH 缓冲、_nicPrev、_driveSpace
    /// 都是无锁共享状态 ⇒ 重入时直接跳过本轮（共享字典的访问也就只发生在采样线程内）。
    /// </summary>
    private void Sample()
    {
        if (_stopping) return;
        if (System.Threading.Interlocked.CompareExchange(ref _sampling, 1, 0) != 0) return;
        try { SampleCore(); }
        finally { System.Threading.Volatile.Write(ref _sampling, 0); }
    }

    /// <summary>采样主体。</summary>
    private void SampleCore()
    {
        double cpu = SampleCpu();
        double[]? cores = SampleCores();
        var mem = ReadMemory();
        var commit = ReadCommit();
        ReadNetwork(out double down, out double up);
        double gpu = _gpuCache?.ReadGpuUsage() ?? 0;
        string gpuName = _gpuCache?.Name ?? "";
        var disk = SampleDisk();
        SampleDriveSpaces();
        var disks = SampleDisks();
        SampleTopProcesses();
        SamplePing();
        // 系统盘空间（来自全体驱动器字典）
        string sysRoot = (System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
        _driveSpace.TryGetValue(sysRoot, out var sysSpace);

        var snap = new MonitorSnapshot
        {
            CpuUsage = cpu,
            CoreUsages = cores,
            CpuFrequencyGHz = SampleFreqGHz(),
            MemoryUsagePercent = mem.percent,
            MemoryUsedGB = mem.used,
            MemoryTotalGB = mem.total,
            GpuUsage = gpu,
            GpuName = gpuName,
            DiskUsagePercent = disk.active,
            DiskReadSpeed = disk.read,
            DiskWriteSpeed = disk.write,
            DriveUsedGB = sysSpace.used,
            DriveTotalGB = sysSpace.total,
            DriveName = sysRoot,
            Disks = disks,
            TopProcesses = _topProcesses,
            TopProcessesByMem = _topProcessesByMem,
            DownloadSpeed = down,
            UploadSpeed = up,
            SessionDownload = _sessionDown,
            SessionUpload = _sessionUp,
            TodayDownload = _todayDown,
            TodayUpload = _todayUp,
            LinkType = DetectLinkType(),
            UptimeHours = Environment.TickCount64 / 3600000.0,
            ProcessCount = SafeCount(_procCounter),
            ThreadCount = SafeCount(_threadCounter),
            CommitGB = commit.used,
            CommitLimitGB = commit.total,
            PingMs = System.Threading.Volatile.Read(ref _pingMs),
        };
        lock (_gate) Current = snap;
        if (_stopping) return;   // 关闭过程中不再抛事件
        Snapshot?.Invoke(snap);
    }

    private double SampleCpu()
    {
        if (_cpuOk && _cpuCounter is not null)
        {
            // PDH 句柄失效（休眠唤醒、计数器重置）时 NextValue 会抛 —— 兜底到 GetSystemTimes，
            // 否则这一轮整个快照都不产出，界面永久停在上一帧且没有任何痕迹
            try
            {
                double v = _cpuCounter.NextValue();
                if (v >= 0 && v <= 100) return v;
            }
            catch { _cpuOk = false; }
        }
        return _cpuFallback.Read();
    }

    private double[]? SampleCores()
    {
        if (!_cpuOk) return null;
        if (_coreQuery is not null)
        {
            // 通配符路径：一次数组读，实例名即逻辑核序号（"_Total" 之类的非数字实例被跳过）
            var values = _coreQuery.ReadAll();
            if (values.Count > 0)
            {
                var result = new double[Math.Max(1, Environment.ProcessorCount)];
                foreach (var (name, value) in values)
                {
                    if (!int.TryParse(name, out int index)) continue;
                    if ((uint)index >= (uint)result.Length) continue;
                    result[index] = Math.Clamp(value, 0, 100);
                }
                return result;
            }
            return null;
        }
        if (_coreCounters is null) return null;
        var legacy = new double[_coreCounters.Length];
        try
        {
            for (int i = 0; i < legacy.Length; i++)
                legacy[i] = Math.Clamp(_coreCounters[i].NextValue(), 0, 100);
        }
        catch { return null; }   // 计数器失效：这一轮不给核心柱，别让整帧停更
        return legacy;
    }

    private readonly TimesFallback _cpuFallback = new();

    private static (double percent, double used, double total) ReadMemory()
    {
        if (GetPhysicalMemory(out ulong total, out ulong avail))
        {
            double totalGB = total / 1024.0 / 1024 / 1024;
            double usedGB = (total - avail) / 1024.0 / 1024 / 1024;
            double pct = total == 0 ? 0 : (total - avail) * 100.0 / total;
            return (pct, usedGB, totalGB);
        }
        return (0, 0, 0);
    }

    /// <summary>提交内存（commit）：limit = pagefile 总量，used = 总量 - 可用。</summary>
    private static (double used, double total) ReadCommit()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m) && m.ullTotalPageFile > 0)
            return ((m.ullTotalPageFile - m.ullAvailPageFile) / 1073741824.0,
                    m.ullTotalPageFile / 1073741824.0);
        return (0, 0);
    }

    private static int SafeCount(PerformanceCounter? c)
    {
        try { return c is null ? 0 : (int)c.NextValue(); }
        catch { return 0; }
    }

    // ---------- Ping（L4 Hardware 同款：3s 节流异步 ICMP，不阻塞采样线程） ----------
    private long _pingMs = -1;
    private DateTime _lastPing = DateTime.MinValue;

    private void SamplePing()
    {
        if ((DateTime.Now - _lastPing).TotalSeconds < 3) return;
        _lastPing = DateTime.Now;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                using var p = new System.Net.NetworkInformation.Ping();
                var r = await p.SendPingAsync("223.5.5.5", 1500);   // 阿里 DNS，国内延迟基准
                long rtt = r.Status == System.Net.NetworkInformation.IPStatus.Success
                    ? r.RoundtripTime : -1;
                System.Threading.Volatile.Write(ref _pingMs, rtt);
            }
            catch { /* 离线/被防火墙拦：保持上一帧 */ }
        });
    }

    private void ReadNetwork(out double down, out double up)
    {
        down = up = 0;
        ReadNetworkBytes(out long inBytes, out long outBytes, out long deltaIn, out long deltaOut);
        var now = DateTime.Now;
        if (!_netInit) { _lastIn = inBytes; _lastOut = outBytes; _lastNetTime = now; _netInit = true; return; }

        double sec = (now - _lastNetTime).TotalSeconds;
        if (sec <= 0) return;

        // ReadNetworkBytes 已按单卡独立做差，即使总计数因某张网卡断开/切换而下降，
        // 其余在线网卡的 deltaIn/deltaOut 仍然有效，直接累加即可（不丢本轮增量）。
        double rawDown = deltaIn / sec;
        double rawUp = deltaOut / sec;
        long dDown = deltaIn, dUp = deltaOut;
        _sessionDown += dDown;   // 会话累计
        _sessionUp += dUp;
        // 今日累计：跨天归零（TrafficMonitor 同款语义）
        if (now.Date != _today)
        {
            _today = now.Date;
            _todayDown = _todayUp = 0;
        }
        _todayDown += dDown;
        _todayUp += dUp;
        _lastIn = inBytes; _lastOut = outBytes; _lastNetTime = now;

        // 今日流量持久化：30s 一写（崩溃/重启不丢）
        if ((now - _lastTrafficSave).TotalSeconds >= 30)
        {
            _lastTrafficSave = now;
            SaveTraffic();
        }

        // 指数平滑，避免数字乱跳
        _smoothDown = _smoothDown * 0.6 + rawDown * 0.4;
        _smoothUp = _smoothUp * 0.6 + rawUp * 0.4;
        down = _smoothDown; up = _smoothUp;
    }

    private DateTime _today = DateTime.Now.Date;
    private long _todayDown, _todayUp;

    // ---------- 今日流量持久化（LiteMonitor 流量统计同款：重启不归零） ----------
    private static string TrafficFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyIsland", "traffic.json");
    private DateTime _lastTrafficSave = DateTime.MinValue;

    private void LoadTraffic()
    {
        try
        {
            if (!System.IO.File.Exists(TrafficFile)) return;
            var json = System.IO.File.ReadAllText(TrafficFile);
            var doc = System.Text.Json.JsonDocument.Parse(json).RootElement;
            string date = doc.GetProperty("date").GetString() ?? "";
            if (date == DateTime.Now.ToString("yyyy-MM-dd"))
            {
                _todayDown = doc.GetProperty("down").GetInt64();
                _todayUp = doc.GetProperty("up").GetInt64();
            }
            // 跨天：不恢复（今日归零），昨天的总量由文件保留（后续可做历史页）
        }
        catch { }
    }

    private void SaveTraffic()
    {
        try
        {
            // 退出可能发生在午夜后的下一次采样之前；避免把昨天的计数写成今天。
            var today = DateTime.Now.Date;
            if (today != _today)
            {
                _today = today;
                _todayDown = _todayUp = 0;
            }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(TrafficFile)!);
            string json = $"{{\"date\":\"{_today:yyyy-MM-dd}\",\"down\":{_todayDown},\"up\":{_todayUp}}}";
            // 原子写（先写临时文件再整体替换，与 Settings.cs 的做法一致）：
            // 直接 WriteAllText 时崩溃/断电会留下半截 JSON，下次启动解析失败 ⇒ 今日累计直接归零。
            string tmp = TrafficFile + ".tmp";
            System.IO.File.WriteAllText(tmp, json);
            System.IO.File.Move(tmp, TrafficFile, true);
        }
        catch { }
    }

    private long _sessionDown, _sessionUp;
    private string _linkType = "";
    private DateTime _linkChecked;

    /// <summary>识别当前活跃连接类型（wifi/ethernet），30 秒查一次 WMI（贵）。</summary>
    private string DetectLinkType()
    {
        if ((DateTime.Now - _linkChecked).TotalSeconds < 30) return _linkType;
        _linkChecked = DateTime.Now;
        try
        {
            // NetworkInterface 无需 WMI 且很快：拿它判断
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
                { _linkType = "wifi"; return _linkType; }
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Ethernet
                    && nic.GetIPProperties().GatewayAddresses.Count > 0)
                { _linkType = "ethernet"; }
            }
        }
        catch { }
        return _linkType;
    }

    // ---------- P/Invoke ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static bool GetPhysicalMemory(out ulong total, out ulong avail)
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m)) { total = m.ullTotalPhys; avail = m.ullAvailPhys; return true; }
        total = avail = 0; return false;
    }

    // 每张网卡各自的绝对计数基线：新增网卡只建基线，不把它的历史累计量算进本轮增量，
    // 否则插拔网卡时"今日下载"会一次性跳变几十 GB
    private readonly Dictionary<string, (long In, long Out)> _nicPrev = new(StringComparer.Ordinal);

    /// <summary>
    /// 读网卡字节计数。inBytes/outBytes 是**绝对计数之和**（用于检测计数器回退）；
    /// deltaIn/deltaOut 是**本轮增量**（每张网卡各自做差，新增网卡贡献 0）。
    /// </summary>
    private void ReadNetworkBytes(out long inBytes, out long outBytes, out long deltaIn, out long deltaOut)
    {
        // 用 .NET NetworkInterface（底层 GetIfTable2，64 位计数器，无 4GB 回绕问题）
        inBytes = outBytes = 0;
        deltaIn = deltaOut = 0;
        var current = new Dictionary<string, (long In, long Out)>(StringComparer.Ordinal);
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                // 手动选择网卡（LiteMonitor PreferredNetwork 同款）：只统计选定网卡
                if (!string.IsNullOrEmpty(PreferredNicId) && nic.Id != PreferredNicId) continue;
                var stats = nic.GetIPStatistics();
                if (stats is null) continue;
                // 过滤回环/隧道：只统计有网关的物理网卡（与 DetectLinkType 一致的口径）
                bool hasGateway = nic.GetIPProperties().GatewayAddresses.Count > 0;
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                if (!hasGateway && nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211) continue;
                inBytes += stats.BytesReceived;
                outBytes += stats.BytesSent;
                current[nic.Id] = (stats.BytesReceived, stats.BytesSent);
            }
        }
        catch { /* 单次失败：调用方按 0 处理并重建基线 */ }

        // 本轮增量 = 各网卡与上一轮基线之差（新增网卡没有基线 → 贡献 0）
        foreach (var (id, v) in current)
            if (_nicPrev.TryGetValue(id, out var prev))
            {
                long dIn = v.In - prev.In, dOut = v.Out - prev.Out;
                if (dIn > 0) deltaIn += dIn;      // 计数器回退（驱动重置）→ 按 0 计
                if (dOut > 0) deltaOut += dOut;
            }
        _nicPrev.Clear();
        foreach (var kv in current) _nicPrev[kv.Key] = kv.Value;
    }

    /// <summary>手动选择的网卡 Id（空=自动聚合所有物理网卡；设置中心"网卡"选项）。</summary>
    public string PreferredNicId { get; set; } = "";

    public void Dispose()
    {
        if (_disposed) return;      // 幂等：重复关闭不重复释放
        _disposed = true;
        StopAndWait();              // 先停表并等在途采样结束，避免采样撞上已释放的 PDH 句柄
        SaveTraffic();              // 退出前落盘今日流量，避免丢失最后最多 30 秒的累计
        _timer.Dispose();
        _cpuCounter?.Dispose();
        _freqCounter?.Dispose();
        _procCounter?.Dispose();
        _threadCounter?.Dispose();
        _diskActiveCounter?.Dispose();
        _diskReadCounter?.Dispose();
        _diskWriteCounter?.Dispose();
        foreach (var ch in _diskChannels) ch.Dispose();
        if (_coreCounters is not null) foreach (var c in _coreCounters) c.Dispose();
        _coreQuery?.Dispose();          // 核心柱通配符查询（PDH 查询句柄 + 缓冲）
        _gpuCache?.Dispose();           // GPU 计数器缓存（此前一直没释放）
    }
}

/// <summary>PerformanceCounter 不可用时用 GetSystemTimes 差分兜底。</summary>
internal sealed class TimesFallback
{
    private long _prevIdle, _prevKernel, _prevUser;
    private bool _init;

    public double Read()
    {
        if (!GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
            return 0;
        long i = ToLong(idle), k = ToLong(kernel), u = ToLong(user);
        if (!_init) { _prevIdle = i; _prevKernel = k; _prevUser = u; _init = true; return 0; }
        long sys = (k - _prevKernel) + (u - _prevUser);
        long delta = sys - (i - _prevIdle);
        _prevIdle = i; _prevKernel = k; _prevUser = u;
        return sys <= 0 ? 0 : Math.Clamp(delta * 100.0 / sys, 0, 100);
    }

    private static long ToLong(FILETIME ft) => ((long)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime, dwHighDateTime; }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);
}
