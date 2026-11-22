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
        Stiffness = stiffness;
        Damping = 2.0 * ratio * Math.Sqrt(stiffness);
    }

    public void To(double target, double? stiffness = null, double? ratio = null)
    {
        Target = target;
        if (stiffness.HasValue)
            Tune(stiffness.Value, ratio ?? (Damping / (2 * Math.Sqrt(Stiffness))));
    }

    /// <summary>直接跳到目标值并清空速度（无动画）。</summary>
    public void Snap(double value)
    {
        Value = Target = value;
        Velocity = 0;
    }

    /// <summary>给速度一个冲量，用于"惊讶"这类弹跳效果。</summary>
    public void Kick(double impulse) => Velocity += impulse;

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
        dt = Math.Clamp(dt, 0, 0.1);
        int n = Math.Max(1, (int)Math.Ceiling(dt / (1.0 / 240)));
        double h = dt / n;
        for (int i = 0; i < n; i++)
        {
            double a = -Stiffness * (Value - Target) - Damping * Velocity;
            Velocity += a * h;
            Value += Velocity * h;
        }
    }
}

public static class Easing
{
    public static double Clamp(double x, double lo, double hi) => x < lo ? lo : x > hi ? hi : x;
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
    public static double Smooth(double t) { t = Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
    public static double EaseOut(double t) { t = Clamp(t, 0, 1); return 1 - Math.Pow(1 - t, 3); }
}
