# MyIsland 双平台一致性总纲

日期：2026-11-25
状态：程序级设计（各阶段另有独立规格）
范围：Windows 版（`src/Island.App/`）与 macOS 版（`src/Island.Avalonia/`）

---

## 1. 目标与判定标准

「两个平台版本一致」= 三件事同时成立：

1. **视觉规格一致** —— 同一元素在两端的字号、环径、间距、分隔线、配色口径相同。
2. **页面结构一致** —— 收起视图集、展开页签集、页内分区顺序相同。
3. **同一功能行为一致** —— 同名功能在两端的输入、输出、边界行为相同。

**平台专属能力不互相搬运**（Windows 的 PawnIO 自举 / Intel GCL / 签名安装包，macOS 的 DDC 显示器控制 / 触感反馈），
但两端的呈现规则统一，且差异在能力清单里公开。

**呈现规则（沿用 mac 已有先例）**：平台不支持的能力，其页签或项**直接不出现**，不置灰、不留空位。
先例：`src/Island.Avalonia/IslandCanvas.cs` L733-734 用 `if (OperatingSystem.IsMacOS())` 隐藏「屏幕」「设备」页签。

**基准方向**：凡两端有出入，**以 mac 版现有实现为准**。

---

## 2. 已经一致的部分（不需要动）

两端通过 **link 同一批源文件**，以下已经是同一份代码，不存在漂移：

| 共享层 | 文件 |
|---|---|
| `Island.Core` | `MonitorModels.cs`、`Reading.cs`、`SamplerDemand.cs` |
| `Island.Native` | `SystemMonitor.cs`、`PdhWildcardQuery.cs`、`NativeMethods.cs`、`GpuUsageReader.cs` |
| `Island.App`（供 mac link） | `Spring.cs`、`ThermalService.cs`、`ThermalSnapshot.cs`、`PawnIoInstaller.cs`、`IntelComputePolicy.cs`、`InpOutSmnService.cs`、`SmnChannel.cs`、`WeatherService.cs`、`Settings.cs` |

即：**数据模型、采样器、设置项、温度链路、天气** 已是同一份代码。
漂移只发生在**渲染层**（`IslandCanvas` / `IslandWindow` / `RenderKit` / `Themes`）与**平台功能层**。
⚠️ 补充事实（2026-11-26 核对）：**渲染层不是同一份代码** —— mac 自带 `src/Island.Avalonia/RenderKit.cs`（18 KB），Windows 是 `src/Island.App/Controls/RenderKit.cs`（38 KB），两边各写各的。
所以阶段 2 的对齐策略是「**以 mac 的数值为准**」（像阶段 1 那样把 mac 的常量抄进规格类、由 Windows 侧断言符合），而不是把两套渲染层合并成一份 —— 后者是重构，不在一致性对齐的范围内。

---

## 3. 能力矩阵（2026-11-25 实测）

**§3 的矩阵由能力清单驱动，见 [`docs/capabilities.md`](../../capabilities.md)。**

- 收起视图 / 展开页签 / 数据源 / 小形象款式：**逐条内容**（键、显示名、两端可用性、差异说明）以
  `src/Island.Core/Capabilities/CapabilityManifest.cs`（**唯一真相源**）为准；人读版本 + 两端已知差异的公开说明见
  [`docs/capabilities.md`](../../capabilities.md)。
- 本节原先手写的矩阵**已移除**：凡是能被代码或实测回答的能力，一律进能力清单，
  由代码 + 门禁（`CapabilityManifestTests`）守住，文档只做转述。

> ⚠️ **易被推翻的记录不要再手写**：2026-11-26 这轮实测推翻了本节原有的三条记录
> （「收起视图两端一致」「12 个同名主题 token 同源」「小形象是同一份实现」）。
> 这类「看着像事实」的记录一旦写进文档就会一直误导下去 —— 能力差异只能由能力清单承载。

清单未覆盖的平台专属能力（全局热键、剪贴板详情窗、图片预览窗、自动跟随窗口、触感反馈、内存回收、
手柄档位 XInput、签名安装包 / 开机自启 / 文件托盘等）不在本矩阵内，
以代码与各阶段规格 / 计划的实测记录为准。

---

## 4. 已发现的行为不一致（本次要收口）

### 4.1 收起视图集：Windows 多 4 个（2026-11-26 实测修正）

> 原文只记了 `window` 一个差异，**低估了差距**。实测：
> - mac 收起态只有 **4** 个视图：`dash / clock / pet / combo`（`IslandWindow.cs:1098`），
>   且 `IslandCanvas` 对其余键一律 fallback 到 `DrawDash`；
> - Windows 收起态有 **8** 个：`clock / dash / pet / combo / monitor / devices / clipboard / window`
>   （`CompactLayout.cs:31-32`，滚轮唯一数据源 `IslandWindow.xaml.cs:1524-1527`）；
> - 顺带发现 Windows **设置下拉的选项顺序**与**滚轮顺序**也不一致（设置里是 dash, clock, combo, monitor, devices, clipboard, pet, window；滚轮是 clock, dash, …）——属于 Windows 内部两处列表漂移。

**`window` 视图的判定（2026-11-26 定）**：**保留在 Windows，明确标注为平台专属，不搬 mac**。
依据 §1：它依赖 user32 前台窗口 API（`Island.Native/ScreenContext.cs` 的 `ForegroundDetector`），
mac 需要 AppKit/NSWorkspace 另一套实现，属典型平台专属能力；§1 的呈现规则要求「平台不支持的能力直接不出现」，
mac 侧本来就没有这个视图，**当前状态已经符合该规则**。

- Windows：`views = { "clock", "dash", "pet", "combo", "monitor", "devices", "clipboard", "window" }`
  （`src/Island.App/Controls/IslandWindow.xaml.cs` L1527）
- mac：`IslandCanvas` 只分发 pet / clock / combo / dash 等，**无 `window`**
- 判定：这是「同一功能（展示前台窗口信息）」只有一边有 → 归入阶段 3。
  两条出路：① mac 补「活动窗口」视图（搬功能）；② 明确归为平台专属并在两端呈现规则里说明。
  **倾向 ②**：`window` 视图依赖平台的「前台窗口标题」API，两端实现不同源，强行对齐会引入平台耦合。

### 4.2 小形象款式：mac 8 种 / Windows 5 种（阶段 2 批次 2 处理）

- mac（`PetFaceAv.cs` L76-83）：classic / mono / cat / robot / pixel / **alien / ghost / frog**
- Windows（`PetFace.cs` L160-164）：classic / mono / cat / robot / pixel（其余走 `_ => 0.42` 默认分支）
- 共享 `Settings.PetStyleValues` 已是 8 种超集（整合时取的并集）。
  Windows 设置界面用的是**自己本地**的 `PetStyles` 数组（`SettingsWindow.cs` L91），因此**不会**列出画不出来的款式 —— 无回归。
- 但若用户把 mac 的 settings.json 带到 Windows，`ApplyFields` 会接受 `alien`/`ghost`/`frog`，Windows 画不出 → 行为不一致。
- 判定：归入阶段 3，**以 mac 为主**：Windows 补齐 alien / ghost / frog 三种款式。

### 4.3 展开页签集与顺序

- mac：`监控 │ 剪贴板 │ 屏幕(仅mac) │ 设备(仅mac)`
- Windows（代码现状）：`监控 │ 剪贴板·文件 │ 设备 │ 控制` —— **顺序其实已经与 mac 的共享页签一致**（本条记录已过期，按代码为准）
- 判定：属**阶段 2**（展开面板对齐）的核心；页签**顺序**与**图标**统一，平台专属页签按 §1 规则不出现。

---

## 5. 阶段路线图

| 阶段 | 内容 | 交付物 | 状态 |
|---|---|---|---|
| 1 | 收起态胶囊对齐 | `2026-11-25-windows-compact-align-mac-design.md` | **已实施（ec50f03）** |
| 2 | 展开面板对齐（页签形态/顺序/图标、页内分区、视觉规格） | 规格 `2026-11-26-windows-expanded-align-mac-design.md` / 计划同名 | **实施中**（目标 = mac 现有实现；Windows 侧以「改调用点参数」为主，不合并两套 RenderKit） |
| 3 | 行为一致性收口（收起视图集 §4.1：Windows 多 monitor/devices/clipboard/window 四个 —— 要么补 mac 收起视图、要么明确归为平台专属） | 独立规格 | 待设计 |
| 4 | 能力清单与「不支持」呈现规则（§1 规则落到两端 UI + 公开文档） | 独立规格 | 待设计 |
| 5 | 一致性守护（跨端断言测试 + 能力清单漂移检查） | 独立规格 | 待设计 |

### 5.1 Windows 侧专项阶段（编号独立于上面的跨端阶段）

| 编号 | 内容 | 交付物 | 状态 |
|---|---|---|---|
| W1 | Windows 外设电量（HID 访问层 + L1/L3/L4 分层探测 + 数据驱动协议表） | 规格 `2026-11-26-windows-peripheral-battery-design.md` / 计划 `2026-11-26-windows-peripheral-battery.md` | **已实施**（`d1820e2` + 审查修复 `11ad76b`，已推 origin/main） |
| W2 | CPU 温度取值口径（分层匹配 + 排除干扰项） | 规格 `2026-11-26-cpu-temperature-picker-design.md` / 计划 `2026-11-26-cpu-temperature-picker.md` | **已实施**（`a2bee97`）：Windows 由 `Core Average` 改取 `CPU Package`，本机 49→58 |
| W3 | macOS 对齐（外设电量 2.4G 接收器 + CPU 温度口径；协议表与判定规则**两端共用同一份**） | 规格 `2026-11-26-macos-peripheral-battery-align-design.md` / 计划 `2026-11-26-macos-peripheral-battery-align.md` | 实施中（原生探针待用户在 Mac 上编译验证） |

W1 与上面的「阶段 1..5」不是同一条序列：跨端阶段解决的是**两端一致性**，
W1 解决的是 **Windows 侧的能力缺口**（2.4G 接收器读不到电量），不涉及 mac 侧改动。
W1 的分层探测模型与验收口径见其规格 §3 / §9.3；L3 协议表允许从
`%LOCALAPPDATA%\MyIsland\peripheral-protocols.json` 装载 —— **新增品牌填表即可，不改架构**。

每个阶段独立走「规格 → 计划 → 实施 → 验证」一轮，互不阻塞（除阶段 5 依赖前四阶段的结果）。

---

## 6. 每阶段的通用验收

1. `dotnet build Island.sln -c Release`：**0 警告 0 错误**
2. `Island.Tests`：**全过**（含各阶段新增断言）
3. **跨端离屏渲染对照**：两端同一状态各渲染一张，收起态/展开页并排比对（组数、元素顺序、字号、间距、分隔线位置）
4. **能力清单同步**：本文件 §3 矩阵随代码更新，不留过期条目
5. **收起态的墨迹级排版口径**（阶段 1 引入，比"胶囊宽"更强）：
   分隔线与最后一个字形的距离 == 18px、组间空白 == 24px、组内无空洞。
   量的是**墨迹尾**，不是预留宽 / 胶囊宽 —— 后者恰好是偏差本身（预留宽 ≠ 实画宽时），量不出来。
   详见阶段 1 规格 §8 第 6 条。

---

## 7. 不在本总纲范围内

- 具体像素级实现细节（各阶段规格里写）
- mac 侧的重构（除阶段 3 明确要求补的款式外）
- 平台专属能力的互相搬运（§1 已定为不搬）
- 展开面板的 XAML ↔ 自绘技术选型（阶段 2 再定）
