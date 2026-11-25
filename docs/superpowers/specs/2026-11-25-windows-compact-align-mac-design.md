# Windows 收起态向 mac 版对齐 —— 设计规格

日期：2026-11-25
状态：待实施
范围：Windows 版（`src/Island.App/`）收起态胶囊的视觉与排版；**mac 侧零改动**

> 本文是「双平台一致性」程序的**阶段 1**，总纲见
> [`2026-11-25-cross-platform-consistency-design.md`](2026-11-25-cross-platform-consistency-design.md)。
> 总纲确立的三条判定标准（视觉规格 / 页面结构 / 同一功能行为）在本阶段的适用方式：
> - 阶段 1 只负责**收起态胶囊**这一块的视觉规格与页面结构；
> - 「同一功能行为一致」中与收起态相关的部分（视图集 §4.1、dash 形态）在本阶段处理；
> - 平台专属能力按总纲 §1 规则处理（本阶段不涉及）。

---

## 1. 背景

Windows 版（`Island.App`，WPF）与 macOS 版（`Island.Avalonia`，Avalonia）是同一产品的两套独立实现，
共用 `Island.Core` 的数据模型与 `Settings` 配置，但**收起态胶囊的画法已经漂移**：

| | mac（基准） | Windows（现状） |
|---|---|---|
| 组间分隔线 | Hairline 竖线，组尾 +18px 处画线（组间总带 24px） | 无（只有 16px 空白） |
| combo 分组 | 4 组：时间 │ 占用环组 │ 温度磁盘环组 │ 网速 | 每个环各自独立成组（8 组） |
| 环的形状 | `Ring` —— **360° 完整圆环**，环心字母，环外数字 | `Dial` —— **270° 开口环**，环内字母 |
| 时间字号 | 13px | 16px |
| 数值字号 | 13px | 12.5px |
| dash 视图 | 单行，组间分隔线 | 两行网格，69px 高 |

目标：**Windows 收起态以 mac 现有实现为唯一基准**，让两端视觉一致。
凡两端有出入之处，改 Windows，**不动 mac**。

---

## 2. 基准（mac 现状的精确规格）

来源：`src/Island.Avalonia/IslandCanvas.cs`

### 2.1 布局常量（L554-L561）

| 常量 | 值 | 含义 |
|---|---|---|
| `ComboPadX` | 10 | 胶囊左右内边距 |
| `ComboFigureFont` | 13 | 数值字号 |
| `ComboDial` | 20 | 环直径 |
| `ComboDialGap` | 5 | 环 → 数值间距 |
| `ComboGaugeGap` | 10 | 同组内相邻环间距 |
| `ComboClockGap` | 12 | 时间 → 第一组间距 |
| `CompactGroupGap` | 12 | 组间**单侧**间距（组间总带 = 2 × 12 = 24px） |
| `CompactValueMinW` | 21 | 数值最小槽宽 |
| `NetGap` | 7 | combo 里 ↓值 与 ↑ 之间间距 |
| `DashPadX`（L1843） | 14 | dash 胶囊左右内边距（combo 是 10） |
| `DashLayout.NetGap`（L1866） | 10 | dash 里 ↓值 与 ↑ 之间间距（combo 是 7） |
| `FontValue` | 13 | 收起态主读数字号（时间/网速数值同级） |

### 2.2 分隔线

```csharp
dc.DrawLine(RenderKit.PenOf(pal.Hairline, 1, 0.8),
            new Point(x + CompactGroupGap / 2, cy - 9),
            new Point(x + CompactGroupGap / 2, cy + 9));
// Gap(): x += CompactGroupGap; Divider(); x += CompactGroupGap;
```

- 颜色：`pal.Hairline`，1px，透明度 0.8
- 长度：上下各 9px（总 18px）
- 位置：**组尾 + 18px** 处（= 组尾 + 1.5 × CompactGroupGap）；下一组从**组尾 + 24px** 开始。组间总带 24px = 2 × CompactGroupGap —— 线不在总带正中，而是偏后 6px。
  （mac 的 `Gap()` 是「先 `x += CompactGroupGap`，再在当时的 `x + CompactGroupGap / 2` 处画线」，所以线在推进之后。）

### 2.3 环

```csharp
RenderKit.Ring(dc, new Point(x + L.Dial / 2, cy), L.Dial,
               Math.Max(1.8, L.Dial * 0.14),   // = 2.8
               g.Usage ?? 0, pal.Track, g.Arc);
DrawGaugeCenter(dc, pal, g.Icon, g.Res, x, cy, L.Dial, g.Arc);
```

- `Ring` 是 **360° 完整圆环**：底环（`pal.Track`）+ 从 12 点顺时针的占比弧（`Arc` 色）
- 环心：`DecorLetters` 开 → 画资源字母 C/M/G/D/B/N；关 → 画矢量图标
- 环 → 数值间距 5px；数值占位 `max(数值宽, 21)`
- 弧的占比死区：`f > 0.004` 才画弧（`RenderKit.cs` L191），低于它只剩底环

### 2.4 combo 分组顺序

```
时间(13px) │ 占用环组(C/M/G) │ 温度磁盘环组(C/G/D) │ 网速(↓值 ↑值)
```

分隔线只画在**两处**：占用组│温度磁盘组、温度磁盘组│网速。
时间│占用组之间**没有线**，只有 `ClockGap=12` 的留白（mac 的 `Gap()` 只调用两次：
`IslandCanvas.cs:694` 与 `:696`；时间后面是 `x += L.ClockW + L.ClockGap`，见 `:677`）。
两处分隔线的总带都是 24px（= 2 × `CompactGroupGap`），线画在「组尾 + 18px」，偏后 6px、不在正中。
网速：箭头与数值间距 3，↓值 与 ↑ 之间 7，数值用 `FormatSpeedFixed`（定宽 6 字符）、中性色。

### 2.5 dash 分组顺序（mac）

单行，组间分隔线（dash 的胶囊左右内边距 14、↓值与↑之间 10，与 combo 的 10/7 不同）：

```
频谱柱 │ 剪贴板计数 │ 温度组 │ 占用组(环+字母+数值) │ 电池 │ 设备电量环 │ 网速
```

---

## 3. 对齐清单（Windows 侧逐项改动）

| # | 项 | mac 基准 | Windows 现状 | 动作 |
|---|---|---|---|---|
| 1 | 组间分隔线 | Hairline 1px，cy±9，组尾 +18px（组间总带 24px） | 无 | `DrawCompactGroups` 增加可选分隔线能力 |
| 2 | combo 分组 | 4 组 | 8 组（每环独立） | 重排为 4 组，组内环距 10px |
| 3 | 时间字号 | 13px | 16px | → 13px |
| 4 | 数值字号 | 13px | 12.5px | → 13px |
| 5 | 数值最小槽 | `max(宽, 21)` | `NumWidth(sample, 12.5)` | → 21px 槽 |
| 6 | 组间距 | 单侧 12px（组间总带 24px，线在组尾 +18px） | 16px 空白 | → 单侧 12px |
| 7 | 组内环距 | 10px | 16px（因每环独立成组） | → 10px |
| 8 | 环的形状 | 360° 闭环 `Ring` | 270° 开口环 `Dial` | **新增 `Ring`**，收起态改用；`Dial` 保留给展开面板 |
| 9 | 环心 | 字母 C/M/G/D/B/N 或矢量图标 | 字母 | 沿用 Windows 现有字母集（`Glyphs`）；不引入 mac 的 B/N 扩展 |
| 10 | 胶囊左右内边距 | combo 10px（dash 14px） | 14px | combo → 10px；dash → 14px（与 mac 一致） |
| 11 | 网速 | `FormatSpeedFixed` 定宽 6 字符；combo ↓↔↑ 间距 7、dash 10 | `DrawNet`（↓↔↑ 硬编码 10） | 对齐为定宽 + 中性色；间距改用 `CompactSpec.NetGap` / `DashNetGap` |
| 12 | dash 布局 | 单行 + 分隔线 | 两行网格，69px | 改单行，组间分隔线；胶囊高度 69 → 44px |

### 3.1 关于第 8 条（关键决策）

Windows 现有 `RenderKit.Dial` 是 **270° 开口环**（`DialStart = 1.25π`，`DialSweep = 1.5π`），
它同时被**展开面板的 CPU 主读数环**使用（BrimDeck 风格的大开口环）。
mac 侧 `Dial` 与 `Ring` **并存**：注释明确写着「和 `Dial`（270° 仪表 + 字母）不同，这个是圆环 + 环外放数字」。

因此：**新增 `RenderKit.Ring`（360° 闭环），只给收起态用；`Dial` 一行不改**，展开面板不受影响。
这是两端已有的同款做法，不是新发明。

### 3.2 关于第 12 条（dash 改单行）

对齐的是**形态**（单行、组间分隔线、环/数值规格），**不是内容清单**：

- mac 的 dash 内容是固定结构；
- Windows 的 dash 有**用户定制能力**（`Want("cores"/"cputemp"/"mem"/"gpu"/"disk"/"ssd"/"net")` + `CompactDensity` 密度分级）。

因此 Windows dash 改为单行后：

- 保留全部可定制项与密度分级语义；
- 每项的「标签在上、数值在下」两行排布改为**单行内联**：标签 9.5px 三级文字 + 数值 13px，间距 3px；
- 组划分：`核心柱群 │ 温度项组 │ 占用项组 │ 网速`（**电池读数不在 dash 里** —— 「设备」收起视图已经有一份，dash 不重复）；
- 胶囊高度 69px → 44px。

**`CompactDensity` 密度的含义随之调整**（这是本规格唯一一处语义变更）：
旧版它只控制「组间距」（Minimal 16 / 其余 26），而组间距已被第 6 条统一为单侧 12px（组间总带 24px）+ 分隔线，
若仍只管间距，密度分级就没有存在意义了。改为**决定显示哪些组**，阈值（<390 / <560）不变：

| 密度 | 显示的组 |
|---|---|
| Minimal（宽 < 390） | 核心柱 + 温度组 + 网速 |
| 其余（宽 ≥ 390） | 上述 + 占用组 |

这与 mac dash「按可用宽度决定显示什么」的精神一致。
（Full 档原本只比 Balanced 多一个「电池组」；电池读数移到设备视图后，Full 与 Balanced 的显示内容相同。）

注：这是**形态对齐**（单行 + 组间分隔线 + 环与数值的规格），不是逐项照抄 mac 的内容清单。
mac 的 dash 把「电池」和「设备电量环」分成两组，Windows 的电池读数放在**设备视图**里 ——
两边不必逐项对应，只要读数都能在自己的视图里看到即可。
Windows 的可定制项多于 mac，因此 dash 的项数与 mac 不同是预期结果。

---

## 4. 实现方案

采用**方案 A：单侧对齐**。只改 Windows 侧，mac 侧零改动。

### 4.1 改动点清单

| 文件 | 改动 |
|---|---|
| `src/Island.App/Controls/RenderKit.cs` | **新增** `Ring(...)`（360° 闭环，签名与语义对齐 mac `RenderKit.Ring`）；`Dial` 不动 |
| `src/Island.App/Controls/IslandCanvas.cs` | `DrawCompactGroups` 增加分隔线能力与「组」概念（一个 group 可含多个元素）；`DrawCombo` 重排为 4 组；`DrawDash` 改单行；新增规格常量块（照抄 mac 的常量表并注明来源行号） |
| `src/Island.App/Controls/IslandWindow.xaml.cs` | 收起态尺寸表：`"dash" => (560, 69)` → `(620, 44)`；`CollapsedSize` 与画布实际测量宽度对齐；宿主窗口宽**恒定 640 DIP**（`ExpandW 560 + margin 40×2`），胶囊不得超出窗口（超出部分被系统裁掉）⇒ dash 上限取 **620**（留 20px 安全余量；4 组实测 580.6 / 613.6 DIP） |
| `tests/Island.Tests/` | 新增收起态排版断言测试（规格值锁定） |
| `docs/superpowers/specs/2026-11-25-windows-compact-align-mac-design.md` | 本文件 |

### 4.2 为什么不抽共享内核（方案 B）

抽到 `Island.Core` 能一劳永逸防漂移，但要动**已发布**的 mac 侧代码并重新验证 mac 构建。
本次先把 mac 的规格以**常量块 + 注释引用（标明 mac 源文件与行号）+ 断言测试**固化在 Windows 侧，
等规格稳定后再评估是否升级成方案 B。

---

## 5. 风险与处理

### 5.1 dash 高度变化（69 → 44）

宿主 `IslandWindow.xaml.cs` 的 `CollapsedSize()` 尺寸表与画布实测宽度必须同步，
否则宿主按旧尺寸摆放、画布按新尺寸绘制，出现裁剪或错位。
处理：改尺寸表后，用离屏渲染在 dash 视图下核对胶囊实测高度与宿主尺寸一致。

### 5.2 变形帧（morph）重排

胶囊宽度变化时内容「从中间揭开」（`IslandCanvas.FinalBodyRect` 提供最终矩形，逐帧裁剪）。
dash 从两行变单行后，元素排布规则改变，宽度过渡期间可能出现错位或元素抖动。
处理：dash 单行化后重跑变形帧检查（截取多个中间宽度 110/140/170/200/230/260/300/345/420 px 的渲染帧，
确认内容始终居中、不叠字、不溢出）。

### 5.3 `+N` 截断与分隔线的交互

宽度不足时 Windows 会省略尾部元素并显示 `+N`（mac 无此机制）。
规则：**最后一个可见组与 `+N` 之间不画分隔线**；`+N` 自身不参与分隔线计算。
处理：在 `DrawCompactGroups` 里把分隔线绑定在「组索引」而非「绝对坐标」，
并对被截断的组做标记。

### 5.4 环形状变更的影响面

新增 `Ring` 而不改 `Dial`，展开面板的 CPU 主读数环不受影响。
收起态由开口环变闭环，会改变现有收起态的视觉；这是本次的目标（对齐 mac），
但属于**可见的产品外观变化**，需在验收时确认。

---

## 6. 验证方式

1. **离屏渲染对照**：渲染 Windows 的 combo / dash 收起态，与 mac 截图并排比对
   （组数、元素顺序、分隔线位置与长度、环形状、字号、槽宽）。
2. **排版断言测试**：在 `Island.Tests` 写规格值断言，锁住第 2 节常量表与分组规则
   （组数、组内元素顺序、各元素 x 坐标、分隔线 x、槽宽 21、组间距 12）。
3. **变形帧检查**：dash 单行化后，在多个中间宽度下渲染，确认无错位/叠字/溢出。
4. **门禁**：`dotnet build Island.sln -c Release` 0 警告 0 错误；`Island.Tests` 全过（现有 276 条 + 新增）。

---

## 7. 不在本次范围

- 展开面板（Windows 的 XAML 面板与 mac 的自绘面板）
- mac 侧任何代码改动
- Windows 胶囊形态（灵动岛圆角块 vs mac 菜单栏扁条 —— 平台差异，不是设计不一致）
- Windows 独有读数：小形象、宠物图层、设备电量环、剪贴板计数、核心柱群
- mac 的 `DecorLetters` 扩展字母（B/N）与 mac 的 `ClipDetailWindow` / `ImagePreviewWindow`
- 时钟显隐策略：沿用 Windows 现有的 `WantCombo("clock")` 显示项开关（mac 侧恒显时钟）。
  这是「用户可配置能力」的差异，不是视觉不一致，故不改。

---

## 8. 验收标准

1. Windows combo 收起态：时间 │ 占用环组 │ 温度磁盘环组 │ 网速，**两处 Hairline 分隔线**（时间与占用环组之间无线，只有 12px 留白），
   环为 360° 闭环、环心字母、环外数值 13px、时间 13px、槽宽 21px。
2. Windows dash 收起态：单行（核心柱群 │ 温度项组 │ 占用项组 │ 网速），组间分隔线，高度 44px，
   可定制项与密度分级语义不变；**电池读数移到「设备」视图**（dash 不重复）。
   收起态胶囊宽必须 ≤ 宿主恒定窗口宽 640 DIP（`ExpandW 560 + margin 40×2`），超出 HWND 会被系统裁掉；
   dash 的设计上限取 **620**（留 20px 安全余量；4 组实测 580.6 / 613.6 DIP ⇒ 4 组全显示、网速可见）。
3. `dotnet build Island.sln -c Release`：0 警告 0 错误。
4. `Island.Tests`：全过（含新增排版断言）。
5. mac 侧代码零改动（`git diff` 中 mac 相关文件为空）。
