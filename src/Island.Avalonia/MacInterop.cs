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
    private const string ObjcLib = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjcLib, EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjcLib, EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_Bool(IntPtr obj, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr(IntPtr obj, IntPtr sel);

    /// <summary>
    /// 设置「鼠标穿透」：NSWindow.ignoresMouseEvents = true 时，鼠标点击会穿过窗口
    /// （面板还能被看见，但点不到 —— 游戏时就不会误触展开）。
    /// macOS 上实现 click-through 的标准做法；Avalonia 没有内建。
    /// </summary>
    public static void SetIgnoresMouseEvents(IntPtr window, bool ignore)
    {
        if (!OperatingSystem.IsMacOS() || window == IntPtr.Zero) return;
        try
        {
            IntPtr sel = sel_registerName("setIgnoresMouseEvents:");
            objc_msgSend_Bool(window, sel, ignore);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[mac] ignoresMouseEvents: {ex.Message}"); }
    }

    // ---- NSWindow 改尺寸（窗口宽度要跟得上胶囊宽度）----
    // setFrame:display: 有两个参数（NSRect, BOOL），ARM64 下必须完整声明 display 参数（1 字节 BOOL），
    // 否则寄存器里残留随机值会导致窗口不重绘或异常。
    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_Rect_Bool(IntPtr obj, IntPtr sel, CGRect rect, [MarshalAs(UnmanagedType.I1)] bool display);

    /// <summary>NSRect（AppKit 里是 CGFloat 双精度）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect { public double X, Y, Width, Height; }

    /// <summary>
    /// 直接改 NSWindow 的尺寸。Avalonia 的 <c>Window.Width</c> 对**已显示**的窗口不生效
    /// （实测改了窗口实际大小不变，胶囊两端被裁平），所以这里走 AppKit。
    /// </summary>
    public static void SetWindowSize(IntPtr window, double x, double y, double w, double h)
    {
        if (!OperatingSystem.IsMacOS() || window == IntPtr.Zero) return;
        try
        {
            IntPtr sel = sel_registerName("setFrame:display:");
            objc_msgSend_Rect_Bool(window, sel, new CGRect { X = x, Y = y, Width = w, Height = h }, true);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[mac] setFrame: {ex.Message}"); }
    }

    /// <summary>取 NSApp（应用对象）。</summary>
    public static IntPtr NSApp()
    {
        if (!OperatingSystem.IsMacOS()) return IntPtr.Zero;
        IntPtr cls = objc_getClass("NSApplication");
        if (cls == IntPtr.Zero) return IntPtr.Zero;
        IntPtr sel = sel_registerName("sharedApplication");
        return objc_msgSend_IntPtr(cls, sel);
    }

    // ============ 窗口层级（为什么 Avalonia 的 Topmost 不够高）============
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

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_Long(IntPtr obj, IntPtr sel, long value);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_ULong(IntPtr obj, IntPtr sel, ulong value);

    /// <summary>
    /// 设置窗口层级与 collectionBehavior —— 让胶囊真正压在所有窗口之上
    /// （含全屏应用），且切桌面/进全屏都不消失、不进 Cmd+Tab 列表。
    /// </summary>
    public static void SetWindowLevel(IntPtr window, long level, ulong collectionBehavior)
    {
        if (!OperatingSystem.IsMacOS() || window == IntPtr.Zero) return;
        try
        {
            objc_msgSend_Long(window, sel_registerName("setLevel:"), level);
            objc_msgSend_ULong(window, sel_registerName("setCollectionBehavior:"), collectionBehavior);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[mac] setLevel: {ex.Message}"); }
    }
}
