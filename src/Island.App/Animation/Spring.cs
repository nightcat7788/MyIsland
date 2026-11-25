using System;

namespace Island.App.Animation;

/// <summary>
/// 阻尼弹簧：所有形变/位移共用。ratio &lt; 1 欠阻尼带回弹，= 1 临界阻尼，&gt; 1 过阻尼。
/// 移植自 wisland/ui/anim.py 的 Spring，用半隐式欧拉 + 子步积分保证大 dt 下稳定。
/// </summary>
public sealed class Spring
{
    public double Value { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; private set; }
    public double Stiffness { get; private set; }
    public double Damping { get; private set; }
    public double Epsilon { get; set; } = 0.05;

    public Spring(double value = 0, double stiffness = 300, double ratio = 0.8, double epsilon = 0.05)
    {
        Value = Target = value;
        Tune(stiffness, ratio);
        Epsilon = epsilon;
    }

    public void Tune(double stiffness, double ratio)
    {
        // sqrt(负刚度)=NaN、ratio<=0 会让阻尼退化 ⇒ 非有限/非法参数直接忽略，
        // 宁可保持原参数，也不要把整条弹簧推进 NaN（NaN 会一路传染到 Rect 构造并崩掉整窗）
        if (!double.IsFinite(stiffness) || stiffness <= 0) return;
        if (!double.IsFinite(ratio) || ratio <= 0) return;
        Stiffness = stiffness;
        Damping = 2.0 * ratio * Math.Sqrt(stiffness);
    }

    public void To(double target, double? stiffness = null, double? ratio = null)
    {
        if (!double.IsFinite(target)) return;
        Target = target;
        if (stiffness.HasValue)
            Tune(stiffness.Value, ratio ?? CurrentRatio());
    }

    /// <summary>未显式给 ratio 时按当前阻尼反推；刚度为 0 会除出 Infinity，这里兜一个默认值。</summary>
    private double CurrentRatio()
        => Stiffness > 0 ? Damping / (2.0 * Math.Sqrt(Stiffness)) : 0.8;

    /// <summary>直接跳到目标值并清空速度（无动画）。</summary>
    public void Snap(double value)
    {
        if (!double.IsFinite(value)) return;
        Value = Target = value;
        Velocity = 0;
    }

    /// <summary>给速度一个冲量，用于"惊讶"这类弹跳效果。</summary>
    public void Kick(double impulse)
    {
        if (!double.IsFinite(impulse)) return;
        Velocity += impulse;
    }

    public bool AtRest =>
        Math.Abs(Value - Target) < Epsilon && Math.Abs(Velocity) < Epsilon * 8;

    /// <summary>推进一个时间步（秒）。内部分成不超过 1/240s 的子步。</summary>
    public void Step(double dt)
    {
        if (AtRest)
        {
            Value = Target;
            Velocity = 0;
            return;
        }
        // dt 非有限（NaN）会让 (int)Math.Ceiling(NaN) 变成 int.MinValue，子步数与步长一起变成 NaN
        if (!double.IsFinite(dt) || dt <= 0) return;
        dt = Math.Min(dt, 0.1);
        int n = Math.Max(1, (int)Math.Ceiling(dt / (1.0 / 240)));
        double h = dt / n;
        for (int i = 0; i < n; i++)
        {
            double a = -Stiffness * (Value - Target) - Damping * Velocity;
            Velocity += a * h;
            Value += Velocity * h;
        }
        // 自愈：积分一旦溢出（Inf/NaN）立刻回到目标值，而不是让坏值永久留在状态里
        if (!double.IsFinite(Value) || !double.IsFinite(Velocity))
        {
            Value = Target;
            Velocity = 0;
        }
    }
}

public static class Easing
{
    // NaN 参与比较恒为 false，原实现会把 NaN 原样放行；这里把 NaN 收敛到下界，
    // ±Inf 仍按上下界正常收敛
    public static double Clamp(double x, double lo, double hi)
    {
        if (double.IsNaN(x)) return lo;
        if (x < lo) return lo;
        if (x > hi) return hi;
        return x;
    }
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
    public static double Smooth(double t) { t = Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
    public static double EaseOut(double t) { t = Clamp(t, 0, 1); return 1 - Math.Pow(1 - t, 3); }
}
