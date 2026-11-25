# Windows 收起态向 mac 对齐 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 Windows 版收起态胶囊改成 mac 版现有的样子：combo 重排为「时间│占用环组│温度磁盘环组│网速」四组并加组间分隔线、环改为 360° 闭环、字号/槽宽/间距对齐 mac 规格，dash 由两行网格改为单行。

**Architecture:** 新建纯计算的 `CompactSpec` 静态类承载全部排版常量与几何/宽度计算（不依赖 WPF，可被零依赖测试工程直接链接断言）；`RenderKit` 新增 360° 闭环 `Ring`（`Dial` 保留给展开面板）；`IslandCanvas` 的收起态排版器 `DrawCompactGroups` 升级为「视觉组 + 可选分隔线」；`DrawCombo`/`DrawDash` 按 mac 的分组与规格重写。

**Tech Stack:** C# / .NET 8 (net8.0-windows10.0.19041.0) / WPF（`System.Windows.Media` 直接绘制）/ 自研零依赖测试 harness（`Island.Tests`，`Check` 失败即抛、退出码非 0）/ `dotnet` 8.0.428 位于 `D:\YQDESK\.dotnet\dotnet.exe`

## Global Constraints

- **基准**：一切数值以 mac 版 `src/Island.Avalonia/IslandCanvas.cs` 现有实现为准；**mac 侧任何文件零改动**。
- **规格常量（不可改动，逐一对应 mac 版源行）**：`PadX=10`、`FigureFont=13`、`Dial=20`、`DialGap=5`、`GaugeGap=10`、`ClockGap=12`、`GroupGap=12`、`ValueMinW=21`、`NetGap=7`、`DividerHalfHeight=9`。
- **分隔线**：`pal.Hairline`，1px，透明度 0.8，上下各 9px，画在组间距正中（两侧各留 12px）。
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
  - 常量（全 `double`）：`PadX / FigureFont / Dial / DialGap / GaugeGap / ClockGap / GroupGap / ValueMinW / NetGap / DividerHalfHeight`
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
/// 源：src/Island.Avalonia/IslandCanvas.cs L554-L561（常量）、L596-L608（宽度公式）、
///     src/Island.Avalonia/RenderKit.cs L184-L214（Ring 几何）。
/// 改任何一个都要同步改 mac 侧，否则两端会漂移。
/// </summary>
public static class CompactSpecTests
{
    public static void Run(Action<string, bool> check)
    {
        // ---- 常量逐项锁死 ----
        check("规格：胶囊左右内边距 = 10", CompactSpec.PadX == 10);
        check("规格：数值字号 = 13", CompactSpec.FigureFont == 13);
        check("规格：环直径 = 20", CompactSpec.Dial == 20);
        check("规格：环→数值间距 = 5", CompactSpec.DialGap == 5);
        check("规格：同组内相邻环间距 = 10", CompactSpec.GaugeGap == 10);
        check("规格：时间→第一组间距 = 12", CompactSpec.ClockGap == 12);
        check("规格：组间距 = 12（分隔线居中）", CompactSpec.GroupGap == 12);
        check("规格：数值最小槽宽 = 21", CompactSpec.ValueMinW == 21);
        check("规格：↓值与↑之间 = 7", CompactSpec.NetGap == 7);
        check("规格：分隔线上下各 9px", CompactSpec.DividerHalfHeight == 9);

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

        // ---- 分隔线落在组间距正中 ----
        check("分隔线 x = 组尾 + 组间距/2", CompactSpec.DividerXAfter(100) == 106);
        check("分隔线 x 可用自定义组间距", CompactSpec.DividerXAfter(100, 16) == 108);

        // ---- dash 单行项宽 ----
        check("dash 单行项宽 = 标签 + 3 + 数值槽", CompactSpec.InlineItemWidth(28, 30) == 61);
        check("dash 单行项宽窄值按最小槽 21 兜底", CompactSpec.InlineItemWidth(28, 10) == 52);

        // ---- 360° 闭环几何 ----
        check("环半径 = 直径/2 - 线宽/2", CompactSpec.RingRadius(20, 2.8) == 8.6);
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
预期：编译失败，报 `CS0246: 未能找到类型或命名空间名 'CompactSpec'`。

- [ ] **Step 3: 实现 CompactSpec**

新建 `src/Island.App/Controls/CompactSpec.cs`：

```csharp
using System;

namespace Island.App.Controls;

/// <summary>
/// 收起态排版规格 —— 唯一来源。
///
/// 数值全部照抄 mac 版：
///   常量   src/Island.Avalonia/IslandCanvas.cs L554-L561
///   宽度   src/Island.Avalonia/IslandCanvas.cs L596-L608
///   环几何 src/Island.Avalonia/RenderKit.cs L184-L214
/// 目的：Windows 与 mac 两端收起态视觉一致。改这里必须同步改 mac 侧。
///
/// 本文件刻意只放「纯计算」（不碰 WPF 图形对象），Island.Tests 零依赖链接后可直接断言。
/// </summary>
public static class CompactSpec
{
    // ---------- 与 mac 版一一对应的常量 ----------
    /// <summary>胶囊左右内边距（mac ComboPadX）。</summary>
    public const double PadX = 10;
    /// <summary>数值字号（mac ComboFigureFont）。</summary>
    public const double FigureFont = 13;
    /// <summary>环直径（mac ComboDial）。</summary>
    public const double Dial = 20;
    /// <summary>环 → 数值间距（mac ComboDialGap）。</summary>
    public const double DialGap = 5;
    /// <summary>同组内相邻环间距（mac ComboGaugeGap）。</summary>
    public const double GaugeGap = 10;
    /// <summary>时间 → 第一组间距（mac ComboClockGap）。</summary>
    public const double ClockGap = 12;
    /// <summary>组与组之间（分隔线居中，两侧各留白）—— mac CompactGroupGap。</summary>
    public const double GroupGap = 12;
    /// <summary>数值最小槽宽（mac CompactValueMinW）—— 窄数值也占这么宽，避免抖动。</summary>
    public const double ValueMinW = 21;
    /// <summary>↓值 与 ↑ 之间（mac NetGap）。</summary>
    public const double NetGap = 7;
    /// <summary>分隔线上下各多少（mac Divider 里的 cy ± 9）。</summary>
    public const double DividerHalfHeight = 9;

    /// <summary>
    /// 一组环的总宽：每环 = 环径 + 环→数值间距 + max(数值宽, 最小槽宽)，
    /// 组内相邻环之间再插一个 GaugeGap，末环后面不插。
    /// 对应 mac ComboLayout.GaugeWidth。
    /// </summary>
    public static double GaugeGroupWidth(int count, Func<int, double> figureWidth)
    {
        if (count <= 0) return 0;
        double w = 0;
        for (int i = 0; i < count; i++)
            w += Dial + DialGap + Math.Max(figureWidth(i), ValueMinW) + GaugeGap;
        return w - GaugeGap;
    }

    /// <summary>combo 内容宽（不含胶囊左右内边距）。对应 mac ComboLayout.ContentW。</summary>
    public static double ComboContentWidth(double clockW, double gaugesW, double systemGaugesW,
                                           double netDownW, double netUpW)
        => clockW + ClockGap + gaugesW
         + (systemGaugesW > 0 ? GroupGap * 2 + systemGaugesW : 0)
         + GroupGap * 2 + netDownW + NetGap + netUpW;

    /// <summary>combo 胶囊总宽。对应 mac ComboLayout.CapsuleW。</summary>
    public static double ComboCapsuleWidth(double clockW, double gaugesW, double systemGaugesW,
                                           double netDownW, double netUpW)
        => ComboContentWidth(clockW, gaugesW, systemGaugesW, netDownW, netUpW) + PadX * 2;

    /// <summary>分隔线的 x：组尾再往前半个组间距（分隔线居中）。</summary>
    public static double DividerXAfter(double groupEndX, double gap = GroupGap) => groupEndX + gap / 2;

    /// <summary>dash 单行项宽：标签 + 间距 + 数值槽（窄数值按最小槽兜底）。</summary>
    public static double InlineItemWidth(double labelWidth, double valueWidth)
        => labelWidth + 3 + Math.Max(valueWidth, ValueMinW);

    /// <summary>360° 闭环半径（mac RenderKit.Ring：radius = diameter/2 - thickness/2）。</summary>
    public static double RingRadius(double diameter, double thickness)
        => Math.Max(0.5, diameter / 2 - thickness / 2);

    /// <summary>360° 闭环的扫角（占比 * 2π，从 12 点顺时针）。</summary>
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
预期：末尾输出 `全部通过：<N> 条`（N = 276 + 本任务新增 25 条 = 301），退出码 0。

- [ ] **Step 5: 提交**

```powershell
git -C 'D:\YQDESK\MyIsland-src' add src/Island.App/Controls/CompactSpec.cs tests/Island.Tests/CompactSpecTests.cs tests/Island.Tests/Island.Tests.csproj tests/Island.Tests/Program.cs
git -C 'D:\YQDESK\MyIsland-src' commit -m "feat: 抽出收起态排版规格 CompactSpec（数值照抄 mac 侧，纯计算可测）"
```

---

### Task 2: 新增 360° 闭环 RenderKit.Ring

**Files:**
- Modify: `src/Island.App/Controls/RenderKit.cs`（在 `Dial` 方法之后插入）

**Interfaces:**
- Consumes: `CompactSpec.RingRadius(double, double)`、`CompactSpec.RingSweep(double)`（Task 1）
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
        // 底环：整圈
        dc.DrawEllipse(null, PenOf(track, thickness, 0.9), center, radius, radius);
        double sweep = CompactSpec.RingSweep(share);
        if (sweep <= 0.004 * 2 * Math.PI) return;
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
- Modify: `src/Island.App/Controls/IslandCanvas.cs`（新增嵌套类型 `CompactGroup`；替换 `DrawCompactGroups`；改 4 个调用点）

**Interfaces:**
- Consumes: `CompactSpec.GroupGap / PadX / DividerHalfHeight / DividerXAfter`（Task 1）
- Produces:
  - `private sealed class IslandCanvas.CompactGroup`：`double Width { get; init; }`、`Action<double> Draw { get; init; }`、`bool DividerAfter { get; init; }`
  - `private double DrawCompactGroups(DrawingContext dc, Palette pal, Rect r, double cy, List<CompactGroup> groups, double gap = CompactSpec.GroupGap, double padding = CompactSpec.PadX, bool drawDividers = false)`

- [ ] **Step 1: 写失败的测试**

在 `tests/Island.Tests/CompactSpecTests.cs` 的 `Run` 方法末尾追加：

```csharp
        // ---- 分隔线落在组间距正中（渲染层用这个纯函数定位）----
        check("分隔线 x = 组尾 + 组间距/2", CompactSpec.DividerXAfter(200) == 206);
        check("分隔线 x 可用自定义组间距", CompactSpec.DividerXAfter(200, 16) == 208);
```

- [ ] **Step 2: 运行测试，确认失败**

```powershell
& 'D:\YQDESK\.dotnet\dotnet.exe' run --project 'D:\YQDESK\MyIsland-src\tests\Island.Tests\Island.Tests.csproj' -c Release --nologo
```
预期：`FAIL: 分隔线 x = 组尾 + 组间距/2`（`DividerXAfter` 还不存在 → 编译错误）。

- [ ] **Step 3: 加嵌套类型与新的 DrawCompactGroups**

在 `src/Island.App/Controls/IslandCanvas.cs` 的 `DrawCompactGroups` 方法之前插入：

```csharp
    /// <summary>
    /// 收起态的一个视觉组：组内可放多个元素（彼此紧挨），组与组之间留 GroupGap 并画一条 Hairline 竖线。
    /// 对应 mac 版 DrawCombo 的 DrawGaugeGroup + Gap()。
    /// </summary>
    private sealed class CompactGroup
    {
        /// <summary>本组占用宽度（组内所有元素 + 组内间距）。</summary>
        public double Width { get; init; }
        /// <summary>绘制本组；参数是本组左边缘 x。</summary>
        public Action<double> Draw { get; init; } = _ => { };
        /// <summary>本组之后是否画分隔线（mac 的 Gap()）。最后一个可见组不画（其后是 +N 或空白）。</summary>
        public bool DividerAfter { get; init; }
    }
```

把 `DrawCompactGroups` 整个方法替换为：

```csharp
    /// <summary>
    /// 收起态分组排版：组间留白 gap，可选在组间距正中画 Hairline 竖线（mac 的 Gap()）。
    /// 宽度不够时截断尾部并显示 "+N"；被截断时最后一个可见组**不**画分隔线。
    /// </summary>
    private double DrawCompactGroups(DrawingContext dc, Palette pal, Rect r, double cy,
        List<CompactGroup> groups, double gap = CompactSpec.GroupGap, double padding = CompactSpec.PadX,
        bool drawDividers = false)
    {
        double available = Math.Max(0, CompactWidthBudget - padding * 2);
        double used = 0;
        int shown = 0;
        for (int i = 0; i < groups.Count; i++)
        {
            double next = used + (shown > 0 ? gap : 0) + groups[i].Width;
            int remaining = groups.Count - i - 1;
            double suffix = remaining > 0 ? gap + Text($"+{remaining}", 11, pal.TextDim, ppd: Ppd).Width : 0;
            if (next + suffix > available) break;
            used = next;
            shown++;
        }
        double x = r.X + padding;
        for (int i = 0; i < shown; i++)
        {
            double groupStart = x;
            groups[i].Draw(groupStart);
            double groupEnd = groupStart + groups[i].Width;
            x = groupEnd + gap;
            // 组间分隔线：竖线落在组间距正中；最后一个可见组后面不画（其后是 +N 或空白）
            if (drawDividers && groups[i].DividerAfter && i < shown - 1)
            {
                double lineX = CompactSpec.DividerXAfter(groupEnd, gap);
                dc.DrawLine(RenderKit.PenOf(pal.Hairline, 1, 0.8),
                    new Point(lineX, cy - CompactSpec.DividerHalfHeight),
                    new Point(lineX, cy + CompactSpec.DividerHalfHeight));
            }
        }
        if (shown < groups.Count)
        {
            var more = Text($"+{groups.Count - shown}", 11, pal.TextDim, ppd: Ppd);
            dc.DrawText(more, new Point(x, cy - more.Height / 2));
            used += (shown > 0 ? gap : 0) + more.Width;
        }
        return Math.Min(CompactWidthBudget, Math.Max(80, padding * 2 + used));
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

- [ ] **Step 5: 构建并跑测试，确认全绿**

```powershell
& 'D:\YQDESK\.dotnet\dotnet.exe' build 'D:\YQDESK\MyIsland-src\Island.sln' -c Release --nologo
& 'D:\YQDESK\.dotnet\dotnet.exe' run --project 'D:\YQDESK\MyIsland-src\tests\Island.Tests\Island.Tests.csproj' -c Release --nologo
```
预期：构建 0 警告 0 错误；测试 `全部通过：303 条`（301 + 本任务新增 2 条），退出码 0。

- [ ] **Step 6: 提交**

```powershell
git -C 'D:\YQDESK\MyIsland-src' add src/Island.App/Controls/IslandCanvas.cs tests/Island.Tests/CompactSpecTests.cs
git -C 'D:\YQDESK\MyIsland-src' commit -m "refactor: 收起态排版器升级为视觉组 + 可选组间分隔线（对齐 mac 的 Gap/Divider）"
```

---

### Task 4: DrawCombo 按 mac 重排为四组

**Files:**
- Modify: `src/Island.App/Controls/IslandCanvas.cs`（`DrawCombo`）
- Modify: `src/Island.App/Controls/IslandWindow.xaml.cs`（同步 `ComboNumericHero`）

**Interfaces:**
- Consumes: `CompactSpec`（Task 1）、`RenderKit.Ring`（Task 2）、`CompactGroup` / `DrawCompactGroups`（Task 3）
- Produces: `private double DrawCombo(DrawingContext dc, Palette pal, Rect r, double cy, MonitorSnapshot? snap, Services.ThermalSnapshot? thermal)`（签名不变，行为改为四组）；`public bool ComboNumericHero { get; set; }`（IslandCanvas 新属性）

- [ ] **Step 1: 写失败的测试**

在 `tests/Island.Tests/CompactSpecTests.cs` 的 `Run` 方法末尾追加：

```csharp
        // ---- combo 的组结构：时间│占用组│温度磁盘组│网速（对应 mac LayoutCombo）----
        check("combo 占用组 = 3 环（C/M/G）总宽 185", CompactSpec.GaugeGroupWidth(3, _ => 30) == 185);
        check("combo 系统组 = 3 环（C/G/D）总宽 185", CompactSpec.GaugeGroupWidth(3, _ => 30) == 185);
        // 四组之间有 3 处分隔线 → 内容宽里含 3 段 GroupGap*2
        double content = CompactSpec.ComboContentWidth(40, 185, 185, 60, 60);
        check("combo 内容宽含 3 段组间距",
            content == 40 + 12 + 185 + (12 * 2 + 185) + 12 * 2 + 60 + 7 + 60);
```

- [ ] **Step 2: 运行测试，确认失败**

```powershell
& 'D:\YQDESK\.dotnet\dotnet.exe' run --project 'D:\YQDESK\MyIsland-src\tests\Island.Tests\Island.Tests.csproj' -c Release --nologo
```
预期：断言 `FAIL`（当前 combo 的组间距段数不是 3 段）。

- [ ] **Step 3: 重写 DrawCombo**

把 `src/Island.App/Controls/IslandCanvas.cs` 的 `DrawCombo` 整体替换为：

```csharp
    /// <summary>
    /// 信息胶囊（combo）：时间 │ 占用环组(C/M/G) │ 温度磁盘环组(C/G/D) │ 网速，组间画 Hairline 分隔线。
    /// 对应 mac 版 LayoutCombo + DrawCombo（src/Island.Avalonia/IslandCanvas.cs L612-L707）。
    /// 规格：环 20px 闭环（RenderKit.Ring），环心字母，数值 13px 等宽放环外，槽宽 21px，组间距 12px。
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
        void AddGaugeGroup(List<(string Letter, double Share, string Text, Color ArcColor, Color TextColor)> items,
                          bool dividerAfter)
        {
            if (items.Count == 0) return;
            var figures = items
                .Select(i => (double)RenderKit.NumWidth(i.Text, CompactSpec.FigureFont, true, ppd)).ToArray();
            double groupW = CompactSpec.GaugeGroupWidth(items.Count, i => figures[i]);
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
                        gx += Math.Max(figures[i], CompactSpec.ValueMinW) + CompactSpec.GaugeGap;
                    }
                },
                DividerAfter = dividerAfter,
            });
        }

        // ---- 组 1：时间 ----
        // mac 的 ComboStyle.ClockStrong = !_comboNumericHero：数字主角时时钟降为次级文字。
        if (WantCombo("clock"))
        {
            var clock = Text(DateTime.Now.ToString("HH:mm"), CompactSpec.FigureFont,
                ComboNumericHero ? pal.TextDim : pal.Text, true, ppd);
            groups.Add(new CompactGroup
            {
                Width = clock.WidthIncludingTrailingWhitespace,
                Draw = x => Baseline(dc, clock, x, Math.Round(cy + CompactSpec.FigureFont * 0.36)),
                DividerAfter = true,
            });
        }

        // ---- 组 2：占用环组（C/M/G）----
        var dials = new List<(string Letter, double Share, string Text, Color ArcColor, Color TextColor)>();
        if (WantCombo("dials") && snap is not null)
        {
            if (snap.CpuUsage > 0)
            {
                var c = LevelColor(snap.CpuUsage, pal.Accent, pal);
                dials.Add(("C", snap.CpuUsage / 100.0, $"{Math.Round(snap.CpuUsage)}%", c,
                    LevelColor(snap.CpuUsage, pal.Text, pal)));
            }
            if (snap.MemoryUsagePercent > 0)
            {
                var m = LevelColor(snap.MemoryUsagePercent, neutral, pal);
                dials.Add(("M", snap.MemoryUsagePercent / 100.0, $"{Math.Round(snap.MemoryUsagePercent)}%", m,
                    LevelColor(snap.MemoryUsagePercent, pal.Text, pal)));
            }
            if (snap.GpuUsage > 0)
            {
                var g = LevelColor(snap.GpuUsage, neutral, pal);
                dials.Add(("G", snap.GpuUsage / 100.0, $"{Math.Round(snap.GpuUsage)}%", g,
                    LevelColor(snap.GpuUsage, pal.Text, pal)));
            }
        }
        AddGaugeGroup(dials, dividerAfter: true);

        // ---- 组 3：温度 / 磁盘环组（C/G/D）----
        var sys = new List<(string Letter, double Share, string Text, Color ArcColor, Color TextColor)>();
        if (snap is not null)
        {
            double? ct = thermal?.CpuTemp, gt = thermal?.GpuTemp;
            if (WantCombo("cputemp"))
            {
                var c = TempColor(ct, pal);
                sys.Add(("C", ct is { } t ? Math.Clamp(t / 125.0, 0, 1) : 0,
                    ct is { } tv ? $"{Math.Round(tv)}°" : "--", c, c));
            }
            if (WantCombo("gputemp"))
            {
                var g = TempColor(gt, pal);
                sys.Add(("G", gt is { } t2 ? Math.Clamp(t2 / 125.0, 0, 1) : 0,
                    gt is { } tv2 ? $"{Math.Round(tv2)}°" : "--", g, g));
            }
            if (WantCombo("disk"))
            {
                var d = LevelColor(snap.DiskUsagePercent, neutral, pal);
                sys.Add(("D", snap.DiskUsagePercent / 100.0,
                    $"{Math.Round(snap.DiskUsagePercent)}%", d,
                    LevelColor(snap.DiskUsagePercent, pal.Text, pal)));
            }
        }
        AddGaugeGroup(sys, dividerAfter: true);

        // ---- 组 4：网速（↓值 ↑值，中性色）----
        if (WantCombo("net") && snap is not null)
        {
            groups.Add(new CompactGroup
            {
                Width = NetWidth(CompactSpec.FigureFont, snap, ppd),
                Draw = x => DrawNet(dc, pal, x, Math.Round(cy + CompactSpec.FigureFont * 0.36),
                    CompactSpec.FigureFont, snap, ppd),
            });
        }

        return DrawCompactGroups(dc, pal, r, cy, groups, drawDividers: true);
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

- [ ] **Step 5: 构建并跑测试**

```powershell
& 'D:\YQDESK\.dotnet\dotnet.exe' build 'D:\YQDESK\MyIsland-src\Island.sln' -c Release --nologo
& 'D:\YQDESK\.dotnet\dotnet.exe' run --project 'D:\YQDESK\MyIsland-src\tests\Island.Tests\Island.Tests.csproj' -c Release --nologo
```
预期：0 警告 0 错误；`全部通过：306 条`，退出码 0。

- [ ] **Step 6: 离屏渲染核对 combo**

用真实控件离屏渲染出 combo 收起态（沿用 `ui-preview-2026-11-24/` 的做法），确认：时间在最左、三条分隔线、C/M/G 三个闭环字母环、C/G/D 三个闭环字母环、网速在最右。
产物放 `D:\YQDESK\ui-preview-2026-11-25\`，**不要提交进仓库**。

- [ ] **Step 7: 提交**

```powershell
git -C 'D:\YQDESK\MyIsland-src' add src/Island.App/Controls/IslandCanvas.cs src/Island.App/Controls/IslandWindow.xaml.cs tests/Island.Tests/CompactSpecTests.cs
git -C 'D:\YQDESK\MyIsland-src' commit -m "feat: combo 重排为 mac 的四组（时间│占用组│温度磁盘组│网速）+ 360° 闭环 + 13px 规格 + 组间分隔线"
```

---

### Task 5: dash 改单行 + 高度 69 → 44

**Files:**
- Modify: `src/Island.App/Controls/IslandCanvas.cs`（`DrawDash` 整体重写）
- Modify: `src/Island.App/Controls/IslandWindow.xaml.cs`（收起态尺寸表）

**Interfaces:**
- Consumes: `CompactSpec`（Task 1）、`RenderKit.Ring`（Task 2）、`CompactGroup` / `DrawCompactGroups`（Task 3）
- Produces: `DrawDash` 单行版；尺寸表 `"dash" => (560, 44)`

- [ ] **Step 1: 写失败的测试**

在 `tests/Island.Tests/CompactSpecTests.cs` 的 `Run` 方法末尾追加：

```csharp
        // ---- dash 单行：标签与数值同行（不再是标签在上、数值在下的两行网格）----
        check("dash 单行项宽 = 标签 + 3 + 数值槽", CompactSpec.InlineItemWidth(28, 30) == 61);
        check("dash 单行项宽窄值按最小槽 21 兜底", CompactSpec.InlineItemWidth(28, 10) == 52);
```

- [ ] **Step 2: 运行测试，确认失败**

```powershell
& 'D:\YQDESK\.dotnet\dotnet.exe' run --project 'D:\YQDESK\MyIsland-src\tests\Island.Tests\Island.Tests.csproj' -c Release --nologo
```
预期：编译失败（`CompactSpec.InlineItemWidth` 不存在）。

- [ ] **Step 3: 给 CompactSpec 加 InlineItemWidth**

在 `src/Island.App/Controls/CompactSpec.cs` 的 `DividerXAfter` 之前插入：

```csharp
    /// <summary>dash 单行项宽：标签 + 间距 + 数值槽（窄数值按最小槽兜底）。</summary>
    public static double InlineItemWidth(double labelWidth, double valueWidth)
        => labelWidth + 3 + Math.Max(valueWidth, ValueMinW);
```

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
            x += icon.Width + 3;
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

        // ---- 组 5：网速 ----
        if (Want("net"))
        {
            AddItemGroup(new List<(string, string, string, Color?)>
            {
                ("↓", MonitorSnapshot.FormatSpeedFixed(snap.DownloadSpeed), "  0.0B", null),
                ("↑", MonitorSnapshot.FormatSpeedFixed(snap.UploadSpeed), "  0.0B", null),
            }, dividerAfter: false);
        }

        return DrawCompactGroups(dc, pal, r, cy, groups, drawDividers: true);
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
预期：0 警告 0 错误；`全部通过：308 条`，退出码 0。

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
