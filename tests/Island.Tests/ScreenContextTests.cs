using Island.Native;

namespace Island.Tests;

/// <summary>
/// 前台窗口全屏/最大化/桌面分类回归用例：
/// - WorkerW / Progman 壁纸桌面窗口即使铺满全屏也必须判为 Desktop（非全屏打扰）
/// - 保留 WS_CAPTION 的浏览器 F11 / HTML5 视频全屏（fillsMonitor 且 !zoomed）必须判为 Borderless（触发避让）
/// - D3D 独占全屏优先判为 Fullscreen
/// - 普通最大化窗口（zoomed）判为 Maximized（缩为指示条而非隐藏）
/// </summary>
internal static class ScreenContextTests
{
    public static void Run(Action<string, bool> check)
    {
        var workerDesktop = ForegroundDetector.ClassifyWindow(
            className: "WorkerW", processName: "explorer", title: "",
            exclusive: false, fillsMonitor: true, zoomed: false);
        check("WorkerW 全屏桌面宿主判为 Desktop 而非 Borderless",
            workerDesktop == ForegroundKind.Desktop
            && !(new ScreenContext { Kind = workerDesktop }.IsDistracting));

        var progmanDesktop = ForegroundDetector.ClassifyWindow(
            className: "Progman", processName: "explorer", title: "Program Manager",
            exclusive: false, fillsMonitor: true, zoomed: false);
        check("Progman 桌面窗口判为 Desktop",
            progmanDesktop == ForegroundKind.Desktop);

        var browserF11 = ForegroundDetector.ClassifyWindow(
            className: "Chrome_WidgetWin_1", processName: "chrome", title: "YouTube - Google Chrome",
            exclusive: false, fillsMonitor: true, zoomed: false);
        check("浏览器 F11 / 全屏视频（铺满显示器且非 Zoomed）判为 Borderless 并触发防打扰",
            browserF11 == ForegroundKind.Borderless
            && new ScreenContext { Kind = browserF11 }.IsDistracting);

        var exclusiveGame = ForegroundDetector.ClassifyWindow(
            className: "UnrealWindow", processName: "Game", title: "Game",
            exclusive: true, fillsMonitor: false, zoomed: false);
        check("独占全屏状态优先判为 Fullscreen",
            exclusiveGame == ForegroundKind.Fullscreen
            && new ScreenContext { Kind = exclusiveGame, Exclusive = true }.IsDistracting);

        var maximizedApp = ForegroundDetector.ClassifyWindow(
            className: "CASCADIA_HOSTING_WINDOW_CLASS", processName: "WindowsTerminal", title: "PowerShell",
            exclusive: false, fillsMonitor: true, zoomed: true);
        check("普通最大化窗口判为 Maximized 且不视为全屏打扰",
            maximizedApp == ForegroundKind.Maximized
            && !(new ScreenContext { Kind = maximizedApp }.IsDistracting));
    }
}
