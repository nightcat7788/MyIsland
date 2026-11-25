namespace Island.App.Services;

/// <summary>CPU 温度的候选传感器（名字 + 读数）。纯数据，不依赖硬件库。</summary>
public sealed record CpuTemperatureCandidate(string Name, double Value);

/// <summary>CPU 温度的取值来源（诊断日志用，也让界面知道这个数是从哪来的）。</summary>
public enum CpuTemperatureSource
{
    /// <summary>没选到（读数未知）。</summary>
    None = 0,

    /// <summary>封装温度（CPU Package）—— Intel/AMD 的标准口径，与 HWiNFO 等工具一致。</summary>
    Package,

    /// <summary>AMD 硅片温度 Tdie —— 比 Tctl 低 10–27°C，更接近真实核心温度。</summary>
    Tdie,

    /// <summary>AMD 的 Tctl/Tdie 合并值（Zen 移动端只有这一个）。</summary>
    TctlTdie,

    /// <summary>AMD 风扇控制温度 Tctl —— 偏保守策略值，通常虚高。</summary>
    Tctl,

    /// <summary>AMD CCD 温度（多 CCD 芯片上按最热的一个算）。</summary>
    Ccd,

    /// <summary>硬件库自己聚合的"最热核心"（Core Max）。</summary>
    CoreMax,

    /// <summary>逐核心读数里取最大值（只有逐核传感器时的兜底）。</summary>
    PerCoreMax,

    /// <summary>硬件库聚合的平均值（Core Average）—— 最低优先级，峰值口径优先。</summary>
    Average,
}

/// <summary>选中的 CPU 温度：数值 + 命中的传感器 + 取值依据。</summary>
public sealed record CpuTemperaturePick(string SensorName, double Value, CpuTemperatureSource Source);

/// <summary>
/// CPU 温度取值规则（**纯规则**，不依赖 LibreHardwareMonitor，可链进零包测试工程断言）。
///
/// 为什么需要它：不同 CPU 暴露的传感器名字差别很大 ——
///   Intel 桌面/移动：CPU Package、Core Max、Core Average、P-Core #n、E-Core #n
///   AMD Zen：Core (Tdie)、Core (Tctl)、Core (Tctl/Tdie)、CCD1 (Tdie)、Core #n
///   还有 Intel 的 "P-Core #n Distance to TjMax" —— 那是**离最高温度还有多远**，不是温度，
///   名字里带 "core"，一旦用"包含 core"的兜底规则就会被当成温度读进来。
/// 所以取值必须分层 + 显式排除干扰项，不能只写一串名字白名单。
///
/// 分层（先命中先用，同层取最大值）：
///   ① Package      封装温度（工业标准口径）
///   ② Tdie         AMD 硅片温度（比 Tctl 准，MyIsland 既有结论：Tctl 虚高 10–27°C）
///   ③ Tctl/Tdie    AMD 合并值
///   ④ Tctl         AMD 风扇控制温度
///   ⑤ CCD          AMD CCD
///   ⑥ Core Max     硬件库聚合的最热核心
///   ⑦ 逐核取最大   只有逐核传感器时的兜底
///   ⑧ Average      平均值，最后兜底（峰值口径优先，均值会低估降频风险）
///
/// 参考：LiteMonitor（Diorser/LiteMonitor，MIT）的 SensorMatcher / ComponentProcessor
/// —— 它的 "package 黄金规则" 与 "排除 Distance/Average/Max 后取多核最大值" 口径被这里吸收，
/// 并补上了它没有的两点：Tdie 优先于 Tctl、以及把来源显式返回给诊断日志。
/// </summary>
public static class CpuTemperatureRule
{
    /// <summary>CPU 温度的物理上限：超过就是坏值（真 CPU 的 TjMax 最高也就 125°C 档）。</summary>
    public const double MaxPlausibleCelsius = 125;

    /// <summary>
    /// 从候选传感器里选出 CPU 温度。选不到返回 null（读数未知，不编造）。
    /// </summary>
    public static CpuTemperaturePick? Pick(IReadOnlyList<CpuTemperatureCandidate>? candidates)
    {
        if (candidates is null || candidates.Count == 0) return null;

        var usable = new List<CpuTemperatureCandidate>();
        foreach (var c in candidates)
        {
            if (c is null) continue;
            if (c.Value <= 0.01 || c.Value > MaxPlausibleCelsius) continue;   // 坏值直接丢
            if (IsDistractor(c.Name)) continue;                               // 距离/SOC/VRM/水泵/风扇
            usable.Add(c);
        }
        if (usable.Count == 0) return null;

        // ① 封装温度：工业标准口径，也是 LiteMonitor 的"黄金规则"第一条
        var hit = Tier(usable, CpuTemperatureSource.Package, n => Has(n, "package"));
        if (hit is not null) return hit;

        // ② AMD 硅片温度（Tdie 单独出现时）：比 Tctl 低 10–27°C，更接近真实核心温度
        hit = Tier(usable, CpuTemperatureSource.Tdie, n => Has(n, "tdie") && !Has(n, "tctl"));
        if (hit is not null) return hit;

        // ③ AMD 的 Tctl/Tdie 合并值
        hit = Tier(usable, CpuTemperatureSource.TctlTdie, n => Has(n, "tctl") && Has(n, "tdie"));
        if (hit is not null) return hit;

        // ④ Tctl：风扇控制用的保守值，通常虚高，放最后
        hit = Tier(usable, CpuTemperatureSource.Tctl, n => Has(n, "tctl"));
        if (hit is not null) return hit;

        // ⑤ CCD：多 CCD 芯片上按最热的一个算
        hit = Tier(usable, CpuTemperatureSource.Ccd, n => Has(n, "ccd"));
        if (hit is not null) return hit;

        // ⑥ 硬件库自己聚合的"最热核心"
        hit = Tier(usable, CpuTemperatureSource.CoreMax, n => Has(n, "core max") || Has(n, "max core"));
        if (hit is not null) return hit;

        // ⑦ 逐核心读数取最大（LiteMonitor 的 ComponentProcessor.GetCpuTemp 口径）
        hit = Tier(usable, CpuTemperatureSource.PerCoreMax, IsPerCore);
        if (hit is not null) return hit;

        // ⑧ 平均值：最后兜底
        return Tier(usable, CpuTemperatureSource.Average, n => Has(n, "average"));
    }

    /// <summary>同一层里取最大值（多 CCD / 多命名重复时取最热的那条）。</summary>
    private static CpuTemperaturePick? Tier(List<CpuTemperatureCandidate> usable, CpuTemperatureSource source,
        Func<string, bool> match)
    {
        CpuTemperatureCandidate? best = null;
        foreach (var c in usable)
        {
            if (!match(c.Name)) continue;
            if (best is null || c.Value > best.Value) best = c;
        }
        return best is null ? null : new CpuTemperaturePick(best.Name, best.Value, source);
    }

    /// <summary>
    /// 是不是"单个核心"的温度传感器（用于逐核取最大）。
    /// 必须把各种聚合量排除掉，否则 Core Average / Core Max 会被当成一颗核心。
    /// </summary>
    private static bool IsPerCore(string name)
    {
        if (!Has(name, "core") && !Has(name, "cpu")) return false;
        foreach (string aggregate in Aggregates)
            if (Has(name, aggregate)) return false;
        return true;
    }

    /// <summary>聚合类关键词（不是"单个核心"的读数）。</summary>
    private static readonly string[] Aggregates =
        { "average", "max", "package", "tdie", "tctl", "ccd" };

    /// <summary>
    /// 干扰项：这些传感器**不是 CPU 核心温度**，混进来就会给出错误读数。
    ///  · "Distance to TjMax" —— 离最高温度还有多远（Intel 每核都有，名字里带 core！）
    ///  · SoC / VRM / 水泵 / 水冷液 / 风扇 —— 都不是核心温度
    /// </summary>
    private static bool IsDistractor(string name)
    {
        foreach (string bad in Distractors)
            if (Has(name, bad)) return true;
        return false;
    }

    private static readonly string[] Distractors =
        { "distance", "tjmax", "soc", "vrm", "pump", "liquid", "coolant", "fan" };

    /// <summary>不区分大小写的包含匹配。</summary>
    private static bool Has(string name, string sub)
        => !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(sub)
           && name.Contains(sub, StringComparison.OrdinalIgnoreCase);
}
