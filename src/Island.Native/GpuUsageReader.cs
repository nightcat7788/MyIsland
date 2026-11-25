using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Island.Native;

/// <summary>
/// 通过 PDH 的 "GPU Engine" 计数器聚合 GPU 3D 使用率（纯系统 API，无需驱动）。
/// 首次构造会枚举计数器实例，代价略高，只在启动时做一次。
/// 不同显卡/驱动计数器名称可能不同，读不到时优雅返回 0，不让采样崩掉。
/// </summary>
internal sealed class CounterSampleCache : IDisposable
{
    // 快路径：一个通配符查询，一轮 = 一次 collect + 一次数组读（实测毫秒级）。
    // 旧路径：逐实例 PerformanceCounter，139 个 3D 实例一轮 167ms，每秒一轮就是 17% 单核 —— 只作兜底。
    private PdhWildcardQuery? _engines;
    private PerformanceCounter[]? _legacy;
    private string[]? _legacyNames;   // 与 _legacy 一一对应的实例名（兜底路径分组用）
    private string _name = "";

    public string Name => _name;

    public CounterSampleCache()
    {
        // 快路径：英文计数器名 + 通配符，与系统语言无关
        try
        {
            var query = new PdhWildcardQuery();
            if (query.Open(@"\GPU Engine(*)\Utilization Percentage"))
            {
                _engines = query;
                _name = GuessGpuName();
                return;
            }
            query.Dispose();
        }
        catch { /* 没有 GPU Engine 计数器（如旧 WDDM 驱动）→ 走兜底 */ }

        TryInitLegacy();
    }

    private void TryInitLegacy()
    {
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            string[] instances = category.GetInstanceNames();
            var list = new System.Collections.Generic.List<PerformanceCounter>();
            var names = new System.Collections.Generic.List<string>();
            foreach (var inst in instances)
            {
                // 只关心 3D 渲染引擎，且属于整个 GPU（engtype_3D），排除 copy/video decode
                if (!inst.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                    c.NextValue();
                    list.Add(c);
                    names.Add(inst);
                }
                catch { /* 单个实例不可读则跳过 */ }
            }
            _legacy = list.Count > 0 ? list.ToArray() : null;
            _legacyNames = _legacy is null ? null : names.ToArray();
            _name = GuessGpuName();
        }
        catch
        {
            _legacy = null;
            _legacyNames = null;
            _name = "GPU";
        }
    }

    public double ReadGpuUsage()
    {
        // 微软口径（DirectX 博客 "GPUs in the Task Manager"）：同一个 (适配器, 引擎) 上
        // 各进程的份额**相加** = 该引擎的利用率，多张卡之间再取最大（= 最忙引擎）。
        // 既不是"所有卡相加"，也不是"同一张卡上所有引擎相加"（那是"假满载"）。
        if (_engines is not null) return _engines.ReadMaxByAdapter("engtype_3D", 100);
        if (_legacy is not null && _legacyNames is not null)
        {
            try
            {
                // 兜底路径与快路径同一口径：按 (适配器, 引擎) 分组求和，组间取 max
                var sums = new Dictionary<string, double>(StringComparer.Ordinal);
                double unparsed = 0;
                for (int i = 0; i < _legacy.Length; i++)
                {
                    double v = _legacy[i].NextValue();
                    string key = PdhWildcardQuery.GroupKeyOf(_legacyNames[i]);
                    if (key.Length == 0) { unparsed = Math.Max(unparsed, v); continue; }
                    sums.TryGetValue(key, out double s);
                    sums[key] = s + v;
                }
                double best = unparsed;
                foreach (var kv in sums) best = Math.Max(best, kv.Value);
                return Math.Clamp(best, 0, 100);
            }
            catch { return 0; }
        }
        return 0;
    }

    public void Dispose()
    {
        _engines?.Dispose();
        if (_legacy is not null)
            foreach (var c in _legacy)
                try { c.Dispose(); } catch { }
    }

    private static string GuessGpuName()
    {
        try
        {
            using var search = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController");
            foreach (var obj in search.Get())
            {
                string? n = obj["Name"]?.ToString();
                if (string.IsNullOrEmpty(n)) continue;
                // 虚拟显示器（远控/虚拟显示驱动）常排在最前面，会把真显卡名顶掉
                // （本机实测：GameViewer Virtual Display Adapter 排在 Intel Arc 之前）
                if (n.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("Indirect", StringComparison.OrdinalIgnoreCase))
                    continue;
                return n;
            }
        }
        catch { /* WMI 不可用就用默认名 */ }
        return "GPU";
    }
}
