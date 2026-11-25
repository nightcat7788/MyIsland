using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using Island.App.Services;

namespace Island.Tests;

/// <summary>
/// "用完即还"（<see cref="MemoryReclaim"/>）的用例（2026-11-24：挂机几小时后工作集涨到 276 MB）。
///
/// 背景：展开面板和设置窗口都是按需创建、用完释放的，但只释放不回收——它们的垃圾和碰过的页
/// 会一直留在工作集里，而岛体大部分时间是收着的。MemoryReclaim 负责在界面静下来之后做一次
/// 压缩式回收并把工作集交还系统。这里钉住的是"什么时候做、做几次"：
///   ① 窗口期内的多次 Request 合并成一次回收；
///   ② 后来的请求不推迟已排定的回收（保持最早的到期时刻）；
///   ③ Request 立即返回，回收不在调用线程（UI 线程）上做；
///   ④ 界面忙（IsIdle=false）时不回收，但也不丢：隔一会儿再问，空闲了才做；
///   ⑤ 到点后界面一直忙：等到上限就丢弃，之后的新请求照常受理；
///   ⑥ 宿主的 IsIdle 抛异常：按"不空闲"处理，异常不外逃；
///   ⑦ 一轮做完之后再来请求，照样排期（常驻进程要能一直用）；
///   ⑧ 真正的 Reclaim() 确实做了第 2 代回收，且不留下进程级设置的尾巴；
///   ⑨ 没设 IsIdle 按空闲处理，到期真的落到 Reclaim()；
///   ⑩ 有 Application 时到点那一步走 Dispatcher（Background 优先级）。
///
/// ①–⑦、⑩ 用 <see cref="MemoryReclaim.ReclaimHookForTest"/> 把回收动作换成只记数的钩子
/// （不做真实 GC：快，别的线程上的回收也搅不进计数）；⑧⑨ 用真实动作，按 GC.CollectionCount(2) 判断。
/// 计时一律留大余量（延迟 50–1000ms、等待 2–3s），满载的机器上也不该抖。
/// </summary>
internal static class MemoryReclaimTests
{
    public static void Run(Action<string, bool> check)
    {
        try
        {
            CoalescesToOne(check);
            KeepsEarliestDue(check);
            RequestDoesNotBlock(check);
            WaitsForIdle(check);
            GivesUpAfterUpperBound(check);
            SurvivesThrowingIsIdle(check);
            KeepsWorkingAcrossRounds(check);
            RealReclaim(check);
            NullIsIdleRunsRealReclaim(check);
            // 必须排在最后：它会在进程里创建那唯一的一个 Application
            UsesDispatcherWhenApplicationExists(check);
        }
        finally
        {
            // 收尾：清掉待办、恢复默认值，不留一个 5 秒后才响的计时器给别的用例
            MemoryReclaim.ResetForTest();
        }
    }

    /// <summary>轮询等待条件成立（每 10ms 看一次）；超时前没成立返回 false。</summary>
    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    // ---------- ① 窗口期内的多次 Request 合并成一次回收 ----------
    // 真实缺陷长什么样：每个请求各排一次（回收 5 次），或者占位实现根本不排（回收 0 次）。
    private static void CoalescesToOne(Action<string, bool> check)
    {
        MemoryReclaim.ResetForTest();
        int count = 0;
        MemoryReclaim.DefaultDelay = TimeSpan.FromMilliseconds(300);
        MemoryReclaim.ReclaimHookForTest = () => Interlocked.Increment(ref count);

        // 5 次请求都落在首个请求的 300ms 窗口内：只该排一次回收
        for (int i = 0; i < 5; i++)
        {
            MemoryReclaim.Request();
            Thread.Sleep(10);
        }
        WaitUntil(() => Volatile.Read(ref count) > 0, 2000);
        Thread.Sleep(600);   // 再空等 2 个延迟：多排的那一次要来早该来了
        check("MemoryReclaim：窗口期内的多次 Request 只触发一次回收", Volatile.Read(ref count) == 1);
    }

    // ---------- ② 后来的请求不推迟已排定的回收 ----------
    // 这是最容易写错的一条：照搬 Settings.RequestSave 的"尾随防抖"（每来一个请求就重排计时器），
    // 回收就会被一路往后推——用户反复开关面板时它永远不来。
    // 真实缺陷长什么样：到期时刻从首个请求的 1000ms 被推到末次请求之后（≥1700ms）。
    private static void KeepsEarliestDue(Action<string, bool> check)
    {
        MemoryReclaim.ResetForTest();
        var sw = Stopwatch.StartNew();
        long firedAt = -1;
        MemoryReclaim.DefaultDelay = TimeSpan.FromMilliseconds(1000);
        // 只记第一次：机器慢到后两个请求落在窗口外时，它们本来就该另排一次，不该影响这里的时间点
        MemoryReclaim.ReclaimHookForTest = () =>
            Interlocked.CompareExchange(ref firedAt, sw.ElapsedMilliseconds, -1);

        // 0 / 350 / 700ms 各请求一次，全在首个请求的 1000ms 窗口内
        for (int i = 0; i < 3; i++)
        {
            MemoryReclaim.Request();
            Thread.Sleep(350);
        }
        bool fired = WaitUntil(() => Volatile.Read(ref firedAt) >= 0, 3000);
        long at = Volatile.Read(ref firedAt);
        // 时刻在回调里记，不受测试线程被调度得早晚的影响。正确实现 ≈1000ms，尾随防抖 ≥1700ms；
        // 下界留 100ms 给系统时钟粒度，上界留 400ms 给线程池计时器的迟到。
        check("MemoryReclaim：窗口期内的新请求不推迟回收（仍在首个请求的延迟到点时执行，不早也不晚）",
            fired && at >= 900 && at <= 1400);
    }

    // ---------- ③ Request 立即返回：回收不在调用线程上做 ----------
    // Request 是 UI 线程调的；回收是一次阻塞的第 2 代压缩，就地执行 = 调用点当场卡住。
    // 真实缺陷长什么样：延迟为 0 时就地执行 → 被下面那个不放行的钩子卡满 5 秒。
    private static void RequestDoesNotBlock(Action<string, bool> check)
    {
        MemoryReclaim.ResetForTest();
        int count = 0;
        var gate = new ManualResetEventSlim(false);
        MemoryReclaim.ReclaimHookForTest = () =>
        {
            Interlocked.Increment(ref count);
            gate.Wait(TimeSpan.FromSeconds(5));   // 测试线程放行之前一直卡着
        };

        var sw = Stopwatch.StartNew();
        MemoryReclaim.Request(TimeSpan.Zero);   // 延迟为 0 也不能就地执行
        sw.Stop();
        long elapsed = sw.ElapsedMilliseconds;

        gate.Set();   // 放行，让回收在它自己的线程上收尾
        bool ran = WaitUntil(() => Volatile.Read(ref count) >= 1, 2000);
        // 正常只是加锁 + 排一个计时器（亚毫秒级）；"回收确实发生了"一并断言，免得空实现白拿这一条
        check("MemoryReclaim：Request 立即返回（不在调用线程上做回收），而回收本身在别处照常发生",
            elapsed < 1000 && ran);
    }

    // ---------- ④ 界面忙时不回收、但也不丢；空闲后回收 ----------
    // 真实缺陷长什么样：界面忙也照回收（忙期间回收次数 >0）；忙就丢弃请求（只问了 1 次 IsIdle）；
    // 空闲之后再也不回收。
    private static void WaitsForIdle(Action<string, bool> check)
    {
        MemoryReclaim.ResetForTest();
        int count = 0, asked = 0, idle = 0;
        MemoryReclaim.DefaultDelay = TimeSpan.FromMilliseconds(100);
        MemoryReclaim.RetryDelay = TimeSpan.FromMilliseconds(50);
        MemoryReclaim.ReclaimHookForTest = () => Interlocked.Increment(ref count);
        MemoryReclaim.IsIdle = () => { Interlocked.Increment(ref asked); return Volatile.Read(ref idle) == 1; };

        MemoryReclaim.Request();
        Thread.Sleep(500);   // 100ms 到点后又过了约 8 个重试间隔，界面一直"忙"
        int askedWhileBusy = Volatile.Read(ref asked);
        check("MemoryReclaim：IsIdle 为 false 时不回收（忙期间回收 0 次），且按重试间隔反复询问、没有丢掉请求",
            Volatile.Read(ref count) == 0 && askedWhileBusy >= 2);

        Volatile.Write(ref idle, 1);
        bool ran = WaitUntil(() => Volatile.Read(ref count) >= 1, 2000);
        Thread.Sleep(300);
        check("MemoryReclaim：IsIdle 变为 true 后发生回收（且只回收一次）",
            ran && Volatile.Read(ref count) == 1);
    }

    // ---------- ⑤ 到点后界面一直忙：等到上限就丢弃；之后的新请求照常受理 ----------
    // 没有上限的话，几小时前关面板留下的请求会在某个不合时宜的时刻突然回收。
    // 真实缺陷长什么样：一直问下去（上限之后询问次数还在涨）；或者才重试一两次就不问了（太早放弃）；
    // 丢弃时没把"有待办"的标记清掉——后面的请求全被并进一个再也不会执行的请求里。
    private static void GivesUpAfterUpperBound(Action<string, bool> check)
    {
        MemoryReclaim.ResetForTest();
        int count = 0, asked = 0;
        var sw = Stopwatch.StartNew();
        long lastAskAt = -1;
        MemoryReclaim.DefaultDelay = TimeSpan.FromMilliseconds(50);
        MemoryReclaim.RetryDelay = TimeSpan.FromMilliseconds(50);
        MemoryReclaim.MaxWait = TimeSpan.FromMilliseconds(200);
        MemoryReclaim.ReclaimHookForTest = () => Interlocked.Increment(ref count);
        MemoryReclaim.IsIdle = () =>
        {
            Interlocked.Increment(ref asked);
            Volatile.Write(ref lastAskAt, sw.ElapsedMilliseconds);
            return false;
        };

        MemoryReclaim.Request();
        // 50ms 到点，上限 = 到点后再等 200ms ⇒ 最迟约 250ms 之后不该再问
        //
        // 这里用**轮询到静默**而不是固定 sleep：机器负载高时（多个构建并行）定时器会迟到，
        // 固定 700ms 就断言会把"迟到"误判成"没问够"，门禁随机变红 —— 2026-11-26 实测过一次。
        // 等到"至少问过两次"且"最后一次询问已过去 400ms"，再观察询问次数是否还在涨。
        WaitUntil(() => Volatile.Read(ref asked) >= 2
                       && (sw.ElapsedMilliseconds - Volatile.Read(ref lastAskAt)) >= 400, 5000);
        int askedWhenQuiet = Volatile.Read(ref asked);
        long lastAskWhenQuiet = Volatile.Read(ref lastAskAt);
        Thread.Sleep(600);
        check("MemoryReclaim：到点后界面一直忙，等到上限就停止询问并丢弃请求（始终没有回收）",
            Volatile.Read(ref count) == 0
            && askedWhenQuiet >= 2 && lastAskWhenQuiet >= 200
            && Volatile.Read(ref asked) == askedWhenQuiet);

        MemoryReclaim.IsIdle = () => true;
        MemoryReclaim.Request();
        bool ran = WaitUntil(() => Volatile.Read(ref count) >= 1, 2000);
        check("MemoryReclaim：丢弃一次之后，新的请求照常触发回收", ran && Volatile.Read(ref count) == 1);
    }

    // ---------- ⑥ 宿主的 IsIdle 抛异常：按"不空闲"处理，异常不外逃 ----------
    // 到点那一步要么在线程池线程上，要么在 Dispatcher 操作里：异常逃出去 = 进程直接终止。
    // 回收是锦上添花的事，不值得为它冒险。（真逃出去的话，后面的用例一条都跑不到，很好认。）
    private static void SurvivesThrowingIsIdle(Action<string, bool> check)
    {
        MemoryReclaim.ResetForTest();
        int count = 0, asked = 0;
        MemoryReclaim.DefaultDelay = TimeSpan.FromMilliseconds(50);
        MemoryReclaim.RetryDelay = TimeSpan.FromMilliseconds(50);
        MemoryReclaim.MaxWait = TimeSpan.FromMilliseconds(200);
        MemoryReclaim.ReclaimHookForTest = () => Interlocked.Increment(ref count);
        MemoryReclaim.IsIdle = () => { Interlocked.Increment(ref asked); throw new InvalidOperationException("宿主的判定挂了"); };

        MemoryReclaim.Request();
        Thread.Sleep(500);
        check("MemoryReclaim：IsIdle 抛异常时按不空闲处理（不回收），异常没有逃到计时器线程上",
            Volatile.Read(ref count) == 0 && Volatile.Read(ref asked) >= 2);

        MemoryReclaim.IsIdle = () => true;
        MemoryReclaim.Request();
        bool ran = WaitUntil(() => Volatile.Read(ref count) >= 1, 2000);
        check("MemoryReclaim：IsIdle 恢复正常后回收照常进行", ran && Volatile.Read(ref count) == 1);
    }

    // ---------- ⑦ 一轮做完之后再来请求：照样排期 ----------
    // 真实缺陷长什么样：一次性计时器用完就丢、或者"有待办"标记不复位，第二轮永远等不到。
    private static void KeepsWorkingAcrossRounds(Action<string, bool> check)
    {
        MemoryReclaim.ResetForTest();
        int count = 0;
        MemoryReclaim.DefaultDelay = TimeSpan.FromMilliseconds(100);
        MemoryReclaim.ReclaimHookForTest = () => Interlocked.Increment(ref count);

        // 连续三轮：每轮一次 Request，恰好多一次回收
        const int Rounds = 3;
        bool ok = true;
        for (int round = 1; round <= Rounds && ok; round++)
        {
            MemoryReclaim.Request();
            if (!WaitUntil(() => Volatile.Read(ref count) >= round, 2000)) { ok = false; break; }
            Thread.Sleep(300);   // 再空等一会儿：这一轮不该多出第二次
            ok = Volatile.Read(ref count) == round;
        }
        check("MemoryReclaim：一次回收完成后再 Request 会排一次新的回收（连续三轮，每轮恰好一次）",
            ok && Volatile.Read(ref count) == Rounds);
    }

    // ---------- ⑧ 真正的 Reclaim()：确实做了第 2 代回收，且不留进程级设置的尾巴 ----------
    private static void RealReclaim(Action<string, bool> check)
    {
        MemoryReclaim.ResetForTest();   // 顺带把 IsIdle 清成 null、换掉上一场景的计数动作
        MemoryReclaim.IsIdle = null;

        int gen2 = GC.CollectionCount(2);
        bool threw = false;
        try { MemoryReclaim.Reclaim(); }
        catch { threw = true; }
        check("MemoryReclaim：真正的 Reclaim() 触发第 2 代回收，IsIdle 未设置时也不抛异常",
            !threw && GC.CollectionCount(2) > gen2);

        // 宿主万一开着"持续低延迟"（SustainedLowLatency，运行时会尽量躲开整堆的阻塞回收）：
        // 这一次回收期间要临时解除，做完必须还原；CompactOnce 同理，不能留在进程里。
        GCLatencyMode latencyBefore = GCSettings.LatencyMode;
        GCLatencyMode latencyAfter;
        GCLargeObjectHeapCompactionMode lohAfter;
        try
        {
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            gen2 = GC.CollectionCount(2);
            threw = false;
            try { MemoryReclaim.Reclaim(); }
            catch { threw = true; }
            latencyAfter = GCSettings.LatencyMode;
            lohAfter = GCSettings.LargeObjectHeapCompactionMode;
        }
        finally { GCSettings.LatencyMode = latencyBefore; }
        check("MemoryReclaim：Reclaim() 做完第 2 代回收后，延迟模式与 LOH 压缩开关都还原（不永久改进程级设置）",
            !threw && GC.CollectionCount(2) > gen2
            && latencyAfter == GCLatencyMode.SustainedLowLatency
            && lohAfter == GCLargeObjectHeapCompactionMode.Default);
    }

    // ---------- ⑨ 没设 IsIdle：按空闲处理 ----------
    // 这里特意用真实动作（不换计数钩子）：验证"钩子为空时到期那一步确实落到真正的 Reclaim()"。
    // 只调钩子的实现能让上面所有计数用例全绿，生产环境却一次都不回收。
    private static void NullIsIdleRunsRealReclaim(Action<string, bool> check)
    {
        MemoryReclaim.ResetForTest();   // IsIdle = null、ReclaimHookForTest = null
        MemoryReclaim.DefaultDelay = TimeSpan.FromMilliseconds(100);

        int gen2 = GC.CollectionCount(2);
        MemoryReclaim.Request();
        bool ran = WaitUntil(() => GC.CollectionCount(2) > gen2, 3000);
        int afterRun = GC.CollectionCount(2);
        Thread.Sleep(400);   // 再空等一会儿：不该有第二次
        check("MemoryReclaim：没设 IsIdle（null）时按空闲处理，请求到期后执行真正的回收（且只执行一次）",
            ran && GC.CollectionCount(2) == afterRun);
    }

    // ---------- ⑩ 有 Application 时：到点那一步走 Dispatcher（Background 优先级） ----------
    // 这是生产环境真正走的路径——测试进程里本来没有 Application，前面的场景走的都是计时器线程。
    // 在一条 STA 线程上起一个真实的 WPF Application + 消息循环来模拟。进程里只能创建一个 Application
    // （关掉之后也不能再建），所以这个场景只能有一个、且必须排在最后；结束时走它自己的关停流程。
    // 真实缺陷长什么样：有 Application 也在计时器线程上直接回收（UI 线程被占住时回收次数就涨了）；
    // 投递的代号不对、永远不执行；优先级不是 Background（排到输入和渲染前面，回收的停顿露出来）。
    private static void UsesDispatcherWhenApplicationExists(Action<string, bool> check)
    {
        const string pathCheck = "MemoryReclaim：有 Application 时到点那一步走 Dispatcher——UI 线程忙时不回收（也没跑到计时器线程上），放开后恰好回收一次，IsIdle 与回收都在 UI 线程上执行";
        const string priorityCheck = "MemoryReclaim：投递给 Dispatcher 的回收用的是 Background 优先级";

        Application? app = null;
        Exception? startupError = null;
        var created = new ManualResetEventSlim(false);
        var looping = new ManualResetEventSlim(false);
        var ui = new Thread(() =>
        {
            try
            {
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                // 这一笔要由消息循环取出来执行才会置位：置位 = 循环已经在跑
                app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(looping.Set));
                created.Set();
                app.Run();   // 一直跑消息循环，直到下面请求关闭
            }
            catch (Exception ex)
            {
                startupError = ex;
                created.Set();
            }
        })
        { IsBackground = true, Name = "island-tests-ui" };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();

        if (!created.Wait(TimeSpan.FromSeconds(10)) || app is null)
        {
            check(pathCheck + "（测试进程里没建起 WPF Application，无法验证：" + startupError?.GetType().Name + "）", false);
            return;
        }
        Dispatcher dispatcher = app.Dispatcher;

        int count = 0, askedOnUi = 0, ranOnUi = 0;
        var posted = new ConcurrentQueue<(DispatcherPriority Priority, int Thread)>();
        // 记下投递给这个调度器的每个操作（优先级 + 发起线程），用来断言 MemoryReclaim 用的是 Background
        void OnOperationPosted(object? sender, DispatcherHookEventArgs e) =>
            posted.Enqueue((e.Operation.Priority, Environment.CurrentManagedThreadId));

        var releaseUi = new ManualResetEventSlim(false);
        var uiBlocked = new ManualResetEventSlim(false);
        try
        {
            if (!looping.Wait(TimeSpan.FromSeconds(10)))
            {
                check(pathCheck + "（WPF 消息循环没有跑起来，无法验证）", false);
                return;
            }
            dispatcher.Hooks.OperationPosted += OnOperationPosted;

            MemoryReclaim.ResetForTest();
            MemoryReclaim.DefaultDelay = TimeSpan.FromMilliseconds(100);
            MemoryReclaim.ReclaimHookForTest = () =>
            {
                Interlocked.Increment(ref count);
                if (dispatcher.CheckAccess()) Interlocked.Increment(ref ranOnUi);
            };
            MemoryReclaim.IsIdle = () =>
            {
                if (dispatcher.CheckAccess()) Interlocked.Increment(ref askedOnUi);
                return true;
            };

            // 先用一个普通优先级的操作把 UI 线程占住：这期间到点的请求只能排队
            dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                uiBlocked.Set();
                releaseUi.Wait(TimeSpan.FromSeconds(5));
            }));
            if (!uiBlocked.Wait(TimeSpan.FromSeconds(5)))
            {
                check(pathCheck + "（没能占住 UI 线程，无法验证）", false);
                return;
            }

            MemoryReclaim.Request();
            Thread.Sleep(400);   // 早已过了 100ms 的到期时刻，UI 线程仍被占着
            int countWhileUiBusy = Volatile.Read(ref count);
            releaseUi.Set();
            bool ran = WaitUntil(() => Volatile.Read(ref count) >= 1, 2000);
            Thread.Sleep(300);
            check(pathCheck,
                countWhileUiBusy == 0 && ran && Volatile.Read(ref count) == 1
                && Volatile.Read(ref ranOnUi) >= 1 && Volatile.Read(ref askedOnUi) >= 1);

            // 来自线程池线程（既不是测试线程也不是这条 UI 线程）的投递应当恰好一个，且是 Background
            var fromPool = posted
                .Where(p => p.Thread != Environment.CurrentManagedThreadId && p.Thread != ui.ManagedThreadId)
                .Select(p => p.Priority).ToArray();
            check(priorityCheck, fromPool.Length == 1 && fromPool[0] == DispatcherPriority.Background);
        }
        finally
        {
            releaseUi.Set();                                            // 别把 UI 线程留在被占住的状态
            dispatcher.Hooks.OperationPosted -= OnOperationPosted;
            // 收尾必须干净（2026-11-24 实测踩过）：只关调度器（BeginInvokeShutdown）的话 UI 线程是退了，
            // 但 Application.Current 一直非 null——进程里后面的 Request 会被投给一个已经停了的调度器、永远不执行。
            // 要走 Application 自己的关停流程；而 Shutdown 只能在它自己的线程上调用，别处调会抛"另一个线程拥有该对象"。
            dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(app.Shutdown));
            ui.Join(TimeSpan.FromSeconds(5));
            check("MemoryReclaim：Dispatcher 场景收尾后 Application.Current 回到 null（测试进程恢复成没有 Application 的状态，不拖累后面的用例）",
                System.Windows.Application.Current is null && !ui.IsAlive);
            MemoryReclaim.ResetForTest();
        }
    }
}
