using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Island.App.Controls;

namespace Island.App.Services;

/// <summary>
/// 毛玻璃背景：后台线程用 GDI StretchBlt(HALFTONE) 把岛身后的屏幕区域直接缩成很小的图，
/// 三次 3×3 平均模糊；主线程构造平铺纹理画刷。
/// 移植自 wisland/ui/glass.py 的做法：
/// - 不用 CAPTUREBLT（它会让分层窗口闪烁）
/// - 截屏要等 DWM 回读（10~30ms），绝不能放 UI 线程
/// - 两次截图至少间隔 1/20s，避免连续唤醒占满 CPU
///
/// 变化检测（2026-11-24 性能优化）：静止桌面上相邻两张缩略图几乎一样，却每一张都触发整画布重绘
/// （空闲时每秒白白重绘两次）。现在先比对再通知：身后的桌面没变就不换图、不重绘；
/// 连续多张没变化就把截图间隔放宽到 <see cref="CalmInterval"/>，一有变化立刻恢复。
/// </summary>
public sealed class GlassBackdrop : IDisposable
{
    private const int DOWN = 12;          // 缩小倍数（越大越糊）
    private const double MinInterval = 1.0 / 20;

    /// <summary>连续这么多张截图都没变化，就认为身后的桌面是静止的。</summary>
    private const int CalmAfter = 6;

    /// <summary>静止时的截图间隔（秒）。此时桌面背景若变化，玻璃色调最长 1 秒跟上（原来是 0.5 秒）。</summary>
    private const double CalmInterval = 1.0;

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

    // 变化检测的状态：只在工作线程（Run/Capture）上访问，不需要加锁
    private byte[]? _prev;

    /// <summary>
    /// 强制换图：截图区变了（本体现在在屏幕上的位置/尺寸不同，底图与新截图区的对应关系必须重建），
    /// 或宿主明确要求（换主题：岛体自己的颜色变了，而截图里能截到岛体自己）。
    /// 这时即使像素比对认为"没变"也要换一次。
    /// </summary>
    private volatile bool _force = true;

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

    /// <summary>
    /// 宿主明确要求换一张底图（如切换主题）：下一次截图即使像素没变也通知重绘。
    /// </summary>
    public void Invalidate()
    {
        _force = true;
        SetWake();
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
        if (urgent || moved)
        {
            _force = true;   // 截图区变了：底图必须跟着换，不能被"像素没变"拦下
            SetWake();
        }
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
        int calm = 0;       // 连续"没变化"的截图张数
        int converge = 0;   // 收敛窗口：还剩几张要无条件换上去（见 ConvergeRounds）
        while (!_stop)
        {
            double interval;
            (int x, int y, int w, int h) want;
            lock (_gate) { interval = _intervalSec; want = _want; }
            if (want == default) { if (!WaitWake(TimeSpan.FromMilliseconds(500))) break; ResetWake(); continue; }

            // 连续多张没变化 ⇒ 身后的桌面是静止的：放慢截图。任何一次变化（或本体移动）立刻恢复原节奏。
            double wait = Math.Max(interval, calm >= CalmAfter ? CalmInterval : MinInterval);
            var until = last.AddSeconds(wait);
            var now = DateTime.UtcNow;
            if (now < until) { if (!WaitWake(until - now)) break; }
            ResetWake();
            if (_stop) break;

            try
            {
                if (Capture(want.x, want.y, want.w, want.h, converge, out var img))
                {
                    if (img is null)
                        calm++;                       // 这一张不用换：不换图、不通知重绘
                    else
                    {
                        calm = 0;
                        converge = ConvergeRounds;   // 换过一张图，岛体颜色会跟着变一步：后面几张要继续换，直到稳定
                        lock (_gate) _tiny = img;
                        Updated?.Invoke();
                    }
                }
            }
            catch { /* 单次失败忽略 */ }
            if (converge > 0) converge--;
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

    // ---------- 变化检测：静止桌面上的重复截图不该触发重绘 ----------
    // 实测（2026-11-24，静止桌面，缩略图 106×20）：相邻两张边缘带最大差 1 级、内部最大差 ≤ 11 级，
    // 变的只有岛体自己的读数；而切换视图时内部有 18–31% 的像素明显变化。
    // 注意 GDI 屏幕截图里能截到岛体自己（分层窗口也在合成结果里），所以"内部"不等于"身后的桌面"：
    //   边缘带（截图区最外圈 = 本体外扩的那 10 DIP）才是本体之外的桌面，对它从严；
    //   内部只有大面积变化才算（半透明/专注模式下透出的桌面变了），岛体读数跳动不算。

    /// <summary>边缘带宽度（缩略图像素）。截图区 = 本体外扩 10 DIP；200% 缩放下外扩 20 物理像素 ÷ 缩小倍数 12 ≈ 2。</summary>
    private const int RingPx = 2;

    /// <summary>边缘带：任一通道差超过它，这个像素算"变了"。</summary>
    private const byte RingDelta = 12;

    /// <summary>边缘带：至少这么多像素变了才算变化（滤掉单像素噪点）。</summary>
    private const int RingMinPixels = 3;

    /// <summary>内部：任一通道差超过它，这个像素算"变了"。</summary>
    private const byte InteriorDelta = 10;

    /// <summary>内部：变了的比例达到它才算变化（岛体读数的跳动远到不了这个比例）。</summary>
    private const double InteriorFraction = 0.10;

    /// <summary>
    /// 两张缩略截图之间，"身后的桌面"是否变了（纯函数，可单测；BGRA，w×h 像素）。
    /// 没有上一张、或尺寸对不上 ⇒ true（宁可多刷新一次）。
    /// </summary>
    internal static bool BackdropChanged(byte[]? prev, byte[] next, int w, int h)
    {
        if (prev is null || prev.Length != next.Length || next.Length != w * h * 4) return true;
        int ringHits = 0, interiorHits = 0, interiorTotal = 0;
        for (int y = 0; y < h; y++)
        {
            bool ringRow = y < RingPx || y >= h - RingPx;
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                int d = 0;
                for (int c = 0; c < 3; c++)
                {
                    int t = next[i + c] - prev[i + c];
                    if (t < 0) t = -t;
                    if (t > d) d = t;
                }
                if (ringRow || x < RingPx || x >= w - RingPx)
                {
                    if (d > RingDelta) ringHits++;
                }
                else
                {
                    interiorTotal++;
                    if (d > InteriorDelta) interiorHits++;
                }
            }
        }
        return ringHits >= RingMinPixels || interiorHits >= interiorTotal * InteriorFraction;
    }

    /// <summary>
    /// 换过一张图之后，还要无条件继续换的张数（收敛窗口）。
    /// GDI 屏幕截图里能截到岛体自己：底图 = 岛体"上一帧"的模糊像，而岛体 = 58% 主题色 + 42% 底图
    /// ⇒ 每换一张图，岛体与主题色的差值只缩到 0.42 倍。旧实现每张都换，最终完全收敛。
    /// 只靠像素比对会在第 3 步被"没变化"拦下，颜色停在中途 ⇒ 外观变了。所以换过图之后的这几张
    /// 必须无条件换上去：8 张后残差 0.42^8 ≈ 0.1%，与旧实现的稳态看不出区别。
    /// </summary>
    private const int ConvergeRounds = 8;

    /// <summary>
    /// 这一张截图要不要换上去（纯函数，可单测）。forced = 截图区变了或宿主点名要求；
    /// convergeLeft = 收敛窗口剩余张数；changed = 像素比对认为身后的桌面变了。
    /// </summary>
    internal static bool ShouldApply(bool forced, int convergeLeft, bool changed)
        => forced || convergeLeft > 0 || changed;

    // ---------- GDI 截图 ----------

    /// <summary>
    /// 截一张缩略图。返回 false = 截图失败（沿用上一张）；
    /// 返回 true 且 <paramref name="bmp"/> 为 null = 这一张不用换上去（见 <see cref="ShouldApply"/>）。
    /// </summary>
    private bool Capture(int px, int py, int pw, int ph, int convergeLeft, out BitmapSource? bmp)
    {
        bmp = null;
        int tw = Math.Max(2, pw / DOWN), th = Math.Max(2, ph / DOWN);
        IntPtr sdc = GetDC(IntPtr.Zero);
        if (sdc == IntPtr.Zero) return false;
        IntPtr mdc = CreateCompatibleDC(sdc);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = tw, biHeight = -th, biPlanes = 1, biBitCount = 32,
        };
        IntPtr bits = IntPtr.Zero;
        IntPtr hbmp = mdc == IntPtr.Zero ? IntPtr.Zero : CreateDIBSection(mdc, ref bmi, 0, ref bits, IntPtr.Zero, 0);
        if (hbmp == IntPtr.Zero || bits == IntPtr.Zero || mdc == IntPtr.Zero)
        {
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            if (mdc != IntPtr.Zero) DeleteDC(mdc);
            ReleaseDC(IntPtr.Zero, sdc);
            return false;
        }
        try
        {
            IntPtr old = SelectObject(mdc, hbmp);
            SetStretchBltMode(mdc, HALFTONE);
            SetBrushOrgEx(mdc, 0, 0, IntPtr.Zero);
            // 不用 CAPTUREBLT：它会让分层窗口闪烁（wisland 实测结论）
            bool blt = StretchBlt(mdc, 0, 0, tw, th, sdc, px, py, pw, ph, SRCCOPY);
            // 成功失败都先把位图选出 DC：还选在 DC 里的位图 DeleteObject 删不掉，会漏 GDI 对象
            SelectObject(mdc, old);
            if (!blt) return false;

            int bytes = tw * th * 4;
            var buf = new byte[bytes];
            Marshal.Copy(bits, buf, 0, bytes);

            // 三次 3x3 平均模糊（叠加以 1/k 透明度，结果恰为均值，不变亮；三次更"磨砂"）
            buf = BoxBlur(buf, tw, th);
            buf = BoxBlur(buf, tw, th);
            buf = BoxBlur(buf, tw, th);

            // 这一张要不要换上去（不换的话 bmp 留 null）
            bool force = _force;
            _force = false;
            if (!ShouldApply(force, convergeLeft, BackdropChanged(_prev, buf, tw, th))) return true;
            _prev = buf;

            var tiny = BitmapSource.Create(tw, th, 96, 96, PixelFormats.Bgra32, null, buf, tw * 4);
            tiny.Freeze();
            bmp = tiny;
            return true;
        }
        finally
        {
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
