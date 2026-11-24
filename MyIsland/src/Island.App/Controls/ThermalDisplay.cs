using Island.App.Services;

namespace Island.App.Controls;

/// <summary>
/// 温度快照在监控页上"看得见的样子"（2026-11-24 展开态降耗）。
/// 温度服务大约 1.4 秒推一次新快照，而监控页只用到取整后的温度 / 功耗和一位小数的显存：
/// 两份快照只要这里相同，画出来就逐像素相同，就不必重绘。实机诊断（非管理员）：展开态监控页每 5 秒
/// 重绘 3–4 次，其中只有 1 次是因为监控快照变了，其余全是温度更新触发的白画 —— 每次重绘含软件光栅化约 30ms。
/// 非管理员时温度位一直是"需管理员"，这些重绘一次都不需要。
/// ⚠️ 下面的格式必须与 MonitorPage.OnRender 里画温度的格式一致：CPU 行的温度、显卡行的温度与显存、
///    页脚的功耗 / 主板 / SSD。改那边的显示格式时同步改这里，DesignSystemTests 守着取整边界。
/// 没有任何 WPF 依赖，链进测试工程。
/// </summary>
internal static class ThermalDisplay
{
    /// <summary>两份快照的签名相同 = 画面相同。没有快照 = 全部读不到，与"读数全是空"的快照相同。</summary>
    public static string Signature(ThermalSnapshot? t)
    {
        t ??= new ThermalSnapshot();
        static string Deg(double? v) => v is { } x ? $"{Math.Round(x)}" : "-";
        string vram = t.GpuMemoryUsedGB is { } used ? $"{used:F1}/{t.GpuMemoryTotalGB:F0}" : "-";
        string power = t.CpuPower is { } p && p > 0.01 ? $"C{Math.Round(p)}"
                     : t.GpuPower is { } gp && gp > 0.01 ? $"G{Math.Round(gp)}" : "";
        return $"{Deg(t.CpuTemp)}|{Deg(t.GpuTemp)}|{vram}|{power}|{Deg(t.MotherboardTemp)}|{Deg(t.SsdTemp)}";
    }
}
