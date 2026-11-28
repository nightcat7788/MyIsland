using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Island.App.Controls;

namespace Island.App.Services;

/// <summary>
/// 毛玻璃背景：后台线程用 GDI StretchBlt(HALFTONE, SRCCOPY) 把岛身后的屏幕区域直接缩成很小的图，
/// 三次 3×3 平均模糊；主线程构造平铺纹理画刷。
/// 移植自 wisland/ui/glass.py 的做法：
/// - 不用 CAPTUREBLT（既避免分层窗口闪烁，也让 WPF UpdateLayeredWindow 精灵窗口天然不进入 GDI 桌面位图）
/// - 切勿对 WPF AllowsTransparency 分层窗口调用 SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)，
///   Win11 DWM 下该标志会导致 UpdateLayeredWindow(ULW_ALPHA) 位图表面无法合成而整窗隐形
/// - 截屏要等 DWM 回读（10~30ms），绝不能放 UI 线程
/// - 两次截图至少间隔 1/20s，避免连续唤醒占满 CPU
/// </summary>
public sealed class GlassBackdrop : IDisposable
{
    private const int DOWN = 12;          // 缩小倍数（越大越糊）
    private const double MinInterval = 1.0 / 20;

    private Thread? _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;
    private readonly object _gate = new();

    // 请求参数（屏幕物理像素）
    private (int x, int y, int w, int h) _want;
    private double _intervalSec = 0.1;

    // 最新结果（主线程读）
    private BitmapSource? _tiny;
    private Rect _screenRectDip;

    /// <summary>结果就绪（在后台线程触发；构造画刷需回到 UI 线程）。</summary>
    public event Action? Updated;

    /// <summary>本次结果对应的屏幕区域（DIP）。</summary>
    public Rect ScreenRectDip => _screenRectDip;

    public void Start()
    {
        if (_thread is { IsAlive: true }) return;
        _stop = false;
        _thread = new Thread(Run) { Name = "island-glass", IsBackground = true };
        _thread.Start();
    }

    /// <summary>请求覆盖某块屏幕区域（DIP）。urgent=true 立即截一次（如刚移动完）。</summary>
    public void Request(Rect screenRectDip, double dpiScale, double intervalSec = 0.1, bool urgent = false)
    {
        if (screenRectDip.Width < 4 || screenRectDip.Height < 4) return;
        var phys = (
            (int)Math.Round(screenRectDip.X * dpiScale),
            (int)Math.Round(screenRectDip.Y * dpiScale),
            (int)Math.Round(screenRectDip.Width * dpiScale),
            (int)Math.Round(screenRectDip.Height * dpiScale));
        bool moved;
        lock (_gate)
        {
            moved = _want != phys;
            _want = phys;
            _intervalSec = intervalSec;
            _screenRectDip = screenRectDip;   // 记录 DIP 区域供 BuildBrush 映射
        }
        if (urgent || moved) SetWake();
    }

    /// <summary>等待唤醒事件。返回 false 表示事件已被释放（线程应当收尾退出）。
    /// 事件被释放时 WaitOne 会抛 ObjectDisposedException——这里吞掉让线程正常退出，
    /// 绝不让异常逃到后台线程上（后台线程未处理异常 = 进程直接终止）。</summary>
    private bool WaitWake(TimeSpan timeout)
    {
        try { return _wake.WaitOne(timeout); }
        catch (ObjectDisposedException) { return false; }
    }

    /// <summary>复位唤醒事件（事件已释放时忽略）。</summary>
    private void ResetWake()
    {
        try { _wake.Reset(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>唤醒工作线程（事件已释放时忽略：Dispose 之后不该再有请求）。</summary>
    private void SetWake()
    {
        try { _wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    private void Run()
    {
        var last = DateTime.MinValue;
        while (!_stop)
        {
            double interval;
            (int x, int y, int w, int h) want;
            lock (_gate) { interval = _intervalSec; want = _want; }
            if (want == default) { if (!WaitWake(TimeSpan.FromMilliseconds(500))) break; ResetWake(); continue; }

            var until = last.AddSeconds(Math.Max(interval, MinInterval));
            var now = DateTime.UtcNow;
            if (now < until) { if (!WaitWake(until - now)) break; }
            ResetWake();
            if (_stop) break;

            try
            {
                var img = Capture(want.x, want.y, want.w, want.h);
                if (img is not null)
                {
                    lock (_gate) _tiny = img;
                    Updated?.Invoke();
                }
            }
            catch { /* 单次失败忽略 */ }
            last = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 把最近一次截图构造为覆盖本体区域（窗口 DIP 坐标）的画刷。必须在 UI 线程调用。
    /// Viewbox 相对被填充几何的包围盒：从截图中裁出本体对应的那一块。
    /// </summary>
    public Brush? BuildBrush(Rect bodyWindowDip, double windowLeftScreenDip, double windowTopScreenDip)
    {
        BitmapSource? tiny;
        Rect cover;
        lock (_gate) { tiny = _tiny; cover = _screenRectDip; }
        if (tiny is null || cover.Width < 1 || cover.Height < 1) return null;

        // 截图覆盖区（屏幕 DIP）换算到窗口 DIP 坐标系
        double ox = cover.X - windowLeftScreenDip, oy = cover.Y - windowTopScreenDip;
        var brush = new ImageBrush(tiny)
        {
            Stretch = Stretch.Fill,
            Viewbox = RenderKit.SafeRect(
                (bodyWindowDip.X - ox) / cover.Width,
                (bodyWindowDip.Y - oy) / cover.Height,
                bodyWindowDip.Width / cover.Width,
                bodyWindowDip.Height / cover.Height),
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            TileMode = TileMode.None,
        };
        brush.Freeze();
        return brush;
    }

    // ---------- GDI 截图 ----------
    private BitmapSource? Capture(int px, int py, int pw, int ph)
    {
        int tw = Math.Max(2, pw / DOWN), th = Math.Max(2, ph / DOWN);
        IntPtr sdc = GetDC(IntPtr.Zero);
        if (sdc == IntPtr.Zero) return null;
        IntPtr mdc = CreateCompatibleDC(sdc);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = tw, biHeight = -th, biPlanes = 1, biBitCount = 32,
        };
        IntPtr bits = IntPtr.Zero;
        IntPtr hbmp = CreateDIBSection(mdc, ref bmi, 0, ref bits, IntPtr.Zero, 0);
        if (hbmp == IntPtr.Zero || bits == IntPtr.Zero || mdc == IntPtr.Zero)
        {
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            if (mdc != IntPtr.Zero) DeleteDC(mdc);
            ReleaseDC(IntPtr.Zero, sdc);
            return null;
        }
        IntPtr old = IntPtr.Zero;
        try
        {
            old = SelectObject(mdc, hbmp);
            SetStretchBltMode(mdc, HALFTONE);
            SetBrushOrgEx(mdc, 0, 0, IntPtr.Zero);
            // 不用 CAPTUREBLT：它会让分层窗口闪烁（wisland 实测结论）
            if (!StretchBlt(mdc, 0, 0, tw, th, sdc, px, py, pw, ph, SRCCOPY))
                return null;
            SelectObject(mdc, old);
            old = IntPtr.Zero;

            int bytes = tw * th * 4;
            byte[] buf = new byte[bytes];
            Marshal.Copy(bits, buf, 0, bytes);

            buf = BoxBlur(buf, tw, th);
            buf = BoxBlur(buf, tw, th);
            buf = BoxBlur(buf, tw, th);

            var bmp = BitmapSource.Create(tw, th, 96, 96, PixelFormats.Bgra32, null, buf, tw * 4);
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            // 必须先将 hbmp 从 mdc 中解除选中，否则锁屏/安全桌面下 StretchBlt 失败提前返回时
            // DeleteObject 对仍被选中的位图会返回 FALSE，每次泄漏一个 DIBSection GDI 句柄直至崩进程。
            if (old != IntPtr.Zero) SelectObject(mdc, old);
            DeleteObject(hbmp);
            DeleteDC(mdc);
            ReleaseDC(IntPtr.Zero, sdc);
        }
    }

    private static byte[] BoxBlur(byte[] src, int w, int h)
    {
        var dst = new byte[src.Length];
        // 9 邻域均值（BGRA，premultiplied 无所谓，只做低频近似）
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - 1) * w * 4, y1 = y * w * 4, y2 = Math.Min(h - 1, y + 1) * w * 4;
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - 1) * 4, x1 = x * 4, x2 = Math.Min(w - 1, x + 1) * 4;
                for (int c = 0; c < 4; c++)
                {
                    int sum = src[y0 + x0 + c] + src[y0 + x1 + c] + src[y0 + x2 + c]
                            + src[y1 + x0 + c] + src[y1 + x1 + c] + src[y1 + x2 + c]
                            + src[y2 + x0 + c] + src[y2 + x1 + c] + src[y2 + x2 + c];
                    dst[y1 + x1 + c] = (byte)(sum / 9);
                }
            }
        }
        return dst;
    }

    public void Dispose()
    {
        _stop = true;
        SetWake();
        bool stopped = _thread is null || _thread.Join(500);
        // Join 超时说明工作线程此刻可能正卡在 GDI 抓屏（DWM 回读偶发 >500ms）：
        // 这时把 _wake 释放掉，工作线程后续 WaitOne 会抛 ObjectDisposedException，
        // 后台线程未处理异常 = 进程直接终止（AppDomain.UnhandledException 只记日志，拦不住终止）。
        // 所以只有确认线程已退出才释放事件；否则交给 GC（WaitHandle 有终结器，句柄照样回收）。
        if (stopped) _wake.Dispose();
    }

    // ---------- Win32 ----------
    private const uint SRCCOPY = 0x00CC0020;
    private const int HALFTONE = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint iUsage, ref IntPtr ppvBits, IntPtr hSection, uint dwOffset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr lppt);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);
}
