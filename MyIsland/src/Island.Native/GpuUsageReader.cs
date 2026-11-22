using System;
using System.Diagnostics;

namespace Island.Native;

/// <summary>
/// 通过 PDH 的 "GPU Engine" 计数器聚合 GPU 3D 使用率（纯系统 API，无需驱动）。
/// 首次构造会枚举计数器实例，代价略高，只在启动时做一次。
/// 不同显卡/驱动计数器名称可能不同，读不到时优雅返回 0，不让采样崩掉。
/// </summary>
internal sealed class CounterSampleCache
{
    private PerformanceCounter[]? _engines;
    private bool _initialized;
    private string _name = "";

    public string Name => _name;

    public CounterSampleCache()
    {
        TryInit();
    }

    private void TryInit()
    {
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            string[] instances = category.GetInstanceNames();
            var list = new System.Collections.Generic.List<PerformanceCounter>();
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
                }
                catch { /* 单个实例不可读则跳过 */ }
            }
            if (list.Count > 0) { _engines = list.ToArray(); _initialized = true; }
            _name = GuessGpuName();
        }
        catch
        {
            _initialized = false;
            _name = "GPU";
        }
    }

    public double ReadGpuUsage()
    {
        if (!_initialized || _engines is null) return 0;
        try
        {
            double sum = 0;
            foreach (var c in _engines) sum += c.NextValue();
            // 多引擎求和后按 100 封顶
            return Math.Clamp(sum, 0, 100);
        }
        catch { return 0; }
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
                if (!string.IsNullOrEmpty(n)) return n;
            }
        }
        catch { /* WMI 不可用就用默认名 */ }
        return "GPU";
    }
}
