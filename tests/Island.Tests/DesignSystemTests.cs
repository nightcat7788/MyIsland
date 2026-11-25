using System.Windows;
using System.Windows.Media;
using Island.App.Controls;
using Island.App.Themes;

namespace Island.Tests;

/// <summary>
/// 设计系统的用例（2026-11-24 视觉重做）：等宽数字、每套主题的对比度、渲染层对坏数值的防线。
/// 这些是"看不出来但一坏整块界面就散"的规则，靠眼睛守不住，交给测试守。
/// </summary>
internal static class DesignSystemTests
{
    /// <summary>设置中心里可选的全部内置主题名。</summary>
    private static readonly string[] Builtins =
        { "Dark", "Light", "OneDark", "GitHubDark", "Dracula", "NightOwl", "Island", "Cyberpunk", "Pixel" };

    public static void Run(Action<string, bool> check)
    {
        // ---------- 等宽数字 ----------
        // 数据字体（Consolas）的数字本身就是等宽的。哪天数据字体被换成比例宽的（比如回到 Segoe UI），
        // 逐字排进格子的做法仍然成立，但这组用例要把前提钉住：读数跳数时整行不能抖。
        check("数据字体：1 与 0 等宽（读数跳数不抖的根基）",
            RenderKit.Mono("1", 15, Colors.Black, true, 1).Width == RenderKit.Mono("0", 15, Colors.Black, true, 1).Width);
        check("数据字体不是界面字体（换掉默认字体是这次重做的目的之一）",
            RenderKit.Mono("37", 15, Colors.Black, false, 1).Width != RenderKit.Text("37", 15, Colors.Black, false, 1).Width);

        foreach (var (size, strong) in new[] { (10.0, false), (12.5, true), (17.0, true), (36.0, false) })
        {
            string tag = $"{size}px{(strong ? "半粗" : "常规")}";
            check($"NumWidth：{tag} 下 1111 与 0008 等宽（读数跳数不抖）",
                RenderKit.NumWidth("1111", size, strong, 1) == RenderKit.NumWidth("0008", size, strong, 1));
            check($"NumWidth：{tag} 下宽度 = 格宽 × 位数",
                Math.Abs(RenderKit.NumWidth("1234567890", size, strong, 1) - 10 * RenderKit.DigitCell(size, strong, 1)) < 0.01);
        }
        check("NumWidth：等宽体里小数点 / 冒号与数字同宽（整段读数落在严格的网格上）",
            RenderKit.NumWidth(".", 15, true, 1) == RenderKit.DigitCell(15, true, 1)
            && RenderKit.NumWidth(":", 15, true, 1) == RenderKit.DigitCell(15, true, 1));
        check("NumWidth：坏字号 / 坏 DPI 被夹到合法值，不抛异常", RenderKit.NumWidth("12", double.NaN, true, 1) > 0
            && RenderKit.NumWidth("12", 0, false, double.NaN) > 0);

        // ---------- 对比度（WCAG） ----------
        foreach (var name in Builtins)
        {
            var p = Palette.ByName(name)!;
            check($"{name}：正文对底色 ≥ 7:1", Ratio(p.Text, p.Surface) >= 7);
            check($"{name}：次要文字 ≥ 6:1", Ratio(p.TextDim, p.Surface) >= 6);
            check($"{name}：第三级文字（10px 的说明）≥ 4.5:1", Ratio(p.TextTertiary, p.Surface) >= 4.5);
            check($"{name}：强调色当文字用（选中的电源计划）≥ 4.5:1", Ratio(p.Accent, p.Surface) >= 4.5);
            check($"{name}：轨道 / 发丝线叠在底色上比底色亮 ≥ 12%", Ratio(Over(p.Track, p.Surface), p.Surface) >= 1.12
                && Ratio(Over(p.Hairline, p.Surface), p.Surface) >= 1.12);
            check($"{name}：状态色（警示 / 危险 / 跌）≥ 3:1", Ratio(p.Warning, p.Surface) >= 3
                && Ratio(p.Up, p.Surface) >= 3 && Ratio(p.Down, p.Surface) >= 3);
        }
        check("单色主题（像素）的强调色就是文字色", Palette.Pixel.Accent == Palette.Pixel.Text);
        check("彩色主题的强调色不是文字色（否则选中态分不出来）",
            Builtins.Where(n => n != "Pixel").All(n => Palette.ByName(n)!.Accent != Palette.ByName(n)!.Text));

        // ---------- 渲染层对坏数值的防线 ----------
        // 这些函数都跑在 OnRender 里：抛异常 = 整窗崩。NaN / 超范围的输入只能被削平。
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            bool ok = true;
            try
            {
                ok = RenderKit.Num(dc, "37.5%", 10, 20, double.NaN, Colors.White, false, 1) > 10
                    && RenderKit.Num(dc, "8", 10, 20, 12, Colors.White, true, -1) > 10;
                RenderKit.Meter(dc, RenderKit.SafeRect(0, 0, 50, 3), double.NaN, Palette.Dark, Palette.Dark.Accent);
                RenderKit.Meter(dc, RenderKit.SafeRect(0, 0, 50, 3), 2, Palette.Dark, Palette.Dark.Accent);
                RenderKit.Meter(dc, RenderKit.SafeRect(0, 0, 50, 3), -1, Palette.Dark, Palette.Dark.Accent);
                RenderKit.Hairline(dc, Palette.Dark, 0, 100, double.NaN);
                // 暗色科技的发光件：辉光参数 / 字号 / 占比是 NaN、超范围时只能被削平
                ok &= RenderKit.Num(dc, "37", 10, 20, double.NaN, Colors.White, false, double.NaN) > 10;
                ok &= RenderKit.Num(dc, "8", 10, 20, 0, Colors.White, true, -1) > 10;
                RenderKit.Dial(dc, new Point(20, 20), 40, 3, 0.5, Colors.Black, Colors.White, Colors.White, "", 1, glow: double.NaN);
                RenderKit.Dial(dc, new Point(20, 20), 40, 3, 0.5, Colors.Black, Colors.White, Colors.White, "", 1, glow: -4);
                RenderKit.Dial(dc, new Point(20, 20), 40, 3, 2, Colors.Black, Colors.White, Colors.White, "", 1, glow: 3);
                var spot = RenderKit.Spotlight(Palette.Dark.Accent, double.NaN, double.PositiveInfinity);
                ok &= spot.IsFrozen && RenderKit.ReferenceEquals(spot, RenderKit.Spotlight(Palette.Dark.Accent, double.NaN, double.PositiveInfinity));
                ok &= RenderKit.Halo(Colors.White, 7).A == 255 && RenderKit.Halo(Colors.White, -1).A == 0;

                // 发光文字的位图缓存：同一段文字第二次拿到的是同一张图（没变不重做）；位图冻结、确实有发光的像素；
                // 坏字号 / 坏 DPI 不抛异常
                var glow = new GlowText();
                var g1 = glow.Get("37", 36, Palette.Dark.Accent, false, 1);
                ok &= g1.Bmp.IsFrozen && g1.Bmp.PixelWidth > 20 && g1.Bmp.PixelHeight > 20;
                var gp = new byte[g1.Bmp.PixelWidth * g1.Bmp.PixelHeight * 4];
                g1.Bmp.CopyPixels(gp, g1.Bmp.PixelWidth * 4, 0);
                ok &= gp.Where((b, i) => i % 4 == 3 && b > 0).Any() && gp.Where((b, i) => i % 4 == 3 && b < 255).Any();
                var g2 = glow.Get("37", 36, Palette.Dark.Accent, false, 1);
                ok &= ReferenceEquals(g1.Bmp, g2.Bmp) && g1.Bounds == g2.Bounds && g2.Bounds.Width > g2.Bounds.X * -1;
                var g3 = glow.Get("38", 36, Palette.Dark.Accent, false, 1);
                ok &= !ReferenceEquals(g1.Bmp, g3.Bmp);
                ok &= glow.Get("37", double.NaN, Colors.White, false, double.NaN).Bmp.PixelWidth >= 1;

                // 限宽文本的缓存：同一段字在同一种宽度下拿到的是同一份排版；宽度变了是另一份；坏宽度不抛、按 1 算
                var e1 = RenderKit.Trimmed("explorer", 11, Colors.Gray, 90, false, 1);
                ok &= ReferenceEquals(e1, RenderKit.Trimmed("explorer", 11, Colors.Gray, 90, false, 1));
                ok &= !ReferenceEquals(e1, RenderKit.Trimmed("explorer", 11, Colors.Gray, 60, false, 1));
                ok &= ReferenceEquals(RenderKit.MonoTrimmed("Island.App", 12, Colors.White, 80, false, 1),
                    RenderKit.MonoTrimmed("Island.App", 12, Colors.White, 80, false, 1));
                ok &= RenderKit.Trimmed("explorer", 11, Colors.Gray, double.NaN, false, 1).MaxTextWidth == 1;
                ok &= RenderKit.MonoTrimmed("explorer", double.NaN, Colors.White, -5, true, double.NaN).MaxTextWidth == 1;
                foreach (GlyphKind kind in Enum.GetValues<GlyphKind>())
                {
                    Glyphs.Draw(dc, kind, new Rect(10, 10, 16, 16), Colors.White);
                    Glyphs.Draw(dc, kind, new Rect(0, 0, 0, 0), Colors.White);          // 还没布局（尺寸为 0）
                    Glyphs.Draw(dc, kind, new Rect(double.NaN, 0, 16, double.PositiveInfinity), Colors.White);
                }
            }
            catch { ok = false; }
            check("Num / Meter / Hairline / Glyphs：NaN、超范围、零尺寸输入不抛异常", ok);
        }

        // ---------- 温度更新：画面不变不重绘（2026-11-24 展开态降耗） ----------
        // 这一页只用得到取整后的温度 / 功耗和一位小数的显存。小数位在动、画面却没动的那次更新不能触发重绘；
        // 真会变的（跨过取整边界、读数出现或消失）一个都不能漏。
        static Island.App.Services.ThermalSnapshot Th(double? cpu = null, double? gpu = null, double? used = null, double? total = null,
            double? cpuPower = null, double? gpuPower = null, double? mb = null, double? ssd = null)
            => new() { CpuTemp = cpu, GpuTemp = gpu, GpuMemoryUsedGB = used, GpuMemoryTotalGB = total, CpuPower = cpuPower, GpuPower = gpuPower, MotherboardTemp = mb, SsdTemp = ssd };
        string Show(Island.App.Services.ThermalSnapshot? t) => ThermalDisplay.Signature(t);

        check("温度：小数位在动、取整后没变 → 画面不变",
            Show(Th(cpu: 47.2, gpu: 41.1, mb: 38.2, ssd: 36.1, cpuPower: 14.2, used: 1.41, total: 17.9))
                == Show(Th(cpu: 47.4, gpu: 41.4, mb: 38.4, ssd: 36.4, cpuPower: 14.4, used: 1.43, total: 17.9)));
        check("温度：跨过取整边界 → 画面变了（CPU / 显卡 / 主板 / SSD / 功耗 各查一次）",
            Show(Th(cpu: 47.4)) != Show(Th(cpu: 47.6)) && Show(Th(gpu: 41.4)) != Show(Th(gpu: 41.6))
            && Show(Th(mb: 38.4)) != Show(Th(mb: 38.6)) && Show(Th(ssd: 36.4)) != Show(Th(ssd: 36.6))
            && Show(Th(cpuPower: 14.4)) != Show(Th(cpuPower: 14.6)));
        check("温度：读数出现 / 消失 → 画面变了；没有快照 = 全部读不到",
            Show(null) == Show(Th()) && Show(Th()) != Show(Th(cpu: 47.2)) && Show(Th(used: null)) != Show(Th(used: 1.4)));
        check("温度：显存一位小数变了才算变；总量取整后变了也算",
            Show(Th(used: 1.43)) != Show(Th(used: 1.46, total: 17.9))                       // 1.4 → 1.5
            && Show(Th(used: 1.41, total: 17.9)) == Show(Th(used: 1.43, total: 17.9))        // 1.4 → 1.4
            && Show(Th(used: 1.41, total: 17.9)) != Show(Th(used: 1.41, total: 18.6)));      // /18 → /19
        check("温度：CPU 功耗读不到时改显示 GPU 功耗 —— 两者互换是画面变化",
            Show(Th(cpuPower: 14.2, gpuPower: 20.1)) != Show(Th(cpuPower: null, gpuPower: 20.1))
            && Show(Th(cpuPower: null, gpuPower: 20.1)) != Show(Th(cpuPower: null, gpuPower: null))
            && Show(Th(cpuPower: 0.005, gpuPower: null)) == Show(Th()));   // 0.01 W 以下当没有
    }

    private static double Lum(Color c)
    {
        static double Ch(byte v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    private static double Ratio(Color a, Color b)
    {
        double x = Lum(a), y = Lum(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>半透明的颜色实际画在底色上之后的样子。</summary>
    private static Color Over(Color top, Color under)
    {
        double a = top.A / 255.0;
        return Color.FromRgb((byte)Math.Round(top.R * a + under.R * (1 - a)),
            (byte)Math.Round(top.G * a + under.G * (1 - a)), (byte)Math.Round(top.B * a + under.B * (1 - a)));
    }
}
