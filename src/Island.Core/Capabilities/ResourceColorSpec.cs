namespace Island.Core.Capabilities;

/// <summary>
/// **按资源配色的色值**（两端唯一真相源，2026-11-27 新增）。
///
/// 来源：用户提供的参考图（深色胶囊 + 按资源分色）。色值是**从参考图逐像素取样**得出的，
/// 不是凭感觉配的：CPU 蓝 `#0A84FF`、内存 青 `#5CE1E6`、GPU 粉 `#FF8FB1`、
/// 磁盘 薄荷绿 `#98DBB0`、电池 蓝紫 `#9CB9FF`、网速 ↓蓝 / ↑青。
///
/// 为什么放在这里：这套色是**跨端必须一致**的（两端收起态读数带要长得一样），
/// 两端各写一份就会漂 —— 这正是能力清单要治的病（同 §8 的 token 口径）。
///
/// 三条约定：
///   · 色值**独立于主题**：主题换的是底色与文字色，资源色不变（沿用 mac 既有做法）；
///   · 这里只放**色值**，不放"什么时候用哪个色"的规则 —— 那是各端渲染层的事；
///   · 浅色主题下要压对比度 —— 各端沿用自己既有的 Readable() 机制处理，本类不介入。
/// </summary>
public static class ResourceColorSpec
{
    /// <summary>CPU：蓝（#0A84FF）。</summary>
    public const string Cpu = "#0A84FF";

    /// <summary>内存：青（#5CE1E6）。</summary>
    public const string Memory = "#5CE1E6";

    /// <summary>GPU：粉（#FF8FB1）。</summary>
    public const string Gpu = "#FF8FB1";

    /// <summary>磁盘：薄荷绿（#98DBB0）。</summary>
    public const string Disk = "#98DBB0";

    /// <summary>电池：蓝紫（#9CB9FF）。</summary>
    public const string Battery = "#9CB9FF";

    /// <summary>网速·下行箭头：蓝（#0A84FF）。</summary>
    public const string NetDown = "#0A84FF";

    /// <summary>网速·上行箭头：青（#5CE1E6）。</summary>
    public const string NetUp = "#5CE1E6";
}
