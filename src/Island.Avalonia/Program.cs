using Avalonia;
using System.Runtime.InteropServices;

namespace Island.Avalonia;

internal static class Program
{
    private static FileStream? _instanceLock;

    [STAThread]
    public static void Main(string[] args)
    {
        // 日志落盘：代码里有 31 处 Console.Error 诊断点（DDC 失败原因 / 热键注册 / 布局告警），
        // 但 .app 双击启动时 stderr 无人接收 → 用户报障时无现场。重定向到固定文件，滚动保留 3 份。
        RedirectLogs();
        if (!TryAcquireSingleInstance())
        {
            Console.Error.WriteLine("[startup] 已有 MyIsland 实例在运行，本次启动退出。");
            return;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine($"[fatal] UnhandledException: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.Error.WriteLine($"[task] UnobservedTaskException: {e.Exception}");
            e.SetObserved();
        };
        WakeDisplays();               // 必须在 Avalonia 初始化之前
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            try { _instanceLock?.Dispose(); } catch { }
        }
    }

    private static bool TryAcquireSingleInstance()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyIsland");
            Directory.CreateDirectory(dir);
            string lockPath = Path.Combine(dir, "avalonia-instance.lock");
            _instanceLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[startup] single-instance lock skipped: {ex.Message}");
            return true;
        }
    }

    /// <summary>stderr + stdout → macOS: ~/Library/Logs/MyIsland/app.log；其它平台: LocalAppData/MyIsland/avalonia.log。</summary>
    private static void RedirectLogs()
    {
        try
        {
            string dir = OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "MyIsland")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyIsland");
            Directory.CreateDirectory(dir);
            string log = Path.Combine(dir, OperatingSystem.IsMacOS() ? "app.log" : "avalonia.log");
            for (int i = 2; i >= 1; i--)
                if (File.Exists(log + $".{i}"))
                    File.Move(log + $".{i}", log + $".{i + 1}", true);
            if (File.Exists(log)) File.Move(log, log + ".1", true);
            var writer = TextWriter.Synchronized(new StreamWriter(log, append: true) { AutoFlush = true });
            Console.SetError(writer);
            Console.SetOut(writer);
            Console.Error.WriteLine($"[log] === MyIsland 启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} ===");
        }
        catch { /* 日志失败不阻塞启动 */ }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    // ---------- 显示器唤醒 ----------
    private const string IOPMAssertionName = "MyIsland wake display";
    private const int kIOPMUserActiveLocal = 0;

    // 原型：IOReturn IOPMAssertionDeclareUserActivity(CFStringRef AssertionName, IOPMUserActiveType userType, IOPMAssertionID *AssertionID)
    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit", EntryPoint = "IOPMAssertionDeclareUserActivity")]
    private static extern int IOPMAssertionDeclareUserActivity(
        IntPtr assertionName, int userType, out uint assertionIDOut);

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit", EntryPoint = "IOPMAssertionRelease")]
    private static extern void IOPMAssertionRelease(uint assertionID);

    // CFString → 原生指针（CoreFoundation 的字符串是不可变引用计数对象）
    private static class CFString
    {
        private const uint kCFStringEncodingUTF8 = 0x08000100;

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation",
            EntryPoint = "CFStringCreateWithCString")]
        private static extern IntPtr CFStringCreateWithCString(
            IntPtr alloc, [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")]
        private static extern void CFRelease(IntPtr cf);

        /// <summary>建一个 CFString（调用方负责 CFRelease）。</summary>
        public static IntPtr CreateNative(string s)
            => CFStringCreateWithCString(IntPtr.Zero, s, kCFStringEncodingUTF8);

        public static void Release(IntPtr p) { if (p != IntPtr.Zero) CFRelease(p); }
    }

    private static void WakeDisplays()
    {
        if (!OperatingSystem.IsMacOS()) return;
        IntPtr name = IntPtr.Zero;
        try
        {
            uint id = 0;
            name = CFString.CreateNative(IOPMAssertionName);
            if (name == IntPtr.Zero) return;
            int kr = IOPMAssertionDeclareUserActivity(name, kIOPMUserActiveLocal, out id);
            if (kr != 0) { Console.Error.WriteLine($"[wake] IOPMAssertionDeclareUserActivity failed kr={kr}"); return; }
            IOPMAssertionRelease(id);
            Thread.Sleep(350);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[wake] {ex.GetType().Name}: {ex.Message}"); }
        finally { CFString.Release(name); }
    }
}
