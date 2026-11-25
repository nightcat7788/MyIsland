using Island.App.Services;

namespace Island.Tests;

/// <summary>
/// 毛玻璃"身后的桌面变了吗"判定（GlassBackdrop.BackdropChanged）的用例。
///
/// 背景：毛玻璃每 0.5 秒截一次图，每一张都触发一次整画布重绘。但实测（2026-11-24，静止桌面）
/// 相邻两张缩略图几乎一样：边缘带最大差 1 级，内部最大差不超过 11 级——变化的只有岛体自己的读数。
/// 现在先比对再决定要不要通知重绘：只有"本体之外的桌面"真的变了才算变化。
///
/// 判定规则（与实现里的常量一致）：
///   截图区 = 本体外扩 10 DIP；缩略图最外圈 2 像素是"边缘带"，对应本体之外的桌面；
///   边缘带出现 ≥3 个像素、任一通道差超过 12 级 ⇒ 变化；
///   内部有 ≥10% 的像素、任一通道差超过 10 级 ⇒ 变化（岛体自己的读数跳动远到不了这个比例）。
/// </summary>
internal static class GlassBackdropTests
{
    // 40×20 的缩略图：边缘带 2 像素 ⇒ 内部 36×16 = 576 像素
    private const int W = 40, H = 20;

    private static byte[] Gray(byte level = 100)
    {
        var buf = new byte[W * H * 4];
        for (int i = 0; i < buf.Length; i += 4)
        {
            buf[i] = buf[i + 1] = buf[i + 2] = level;
            buf[i + 3] = 255;
        }
        return buf;
    }

    private static void Paint(byte[] buf, int x, int y, byte level)
    {
        int i = (y * W + x) * 4;
        buf[i] = buf[i + 1] = buf[i + 2] = level;
    }

    public static void Run(Action<string, bool> check)
    {
        // ---------- 没有可比对象：一律按"变了"处理，宁可多刷新一次 ----------
        check("毛玻璃：首次截图（没有上一张）视为变化", GlassBackdrop.BackdropChanged(null, Gray(), W, H));
        check("毛玻璃：尺寸变化视为变化", GlassBackdrop.BackdropChanged(new byte[8 * 6 * 4], Gray(), W, H));

        // ---------- 完全一样 ----------
        check("毛玻璃：两张完全相同不算变化", !GlassBackdrop.BackdropChanged(Gray(), Gray(), W, H));

        // ---------- 边缘带：本体之外的桌面 ----------
        var ring = Gray();
        Paint(ring, 0, 2, (byte)160); Paint(ring, 0, 3, (byte)160); Paint(ring, 3, 0, (byte)160);
        check("毛玻璃：边缘带明显变化算变化（身后桌面变了）", GlassBackdrop.BackdropChanged(Gray(), ring, W, H));

        var ringNoise = Gray();
        for (int y = 0; y < H; y++) Paint(ringNoise, 0, y, (byte)108);
        check("毛玻璃：边缘带整体轻微变化（8 级，在容差内）不算变化",
            !GlassBackdrop.BackdropChanged(Gray(), ringNoise, W, H));

        var onePixel = Gray();
        Paint(onePixel, 0, 2, (byte)200);
        check("毛玻璃：边缘带单个像素跳变不算变化", !GlassBackdrop.BackdropChanged(Gray(), onePixel, W, H));

        // ---------- 内部：岛体自己的读数在跳（像素少、幅度大） ----------
        var readouts = Gray();
        for (int x = 10; x < 15; x++) Paint(readouts, x, 10, (byte)255);
        check("毛玻璃：内部小范围像素大幅跳动（岛体自己的读数）不算变化",
            !GlassBackdrop.BackdropChanged(Gray(), readouts, W, H));

        // ---------- 内部：大面积变化（半透明时透出的桌面变了） ----------
        var sweep = Gray();
        for (int y = 4; y < 16; y++)
            for (int x = 6; x < 23; x++) Paint(sweep, x, y, (byte)140);
        check("毛玻璃：内部大面积变化算变化", GlassBackdrop.BackdropChanged(Gray(), sweep, W, H));

        // ---------- 两处都只是轻微变化：加起来也不算 ----------
        var bothSubtle = Gray();
        for (int y = 0; y < H; y++) Paint(bothSubtle, 0, y, (byte)108);
        for (int x = 4; x < 12; x++) Paint(bothSubtle, x, 10, (byte)109);
        check("毛玻璃：边缘带与内部都只有轻微变化时不算变化",
            !GlassBackdrop.BackdropChanged(Gray(), bothSubtle, W, H));

        // ---------- 换图判定：这一张截图要不要换上去 ----------
        // 实测截图里能截到岛体自己：底图 = 岛体"上一帧"的模糊像，而岛体又是"58% 主题色 + 42% 底图"画出来的。
        // 所以每换一张图，岛体颜色只向主题色靠一步（差值 ×0.42），要连续换上 8 张左右才稳定。
        // 旧实现每张都换、最终完全收敛；只靠变化检测会在第 3 步被"没变化"拦下，颜色停在中途 ⇒ 外观变了。
        // 因此刚换过图之后的若干张必须无条件换上去（收敛窗口），窗口结束后才交给变化检测。
        check("毛玻璃：收敛窗口内即使像素没变也要换图", GlassBackdrop.ShouldApply(forced: false, convergeLeft: 3, changed: false));
        check("毛玻璃：强制换图（本体移动/换主题）时无条件换图", GlassBackdrop.ShouldApply(forced: true, convergeLeft: 0, changed: false));
        check("毛玻璃：收敛窗口结束后，没变化就不换图", !GlassBackdrop.ShouldApply(forced: false, convergeLeft: 0, changed: false));
        check("毛玻璃：收敛窗口结束后，有变化才换图", GlassBackdrop.ShouldApply(forced: false, convergeLeft: 0, changed: true));
    }
}
