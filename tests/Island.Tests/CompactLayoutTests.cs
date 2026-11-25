using System;
using Island.App.Controls;

namespace Island.Tests;

/// <summary>
/// 收起态**宿主布局**断言 —— 守住「胶囊不得超出宿主窗口宽」这条硬约束。
///
/// 背景（2026-11-25 验收轮登记的 Minor）：这条约束原先只存在于仓库外的离屏 harness
/// （D:\YQDESK\ui-preview-2026-11-25\harness\Program.cs），仓库内的 331 条断言不覆盖
/// 宿主尺寸表 —— 有人把上限改回 700/720（2026-11-25 修复轮 1 试过一次，被复审判为回归），
/// 仓库内测试拦不住，裁切会重新出现。
///
/// 现在尺寸表抽到了纯数据的 <see cref="CompactLayout"/>（Island.Tests 零依赖链接），
/// 这里逐项锁死：dash/combo 的上限、每个收起视图的上限都 ≤ 宿主窗口宽 640、高度 44。
/// </summary>
internal static class CompactLayoutTests
{
    public static void Run(Action<string, bool> check)
    {
        // ---- 宿主窗口宽（恒定）：ExpandW 560 + margin 40×2 = 640 ----
        check("宿主：展开面板宽 = 560",
            CompactLayout.ExpandW == 560);
        check("宿主：窗口边距 = 40",
            CompactLayout.WindowMargin == 40);
        check("宿主：窗口宽恒定 = 640 DIP（ExpandW 560 + margin 40×2）",
            CompactLayout.HostWindowWidth == 640
            && CompactLayout.HostWindowWidth == CompactLayout.ExpandW + CompactLayout.WindowMargin * 2);

        // ---- 尺寸表：dash / combo 的上限与高度 ----
        var dash = CompactLayout.Size("dash");
        check("尺寸表：dash 上限 = 620", dash.Width == 620);
        check("尺寸表：dash 高度 = 44（单行）", dash.Height == 44);
        var combo = CompactLayout.Size("combo");
        check("尺寸表：combo 上限 = 640", combo.Width == 640);
        check("尺寸表：combo 高度 = 44", combo.Height == 44);
        check("尺寸表：monitor 高度 = 44", CompactLayout.Size("monitor").Height == 44);
        check("尺寸表：clock 高度 = 44", CompactLayout.Size("clock").Height == 44);

        // ---- 硬约束：每个收起视图的设计上限都不得超出宿主窗口宽 ----
        // 宿主窗口宽度恒定、胶囊在窗口内居中画，超出 HWND 的部分由系统裁掉 ⇒ 超出即裁切。
        foreach (var view in CompactLayout.Views)
        {
            var size = CompactLayout.Size(view);
            check($"尺寸表：{view} 上限 {size.Width} ≤ 宿主窗口宽 {CompactLayout.HostWindowWidth}",
                size.Width <= CompactLayout.HostWindowWidth);
        }
        // 未知视图走默认档，也不能超宽
        var fallback = CompactLayout.Size("__unknown__");
        check($"尺寸表：未知视图默认档 {fallback.Width} ≤ 宿主窗口宽 {CompactLayout.HostWindowWidth}",
            fallback.Width <= CompactLayout.HostWindowWidth);

        // ---- 两个上限的具体裁决（改动前先看这里）----
        // dash 620：留 20px 安全余量。修复轮 1 的 700（实测胶囊 678.7，左右各超出 19.35 DIP）
        // 已被复审判为回归 ⇒ 门槛定死「上限 ≤ 宿主宽 - 20」。
        check("尺寸表：dash 上限 ≤ 宿主窗口宽 - 20（700/720 已被复审判为回归）",
            CompactLayout.HostWindowWidth - dash.Width >= 20);
        // combo 640：正好等于宿主窗口宽 —— 满宽但不被裁。
        check("尺寸表：combo 上限 = 宿主窗口宽（满宽不被裁）",
            combo.Width == CompactLayout.HostWindowWidth);
    }
}