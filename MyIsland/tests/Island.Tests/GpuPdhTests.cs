using Island.Native;

namespace Island.Tests;

/// <summary>
/// PDH "GPU Engine" 读数的分组聚合回归用例（2026-11-23 "GPU 恒 100%" 事故）。
///
/// 旧实现有三个叠加缺陷，本用例逐条钉住：
///   ① 累加器跨采样不清零  → 读数单调爬升，两三轮后被 clamp 成 100%（"GPU 永远满载"）
///   ② 适配器键解析假设实例名以 "luid_" 开头（真实名以 "pid_" 开头）→ 所有实例挤进同一组
///   ③ 组内用"求和"而不是"max" → 同一张卡上 139 个 3D 引擎相加 ≈ 48%，真实最忙引擎仅 ≈ 22%
/// </summary>
internal static class GpuPdhTests
{
    // 真实实例名（来自本机 \\GPU Engine(*) 计数器）：
    // pid_<pid>_luid_<LUID高位>_<LUID低位=适配器标识>_phys_<n>_eng_<n>_engtype_<类型>
    private const string GpuA = "pid_1128_luid_0x00000000_0x00010c47_phys_0_eng_0_engtype_3D";
    private const string GpuAOtherPid = "pid_20340_luid_0x00000000_0x00010c47_phys_0_eng_1_engtype_3D";
    private const string GpuB = "pid_4180_luid_0x00000000_0x00011076_phys_0_eng_0_engtype_3D";
    private const string CopyEngine = "pid_1128_luid_0x00000000_0x00010c47_phys_0_eng_0_engtype_Copy";

    public static void Run(Action<string, bool> check)
    {
        check("适配器键取自实例名的 luid 段（不是字面量 luid）",
            PdhWildcardQuery.AdapterKeyOf(GpuA) == "0x00000000_0x00010c47");

        check("同一张卡的不同进程拿到同一个适配器键",
            PdhWildcardQuery.AdapterKeyOf(GpuAOtherPid) == PdhWildcardQuery.AdapterKeyOf(GpuA));

        check("不同显卡拿到不同的适配器键",
            PdhWildcardQuery.AdapterKeyOf(GpuB) != PdhWildcardQuery.AdapterKeyOf(GpuA));

        check("没有 luid 段的实例名取不到键（返回空串而不是抛异常）",
            PdhWildcardQuery.AdapterKeyOf("some_other_counter") == "");

        check("同一张卡的多个引擎取 max 而不是求和",
            Near(PdhWildcardQuery.AggregateForTest(
                new[] { GpuA, GpuAOtherPid }, new[] { 30.0, 40.0 }, "engtype_3D"), 40.0));

        check("多张卡之间取最大值（任务管理器口径）",
            Near(PdhWildcardQuery.AggregateForTest(
                new[] { GpuA, GpuB }, new[] { 30.0, 25.0 }, "engtype_3D"), 30.0));

        check("非 3D 引擎不参与聚合",
            Near(PdhWildcardQuery.AggregateForTest(
                new[] { GpuA, CopyEngine }, new[] { 10.0, 90.0 }, "engtype_3D"), 10.0));

        var rounds = PdhWildcardQuery.AggregateRoundsForTest(
            new[] { GpuA, GpuAOtherPid }, new[] { 30.0, 40.0 }, "engtype_3D", 3);
        check("连续采样不会把上一轮累加进来（旧的恒 100% 根因）",
            rounds.Length == 3 && Near(rounds[0], 40.0) && Near(rounds[1], 40.0) && Near(rounds[2], 40.0));
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1e-9;
}
