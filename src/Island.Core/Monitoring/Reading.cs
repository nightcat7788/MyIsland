namespace Island.Core.Monitoring;

/// <summary>
/// 三态读数（借鉴 BrimDeck Core/SystemMonitor.cs 的 Reading&lt;T&gt;）：
/// 「读到了」「读不到，原因 X」「还没读」是三种不同的状态，不能用一个 0 混过去。
///
/// 没有它的时候：CPU 采样用了错误的结构体位宽，idle 恒为 0 → 显示 100%——
/// 因为"读不到"和"读到 0"在 UI 上长得一模一样，这个 bug 藏了 20 轮才被用户发现。
/// 有了它：读不到时 UI 显示 "--" 并把 Reason 画出来，任何采样错误立刻可见。
/// </summary>
public readonly struct Reading<T>
{
    private readonly T _value;
    private readonly string? _reason;

    public bool HasValue { get; }

    /// <summary>读不到的原因；读到了则为空串。永不为 null。</summary>
    public string Reason => HasValue ? "" : _reason ?? "尚未读取";

    private Reading(T value, bool known, string? reason)
    {
        _value = value; HasValue = known; _reason = reason;
    }

    /// <summary>读到了一个值。</summary>
    public static Reading<T> Of(T value) => new(value, true, null);

    /// <summary>读不到，并说明为什么。</summary>
    public static Reading<T> Unavailable(string reason) => new(default!, false, reason);

    /// <summary>还没轮到读（按需采样下，这一项本轮没被要求）。</summary>
    public static Reading<T> NotRequested => new(default!, false, null);

    public bool TryGet(out T value) { value = _value; return HasValue; }

    /// <summary>有值给值，否则给类型默认值。仅在"默认值有意义"的场合用（如做算术）。</summary>
    public T? OrDefault => HasValue ? _value : default;

    /// <summary>有值时映射，没值时保留未请求或原因。</summary>
    public Reading<TResult> Map<TResult>(Func<T, TResult> map)
        => HasValue ? Reading<TResult>.Of(map(_value))
         : _reason is null ? Reading<TResult>.NotRequested
         : Reading<TResult>.Unavailable(Reason);

    /// <summary>有值时给值，否则给调用方准备的兜底值（用于老代码兼容）。</summary>
    public T GetValueOrDefault(T fallback) => HasValue ? _value : fallback;

    public static implicit operator Reading<T>(T value) => Of(value);

    public override string ToString() => HasValue ? _value?.ToString() ?? "" : $"--（{Reason}）";
}

/// <summary>MonitorSnapshot 上各读数的可用性（独立于数值字段，老代码不用改）。</summary>
public sealed record ReadingStatus
{
    public static readonly ReadingStatus Empty = new();

    public Reading<double> Cpu { get; init; } = Reading<double>.NotRequested;
    public Reading<double> Memory { get; init; } = Reading<double>.NotRequested;
    public Reading<double> Gpu { get; init; } = Reading<double>.NotRequested;
    public Reading<double> Disk { get; init; } = Reading<double>.NotRequested;
    public Reading<double> NetDown { get; init; } = Reading<double>.NotRequested;
    public Reading<double> NetUp { get; init; } = Reading<double>.NotRequested;
    public Reading<double> CpuTemp { get; init; } = Reading<double>.NotRequested;
    public Reading<double> GpuTemp { get; init; } = Reading<double>.NotRequested;
    public Reading<double> SsdTemp { get; init; } = Reading<double>.NotRequested;
    public Reading<double> Battery { get; init; } = Reading<double>.NotRequested;

    /// <summary>所有读数里第一个"读不到"的原因（给状态行用）；全都正常返回空串。</summary>
    public string FirstProblem
    {
        get
        {
            foreach (var r in new[] { Cpu, Memory, Gpu, Disk, NetDown, CpuTemp })
                if (!r.HasValue && r.Reason != "尚未读取") return r.Reason;
            return "";
        }
    }
}
