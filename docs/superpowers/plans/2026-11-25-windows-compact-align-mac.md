# Windows 收起态向 mac 对齐 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 Windows 版收起态胶囊改成 mac 版现有的样子：combo 重排为「时间│占用环组│温度磁盘环组│网速」四组并加组间分隔线、环改为 360° 闭环、字号/槽宽/间距对齐 mac 规格，dash 由两行网格改为单行。

**Architecture:** 新建纯计算的 `CompactSpec` 静态类承载全部排版常量与几何/宽度计算（不依赖 WPF，可被零依赖测试工程直接链接断言）；`RenderKit` 新增 360° 闭环 `Ring`（`Dial` 保留给展开面板）；`IslandCanvas` 的收起态排版器 `DrawCompactGroups` 升级为「视觉组 + 可选分隔线」；`DrawCombo`/`DrawDash` 按 mac 的分组与规格重写。

**Tech Stack:** C# / .NET 8 (net8.0-windows10.0.19041.0) / WPF（`System.Windows.Media` 直接绘制）/ 自研零依赖测试 harness（`Island.Tests`，`Check` 失败即抛、退出码非 0）/ `dotnet` 8.0.428 位于 `D:\YQDESK\.dotnet\dotnet.exe`

## Global Constraints

- **基准**：一切数值以 mac 版 `src/Island.Avalonia/IslandCanvas.cs` 现有实现为准；**mac 侧任何文件零改动**。
- **规格常量（不可改动，逐一对应 mac 版源行）**：`PadX=10`、`DashPadX=14`、`FigureFont=13`、`Dial=20`、`DialGap=5`、`GaugeGap=10`、`ClockGap=12`、`GroupGap=12`、`SeparatorBand=24`、`ValueMinW=21`、`NetGap=7`、`DashNetGap=10`、`ArrowValueGap=3`、`DividerHalfHeight=9`、`RingDeadZone=0.004`。
- **分隔线**：`pal.Hairline`，1px，透明度 0.8，上下各 9px；**组尾 + 18px 处画线，下一组从 +24px 开始**（组间总带 24px = 2 × GroupGap，线不在总带正中，而是偏后 6px）。
- **环**：收起态用新增的 360° 闭环 `RenderKit.Ring`；`RenderKit.Dial`（270° 开口环）**一行不改**，展开面板继续用它。
- **配色口径不改**：沿用 Windows 现有的「暗色科技」规则（强调色只给 CPU，其余表盘中性色，≥70%/≥90% 才换状态色；温度用 `TempColor`）。mac 的「每资源一色」是另一套配色哲学，**不在本阶段范围**（总纲 §1 只要求视觉规格一致，配色口径以两端各自的既有规则为准）。
- **字体族不改**：读数继续走等宽 `RenderKit.Num`（Windows 设计系统规则，有 `DesignSystemTests` 守护）。
- **构建门禁**：`dotnet build Island.sln -c Release` 必须 **0 警告 0 错误**（`dev.ps1` 把警告当错误）。
- **测试门禁**：`Island.Tests` 必须全过，且退出码为 0。
- **命令统一用**：`D:\YQDESK\.dotnet\dotnet.exe`（系统 dotnet 无 SDK）。
- **编码**：`.cs` / `.csproj` 用 UTF-8；`.ps1` 必须 UTF-8 **with BOM**。
- **不得顺手改**：展开面板、mac 侧代码、Windows 胶囊外形（灵动岛圆角块）、`+N` 宽度截断机制、小形象款式（属阶段 3）。

---

### Task 1: 抽出收起态排版规格（纯计算，可测）

**Files:**
- Create: `src/Island.App/Controls/CompactSpec.cs`
- Create: `tests/Island.Tests/CompactSpecTests.cs`
- Modify: `tests/Island.Tests/Island.Tests.csproj`（把 `CompactSpec.cs` 链接进测试工程）
- Modify: `tests/Island.Tests/Program.cs`（注册新测试）

**Interfaces:**
- Consumes: 无（全新文件）
- Produces: `public static class Island.App.Controls.CompactSpec`
  - 常量（全 `double`）：`PadX / DashPadX / FigureFont / Dial / DialGap / GaugeGap / ClockGap / GroupGap / SeparatorBand / ValueMinW / NetGap / DashNetGap / ArrowValueGap / DividerHalfHeight / RingDeadZone`
  - `static double GaugeGroupWidth(int count, Func<int, double> figureWidth)`
  - `static double ComboContentWidth(double clockW, double gaugesW, double systemGaugesW, double netDownW, double netUpW)`
  - `static double ComboCapsuleWidth(double clockW, double gaugesW, double systemGaugesW, double netDownW, double netUpW)`
  - `static double DividerXAfter(double groupEndX, double gap = GroupGap)`
  - `static double InlineItemWidth(double labelWidth, double valueWidth)`
  - `static double RingRadius(double diameter, double thickness)`
  - `static double RingSweep(double share)`

- [ ] **Step 1: 写失败的测试**

新建 `tests/Island.Tests/CompactSpecTests.cs`：

```csharp
using System;
using Island.App.Controls;

namespace Island.Tests;

/// <summary>
/// 收起态排版规格断言 —— 这些数字是「Windows 收起态与 mac 一致」的唯一契约。
/// 源：src/Island.Avalonia/IslandCanvas.cs L554-L561（常量）、L595-L608（宽度公式）、
///     L671-L673（分隔线）、L1843 / L1863-L1866（dash 规格）、
///     src/Island.Avalonia/RenderKit.cs L185-L215（Ring 几何）。
/// 改任何一个都要同步改 mac 侧，否则两端会漂移。
/// </summary>
internal static class CompactSpecTests
{
    public static void Run(Action<string, bool> check)
    {
        // ---- 常量逐项锁死 ----
        check("规格：combo 胶囊左右内边距 = 10", CompactSpec.PadX == 10);
        check("规格：dash 胶囊左右内边距 = 14", CompactSpec.DashPadX == 14);
        check("规格：数值字号 = 13", CompactSpec.FigureFont == 13);
        check("规格：环直径 = 20", CompactSpec.Dial == 20);
        check("规格：环→数值间距 = 5", CompactSpec.DialGap == 5);
        check("规格：同组内相邻环间距 = 10", CompactSpec.GaugeGap == 10);
        check("规格：时间→第一组间距 = 12", CompactSpec.ClockGap == 12);
        check("规格：组间单侧间距 = 12", CompactSpec.GroupGap == 12);
        check("规格：组间总带 = 24（mac Gap() 推进两次 GroupGap）",
            CompactSpec.SeparatorBand == 24 && CompactSpec.SeparatorBand == CompactSpec.GroupGap * 2);
        check("规格：数值最小槽宽 = 21", CompactSpec.ValueMinW == 21);
        check("规格：combo 里 ↓值与↑之间 = 7", CompactSpec.NetGap == 7);
        check("规格：dash 里 ↓值与↑之间 = 10", CompactSpec.DashNetGap == 10);
        check("规格：方向箭头与数值间距 = 3", CompactSpec.ArrowValueGap == 3);
        check("规格：分隔线上下各 9px", CompactSpec.DividerHalfHeight == 9);
        check("规格：闭环弧占比死区 = 0.004", CompactSpec.RingDeadZone == 0.004);

        // ---- 一组环的宽度 ----
        // 3 个环，每个数值宽 30（>最小槽 21）：3*(20+5+30) + 2*10(组内间距) = 165+20 = 185
        check("环组宽度：3 个环各 30 宽 = 185", CompactSpec.GaugeGroupWidth(3, _ => 30) == 185);
        // 数值窄于最小槽时按 21 算：3*(20+5+21) + 2*10 = 138+20 = 158
        check("环组宽度：窄数值按最小槽 21 兜底 = 158", CompactSpec.GaugeGroupWidth(3, _ => 10) == 158);
        check("环组宽度：空组 = 0", CompactSpec.GaugeGroupWidth(0, _ => 30) == 0);

        // ---- combo 内容宽（对应 mac ComboLayout.ContentW）----
        double expectContent = 40 + 12 + 185 + (12 * 2 + 158) + 12 * 2 + 60 + 7 + 60;
        check("combo 内容宽与 mac 公式一致",
            CompactSpec.ComboContentWidth(40, 185, 158, 60, 60) == expectContent);
        check("combo 胶囊宽 = 内容宽 + 2*PadX",
            CompactSpec.ComboCapsuleWidth(40, 185, 158, 60, 60) == expectContent + 20);
        double expectNoSystem = 40 + 12 + 185 + 12 * 2 + 60 + 7 + 60;
        check("combo 无系统组时不留该段空位",
            CompactSpec.ComboContentWidth(40, 185, 0, 60, 60) == expectNoSystem);

        // ---- 分隔线位置：mac 的 Gap() 先推进一次组间距再画线 ----
        // 组尾 100 → 推进后 112 → 线在 112 + 6 = 118 → 下一组从 124 开始
        check("分隔线 x = 组尾 + 18（mac：Gap() 推进两次 GroupGap，线在 1.5×处）",
            CompactSpec.DividerXAfter(100) == 118);
        check("分隔线 x 可用自定义组间距（gap=16 → 组尾 + 24）",
            CompactSpec.DividerXAfter(100, 16) == 124);
        check("分隔线落在组间总带内（组尾 + 18 < 组尾 + 24）",
            CompactSpec.DividerXAfter(100) - 100 < CompactSpec.SeparatorBand);

        // ---- dash 单行项宽（Windows 侧组合，mac dash 没有对应公式）----
        check("dash 单行项宽 = 标签 + 箭头数值间距 3 + 数值槽", CompactSpec.InlineItemWidth(28, 30) == 61);
        check("dash 单行项宽窄值按最小槽 21 兜底", CompactSpec.InlineItemWidth(28, 10) == 52);

        // ---- 360° 闭环几何 ----
        check("环半径 = 直径/2 - 线宽/2", CompactSpec.RingRadius(20, 2.8) == 8.6);
        check("环半径不设下限（mac 公式：线宽=直径时半径为 0）", CompactSpec.RingRadius(4, 4) == 0);
        check("环扫角 = 占比 * 2π", Math.Abs(CompactSpec.RingSweep(0.25) - Math.PI / 2) < 1e-9);
        check("环占比夹到 0..1", CompactSpec.RingSweep(2) == 2 * Math.PI);
        check("环 NaN 占比按 0 处理", CompactSpec.RingSweep(double.NaN) == 0);
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

```powershell
& 'D:\YQDESK\.dotnet\dotnet.exe' build 'D:\YQDESK\MyIsland-src\tests\Island.Tests\Island.Tests.csproj' -c Release --nologo
```
预期：编译失败，报 `CS0103: 当前上下文中不存在名称"CompactSpec"`（类型未实现；草稿预测的是 CS0246，实际编译器给 CS0103，两者等价）。

- [ ] **Step 3: 实现 CompactSpec**

新建 `src/Island.App/Controls/CompactSpec.cs`：

```csharp
using System;

namespace Island.App.Controls;

/// <summary>
/// 收起态排版规格 —— 唯一来源。
///
/// 数值全部照抄 mac 版（行号以 src/Island.Avalonia/ 为准）：
///   combo 常量 src/Island.Avalonia/IslandCanvas.cs L554-L561
///   combo 宽度 src/Island.Avalonia/IslandCanvas.cs L595-L608
///   分隔线   src/Island.Avalonia/IslandCanvas.cs L671-L673（dash 同构副本 L1948-L1950）
///   dash 规格 src/Island.Avalonia/IslandCanvas.cs L1843 / L1863-L1866
///   环几何   src/Island.Avalonia/RenderKit.cs L185-L215
/// 目的：Windows 与 mac 两端收起态视觉一致。改这里必须同步改 mac 侧。
///
/// 本文件刻意只放「纯计算」（不碰 WPF 图形对象），Island.Tests 零依赖链接后可直接断言。
/// </summary>
public static class CompactSpec
{
    // ---------- 与 mac 版一一对应的常量 ----------
    /// <summary>combo 胶囊左右内边距（mac ComboPadX，IslandCanvas.cs L554）。</summary>
    public const double PadX = 10;
    /// <summary>dash 胶囊左右内边距（mac DashPadX，IslandCanvas.cs L1843）—— 与 combo 的 PadX 不同，别混用。</summary>
    public const double DashPadX = 14;
    /// <summary>数值字号（mac ComboFigureFont，IslandCanvas.cs L555）。</summary>
    public const double FigureFont = 13;
    /// <summary>环直径（mac ComboDial，IslandCanvas.cs L555）。</summary>
    public const double Dial = 20;
    /// <summary>环 → 数值间距（mac ComboDialGap，IslandCanvas.cs L555）。</summary>
    public const double DialGap = 5;
    /// <summary>同组内相邻环间距（mac ComboGaugeGap，IslandCanvas.cs L555）。</summary>
    public const double GaugeGap = 10;
    /// <summary>时间 → 第一组间距（mac ComboClockGap，IslandCanvas.cs L555）。</summary>
    public const double ClockGap = 12;
    /// <summary>组与组之间的**单侧**间距（mac CompactGroupGap，IslandCanvas.cs L561）。组间总带见 SeparatorBand。</summary>
    public const double GroupGap = 12;
    /// <summary>
    /// 组与组之间的**总**间距 = 2 × GroupGap = 24。
    /// mac 的 Gap() 连续推进两次 CompactGroupGap（IslandCanvas.cs L673 / L1950），
    /// 所以「组尾 → 下一组起点」整整一条带是 24px，分隔线画在这条带里。
    /// </summary>
    public const double SeparatorBand = GroupGap * 2;
    /// <summary>数值最小槽宽（mac CompactValueMinW，IslandCanvas.cs L559）—— 窄数值也占这么宽，避免抖动。</summary>
    public const double ValueMinW = 21;
    /// <summary>combo 里 ↓值 与 ↑ 之间（mac ComboLayout.NetGap，IslandCanvas.cs L595）。</summary>
    public const double NetGap = 7;
    /// <summary>dash 里 ↓值 与 ↑ 之间（mac DashLayout.NetGap，IslandCanvas.cs L1866）—— 与 combo 的 NetGap 不同。</summary>
    public const double DashNetGap = 10;
    /// <summary>方向箭头与数值之间的间距（mac ComboLayout.NetDownW / NetUpW 里的 +3，IslandCanvas.cs L596-L597）。</summary>
    public const double ArrowValueGap = 3;
    /// <summary>分隔线上下各多少（mac Divider 里的 cy ± 9，IslandCanvas.cs L672 / L1949）。</summary>
    public const double DividerHalfHeight = 9;
    /// <summary>360° 闭环的弧占比死区（mac RenderKit.Ring 的 f 大于 0.004 才画弧，RenderKit.cs L191）。</summary>
    public const double RingDeadZone = 0.004;

    /// <summary>
    /// 一组环的总宽：每环 = 环径 + 环→数值间距 + max(数值宽, 最小槽宽)，
    /// 组内相邻环之间再插一个 GaugeGap，末环后面不插。
    /// 对应 mac ComboLayout.GaugeWidth（IslandCanvas.cs L600-L601）。
    /// </summary>
    public static double GaugeGroupWidth(int count, Func<int, double> figureWidth)
    {
        if (count <= 0) return 0;
        double w = 0;
        for (int i = 0; i < count; i++)
            w += Dial + DialGap + Math.Max(figureWidth(i), ValueMinW) + GaugeGap;
        return w - GaugeGap;
    }

    /// <summary>combo 内容宽（不含胶囊左右内边距）。对应 mac ComboLayout.ContentW（IslandCanvas.cs L605-L607）。</summary>
    public static double ComboContentWidth(double clockW, double gaugesW, double systemGaugesW,
                                           double netDownW, double netUpW)
        => clockW + ClockGap + gaugesW
         + (systemGaugesW > 0 ? SeparatorBand + systemGaugesW : 0)
         + SeparatorBand + netDownW + NetGap + netUpW;

    /// <summary>combo 胶囊总宽。对应 mac ComboLayout.CapsuleW（IslandCanvas.cs L608）。</summary>
    public static double ComboCapsuleWidth(double clockW, double gaugesW, double systemGaugesW,
                                           double netDownW, double netUpW)
        => ComboContentWidth(clockW, gaugesW, systemGaugesW, netDownW, netUpW) + PadX * 2;

    /// <summary>
    /// 分隔线的 x：**组尾 + 1.5 × 单侧组间距**（默认 +18px）。
    ///
    /// mac 的 Gap() 是「先把 x 推进一个 CompactGroupGap，再在当时的 x 上画线」，
    /// 而线画在 x + CompactGroupGap / 2 处（IslandCanvas.cs L671-L673 / L1948-L1950）：
    /// 组尾 100 → 推进后 112 → 线在 118 → 下一组从 124 开始。
    /// 即线落在组间总带（SeparatorBand = 24）里偏后 6px，**不在总带正中**。
    /// </summary>
    public static double DividerXAfter(double groupEndX, double gap = GroupGap) => groupEndX + gap * 1.5;

    /// <summary>
    /// dash 单行项宽：标签 + 箭头/数值间距 + 数值槽（窄数值按最小槽兜底）。
    ///
    /// 注意：这是 **Windows 侧**的组合，mac dash 里没有对应公式，别去 mac 找「同步对象」：
    /// mac dash 是「图标槽 + 图标间距 + max(数值宽, CompactValueMinW) + 温度间距」
    /// （IslandCanvas.cs L1863），既没有「标签 + 数值」这种行内排布，
    /// 它的占用值最小槽也是 34（L1864-L1865）而不是本文件的 ValueMinW = 21。
    /// 中间那个间距取自 mac 的箭头 → 数值间距（ArrowValueGap，IslandCanvas.cs L596-L597）。
    /// </summary>
    public static double InlineItemWidth(double labelWidth, double valueWidth)
        => labelWidth + ArrowValueGap + Math.Max(valueWidth, ValueMinW);

    /// <summary>
    /// 360° 闭环半径（mac RenderKit.Ring：radius = diameter / 2 - thickness / 2，RenderKit.cs L187）。
    /// 与 mac 一样**不设下限**：线宽等于直径时半径就是 0。
    /// </summary>
    public static double RingRadius(double diameter, double thickness)
        => diameter / 2 - thickness / 2;

    /// <summary>
    /// 360° 闭环的扫角（占比 * 2π，从 12 点顺时针）。
    /// NaN 按 0、越界夹到 0..1（与 mac RenderKit.Ring 里 f 的归一一致，RenderKit.cs L190）。
    /// 扫角超过 180° 要拆成两段弧画，那是**渲染层**的职责（Task 2 的 RenderKit.Ring），不在本类里。
    /// </summary>
    public static double RingSweep(double share)
    {
        double f = double.IsNaN(share) ? 0 : Math.Clamp(share, 0, 1);
        return f * 2 * Math.PI;
    }
}
```

在 `tests/Island.Tests/Island.Tests.csproj` 的 `<ItemGroup>` 里（放在 `Theme.cs` 那条之后）加：

```xml
    <!-- 收起态排版规格（纯计算）：Windows 与 mac 两端一致性的唯一契约 -->
    <Compile Include="..\..\src\Island.App\Controls\CompactSpec.cs" Link="Merged\CompactSpec.cs" />
```

在 `tests/Island.Tests/Program.cs` 的 `try { ... }` 块里加：

```csharp
    CompactSpecTests.Run(Check);   // 2026-11-25 收起态对齐：规格常量与宽度公式锁死
```

- [ ] **Step 4: 运行测试，确认通过**

```powershell
& 'D:\YQDESK\.dotnet\dotnet.exe' run --project 'D:\YQDESK\MyIsland-src\tests\Island.Tests\Island.Tests.csproj' -c Release --nologo
```
预期：末尾输出 `全部通过：307 条`（276 基线 + 本任务新增 31 条），退出码 0。

- [ ] **Step 5: 提交**

```powershell
git -C 'D:\YQDESK\MyIsland-src' add src/Island.App/Controls/CompactSpec.cs tests/Island.Tests/CompactSpecTests.cs tests/Island.Tests/Island.Tests.csproj tests/Island.Tests/Program.cs
git -C 'D:\YQDESK\MyIsland-src' commit -m "feat: 抽出收起态排版规格 CompactSpec（数值照抄 mac 侧，纯计算可测）"
```

> **修复轮 1/5（2026-11-25，评审后）**：分隔线几何按 mac 的实际语义改正
> （`DividerXAfter` = 组尾 + 1.5 × 组间距；新增 `SeparatorBand = 24`）、`RingRadius` 去掉 0.5 下限、
> `+3` 提成 `ArrowValueGap`、补齐 `DashPadX / DashNetGap / RingDeadZone`。
> 本任务断言 31 条 → 全套 307 条（原草稿估的 25 条 / 301 条是按旧口径算的，已作废）。
> 修复提交：`fix: 分隔线几何对齐 mac 实际语义 + 补齐 mac 规格常量（SeparatorBand/DashPadX/DashNetGap/RingDeadZone）`。

---

### Task 2: 新增 360° 闭环 RenderKit.Ring

**Files:**
- Modify: `src/Island.App/Controls/RenderKit.cs`（在 `Dial` 方法之后插入）

**Interfaces:**
- Consumes: `CompactSpec.RingRadius(double, double)`、`CompactSpec.RingSweep(double)`、`CompactSpec.RingDeadZone`（Task 1）
- Produces: `public static void RenderKit.Ring(DrawingContext dc, Point center, double diameter, double thickness, double share, Color track, Color arc)`

- [ ] **Step 1: 写实现**

在 `src/Island.App/Controls/RenderKit.cs` 的 `Dial` 方法之后插入：

```csharp
    // ---------- 360° 完整圆环（mac RenderKit.Ring 同款）----------
    /// <summary>
    /// 360° 完整圆环：底环 + 按占比的彩色弧（从 12 点顺时针）。
    /// 与 <see cref="Dial"/>（270° 开口环 + 环内字母）不同：这里是闭环，数字放环外。
    /// 收起态用这个；展开面板的 CPU 主读数环仍是 <see cref="Dial"/>，两者并存（mac 侧同样并存）。
    /// 源：src/Island.Avalonia/RenderKit.cs L184-L215。
    /// </summary>
    public static void Ring(DrawingContext dc, Point center, double diameter, double thickness,
        double share, Color track, Color arc)
    {
        double radius = CompactSpec.RingRadius(diameter, thickness);
        // CompactSpec.RingRadius 按 mac 取消了 0.5 下限（RenderKit.Ring:187），
        // 于是 thickness > diameter 时半径为负 —— WPF 的 DrawEllipse 会因负半径抛异常，
        // 而 mac 的 Avalonia 路径不会。参数不合法的调用直接不画，交给调用方保证 thickness <= diameter。
        if (radius <= 0) return;
        // 底环：整圈
        dc.DrawEllipse(null, PenOf(track, thickness, 0.9), center, radius, radius);
        double sweep = CompactSpec.RingSweep(share);
        // 占比死区与 mac 一致（RenderKit.cs L191）：f <= 0.004 不画弧
        if (sweep <= CompactSpec.RingDeadZone * 2 * Math.PI) return;
        // 弧：从 12 点（-90°）顺时针；超过半圈要拆两段（一次 ArcTo 画不出 180° 以上）
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            Point At(double a) => new(center.X + radius * Math.Sin(a), center.Y - radius * Math.Cos(a));
            if (sweep <= Math.PI)
            {
                ctx.BeginFigure(At(0), false);
                ctx.ArcTo(At(sweep), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            }
            else
            {
                ctx.BeginFigure(At(0), false);
                ctx.ArcTo(At(Math.PI), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
                ctx.ArcTo(At(sweep), new Size(radius, radius), 0, sweep - Math.PI > Math.PI, SweepDirection.Clockwise);
            }
            ctx.EndFigure(false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, PenOf(arc, thickness), geo);
    }
```

- [ ] **Step 2: 构建，确认 0 警告 0 错误**

```powershell
& 'D:\YQDESK\.dotnet\dotnet.exe' build 'D:\YQDESK\MyIsland-src\Island.sln' -c Release --nologo
```
预期：`已成功生成。0 个警告 0 个错误`。

- [ ] **Step 3: 提交**

```powershell
git -C 'D:\YQDESK\MyIsland-src' add src/Island.App/Controls/RenderKit.cs
git -C 'D:\YQDESK\MyIsland-src' commit -m "feat: 新增 360° 闭环 RenderKit.Ring（收起态用；Dial 保留给展开面板）"
```

---

### Task 3: 收起态排版器升级为「视觉组 + 可选分隔线」

**Files:**
- Modify: `src/Island.App/Controls/IslandCanvas.cs`（新增嵌套类型 `CompactGroup`；替换 `DrawCompactGroups`；改 **5** 个调用点：monitor / combo / dash / devices / clock）
- Modify: `src/Island.App/Controls/CompactSpec.cs`（追加 `GroupAdvance` 与排版纯函数 `PlanCompactGroups`）
- Modify: `src/Island.App/Controls/RenderKit.cs`（只改 `Ring` 的注释：负半径 / NaN 都不抛异常，但形状无意义 ⇒ 提前返回不画）
- Modify: `tests/Island.Tests/CompactSpecTests.cs`（追加组间距断言 + 15 条截断决策断言）
- Modify: `tests/Island.Tests/DesignSystemTests.cs`（`Ring` 的坏值防线用例，单列一条有名断言）

**Interfaces:**
- Consumes: `CompactSpec.SeparatorBand / PadX / ClockGap / DividerHalfHeight / DividerXAfter / GroupAdvance / PlanCompactGroups`（Task 1；`GroupAdvance` 与 `PlanCompactGroups` 在本任务 Step 3 新增）
- Produces:
  - `private sealed class IslandCanvas.CompactGroup`：`double Width { get; init; }`、`Action<double> Draw { get; init; }`、`bool DividerAfter { get; init; }`、`double? TrailingGap { get; init; }`（null = 用渲染层默认组间距）
  - `private double DrawCompactGroups(DrawingContext dc, Palette pal, Rect r, double cy, List<CompactGroup> groups, double gap = 16, double padding = 14, bool drawDividers = false)`
  - `public static (int Shown, double ContentWidth, double MoreOffset) CompactSpec.PlanCompactGroups(int count, Func<int, double> widthOf, Func<int, double?> trailingGapOf, double defaultGap, double available, Func<int, double> moreWidth)` —— 排版决策的**纯计算**（可见组数 / 内容总宽 / 「+N」偏移）；渲染层只照着画 ⇒「外观不变」由测试守住
  - **本任务是纯重构**：默认值保持旧的 `gap=16 / padding=14`，保证 monitor/devices/clipboard 等视图外观不变；combo 的新规格由 Task 4 显式传参实现。

- [ ] **Step 1: 写失败的测试**

在 `tests/Island.Tests/CompactSpecTests.cs` 的 `Run` 方法末尾追加（**分隔线位置的断言已在 Task 1 落盘，不要重复添加**；本任务新增的是「组间距」规则）：

```csharp
        // ---- 组到下一组的间距：组自带 TrailingGap 优先，否则用渲染层默认组间距 ----
        check("组间距默认取渲染层默认值", CompactSpec.GroupAdvance(null, 24) == 24);
        check("组自带 TrailingGap 时优先用它（combo 的 时间│占用组 = ClockGap 12）",
            CompactSpec.GroupAdvance(12, 24) == 12);
```

- [ ] **Step 2: 运行测试，确认失败**

```powershell
& 'D:\YQDESK\.dotnet\dotnet.exe' run --project 'D:\YQDESK\MyIsland-src\tests\Island.Tests\Island.Tests.csproj' -c Release --nologo
```
预期：编译失败（`CompactSpec.GroupAdvance` 还不存在）。

- [ ] **Step 3: 加嵌套类型与新的 DrawCompactGroups**

先给 `src/Island.App/Controls/CompactSpec.cs` 补一个纯函数（Task 1 已落盘，本任务追加）：

```csharp
    /// <summary>
    /// 组到下一组的间距：组自带 TrailingGap 优先，否则用渲染层默认组间距。
    /// mac 的 combo 有两种边界：有分隔线的边界 = 24px 总带（SeparatorBand），
    /// 没有分隔线的边界（时间│占用组）= 12px（ClockGap）。
    /// </summary>
    public static double GroupAdvance(double? trailingGap, double defaultGap)
        => trailingGap ?? defaultGap;

    /// <summary>
    /// 收起态分组排版（纯计算）：决定能显示几组、「+N」画在哪、内容占多宽。
    ///
    /// 规则（与 2026-11-25 修复前的旧渲染层逐条一致，别顺手改）：
    ///   - 每组拥有它**后面**那一段组间距：组 i 与组 i+1 之间 = 组 i 的 TrailingGap（缺省 defaultGap）；
    ///   - 预算校验时「+N」跟在候选组**后面**，所以为它预留的空间要用**候选组自己**的 TrailingGap
    ///     （用上一组的会算错：异质间距时会把候选组错误地挤掉或错误地放进来）；
    ///   - 第一个放不下的候选组出现时就停，它后面的组全部合并进「+N」。
    ///
    /// moreWidth(n) = 「+n」这行字的宽度（n = 被折叠掉的组数）。
    /// </summary>
    /// <returns>可见组数 / 收起态内容总宽（不含胶囊左右内边距，含「+N」占位）/ 「+N」左边缘相对第一组左边缘的偏移。</returns>
    public static (int Shown, double ContentWidth, double MoreOffset) PlanCompactGroups(
        int count, Func<int, double> widthOf, Func<int, double?> trailingGapOf,
        double defaultGap, double available, Func<int, double> moreWidth)
    {
        double used = 0;        // 已显示内容的宽（不含最后一段组间距）
        int shown = 0;
        double prevGap = 0;     // 上一组到本组的间距
        double lastGap = 0;     // 最后一个可见组之后的间距
        for (int i = 0; i < count; i++)
        {
            double next = used + (shown > 0 ? prevGap : 0) + widthOf(i);
            int remaining = count - i - 1;
            double ownGap = GroupAdvance(trailingGapOf(i), defaultGap);
            // 「+N」跟在候选组后面 ⇒ 为它预留的空间用候选组自己的间距
            double suffix = remaining > 0 ? ownGap + moreWidth(remaining) : 0;
            if (next + suffix > available) break;
            used = next;
            prevGap = ownGap;
            lastGap = ownGap;
            shown++;
        }
        double moreOffset = used + (shown > 0 ? lastGap : 0);   // 「+N」的左边缘
        double contentWidth = shown < count ? moreOffset + moreWidth(count - shown) : used;
        return (shown, contentWidth, moreOffset);
    }
```

再在 `src/Island.App/Controls/IslandCanvas.cs` 的 `DrawCompactGroups` 方法之前插入：

```csharp
    /// <summary>
    /// 收起态的一个视觉组：组内可放多个元素（彼此紧挨），组与组之间按 TrailingGap 留白，
    /// 需要时再在留白里画一条 Hairline 竖线（mac 的 DrawGaugeGroup + Gap()）。
    /// </summary>
    private sealed class CompactGroup
    {
        /// <summary>本组占用宽度（组内所有元素 + 组内间距）。</summary>
        public double Width { get; init; }
        /// <summary>绘制本组；参数是本组左边缘 x。</summary>
        public Action<double> Draw { get; init; } = _ => { };
        /// <summary>本组之后是否画分隔线（mac 的 Gap()）。最后一个可见组不画（其后是 +N 或空白）。</summary>
        public bool DividerAfter { get; init; }
        /// <summary>
        /// 到下一组的间距；null = 用渲染层默认组间距（gap 参数）。
        /// mac 的 combo 有两种边界：有分隔线的边界 = 24px 总带（SeparatorBand），
        /// 没有分隔线的边界（时间│占用组）= 12px（ClockGap）。
        /// </summary>
        public double? TrailingGap { get; init; }
    }
```

把 `DrawCompactGroups` 整个方法替换为：

```csharp
    /// <summary>
    /// 收起态分组排版：组与组之间按「上一组的 TrailingGap（缺省用 gap 参数）」留白，
    /// 需要时在留白里画 Hairline 竖线（mac 的 Gap()：线在「组尾 + 18px」，偏后 6px，不在正中）。
    /// 宽度不够时截断尾部并显示 "+N"；被截断时最后一个可见组**不**画分隔线。
    ///
    /// 排版决策（显示几组 / 「+N」画在哪 / 内容多宽）在 <see cref="CompactSpec.PlanCompactGroups"/>
    /// 里做纯计算，这里只负责照着画 —— 这样「外观不变」由测试守住，不靠人读代码。
    /// </summary>
    private double DrawCompactGroups(DrawingContext dc, Palette pal, Rect r, double cy,
        List<CompactGroup> groups, double gap = 16, double padding = 14,
        bool drawDividers = false)
    {
        double available = Math.Max(0, CompactWidthBudget - padding * 2);
        var plan = CompactSpec.PlanCompactGroups(groups.Count,
            i => groups[i].Width,
            i => groups[i].TrailingGap,
            gap, available,
            n => Text($"+{n}", 11, pal.TextDim, ppd: Ppd).Width);
        double x = r.X + padding;
        for (int i = 0; i < plan.Shown; i++)
        {
            double groupStart = x;
            groups[i].Draw(groupStart);
            double groupEnd = groupStart + groups[i].Width;
            bool hasNext = i < plan.Shown - 1;
            // 每个可见组之后都照常推进一段组间距：最后一个可见组后面是「+N」，
            // 也得留出这一段（旧行为；塌成紧贴会让「+N」贴住最后一根柱子）。
            x = groupEnd + CompactSpec.GroupAdvance(groups[i].TrailingGap, gap);
            // 组间分隔线：mac 的 Gap() 在总带里画线（组尾 + 18px = 组尾 + 1.5 × 单侧组间距）；
            // 最后一个可见组后面不画（其后是 +N 或空白）
            if (drawDividers && groups[i].DividerAfter && hasNext)
            {
                double lineX = CompactSpec.DividerXAfter(groupEnd);
                dc.DrawLine(RenderKit.PenOf(pal.Hairline, 1, 0.8),
                    new Point(lineX, cy - CompactSpec.DividerHalfHeight),
                    new Point(lineX, cy + CompactSpec.DividerHalfHeight));
            }
        }
        if (plan.Shown < groups.Count)
        {
            var more = Text($"+{groups.Count - plan.Shown}", 11, pal.TextDim, ppd: Ppd);
            dc.DrawText(more, new Point(r.X + padding + plan.MoreOffset, cy - more.Height / 2));
        }
        return Math.Min(CompactWidthBudget, Math.Max(80, padding * 2 + plan.ContentWidth));
    }
```

- [ ] **Step 4: 改造 4 个调用点（只换类型，行为不变）**

`DrawMonitor`（原 L612 附近）：

```csharp
    // 改前
    var groups = new List<(double Width, Action<double> Draw)>();
    // 改后
    var groups = new List<CompactGroup>();
```

它内部那一条核心柱群：

```csharp
    // 改前
    groups.Add((n * (bw + gap), x =>
    {
        ...（原样保留）...
    }));
    // 改后
    groups.Add(new CompactGroup
    {
        Width = n * (bw + gap),
        Draw = x =>
        {
            ...（原样保留）...
        },
    });
```

`DrawCombo`（原 L652 附近）：

```csharp
    // 改前
    var groups = new List<(double Width, Action<double> Draw)>();
    // 改后
    var groups = new List<CompactGroup>();
```

它内部的 `Gauge` 助手：

```csharp
    // 改前
    void Gauge(double? value, string letter, string sample = "100%", string? text = null, Color? color = null, double? fill = null)
    => groups.Add((25 + Math.Ceiling(RenderKit.NumWidth(sample, 12.5, true, ppd)),
        x => DrawDialGauge(dc, pal, x, cy, ppd, value, letter == "C" && text is null ? pal.Accent : neutral,
        letter, 20, 12.5, fill, text, color, sample)));
    // 改后（Task 4 会整体重写这一段，这里先保持等价）
    void Gauge(double? value, string letter, string sample = "100%", string? text = null, Color? color = null, double? fill = null)
    => groups.Add(new CompactGroup
    {
        Width = 25 + Math.Ceiling(RenderKit.NumWidth(sample, 12.5, true, ppd)),
        Draw = x => DrawDialGauge(dc, pal, x, cy, ppd, value, letter == "C" && text is null ? pal.Accent : neutral,
            letter, 20, 12.5, fill, text, color, sample),
    });
```

`DrawDash`（原 L664 附近）：

```csharp
    // 改前
    var groups = new List<(double Width, Action<double> Draw)>();
    // 改后
    var groups = new List<CompactGroup>();
```

它内部的 `Item` 助手：

```csharp
    // 改前
    void Item(string label, string value, string sample, Color? valueColor = null)
    {
        var icon = Text(label, 9.5, pal.TextTertiary, ppd: ppd);
        double width = Math.Max(Math.Max(RenderKit.NumWidth(sample, 13, true, ppd),
        RenderKit.NumWidth(value, 13, true, ppd)), icon.Width);
        groups.Add((width, x =>
        {
            dc.DrawText(icon, new Point(x, cy - 14));
            Num(dc, value, x, cy + 15, 13, valueColor ?? pal.Text, true, ppd);
        }));
    }
    // 改后（Task 5 会整体重写这一段，这里先保持等价）
    void Item(string label, string value, string sample, Color? valueColor = null)
    {
        var icon = Text(label, 9.5, pal.TextTertiary, ppd: ppd);
        double width = Math.Max(Math.Max(RenderKit.NumWidth(sample, 13, true, ppd),
        RenderKit.NumWidth(value, 13, true, ppd)), icon.Width);
        groups.Add(new CompactGroup
        {
            Width = width,
            Draw = x =>
            {
                dc.DrawText(icon, new Point(x, cy - 14));
                Num(dc, value, x, cy + 15, 13, valueColor ?? pal.Text, true, ppd);
            },
        });
    }
```

`DrawDevices`（原 L737 附近）：

```csharp
    // 改前
    var groups = new List<(double Width, Action<double> Draw)>();
    // 改后
    var groups = new List<CompactGroup>();
```

它内部两条 `groups.Add`：

```csharp
    // 改前
    groups.Add((fi.Width + 4 + RenderKit.NumWidth($"{files}", 13, true, ppd), x =>
    {
        dc.DrawText(fi, new Point(x, cy - fi.Height / 2));
        Num(dc, $"{files}", x + fi.Width + 4, Math.Round(cy + 5), 13, pal.Text, true, ppd);
    }));
    // 改后
    groups.Add(new CompactGroup
    {
        Width = fi.Width + 4 + RenderKit.NumWidth($"{files}", 13, true, ppd),
        Draw = x =>
        {
            dc.DrawText(fi, new Point(x, cy - fi.Height / 2));
            Num(dc, $"{files}", x + fi.Width + 4, Math.Round(cy + 5), 13, pal.Text, true, ppd);
        },
    });
```

```csharp
    // 改前
    groups.Add((width, x =>
    {
        ...（原样保留）...
    }));
    // 改后
    groups.Add(new CompactGroup
    {
        Width = width,
        Draw = x =>
        {
            ...（原样保留）...
        },
    });
```

`DrawClock`（原 L937 附近，**brief 原先漏列，但签名换成 `List<CompactGroup>` 后不改就编译不过**，故一并转换）：

```csharp
    // 改前
    var groups = new List<(double Width, Action<double> Draw)>
    {
        (tw, x => Num(dc, DateTime.Now.ToString("HH:mm"), x, Math.Round(cy + 15 * 0.36), 15, pal.Text, true, ppd))
    };
    // 改后
    var groups = new List<CompactGroup>
    {
        new CompactGroup
        {
            Width = tw,
            Draw = x => Num(dc, DateTime.Now.ToString("HH:mm"), x, Math.Round(cy + 15 * 0.36), 15, pal.Text, true, ppd),
        },
    };
```

（它内部两条 `groups.Add` 同样按上面的写法换成 `new CompactGroup { Width = …, Draw = … }`。）

- [ ] **Step 5: 构建并跑测试，确认全绿**

```powershell
& 'D:\YQDESK\.dotnet\dotnet.exe' build 'D:\YQDESK\MyIsland-src\Island.sln' -c Release --nologo
& 'D:\YQDESK\.dotnet\dotnet.exe' run --project 'D:\YQDESK\MyIsland-src\tests\Island.Tests\Island.Tests.csproj' -c Release --nologo
```
预期：构建 0 警告 0 错误；测试 `全部通过：325 条`（307 基线 + 本任务新增 2 条组间距断言 + 15 条截断决策断言 + 1 条 Ring 坏值用例），退出码 0。

- [ ] **Step 6: 提交**

```powershell
git -C 'D:\YQDESK\MyIsland-src' add src/Island.App/Controls/CompactSpec.cs src/Island.App/Controls/IslandCanvas.cs src/Island.App/Controls/RenderKit.cs tests/Island.Tests/CompactSpecTests.cs tests/Island.Tests/DesignSystemTests.cs
git -C 'D:\YQDESK\MyIsland-src' commit -m "refactor: 收起态排版器升级为视觉组 + 可选组间分隔线（对齐 mac 的 Gap/Divider）"
```

> **2026-11-25 修复轮 1（评审后）**：本任务的首个提交（逐字照抄上面的代码块）带两处截断路径的外观回归 ——
> 「+N」紧贴最后一个可见元素、以及 `suffix` 用错组间距导致截断判据放宽。已修：
> 截断决策抽成纯函数 `CompactSpec.PlanCompactGroups`（可见组数 / 内容总宽 / 「+N」偏移），渲染层只照着画；
> 「+N」按旧行为推进一个组间距；`suffix` 改用候选组自己的 `TrailingGap`（顺带修掉 Task 4 异质间距的前瞻问题）。
> 修复提交：`fix: 恢复 +N 截断路径的旧行为（间距与判据），并把截断决策抽成可测纯函数`。

---

### Task 4: DrawCombo 按 mac 重排为四组

**Files:**
- Modify: `src/Island.App/Controls/IslandCanvas.cs`（`DrawCombo`）
- Modify: `src/Island.App/Controls/IslandWindow.xaml.cs`（同步 `ComboNumericHero`）

**Interfaces:**
- Consumes: `CompactSpec`（Task 1，含 `NetGap` / `ArrowValueGap`）、`RenderKit.Ring`（Task 2）、`CompactGroup` / `DrawCompactGroups`（Task 3）
- Produces: `private double DrawCombo(DrawingContext dc, Palette pal, Rect r, double cy, MonitorSnapshot? snap, Services.ThermalSnapshot? thermal)`（签名不变，行为改为四组）；`public bool ComboNumericHero { get; set; }`（IslandCanvas 新属性）

- [ ] **Step 1: 写失败的测试**

在 `tests/Island.Tests/CompactSpecTests.cs` 的 `Run` 方法末尾追加：

```csharp
        // ---- combo 的组结构：时间│占用组│温度磁盘组│网速（对应 mac LayoutCombo）----
        check("combo 占用组 = 3 环（C/M/G）总宽 185", CompactSpec.GaugeGroupWidth(3, _ => 30) == 185);
        check("combo 系统组 = 3 环（C/G/D）总宽 185", CompactSpec.GaugeGroupWidth(3, _ => 30) == 185);
        // mac 的 combo 只有 **两处** 分隔线（占用组│温度磁盘组、温度磁盘组│网速）；
        // 时间│占用组之间没有线，只有 ClockGap=12 的留白。
        double content = CompactSpec.ComboContentWidth(40, 185, 185, 60, 60);
        check("combo 内容宽 = 时钟12 + 占用组 + (24+系统组) + 24 + 网速",
            content == 40 + 12 + 185 + (24 + 185) + 24 + 60 + 7 + 60);
```

- [ ] **Step 2: 运行测试，确认失败**

```powershell
& 'D:\YQDESK\.dotnet\dotnet.exe' run --project 'D:\YQDESK\MyIsland-src\tests\Island.Tests\Island.Tests.csproj' -c Release --nologo
```
预期：断言 `FAIL`（`ComboContentWidth` 已存在但本条断言的期望值按 mac 的两分隔线口径写；确认它在改动前后能区分）。

- [ ] **Step 3: 重写 DrawCombo**

把 `src/Island.App/Controls/IslandCanvas.cs` 的 `DrawCombo` 整体替换为：

```csharp
    /// <summary>
    /// 信息胶囊（combo）：时间 │ 占用环组(C/M/G) │ 温度磁盘环组(C/G/D) │ 网速。
    /// 对应 mac 版 LayoutCombo + DrawCombo（src/Island.Avalonia/IslandCanvas.cs L612-L707）。
    /// 分隔线只有**两处**（占用组│温度磁盘组、温度磁盘组│网速，mac 的 Gap() 只调用两次：:694/:696）；
    /// 时间│占用组之间没有线，只有 ClockGap=12 的留白（mac :677 的 x += L.ClockW + L.ClockGap）。
    /// 规格：环 20px 闭环（RenderKit.Ring），环心字母，数值 13px 等宽放环外，槽宽 21px，
    /// 有分隔线的边界总带 24px（SeparatorBand），线在「组尾 + 18px」。
    /// 配色沿用 Windows 的「暗色科技」口径（见 Global Constraints），不是 mac 的每资源一色。
    /// </summary>
    private double DrawCombo(DrawingContext dc, Palette pal, Rect r, double cy,
        MonitorSnapshot? snap, Services.ThermalSnapshot? thermal)
    {
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        DrawCapsuleBase(dc, pal);
        Color neutral = Neutral(pal);
        var groups = new List<CompactGroup>();

        // 一个环项：环心字母 + 环外等宽数值。
        // 环弧长 = share（占用率/100 或 温度/125），环色 = ArcColor，数值色 = TextColor。
        // 数值宽度用 RenderKit.NumWidth 量（等宽排版有缓存，不额外分配）。
        void AddGaugeGroup(List<(string Letter, double Share, string Text, Color ArcColor, Color TextColor)> items,
                          bool dividerAfter)
        {
            if (items.Count == 0) return;
            double groupW = CompactSpec.GaugeGroupWidth(items.Count,
                i => RenderKit.NumWidth(items[i].Text, CompactSpec.FigureFont, true, ppd));
            groups.Add(new CompactGroup
            {
                Width = groupW,
                Draw = x =>
                {
                    double gx = x;
                    for (int i = 0; i < items.Count; i++)
                    {
                        var it = items[i];
                        RenderKit.Ring(dc, new Point(gx + CompactSpec.Dial / 2, cy), CompactSpec.Dial,
                            Math.Max(1.8, CompactSpec.Dial * 0.14), it.Share, pal.Track, it.ArcColor);
                        // 环心字母（mac DrawGaugeCenter 的 DecorLetters 分支）
                        var letter = Text(it.Letter, CompactSpec.Dial * 0.52, it.ArcColor, true, ppd);
                        dc.DrawText(letter, new Point(gx + (CompactSpec.Dial - letter.Width) / 2, cy - letter.Height / 2));
                        gx += CompactSpec.Dial + CompactSpec.DialGap;
                        Num(dc, it.Text, gx, Math.Round(cy + CompactSpec.FigureFont * 0.36),
                            CompactSpec.FigureFont, it.TextColor, true, ppd);
                        gx += Math.Max(RenderKit.NumWidth(it.Text, CompactSpec.FigureFont, true, ppd),
                            CompactSpec.ValueMinW) + CompactSpec.GaugeGap;
                    }
                },
                DividerAfter = dividerAfter,
            });
        }

        // ---- 组 1：时间 ----
        // mac 的 ComboStyle.ClockStrong = !_comboNumericHero（IslandCanvas.cs:617-618 + :575）：
        // 数字主角（hero）模式下时钟是**非粗体**的次级文字；经典版才是粗体主角。
        if (WantCombo("clock"))
        {
            var clock = Text(DateTime.Now.ToString("HH:mm"), CompactSpec.FigureFont,
                ComboNumericHero ? pal.TextDim : pal.Text, !ComboNumericHero, ppd);
            groups.Add(new CompactGroup
            {
                Width = clock.WidthIncludingTrailingWhitespace,
                Draw = x => Baseline(dc, clock, x, Math.Round(cy + CompactSpec.FigureFont * 0.36)),
                // 时间后面**没有分隔线**（mac 只在占用组│系统组、系统组│网速两处画线），只有 12px 留白
                DividerAfter = false,
                TrailingGap = CompactSpec.ClockGap,
            });
        }

        // ---- 组 2：占用环组（C/M/G）----
        var dials = new List<(string Letter, double Share, string Text, Color ArcColor, Color TextColor)>();
        if (WantCombo("dials") && snap is not null)
        {
            // C/M 无条件画（mac 同款：0% 也画空环 + "0%"）—— 环数恒定，胶囊宽度不因读数跳到 0 而抖。
            var c = LevelColor(snap.CpuUsage, pal.Accent, pal);
            dials.Add(("C", snap.CpuUsage / 100.0, $"{Math.Round(snap.CpuUsage)}%", c,
                LevelColor(snap.CpuUsage, pal.Text, pal)));
            var m = LevelColor(snap.MemoryUsagePercent, neutral, pal);
            dials.Add(("M", snap.MemoryUsagePercent / 100.0, $"{Math.Round(snap.MemoryUsagePercent)}%", m,
                LevelColor(snap.MemoryUsagePercent, pal.Text, pal)));
            // G 保留 >0 判断：Windows 无独显 / 读不到时不出这个环（改动前的既有取舍，mac 是恒画三个环）。
            if (snap.GpuUsage > 0)
            {
                var g = LevelColor(snap.GpuUsage, neutral, pal);
                dials.Add(("G", snap.GpuUsage / 100.0, $"{Math.Round(snap.GpuUsage)}%", g,
                    LevelColor(snap.GpuUsage, pal.Text, pal)));
            }
        }
        // 占用组之后有分隔线（mac :694）
        AddGaugeGroup(dials, dividerAfter: true);

        // ---- 组 3：温度 / 磁盘环组（C/G/D）----
        var sys = new List<(string Letter, double Share, string Text, Color ArcColor, Color TextColor)>();
        if (snap is not null)
        {
            double? ct = thermal?.CpuTemp, gt = thermal?.GpuTemp;
            // 温度 ≤0 视为「读不到」（mac :634-635 的 is > 0 ? … : null）：传感器报 0℃ 时显示占位符，不是 "0°"。
            double? ctv = ct is > 0 ? ct : null;
            double? gtv = gt is > 0 ? gt : null;
            if (WantCombo("cputemp"))
            {
                var c = TempColor(ctv, pal);
                sys.Add(("C", ctv is { } t ? Math.Clamp(t / 125.0, 0, 1) : 0,
                    ctv is { } tv ? $"{Math.Round(tv)}°" : "--", c, c));
            }
            if (WantCombo("gputemp"))
            {
                var g = TempColor(gtv, pal);
                sys.Add(("G", gtv is { } t2 ? Math.Clamp(t2 / 125.0, 0, 1) : 0,
                    gtv is { } tv2 ? $"{Math.Round(tv2)}°" : "--", g, g));
            }
            if (WantCombo("disk"))
            {
                var d = LevelColor(snap.DiskUsagePercent, neutral, pal);
                sys.Add(("D", snap.DiskUsagePercent / 100.0,
                    $"{Math.Round(snap.DiskUsagePercent)}%", d,
                    LevelColor(snap.DiskUsagePercent, pal.Text, pal)));
            }
        }
        // 系统组之后有分隔线（mac :696）
        AddGaugeGroup(sys, dividerAfter: true);

        // ---- 组 4：网速（↓值 ↑值，中性色）----
        if (WantCombo("net") && snap is not null)
        {
            groups.Add(new CompactGroup
            {
                Width = NetWidth(CompactSpec.FigureFont, snap, ppd, CompactSpec.NetGap, NetSpeedFormat.Fixed),
                Draw = x => DrawNet(dc, pal, x, Math.Round(cy + CompactSpec.FigureFont * 0.36),
                    CompactSpec.FigureFont, snap, ppd, CompactSpec.NetGap, NetSpeedFormat.Fixed),
            });
        }

        // 组间距：mac 的 combo 有分隔线的边界总带 24px（SeparatorBand），左右内边距 10px（ComboPadX）
        return DrawCompactGroups(dc, pal, r, cy, groups,
            gap: CompactSpec.SeparatorBand, padding: CompactSpec.PadX, drawDividers: true);
    }
```

- [ ] **Step 4: 配套改三处**

1. `IslandCanvas` 新增属性（放在 `ComboItems` 声明附近）：

```csharp
    /// <summary>信息胶囊样式：true = 数字主角（时钟降为次级文字）。与设置中心的「数字主角」开关同源（mac 同名开关）。</summary>
    public bool ComboNumericHero { get; set; }
```

2. `src/Island.App/Controls/IslandWindow.xaml.cs` 里 `islandCanvas.CompactView = _settings.CompactView;` 附近补一行：

```csharp
    islandCanvas.ComboNumericHero = _settings.ComboNumericHero;
```

3. `DrawNet` / `NetWidth` 里的数值改成**定宽**格式（mac 用 `FormatSpeedFixed`，Windows 现在用 `CompactSpeed`）：把两处 `MonitorSnapshot.CompactSpeed(...)` 换成 `MonitorSnapshot.FormatSpeedFixed(...)`。

4. **网速间距必须走 `CompactSpec`**（Windows 现在把 ↓↔↑ 硬编码成 10，与 mac 的 7 冲突）：
   - `NetWidth` / `DrawNet`（`IslandCanvas.cs` L449-L463）里的 `+ 3` 换成 `CompactSpec.ArrowValueGap`（3）；
   - 两个方法各加一个 `netGap` 参数（**绘制位置与预留宽度必须是同一个口径**），三个调用点：
     **combo 传 `CompactSpec.NetGap`（7）**；**clock / monitor 传 `CompactSpec.DashNetGap`（10）**（与原硬编码同值，外观不变）；
   - 注意：Windows 的 dash 视图**不走** `NetWidth`/`DrawNet`（它把 ↓/↑ 画成两个独立 item），
     mac 的 DashLayout.NetGap = 10（IslandCanvas.cs L1866）对应的是 Windows 这两个视图；
   - 顺带清理：`DrawCompactGroups` 循环后的 `x` 是死变量（「+N」按 `plan.MoreOffset` 画），
     改成「最后一个可见组不推进」，并在累加处注明排版口径唯一在 `CompactSpec.PlanCompactGroups`。

5. **网速读数格式必须按视图区分**（修审轮 Important 3）：`NetWidth`/`DrawNet` 再加一个格式参数
   （`NetSpeedFormat.Fixed` = `FormatSpeedFixed` 恒 6 字符；`NetSpeedFormat.Compact` = 旧的 `CompactSpeed`）：
   **只有 combo 传定宽**（mac combo/dash 口径），**clock / monitor 传旧格式**（保持改动前外观，
   它们的网速格式对齐留给 Task 5）。宽度量算与绘制都走 `NetSpeedText()` 一个函数，不许各写一份。

6. **combo 的设计宽度上限 560 → 640**（修审轮 Important 4）：`IslandWindow.xaml.cs` 的
   `CollapsedSize("combo") => (560, 44)` 改成 `(640, 44)`。
   胶囊实际宽度仍按实测内容（宿主 `CurrentCollapsedSize()` 用 `clamp(CompactContentWidth, 110, 上限)`），
   所以常态不变；上限只决定最坏情况还装不装得下：两个读数同时到 100%（4 字符，比最小槽 21 宽约 7px）
   时 560 会把网速整段挤进「+N」，640 才有余量。⚠️ Task 5 的 Step 5 也改同一张尺寸表（dash 的高度 69→44），
   两处改动要一起看，别互相覆盖。

- [ ] **Step 5: 构建并跑测试**

```powershell
& 'D:\YQDESK\.dotnet\dotnet.exe' build 'D:\YQDESK\MyIsland-src\Island.sln' -c Release --nologo
& 'D:\YQDESK\.dotnet\dotnet.exe' run --project 'D:\YQDESK\MyIsland-src\tests\Island.Tests\Island.Tests.csproj' -c Release --nologo
```
预期：0 警告 0 错误；`全部通过：330 条`（325 基线 + 本任务新增 5 条 combo 组结构断言），退出码 0。

- [ ] **Step 6: 离屏渲染核对 combo**

用真实控件离屏渲染出 combo 收起态（沿用 `ui-preview-2026-11-24/` 的做法），确认：时间在最左、**两处**分隔线（占用组│温度磁盘组、温度磁盘组│网速；时间│占用组之间只有 12px 留白、无线）、C/M/G 三个闭环字母环、C/G/D 三个闭环字母环、网速在最右。
产物放 `D:\YQDESK\ui-preview-2026-11-25\`（`combo-hero.png` / `combo-classic.png` / `combo-zero.png` /
`combo-worst-640.png` / `combo-worst-560.png` / `combo-temp0.png` + 离屏渲染用的 `harness/`），**不要提交进仓库**。

- [ ] **Step 7: 提交**

```powershell
git -C 'D:\YQDESK\MyIsland-src' add src/Island.App/Controls/IslandCanvas.cs src/Island.App/Controls/IslandWindow.xaml.cs tests/Island.Tests/CompactSpecTests.cs
git -C 'D:\YQDESK\MyIsland-src' commit -m "feat: combo 重排为 mac 的四组（时间│占用组│温度磁盘组│网速）+ 360° 闭环 + 13px 规格 + 组间分隔线"
```

> **2026-11-25 实现说明（Task 4 落地后回填）**：
> - **分隔线是两处，不是三处**：mac 的 `Gap()` 只调用两次（占用组│温度磁盘组、温度磁盘组│网速），
>   时间│占用组之间只有 `ClockGap=12` 的留白、无线。原文 Step 6 写的「三条分隔线」是旧稿笔误，已改正。
> - **边界总带**：`DrawCompactGroups` 按硬性要求传 `gap: CompactSpec.SeparatorBand`(24)、`padding: CompactSpec.PadX`(10)、`drawDividers: true`；
>   带分隔线的组若显式设 `TrailingGap`，该值必须等于 `SeparatorBand`（本任务里只有时钟组带 `TrailingGap = ClockGap`）。
> - **断言**：`全部通过：328 条`（325 基线 + 3 条）。这 3 条断言**加进去就是绿的** ——
>   `CompactSpec` 的 mac 公式在 Task 1 就写好了，本任务断言的是「公式已存在且口径是 mac 的两处分隔线」，
>   属于特征化断言（不是先红后绿）。为证明它不是空转，做过一次变异：把 `ComboContentWidth` 里网速前的总带
>   从 `SeparatorBand`(24) 改成 `GroupGap`(12) → 该断言 FAIL（连同既有 3 条），改回即绿。
> - **网速定宽**：`NetWidth`/`DrawNet` 改用 `MonitorSnapshot.FormatSpeedFixed`（恒 6 字符），
>   箭头与数值的间距走 `CompactSpec.ArrowValueGap`(3)，↓值与↑之间走 `netGap` 参数：
>   combo = `CompactSpec.NetGap`(7)，clock / monitor = `CompactSpec.DashNetGap`(10)（与原硬编码同值，外观不变）。
> - **离屏核对**（`D:\YQDESK\ui-preview-2026-11-25\combo-hero.png` / `combo-classic.png`）：
>   两条分隔线落在 DIP 231 与 414，正好是「组尾 + 18px」（组尾 213 / 396），下一组从「组尾 + 24」开始 ——
>   即线落在 24px 总带里偏后 6px，与 mac 一致；时钟与第一个环之间无线。
> - **一处 Windows 独有的取舍（不是 mac 行为基准）**：用户把「温度/硬盘」三项全关掉时系统组为空 →
>   占用组仍带 `DividerAfter=true`，于是占用组与网速之间会画一条分隔线。
>   mac 侧这个状态**不可达**（mac 无条件添加 C/G/D 三个系统环，IslandCanvas.cs:636-643，
>   `L.SystemGauges.Count > 0` 恒真，:694/:696 的条件是防御性代码），
>   所以不能拿 mac 当这一处的基准；Windows 有三个独立开关（IslandWindow.xaml.cs + Settings.cs 默认串），
>   该状态可达，画一条线把「占用组│网速」分开是合理取舍，宽度口径也与 mac 一致（两边都保留 24px 带）。
> - **延后项（不在本轮处理）**：① 温度占位符用 `"--"`（brief 原文）而 mac 用 `"—"`；
>   ② `src/Island.Core/Monitoring/MonitorModels.cs` 里 `CompactSpeed` 的注释已漂移（该函数现只剩 dash 与监控页在用）→ 延后到 Task 5/6；
>   ③ `CompactSpec.PlanCompactGroups` 返回三元组而非具名 record struct → 延后。

> **2026-11-25 修复轮 1（评审后）**：评审逐条对照 mac 源码后确认三条硬性要求（24px 总带 / 时间边界无线 / 网速定宽同口径）
> 已正确落地、分隔线几何与 mac 逐式一致；但有 4 条 Important 必须修，其中两条与 brief 的**逐字代码**冲突（brief 自身有错）——
> 裁决以 mac 现有实现为准（本项目总原则），brief 的错误代码已改：
> - **Important 1（0% 时占用环整环消失）**：去掉 C/M 的 `> 0` 门，恢复无条件画 C/M 环
>   （mac :625-633 恒画三个环；改动前的旧 Windows 代码也是 C/M 恒画 —— 本次改动把它变成了新引入的回归）。
>   G 保留 `> 0`（Windows 无独显时的既有取舍，已在注释里写明它不是 mac 口径）。
> - **Important 2（断言记录了实现无法保证的结构）**：删掉与 Task 1 重复的「3 环 = 185」求值，
>   改成覆盖真实环数形态（1 环 = 55、2 环 = 120；3 环 = 185 已由 Task 1 的用例锁住）。
> - **Important 3（格式替换外溢到 clock / monitor）**：`NetWidth`/`DrawNet` 加格式参数，只有 combo 用定宽格式，
>   clock/monitor 恢复改动前外观（旧 `CompactSpeed` + 10 间距）；dash 留给 Task 5 按 mac 对齐。
> - **Important 4（最坏情况网速整段消失）**：`CollapsedSize("combo")` 560 → 640
>   （胶囊仍按实测内容定宽，常态宽度不变；只是把「装不下就丢网速」的最坏情况消掉）。
> - **Minor 3（温度 0 被当作有效读数）**：温度 ≤0 视为读不到（对齐 mac :634-635）。
> - **Minor 4（hero 模式时钟字重）**：数字主角模式下时钟用非粗体 + `pal.TextDim`（对齐 mac :617-618 + :575）。
> - **Minor 7（补钉住「两处分隔线」的断言）**：新增 `GroupAdvance(ClockGap, SeparatorBand) == 12` 与
>   `GroupAdvance(null, SeparatorBand) == 24` 两条断言。
> 修复提交：`fix: combo 对齐 mac 细节（0% 仍画环 / 网速格式不外溢 / 宽度上限 640 / 温度与时钟口径）`。

---

### Task 5: dash 改单行 + 高度 69 → 44

**Files:**
- Modify: `src/Island.App/Controls/IslandCanvas.cs`（`DrawDash` 整体重写）
- Modify: `src/Island.App/Controls/IslandWindow.xaml.cs`（收起态尺寸表）

**Interfaces:**
- Consumes: `CompactSpec`（Task 1，含 `DashPadX` / `ArrowValueGap` / `DashNetGap`）、`RenderKit.Ring`（Task 2）、`CompactGroup` / `DrawCompactGroups`（Task 3）
- Produces: `DrawDash` 单行版；尺寸表 `"dash" => (560, 44)`

- [ ] **Step 1: 写失败的测试**

在 `tests/Island.Tests/CompactSpecTests.cs` 的 `Run` 方法末尾追加：

```csharp
        // ---- dash 单行：标签与数值同行（不再是标签在上、数值在下的两行网格）----
        check("dash 单行项宽 = 标签 + 箭头数值间距 3 + 数值槽", CompactSpec.InlineItemWidth(28, 30) == 61);
        check("dash 单行项宽窄值按最小槽 21 兜底", CompactSpec.InlineItemWidth(28, 10) == 52);
```

- [ ] **Step 2: 运行测试，确认失败**

```powershell
& 'D:\YQDESK\.dotnet\dotnet.exe' run --project 'D:\YQDESK\MyIsland-src\tests\Island.Tests\Island.Tests.csproj' -c Release --nologo
```
预期：编译失败（`CompactSpec.InlineItemWidth` 不存在）。

- [ ] **Step 3: 确认 CompactSpec 已有 InlineItemWidth（Task 1 已提供，本步不要重复插入）**

`CompactSpec.InlineItemWidth` 由 Task 1 提供（标签 + `CompactSpec.ArrowValueGap` + 数值槽，窄值按 `ValueMinW` 兜底），
本步**不要**再往 `CompactSpec.cs` 里加方法，只需在 Step 4 里调用它。

- [ ] **Step 4: 把 DrawDash 改成单行**

把 `src/Island.App/Controls/IslandCanvas.cs` 的 `DrawDash` 整体替换为：

```csharp
    /// <summary>
    /// dash 视图（全读数单行）：核心柱群 │ 温度项组 │ 占用项组 │ 电池组 │ 网速，组间画 Hairline 分隔线。
    /// 对应 mac 版 dash 的单行形态（mac: 频谱│剪贴板计数│温度组│占用组│电池│设备环│网速）。
    ///
    /// 与旧版的差别：旧版是「标签在上、数值在下」的两行网格（高 69px）；
    /// 现在是单行（高 44px），标签与数值同行。内容项与「用户定制显示项」语义不变。
    ///
    /// CompactDensity 的含义随之调整：旧版它只控制组间距（16/26），而组间距已被规格统一为
    /// 12 + 分隔线，所以改为**决定显示哪些组**（阈值 <390 / <560 不变）：
    ///   Minimal  → 核心柱 + 温度组 + 网速
    ///   Balanced → 上述 + 占用组
    ///   Full     → 上述 + 电池组
    /// </summary>
    private double DrawDash(DrawingContext dc, Palette pal, Rect r, double cy,
        MonitorSnapshot? snap, Services.ThermalSnapshot? thermal)
    {
        if (snap is null) return 0;
        if (r.Width < 40 || r.Height < 20) return 0;
        double ppd = Ppd;
        DrawCapsuleBase(dc, pal);
        var density = DensityFor();
        var groups = new List<CompactGroup>();

        // 单行项：标签（9.5px 三级文字）+ 间距 3 + 数值（13px 等宽）
        void InlineItem(string label, string value, string sample, Color? valueColor, ref double x)
        {
            var icon = Text(label, 9.5, pal.TextTertiary, ppd: ppd);
            Baseline(dc, icon, x, Math.Round(cy + 13 * 0.36));
            x += icon.Width + CompactSpec.ArrowValueGap;
            x = Num(dc, value, x, Math.Round(cy + 13 * 0.36), 13, valueColor ?? pal.Text, true, ppd);
            x += CompactSpec.GaugeGap;
        }

        // 一个组：若干单行项，组内用 GaugeGap 隔开
        void AddItemGroup(List<(string Label, string Value, string Sample, Color? Color)> items, bool dividerAfter)
        {
            if (items.Count == 0) return;
            var labels = items.Select(i => (double)Text(i.Label, 9.5, pal.TextTertiary, ppd: ppd).Width).ToArray();
            var values = items.Select(i => (double)RenderKit.NumWidth(i.Sample, 13, true, ppd)).ToArray();
            double groupW = 0;
            for (int i = 0; i < items.Count; i++)
                groupW += CompactSpec.InlineItemWidth(labels[i], values[i]) + CompactSpec.GaugeGap;
            groupW -= CompactSpec.GaugeGap;
            groups.Add(new CompactGroup
            {
                Width = groupW,
                Draw = x =>
                {
                    double ix = x;
                    for (int i = 0; i < items.Count; i++)
                    {
                        InlineItem(items[i].Label, items[i].Value, items[i].Sample, items[i].Color, ref ix);
                    }
                },
                DividerAfter = dividerAfter,
            });
        }

        // ---- 组 1：核心柱群（保持现状，只改成分组形式）----
        var cores = snap.CoreUsages;
        if (Want("cores") && cores is not null && cores.Length > 0)
        {
            int n = Math.Min(8, cores.Length);
            double bw = 4, gap = 2.6, maxH = 26;
            double baseY = cy + maxH / 2 - 4;
            groups.Add(new CompactGroup
            {
                Width = n * (bw + gap),
                Draw = x =>
                {
                    for (int i = 0; i < n; i++)
                    {
                        double v = Easing.Clamp(cores[i] / 100.0, 0, 1);
                        dc.DrawRoundedRectangle(BrushOf(pal.Track), null,
                            RenderKit.SafeRect(x + i * (bw + gap), baseY - maxH, bw, maxH), 1.6, 1.6);
                        double h = 2 + v * maxH;
                        dc.DrawRoundedRectangle(BrushOf(LevelColor(v * 100, pal.Accent, pal)), null,
                            RenderKit.SafeRect(x + i * (bw + gap), baseY - h, bw, h), 1.6, 1.6);
                    }
                },
                DividerAfter = true,
            });
        }

        // ---- 组 2：温度项组（CPU/GPU/SSD）----
        double? ct = thermal?.CpuTemp, gt = thermal?.GpuTemp, st = thermal?.SsdTemp;
        var temps = new List<(string Label, string Value, string Sample, Color? Color)>();
        if (Want("cputemp")) temps.Add(("CPU", ct is { } c1 ? $"{Math.Round(c1)}°" : "--", "125°", TempColor(ct, pal)));
        if (Want("gputemp")) temps.Add(("GPU", gt is { } g1 ? $"{Math.Round(g1)}°" : "--", "125°", TempColor(gt, pal)));
        if (Want("ssd")) temps.Add(("SSD", st is { } s1 ? $"{Math.Round(s1)}°" : "--", "125°", TempColor(st, pal)));
        AddItemGroup(temps, dividerAfter: density != CompactDensity.Minimal);

        // ---- 组 3：占用项组（内存/GPU%/磁盘）----
        var usage = new List<(string Label, string Value, string Sample, Color? Color)>();
        if (Want("mem")) usage.Add(("MEM", $"{snap.MemoryUsagePercent:F0}%", "100%", LevelColor(snap.MemoryUsagePercent, pal.Text, pal)));
        if (Want("gpu")) usage.Add(("GPU%", $"{snap.GpuUsage:F0}%", "100%", LevelColor(snap.GpuUsage, pal.Text, pal)));
        if (Want("disk")) usage.Add(("DISK", $"{snap.DiskUsagePercent:F0}%", "100%", LevelColor(snap.DiskUsagePercent, pal.Text, pal)));
        AddItemGroup(usage, dividerAfter: density != CompactDensity.Minimal);

        // ---- 组 4：电池组 ----
        var bat = DeviceService.Snapshot.FirstOrDefault(d => d.Kind == "battery");
        if (density == CompactDensity.Full && Want("battery") && bat is not null)
        {
            Color bc = bat.Charging ? pal.Down
                : bat.Percent <= 20 ? pal.Up
                : bat.Percent <= 40 ? pal.Warning
                : pal.Text;
            AddItemGroup(new List<(string, string, string, Color?)>
            {
                (bat.Charging ? "电" : "电", $"{bat.Percent}%", "100%", bc),
            }, dividerAfter: true);
        }

        // ---- 组 5：网速（↓ ↔ ↑ 间距用 CompactSpec.DashNetGap = 10，mac DashLayout.NetGap）----
        if (Want("net"))
        {
            AddItemGroup(new List<(string, string, string, Color?)>
            {
                ("↓", MonitorSnapshot.FormatSpeedFixed(snap.DownloadSpeed), "  0.0B", null),
                ("↑", MonitorSnapshot.FormatSpeedFixed(snap.UploadSpeed), "  0.0B", null),
            }, dividerAfter: false);
        }

        // dash 的胶囊左右内边距用 CompactSpec.DashPadX = 14（mac DashPadX），不是 combo 的 PadX = 10
        return DrawCompactGroups(dc, pal, r, cy, groups, padding: CompactSpec.DashPadX, drawDividers: true);
    }
```

- [ ] **Step 5: 同步宿主尺寸表**

在 `src/Island.App/Controls/IslandWindow.xaml.cs` 的尺寸表里把：

```csharp
        "dash" => (560, 69),    // LiteMonitor 同款全读数行（含硬盘/SSD 温度/电池）
```

改成：

```csharp
        "dash" => (560, 44),    // 全读数单行（核心柱│温度组│占用组│电池│网速，组间分隔线；对齐 mac）
```

- [ ] **Step 6: 构建并跑测试**

```powershell
& 'D:\YQDESK\.dotnet\dotnet.exe' build 'D:\YQDESK\MyIsland-src\Island.sln' -c Release --nologo
& 'D:\YQDESK\.dotnet\dotnet.exe' run --project 'D:\YDESK\MyIsland-src\tests\Island.Tests\Island.Tests.csproj' -c Release --nologo
```
预期：0 警告 0 错误；`全部通过：314 条`，退出码 0。

- [ ] **Step 7: 变形帧检查（dash 单行化后必做）**

渲染 dash 收起态在多个中间宽度（110 / 140 / 170 / 200 / 230 / 260 / 300 / 345 / 420 px）下的变形帧，确认：内容始终水平居中、元素不叠字、不溢出胶囊、胶囊高度稳定在 44px。
产物放 `D:\YQDESK\ui-preview-2026-11-25\`，**不提交进仓库**。

- [ ] **Step 8: 提交**

```powershell
git -C 'D:\YQDESK\MyIsland-src' add src/Island.App/Controls/IslandCanvas.cs src/Island.App/Controls/IslandWindow.xaml.cs src/Island.App/Controls/CompactSpec.cs tests/Island.Tests/CompactSpecTests.cs
git -C 'D:\YQDESK\MyIsland-src' commit -m "feat: dash 改单行（组间分隔线，高度 69→44px），对齐 mac 的 dash 形态"
```

---

### Task 6: 端到端验收与文档回填

**Files:**
- Modify: `docs/superpowers/specs/2026-11-25-cross-platform-consistency-design.md`（阶段 1 状态行）
- Modify: `docs/superpowers/specs/2026-11-25-windows-compact-align-mac-design.md`（验收结论）

**Interfaces:**
- Consumes: Task 1-5 的全部产出
- Produces: 验收记录

- [ ] **Step 1: 全量门禁**

```powershell
& 'D:\YQDESK\.dotnet\dotnet.exe' build 'D:\YQDESK\MyIsland-src\Island.sln' -c Release --nologo
& 'D:\YQDESK\.dotnet\dotnet.exe' run --project 'D:\YQDESK\MyIsland-src\tests\Island.Tests\Island.Tests.csproj' -c Release --nologo
```
预期：0 警告 0 错误；测试全过，退出码 0。

- [ ] **Step 2: 确认 mac 侧零改动**

```powershell
git -C 'D:\YQDESK\MyIsland-src' diff --stat HEAD -- src/Island.Avalonia/
```
预期：**空输出**。

- [ ] **Step 3: 纯净克隆复验**

```powershell
$v = 'D:\YQDESK\.workbuddy\verify-phase1'
if (Test-Path $v) { Remove-Item -Recurse -Force -LiteralPath $v }
git clone --quiet --branch mainline/github-layout 'D:\YQDESK\MyIsland-src' $v
& 'D:\YQDESK\.dotnet\dotnet.exe' build (Join-Path $v 'Island.sln') -c Release --nologo
& 'D:\YQDESK\.dotnet\dotnet.exe' run --project (Join-Path $v 'tests\Island.Tests\Island.Tests.csproj') -c Release --nologo
```
预期：0 警告 0 错误，测试全过。

- [ ] **Step 4: 回填文档状态**

总纲 §5 路线表里阶段 1 的状态从「**规格已提交**」改成「**已实施（<commit>）**」；阶段 1 规格末尾追加验收结论（构建/测试条数/离屏渲染核对结果/CompactDensity 语义变更说明）。

- [ ] **Step 5: 提交**

```powershell
git -C 'D:\YQDESK\MyIsland-src' add docs/superpowers/specs/
git -C 'D:\YQDESK\MyIsland-src' commit -m "docs: 阶段 1 验收结论回填（收起态已与 mac 对齐）"
```

---

## 验收标准（对应规格 §8）

1. combo 收起态：时间 │ 占用环组 │ 温度磁盘环组 │ 网速，三处 Hairline 分隔线，环为 360° 闭环、环心字母、环外数值 13px、时间 13px、槽宽 21px。
2. dash 收起态：单行，组间分隔线，高度 44px，内容项与显示项定制语义不变。
3. `dotnet build Island.sln -c Release`：0 警告 0 错误。
4. `Island.Tests`：全过（含新增排版断言）。
5. mac 侧代码零改动。
