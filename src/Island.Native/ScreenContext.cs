using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static Island.Native.NativeMethods;

namespace Island.Native;

public enum ForegroundKind
{
    Desktop,
    Maximized,
    Borderless,
    Fullscreen,
    Other,
}

/// <summary>前台窗口状态的快照，用来决定岛屿是否隐藏 / 缩小成指示条。</summary>
public sealed record ScreenContext
{
    public ForegroundKind Kind { get; init; }
    public bool Exclusive { get; init; }
    public string ProcessName { get; init; } = "";
    /// <summary>前台窗口标题（活动窗口视图用）。</summary>
    public string Title { get; init; } = "";
    public IntPtr Handle { get; init; }

    /// <summary>前台是否处于会被游戏/视频打扰的状态：独占全屏或无边框全屏。</summary>
    public bool IsDistracting =>
        Exclusive || Kind == ForegroundKind.Fullscreen || Kind == ForegroundKind.Borderless;
}

/// <summary>检测前台窗口是否全屏/最大化，参考 BrimDeck/WindowsHost.Detect。</summary>
public static class ForegroundDetector
{
    public static ScreenContext Detect()
    {
        IntPtr hwnd = GetForegroundWindow();
        var ctx = new ScreenContext { Handle = hwnd };

        // 系统独占（D3D 全屏 / 演示模式）
        // QUNS_BUSY = 2，微软官方定义："A full-screen application is running **or** Presentation Settings are applied"
        //   —— 全屏应用在跑**或**开了演示模式都算独占，漏掉它 ⇒ 演示模式/全屏下窗口不隐藏。
        bool exclusive = false;
        if (SHQueryUserNotificationState(out int qns) == 0)
            exclusive = qns is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;

        ForegroundKind kind = ForegroundKind.Other;
        string proc = "";
        string title = "";

        if (hwnd != IntPtr.Zero)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            try
            {
                using var p = Process.GetProcessById((int)pid);
                proc = p.ProcessName;
            }
            catch { /* 进程可能刚退出 */ }

            // 用 DWM 扩展帧拿到真实可见矩形，排除阴影边框造成的"假全屏"
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame,
                                      Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(hwnd, out frame);

            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(monitor, ref mi);
            RECT m = mi.rcMonitor;

            bool fillsMonitor = frame.Left <= m.Left + 2 && frame.Top <= m.Top + 2 &&
                                frame.Right >= m.Right - 2 && frame.Bottom >= m.Bottom - 2;

            bool zoomed = IsZoomed(hwnd);
            title = GetTitle(hwnd);

            if (fillsMonitor && !zoomed)
                kind = HasCaption(hwnd) ? ForegroundKind.Maximized : ForegroundKind.Borderless;
            else if (exclusive)
                kind = ForegroundKind.Fullscreen;
            else if (zoomed)
                kind = ForegroundKind.Maximized;
            else if (proc.Equals("explorer", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(title))
                kind = ForegroundKind.Desktop;
        }
        else if (exclusive)
        {
            kind = ForegroundKind.Fullscreen;
        }

        return ctx with { Kind = kind, Exclusive = exclusive, ProcessName = proc, Title = title ?? "" };
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool HasCaption(IntPtr hwnd)
    {
        IntPtr style = GetWindowLongPtr(hwnd, GWL_STYLE);
        long s = style.ToInt64();
        const long WS_CAPTION = 0x00C00000;
        return (s & WS_CAPTION) != 0;
    }
}
