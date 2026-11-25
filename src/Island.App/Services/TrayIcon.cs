using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Island.App.Services;

/// <summary>
/// 纯 WPF 托盘图标（Shell_NotifyIcon P/Invoke），不依赖 WinForms，避免命名空间冲突。
/// 隐藏一个消息窗口接收托盘回调（左键/右键）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Window _messageWindow;
    private readonly int _id = 1;
    private bool _added;

    private const int WM_TRAYICON = 0x8000;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONUP = 0x0202;

    public event Action? LeftClicked;
    public event Action? RightClicked;

    public TrayIcon(string toolTip)
    {
        _messageWindow = new Window { Width = 0, Height = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        var helper = new WindowInteropHelper(_messageWindow);
        helper.EnsureHandle();
        IntPtr hwnd = helper.Handle;

        var hSource = HwndSource.FromHwnd(hwnd);
        hSource?.AddHook(WndProc);

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = _id,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = LoadBrandIcon() ?? LoadDefaultIcon(),
            szTip = toolTip,
        };
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    /// <summary>载入产品 logo（island-logo.ico）作为托盘图标；缺失回退默认。</summary>
    private static IntPtr? LoadBrandIcon()
    {
        try
        {
            string? dir = Environment.GetEnvironmentVariable("ISLAND_DIR");
            string[] candidates =
            {
                dir is null ? "" : System.IO.Path.Combine(dir, "island-logo.ico"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "island-logo.ico"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "branding", "island-logo.ico"),
            };
            foreach (var path in candidates)
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) continue;
                // LR_LOADFROMFILE(0x10) | LR_DEFAULTSIZE(0x40)
                IntPtr h = LoadImage(IntPtr.Zero, path, 1 /*IMAGE_ICON*/, 0, 0, 0x10 | 0x40);
                if (h != IntPtr.Zero) return h;
            }
        }
        catch { /* 资源缺失/格式问题 → 用默认图标 */ }
        return null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON && wParam.ToInt32() == _id)
        {
            int ev = lParam.ToInt32() & 0xFFFF;
            if (ev == WM_LBUTTONUP) LeftClicked?.Invoke();
            else if (ev == WM_RBUTTONUP) RightClicked?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static IntPtr LoadDefaultIcon()
    {
        // 载入系统默认应用图标
        return LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = new WindowInteropHelper(_messageWindow).Handle,
                uID = _id,
            };
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        _messageWindow.Close();
    }

    private const int NIM_ADD = 0, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType,
        int cxDesired, int cyDesired, uint fuLoad);
}
