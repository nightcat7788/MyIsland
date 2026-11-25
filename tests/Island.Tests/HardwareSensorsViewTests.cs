using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Island.App.Controls;
using Island.App.Services;
using Island.App.Themes;
using Island.Tools.VisualProbe;

namespace Island.Tests;

/// <summary>
/// 硬件传感器面板的**离屏渲染**用例（2026-11-26 task4）。
///
/// 解决的是"验证盲区"：本机屏幕捕获在锁屏会话里只能拍到全黑，肉眼确认不了的事交给机器判。
/// 这里把硬件传感器视图**离屏**渲染成位图（不建窗口），判定：
///  ① 渲染结果**不是全黑**、且确实有文字像素（不是一张空白的列表）；
///  ② 读不到的那行显示的是"未知 / 需管理员"，**不是 0**（红线，界面层的最后一跳）；
///  ③ 刷新时（传感器清单没变、只有读数变了）**不重建列表** ⇒ 不触发整窗重排。
///
/// 被测对象在 src/Island.App/Controls/HardwareSensorsWindow.cs（视图层只依赖
/// IHardwareSensorSource 接口，不依赖硬件库 ⇒ 可以链进零包测试工程）。
/// </summary>
internal static class HardwareSensorsViewTests
{
    public static void Run(Action<string, bool> check)
    {
        var palette = Palette.Dark;

        // 合成读数：有值 / 读不到 / 需要提权 / 真的 0 / 危险温度，各一条。
        var source = new FakeSource
        {
            Elevated = false,
            Sensors = new List<HardwareSensorEntry>
            {
                new("AMD Ryzen 9", "CPU Package", HardwareSensorCategory.Temperature, 55.0, true),
                new("AMD Ryzen 9", "Core (Tdie)", HardwareSensorCategory.Temperature, null, true),
                new("NVIDIA RTX", "GPU Fan", HardwareSensorCategory.Fan, 0.0, false),
                new("NVIDIA RTX", "GPU Core", HardwareSensorCategory.Temperature, 95.0, false),
                new("NVIDIA RTX", "GPU Memory Used", HardwareSensorCategory.Data, null, false),
                new("主板", "System Fan #1", HardwareSensorCategory.Fan, null, true),
            },
        };

        HardwareSensorsView? view = null;
        Exception? failure = null;
        RunOnSta(() =>
        {
            try
            {
            view = new HardwareSensorsView(palette, source);
            var bitmap = VisualProbeCore.Render(view, 560, 620, palette);

            var (painted, ink, total) = PixelStats(bitmap, palette.Surface);
            check("硬件传感器面板：渲染结果非全黑（非背景像素占比 > 2%）",
                (double)painted / total > 0.02);
            Console.WriteLine($"      非背景像素 {painted}/{total} = {(double)painted / total:P2}");
            check("硬件传感器面板：有文字像素（不是一张空列表）",
                (double)ink / total > 0.0005);
            Console.WriteLine($"      高亮像素 {ink}/{total} = {(double)ink / total:P3}");

            // 读数位：读不到 ⇒ 未知 / 需管理员；真的 0 ⇒ 0；危险温度原样显示
            var texts = view.RowTexts;
            check("硬件传感器面板：读不到的行显示「未知 / 需管理员」，不是 0",
                texts.Contains(HardwareSensorList.UnknownText)
                && texts.Contains(HardwareSensorList.NeedAdminText)
                && !texts.Contains("0.0"));
            Console.WriteLine("      " + string.Join(" | ", texts));
            check("硬件传感器面板：真的 0 的读数显示「0」（与读不到区分）",
                texts.Contains("0"));

            // 刷新（只有读数变）⇒ 不重建列表
            int before = view.RebuildCount;
            source.Sensors = new List<HardwareSensorEntry>
            {
                new("AMD Ryzen 9", "CPU Package", HardwareSensorCategory.Temperature, 56.0, true),
                new("AMD Ryzen 9", "Core (Tdie)", HardwareSensorCategory.Temperature, null, true),
                new("NVIDIA RTX", "GPU Fan", HardwareSensorCategory.Fan, 0.0, false),
                new("NVIDIA RTX", "GPU Core", HardwareSensorCategory.Temperature, 95.0, false),
                new("NVIDIA RTX", "GPU Memory Used", HardwareSensorCategory.Data, null, false),
                new("主板", "System Fan #1", HardwareSensorCategory.Fan, null, true),
            };
            source.Raise();
            check("硬件传感器面板：只有读数变化时不重建列表（刷新不触发整窗重排）",
                view.RebuildCount == before);
            Console.WriteLine($"      重建次数 {before} → {view.RebuildCount}");
            check("硬件传感器面板：刷新后读数更新",
                view.RowTexts.Contains("56.0"));

            // 清单变了（驱动装好、多出传感器）⇒ 才重建
            source.Sensors = new List<HardwareSensorEntry>
            {
                new("AMD Ryzen 9", "CPU Package", HardwareSensorCategory.Temperature, 56.0, true),
                new("NVIDIA RTX", "GPU Fan", HardwareSensorCategory.Fan, 0.0, false),
                new("NVIDIA RTX", "GPU Core", HardwareSensorCategory.Temperature, 95.0, false),
                new("NVIDIA RTX", "GPU Memory Used", HardwareSensorCategory.Data, null, false),
                new("主板", "System Fan #1", HardwareSensorCategory.Fan, null, true),
                new("主板", "CPU Fan", HardwareSensorCategory.Fan, 1500.0, true),
            };
            source.Raise();
            check("硬件传感器面板：传感器清单变了才重建列表",
                view.RebuildCount == before + 1);
            Console.WriteLine($"      重建次数 {before} → {view.RebuildCount}");
            }
            catch (Exception ex)
            {
                // 离屏渲染失败就是失败：把异常记下来，如实报告，不拿默认值凑结果。
                failure = ex;
            }
        });

        if (failure is not null)
        {
            check("硬件传感器面板：离屏渲染不抛异常（渲染失败即门禁失败）", false);
            Console.WriteLine("      " + failure);
            return;
        }
        check("硬件传感器面板：离屏渲染不抛异常（渲染失败即门禁失败）", true);
    }

    /// <summary>在 STA 线程上跑（WPF 的离屏渲染必须在 STA 上做）。</summary>
    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.IsBackground = true;
        worker.Start();
        worker.Join();
        if (failure is not null) throw failure;
    }

    /// <summary>位图统计：非背景像素数、高亮（文字）像素数、像素总数。</summary>
    private static (int Painted, int Ink, int Total) PixelStats(BitmapSource bitmap, Color background)
    {
        int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
        var data = new byte[w * h * 4];
        bitmap.CopyPixels(data, w * 4, 0);
        int painted = 0, ink = 0;
        for (int i = 0; i < w * h; i++)
        {
            byte b = data[i * 4], g = data[i * 4 + 1], r = data[i * 4 + 2];
            int d = Math.Max(Math.Abs(r - background.R), Math.Max(Math.Abs(g - background.G), Math.Abs(b - background.B)));
            if (d >= 2) painted++;
            if (Math.Max(r, Math.Max(g, b)) >= 160) ink++;
        }
        return (painted, ink, w * h);
    }

    /// <summary>合成的数据源：不碰真硬件，渲染结果确定、可当回归基线。</summary>
    private sealed class FakeSource : IHardwareSensorSource
    {
        public IReadOnlyList<HardwareSensorEntry> Sensors { get; set; } = Array.Empty<HardwareSensorEntry>();
        public event Action? SensorsChanged;
        public bool Elevated { get; set; }
        public void RequestRefresh() { /* 测试里不真采样 */ }
        public void Raise() => SensorsChanged?.Invoke();
    }
}
