namespace Island.Core.Monitoring;

/// <summary>
/// 三态读数（借鉴 BrimDeck Core/SystemMonitor.cs 的 Reading&lt;T&gt;）：
/// 「读到了」「读不到，原因 X」「还没读」是三种不同的状态，不能用一个 0 混过去。
/// </summary>
public readonly struct Reading<T>
{
    private enum State : byte
    {
        NotRequested = 0,
        Available = 1,
        Unavailable = 2,
    }

    private readonly T _value;
    private readonly string? _reason;
    private readonly State _state;

    public bool HasValue => _state == State.Available;
    public bool IsNotRequested => _state == State.NotRequested;

    /// <summary>读不到的原因；读到了则为空串。永不为 null。</summary>
    public string Reason => _state switch
    {
        State.Available => "",
        State.Unavailable => string.IsNullOrWhiteSpace(_reason) ? "读取失败" : _reason,
        _ => "尚未读取",
    };

    private Reading(T value, State state, string? reason)
    {
        _value = value;
        _state = state;
        _reason = reason;
    }

    /// <summary>读到了一个值。</summary>
    public static Reading<T> Of(T value) => new(value, State.Available, null);

    /// <summary>读不到，并说明为什么。</summary>
    public static Reading<T> Unavailable(string reason) =>
        new(default!, State.Unavailable, string.IsNullOrWhiteSpace(reason) ? "读取失败" : reason);

    /// <summary>还没轮到读（按需采样下，这一项本轮没被要求）。</summary>
    public static Reading<T> NotRequested => default;

    public bool TryGet(out T value) { value = _value; return HasValue; }

    /// <summary>有值给值，否则给类型默认值。仅在"默认值有意义"的场合用（如做算术）。</summary>
    public T? OrDefault => HasValue ? _value : default;

    /// <summary>有值时映射，没值时保留未请求或原因。</summary>
    public Reading<TResult> Map<TResult>(Func<T, TResult> map) => _state switch
    {
        State.Available => Reading<TResult>.Of(map(_value)),
        State.Unavailable => Reading<TResult>.Unavailable(Reason),
        _ => Reading<TResult>.NotRequested,
    };

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

    /// <summary>所有读数里第一个真实错误的原因（跳过未请求与首个采样预热期）；全都正常返回空串。</summary>
    public string FirstProblem
    {
        get
        {
            if (IsProblem(Cpu, out string r)) return r;
            if (IsProblem(Memory, out r)) return r;
            if (IsProblem(Gpu, out r)) return r;
            if (IsProblem(Disk, out r)) return r;
            if (IsProblem(NetDown, out r)) return r;
            if (IsProblem(NetUp, out r)) return r;
            if (IsProblem(CpuTemp, out r)) return r;
            return "";
        }
    }

    private static bool IsProblem(Reading<double> r, out string reason)
    {
        if (r.HasValue || r.IsNotRequested) { reason = ""; return false; }
        reason = r.Reason;
        if (reason == "尚未读取" || reason == "首个采样周期") { reason = ""; return false; }
        return reason.Length > 0;
    }
}
