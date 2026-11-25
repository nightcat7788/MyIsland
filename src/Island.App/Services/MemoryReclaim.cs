using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace Island.App.Services;

/// <summary>
/// 「用完即还」：展开面板 / 设置窗口释放之后，等界面静下来，做一次压缩式回收，再把工作集交还系统。
///
/// 为什么需要它（2026-11-24）：挂机几小时后工作集涨到 276 MB。面板和设置窗口都是按需创建、用完释放的，
/// 但"释放"只是不再引用——它们的垃圾、它们碰过的页还留在进程里；岛体收着的时候又几乎没有分配，
/// 第 2 代回收迟迟不来，这些内存就一直占着。
///
/// 它做什么 / 不做什么（别期待太多）：
///   - 降的是**工作集**（任务管理器里看到的那个数）。EmptyWorkingSet 把进程的页全部移出工作集，
///     包括还活着的对象所在的页；下次碰到这些页是一次软缺页——不用读盘，但下一次展开要把它们摸一遍，略慢。
///   - 提交内存只在"压缩之后 GC 能把段还给系统"的那部分变小；碎块搬不动、段还半满的那部分照旧占着。
///   - 回收不了的东西照样回收不了：进程级的 DirectWrite 字体缓存、静态缓存、被钉住的大对象。
///   - 回收本身是阻塞的：停掉所有托管线程做完一次整堆压缩，耗时长短取决于存活对象数。所以只在界面空闲时做，
///     并且排在 Dispatcher 的 Background 优先级上（输入、渲染都走完了才轮到它）。
///
/// 接入方式：面板释放（IslandWindow.ReleasePanel）、设置窗口关闭之后各 <see cref="Request"/> 一次；
/// 宿主用 <see cref="IsIdle"/> 回答"界面现在静不静"。
/// </summary>
public static class MemoryReclaim
{
    /// <summary>
    /// 宿主提供的"界面现在静不静"：请求到点后在 UI 线程上问一次，true 才回收
    /// （宿主没有 Application 的时候——比如测试进程——在计时器线程上问）。
    /// 返回 false 不丢请求：隔一会儿（默认 2 秒）再问，直到空闲，或者等到上限（到点后 10 分钟）为止。
    /// 没设（null）按空闲处理；判定抛异常按"不空闲"处理——回收是锦上添花的事，不能让宿主的异常
    /// 跑到线程池线程或 Dispatcher 操作上（前者 = 进程直接终止）。
    /// </summary>
    public static Func<bool>? IsIdle { get; set; }

    /// <summary>
    /// 请求一次"用完即还"：在 <paramref name="delay"/> 之后（默认 5 秒）界面空闲时回收一次。
    /// 绝不阻塞调用方（UI 线程）：这里只加锁、排一个计时器，回收在别处做。
    /// <paramref name="delay"/> 须为 null 或非负（底层是 Threading.Timer：-1 毫秒 = 永不触发，再小 / 超过 49.7 天会抛）。
    ///
    /// 合并规则：窗口期内再来请求，并进已排的那一次，**保持最早的到期时刻**——既不推迟也不多排。
    /// 不照搬 Settings.RequestSave 的尾随防抖（每来一个请求就重排）：那样用户反复开关面板时，回收会被一路往后推。
    /// </summary>
    public static void Request(TimeSpan? delay = null)
    {
        long due = Environment.TickCount64 + (long)(delay ?? DefaultDelay).TotalMilliseconds;
        lock (Gate)
        {
            if (_pending && due >= _due) return;   // 已排的那次更早（或同时）：并进去，不动它
            _pending = true;
            _due = due;                            // 首个请求，或者比已排的那次更早：以最早的为准，上限跟着走
            _deadline = due + (long)MaxWait.TotalMilliseconds;
            ArmLocked(due);
        }
    }

    /// <summary>
    /// 真正的回收动作，哪条线程都能调，绝不抛异常：一次阻塞、压缩的第 2 代回收（连大对象堆一起压），
    /// 让终结器跑完，补一次回收，最后把工作集交还系统。
    /// </summary>
    public static void Reclaim()
    {
        // 两条线程同时 Reclaim 会互相拆台：CompactOnce 只管"下一次"第 2 代回收，前一次回收一跑完开关就弹回
        // Default，后一次就没带 LOH 压缩。这里串行化。
        lock (ReclaimGate)
        {
            var latency = GCSettings.LatencyMode;
            // 本进程从不改延迟模式（默认 Interactive，允许阻塞式的整堆回收），这里只是保险：
            // 万一宿主开着 SustainedLowLatency（运行时会尽量躲开整堆的阻塞回收），这一次回收期间临时解除，做完还原。
            // 工作站 GC + 非并发由 Island.App.csproj 配好（ConcurrentGarbageCollection=false），这里不改也改不了。
            bool liftLatency = latency == GCLatencyMode.SustainedLowLatency;
            try
            {
                if (liftLatency) GCSettings.LatencyMode = GCLatencyMode.Interactive;
                // 大对象堆（≥85000 字节的对象）平时只清扫不压缩——搬大对象代价高。CompactOnce 是
                // "只对下一次阻塞的第 2 代回收生效"的开关：让那一次连大对象堆一起压，之后自己弹回 Default。
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect();   // 被终结的对象壳（和它们拖到终结为止的东西）现在才真正可回收：补一次；堆刚压缩过，这一下很短
            }
            catch { /* 回收失败不影响运行 */ }
            finally
            {
                // 还原：不能因为一次回收就永久改掉进程级设置
                try { GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.Default; }
                catch { /* 还原失败只能认了 */ }
                if (liftLatency)
                {
                    try { GCSettings.LatencyMode = latency; }
                    catch { /* 还原失败只能认了 */ }
                }
            }
            try { EmptyWorkingSet(GetCurrentProcess()); }
            catch { /* 交还工作集失败不影响运行 */ }
        }
    }

    // ---------- 测试钩子（internal：只给测试工程用，生产代码不碰） ----------

    /// <summary>
    /// 测试钩子：换掉"到点且空闲"时执行的那个动作（null = 真正的 <see cref="Reclaim"/>）。
    /// 测试用它记"回收发生了没有"，不做真实 GC。
    /// </summary>
    internal static Action? ReclaimHookForTest { get; set; }

    /// <summary>请求的默认延迟（没给 delay 时）。默认 5 秒：让收起动画、窗口缩回和随后的重绘都先走完。测试改短。</summary>
    internal static TimeSpan DefaultDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>到点后界面还忙时，隔多久再问一次 IsIdle。默认 2 秒。测试改短。</summary>
    internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>到点之后最多再等多久界面空闲，超过就丢弃这个请求。默认 10 分钟。测试改短。</summary>
    internal static TimeSpan MaxWait { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 测试钩子：清掉待办（没到点的计时器、已投递未执行的回调全部作废），并把上面几个钩子恢复默认值。
    /// 不等待正在执行的动作结束。
    /// </summary>
    internal static void ResetForTest()
    {
        lock (Gate)
        {
            ClearLocked();
            IsIdle = null;
            ReclaimHookForTest = null;
            DefaultDelay = TimeSpan.FromSeconds(5);
            RetryDelay = TimeSpan.FromSeconds(2);
            MaxWait = TimeSpan.FromMinutes(10);
        }
    }

    // ---------- 排期：状态全部由 Gate 保护（请求来自 UI 线程，计时器回调在线程池线程） ----------

    private static readonly object Gate = new();
    private static readonly object ReclaimGate = new();

    private static Timer? _timer;      // 当前在等的那枚一次性计时器
    private static bool _pending;      // 有一个请求在等（既没回收，也没丢弃）
    private static long _due;          // 到期时刻（TickCount64，毫秒）
    private static long _deadline;     // 到点之后等界面空闲的截止时刻，过了就丢弃
    private static int _generation;    // 代号：每次排期 / 清空都自增，在途的旧回调靠它作废

    /// <summary>
    /// （持锁）排一个一次性的到期回调。每次都换新计时器，并把代号带进回调：
    /// 被换掉的那枚就算已经排队，也会在 <see cref="OnTimer"/> 里因为代号过期被丢弃——不会提前触发，也不会多触发。
    /// </summary>
    private static void ArmLocked(long due)
    {
        int generation = ++_generation;
        long wait = Math.Max(0, due - Environment.TickCount64);
        var timer = new Timer(_ => OnTimer(generation), null,
            TimeSpan.FromMilliseconds(wait), Timeout.InfiniteTimeSpan);
        _timer?.Dispose();
        _timer = timer;
    }

    /// <summary>（持锁）清掉待办：代号自增让在途的回调全部作废，计时器释放。</summary>
    private static void ClearLocked()
    {
        _pending = false;
        _generation++;
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// 计时器回调（线程池线程）：只负责把"到点"这件事交给该在的线程。
    /// 有 Application 就投递到它的 Dispatcher（Background 优先级）；没有（比如测试进程）就地往下走。
    /// </summary>
    private static void OnTimer(int generation)
    {
        lock (Gate)
        {
            if (!_pending || generation != _generation) return;   // 已作废的回调（被重排 / 被 Reset）
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => OnDue(generation)));
                return;
            }
            catch (Exception)
            {
                // Dispatcher 正在关闭（应用退出途中）：进程都要没了，工作集随进程消失，这一笔作废
                return;
            }
        }
        OnDue(generation);
    }

    /// <summary>
    /// 到点：问一次界面静不静。静就回收；不静就隔 <see cref="RetryDelay"/> 再来，等到 <see cref="MaxWait"/> 为止。
    /// 生产环境跑在 UI 线程（Background 优先级的 Dispatcher 操作），没有 Application 时跑在计时器线程。
    /// </summary>
    private static void OnDue(int generation)
    {
        lock (Gate)
        {
            if (!_pending || generation != _generation) return;
        }

        bool idle = AskIdle();   // 宿主的代码，不放在锁里问

        Action action;
        lock (Gate)
        {
            // 问的工夫状态变了（被 Reset / 被重排）：这一笔不认
            if (!_pending || generation != _generation) return;
            if (!idle)
            {
                long now = Environment.TickCount64;
                long retryMs = (long)RetryDelay.TotalMilliseconds;
                // 没有上限的话，几小时前关面板留下的请求会在某个不合时宜的时刻突然回收
                if (now + retryMs > _deadline)
                {
                    ClearLocked();   // 等到头了：丢弃，之后的新请求重新排
                    return;
                }
                _due = now + retryMs;
                ArmLocked(_due);
                return;
            }
            // 先清掉待办再执行动作：动作执行期间来的新请求会另排一次，不会被"正在跑的这一次"吞掉
            ClearLocked();
            action = ReclaimHookForTest ?? Reclaim;
        }
        action();
    }

    /// <summary>
    /// 问宿主"界面静不静"。没设按空闲；宿主的判定抛了按不空闲
    /// （宁可不回收，也不能让异常逃到线程池线程 / Dispatcher 操作上）。
    /// </summary>
    private static bool AskIdle()
    {
        var isIdle = IsIdle;
        if (isIdle is null) return true;
        try { return isIdle(); }
        catch { return false; }
    }

    // ---------- Win32 ----------

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
