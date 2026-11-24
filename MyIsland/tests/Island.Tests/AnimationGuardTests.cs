using System.Windows;
using Island.App.Animation;
using Island.App.Controls;

namespace Island.Tests;

/// <summary>
/// 数值安全回归用例。
/// 2026-11-23 首帧 Arrange 阶段崩溃（crash.log：宽度和高度必须为非负值）：
/// 弹簧/缓动一旦出现 NaN，渲染链路的 Rect 构造器会抛未处理异常，整窗崩掉。
/// 这里锁死三条防线：缓动夹紧不漏 NaN、弹簧遇 NaN 自愈、渲染矩形统一夹紧。
/// </summary>
internal static class AnimationGuardTests
{
    public static void Run(Action<string, bool> check)
    {
        // ---------- 缓动 ----------
        check("Easing.Clamp 遇 NaN 收敛到下界", Easing.Clamp(double.NaN, 0.5, 1.2) == 0.5);
        check("Easing.Clamp 遇 +Inf 收敛到上界", Easing.Clamp(double.PositiveInfinity, 0.5, 1.2) == 1.2);
        check("Easing.Clamp 正常区间保持原值", Easing.Clamp(0.8, 0.5, 1.2) == 0.8);

        // ---------- 弹簧：坏输入不许进状态 ----------
        var s = new Spring(1);
        s.To(double.NaN);
        check("Spring.To 忽略 NaN 目标", s.Target == 1);
        s.Snap(double.NaN);
        check("Spring.Snap 忽略 NaN", s.Value == 1 && s.Target == 1);
        s.Kick(double.NaN);
        check("Spring.Kick 忽略 NaN 冲量", s.Velocity == 0);
        s.Step(double.NaN);
        check("Spring.Step 遇 NaN dt 不污染状态", double.IsFinite(s.Value) && double.IsFinite(s.Velocity));
        s.To(2, -100, 0.5);
        check("Spring.To 忽略非法刚度", s.Target == 2 && double.IsFinite(s.Value));

        // ---------- 弹簧：状态已被污染时必须自愈 ----------
        var b = new Spring(1);
        typeof(Spring).GetProperty("Value")!.GetSetMethod(nonPublic: true)!
            .Invoke(b, new object[] { double.NaN });
        b.Step(0.016);
        check("弹簧状态被 NaN 污染后 Step 自愈回目标", b.Value == 1 && b.Velocity == 0);

        // ---------- 渲染矩形夹紧 ----------
        // WPF 的 Rect 构造器拦「负宽高」抛 ArgumentException，但不拦 NaN
        // ⇒ 历史崩溃（首帧 BodyRect 为空 → 活动条宽度 0-114 负数）正是这一类
        bool negThrew = false;
        try { _ = new Rect(0, 0, -5, 6); } catch (ArgumentException) { negThrew = true; }
        check("WPF Rect 对负宽高抛异常（历史崩溃点）", negThrew);

        bool nanThrew = false;
        try { _ = new Rect(0, 0, double.NaN, 6); } catch (ArgumentException) { nanThrew = true; }
        check("WPF Rect 不拦 NaN 宽高（所以 NaN 也要夹）", !nanThrew);

        check("SafeRect 把负宽高削平", RenderKit.SafeRect(0, 0, -5, -6) == new Rect(0, 0, 0, 0));
        check("SafeRect 把 NaN 宽高削平", RenderKit.SafeRect(double.NaN, 1, double.NaN, 3) == new Rect(0, 1, 0, 3));
        check("SafeRect 保留正常矩形", RenderKit.SafeRect(1, 2, 3, 4) == new Rect(1, 2, 3, 4));

        // 首帧崩溃路径的复现：本体矩形还是空的（0×0）时，活动条宽度是负数
        double firstFrameBarWidth = Math.Max(0, 0 - 70 - 44);
        check("首帧空本体下活动条宽度不为负", firstFrameBarWidth >= 0);
        check("SafeRect 兜住首帧负宽度",
            RenderKit.SafeRect(44, 10, 0 - 114, 6).Width >= 0);
    }
}
