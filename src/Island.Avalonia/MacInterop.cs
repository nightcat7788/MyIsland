using System.Runtime.InteropServices;

namespace Island.Avalonia;

/// <summary>
/// macOS 原生互操作。Avalonia 没有内建「鼠标穿透」，必须走 AppKit。
///
/// 实现取舍：只做 objc_msgSend 传 BOOL 的调用 —— 不碰 NSRect 这类结构体返回值
/// （ARM64 上 struct 返回走隐藏指针，C# P/Invoke 声明不了，踩过坑）。
/// </summary>
internal static class MacInterop
{
    [DllImport("/System/Library/Frameworks/AppKit.framework/AppKit", EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/System/Library/Frameworks/AppKit.framework/AppKit", EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/System/Library/Frameworks/AppKit.framework/AppKit", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_Bool(IntPtr obj, IntPtr sel, bool value);

    [DllImport("/System/Library/Frameworks/AppKit.framework/AppKit", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr(IntPtr obj, IntPtr sel);

    /// <summary>
    /// 设置「鼠标穿透」：NSWindow.ignoresMouseEvents = true 时，鼠标点击会穿过窗口
    /// （面板还能被看见，但点不到 —— 游戏时就不会误触展开）。
    /// macOS 上实现 click-through 的标准做法；Avalonia 没有内建。
    /// </summary>
    public static void SetIgnoresMouseEvents(IntPtr window, bool ignore)
    {
        if (window == IntPtr.Zero) return;
        try
        {
            IntPtr sel = sel_registerName("setIgnoresMouseEvents:");
            objc_msgSend_Bool(window, sel, ignore);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[mac] ignoresMouseEvents: {ex.Message}"); }
    }

    // ---- NSWindow 改尺寸（窗口宽度要跟得上胶囊宽度）----
    [DllImport("/System/Library/Frameworks/AppKit.framework/AppKit", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_Struct(IntPtr obj, IntPtr sel, CGRect rect);

    [DllImport("/System/Library/Frameworks/AppKit.framework/AppKit", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_Bool_Int(IntPtr obj, IntPtr sel, bool value, IntPtr extra);

    /// <summary>NSRect（AppKit 里是 CGFloat 双精度）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect { public double X, Y, Width, Height; }

    /// <summary>
    /// 直接改 NSWindow 的尺寸。Avalonia 的 <c>Window.Width</c> 对**已显示**的窗口不生效
    /// （实测改了窗口实际大小不变，胶囊两端被裁平），所以这里走 AppKit。
    /// </summary>
    public static void SetWindowSize(IntPtr window, double x, double y, double w, double h)
    {
        if (window == IntPtr.Zero) return;
        try
        {
            IntPtr sel = sel_registerName("setFrame:display:");
            objc_msgSend_Struct(window, sel, new CGRect { X = x, Y = y, Width = w, Height = h });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[mac] setFrame: {ex.Message}"); }
    }

    /// <summary>取 NSApp（应用对象）。</summary>
    public static IntPtr NSApp()
    {
        IntPtr cls = objc_getClass("NSApplication");
        if (cls == IntPtr.Zero) return IntPtr.Zero;
        IntPtr sel = sel_registerName("sharedApplication");
        return objc_msgSend_IntPtr(cls, sel);
    }

    // ============ 窗口层级（为什么 Avalonia 的 Topmost 不够高）============
    // Avalonia 的 Window.Topmost 在 macOS 上只映射到 NSWindowLevel = NSFloatingWindowLevel(3)，
    // 而普通应用窗口是 NSNormalWindowLevel(0)、菜单栏是 NSStatusWindowLevel(25)、
    // 屏幕保护是 1000。3 这个层级**压不过普通应用窗口** —— 所以置顶「看起来没生效」。
    //
    // Vibe Island（开源的灵动岛实现）能稳稳压在最上面，靠的是：
    //   1) window.level = .statusBar        → NSStatusWindowLevel = 25
    //   2) window.collectionBehavior = ...  → 所有桌面都跟随、全屏时也在、不参与 Cmd+Tab
    // 这里照搬这套：直接用 AppKit 设 level + collectionBehavior。
    //
    // NSWindowLevel（NSInteger，64 位）
    public const long NsNormalWindowLevel = 0;
    public const long NsFloatingWindowLevel = 3;
    public const long NsStatusWindowLevel = 25;

    // NSWindowCollectionBehavior（NSUInteger，64 位）
    public const ulong CollectionCanJoinAllSpaces = 1UL << 0;      // 所有桌面都显示（切 Space 不消失）
    public const ulong CollectionStationary = 1UL << 4;            // 不随桌面切换移动
    public const ulong CollectionIgnoresCycle = 1UL << 6;          // 不出现在 Cmd+Tab 切换列表
    public const ulong CollectionFullScreenAuxiliary = 1UL << 8;   // 全屏应用在前时仍然显示

    /// <summary>「最高可见层级」的完整行为组合。</summary>
    public const ulong CollectionAlwaysOnTop =
        CollectionCanJoinAllSpaces | CollectionStationary | CollectionIgnoresCycle | CollectionFullScreenAuxiliary;

    [DllImport("/System/Library/Frameworks/AppKit.framework/AppKit", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_Long(IntPtr obj, IntPtr sel, long value);

    [DllImport("/System/Library/Frameworks/AppKit.framework/AppKit", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_ULong(IntPtr obj, IntPtr sel, ulong value);

    /// <summary>
    /// 设置窗口层级与 collectionBehavior —— 让胶囊真正压在所有窗口之上
    /// （含全屏应用），且切桌面/进全屏都不消失、不进 Cmd+Tab 列表。
    /// </summary>
    public static void SetWindowLevel(IntPtr window, long level, ulong collectionBehavior)
    {
        if (window == IntPtr.Zero) return;
        try
        {
            // setLevel: （NSInteger）
            objc_msgSend_Long(window, sel_registerName("setLevel:"), level);
            // setCollectionBehavior: （NSUInteger）
            objc_msgSend_ULong(window, sel_registerName("setCollectionBehavior:"), collectionBehavior);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[mac] setLevel: {ex.Message}"); }
    }
}
