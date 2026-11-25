namespace Island.App.Controls;

/// <summary>
/// 收起态**宿主布局**规格（纯数据，无 WPF 依赖）—— 各视图尺寸表与宿主窗口宽的唯一来源。
///
/// 为什么要从 IslandWindow 里抽出来（2026-11-25 验收轮登记的 Minor）：
/// 尺寸表原来长在 IslandWindow.xaml.cs（一个 WPF Window）里，而仓库内的 Island.Tests 链不到它
/// —— IslandWindow 依赖 XAML 生成代码与 Island.Native 的 NuGet 包，测试工程刻意保持零 NuGet 依赖。
/// 结果是「胶囊不得超出宿主窗口宽」这条硬约束只活在仓库外的离屏 harness 里，
/// 仓库内没有任何断言拦着有人把上限改回 700/720（2026-11-25 修复轮 1 试过一次，被复审判为回归），
/// 裁切会重新出现。抽到这里后 Island.Tests 零依赖链接即可断言（tests/Island.Tests/CompactLayoutTests.cs）。
///
/// 硬约束：**每个收起视图的设计上限 ≤ 宿主窗口宽（HostWindowWidth = 640）**。
/// 宿主窗口宽度**恒定**（2026-11-24 防抖决策：窗口宽度跟着内容变会抖，所以只调高度），
/// 胶囊在窗口内居中画、超出 HWND 的部分由系统裁掉 ⇒ 超出即裁切。
/// </summary>
public static class CompactLayout
{
    /// <summary>展开面板宽度（DIP）：宿主窗口宽 = ExpandW + WindowMargin × 2。</summary>
    public const double ExpandW = 560;

    /// <summary>宿主窗口四周留白（DIP）：窗口比本体大出来的这一圈。</summary>
    public const double WindowMargin = 40;

    /// <summary>宿主窗口宽度（DIP，恒定）= 560 + 40 × 2 = 640。</summary>
    public const double HostWindowWidth = ExpandW + WindowMargin * 2;

    /// <summary>
    /// 全部收起视图（尺寸表覆盖的视图；宠物视图走默认档 —— 它按内容定宽）。
    /// </summary>
    public static readonly string[] Views =
        { "clock", "dash", "pet", "combo", "monitor", "devices", "clipboard", "window" };

    /// <summary>
    /// 收起态各视图的（设计上限宽, 高）。
    /// 宽度是**上限**：实际胶囊宽按内容实测夹到 [110, 上限]（见 IslandWindow.CurrentCollapsedSize）。
    /// </summary>
    public static (double Width, double Height) Size(string view) => view switch
    {
        "clock" => (230, 44),   // 时间 + 天气 + 网速（参考 BrimDeck compact-clock 的信息密度）
        // 信息胶囊：时间 + 占用环组(C/M/G) + 温度/硬盘环组(C/G/D) + 网速。
        // 上限 640：内容宽按实测定（宿主 CurrentCollapsedSize 用 clamp(实测内容宽, 110, 上限)），
        // 上限只决定「最坏情况还装不装得下」——两个读数同时到 100%（4 字符，比最小槽 21 宽约 7px）
        // 时 560 会把网速整段挤进「+N」，640 才有余量。mac 的 CapsuleW 同样是按内容定宽。
        "combo" => (640, 44),
        // 2026-11-24：读数带的等宽数字 + 四个表盘 + 网速实测约 470 宽，原来上限 460 会把末尾的 "K" 裁掉
        "monitor" => (500, 44), // 单排高密度读数带
        // 全读数单行（核心柱│温度组│占用组│网速，组间分隔线；对齐 mac）。
        // 上限 620：宿主窗口宽度**恒定 640 DIP**（ExpandW 560 + margin 40×2，2026-11-24 定下的防抖决策），
        // 胶囊在窗口内居中画、超出 HWND 的部分由系统裁掉 ⇒ 硬约束是「胶囊 ≤ 640」。
        // 620 < 640 ⇒ 任何读数组合下都不会被裁（装不下的内容折进 "+N"）；4 组实测 580.6（3 核）/ 613.6（8 核），
        // 在 620 内 ⇒ 4 组全显示、网速可见，并留 20px 安全余量。
        // 电池读数不在 dash 里（设备视图已有一份），dash 不重复。
        "dash" => (620, 44),
        "window" => (360, 44),  // 活动窗口（YASB ActiveWindow 同款：进程名 + 标题）
        "devices" => (300, 64),
        "clipboard" => (240, 44),
        _ => (110, 44),         // 宠物视图与未知视图：按内容定宽，下限 110
    };
}
