using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Island.App.Services;

/// <summary>
/// 纯 WPF 托盘图标（Shell_NotifyIcon P/Invoke）：
/// - 处理 explorer.exe 重启后的 TaskbarCreated 重建；
/// - 右键弹出前 SetForegroundWindow 保证点击外部可正常收起菜单；
/// - Dispose 时释放自载入的 HICON 句柄。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Window _messageWindow;
    private readonly IntPtr _hwnd;
    private readonly string _toolTip;
    private readonly int _id = 1;
    private readonly uint _taskbarCreatedMsg;
    private IntPtr _ownedIcon;
    private readonly IntPtr _iconHandle;
    private bool _added;

    private const int WM_TRAYICON = 0x8000;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONUP = 0x0202;

    public event Action? LeftClicked;
    public event Action? RightClicked;

    public TrayIcon(string toolTip)
    {
        _toolTip = toolTip;
        _messageWindow = new Window { Width = 0, Height = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        var helper = new WindowInteropHelper(_messageWindow);
        helper.EnsureHandle();
        _hwnd = helper.Handle;

        _taskbarCreatedMsg = Island.Native.NativeMethods.RegisterWindowMessage("TaskbarCreated");

        var hSource = HwndSource.FromHwnd(_hwnd);
        hSource?.AddHook(WndProc);

        _ownedIcon = LoadBrandIcon() ?? IntPtr.Zero;
        _iconHandle = _ownedIcon != IntPtr.Zero ? _ownedIcon : LoadDefaultIcon();
        AddOrRestoreIcon();
    }

    private void AddOrRestoreIcon()
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = _id,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _iconHandle,
            szTip = _toolTip,
        };
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private static IntPtr? LoadBrandIcon()
    {
        try
        {
            string? dir = Environment.GetEnvironmentVariable("ISLAND_DIR");
            string[] candidates =
            {
                System.IO.Path.Combine(AppContext.BaseDirectory, "island-logo.ico"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "branding", "island-logo.ico"),
                dir is null ? "" : System.IO.Path.Combine(dir, "island-logo.ico"),
            };
            foreach (var path in candidates)
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) continue;
                IntPtr h = LoadImage(IntPtr.Zero, path, 1 /*IMAGE_ICON*/, 0, 0, 0x10 | 0x40);
                if (h != IntPtr.Zero) return h;
            }
        }
        catch { }
        return null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg)
        {
            AddOrRestoreIcon();
            return IntPtr.Zero;
        }
        if (msg == WM_TRAYICON && (wParam.ToInt64() & 0xFFFF) == _id)
        {
            long ev = lParam.ToInt64() & 0xFFFF;
            if (ev == WM_LBUTTONUP)
            {
                LeftClicked?.Invoke();
            }
            else if (ev == WM_RBUTTONUP)
            {
                SetForegroundWindow(_hwnd);
                RightClicked?.Invoke();
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static IntPtr LoadDefaultIcon()
        => LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION

    public void Dispose()
    {
        if (_added)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = _id,
            };
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_ownedIcon != IntPtr.Zero)
        {
            DestroyIcon(_ownedIcon);
            _ownedIcon = IntPtr.Zero;
        }
        try { _messageWindow.Close(); } catch { }
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

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
