using System;
using System.Threading;
using System.Windows.Threading;

namespace Island.App.Services;

/// <summary>
/// 「用户正在看 / 正在等」的重复计时器：线程池计时，每次 tick 用 Dispatcher.BeginInvoke 投递回 UI 线程。
///
/// 为什么不直接用 DispatcherTimer：它骑在 WM_TIMER 上，而 Windows 只在消息队列空闲时才生成 WM_TIMER；
/// 鼠标持续移动时 tick 最迟到 3.5 秒才来（参考 BrimDeck/PromptTimer 的实测）。
/// 本计时器由线程池驱动，到点即向 UI 线程排队，鼠标在动也照样准时。
///
/// 低频工作（每秒全屏检测、30 分钟天气刷新等）晚一点没关系，仍留在 DispatcherTimer 上。
/// 对外接口与 DispatcherTimer 对齐：Interval / Start() / Stop() / IsEnabled / Tick 事件。
/// </summary>
public sealed class PromptTimer : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private Timer? _timer;
    private TimeSpan _interval;
    private int _generation;   // 代号：Start/Stop 自增，已投递未执行的 tick 靠它作废
    private int _pending;      // 等待位：1 = 已有一个 tick 在等 UI 线程
    private bool _disposed;

    /// <summary>绑定 UI 调度器，tick 一律投递到该线程执行。</summary>
    public PromptTimer(Dispatcher dispatcher, TimeSpan interval)
    {
        _dispatcher = dispatcher;
        _interval = interval;
    }

    /// <summary>计时周期。运行中修改仍保持运行，新周期从此刻起算。</summary>
    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            if (_interval == value) return;
            _interval = value;
            if (!IsEnabled) return;
            try { _timer?.Change(Period, Period); }
            catch (ObjectDisposedException) { /* 与 Stop 竞态：底层计时器已释放 */ }
        }
    }

    /// <summary>是否正在计时。</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>周期回调，始终在 UI 线程执行。</summary>
    public event EventHandler? Tick;

    // 周期夹在 [1ms, 4294967294ms]：Threading.Timer 不接受 0/负数，也不能超过 uint.MaxValue-1 毫秒
    private TimeSpan Period
    {
        get
        {
            if (_interval < TimeSpan.FromMilliseconds(1)) return TimeSpan.FromMilliseconds(1);
            if (_interval > TimeSpan.FromMilliseconds(4294967294)) return TimeSpan.FromMilliseconds(4294967294);
            return _interval;
        }
    }

    /// <summary>开始计时。重复调用等价于先 Stop 再 Start，上一轮已投递的 tick 立即作废。</summary>
    public void Start()
    {
        if (_disposed) return;
        Stop();
        int generation = Volatile.Read(ref _generation);   // 取 Stop 之后的代号
        IsEnabled = true;
        _timer = new Timer(_ => OnTimer(generation), null, Period, Period);
    }

    /// <summary>停止计时并释放底层计时器；已投递但还没执行的 tick 一律作废。</summary>
    public void Stop()
    {
        Interlocked.Increment(ref _generation);            // 代号自增：旧 tick 全部失效
        IsEnabled = false;
        Timer? t = _timer;
        _timer = null;
        t?.Dispose();
    }

    /// <summary>释放底层计时器并解绑 Tick（窗口关闭时调用，避免残留回调与引用泄漏）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        Tick = null;
    }

    /// <summary>线程池回调：只负责把 tick 投递到 UI 线程。</summary>
    private void OnTimer(int generation)
    {
        // 至多一个 tick 在等：UI 忙时只补一次，不积压
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                Interlocked.Exchange(ref _pending, 0);
                // 代号守卫：Start/Stop 之后，已投递但还没执行的 tick 必须作废
                if (generation != Volatile.Read(ref _generation)) return;
                if (!IsEnabled) return;
                Tick?.Invoke(this, EventArgs.Empty);
            }));
        }
        catch
        {
            // UI 线程已关闭（窗口退出途中）：这一笔作废，别留下等待位
            Interlocked.Exchange(ref _pending, 0);
        }
    }
}
