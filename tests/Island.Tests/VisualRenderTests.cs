using System.IO;
using Island.Tools.VisualProbe;

namespace Island.Tests;

/// <summary>
/// 离屏渲染的视觉回归用例（2026-11-26 视觉回归探针）。
///
/// 解决的是**验证盲区**：本机屏幕捕获在锁屏会话里只能拍到全黑，
/// "画面是不是空的 / 布局对不对"没法用肉眼确认，于是这块回归长期没有门禁。
/// 这里把展开面板的监控页与设备页**离屏**渲染出来（不建窗口，元素 Measure/Arrange 后直接 Render），
/// 对渲染结果判定一组可自动断言的不变量：
///
///  1. 渲染结果**不是全黑**（非背景像素占比 > 2%）—— 这条就是此前 shot-expanded.png 全黑的防线；
///  2. 有实际内容（高亮文字/图标像素 > 0.05%），防"只画出卡片底、没有文字"；
///  3. 2×2 卡片栅格几何（卡宽 256 / 列距 12 / 卡头 42 / 上排 181 / 下排 156）；
///  4. 四张卡互不重叠、都在内容区内；
///  5. 卡片描边真的落在规格位置上（像素探针，不是只念常量）；
///  6. 文本不越出卡片（右对齐读数 x &lt;= 卡右 - 内缩 15）。
///
/// 判定逻辑与 tools/visual-probe 是**同一份源码**（VisualProbeCore.cs），
/// 免得"工具里过了、门禁里是另一套标准"这类漂移。
/// 渲染数据是合成的快照（不读真硬件），所以结果确定、可当回归基线。
/// </summary>
internal static class VisualRenderTests
{
    public static void Run(Action<string, bool> check)
    {
        ProbeReport report;
        try
        {
            // 不落盘：门禁只要判定，不要往仓库里写 PNG。
            report = VisualProbeCore.Run(new ProbeOptions { OutputDirectory = null });
        }
        catch (Exception ex)
        {
            // 渲染失败就是失败：不许用默认值凑一个"看起来正常"的结果。
            check("离屏渲染：不抛异常（渲染失败即门禁失败）", false);
            Console.WriteLine("      " + ex.Message);
            return;
        }

        foreach (var c in report.Checks)
        {
            check(c.Name, c.Passed);
            if (!c.Passed) Console.WriteLine("      " + c.Detail);
        }
    }
}
