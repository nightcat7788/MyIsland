using System;
using System.Collections.Generic;

namespace Island.App.Services;

/// <summary>一个候选传感器（名字 + 读数）。纯数据，不依赖硬件库。</summary>
public sealed record HardwareSensor(string Name, double Value, int? ReportId = null);

/// <summary>
/// 传感器语义（对应 LiteMonitor 的标准键）。
/// 名字 → 语义的识别交给 <see cref="HardwareSensorRules"/>。
/// </summary>
public enum HardwareSensorKind
{
    CpuTemperature, CpuPower, CpuFan,
    GpuTemperature, GpuPower, GpuFan, GpuMemoryUsed, GpuMemoryTotal, GpuClock,
    StorageTemperature, MotherboardTemperature, MotherboardFan,
}

/// <summary>选中结果：语义 + 命中的传感器名 + 值。</summary>
public sealed record HardwareSensorPick(HardwareSensorKind Kind, string SensorName, double Value);

/// <summary>
/// 显存口径的取值（2026-11-26 task15）。
///
/// 为什么要有它：核显的显存读数走 **D3D 共享内存**，那条路的"总量"是共享内存池大小（≈ 系统内存），
/// **不是显存容量** —— 显示成"显存 1.5/17.9 GB"会让人以为核显有十几 GB 显存（误导性读数）。
/// </summary>
public static class GpuMemoryKinds
{
    /// <summary>厂商显存（"GPU Memory Used/Total"，NVIDIA/AMD 那条路）：总量就是显存容量。</summary>
    public const string Vendor = "vendor";
    /// <summary>D3D 专用显存（"D3D Dedicated Memory Used/Total"）：总量就是显存容量。</summary>
    public const string Dedicated = "dedicated";
    /// <summary>D3D 共享内存（"D3D Shared Memory Used/Total"）：**总量是共享内存池大小，不是显存容量**。</summary>
    public const string Shared = "shared";
    /// <summary>口径未知（判不出来就是未知，绝不猜）。</summary>
    public const string Unknown = "";
}

/// <summary>
/// 显存读数的显示文本与口径合并（纯逻辑：显示层与温度签名共用同一份规则，两端一致）。
/// </summary>
public static class GpuMemoryDisplay
{
    /// <summary>
    /// 显存读数的显示文本。
    ///
    /// 口径决定形状（2026-11-26 task15 的裁决）：
    ///   · 共享（核显）⇒ 只显示已用 + "共享"标注，**不显示"总量"** ——
    ///     那个总量是共享内存池大小（≈ 系统内存），显示出来会被读成"显存容量"；
    ///   · 专用 / 厂商显存 ⇒ 显示"已用/总量"（那就是显存容量），不加额外标注（不加噪音）；
    ///   · 口径未知 ⇒ 只显示已用，既不标口径也不给总量（可能冒充显存容量的数字，宁可少显示）。
    /// </summary>
    public static string Text(double? usedGB, double? totalGB, string? kind)
    {
        if (usedGB is not { } used) return "";
        string usedText = $"{used:F1} GB";
        if (kind == GpuMemoryKinds.Shared) return usedText + " 共享";
        if (kind is GpuMemoryKinds.Vendor or GpuMemoryKinds.Dedicated)
            return totalGB is { } total ? $"{used:F1}/{total:F0} GB" : usedText;
        return usedText;
    }

    /// <summary>
    /// 合并"已用"与"总量"两个读数的口径：两边一致才算那个口径；
    /// 只有一边有口径就用那一边；两边都有但不一致（或都没有）⇒ 未知（**不猜**）。
    /// </summary>
    public static string CombineKind(string? usedKind, string? totalKind)
    {
        if (!string.IsNullOrEmpty(usedKind) && !string.IsNullOrEmpty(totalKind))
            return usedKind == totalKind ? usedKind : GpuMemoryKinds.Unknown;
        if (!string.IsNullOrEmpty(usedKind)) return usedKind;
        if (!string.IsNullOrEmpty(totalKind)) return totalKind;
        return GpuMemoryKinds.Unknown;
    }
}

/// <summary>
/// 显存读数 **+ 口径**（一次选取同时给出两者）。
///
/// 口径**与数值同源**：它就是命中的那一层 —— 显存分层数组（厂商显存 → D3D 专用显存 → D3D 共享显存）
/// 本身就是口径的定义，所以这里不再另算一套判定（2026-11-26 task15）。
/// </summary>
/// <param name="Kind">口径：<see cref="GpuMemoryKinds"/> 里的 vendor / dedicated / shared / 未知（""）。</param>
public sealed record GpuMemoryPick(string SensorName, double Value, string Kind);

/// <summary>
/// 传感器类别（**本类自己的枚举**）。
/// 为什么不直接用 LibreHardwareMonitor 的 SensorType：本类必须零硬件库依赖，
/// 才能链进零 NuGet 包的测试工程（见 tests/Island.Tests/Island.Tests.csproj）。
/// </summary>
public enum HardwareSensorCategory
{
    Temperature, Power, Fan, Clock, Data, Load, Voltage, Control, Other,
}

/// <summary>
/// 硬件传感器取值规则（纯逻辑：无 Win32 / WPF / LibreHardwareMonitor 依赖，可链进测试工程）。
///
/// 背景：MyIsland 早期在 ThermalService 里用"名字白名单 + 包含匹配"挑传感器
/// （Pick(hw, SensorType.Temperature, "CPU Package", "Package", "Tdie", "Tctl")）。
/// 三个硬伤：
///   ① 换一批硬件/固件就可能读不到或读错（名单是按某几台机器的经验攒出来的）；
///   ② 同一台机器上多个同类传感器时，取舍只靠名单顺序；
///   ③ "Tctl"（风扇控制温度，比真实温度虚高 10–27°C）、"Distance to TjMax"
///      （离最高温还有多远，**不是温度**）这类干扰项没有统一排除。
///
/// 本类按 LiteMonitor（Diorser/LiteMonitor，MIT）的三层思路落地：
///   · 匹配：<see cref="KindOf(string, HardwareSensorCategory)"/> —— 把五花八门的名字识别成标准语义；
///   · 裁决：<see cref="Pick(HardwareSensorKind, IReadOnlyList{HardwareSensor})"/> —— 同语义多候选时按分层优先级取舍；
///   · 排除：<see cref="IsDistractor(string)"/> —— 统一剔除距离 / SoC / VRM / 水泵 / 聚合值等干扰项。
///
/// 互指：CPU 温度的分层顺序与 <c>Services/CpuTemperatureRule.cs</c>（既有结论）**完全一致**，
/// 细节说明看那个文件；改一处请同步另一处。
/// </summary>
public static class HardwareSensorRules
{
    /// <summary>温度的物理上限：超过就是坏值（真 CPU 的 TjMax 最高也就 125°C 档）。</summary>
    public const double MaxPlausibleCelsius = 125;

    /// <summary>
    /// 硬干扰项关键词：这些名字代表的**不是**我们要的那个量，混进来就是错读数。
    /// 抄自 LiteMonitor 的排除清单，并补上了 "dtex"（其它厂商的扩展温度域）。
    /// </summary>
    private static readonly string[] DistractorKeywords =
    {
        "distance",  // "P-Core #1 Distance to TjMax" —— 离最高温还有多远，不是温度
        "tjmax",     // 同上
        "soc",       // 片上系统温度（核显/SoC），不是 CPU 封装温度
        "vrm",       // 供电模块温度
        "pump",      // 水泵
        "liquid",    // 水冷液
        "coolant",   // 冷却液
        "dtex",      // 厂商扩展温度域
    };

    /// <summary>
    /// 只在**温度语义**下才算干扰项的关键词。
    /// "CPU Fan" 的转速是合法读数，但把它当温度读进来就是错的 —— 所以要看语义。
    /// </summary>
    private static readonly string[] TemperatureOnlyDistractorKeywords = { "fan" };

    /// <summary>聚合类关键词（"Core Average" 这类聚合口径不是单个读数）。</summary>
    private static readonly string[] AggregateKeywords =
        { "average", "max", "package", "tdie", "tctl", "ccd" };

    // ---------------------------------------------------------------- 排除

    /// <summary>
    /// 这个名字是不是干扰项（代表的不是我们要的那个量）。
    /// 注意"fan"只在温度语义下才算干扰项，见 <see cref="IsDistractor(string, bool)"/>。
    /// </summary>
    public static bool IsDistractor(string name) => IsDistractor(name, isTemperature: false);

    /// <summary>
    /// 这个名字是不是干扰项。
    /// isTemperature=true 时额外排除"fan"（风扇的转速/温度都不是核心温度）。
    /// </summary>
    public static bool IsDistractor(string name, bool isTemperature)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (HasAny(name, DistractorKeywords)) return true;
        if (isTemperature && HasAny(name, TemperatureOnlyDistractorKeywords)) return true;
        // "average"（Core Average）是**聚合口径**而不是"另一个量"，
        // 所以它不进常规候选，但也不算硬干扰项 —— 它由 CPU 温度的最后一层专门放行
        // （见 PickCpuTemperature 里对聚合层的处理）。此处按"干扰项"对外暴露。
        if (Has(name, "average")) return true;
        return false;
    }

    /// <summary>
    /// 硬干扰项检查（不含"average"）—— 聚合值层用它放行 "Core Average"。
    /// </summary>
    private static bool IsHardDistractor(string name, bool isTemperature)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (HasAny(name, DistractorKeywords)) return true;
        if (isTemperature && HasAny(name, TemperatureOnlyDistractorKeywords)) return true;
        return false;
    }

    // ---------------------------------------------------------------- 匹配

    /// <summary>
    /// 名字 → 语义。
    /// isTemperature=true：按温度语义解析（GPU / 存储 / 主板 / CPU）。
    /// isTemperature=false：按非温度语义解析，依次尝试 显存 → 频率 → 风扇 → 功耗
    /// （这个顺序的理由："GPU Memory Used" 只有显存语义能确定；"GPU Core" 频率与功耗都能解释，
    ///   但频率是更常见的诉求，且功耗有更明确的关键词 package/power）。
    /// </summary>
    public static HardwareSensorKind? KindOf(string name, bool isTemperature)
    {
        if (isTemperature) return KindOf(name, HardwareSensorCategory.Temperature);
        HardwareSensorCategory[] order =
        {
            HardwareSensorCategory.Data, HardwareSensorCategory.Clock,
            HardwareSensorCategory.Fan, HardwareSensorCategory.Power,
        };
        foreach (var category in order)
            if (KindOf(name, category) is { } kind) return kind;
        return null;
    }

    /// <summary>
    /// 名字 → 语义（按传感器类别判定）。
    /// 认不出来就返回 null —— 宁可"读不到"，也不要把别的量硬塞给某个语义。
    /// </summary>
    public static HardwareSensorKind? KindOf(string name, HardwareSensorCategory category)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (IsDistractor(name, category == HardwareSensorCategory.Temperature)) return null;
        switch (category)
        {
            case HardwareSensorCategory.Temperature: return KindOfTemperature(name);
            case HardwareSensorCategory.Power: return KindOfPower(name);
            case HardwareSensorCategory.Fan: return KindOfFan(name);
            case HardwareSensorCategory.Clock: return KindOfClock(name);
            case HardwareSensorCategory.Data: return KindOfData(name);
            default: return null;
        }
    }

    private static HardwareSensorKind? KindOfTemperature(string name)
    {
        if (Has(name, "gpu") || Has(name, "hot spot") || Has(name, "hotspot"))
            return HardwareSensorKind.GpuTemperature;
        if (Has(name, "composite") || Has(name, "nand") || Has(name, "ssd") || Has(name, "drive"))
            return HardwareSensorKind.StorageTemperature;
        if (Has(name, "motherboard") || Has(name, "system") || Has(name, "chipset") || Has(name, "pch"))
            return HardwareSensorKind.MotherboardTemperature;
        if (Has(name, "cpu") || Has(name, "core") || Has(name, "package")
            || Has(name, "tdie") || Has(name, "tctl") || Has(name, "ccd"))
            return HardwareSensorKind.CpuTemperature;
        return null;
    }

    private static HardwareSensorKind? KindOfPower(string name)
    {
        if (Has(name, "gpu")) return HardwareSensorKind.GpuPower;
        if (Has(name, "cpu") || Has(name, "package") || Has(name, "core") || Has(name, "cores"))
            return HardwareSensorKind.CpuPower;
        return null;
    }

    private static HardwareSensorKind? KindOfFan(string name)
    {
        if (Has(name, "gpu")) return HardwareSensorKind.GpuFan;
        if (Has(name, "cpu")) return HardwareSensorKind.CpuFan;
        // 其余（System Fan / Chassis Fan / Fan #1…）算主板机箱风扇
        return HardwareSensorKind.MotherboardFan;
    }

    private static HardwareSensorKind? KindOfClock(string name)
    {
        // 只认显卡核心频率；显存频率（GPU Memory）不是同一个量
        if (Has(name, "gpu") && (Has(name, "core") || Has(name, "graphics") || Has(name, "shader")))
            return HardwareSensorKind.GpuClock;
        return null;
    }

    private static HardwareSensorKind? KindOfData(string name)
    {
        if (!Has(name, "memory")) return null;
        if (Has(name, "used")) return HardwareSensorKind.GpuMemoryUsed;
        if (Has(name, "total")) return HardwareSensorKind.GpuMemoryTotal;
        return null;
    }

    // ---------------------------------------------------------------- 裁决

    /// <summary>
    /// 从候选里选出某个语义的最佳读数；选不到返回 null（读数未知，不编造）。
    /// </summary>
    public static HardwareSensorPick? Pick(HardwareSensorKind kind, IReadOnlyList<HardwareSensor>? candidates)
    {
        if (candidates is null || candidates.Count == 0) return null;
        switch (kind)
        {
            case HardwareSensorKind.CpuTemperature:
                return PickCpuTemperature(candidates);
            case HardwareSensorKind.CpuPower:
                return PickByTier(kind, candidates, CpuPowerTiers, takeMax: true);
            case HardwareSensorKind.CpuFan:
                return PickByTier(kind, candidates, CpuFanTiers, takeMax: false);
            case HardwareSensorKind.GpuTemperature:
                return PickByTier(kind, candidates, GpuTemperatureTiers, takeMax: true);
            case HardwareSensorKind.GpuPower:
                return PickByTier(kind, candidates, GpuPowerTiers, takeMax: true);
            case HardwareSensorKind.GpuFan:
                return PickByTier(kind, candidates, GpuFanTiers, takeMax: false);
            case HardwareSensorKind.GpuMemoryUsed:
            case HardwareSensorKind.GpuMemoryTotal:
            {
                var gp = PickGpuMemory(kind, candidates);
                return gp is null ? null : new HardwareSensorPick(kind, gp.SensorName, gp.Value);
            }
            case HardwareSensorKind.GpuClock:
                return PickByTier(kind, candidates, GpuClockTiers, takeMax: false);
            case HardwareSensorKind.StorageTemperature:
                return PickByTier(kind, candidates, StorageTemperatureTiers, takeMax: true);
            case HardwareSensorKind.MotherboardTemperature:
                return PickMotherboardTemperature(candidates);
            case HardwareSensorKind.MotherboardFan:
                return PickByTier(kind, candidates, MotherboardFanTiers, takeMax: false);
            default:
                return null;
        }
    }

    /// <summary>便捷方法：只想要命中的传感器名（诊断日志用）；选不到返回 null。</summary>
    public static string? BestSensorName(HardwareSensorKind kind, IReadOnlyList<HardwareSensor>? candidates)
        => Pick(kind, candidates)?.SensorName;

    /// <summary>便捷方法：CPU 温度命中的传感器名（诊断日志用）；选不到返回 null。</summary>
    public static string? BestTemperatureName(IReadOnlyList<HardwareSensor>? candidates)
        => BestSensorName(HardwareSensorKind.CpuTemperature, candidates);

    /// <summary>
    /// CPU 温度：分层裁决。
    /// 分层顺序与 <c>Services/CpuTemperatureRule.cs</c> **完全一致**（既有结论，不重复发明）：
    ///   ① package 封装温度（工业标准口径）
    ///   ② tdie（不含 tctl）AMD 硅片温度
    ///   ③ tctl/tdie 合并值
    ///   ④ tctl 风扇控制温度（虚高 10–27°C，所以排在最后）
    ///   ⑤ ccd
    ///   ⑥ core max
    ///   ⑦ 逐核读数取最大
    ///   ⑧ core average 兜底
    /// 同层内取**最大值**：多核 / 多 CCD 时取最热的那一个（峰值口径，均值会低估降频风险）。
    /// </summary>
    private static HardwareSensorPick? PickCpuTemperature(IReadOnlyList<HardwareSensor> candidates)
    {
        var usable = new List<HardwareSensor>();
        foreach (var c in candidates)
        {
            if (c is null) continue;
            if (!IsValidValue(HardwareSensorKind.CpuTemperature, c.Value)) continue;
            if (IsDistractor(c.Name, isTemperature: true)) continue;
            usable.Add(c);
        }
        // 注意：这里不能因为 usable 为空就提前返回 —— "Core Average" 走的是下面的聚合层，
        // 它的候选按定义不在 usable 里（聚合口径不是单个读数）。
        foreach (var tier in CpuTemperatureTiers)
        {
            var hit = Best(usable, tier, takeMax: true);
            if (hit is not null)
                return new HardwareSensorPick(HardwareSensorKind.CpuTemperature, hit.Name, hit.Value);
        }

        // ⑧ 聚合值兜底："Core Average" 是聚合口径而不是单个读数，被 IsDistractor 排除在常规层之外，
        //    这里单独放行（但仍要过其它硬干扰项检查，避免 "Average Distance to TjMax" 混进来）。
        var aggregates = new List<HardwareSensor>();
        foreach (var c in candidates)
        {
            if (c is null) continue;
            if (!IsValidValue(HardwareSensorKind.CpuTemperature, c.Value)) continue;
            if (IsHardDistractor(c.Name, isTemperature: true)) continue;
            if (!Has(c.Name, "average")) continue;
            aggregates.Add(c);
        }
        var avg = Best(aggregates, n => Has(n, "average"), takeMax: true);
        return avg is null
            ? null
            : new HardwareSensorPick(HardwareSensorKind.CpuTemperature, avg.Name, avg.Value);
    }

    /// <summary>
    /// 通用分层裁决：按层的先后顺序取第一个有命中的层。
    /// takeMax=true：同层取最大值（温度/功耗/容量用峰值口径，最保守）；
    /// takeMax=false：同层取第一个命中（风扇/频率没有"峰值"语义，
    ///   而且同一层里可能混着显存频率、机箱风扇，取第一个更接近"原来那台机器读的那个量"）。
    /// </summary>
    private static HardwareSensorPick? PickByTier(HardwareSensorKind kind, IReadOnlyList<HardwareSensor> candidates,
        Func<string, bool>[] tiers, bool takeMax)
    {
        var usable = UsableSensors(kind, candidates);
        if (usable.Count == 0) return null;

        foreach (var tier in tiers)
        {
            var hit = Best(usable, tier, takeMax);
            if (hit is not null) return new HardwareSensorPick(kind, hit.Name, hit.Value);
        }
        return null;
    }

    /// <summary>
    /// 可用候选（值合法 + 不是干扰项）—— 分层裁决与显存口径**共用这一份过滤**，不复制判定。
    /// </summary>
    private static List<HardwareSensor> UsableSensors(HardwareSensorKind kind, IReadOnlyList<HardwareSensor> candidates)
    {
        bool isTemperature = kind is HardwareSensorKind.CpuTemperature
            or HardwareSensorKind.GpuTemperature
            or HardwareSensorKind.StorageTemperature
            or HardwareSensorKind.MotherboardTemperature;

        var usable = new List<HardwareSensor>();
        foreach (var c in candidates)
        {
            if (c is null) continue;
            if (!IsValidValue(kind, c.Value)) continue;
            if (IsDistractor(c.Name, isTemperature)) continue;
            usable.Add(c);
        }
        return usable;
    }

    /// <summary>
    /// 显存读数 **+ 口径**（一次选取同时给出两者）。
    ///
    /// 口径**就是命中的那一层**：显存分层数组（厂商显存 → D3D 专用显存 → D3D 共享显存）
    /// 本身就是口径的定义，所以这里不另算一套判定（2026-11-26 task15）。
    /// 命中第 3 层（兜底层）时，只有名字里真写着 "shared" 才算共享口径 ——
    /// 判不出来就写未知（""),**不猜**（那个"总量"若是共享内存池，冒充显存容量就是误导性读数）。
    /// </summary>
    public static GpuMemoryPick? PickGpuMemory(HardwareSensorKind kind, IReadOnlyList<HardwareSensor>? candidates)
    {
        if (candidates is null || candidates.Count == 0) return null;
        Func<string, bool>[] tiers = kind switch
        {
            HardwareSensorKind.GpuMemoryUsed => GpuMemoryUsedTiers,
            HardwareSensorKind.GpuMemoryTotal => GpuMemoryTotalTiers,
            _ => Array.Empty<Func<string, bool>>(),
        };
        if (tiers.Length == 0) return null;

        var usable = UsableSensors(kind, candidates);
        for (int i = 0; i < tiers.Length; i++)
        {
            var hit = Best(usable, tiers[i], takeMax: true);
            if (hit is null) continue;
            return new GpuMemoryPick(hit.Name, hit.Value, GpuMemoryKindOfTier(i));
        }
        return null;
    }

    /// <summary>
    /// 层号 → 口径：**口径就是显存分层的层号**，不另发明一套判定。
    /// 层序与产品既有口径完全一致：0 = 厂商显存（NVIDIA/AMD 的 "GPU Memory Used/Total"）、
    /// 1 = D3D 专用显存、2 = D3D 共享内存（核显走这条 —— 那个"总量"是共享内存池大小，不是显存容量）。
    /// 取值字符串与 <see cref="GpuMemoryKinds"/> 一一对应（由 GpuMemoryKindTests 守住）。
    /// </summary>
    private static string GpuMemoryKindOfTier(int tierIndex) => tierIndex switch
    {
        0 => GpuMemoryKinds.Vendor,
        1 => GpuMemoryKinds.Dedicated,
        2 => GpuMemoryKinds.Shared,
        _ => GpuMemoryKinds.Unknown,
    };

    /// <summary>主板温度：先按标准名字，再按"合理区间内取最大"兜底（LiteMonitor 的 MOBO.Temp 口径）。</summary>
    private static HardwareSensorPick? PickMotherboardTemperature(IReadOnlyList<HardwareSensor> candidates)
    {
        var usable = new List<HardwareSensor>();
        foreach (var c in candidates)
        {
            if (c is null) continue;
            if (!IsValidValue(HardwareSensorKind.MotherboardTemperature, c.Value)) continue;
            if (IsDistractor(c.Name, isTemperature: true)) continue;
            usable.Add(c);
        }
        if (usable.Count == 0) return null;

        foreach (var tier in MotherboardTemperatureTiers)
        {
            var hit = Best(usable, tier, takeMax: true);
            if (hit is not null)
                return new HardwareSensorPick(HardwareSensorKind.MotherboardTemperature, hit.Name, hit.Value);
        }

        // 兜底：优先取"合理区间"（15–68°C）内最大的那条 —— 主板/系统温度的合理上限，
        // 超过这个数的多半是芯片组或 VRM；实在没有才放宽到 (0, 95) 区间取最大。
        HardwareSensor? bestSafe = null, bestLoose = null;
        foreach (var c in usable)
        {
            if (c.Value > 0 && c.Value < 95 && (bestLoose is null || c.Value > bestLoose.Value))
                bestLoose = c;
            if (c.Value >= 15 && c.Value <= 68 && (bestSafe is null || c.Value > bestSafe.Value))
                bestSafe = c;
        }
        var pick = bestSafe ?? bestLoose;
        return pick is null
            ? null
            : new HardwareSensorPick(HardwareSensorKind.MotherboardTemperature, pick.Name, pick.Value);
    }

    /// <summary>某一层里的最佳候选：takeMax 时取最大值，否则取第一个。</summary>
    private static HardwareSensor? Best(List<HardwareSensor> pool, Func<string, bool> match, bool takeMax)
    {
        HardwareSensor? best = null;
        foreach (var c in pool)
        {
            if (!match(c.Name)) continue;
            if (best is null) { best = c; if (!takeMax) return best; continue; }
            if (takeMax && c.Value > best.Value) best = c;
        }
        return best;
    }

    /// <summary>读数是否在合理范围内（温度有上限；风扇/功耗/显存 0 是合法读数）。</summary>
    private static bool IsValidValue(HardwareSensorKind kind, double value)
    {
        switch (kind)
        {
            case HardwareSensorKind.CpuTemperature:
            case HardwareSensorKind.GpuTemperature:
            case HardwareSensorKind.StorageTemperature:
            case HardwareSensorKind.MotherboardTemperature:
                return value > 0.01 && value <= MaxPlausibleCelsius;
            case HardwareSensorKind.CpuPower:
            case HardwareSensorKind.GpuPower:
            case HardwareSensorKind.CpuFan:
            case HardwareSensorKind.GpuFan:
            case HardwareSensorKind.MotherboardFan:
            case HardwareSensorKind.GpuMemoryUsed:
            case HardwareSensorKind.GpuMemoryTotal:
            case HardwareSensorKind.GpuClock:
                return value >= 0;
            default:
                return value >= 0;
        }
    }

    // ---------------------------------------------------------------- 各语义的分层

    /// <summary>CPU 温度分层（与 CpuTemperatureRule.cs 一致；aggregate 层单独处理，见 PickCpuTemperature）。</summary>
    private static readonly Func<string, bool>[] CpuTemperatureTiers =
    {
        n => Has(n, "package"),
        n => Has(n, "tdie") && !Has(n, "tctl"),
        n => Has(n, "tctl") && Has(n, "tdie"),
        n => Has(n, "tctl"),
        n => Has(n, "ccd"),
        n => Has(n, "core max") || Has(n, "max core"),
        IsPerCore,
    };

    /// <summary>CPU 功耗：封装功耗优先，其次带 CPU/cores 的其它口径。</summary>
    private static readonly Func<string, bool>[] CpuPowerTiers =
    {
        n => Has(n, "package"),
        n => Has(n, "cpu") || Has(n, "cores"),
    };

    /// <summary>
    /// CPU 风扇：只认名字里带 "cpu" 的（SuperIO 上的 "CPU Fan" / "CPU #1" / "CPU #1 Fan"）。
    /// 故意不兜底到"任意风扇"—— 主板上的机箱风扇不是 CPU 风扇。
    /// </summary>
    private static readonly Func<string, bool>[] CpuFanTiers =
    {
        n => Has(n, "cpu"),
    };

    /// <summary>GPU 温度：核心温度优先，其次热点温度，最后泛指的 GPU 温度。</summary>
    private static readonly Func<string, bool>[] GpuTemperatureTiers =
    {
        n => Has(n, "core"),
        n => Has(n, "hot spot") || Has(n, "hotspot"),
        n => Has(n, "gpu"),
    };

    /// <summary>GPU 功耗：封装/芯片功耗优先，其次泛指的 power，最后 core 功耗。</summary>
    private static readonly Func<string, bool>[] GpuPowerTiers =
    {
        n => Has(n, "package") || Has(n, "chip power") || Has(n, "ppt"),
        n => Has(n, "power"),
        n => Has(n, "core"),
    };

    /// <summary>GPU 风扇：优先名字里带 gpu 的，否则取第一个风扇。</summary>
    private static readonly Func<string, bool>[] GpuFanTiers =
    {
        n => Has(n, "gpu"),
        n => true,
    };

    /// <summary>
    /// 显存已用：厂商显存（"GPU Memory Used"）→ 专用显存（D3D Dedicated）→ 其它显存口径（含共享）。
    /// 同层取最大值（已用显存取大的 = 压力口径）。
    /// </summary>
    private static readonly Func<string, bool>[] GpuMemoryUsedTiers =
    {
        n => Has(n, "gpu") && Has(n, "memory") && !Has(n, "d3d"),
        n => Has(n, "dedicated") && Has(n, "used"),
        n => Has(n, "memory") && Has(n, "used"),
    };

    /// <summary>显存总量：层级与已用一致；同层取最大值（总量取大的 = 容量更大的那项口径）。</summary>
    private static readonly Func<string, bool>[] GpuMemoryTotalTiers =
    {
        n => Has(n, "gpu") && Has(n, "memory") && !Has(n, "d3d"),
        n => Has(n, "dedicated") && Has(n, "total"),
        n => Has(n, "memory") && Has(n, "total"),
    };

    /// <summary>
    /// GPU 核心频率：core → graphics → shader。
    /// 故意不兜底到"任意时钟" —— 显存频率（GPU Memory）不是核心频率，取错就是错读数。
    /// 同层取第一个（避免把同层的其它频率当成核心频率）。
    /// </summary>
    private static readonly Func<string, bool>[] GpuClockTiers =
    {
        n => Has(n, "core"),
        n => Has(n, "graphics"),
        n => Has(n, "shader"),
    };

    /// <summary>存储温度：SMART 合成温度（Composite）优先，其次驱动直接给的温度，最后 NAND。</summary>
    private static readonly Func<string, bool>[] StorageTemperatureTiers =
    {
        n => Has(n, "composite"),
        n => Has(n, "temperature"),
        n => Has(n, "nand"),
    };

    /// <summary>主板温度的标准名优先级：System → Motherboard → 芯片组/PCH。</summary>
    private static readonly Func<string, bool>[] MotherboardTemperatureTiers =
    {
        n => Has(n, "system"),
        n => Has(n, "motherboard"),
        n => Has(n, "chipset") || Has(n, "pch"),
    };

    /// <summary>机箱风扇：System/Chassis/Case 优先，其次任意带 fan 的。</summary>
    private static readonly Func<string, bool>[] MotherboardFanTiers =
    {
        n => Has(n, "system") || Has(n, "chassis") || Has(n, "case"),
        n => Has(n, "fan"),
    };

    /// <summary>
    /// 是不是"单个核心"的温度传感器（逐核取最大那一层）。
    /// 必须把聚合量排除掉，否则 Core Average / Core Max 会被当成一颗核心。
    /// </summary>
    private static bool IsPerCore(string name)
    {
        if (!Has(name, "core") && !Has(name, "cpu")) return false;
        foreach (string aggregate in AggregateKeywords)
            if (Has(name, aggregate)) return false;
        return true;
    }

    /// <summary>不区分大小写的包含匹配。</summary>
    private static bool Has(string name, string sub)
        => !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(sub)
           && name.Contains(sub, StringComparison.OrdinalIgnoreCase);

    /// <summary>不区分大小写的包含匹配（多关键词）。</summary>
    private static bool HasAny(string name, string[] subs)
    {
        foreach (string sub in subs)
            if (Has(name, sub)) return true;
        return false;
    }
}

// =====================================================================================
// 硬件传感器的数据契约（2026-11-26 task4：硬件详情面板）
// =====================================================================================

/// <summary>
/// 一个**已枚举到**的硬件传感器（只读数据）。
///
/// 为什么要单独一个类型：温度服务只把"挑出来的那几个"读数放进 <c>ThermalSnapshot</c>，
/// 其它被 LibreHardwareMonitor 枚举到、却没有任何入口能看的传感器（各电压轨、各风扇转速、
/// 每颗内存 / 每个 PCIe 设备的温度、主板 SuperIO 温度……）就永远沉在库里。
/// 这个类型把"枚举结果"本身暴露出来，让它们能被列出来。
///
/// ⚠️ <see cref="Value"/> 为 null 表示**读不到**（权限不足 / 驱动缺失 / 该传感器本来就没值），
/// 与"读数为 0"是两回事：0 是合法读数（风扇停转 0 RPM、显卡空闲 0 W），显示层必须区分开。
///
/// ⚠️ 本类型放在**这个文件**里而不是 Controls 下：Island.Avalonia（mac 端）也编译这个文件
/// （它同样编译 ThermalService.cs），数据契约必须两端都在同一个地方，
/// 否则 mac 端会因为"找不到 Island.App.Controls"而编译不过。
/// 显示层（单位 / 格式 / 告警级别）在 <c>Controls/HardwareSensorList.cs</c>。
/// </summary>
/// <param name="Hardware">硬件名（CPU / 显卡 / 主板……）。</param>
/// <param name="Sensor">传感器名（LibreHardwareMonitor 的 ISensor.Name）。</param>
/// <param name="Category">传感器类别（温度 / 功耗 / 风扇 / 电压……）。</param>
/// <param name="Value">当前读数；null = 读不到（**不是 0**）。</param>
/// <param name="RequiresElevation">该传感器是否需要管理员权限才读得到（主板 / SuperIO、CPU 的 MSR 温度等）。</param>
public sealed record HardwareSensorEntry(
    string Hardware,
    string Sensor,
    HardwareSensorCategory Category,
    double? Value,
    bool RequiresElevation)
{
    /// <summary>行内唯一键：刷新时用它判断"还是同一批传感器吗"（决定原地更新还是重建列表）。</summary>
    public string Key => Hardware + "|" + Sensor + "|" + Category;

    /// <summary>是否有读数（读不到 = false，**不等于**读数为 0）。</summary>
    public bool HasValue => Value.HasValue;
}

/// <summary>
/// 硬件传感器的数据源。窗口只认这个接口，不认具体服务 ⇒ 视图层不依赖硬件库，可离屏渲染、可链进测试。
/// </summary>
public interface IHardwareSensorSource
{
    /// <summary>最近一次枚举到的全部传感器（顺序 = 枚举顺序）。</summary>
    IReadOnlyList<HardwareSensorEntry> Sensors { get; }

    /// <summary>枚举结果更新后触发（**可能在后台线程上**触发，订阅方要自己切回 UI 线程）。</summary>
    event Action? SensorsChanged;

    /// <summary>请求立刻重新枚举一次（可从任意线程调用；实现方自己决定在哪个线程干活）。</summary>
    void RequestRefresh();

    /// <summary>当前进程是否以管理员身份运行（决定"读不到"显示"需管理员"还是"未知"）。</summary>
    bool Elevated { get; }
}
