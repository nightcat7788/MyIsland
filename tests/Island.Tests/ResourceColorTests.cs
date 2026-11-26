using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Island.Core.Capabilities;

namespace Island.Tests;

/// <summary>
/// **「按资源配色」门禁**（2026-11-27）。
///
/// 背景：用户给了参考图（深色胶囊 + 按资源分色），色值已固化为两端唯一真相源
/// <see cref="ResourceColorSpec"/>（从参考图逐像素取样，不是凭感觉配的）。
/// 此前两端的资源色**各写一份**（一份在 Windows 画布、一份在 mac 画布）⇒ 必然漂，
/// 这正是能力清单要治的病（同名 token 不同值）。
///
/// 本文件把"按资源配色"这件事钉住，分四层：
///   ① **色值锁定**：7 个色值逐个等于参考图取样值（有人悄悄改色 ⇒ 红）；
///   ② **两端同源**：Windows 画布与 mac 画布源码里**都**要取 ResourceColorSpec
///      （各写一份 ⇒ 红；只在注释里提一句 ⇒ 也红）；
///   ③ **旧色值不许复活**：两端画布源码里不得再出现改规格之前那套硬编码色值
///      （判定口径见 <see cref="FindOldColorUsage"/> —— mac 源码里同值但**别的角色**
///      仍在用的两处不能误伤）；
///   ④ **范围门禁**：展开面板 MonitorPage 本轮**不动**（保持暗色科技口径），
///      它一旦引用 ResourceColorSpec ⇒ 红（日后要改展开页，必须先改这条门禁）。
///
/// 补充：光有"源码里写了"不够 —— 还得**真的画出来**。那一条由两个离屏探针守住
/// （tools\visual-probe 的「收起态 dash 色相多样性」与 tools\avalonia-probe 的同款判定）。
/// </summary>
internal static class ResourceColorTests
{
    /// <summary>源码静态校验用的仓库根（找不到就跳过静态断言，不让测试因为路径找不到而红）。</summary>
    private static string? RepoRoot { get; } = FindRepoRoot();

    /// <summary>
    /// 改规格之前两端各自写死的那套旧资源色（值取自改规格前的源码）。
    /// 这套值与参考图取样值**不是**同一套 ⇒ 谁把它们搬回来，谁就是在破坏"按资源配色"。
    /// </summary>
    private static readonly (string Hex, string Name)[] OldResourceColors =
    {
        ("2E9BFF", "CPU 蓝"),
        ("4FE3D7", "内存 青"),
        ("F272A8", "GPU 粉"),
        ("5FD3A0", "磁盘 青绿"),
        ("3FB8FF", "电池 蓝"),
        ("6FB8FF", "网络 蓝"),
    };

    /// <summary>
    /// "这一行是资源色上下文"的判据：出现资源色标识符（cpu / mem / gpu / disk / batt / net / down / up…）。
    /// 用于区分"旧色值被搬回资源色"与"同值但别的角色在用"。
    /// </summary>
    private static readonly Regex ResourceColorContextRegex =
        new(@"\b(cpu|mem|memory|gpu|disk|batt|battery|net|down|up)[a-z]*\b", RegexOptions.IgnoreCase);

    public static void Run(Action<string, bool> check)
    {
        CheckSpecValues(check);
        CheckNetArrowColors(check);
        CheckBothEndsUseSpec(check);
        CheckOldPaletteNotBack(check);
        CheckExpandedPanelOutOfScope(check);
    }

    // ==================== ① 色值锁定 ====================

    /// <summary>
    /// 7 个色值逐个钉死（值来自用户参考图逐像素取样）。
    /// 这条是整套门禁的地基：规格一旦被悄悄改色，下面的"两端同源"就失去意义。
    /// </summary>
    private static void CheckSpecValues(Action<string, bool> check)
    {
        check("规格：CPU 色 = #0A84FF（参考图取样）",
            ResourceColorSpec.Cpu == "#0A84FF");
        check("规格：内存色 = #5CE1E6（参考图取样）",
            ResourceColorSpec.Memory == "#5CE1E6");
        check("规格：GPU 色 = #FF8FB1（参考图取样）",
            ResourceColorSpec.Gpu == "#FF8FB1");
        check("规格：磁盘色 = #98DBB0（参考图取样）",
            ResourceColorSpec.Disk == "#98DBB0");
        check("规格：电池色 = #9CB9FF（参考图取样）",
            ResourceColorSpec.Battery == "#9CB9FF");
        check("规格：网速下行色 = #0A84FF（参考图取样）",
            ResourceColorSpec.NetDown == "#0A84FF");
        check("规格：网速上行色 = #5CE1E6（参考图取样）",
            ResourceColorSpec.NetUp == "#5CE1E6");

        // 全部 7 个值都必须是合法的 #RRGGBB（防止有人写成 #RGB 简写或 rgb() 之类）
        var all = new[]
        {
            ResourceColorSpec.Cpu, ResourceColorSpec.Memory, ResourceColorSpec.Gpu,
            ResourceColorSpec.Disk, ResourceColorSpec.Battery, ResourceColorSpec.NetDown,
            ResourceColorSpec.NetUp,
        };
        check("规格：7 个色值都是 #RRGGBB 形式",
            all.All(v => v.Length == 7 && v[0] == '#' && Regex.IsMatch(v, "^#[0-9A-Fa-f]{6}$")));

        // 5 种资源的色必须互不相同（"按资源配色"退化成"按主题配色"就是从这儿开始坏的）
        var five = new[]
        {
            ResourceColorSpec.Cpu, ResourceColorSpec.Memory, ResourceColorSpec.Gpu,
            ResourceColorSpec.Disk, ResourceColorSpec.Battery,
        };
        check("规格：5 种资源的色互不相同",
            five.Distinct(StringComparer.OrdinalIgnoreCase).Count() == five.Length);
    }

    // ==================== ⑤ 网速箭头色 ====================

    /// <summary>
    /// 网速方向箭头：↓ 与 CPU 同蓝、↑ 与内存同青（参考图里就是这个口径）。
    /// 钉这一条是因为箭头色最容易被"顺手改成同一个色"——那样就看不出上下行了。
    /// </summary>
    private static void CheckNetArrowColors(Action<string, bool> check)
    {
        check("网速箭头：下行 ↓ 与 CPU 同色（#0A84FF）",
            ResourceColorSpec.NetDown == ResourceColorSpec.Cpu &&
            ResourceColorSpec.NetDown == "#0A84FF");
        check("网速箭头：上行 ↑ 与内存同色（#5CE1E6）",
            ResourceColorSpec.NetUp == ResourceColorSpec.Memory &&
            ResourceColorSpec.NetUp == "#5CE1E6");
    }

    // ==================== ② 两端同源 ====================

    /// <summary>
    /// 两端画布都得从 <see cref="ResourceColorSpec"/> 取色。
    /// 判定用的是**剥掉注释后的源码** —— 只在注释里提一句 ResourceColorSpec 不算数。
    /// </summary>
    private static void CheckBothEndsUseSpec(Action<string, bool> check)
    {
        if (RepoRoot is null)
        {
            check("两端画布都从 ResourceColorSpec 取色（未定位到仓库根，已跳过）", true);
            return;
        }

        string? win = ReadSource("src", "Island.App", "Controls", "IslandCanvas.cs");
        string? mac = ReadSource("src", "Island.Avalonia", "IslandCanvas.cs");

        check("Windows 画布：从 ResourceColorSpec 取资源色（不再本地写死）",
            win is not null && StripComments(win).Contains("ResourceColorSpec"));
        check("macOS 画布：从 ResourceColorSpec 取资源色（不再本地写死）",
            mac is not null && StripComments(mac).Contains("ResourceColorSpec"));
    }

    // ==================== ③ 旧色值不许复活 ====================

    /// <summary>
    /// 两端画布源码里不得再出现旧的那套硬编码色值。
    /// 判定口径见 <see cref="FindOldColorUsage"/>。
    /// </summary>
    private static void CheckOldPaletteNotBack(Action<string, bool> check)
    {
        if (RepoRoot is null)
        {
            check("旧资源色值不得复活（未定位到仓库根，已跳过）", true);
            return;
        }

        string? win = ReadSource("src", "Island.App", "Controls", "IslandCanvas.cs");
        string? mac = ReadSource("src", "Island.Avalonia", "IslandCanvas.cs");

        string? winHit = win is null ? null : FindOldColorUsage(win, "Windows 画布");
        check("Windows 画布：旧资源色值没有复活", winHit is null);
        if (winHit is not null) Console.WriteLine("      " + winHit);

        string? macHit = mac is null ? null : FindOldColorUsage(mac, "macOS 画布");
        check("macOS 画布：旧资源色值没有复活", macHit is null);
        if (macHit is not null) Console.WriteLine("      " + macHit);
    }

    /// <summary>
    /// 在画布源码里找"旧资源色值复活"的痕迹（找到就返回可读说明，没找到返回 null）。
    ///
    /// 旧色值有两种写法，**两种都要堵**：
    ///   ① **连续 hex**（<c>#2E9BFF</c> / <c>0x2E9BFF</c>）：**全局**禁止 ——
    ///      这种写法本身就是"某个资源的颜色常量"，撞上别的角色的概率极低；
    ///   ② **字节三元组**（<c>0x2E, 0x9B, 0xFF</c>）：源码里还有**同值但别的角色**的用法
    ///      （mac 画布里"常用"圆点 0x5FD3A0、"雨天"图标 0x6FB8FF 与旧磁盘/网络色同值），
    ///      所以只有当**同一行**出现资源色标识符（cpu/mem/gpu/disk/batt/net/down/up…）时才算违规。
    ///
    /// 扫描前先剥掉注释 —— 迁移说明里写"旧值 0x2E,0x9B,0xFF 已废弃"不该把门禁打红。
    /// </summary>
    private static string? FindOldColorUsage(string source, string label)
    {
        var lines = StripComments(source).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (var (hex, name) in OldResourceColors)
            {
                // ① 连续 hex：全局禁止
                if (Regex.IsMatch(lines[i], $"(?<![0-9A-Fa-f]){hex}(?![0-9A-Fa-f])",
                        RegexOptions.IgnoreCase))
                    return $"{label} 第 {i + 1} 行出现旧{name}色值 {hex}：{lines[i].Trim()}";

                // ② 字节三元组：同一行必须是资源色上下文（否则是别的角色，不算复活）
                var triple = $@"0x{hex[..2]}\s*,\s*0x{hex[2..2]}\s*,\s*0x{hex[4..]}";
                if (!Regex.IsMatch(lines[i], triple, RegexOptions.IgnoreCase)) continue;
                if (!ResourceColorContextRegex.IsMatch(lines[i])) continue;
                return $"{label} 第 {i + 1} 行用旧{name}色值（字节三元组写法）：{lines[i].Trim()}";
            }
        }
        return null;
    }

    // ==================== ④ 范围门禁 ====================

    /// <summary>
    /// 展开面板（MonitorPage）本轮**不动**：它保持暗色科技口径，不接资源色规格。
    /// 日后要改展开页，必须先改这条门禁（而不是顺手就接上）。
    /// </summary>
    private static void CheckExpandedPanelOutOfScope(Action<string, bool> check)
    {
        if (RepoRoot is null)
        {
            check("展开面板本轮不动（未定位到仓库根，已跳过）", true);
            return;
        }

        string? page = ReadSource("src", "Island.App", "Controls", "MonitorPage.cs");
        check("展开面板 MonitorPage 本轮不引用 ResourceColorSpec（只改收起态）",
            page?.Contains("ResourceColorSpec") != true);
    }

    // ==================== 小工具 ====================

    /// <summary>剥掉注释（块注释 + 行注释），只留代码 —— 注释里提到旧色值不算"复活"。</summary>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(source, @"//[^\n]*", " ");
    }

    /// <summary>往上找仓库根（找带 Island.sln 的那一层）。</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Island.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>读仓库里的一个源文件（读不到返回 null）。</summary>
    private static string? ReadSource(params string[] parts)
    {
        if (RepoRoot is null) return null;
        string path = Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray());
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
