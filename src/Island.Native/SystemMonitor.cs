using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Timers;
using Island.Core.Monitoring;

namespace Island.Native;

/// <summary>
/// 纯系统 API 的系统监控采样器，不依赖任何内核驱动。
/// 采样在独立 Timer 线程完成，通过 Snapshot 事件推给 UI（UI 只做渲染）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemMonitor : IDisposable
{
    private readonly System.Timers.Timer _timer;
    // 保护原生计数器/PDH 缓冲的生命周期：SampleCore / BackgroundInit 与 Dispose 绝不并发访问原生句柄
    private readonly object _resourceLock = new();

    private int _sampling;
    private volatile bool _stopping;
    private int _initState;
    private System.Threading.Tasks.Task? _initTask;
    private volatile bool _disposed;

    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter[]? _coreCounters;
    private PdhWildcardQuery? _coreQuery;
    private double[]? _coreBuf;
    private bool _cpuOk;

    private long _lastNetTick;
    private double _smoothDown, _smoothUp;
    private bool _netInit;

    private CounterSampleCache? _gpuCache;
    private MonitorSnapshot _current = new();

    public MonitorSnapshot Current => Volatile.Read(ref _current);

    /// <summary>每次采样完成触发，参数是不可变快照。</summary>
    public event Action<MonitorSnapshot>? Snapshot;

    private PerformanceCounter? _procCounter, _threadCounter;

    public SystemMonitor(int intervalMs = 1000)
    {
        _timer = new System.Timers.Timer(Math.Clamp(intervalMs, 250, 10000));
        _timer.Elapsed += OnTick;
    }

    /// <summary>睡眠/休眠唤醒后重置网卡基线与计数状态（避免唤醒首帧网速暴涨或误污染今日流量）。</summary>
    public void NotifySystemResume()
    {
        if (_disposed) return;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            lock (_resourceLock)
            {
                if (_disposed) return;
                _netInit = false;
                _smoothDown = 0;
                _smoothUp = 0;
                _nicPrev.Clear();
                _linkCheckedTick = 0;
            }
        });
    }

    /// <summary>按当前可见视图动态裁剪高开销采样项（进程枚举 / Ping / GPU）。</summary>
    public void SetDemand(SamplerDemand demand) => Volatile.Write(ref UnsafeDemandRef(), (int)demand);
    private int _demandBits = (int)SamplerDemand.MonitorPage;
    private ref int UnsafeDemandRef() => ref _demandBits;
    private SamplerDemand CurrentDemand => (SamplerDemand)Volatile.Read(ref _demandBits);

    private void InitProcCounters()
    {
        PerformanceCounter? proc = null, thread = null;
        try
        {
            proc = new PerformanceCounter("System", "Processes", true);
            thread = new PerformanceCounter("System", "Threads", true);
            proc.NextValue();
            thread.NextValue();
            _procCounter = proc;
            _threadCounter = thread;
        }
        catch
        {
            try { proc?.Dispose(); } catch { }
            try { thread?.Dispose(); } catch { }
            _procCounter = _threadCounter = null;
        }
    }

    public void SetInterval(int intervalMs)
    {
        if (!_disposed) _timer.Interval = Math.Clamp(intervalMs, 250, 10000);
    }

    public void Start()
    {
        if (_disposed) return;
        _stopping = false;
        _timer.Start();
        if (_initTask is null)
            _initTask = System.Threading.Tasks.Task.Run(BackgroundInit);
    }

    private void BackgroundInit()
    {
        try
        {
            lock (_resourceLock)
            {
                if (_disposed) return;
                LoadTraffic();
                InitNetworkBaseline();
                InitProcCounters();
                if (_disposed) return;
                InitCpu();
                if (_disposed) return;
                InitDisk();
                if (_disposed) return;
                _gpuCache = new CounterSampleCache();
            }
        }
        catch { /* 初始化失败：交给采样里的兜底路径 */ }
        finally { Volatile.Write(ref _initState, 1); }

        if (_stopping || _disposed) return;
        try { Sample(); } catch { }
    }

    public void Stop()
    {
        _stopping = true;
        try { _timer.Stop(); } catch { }
    }

    public void StopAndWait(int timeoutMs = 500)
    {
        _stopping = true;
        try { _timer.Stop(); } catch { }
        var init = _initTask;
        if (init is not null)
        {
            try { init.Wait(Math.Max(0, timeoutMs)); } catch { }
        }
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _sampling) != 0 && sw.ElapsedMilliseconds < timeoutMs)
            Thread.Sleep(2);
    }

    private void OnTick(object? sender, ElapsedEventArgs e)
    {
        if (_stopping || _disposed) return;
        if (Volatile.Read(ref _initState) == 0) return;
        try { Sample(); }
        catch (Exception ex)
        {
            if (!_stopping && !_disposed) LogSampleFailure(ex);
        }
    }

    private long _lastSampleErrorTick;
    private static readonly object _logLock = new();

    private void LogSampleFailure(Exception ex)
    {
        try
        {
            long nowTick = Environment.TickCount64;
            if (_lastSampleErrorTick != 0 && nowTick - _lastSampleErrorTick < 60_000) return;
            _lastSampleErrorTick = nowTick;
            AppendRunLog($"[{DateTime.Now:HH:mm:ss}] sample-failed {ex.GetType().Name}: {ex.Message}\n");
        }
        catch { }
    }

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
        PerformanceCounter? total = null;
        var builtCores = new List<PerformanceCounter>();
        try
        {
            total = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            total.NextValue();
            _cpuCounter = total;
            int cores = Math.Max(1, Environment.ProcessorCount);
            try
            {
                var query = new PdhWildcardQuery();
                if (query.Open(@"\Processor(*)\% Processor Time")) _coreQuery = query;
                else query.Dispose();
            }
            catch { _coreQuery = null; }

            if (_coreQuery is null)
            {
                for (int i = 0; i < cores; i++)
                {
                    var c = new PerformanceCounter("Processor", "% Processor Time", i.ToString(), true);
                    c.NextValue();
                    builtCores.Add(c);
                }
                _coreCounters = builtCores.ToArray();
            }
            _cpuOk = true;
        }
        catch
        {
            try { total?.Dispose(); } catch { }
            foreach (var c in builtCores) { try { c.Dispose(); } catch { } }
            _cpuCounter = null;
            _coreCounters = null;
            _cpuOk = false;
        }

        PerformanceCounter? freq = null;
        try
        {
            freq = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
            freq.NextValue();
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            _baseFreqMHz = key?.GetValue("~MHz") is int mhz && mhz > 0 ? mhz : 0;
            _freqCounter = freq;
        }
        catch
        {
            try { freq?.Dispose(); } catch { }
            _freqCounter = null;
        }
    }

    private PerformanceCounter? _freqCounter;
    private int _baseFreqMHz;

    private PerformanceCounter? _diskActiveCounter, _diskReadCounter, _diskWriteCounter;

    private sealed class DiskChannel
    {
        public string Name = "";
        public PerformanceCounter? Active, Read, Write;
        public void Dispose()
        {
            try { Active?.Dispose(); } catch { }
            try { Read?.Dispose(); } catch { }
            try { Write?.Dispose(); } catch { }
        }
    }
    private DiskChannel[] _diskChannels = Array.Empty<DiskChannel>();

    private void InitDisk()
    {
        try
        {
            _diskActiveCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", true);
            _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true);
            _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);
            _diskActiveCounter.NextValue(); _diskReadCounter.NextValue(); _diskWriteCounter.NextValue();
        }
        catch
        {
            _diskActiveCounter?.Dispose(); _diskReadCounter?.Dispose(); _diskWriteCounter?.Dispose();
            _diskActiveCounter = _diskReadCounter = _diskWriteCounter = null;
        }

        var channels = new List<DiskChannel>();
        try
        {
            var cat = new PerformanceCounterCategory("PhysicalDisk");
            foreach (string inst in cat.GetInstanceNames())
            {
                if (inst.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
                string letter = "";
                for (int i = 0; i + 1 < inst.Length; i++)
                    if (char.IsLetter(inst[i]) && inst[i + 1] == ':')
                        letter = char.ToUpperInvariant(inst[i]) + ":";
                if (letter.Length == 0) continue;
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
                catch { ch?.Dispose(); }
            }
            _diskChannels = channels.ToArray();
        }
        catch
        {
            foreach (var ch in channels) ch.Dispose();
            _diskChannels = Array.Empty<DiskChannel>();
        }
    }

    private (double active, double read, double write, bool ok) SampleDisk()
    {
        if (_diskActiveCounter is null) return (0, 0, 0, false);
        try
        {
            double a = _diskActiveCounter.NextValue();
            double r = _diskReadCounter?.NextValue() ?? 0;
            double w = _diskWriteCounter?.NextValue() ?? 0;
            return (
                double.IsFinite(a) ? Math.Clamp(a, 0, 100) : 0,
                double.IsFinite(r) ? Math.Max(0, r) : 0,
                double.IsFinite(w) ? Math.Max(0, w) : 0,
                true);
        }
        catch { return (0, 0, 0, false); }
    }

    private long _drivesCheckedTick;
    private readonly Dictionary<string, (double used, double total)> _driveSpace = new(StringComparer.OrdinalIgnoreCase);

    private void SampleDriveSpaces()
    {
        long nowTick = Environment.TickCount64;
        if (_drivesCheckedTick != 0 && nowTick - _drivesCheckedTick < 30_000) return;
        _drivesCheckedTick = nowTick;
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

    private DiskStat[] SampleDisks()
    {
        if (_diskChannels.Length == 0) return Array.Empty<DiskStat>();
        var list = new List<DiskStat>(_diskChannels.Length);
        foreach (var ch in _diskChannels)
        {
            double a = 0, rd = 0, wr = 0;
            try
            {
                double rawA = ch.Active?.NextValue() ?? 0;
                double rawR = ch.Read?.NextValue() ?? 0;
                double rawW = ch.Write?.NextValue() ?? 0;
                if (double.IsFinite(rawA)) a = Math.Clamp(rawA, 0, 100);
                if (double.IsFinite(rawR)) rd = Math.Max(0, rawR);
                if (double.IsFinite(rawW)) wr = Math.Max(0, rawW);
            }
            catch { }
            _driveSpace.TryGetValue(ch.Name, out var sp);
            list.Add(new DiskStat
            {
                Name = ch.Name, ActivePercent = a, ReadSpeed = rd, WriteSpeed = wr,
                UsedGB = sp.used, TotalGB = sp.total,
            });
        }
        return list.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
    }

    private long _procCheckedTick;
    private Dictionary<int, (string Name, TimeSpan Cpu, DateTime Start)> _procPrev = new();
    private TopProcess[] _topProcesses = Array.Empty<TopProcess>();
    private TopProcess[] _topProcessesByMem = Array.Empty<TopProcess>();

    private void SampleTopProcesses()
    {
        long nowTick = Environment.TickCount64;
        if (_procCheckedTick != 0 && nowTick - _procCheckedTick < 3_000) return;
        double wall = _procCheckedTick == 0 ? 0 : (nowTick - _procCheckedTick) / 1000.0;
        _procCheckedTick = nowTick;

        var prev = _procPrev;
        var next = new Dictionary<int, (string, TimeSpan, DateTime)>(256);
        var cpuCandidates = new List<(int Pid, string Name, double Pct, double MemMB)>(64);
        var memCandidates = new List<(int Pid, string Name, double MemMB)>(64);
        int coresN = Math.Max(1, Environment.ProcessorCount);

        foreach (var proc in Process.GetProcesses())
        {
            using var p = proc;
            try
            {
                int pid = p.Id;
                string name = p.ProcessName;
                if (name is "Idle" or "System" or "Registry" or "Memory Compression") continue;

                // 工作集直接来自系统快照，无需打开高权限进程句柄；独立提取以免被 TotalProcessorTime 权限异常连坐跳过
                long ws = 0;
                try { ws = p.WorkingSet64; } catch { }
                if (ws > 200L * 1048576)
                    memCandidates.Add((pid, name, ws / 1048576.0));

                TimeSpan cpu;
                try { cpu = p.TotalProcessorTime; }
                catch { continue; /* 高权限/受保护进程无权读 CPU 时间：不影响上方内存榜 */ }

                DateTime start = DateTime.MinValue;
                try { start = p.StartTime; } catch { }
                next[pid] = (name, cpu, start);

                if (wall > 0.5 && prev.TryGetValue(pid, out var old) && IsSameProcess(old, name, start))
                {
                    double pct = (cpu - old.Cpu).TotalSeconds / wall / coresN * 100;
                    if (double.IsFinite(pct) && pct >= 0.05)
                        cpuCandidates.Add((pid, name, Math.Min(pct, 100), ws / 1048576.0));
                }
            }
            catch { }
        }
        _procPrev = next;

        // 仅对最终入榜的 Top 5 查询可执行文件路径，且走 PROCESS_QUERY_LIMITED_INFORMATION（避免 PROCESS_VM_READ 触发反作弊）
        _topProcesses = cpuCandidates.Count > 0
            ? cpuCandidates.OrderByDescending(t => t.Pct).Take(5)
                .Select(t => new TopProcess
                {
                    Name = t.Name,
                    CpuPercent = t.Pct,
                    MemoryMB = t.MemMB,
                    ExePath = QueryProcessImagePath(t.Pid),
                }).ToArray()
            : Array.Empty<TopProcess>();

        _topProcessesByMem = memCandidates.Count > 0
            ? memCandidates.OrderByDescending(t => t.MemMB).Take(5)
                .Select(t => new TopProcess
                {
                    Name = t.Name,
                    MemoryMB = t.MemMB,
                    ExePath = QueryProcessImagePath(t.Pid),
                }).ToArray()
            : Array.Empty<TopProcess>();
    }

    private static string QueryProcessImagePath(int pid)
    {
        if (pid <= 0) return "";
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(512);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) && size > 0
                ? sb.ToString(0, size)
                : "";
        }
        catch { return ""; }
        finally { CloseHandle(h); }
    }

    private static bool IsSameProcess((string Name, TimeSpan Cpu, DateTime Start) old, string name, DateTime start)
    {
        if (!old.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return false;
        if (old.Start == DateTime.MinValue || start == DateTime.MinValue) return true;
        return old.Start == start;
    }

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int InformationLevel, IntPtr lpInputBuffer, uint nInputBufferSize,
        [Out] byte[] lpOutputBuffer, uint nOutputBufferSize);

    private double SampleFreqGHz()
    {
        if (_freqCounter is not null && _baseFreqMHz > 0)
        {
            try
            {
                float pct = _freqCounter.NextValue();
                if (float.IsFinite(pct) && pct is > 0 and <= 1000)
                {
                    double ghz = pct * _baseFreqMHz / 1000.0 / 100.0;
                    if (ghz > 0.1) return ghz;
                }
            }
            catch { }
        }
        // 免管理员原生兜底：powrprof CallNtPowerInformation(ProcessorInformation=11)，每逻辑核 24 字节（偏移 +8 为 CurrentMhz）
        try
        {
            int procs = Math.Clamp(Environment.ProcessorCount, 1, 128);
            byte[] buf = new byte[procs * 24];
            if (CallNtPowerInformation(11, IntPtr.Zero, 0, buf, (uint)buf.Length) == 0)
            {
                uint maxMhz = 0;
                for (int i = 0; i < procs; i++)
                {
                    uint cur = BitConverter.ToUInt32(buf, i * 24 + 8);
                    if (cur is >= 100 and <= 9500 && cur > maxMhz) maxMhz = cur;
                }
                if (maxMhz > 0) return maxMhz / 1000.0;
            }
        }
        catch { }
        return 0;
    }

    private void InitNetworkBaseline()
    {
        ReadNetworkBytes(out _, out _, out _, out _);
        _lastNetTick = Environment.TickCount64;
        _netInit = true;
    }

    private void Sample()
    {
        if (_stopping || _disposed) return;
        if (Interlocked.CompareExchange(ref _sampling, 1, 0) != 0) return;
        try
        {
            lock (_resourceLock)
            {
                if (_stopping || _disposed) return;
                SampleCore();
            }
        }
        finally { Volatile.Write(ref _sampling, 0); }
    }

    private void SampleCore()
    {
        SamplerDemand demand = CurrentDemand;
        bool wantGpu = (demand & SamplerDemand.Gpu) != 0;
        bool wantProcesses = (demand & SamplerDemand.Processes) != 0;
        bool wantFreq = (demand & SamplerDemand.CpuFrequency) != 0;

        double cpu = SampleCpu();
        double[]? cores = SampleCores();
        var mem = ReadMemory();
        var commit = ReadCommit();
        ReadNetwork(out double down, out double up);
        double gpu = wantGpu ? (_gpuCache?.ReadGpuUsage() ?? 0) : 0;
        string gpuName = _gpuCache?.Name ?? "";
        var disk = SampleDisk();
        SampleDriveSpaces();
        var disks = wantProcesses ? SampleDisks() : (_current.Disks ?? Array.Empty<DiskStat>());
        if (wantProcesses)
        {
            SampleTopProcesses();
            SamplePing();
        }
        string sysRoot = (System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
        _driveSpace.TryGetValue(sysRoot, out var sysSpace);
        ReadSystemBattery(out double? battPct, out bool battCharging);

        var status = new ReadingStatus
        {
            Cpu = Reading<double>.Of(cpu),
            Memory = mem.total > 0 ? Reading<double>.Of(mem.percent) : Reading<double>.Unavailable("内存读取失败"),
            Gpu = wantGpu ? Reading<double>.Of(gpu) : Reading<double>.NotRequested,
            Disk = disk.ok ? Reading<double>.Of(disk.active) : Reading<double>.Unavailable("磁盘计数器不可用"),
            NetDown = Reading<double>.Of(down),
            NetUp = Reading<double>.Of(up),
            Battery = battPct is { } bp ? Reading<double>.Of(bp) : Reading<double>.Unavailable("无内置电池"),
        };

        var snap = new MonitorSnapshot
        {
            CpuUsage = cpu,
            CoreUsages = cores,
            CpuFrequencyGHz = wantFreq ? SampleFreqGHz() : 0,
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
            BatteryPercent = battPct,
            BatteryCharging = battCharging,
            DownloadSpeed = down,
            UploadSpeed = up,
            SessionDownload = _sessionDown,
            SessionUpload = _sessionUp,
            TodayDownload = _todayDown,
            TodayUpload = _todayUp,
            LinkType = DetectLinkType(),
            UptimeHours = Environment.TickCount64 / 3600000.0,
            ProcessCount = wantProcesses ? SafeCount(_procCounter) : _current.ProcessCount,
            ThreadCount = wantProcesses ? SafeCount(_threadCounter) : _current.ThreadCount,
            CommitGB = commit.used,
            CommitLimitGB = commit.total,
            PingMs = Volatile.Read(ref _pingMs),
            Status = status,
        };
        Volatile.Write(ref _current, snap);
        if (_stopping || _disposed) return;
        Snapshot?.Invoke(snap);
    }

    private double SampleCpu()
    {
        if (_cpuOk && _cpuCounter is not null)
        {
            try
            {
                double v = _cpuCounter.NextValue();
                if (double.IsFinite(v) && v >= 0) return Math.Min(v, 100);
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
            int count = Math.Max(1, Environment.ProcessorCount);
            if (_coreBuf is null || _coreBuf.Length != count) _coreBuf = new double[count];
            if (_coreQuery.ReadIndexed(_coreBuf, 100))
            {
                var copy = new double[count];
                Array.Copy(_coreBuf, copy, count);
                return copy;
            }
            return null;
        }
        if (_coreCounters is null) return null;
        var legacy = new double[_coreCounters.Length];
        try
        {
            for (int i = 0; i < legacy.Length; i++)
            {
                double v = _coreCounters[i]?.NextValue() ?? 0;
                legacy[i] = double.IsFinite(v) ? Math.Clamp(v, 0, 100) : 0;
            }
        }
        catch { return null; }
        return legacy;
    }

    private readonly TimesFallback _cpuFallback = new();

    private static (double percent, double used, double total) ReadMemory()
    {
        if (GetPhysicalMemory(out ulong total, out ulong avail))
        {
            double totalGB = total / 1073741824.0;
            double usedGB = (total - avail) / 1073741824.0;
            double pct = total == 0 ? 0 : Math.Clamp((total - avail) * 100.0 / total, 0, 100);
            return (pct, usedGB, totalGB);
        }
        return (0, 0, 0);
    }

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
        try
        {
            if (c is null) return 0;
            float v = c.NextValue();
            return float.IsFinite(v) && v >= 0 ? (int)v : 0;
        }
        catch { return 0; }
    }

    private long _pingMs = -1;
    private long _lastPingTick;

    private void SamplePing()
    {
        long nowTick = Environment.TickCount64;
        if (_lastPingTick != 0 && nowTick - _lastPingTick < 3_000) return;
        _lastPingTick = nowTick;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                using var p = new Ping();
                var r = await p.SendPingAsync("223.5.5.5", 1500);
                long rtt = r.Status == IPStatus.Success ? r.RoundtripTime : -1;
                Volatile.Write(ref _pingMs, rtt);
            }
            catch
            {
                Volatile.Write(ref _pingMs, -1);
            }
        });
    }

    private void ReadNetwork(out double down, out double up)
    {
        down = up = 0;
        long nowTick = Environment.TickCount64;
        if (!_netInit)
        {
            ReadNetworkBytes(out _, out _, out _, out _);
            _lastNetTick = nowTick;
            _netInit = true;
            return;
        }

        double sec = (nowTick - _lastNetTick) / 1000.0;
        if (sec <= 0.05) return;

        ReadNetworkBytes(out _, out _, out long deltaIn, out long deltaOut);
        _lastNetTick = nowTick;

        // 超过 30 秒间隔（睡眠/休眠唤醒或系统挂起恢复）：仅刷新基线，不将挂起跳变计入瞬时网速或今日流量
        if (sec > 30.0)
        {
            _smoothDown = _smoothUp = 0;
            return;
        }

        double rawDown = deltaIn / sec;
        double rawUp = deltaOut / sec;
        _sessionDown += deltaIn;
        _sessionUp += deltaOut;

        var today = DateTime.Now.Date;
        if (today != _today)
        {
            _today = today;
            _todayDown = _todayUp = 0;
        }
        _todayDown += deltaIn;
        _todayUp += deltaOut;

        if (_lastTrafficSaveTick == 0 || nowTick - _lastTrafficSaveTick >= 30_000)
        {
            _lastTrafficSaveTick = nowTick;
            SaveTraffic();
        }

        _smoothDown = _smoothDown * 0.6 + rawDown * 0.4;
        _smoothUp = _smoothUp * 0.6 + rawUp * 0.4;
        down = _smoothDown;
        up = _smoothUp;
    }

    private DateTime _today = DateTime.Now.Date;
    private long _todayDown, _todayUp;

    private static string TrafficFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyIsland", "traffic.json");
    private long _lastTrafficSaveTick;

    private void LoadTraffic()
    {
        try
        {
            if (!System.IO.File.Exists(TrafficFile)) return;
            var json = System.IO.File.ReadAllText(TrafficFile);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            string date = root.GetProperty("date").GetString() ?? "";
            if (date == DateTime.Now.ToString("yyyy-MM-dd"))
            {
                _todayDown = root.GetProperty("down").GetInt64();
                _todayUp = root.GetProperty("up").GetInt64();
            }
        }
        catch { }
    }

    private void SaveTraffic()
    {
        try
        {
            var today = DateTime.Now.Date;
            if (today != _today)
            {
                _today = today;
                _todayDown = _todayUp = 0;
            }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(TrafficFile)!);
            string json = $"{{\"date\":\"{_today:yyyy-MM-dd}\",\"down\":{_todayDown},\"up\":{_todayUp}}}";
            string tmp = TrafficFile + ".tmp";
            System.IO.File.WriteAllText(tmp, json);
            System.IO.File.Move(tmp, TrafficFile, true);
        }
        catch { }
    }

    private long _sessionDown, _sessionUp;
    private string _linkType = "";
    private long _linkCheckedTick;

    private string DetectLinkType()
    {
        long nowTick = Environment.TickCount64;
        if (_linkCheckedTick != 0 && nowTick - _linkCheckedTick < 30_000) return _linkType;
        _linkCheckedTick = nowTick;
        string detected = "";
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (IsVirtualOrTunnelAdapter(nic)) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211)
                {
                    detected = "wifi";
                    break;
                }
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                    && nic.GetIPProperties().GatewayAddresses.Count > 0)
                {
                    detected = "ethernet";
                }
            }
        }
        catch { }
        _linkType = detected;
        return _linkType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    private static void ReadSystemBattery(out double? percent, out bool charging)
    {
        percent = null;
        charging = false;
        try
        {
            if (!GetSystemPowerStatus(out var sps)) return;
            if (sps.BatteryFlag != 255 && (sps.BatteryFlag & 128) != 0) return;
            if (sps.BatteryLifePercent <= 100)
            {
                percent = sps.BatteryLifePercent;
                charging = (sps.BatteryFlag & 8) != 0 || (sps.ACLineStatus == 1 && sps.BatteryLifePercent < 99);
            }
        }
        catch { }
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static bool GetPhysicalMemory(out ulong total, out ulong avail)
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m)) { total = m.ullTotalPhys; avail = m.ullAvailPhys; return true; }
        total = avail = 0; return false;
    }

    private readonly Dictionary<string, (long In, long Out)> _nicPrev = new(StringComparer.Ordinal);

    private static bool IsVirtualOrTunnelAdapter(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            return true;
        // PropVirtual = 53 / Unknown = 1（常见于 Wintun / Clash / Sing-box / WireGuard / OpenVPN TUN 网卡）
        if ((int)nic.NetworkInterfaceType is 53 or 1)
            return true;
        string desc = $"{nic.Name} {nic.Description}";
        return desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Clash", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Mihomo", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Sing-box", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase);
    }

    private void ReadNetworkBytes(out long inBytes, out long outBytes, out long deltaIn, out long deltaOut)
    {
        inBytes = outBytes = 0;
        deltaIn = deltaOut = 0;
        var current = new Dictionary<string, (long In, long Out)>(StringComparer.Ordinal);
        string preferred = PreferredNicId;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (!string.IsNullOrEmpty(preferred))
                {
                    if (nic.Id != preferred) continue;
                }
                else
                {
                    if (IsVirtualOrTunnelAdapter(nic)) continue;
                    bool hasGateway = nic.GetIPProperties().GatewayAddresses.Count > 0;
                    if (!hasGateway && nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                }
                var stats = nic.GetIPStatistics();
                if (stats is null) continue;
                inBytes += stats.BytesReceived;
                outBytes += stats.BytesSent;
                current[nic.Id] = (stats.BytesReceived, stats.BytesSent);
            }
        }
        catch { }

        foreach (var (id, v) in current)
            if (_nicPrev.TryGetValue(id, out var prev))
            {
                long dIn = v.In - prev.In, dOut = v.Out - prev.Out;
                if (dIn > 0) deltaIn += dIn;
                if (dOut > 0) deltaOut += dOut;
            }
        _nicPrev.Clear();
        foreach (var kv in current) _nicPrev[kv.Key] = kv.Value;
    }

    private string _preferredNicId = "";
    public string PreferredNicId
    {
        get => Volatile.Read(ref _preferredNicId);
        set => Volatile.Write(ref _preferredNicId, value ?? "");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAndWait();
        lock (_resourceLock)
        {
            SaveTraffic();
            _timer.Dispose();
            _cpuCounter?.Dispose(); _cpuCounter = null;
            _freqCounter?.Dispose(); _freqCounter = null;
            _procCounter?.Dispose(); _procCounter = null;
            _threadCounter?.Dispose(); _threadCounter = null;
            _diskActiveCounter?.Dispose(); _diskActiveCounter = null;
            _diskReadCounter?.Dispose(); _diskReadCounter = null;
            _diskWriteCounter?.Dispose(); _diskWriteCounter = null;
            foreach (var ch in _diskChannels) ch?.Dispose();
            _diskChannels = Array.Empty<DiskChannel>();
            if (_coreCounters is not null)
            {
                foreach (var c in _coreCounters) { try { c?.Dispose(); } catch { } }
                _coreCounters = null;
            }
            _coreQuery?.Dispose(); _coreQuery = null;
            _gpuCache?.Dispose(); _gpuCache = null;
        }
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
