using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Island.Native;

/// <summary>
/// 通过 PDH 的 "GPU Engine" 计数器聚合 GPU 3D 使用率（纯系统 API，无需驱动）。
/// 首次构造会枚举计数器实例，代价略高，只在启动时做一次。
/// 不同显卡/驱动计数器名称可能不同，读不到时优雅返回 0，不让采样崩掉。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class CounterSampleCache : IDisposable
{
    private PdhWildcardQuery? _engines;
    private PerformanceCounter[]? _legacy;
    private string[]? _legacyNames;
    private string _name = "";

    public string Name => _name;

    public CounterSampleCache()
    {
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
        var list = new List<PerformanceCounter>();
        var names = new List<string>();
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            string[] instances = category.GetInstanceNames();
            foreach (var inst in instances)
            {
                if (!inst.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    continue;
                PerformanceCounter? c = null;
                try
                {
                    c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                    c.NextValue();
                    list.Add(c);
                    names.Add(inst);
                }
                catch
                {
                    try { c?.Dispose(); } catch { }
                }
            }
            _legacy = list.Count > 0 ? list.ToArray() : null;
            _legacyNames = _legacy is null ? null : names.ToArray();
            _name = GuessGpuName();
        }
        catch
        {
            foreach (var c in list) { try { c.Dispose(); } catch { } }
            _legacy = null;
            _legacyNames = null;
            _name = "GPU";
        }
    }

    public double ReadGpuUsage()
    {
        if (_engines is not null) return _engines.ReadMaxByAdapter("engtype_3D", 100);
        if (_legacy is not null && _legacyNames is not null)
        {
            try
            {
                var sums = new Dictionary<string, double>(StringComparer.Ordinal);
                double unparsed = 0;
                for (int i = 0; i < _legacy.Length; i++)
                {
                    double v = _legacy[i].NextValue();
                    if (!double.IsFinite(v) || v < 0) continue;
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
        _engines = null;
        if (_legacy is not null)
        {
            foreach (var c in _legacy)
            {
                try { c?.Dispose(); } catch { }
            }
            _legacy = null;
        }
    }

    private static string GuessGpuName()
    {
        string? fallback = null;
        try
        {
            using var search = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController");
            using var results = search.Get();
            foreach (System.Management.ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    string? n = obj["Name"]?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(n)) continue;
                    if (n.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("Indirect", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // 双显卡笔记本优先返回独立显卡名（与 ReadGpuUsage 取最忙显卡口径对齐）
                    if (IsLikelyDiscreteGpu(n))
                        return n;
                    fallback ??= n;
                }
            }
        }
        catch { /* WMI 不可用就用默认名 */ }
        return fallback ?? "GPU";
    }

    private static bool IsLikelyDiscreteGpu(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
            || name.Contains("RTX", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GTX", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Quadro", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
