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

    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter[]? _coreCounters;
    private bool _cpuOk;

    private long _lastIn, _lastOut;
    private DateTime _lastNetTime;
    private double _smoothDown, _smoothUp;
    private bool _netInit;

    private readonly CounterSampleCache _gpuCache = new();

    public MonitorSnapshot Current { get; private set; } = new();

    /// <summary>每次采样完成触发，参数是不可变快照。</summary>
    public event Action<MonitorSnapshot>? Snapshot;

    private PerformanceCounter? _procCounter, _threadCounter;

    public SystemMonitor(int intervalMs = 1000)
    {
        InitCpu();
        InitDisk();
        try
        {
            _procCounter = new PerformanceCounter("System", "Processes", true);
            _threadCounter = new PerformanceCounter("System", "Threads", true);
            _procCounter.NextValue(); _threadCounter.NextValue();
        }
        catch { _procCounter = _threadCounter = null; }
        _timer = new System.Timers.Timer(intervalMs);
        _timer.Elapsed += OnTick;
    }

    /// <summary>运行时调整采样间隔（设置中心"监控刷新率"）。</summary>
    public void SetInterval(int intervalMs) => _timer.Interval = Math.Clamp(intervalMs, 250, 10000);

    public void Start()
    {
        LoadTraffic();   // 恢复今日累计（重启不归零）
        InitNetworkBaseline();
        _timer.Start();
        Sample(); // 立即给一个初值
    }

    public void Stop() => _timer.Stop();

    private void OnTick(object? sender, ElapsedEventArgs e)
    {
        try { Sample(); }
        catch { /* 采样失败保留上一帧，绝不让采样线程崩掉 */ }
    }

    private void InitCpu()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            _cpuCounter.NextValue(); // 预热
            int cores = Environment.ProcessorCount;
            _coreCounters = new PerformanceCounter[cores];
            for (int i = 0; i < cores; i++)
            {
                _coreCounters[i] = new PerformanceCounter("Processor", "% Processor Time", i.ToString(), true);
                _coreCounters[i].NextValue();
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
            _diskActiveCounter = _diskReadCounter = _diskWriteCounter = null;   // 计数器缺失则显示 0，不影响其余读数
        }
        // 逐盘实例（多硬盘监控页每盘一行）
        try
        {
            var cat = new PerformanceCounterCategory("PhysicalDisk");
            var channels = new List<DiskChannel>();
            foreach (string inst in cat.GetInstanceNames())
            {
                if (inst.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
                // 实例名里的卷符："0 C: D:" → ["C:","D:"]（跨区卷取最后一个作显示名）
                string letter = "";
                for (int i = 0; i + 1 < inst.Length; i++)
                    if (char.IsLetter(inst[i]) && inst[i + 1] == ':')
                        letter = char.ToUpperInvariant(inst[i]) + ":";
                if (letter.Length == 0) continue;   // 无挂载卷的物理盘跳过
                try
                {
                    var ch = new DiskChannel { Name = letter };
                    ch.Active = new PerformanceCounter("PhysicalDisk", "% Disk Time", inst, true);
                    ch.Read = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", inst, true);
                    ch.Write = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", inst, true);
                    ch.Active.NextValue(); ch.Read.NextValue(); ch.Write.NextValue();
                    channels.Add(ch);
                }
                catch { /* 单实例不可读跳过 */ }
            }
            _diskChannels = channels.ToArray();
        }
        catch { _diskChannels = Array.Empty<DiskChannel>(); }
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
    private Dictionary<int, (string Name, TimeSpan Cpu)> _procPrev = new();
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
        var next = new Dictionary<int, (string, TimeSpan)>(256);
        var tops = new List<TopProcess>(64);
        var mems = new List<TopProcess>(64);
        int coresN = Math.Max(1, Environment.ProcessorCount);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string name = p.ProcessName;
                if (name is "Idle" or "System" or "Registry" or "Memory Compression") continue;
                var cpu = p.TotalProcessorTime;
                long ws = 0;
                try { ws = p.WorkingSet64; } catch { /* 权限不足：内存显示 0 */ }
                next[p.Id] = (name, cpu);
                string exePath = "";
                if (wall > 0.5 && prev.TryGetValue(p.Id, out var old))
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
        if (tops.Count > 0)
            _topProcesses = tops.OrderByDescending(t => t.CpuPercent).Take(5).ToArray();
        if (mems.Count > 0)
            _topProcessesByMem = mems.OrderByDescending(t => t.MemoryMB).Take(5).ToArray();
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
        ReadNetworkBytes(out _lastIn, out _lastOut);
        _lastNetTime = DateTime.Now;
        _netInit = true;
    }

    private void Sample()
    {
        double cpu = SampleCpu();
        double[]? cores = SampleCores();
        var mem = ReadMemory();
        var commit = ReadCommit();
        ReadNetwork(out double down, out double up);
        double gpu = _gpuCache.ReadGpuUsage();
        string gpuName = _gpuCache.Name;
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
            PingMs = _pingMs,
        };
        lock (_gate) Current = snap;
        Snapshot?.Invoke(snap);
    }

    private double SampleCpu()
    {
        if (_cpuOk && _cpuCounter is not null)
        {
            double v = _cpuCounter.NextValue();
            if (v >= 0 && v <= 100) return v;
        }
        return _cpuFallback.Read();
    }

    private double[]? SampleCores()
    {
        if (!_cpuOk || _coreCounters is null) return null;
        var result = new double[_coreCounters.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = Math.Clamp(_coreCounters[i].NextValue(), 0, 100);
        return result;
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
                _pingMs = r.Status == System.Net.NetworkInformation.IPStatus.Success
                    ? r.RoundtripTime : -1;
            }
            catch { /* 离线/被防火墙拦：保持上一帧 */ }
        });
    }

    private void ReadNetwork(out double down, out double up)
    {
        down = up = 0;
        ReadNetworkBytes(out long inBytes, out long outBytes);
        var now = DateTime.Now;
        if (!_netInit) { _lastIn = inBytes; _lastOut = outBytes; _lastNetTime = now; _netInit = true; return; }

        double sec = (now - _lastNetTime).TotalSeconds;
        if (sec <= 0) return;

        // 计数器回退 / 换网卡：重新建立基线（会话/今日累计继续，不回退）
        if (inBytes < _lastIn || outBytes < _lastOut)
        {
            _lastIn = inBytes; _lastOut = outBytes; _lastNetTime = now;
            return;
        }

        double rawDown = (inBytes - _lastIn) / sec;
        double rawUp = (outBytes - _lastOut) / sec;
        long dDown = inBytes - _lastIn, dUp = outBytes - _lastOut;
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
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(TrafficFile)!);
            string json = $"{{\"date\":\"{DateTime.Now:yyyy-MM-dd}\",\"down\":{_todayDown},\"up\":{_todayUp}}}";
            System.IO.File.WriteAllText(TrafficFile, json);
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

    private void ReadNetworkBytes(out long inBytes, out long outBytes)
    {
        // 用 .NET NetworkInterface（底层 GetIfTable2，64 位计数器，无 4GB 回绕问题）
        inBytes = outBytes = 0;
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
            }
        }
        catch { /* 单次失败：调用方按 0 处理并重建基线 */ }
    }

    /// <summary>手动选择的网卡 Id（空=自动聚合所有物理网卡；设置中心"网卡"选项）。</summary>
    public string PreferredNicId { get; set; } = "";

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
        _cpuCounter?.Dispose();
        _freqCounter?.Dispose();
        _diskActiveCounter?.Dispose();
        _diskReadCounter?.Dispose();
        _diskWriteCounter?.Dispose();
        foreach (var ch in _diskChannels) ch.Dispose();
        if (_coreCounters is not null) foreach (var c in _coreCounters) c.Dispose();
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
