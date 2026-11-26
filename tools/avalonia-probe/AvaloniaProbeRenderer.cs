using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using Island.App.Themes;
using Island.Avalonia;

namespace Island.Tools.AvaloniaProbe;

/// <summary>
/// 把产品的 <see cref="IslandCanvas"/> **离屏**渲染成位图（不建窗口、不依赖屏幕）。
///
/// 为什么能离屏：渲染走的是 Avalonia 的绘制管线，与窗口/显示器无关 ——
/// 测量 → 排版 → <see cref="RenderTargetBitmap"/>。本机是锁屏会话（屏幕捕获全黑），
/// 但离屏渲染不经过屏幕，所以照样能拿到真像素（Windows 端的 WPF 探针同理）。
///
/// 三条渲染后端路线（2026-11-26 实测）：
///   ① <c>Avalonia.Headless</c> + <c>UseHeadlessDrawing = false</c> ⇒ **走通**。
///      注意必须再显式 <c>UseSkia()</c>：Headless 只提供窗口/调度，
///      真正的绘制后端要自己挂上，否则报 "No rendering system configured"。
///   ② 纯 <c>UseSkia()</c>（无 Headless）⇒ 需要真实窗口宿主，锁屏会话下不可靠。
///   ③ 纯逻辑兜底（不渲染）⇒ 只能覆盖几何，覆盖不到"画出来是什么样"。
/// 路线 ① 的实测细节见 task-11-mac-visual-report.md。
/// </summary>
internal static class AvaloniaProbeRenderer
{
    private static bool _platformReady;

    /// <summary>初始化离屏渲染平台（进程内只能做一次）。</summary>
    public static void EnsurePlatform()
    {
        if (_platformReady) return;
        AppBuilder.Configure<Application>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        _platformReady = true;
    }

    /// <summary>
    /// 渲染一块画布：把 <paramref name="canvas"/> 放进一个铺满面板底色的宿主里，
    /// 测量排版后渲成位图。
    /// </summary>
    /// <param name="canvas">产品画布（<see cref="IslandCanvas"/>），属性已设好。</param>
    /// <param name="width">面板宽（DIP）。</param>
    /// <param name="height">面板高（DIP）。</param>
    /// <param name="surface">面板底色（铺在画布下面，充当"背景"）。</param>
    /// <returns>渲染结果：像素缓冲 + PNG 字节。</returns>
    public static RenderedPanel Render(IslandCanvas canvas, double width, double height, Color surface)
    {
        var host = new Border
        {
            Background = new SolidColorBrush(surface),
            Width = width,
            Height = height,
            Child = canvas,
        };

        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));

        // 96 DPI ⇒ 1 DIP = 1 像素，像素坐标与 DIP 坐标一一对应（几何断言可直接用像素坐标）。
        var bitmap = new RenderTargetBitmap(new PixelSize((int)width, (int)height), new Vector(96, 96));
        bitmap.Render(host);

        int w = (int)width, h = (int)height;
        int stride = w * 4;
        var pixels = new byte[stride * h];
        IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(stride * h);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, w, h), buffer, stride * h, stride);
            System.Runtime.InteropServices.Marshal.Copy(buffer, pixels, 0, stride * h);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms);
        return new RenderedPanel(new ProbeImage(w, h, pixels), ms.ToArray());
    }

    /// <summary>渲染结果：像素缓冲（供判定核心用）+ PNG 字节（落盘给人眼看）。</summary>
    public sealed record RenderedPanel(ProbeImage Image, byte[] PngBytes);

    /// <summary>把 mac 主题色换算成判定核心用的纯字节三元组。</summary>
    public static ProbePalette PaletteOf(Palette palette) => new(
        new ProbeColor(palette.Surface.R, palette.Surface.G, palette.Surface.B),
        new ProbeColor(palette.Elevated.R, palette.Elevated.G, palette.Elevated.B),
        new ProbeColor(palette.Text.R, palette.Text.G, palette.Text.B));
}
