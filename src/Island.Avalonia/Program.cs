using Avalonia;
using System.Runtime.InteropServices;

namespace Island.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 日志落盘：代码里有 31 处 Console.Error 诊断点（DDC 失败原因 / 热键注册 / 布局告警），
        // 但 .app 双击启动时 stderr 无人接收 → 用户报障时无现场。重定向到固定文件，滚动保留 3 份。
        RedirectLogs();
        WakeDisplays();               // 必须在 Avalonia 初始化之前
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>stderr + stdout → ~/Library/Logs/MyIsland/app.log（滚动保留 3 份）。</summary>
    private static void RedirectLogs()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Logs", "MyIsland");
            Directory.CreateDirectory(dir);
            string log = Path.Combine(dir, "app.log");
            for (int i = 2; i >= 1; i--)
                if (File.Exists(log + $".{i}"))
                    File.Move(log + $".{i}", log + $".{i + 1}", true);
            if (File.Exists(log)) File.Move(log, log + ".1", true);
            var writer = new StreamWriter(log, append: true) { AutoFlush = true };
            Console.SetError(writer);
            Console.SetOut(writer);
            Console.Error.WriteLine($"[log] === MyIsland 启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} ===");
        }
        catch { /* 日志失败不阻塞启动 */ }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    // ---------- 显示器唤醒 ----------
    // 【踩过的坑】外接显示器休眠后，本程序**根本启动不了**：
    //   Avalonia 初始化要建 CVDisplayLink（RenderTimer），而 CVDisplayLink 无法在休眠屏上创建，
    //   抛 "Avalonia.Native was not able to start the RenderTimer. Native error code is: -6661"。
    //   这个异常发生在 Main 的第一行（平台初始化），用户看到的就是"双击没反应"。
    //   复现：外接屏休眠 → 启动 → 崩；执行 caffeinate -u 唤醒 → 启动 → 正常。
    // 修法：初始化前先声明一次用户活动把显示器唤醒，再给系统一点时间建好显示链路。
    private const string IOPMAssertionTypeUserActive = "NoDelaySleepAssertion";
    // 原型：IOPMAssertionDeclareUserActivity(CFStringRef, IOPMAssertionID, IOPMAssertionID *)
    // .NET 的 string → CFStringRef 需要显式转换，这里用 IntPtr 传入 CFString。
    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit", EntryPoint = "IOPMAssertionDeclareUserActivity")]
    private static extern int IOPMAssertionDeclareUserActivity(
        IntPtr assertionType, uint assertionID, out uint assertionIDOut);

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit", EntryPoint = "IOPMAssertionRelease")]
    private static extern void IOPMAssertionRelease(uint assertionID);

    // CFString → 原生指针（CoreFoundation 的字符串是不可变引用计数对象）
    private static class CFString
    {
        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation",
            EntryPoint = "CFStringCreateWithCString")]
        private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, byte[] cStr, int encoding, bool isExternal);
        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFStringGetCStringPtr")]
        private static extern IntPtr CFStringGetCStringPtr(IntPtr theString, int encoding);
        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")]
        private static extern void CFRelease(IntPtr cf);

        /// <summary>建一个 CFString（调用方负责 CFRelease）。</summary>
        public static IntPtr CreateNative(string s)
            => CFStringCreateWithCString(IntPtr.Zero, System.Text.Encoding.UTF8.GetBytes(s + "\0"), 0x08000100, false);

        public static void Release(IntPtr p) { if (p != IntPtr.Zero) CFRelease(p); }
    }

    private static void WakeDisplays()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // CFString 是手动管理的原生对象：释放必须放 finally。
        // 之前 Release 写在 P/Invoke 之后 —— 正常返回没问题，但那一句一旦抛异常
        // （入口点找不到 / 编组失败），指针就泄漏了。
        IntPtr type = IntPtr.Zero;
        try
        {
            // 声明一次"用户活动"：相当于 caffeinate -u，把休眠的显示器唤醒
            uint id = 0;
            type = CFString.CreateNative(IOPMAssertionTypeUserActive);
            int kr = IOPMAssertionDeclareUserActivity(type, 0x6170706C, out id);
            if (kr != 0) { Console.Error.WriteLine($"[wake] IOPMAssertionDeclareUserActivity failed kr={kr}"); return; }
            // 立刻释放（我们只需要"唤醒"这个动作，不是持续阻止休眠）
            IOPMAssertionRelease(id);
            // 给 WindowServer 一点时间重建显示链路（否则 CVDisplayLink 仍可能建不出来）
            Thread.Sleep(350);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[wake] {ex.GetType().Name}: {ex.Message}"); }
        finally { CFString.Release(type); }
    }
}
