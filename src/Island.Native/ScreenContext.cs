using System;
using System.Diagnostics;
using System.IO;
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

        bool exclusive = false;
        if (SHQueryUserNotificationState(out int qns) == 0)
            exclusive = qns is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;

        ForegroundKind kind = ForegroundKind.Other;
        string proc = "";
        string title = "";

        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0) proc = TryGetProcessName(pid);

            string className = GetClass(hwnd);
            title = GetTitle(hwnd);

            bool hasBounds = false;
            RECT frame = default;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out frame, Marshal.SizeOf<RECT>()) == 0)
                hasBounds = true;
            else if (GetWindowRect(hwnd, out frame))
                hasBounds = true;

            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            bool hasMonitor = monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref mi)
                              && mi.rcMonitor.Right > mi.rcMonitor.Left
                              && mi.rcMonitor.Bottom > mi.rcMonitor.Top;

            bool fillsMonitor = false;
            if (hasBounds && hasMonitor)
            {
                RECT m = mi.rcMonitor;
                fillsMonitor = frame.Left <= m.Left + 2 && frame.Top <= m.Top + 2 &&
                               frame.Right >= m.Right - 2 && frame.Bottom >= m.Bottom - 2;
            }

            bool zoomed = IsZoomed(hwnd);
            kind = ClassifyWindow(className, proc, title, exclusive, fillsMonitor, zoomed);
        }
        else if (exclusive)
        {
            kind = ForegroundKind.Fullscreen;
        }

        return ctx with { Kind = kind, Exclusive = exclusive, ProcessName = proc, Title = title };
    }

    /// <summary>纯分类逻辑（供单测验证桌面/全屏视频/最大化判定）。</summary>
    internal static ForegroundKind ClassifyWindow(
        string className, string processName, string title,
        bool exclusive, bool fillsMonitor, bool zoomed)
    {
        if (IsShellDesktopWindow(className, processName, title))
            return ForegroundKind.Desktop;
        if (exclusive)
            return ForegroundKind.Fullscreen;
        // 非桌面窗口铺满整块显示器且并非最大化状态（含保留 WS_CAPTION 的浏览器 F11/HTML5 全屏视频、无边框游戏）
        if (fillsMonitor && !zoomed)
            return ForegroundKind.Borderless;
        if (zoomed)
            return ForegroundKind.Maximized;
        return ForegroundKind.Other;
    }

    private static bool IsShellDesktopWindow(string className, string processName, string title)
    {
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return true;
        return processName.Equals("explorer", StringComparison.OrdinalIgnoreCase)
               && (string.IsNullOrEmpty(title) || title == "Program Manager");
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        int len = GetWindowText(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : "";
    }

    private static string GetClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        int len = GetClassName(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : "";
    }

    private static string TryGetProcessName(uint pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(512);
                int size = sb.Capacity;
                if (QueryFullProcessImageNameW(h, 0, sb, ref size) && size > 0)
                    return Path.GetFileNameWithoutExtension(sb.ToString(0, size));
            }
            finally { CloseHandle(h); }
        }
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
