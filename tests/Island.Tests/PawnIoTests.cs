using Island.App.Services;

namespace Island.Tests;

/// <summary>PawnIO 驱动状态分类与本机只读探测用例（替代旧 inpoutx64 SMN 直读测试）。</summary>
internal static class PawnIoTests
{
    public static void Run(Action<string, bool> check)
    {
        var missing = PawnIoDetector.Classify(false, null, false);
        check("未注册 PawnIO 服务时判定为未安装",
            !missing.IsInstalled && !missing.IsRunning && !missing.IsDisabled
            && missing.Summary.Contains("未检测"));

        var disabled = PawnIoDetector.Classify(true, 4, false, @"\SystemRoot\System32\DriverStore\PawnIO.sys");
        check("Start=4 时判定为已禁用",
            disabled.IsInstalled && disabled.IsDisabled && !disabled.IsRunning
            && disabled.Summary.Contains("禁用"));

        var demand = PawnIoDetector.Classify(true, 3, false, @"\SystemRoot\System32\DriverStore\PawnIO.sys");
        check("Start=3（按需启动）未运行时判定为已安装按需调用",
            demand.IsInstalled && !demand.IsDisabled && !demand.IsRunning
            && demand.Summary.Contains("已安装"));

        var running = PawnIoDetector.Classify(true, 3, true, @"\SystemRoot\System32\DriverStore\PawnIO.sys");
        check("运行中时判定为已就绪",
            running.IsInstalled && !running.IsDisabled && running.IsRunning
            && running.Summary.Contains("运行中"));

        var live = PawnIoDetector.Probe();
        check("本机只读探测 PawnIoDetector.Probe() 不抛异常且返回有效摘要",
            !string.IsNullOrWhiteSpace(live.Summary));
    }
}
