using Island.App.Services;

namespace Island.Tests;

/// <summary>
/// 「拦下开源库的 Intel GCL 初始化」的判定用例（IntelComputePolicy）。
///
/// 背景（2026-11-24 实测，本机 Core Ultra 5 225H + Arc 130T 核显）：LibreHardwareMonitor 0.9.6 的 Intel 显卡模块
/// 在 Windows 上会无条件初始化 Intel 的 GCL（Level Zero 计算栈）。这一步耗 1.4–1.8 秒 CPU，
/// 并不可预测地申请 700 MB – 4.5 GB 提交内存再释放；而在只有核显的机器上它一个读数都不产生
///（库枚举到的唯一设备是核显，被它自己跳过；核显的读数走 D3D 那条路，不花钱）。
/// 规则：机器上至少有一块 Intel 显卡、且全部是核显 ⇒ 拦下；否则一律保持库的默认行为。
/// </summary>
internal static class IntelComputePolicyTests
{
    public static void Run(Action<string, bool> check)
    {
        // ---------- 纯判定：按显卡清单决定要不要拦 ----------
        (bool IsIntel, bool Integrated)[] onlyIntegrated = { (true, true) };
        check("IntelCompute：只有一块 Intel 核显 ⇒ 拦下 GCL 初始化", IntelComputePolicy.ShouldSkip(onlyIntegrated));

        (bool IsIntel, bool Integrated)[] integratedPlusOthers = { (false, false), (true, true), (false, false) };
        check("IntelCompute：Intel 核显 + 其它厂商的显卡（混合显卡笔记本）⇒ 拦下",
            IntelComputePolicy.ShouldSkip(integratedPlusOthers));

        (bool IsIntel, bool Integrated)[] twoIntegrated = { (true, true), (true, true) };
        check("IntelCompute：两块 Intel 核显 ⇒ 拦下", IntelComputePolicy.ShouldSkip(twoIntegrated));

        (bool IsIntel, bool Integrated)[] discrete = { (true, false) };
        check("IntelCompute：有 Intel 独显 ⇒ 不拦（独显的温度/功耗/风扇只有 GCL 这条路给）",
            !IntelComputePolicy.ShouldSkip(discrete));

        (bool IsIntel, bool Integrated)[] mixed = { (true, true), (true, false) };
        check("IntelCompute：核显 + Intel 独显 ⇒ 不拦", !IntelComputePolicy.ShouldSkip(mixed));

        check("IntelCompute：没有任何 Intel 显卡 ⇒ 不拦（没有可拦的东西）",
            !IntelComputePolicy.ShouldSkip(new (bool, bool)[0]));

        check("IntelCompute：只有其它厂商的显卡 ⇒ 不拦",
            !IntelComputePolicy.ShouldSkip(new[] { (false, false), (false, false) }));

        // ---------- 真机探测：本进程里没有开源库程序集时，必须"不拦 + 不抛异常" ----------
        // 测试工程零 NuGet 依赖，刻意没有加载 LibreHardwareMonitorLib；
        // 真机上探测不到库 / 反射失败时同样走这条"保持默认行为"的路。
        bool skip;
        try { skip = IntelComputePolicy.ShouldSkipOnThisMachine(); }
        catch { check("IntelCompute：真机探测在没有开源库程序集时不抛异常", false); return; }
        check("IntelCompute：真机探测在没有开源库程序集时不拦", !skip);

        bool disabled;
        try { disabled = IntelComputePolicy.Disable(); }
        catch { check("IntelCompute：拦下动作在没有开源库程序集时不抛异常", false); return; }
        check("IntelCompute：拦下动作在没有开源库程序集时如实返回「没拦成」", !disabled);
    }
}
