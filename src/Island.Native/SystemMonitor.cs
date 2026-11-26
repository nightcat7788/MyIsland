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
/// - 明细按需：只有监控页才用的读数（进程榜/逐盘行/ping/进程线程数/频率/盘空间）面板展开时才采，见 SetDetailWanted
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
    // "面板才用得到"的明细要不要采（默认 false）。UI 线程写、采样线程读 ⇒ volatile。
    private volatile bool _detailWanted;
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

    // 运行时长口径：**Environment.TickCount64**（系统开机时长）。
    // 曾有一段 PDH 的 System Up Time 计数器（不含休眠），但它**从未被读取** —— 建了不读、还不释放，
    // 于是文档按它写、代码按 TickCount64 跑，两边对不上（2026-11-26 清掉，文档已改正）。
    // 现在两端（Windows / macOS）**同一实现**、同一口径，数值可比。

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
    /// 告诉采样器"展开面板要不要明细"：面板展开时 true，面板释放后 false（宿主调用，默认 false）。
    /// false 时每帧只采收起态用得到的读数（CPU/核心柱/内存%/GPU%/磁盘活动%/网速+流量累计），
    /// 跳过只有监控页才看的部分。2026-11-24 本机实测（约 375 个进程，1000ms 间隔，进程 CPU）折合到每帧：
    /// System\Processes/Threads 两个计数器不节流、每帧都读，各 5.7–8.4ms；进程榜 19–28ms/次 ÷ 3 秒节流；
    /// ping 约 1ms/次 ÷ 3 秒。整帧实测：改前 21–34ms/帧，明细关掉后 1.0–3.1ms/帧（剩下的主要是 GPU 通配符查询 ~0.9ms）。
    /// 网速与今日/会话流量**不受它影响**：流量是从网卡累计计数逐帧做差累加的，少采一帧就永久少一截。
    /// 契约：
    ///  · 任意线程可调；同值重复调用是空操作；Start() 之前、Stop()/Dispose() 之后调用都安全（不补采）。
    ///  · false→true 的上升沿立刻在后台线程补采一帧，面板不必等下一个计时器节拍；
    ///    补采到达前快照里继续带着上一份进程榜/逐盘行。
    ///  · 进程榜（CPU）是两次采样做差分出来的，展开后的第一份榜分三种情况（见 SampleTopProcesses）：
    ///      上一份基线还在 3 秒 + 1.5 个采样间隔之内 ⇒ 补采那一帧直接出新榜（窗口 = 这段时间）；
    ///      更久（含头一回要明细）⇒ 补采那一帧只记基线，ProcFirstWindow 秒后（0.6 秒）再补一帧出新榜，
    ///      这期间显示上一份榜。**不拿"收起期间的平均占用"充数**：一个收起时跑满、展开时已经停了的进程，
    ///      不该在展开后还排在榜首（2026-11-24 修正，实测 12 秒收起后它以 6.6% 排在第一，3 秒后才消失）。
    ///  · 明细自身的节流不变（进程榜 3 秒一采、ping 3 秒一次）：榜上的占用是最近 3 秒的平均，累计 CPU 时间 ÷ 真实墙钟，不会出假峰值。
    ///  · 明细关着时，快照里只有监控页才用的字段是"上一份或 0"：进程榜/逐盘行/ping/盘空间 = 上一份
    ///    （首次补采之前是初始值：空数组 / -1 / 0）；进程数/线程数/CPU 频率/磁盘读写速度 = 0
    ///    （监控页本来就按 0 = 无数据显示）。提交内存/已用内存/显卡名/运行时长与收起态读数出自同一次调用，照常填。
    /// </summary>
    public void SetDetailWanted(bool wanted)
    {
        bool was = _detailWanted;
        // 上升沿先于"要明细"置位：之后任何看到 _detailWanted == true 的采样，一定也看得到这个沿
        if (wanted && !was) _procEdge = true;
        _detailWanted = wanted;
        // 只在上升沿补采：面板刚展开，不能让它干等一个采样间隔
        if (!wanted || was || _stopping || _disposed) return;
        // 初始化没完成：后台初始化末尾的首帧本来就会采（那时标志已经是 true）
        if (System.Threading.Volatile.Read(ref _initState) == 0) return;
        if (!_timer.Enabled) return;   // 宿主停表了：停表期间不产快照
        _ = System.Threading.Tasks.Task.Run(SampleOnDemand);
    }

    /// <summary>
    /// 上升沿补采（线程池线程，绝不占用调用 SetDetailWanted 的 UI 线程）。
    /// 撞上在途的计时器采样就等它结束再采：那一帧可能是在"不要明细"时开的头、不带明细，
    /// 就此放弃的话面板得等到下一个节拍。最多等到"下一个节拍早该来了"为止（封顶 2 秒）——
    /// 那之后计时器自己会带上明细，再等没有意义；等待期间线程在 Sleep，不占 CPU。
    /// 互斥仍然只靠 Sample() 里的 _sampling。
    /// </summary>
    private void SampleOnDemand()
    {
        try
        {
            SampleSoon();
            // 这一帧只给进程榜记了基线（明细关了很久 / 头一回要明细）：等短窗口过去再补一帧，
            // 那一次差分出来的才是"展开后的第一份榜"，不用等 3 秒节流。
            // 等待期间计时器先把榜出了（_procPending 已清）就不再采；面板已经收起 / 正在关闭也不采。
            if (!_procPending || _stopping || !_detailWanted) return;
            var wait = Stopwatch.StartNew();
            double limit = ProcFirstWindow * 1000 + 60;
            while (_procPending && !_stopping && _detailWanted && wait.ElapsedMilliseconds < limit)
                System.Threading.Thread.Sleep(5);
            if (_procPending && !_stopping && _detailWanted) SampleSoon();
        }
        catch (Exception ex)
        {
            if (!_stopping) LogSampleFailure(ex);   // 与 OnTick 同样的留痕
        }
    }

    /// <summary>采一帧；撞上在途采样就等它结束再采（最多等到下一个节拍早该来了，封顶 2 秒）。正在关闭 / 已停表时安静返回。</summary>
    private void SampleSoon()
    {
        var wait = Stopwatch.StartNew();
        double limit = Math.Min(2000, _timer.Interval);
        while (!Sample() && !_stopping && wait.ElapsedMilliseconds < limit)
            System.Threading.Thread.Sleep(5);
    }

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

    /// <summary>
    /// CPU 占用计数器：优先 "Processor Information" 的 **"% Processor Utility"**
    /// （Win8+，任务管理器口径，按实际频率折算）；回退 "% Processor Time"；再回退 legacy 类别。
    /// LiteMonitor 同款回退链。
    /// </summary>
    private static PerformanceCounter? CreateCpuLoadCounter()
    {
        string[][] candidates =
        {
            new[] { "Processor Information", "% Processor Utility", "_Total" },
            new[] { "Processor Information", "% Processor Time", "_Total" },
            new[] { "Processor", "% Processor Time", "_Total" },
        };
        foreach (string[] c in candidates)
        {
            try
            {
                var counter = new PerformanceCounter(c[0], c[1], c[2], true);
                counter.NextValue();   // 预热：第一次 NextValue 永远返回 0
                return counter;
            }
            catch { /* 换下一个候选 */ }
        }
        return null;
    }

    private void InitCpu()
    {
        try
        {
            // CPU 占用：LiteMonitor 口径（2026-11-26）——
            // 用 "Processor Information" 的 **"% Processor Utility"**：它按**实际频率**折算，与任务管理器一致。
            // 旧的 "% Processor Time" 是"占用时间占比"，在会睿频的机器上**只有它的一半左右**
            // （本机实测：Utility 47–54% vs Time 26–28%）⇒ 岛体显示的 CPU 占用明显偏低。
            // 回退链同 LiteMonitor：Utility → (Processor Information) Time → (legacy Processor) Time。
            _cpuCounter = CreateCpuLoadCounter();
            if (_cpuCounter is null) throw new InvalidOperationException("CPU 占用计数器不可用");
            int cores = Environment.ProcessorCount;
            // 核心柱：一次通配符查询读全部逻辑核（旧的逐核 PerformanceCounter 每秒 14 次 PDH collect）。
            // 通配符开不起来时再退回逐核计数器。
            // 核心柱也用 Utility（与总占用同口径，否则"柱子的均值"和"总占用"对不上）。
            // "Processor Information" 的实例名是 "0,0".."0,N"（组号,逻辑核号），另有 "0,_Total" 要跳过。
            try
            {
                var query = new PdhWildcardQuery();
                if (query.Open(@"\Processor Information(*)\% Processor Utility")) _coreQuery = query;
                else query.Dispose();
            }
            catch { _coreQuery = null; }
            // 回退：legacy "Processor" 类别只有 "% Processor Time"
            if (_coreQuery is null)
            {
                try
                {
                    var legacy = new PdhWildcardQuery();
                    if (legacy.Open(@"\Processor(*)\% Processor Time")) _coreQuery = legacy;
                    else legacy.Dispose();
                }
                catch { _coreQuery = null; }
            }
            if (_coreQuery is null)
            {
                // 逐核计数器：按类别里**实际存在的数字实例**建，而不是 Environment.ProcessorCount ——
                // 后者是进程可见的核数，受 CPU 分组/亲和性影响，可能与 PDH 的实例数不一致
                // （一致时就等价，但不一致时按实例数建不会丢核）。
                try
                {
                    string[] instances = new PerformanceCounterCategory("Processor").GetInstanceNames();
                    var numeric = new List<string>();
                    foreach (string inst in instances)
                        if (int.TryParse(inst, out _)) numeric.Add(inst);
                    numeric.Sort((a, b) => int.Parse(a).CompareTo(int.Parse(b)));
                    _coreCounters = new PerformanceCounter[numeric.Count];
                    for (int i = 0; i < numeric.Count; i++)
                    {
                        _coreCounters[i] = new PerformanceCounter("Processor", "% Processor Time", numeric[i], true);
                        _coreCounters[i].NextValue();
                    }
                }
                catch
                {
                    _coreCounters = new PerformanceCounter[cores];
                    for (int i = 0; i < cores; i++)
                    {
                        _coreCounters[i] = new PerformanceCounter("Processor", "% Processor Time", i.ToString(), true);
                        _coreCounters[i].NextValue();
                    }
                }
            }
            _cpuOk = true;
        }
        catch
        {
            _cpuOk = false;
        }
        // CPU 实际频率：优先 **"Actual Frequency"**（直接就是 MHz，LiteMonitor 同款）。
        // 本机实测：Actual Frequency ≈ 2355–2585 MHz，而旧的估算路径（% Processor Performance × 基频）
        // 会低 15–20%（睿频越高差得越多）。
        try
        {
            _actualFreqCounter = new PerformanceCounter("Processor Information", "Actual Frequency", "_Total", true);
            _actualFreqCounter.NextValue();
        }
        catch { _actualFreqCounter = null; }
        // 估算路径（Actual Frequency 不可用时）：Processor Performance(% of 最大值) × 基频
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
    private PerformanceCounter? _actualFreqCounter;
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

    /// <summary>
    /// 磁盘活动时间%（0..100 封顶，PDH 瞬时可能超 100）与读/写速度。
    /// 读/写速度只有监控页的回退行在用：明细不要时跳过这两个计数器（每个 NextValue 是一次 PDH collect，
    /// 2026-11-24 本机实测 ≈ 35–60µs）。
    /// </summary>
    private (double active, double read, double write) SampleDisk(bool detail)
    {
        if (_diskActiveCounter is null) return (0, 0, 0);
        try
        {
            return (Math.Clamp(_diskActiveCounter.NextValue(), 0, 100),
                    detail ? Math.Max(0, _diskReadCounter?.NextValue() ?? 0) : 0,
                    detail ? Math.Max(0, _diskWriteCounter?.NextValue() ?? 0) : 0);
        }
        catch { return (0, 0, 0); }
    }

    // 各盘空间：30 秒查一次（DriveInfo 便宜但没必要每秒）
    private DateTime _drivesChecked = DateTime.MinValue;
    private readonly Dictionary<string, (double used, double total)> _driveSpace = new(StringComparer.OrdinalIgnoreCase);
    // 最近一份逐盘行：明细不要时沿用（面板刚展开、补采还没到的那几毫秒里监控页仍有行可画）
    private DiskStat[] _disks = Array.Empty<DiskStat>();

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
    // 面板刚展开、还没被进程榜消费的上升沿：消费它的那一帧不受 3 秒节流（UI 线程置位、采样线程清零 ⇒ volatile）
    private volatile bool _procEdge;
    // 这一帧只记了基线、还没出榜：下一次差分只用等 ProcFirstWindow 秒（SampleOnDemand 会到点补一帧）
    private volatile bool _procPending;
    // 记基线到出第一份榜的窗口（秒）。再短：进程 CPU 时间按 ~15.6ms 的时钟片记账，一个时钟片就能占掉榜的一大截
    // （0.6 秒时折合 0.19%，14 核），而且下面做差分要求墙钟 > 0.5 秒，得留出余量；
    // 再长：面板约 0.3 秒就展开到位（_open 弹簧），之后每多一秒都是盯着上一份旧榜等。
    private const double ProcFirstWindow = 0.6;
    // 进程身份：PID 会被复用，差分时必须校验"还是不是同一个进程"
    private Dictionary<int, (string Name, TimeSpan Cpu, DateTime Start)> _procPrev = new();
    private TopProcess[] _topProcesses = Array.Empty<TopProcess>();
    private TopProcess[] _topProcessesByMem = Array.Empty<TopProcess>();

    /// <summary>
    /// 进程榜（CPU）：与上一份基线做差分，窗口 = 两帧之间的真实墙钟。展开面板后的第一份榜：
    /// · 上升沿那一帧不受 3 秒节流：上一份基线还在"3 秒 + 1.5 个采样间隔"之内就直接出榜（窗口 = 这段时间）；
    /// · 更久、或头一回要明细：那一帧只记基线不出 CPU 榜（_procPending），ProcFirstWindow 秒后再差分出榜，
    ///   这 0.6 秒里界面显示的是上一份榜。不拿"收起期间的平均占用"充数（2026-11-24 修正：实测收起 12 秒
    ///   期间跑满的进程，展开后以 6.6% 排在榜首，3 秒后才消失；头一回展开则空白约 4 秒）。
    /// 内存榜是瞬时值，不受窗口影响，每一帧照常更新。
    /// </summary>
    private void SampleTopProcesses()
    {
        var now = DateTime.Now;
        bool edge = _procEdge; _procEdge = false;
        bool hasBase = _procChecked != DateTime.MinValue;
        double wall = hasBase ? (now - _procChecked).TotalSeconds : 0;
        // 两帧之间的最小间隔：常规 3 秒；刚记完基线 = 短窗口；上升沿只要超过差分所需的最小窗口（见下面的 wall > 0.5）
        double minGap = _procPending ? ProcFirstWindow : edge ? 0.6 : 3;
        if (hasBase && wall < minGap) return;
        // 只记基线、这一帧不出 CPU 榜：① 头一回（没有上一份基线）② 上一份基线比"正常采样节奏"还旧。
        // ② 的判断不能看 _procPending：展开 0.3 秒就收起、隔 10 秒再展开时它还是 true，照常做差一样是整段时间的平均
        // （2026-11-24 实测：这期间跑满的进程以 6.8% 排在榜首）。连续采样时两帧间隔 ≤ 采样间隔，远到不了这条线；
        // 到了线的只可能是明细关了很久、或者机器睡了一觉。
        bool baselineOnly = !hasBase || wall > 3.0 + _timer.Interval / 1000.0 * 1.5;
        _procChecked = now;
        _procPending = baselineOnly;

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
                if (!baselineOnly && wall > 0.5 && prev.TryGetValue(p.Id, out var old) && IsSameProcess(old, name, start))
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
        // 只记基线的那一帧不动 CPU 榜：从展开到短窗口出榜之间显示的仍是上一份榜
        if (!baselineOnly)
        {
            _topProcesses = tops.Count > 0
                ? tops.OrderByDescending(t => t.CpuPercent).Take(5).ToArray()
                : Array.Empty<TopProcess>();
        }
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

    /// <summary>当前 CPU 平均实际频率 GHz：优先真实读数（Actual Frequency, MHz），回退估算。</summary>
    private double SampleFreqGHz()
    {
        if (_actualFreqCounter is not null)
        {
            try
            {
                float mhz = _actualFreqCounter.NextValue();
                if (mhz > 0 && mhz < 10000) return mhz / 1000.0;
            }
            catch { _actualFreqCounter = null; }
        }
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
    /// 后台首帧采样、计时器 tick、上升沿补采（SampleOnDemand）可能撞在一起，而 PDH 缓冲、_nicPrev、_driveSpace
    /// 都是无锁共享状态 ⇒ 重入时直接跳过本轮（共享字典的访问也就只发生在采样线程内）。
    /// 返回 false = 本轮被跳过（正在关闭，或已有采样在跑）。
    /// </summary>
    private bool Sample()
    {
        if (_stopping) return false;
        if (System.Threading.Interlocked.CompareExchange(ref _sampling, 1, 0) != 0) return false;
        try { SampleCore(_detailWanted); }
        finally { System.Threading.Volatile.Write(ref _sampling, 0); }
        return true;
    }

    /// <summary>采样主体。detail = 本帧要不要采"只有监控页才看"的明细（见 SetDetailWanted）。</summary>
    private void SampleCore(bool detail)
    {
        double cpu = SampleCpu();
        double[]? cores = SampleCores();
        var mem = ReadMemory();
        var commit = ReadCommit();
        ReadNetwork(out double down, out double up);   // 不看 detail：流量累计必须每帧做差
        double gpu = _gpuCache?.ReadGpuUsage() ?? 0;
        string gpuName = _gpuCache?.Name ?? "";
        var disk = SampleDisk(detail);
        // 明细不要时整段跳过：各函数自己的节流（盘空间 30 秒 / 进程榜 3 秒 / ping 3 秒）只在要明细时生效
        if (detail)
        {
            SampleDriveSpaces();
            _disks = SampleDisks();
            SampleTopProcesses();
            SamplePing();
        }
        // 系统盘空间（来自全体驱动器字典）
        string sysRoot = (System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
        _driveSpace.TryGetValue(sysRoot, out var sysSpace);

        var snap = new MonitorSnapshot
        {
            CpuUsage = cpu,
            CoreUsages = cores,
            CpuFrequencyGHz = detail ? SampleFreqGHz() : 0,   // 以下几项只有监控页在用：明细不要时不读
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
            Disks = _disks,                 // 明细不要时沿用上一份逐盘行
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
            ProcessCount = detail ? SafeCount(_procCounter) : 0,
            ThreadCount = detail ? SafeCount(_threadCounter) : 0,
            CommitGB = commit.used,
            CommitLimitGB = commit.total,
            PingMs = _pingMs,
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
                // 长度按**数据里最大的核号**定，不用 Environment.ProcessorCount：
                // 后者是进程可见的核数（受 CPU 分组/亲和性影响），按数据定更贴合实际。
                var indexed = new Dictionary<int, double>();
                foreach (var (name, value) in values)
                {
                    // 实例名两种形态："Processor Information" 是 "0,3"（组号,逻辑核号），
                    // legacy "Processor" 是 "3"；"_Total" / "0,_Total" 这类会被跳过。
                    string idxText = name;
                    int comma = name.IndexOf(',');
                    if (comma >= 0) idxText = name.Substring(comma + 1);
                    if (!int.TryParse(idxText, out int index)) continue;
                    indexed[index] = Math.Clamp(value, 0, 100);
                }
                if (indexed.Count == 0) return null;
                int max = indexed.Keys.Max();
                var result = new double[Math.Max(1, max + 1)];
                foreach (var kv in indexed) result[kv.Key] = kv.Value;
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
                _pingMs = r.Status == System.Net.NetworkInformation.IPStatus.Success
                    ? r.RoundtripTime : -1;
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

        // 计数器回退 / 换网卡：重新建立基线（会话/今日累计继续，不回退）
        if (inBytes < _lastIn || outBytes < _lastOut)
        {
            _lastIn = inBytes; _lastOut = outBytes; _lastNetTime = now;
            return;
        }

        // 速率与累计都基于"本轮增量"：新增网卡的历史累计量不会混进来
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
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(TrafficFile)!);
            string json = $"{{\"date\":\"{DateTime.Now:yyyy-MM-dd}\",\"down\":{_todayDown},\"up\":{_todayUp}}}";
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
            // NetworkInterface 无需 WMI：拿它判断（走 10 秒网卡列表缓存，不再每次枚举）
            foreach (var nic in GetNics())
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

    // 网卡列表缓存：NetworkInterface.GetAllNetworkInterfaces() 每次都要枚举全部适配器、给每张网卡构造一整套属性对象，
    // 2026-11-24 本机实测（6 张网卡、2 张 Up）单次 3.8ms CPU / 13ms 墙钟；枚举完之后逐网卡取字节计数只要 0.2ms ——
    // 以前每帧枚举一次，网速读数的开销 95% 花在"重新列一遍网卡"上。
    // 能缓存的原因（.NET 8 源码 SystemNetworkInterface / SystemIPInterfaceStatistics）：NetworkInterface 对象里的
    // Id / OperationalStatus / 网卡类型 / 网关列表都是构造那一刻的快照，只有 GetIPStatistics() 每次调用都按接口索引现查
    // （GetIfEntry2）⇒ 缓存对象读到的字节计数永远是实时值；接口已经不在了则抛 NetworkInformationException。
    // 网卡增减（插拔 / VPN 连断）相对采样间隔是慢事件 ⇒ 列表最多 10 秒刷新一次；
    // 代价：这 10 秒里新出现 / 刚连上的网卡要等下一次刷新才计入（首次只建基线，见 _nicPrev）。
    // 只在采样路径上访问（_sampling 互斥 + 初始化先于首帧），不需要加锁。
    private NetworkInterface[] _nics = Array.Empty<NetworkInterface>();
    private DateTime _nicsRefreshed;      // 上次成功枚举的时刻（MinValue = 还没枚举过）
    private bool _nicsStale;              // true = 下一帧立即重枚举（有网卡读失败 / 枚举结果为空）
    private static readonly TimeSpan NicListRefresh = TimeSpan.FromSeconds(10);

    /// <summary>取网卡列表（10 秒缓存）。枚举失败或结果为空时沿用旧列表，并让下一帧立刻重试。</summary>
    private NetworkInterface[] GetNics()
    {
        if (!_nicsStale && DateTime.Now - _nicsRefreshed < NicListRefresh) return _nics;
        _nicsStale = false;
        try
        {
            var fresh = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            if (fresh.Length == 0) _nicsStale = true;          // 空列表不可信（回环网卡永远在）：保留旧列表，下一帧重试
            else { _nics = fresh; _nicsRefreshed = DateTime.Now; }
        }
        catch { _nicsStale = true; }                            // 枚举失败：沿用旧列表，下一帧重试
        return _nics;
    }

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
        // 网卡列表走 10 秒缓存；GetIPStatistics() 每次都现查，字节计数是实时的
        // （2026-11-24 实测：缓存期间读数持续增长，与同一轮里重新枚举的读数只差两次读取之间的一百多字节）
        foreach (var nic in GetNics())
        {
            // 单张网卡读失败（缓存的这 10 秒里网卡被拔掉 / VPN 断开：按接口索引查不到就抛）只跳过它，
            // 并让列表下一帧立即重枚举。以前一个 try 包住整个循环：一张读不到，排在它后面的网卡这一帧全丢。
            // 计入流量的网卡掉线 ⇒ 绝对计数之和变小，调用方 ReadNetwork 会按"换网卡"重建基线。
            try
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                // 排除虚拟网卡（LiteMonitor 同款清单）：Hyper-V / VMware / VirtualBox / WSL / 隧道 / 容器。
                // "有网关"那一条挡不住它们（虚拟交换机、Docker 网桥都有网关）⇒ 会把它们的流量算进来，
                // 网速虚高。实体网卡名里出现这些特征词的一律不计。
                if (IsVirtualAdapter(nic)) continue;
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
            catch { _nicsStale = true; }
        }

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

    /// <summary>虚拟网卡名字特征（LiteMonitor 的清单 + 容器/隧道）。命中即不计入网速。</summary>
    private static readonly string[] VirtualAdapterHints =
    {
        "hyper-v", "vethernet", "vmware", "virtualbox", "vbox", "wsl",
        "tailscale", "zerotier", "docker", "wintun", "tap-", "npcap",
    };

    /// <summary>是不是虚拟网卡（名字/描述命中特征词）。</summary>
    private static bool IsVirtualAdapter(System.Net.NetworkInformation.NetworkInterface nic)
    {
        string name = (nic.Name + " " + nic.Description).ToLowerInvariant();
        foreach (string hint in VirtualAdapterHints)
            if (name.Contains(hint)) return true;
        return false;
    }

    /// <summary>手动选择的网卡 Id（空=自动聚合所有物理网卡；设置中心"网卡"选项）。</summary>
    public string PreferredNicId { get; set; } = "";

    public void Dispose()
    {
        if (_disposed) return;      // 幂等：重复关闭不重复释放
        _disposed = true;
        StopAndWait();              // 先停表并等在途采样结束，避免采样撞上已释放的 PDH 句柄
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
