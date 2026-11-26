namespace Island.Core.Capabilities;

/// <summary>
/// **鼠标穿透的判定规则**（两端唯一真相源，2026-11-27 新增）。
///
/// 起因（用户实测）：开着「鼠标穿透」时，岛体整体对鼠标透明（WS_EX_TRANSPARENT）——
/// 面板**看得见却点不动**，连面板里那个开关都点不回来（鸡生蛋），
/// 而托盘右键菜单里**没有**穿透开关 ⇒ 用户只能绕到设置中心才能退出这个状态。
///
/// 规则一句话：**穿透只对收起态生效；面板展开时必须可交互** ——
/// 用户显式展开面板就是要看/要点，这时候把点击拦下来没有意义，
/// 而且正是"看得见点不到"的来源。
/// </summary>
public static class ClickThroughRule
{
    /// <summary>
    /// 是否应当给窗口加 WS_EX_TRANSPARENT（鼠标穿透）。
    /// </summary>
    /// <param name="clickThroughEnabled">用户打开的「鼠标穿透」开关（游戏时不拦截点击）。</param>
    /// <param name="expanded">展开面板中（展开时必须可交互 ⇒ 不穿透）。</param>
    public static bool ShouldBeTransparent(bool clickThroughEnabled, bool expanded)
        => clickThroughEnabled && !expanded;
}
