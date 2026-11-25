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

---

## 3. 能力矩阵（2026-11-25 实测）

| 能力 | Windows | macOS | 判定 |
|---|---|---|---|
| 收起视图：clock / dash / pet / combo / monitor / devices / clipboard | ✓ | ✓ | 一致 |
| 收起视图：**window**（活动窗口） | ✓ | ✗ | **待对齐**（§4.1） |
| 展开页签：监控 / 剪贴板 / 设备 | ✓ | ✓ | 一致 |
| 展开页签：**系统控制**（亮度/WLAN/蓝牙/麦克风/锁屏/睡眠/清理内存） | ✓ | ✗ | 平台专属（Windows） |
| 展开页签：**屏幕**（DDC/色温/关内建屏） | ✗ | ✓ | 平台专属（macOS） |
| 小形象 | ✓ `PetFace.cs` | ✓ `PetFaceAv.cs` | 存在但**款式数不同**（§4.2） |
| 全局热键 | ✓ | ✓ `HotKeyClient` | 一致 |
| 剪贴板历史保留策略（session/1d/7d/30d） | ✓ | ✓ | 一致 |
| 剪贴板详情窗 | ✗ | ✓ `ClipDetailWindow` | 平台专属（macOS） |
| 图片预览窗 | ✗ | ✓ `ImagePreviewWindow` | 平台专属（macOS） |
| 自动跟随窗口 | ✗ | ✓ `AutoFollowEngine` | 平台专属（macOS） |
| 触感反馈 | ✗ | ✓ `Haptics` | 平台专属（macOS 硬件能力） |
| 内存回收（用完即还） | ✓ `MemoryReclaim` | ✗ | 平台专属（Windows） |
| 手柄档位（XInput） | ✓ `XInputService` | ✗ | 平台专属（Windows） |
| BLE GATT 电量直读 | ✓ `BleBatteryService` | ✗（mac 走原生电池 API） | 平台专属（各自实现） |
| 温度链路 / 天气 / 设置项 | ✓ | ✓（共享源） | 一致 |
| 签名安装包 / 开机自启 / 文件托盘 | ✓ | ✗ | 平台专属（Windows） |

---

## 4. 已发现的行为不一致（本次要收口）

### 4.1 收起视图集：Windows 多一个 `window`

- Windows：`views = { "clock", "dash", "pet", "combo", "monitor", "devices", "clipboard", "window" }`
  （`src/Island.App/Controls/IslandWindow.xaml.cs` L1527）
- mac：`IslandCanvas` 只分发 pet / clock / combo / dash 等，**无 `window`**
- 判定：这是「同一功能（展示前台窗口信息）」只有一边有 → 归入阶段 3。
  两条出路：① mac 补「活动窗口」视图（搬功能）；② 明确归为平台专属并在两端呈现规则里说明。
  **倾向 ②**：`window` 视图依赖平台的「前台窗口标题」API，两端实现不同源，强行对齐会引入平台耦合。

### 4.2 小形象款式：mac 8 种 / Windows 5 种

- mac（`PetFaceAv.cs` L76-83）：classic / mono / cat / robot / pixel / **alien / ghost / frog**
- Windows（`PetFace.cs` L160-164）：classic / mono / cat / robot / pixel（其余走 `_ => 0.42` 默认分支）
- 共享 `Settings.PetStyleValues` 已是 8 种超集（整合时取的并集）。
  Windows 设置界面用的是**自己本地**的 `PetStyles` 数组（`SettingsWindow.cs` L91），因此**不会**列出画不出来的款式 —— 无回归。
- 但若用户把 mac 的 settings.json 带到 Windows，`ApplyFields` 会接受 `alien`/`ghost`/`frog`，Windows 画不出 → 行为不一致。
- 判定：归入阶段 3，**以 mac 为主**：Windows 补齐 alien / ghost / frog 三种款式。

### 4.3 展开页签集与顺序

- mac：`监控 │ 剪贴板 │ 屏幕(仅mac) │ 设备(仅mac)`
- Windows：`监控 │ 设备 │ 剪贴板 │ 控制`
- 判定：属**阶段 2**（展开面板对齐）的核心；页签**顺序**与**图标**统一，平台专属页签按 §1 规则不出现。

---

## 5. 阶段路线图

| 阶段 | 内容 | 交付物 | 状态 |
|---|---|---|---|
| 1 | 收起态胶囊对齐 | `2026-11-25-windows-compact-align-mac-design.md` | **已实施（ec50f03）** |
| 2 | 展开面板对齐（页签顺序/图标/页内分区/视觉规格） | 独立规格 | 待设计 |
| 3 | 行为一致性收口（视图集 §4.1 / 小形象款式 §4.2） | 独立规格 | 待设计 |
| 4 | 能力清单与「不支持」呈现规则（§1 规则落到两端 UI + 公开文档） | 独立规格 | 待设计 |
| 5 | 一致性守护（跨端断言测试 + 能力清单漂移检查） | 独立规格 | 待设计 |

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
