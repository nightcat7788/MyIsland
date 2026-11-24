using System.Reflection;

namespace Island.App.Services;

/// <summary>
/// 拦下开源库在"只有核显"的机器上那次纯开销的 Intel GCL 初始化。
///
/// 背景（2026-11-24 实测，本机 Core Ultra 5 225H + Arc 130T 核显）：LibreHardwareMonitor 0.9.6 的 Intel 显卡模块
///（IntelGpuGroup）在 Windows 上会无条件初始化 Intel 的 GCL —— ControlLib → Level Zero 计算栈
///（ze_loader / ze_intel_gpu64 / igc 图形编译器 / NPU 驱动，三十来个模块）。这一步耗 1.4–1.8 秒 CPU，
/// 并不可预测地申请 700 MB – 4.5 GB 提交内存再立刻释放 —— 之前一直没查到的"峰值工作集 929 MB"就是它
///（重复实验：只做这一步，三次峰值 4143 / 4143 / 4142 MB；和 WPF、和初始化顺序都无关）。
/// 而在只有核显的机器上它一个读数都不产生：GCL 枚举到的唯一设备是核显，被库自己跳过；
/// 核显的显存 / 功耗 / 时钟 / 负载走的是 D3D 那条路（实测 55 ms、没有尖峰）。
///
/// 做法：先用库自己的 D3D 探测（D3DDisplayDevice）问一遍系统"每块 Intel 显卡是不是核显"
///（用的是驱动上报的 HybridIntegrated 标志，和库在 D3D 那条路里的判断是同一个）。
/// 机器上至少有一块 Intel 显卡且全是核显 ⇒ 把库的 IntelGcl.IsAvailable 置为 false，库就不会去初始化它。
/// 只要有 Intel 独显、或者任何一步判断不出来 ⇒ 什么都不做，保持库的默认行为
///（独显的温度 / 功耗 / 风扇只有 GCL 这条路给，宁可多花那一下也不能丢读数）。
///
/// 代价（实测逐个传感器对比）：拦下后核显少一个读数 —— GPU 核心时钟（Clock:GPU Core）；
/// 功耗、各路负载、显存 用/空闲/总量 全部不变。MyIsland 没有任何界面显示 GPU 核心时钟
///（GpuCoreClockMHz 只在数据模型里传递），所以界面上看不出区别。
///
/// 对库只用反射按名字取类型，没有编译期依赖；库升级后名字对不上也只是"拦不成"，退回默认行为。
/// 更彻底的办法是上游把这次初始化改成按需（只有存在 Intel 独显时才做）。
/// </summary>
internal static class IntelComputePolicy
{
    private const string LibraryName = "LibreHardwareMonitorLib";
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>
    /// 纯判定：按显卡清单决定要不要拦。
    /// 至少有一块 Intel 显卡、且每一块 Intel 显卡都是核显 ⇒ true；其它厂商的显卡不参与判断。
    /// </summary>
    internal static bool ShouldSkip(IReadOnlyList<(bool IsIntel, bool Integrated)> adapters)
    {
        bool anyIntel = false, allIntegrated = true;
        foreach (var (isIntel, integrated) in adapters)
        {
            if (!isIntel) continue;
            anyIntel = true;
            if (!integrated) allIntegrated = false;
        }
        return anyIntel && allIntegrated;
    }

    // 探测结果缓存：显卡清单在一次进程生命周期里不会变，只探一次
    private static bool _probed, _skip;

    /// <summary>
    /// 在本机上要不要拦。库不在、或者任何一块 Intel 显卡问不出来 ⇒ false（不拦）。绝不抛异常。
    /// </summary>
    internal static bool ShouldSkipOnThisMachine()
    {
        if (_probed) return _skip;
        try { _skip = Probe(); }
        catch { _skip = false; }   // 判断不出来就不拦
        _probed = true;
        return _skip;
    }

    private static bool Probe()
    {
        var library = Type.GetType($"LibreHardwareMonitor.Hardware.Computer, {LibraryName}", throwOnError: false)?.Assembly;
        if (library is null) return false;
        var d3d = library.GetType("LibreHardwareMonitor.Hardware.D3DDisplayDevice", throwOnError: false);
        var getIdentifiers = d3d?.GetMethod("GetDeviceIdentifiers", Static);
        var getInfo = d3d?.GetMethod("GetDeviceInfoByIdentifier", Static);
        if (getIdentifiers is null || getInfo is null) return false;
        if (getIdentifiers.Invoke(null, null) is not string[] identifiers) return false;

        var adapters = new List<(bool IsIntel, bool Integrated)>();
        foreach (string? id in identifiers)
        {
            if (id is null || id.IndexOf("VEN_8086", StringComparison.OrdinalIgnoreCase) < 0) continue;   // 不是 Intel 显卡
            var args = new object?[] { id, null };
            // 库对软件适配器（BasicRender 之类）返回 false；带 Intel 厂商号却问不出来的也按"不知道"处理 ⇒ 不拦
            if (getInfo.Invoke(null, args) is not true || args[1] is not { } info) return false;
            // 注意要在返回对象的实际类型上找字段：参数类型是按引用的（D3DDeviceInfo&），在它上面 GetField 只会得到 null
            //（2026-11-24 实测踩过：那样写在本机上永远"判断不出来"，策略形同虚设）
            if (info.GetType().GetField("Integrated", BindingFlags.Public | BindingFlags.Instance)?.GetValue(info) is not bool integrated)
                return false;
            adapters.Add((true, integrated));
        }
        return ShouldSkip(adapters);
    }

    /// <summary>
    /// 拦下：把库的 IntelGcl.IsAvailable 置为 false（IntelGpuGroup 构造时只看这一个属性）。
    /// 必须在创建第一个 Computer 之前调用。返回是否拦成；拦不成不抛异常，调用方照常使用库。
    /// 读一次 IsAvailable 会触发它的静态构造——那只是探测 ControlLib.dll 的两个导出函数，不会初始化计算栈。
    /// </summary>
    internal static bool Disable()
    {
        try
        {
            var gcl = Type.GetType($"LibreHardwareMonitor.Interop.IntelGcl, {LibraryName}", throwOnError: false);
            var available = gcl?.GetProperty("IsAvailable", Static);
            if (gcl is null || available is null) return false;
            if (available.GetValue(null) is false) return true;   // 本来就不可用（机器上没有 Intel 控制库）：无需拦
            var backing = gcl.GetField("<IsAvailable>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static);
            if (backing is null) return false;                    // 自动属性的写法变了：拦不成
            backing.SetValue(null, false);
            return true;
        }
        catch { return false; }
    }
}
