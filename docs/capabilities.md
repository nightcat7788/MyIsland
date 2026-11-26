# MyIsland 两端能力清单（对外公开）

> **能力清单是唯一真相源**：`src/Island.Core/Capabilities/CapabilityManifest.cs`。
> 本文件是它的人读版本，并公开两端已知差异 —— 双平台一致性总纲 §1 要求「差异在能力清单里公开」。
> **本文件不是真相源**：改能力请先改清单（见文末「怎么加一项能力」），本文件随清单同步。

清单里每个条目都带四个字段：**键**（稳定标识，不随显示名变化）、**显示名**、**可用性**（两端都有 / 仅 Windows / 仅 macOS）、**差异说明**。
表中「—」表示清单里该项没有额外的差异说明（`Note` 为空）。

---

## 1. 收起视图（岛体收起时的视图集）

共享部分的顺序以 mac 为准（总纲 §1）；Windows 独有的四项追加在共享部分之后。
**Windows 比 mac 多 4 个视图**。

| 键 | 显示名 | 可用性 | 差异说明 |
|---|---|---|---|
| `dash` | 读数带 | 两端都有 | — |
| `clock` | 时钟 | 两端都有 | — |
| `pet` | 小形象 | 两端都有 | — |
| `combo` | 组合 | 两端都有 | — |
| `monitor` | 高密度读数带 | 仅 Windows | Windows 独有：每核细柱 + C/M/G/D 表盘 |
| `devices` | 设备电量行 | 仅 Windows | Windows 独有：收起态显示各设备电量图标与读数 |
| `clipboard` | 剪贴板提示行 | 仅 Windows | Windows 独有：收起态显示最近一条剪贴板预览 |
| `window` | 活动窗口 | 仅 Windows | 依赖 user32 前台窗口 API，mac 无等价实现 |

## 2. 展开页签（展开面板）

**共享页签在前、平台专属页签在后**（总纲 §1 的呈现规则，两端统一）。

| 键 | 显示名 | 可用性 | 差异说明 |
|---|---|---|---|
| `monitor` | 监控 | 两端都有 | — |
| `clips` | 剪贴板 | 两端都有 | Windows 的页面内另有「文件」列（Windows 专属内容） |
| `devices` | 设备 | 两端都有 | — |
| `control` | 控制 | 仅 Windows | 亮度/WLAN/蓝牙/麦克风/锁屏/睡眠/清理内存 |
| `screens` | 屏幕 | 仅 macOS | 显示器控制（DDC） |

## 3. 数据源（读数从哪来）

| 键 | 显示名 | 可用性 | 差异说明 |
|---|---|---|---|
| `cpu-usage` | CPU 占用 | 两端都有 | 口径已对齐：% Processor Utility（任务管理器口径），2026-11-26 修正 |
| `cpu-frequency` | CPU 频率 | 两端都有 | 口径已对齐：Actual Frequency 真值优先 |
| `cpu-temp` | CPU 温度 | 两端都有 | 口径已对齐：Package 优先、排除 Distance to TjMax 等干扰项 |
| `gpu` | GPU 占用/温度/显存/频率 | 两端都有 | — |
| `memory` | 内存占用 | 两端都有 | — |
| `disk` | 磁盘活动/读写 | 两端都有 | — |
| `network` | 网速 | 两端都有 | 口径已对齐：排除虚拟网卡（Hyper-V/WSL/容器等） |
| `runtime` | 运行时长 | 两端都有 | 口径已对齐：两端**同一实现** —— `Environment.TickCount64`（系统开机时长）。注：Windows 侧曾有一段 PDH 的 System Up Time 计数器，是**死代码**（建了从不读），已于 2026-11-26 删除 |
| `cpu-power` | CPU 功耗 | 仅 Windows | Windows 走 LHM CPU 功耗传感器（需管理员）；mac 无对应数据源 |
| `board-temp` | 主板温度 | 仅 Windows | Windows 走 LHM 主板传感器（需管理员）；mac 无对应数据源。**现显示在温度卡里**（2026-11-26 task16 从底部状态行移来） |
| `today-traffic` | 今日流量 | 仅 Windows | Windows 走 PDH 网络计数器累计（今日 ↓/↑）；mac 无每日流量口径 |
| `battery-bt` | 蓝牙设备电量 | 两端都有（两端实现不同源） | Windows 走 BLE GATT 0x180F/0x2A19；mac 走 IOKit 电池属性 |
| `battery-hid` | 2.4G 接收器电量 | 两端都有 | 两端共用同一份协议表与三层校验；mac 侧原生探针待实机验证（核对流程：`bash tools/mac-selfcheck.sh` → 报告 `docs/verification/mac-<日期>.md`）；内置表只收**已实机验证**的条目（见 §9） |
| `battery-internal` | 本机内置电池 | 两端都有（两端实现不同源） | Windows 走 WMI Win32_Battery；mac 走 AppleSmartBattery |
| `storage-temp` | SSD 温度 | 仅 macOS | Windows 不可用：LHM 的 NVMe SMART IOCTL 在本机触发 0x1EA 蓝屏两次；mac 走 IOHID NAND 传感器 |

## 4. 小形象款式

清单里的款式键（`CapabilityManifest.PetStyles`）：8 款，**两端一致**（2026-11-26 已补齐 Windows 侧缺失的 3 款：alien / ghost / frog）。
显示名取自两端设置下拉（Windows `src/Island.App/Controls/SettingsWindow.cs`、macOS `src/Island.Avalonia/PetFaceAv.cs`，**两端同名**）。

| 键 | 显示名 | 可用性 | 差异说明 |
|---|---|---|---|
| `classic` | 经典双眼 | 两端都有 | — |
| `mono` | 独眼 | 两端都有 | — |
| `cat` | 猫咪 | 两端都有 | — |
| `robot` | 机器人 | 两端都有 | — |
| `pixel` | 像素 | 两端都有 | — |
| `alien` | 外星人 | 两端都有 | — |
| `ghost` | 幽灵 | 两端都有 | — |
| `frog` | 青蛙 | 两端都有 | — |

⚠️ 款式**列表**两端一致，但**实现是两份独立代码**（Windows `src/Island.App/Animation/PetFace.cs` / macOS `src/Island.Avalonia/PetFaceAv.cs`），
详见下一节第 5 条。

---

## 5. 两端已知差异（公开）

以下六条是 **2026-11-26 实测**结论（不是推测），此前总纲里有三条记录与之相反，已按实测修正。

1. **收起视图集**：共享 4 个（`dash` / `clock` / `pet` / `combo`，顺序以 mac 为准）+ Windows 独有 4 个
   （`monitor` 高密度读数带 / `devices` 设备电量行 / `clipboard` 剪贴板提示行 / `window` 活动窗口）。
   —— 旧总纲写「两端一致」不成立。
2. **展开页签**：共享 3 个（监控 / 剪贴板 / 设备）+ Windows 独有「控制」+ macOS 独有「屏幕」；
   **平台专属页签统一排在共享页签之后**（两端同一规则）。
3. **数据源**：蓝牙设备电量、本机内置电池、SSD 温度 **两端实现不同源**；
   2.4G 接收器电量 **两端共用同一份协议表**（同一份协议表 + 三层校验）。
4. **主题 token 不同源**：12 个同名 token 在每套主题下取值都不同
   （Windows「暗色科技：近黑底 + 单一强调色」vs macOS「Apple 系统色：中性色阶 + 个性色」）；
   `Island` 主题还是「Windows 单一深色 vs macOS 日夜自动切换」。
   **语义口径见第 8 节**（同名 = 同一语义角色，取值按设计不同）。
5. **渲染层与小形象是两份独立实现**：
   渲染层 Windows `src/Island.App/Controls/RenderKit.cs`（38 KB）vs macOS `src/Island.Avalonia/RenderKit.cs`（18 KB）；
   小形象同理（`PetFace.cs` vs `PetFaceAv.cs`），mac 工程没有 link Windows 的实现。
6. **展开页监控页**：Windows 的 2×2 栅格里不显示 **功耗 / SSD 温度 / 运行时长 / 今日流量**
   （2×2 栅格放不下，**收起态的 dash / combo 里仍然有**）；**主板温度**已从底部状态行移进**温度卡**
   （Windows 专属行，见 §7.3），避免同屏出现两次。

---

## 6. 怎么加一项能力

1. **改清单**：在 `src/Island.Core/Capabilities/CapabilityManifest.cs` 里增删条目（键、显示名、分组、可用性、归属端、差异说明）。
2. **两端从清单取**：两端的收起视图列表 / 展开页签顺序 / 小形象款式表都从清单取，不再各写一份；
   平台不支持的项按总纲 §1 **直接不出现**（不置灰、不留空位）。
3. **门禁**：`CapabilityManifestTests` 断言清单自身完整性，并断言两端代码引用的就是清单里的值 ——
   **清单改了、两端代码没跟着改 ⇒ 门禁红**。
4. **同步本文件**：本文件是人读版本，随清单同步更新；两边对不上时**以清单为准**。

## 7. 产品决策（2026-11-26 已拍板）

### 7.1 展开页监控的底部状态行：**恢复**

2×2 栅格下方新增一条 **9px 单行状态行**，放「不在卡片里、但值得一眼看到」的读数。
**两端同结构**：字段位清单、顺序、几何都取自唯一真相源
`src/Island.Core/Capabilities/MonitorStatusLineSpec.cs`；每个字段位在不在本平台出现，
由 §3 的数据源清单决定（`SlotsFor(macOS)`）。

| 字段位 | Windows | macOS | 说明 |
|---|---|---|---|
| 功耗 | 出现 | **不出现** | Windows 走 LHM CPU 功耗；mac 无对应数据源 |
| 主板温度 | **不出现** | **不出现** | 已移到**温度卡**（Windows 专属行，§7.3）—— 同一读数不在同屏出现两次 |
| SSD 温度 | **不出现** | 出现 | Windows 侧 NVMe SMART IOCTL 有 0x1EA 蓝屏史 |
| 运行时长 | 出现 | 出现 | 两端同一实现：`Environment.TickCount64`（系统开机时长） |
| 今日流量 | 出现 | **不出现** | Windows 走 PDH 网络计数器累计；mac 无每日流量口径 |

**呈现规则**：平台不支持 ⇒ 直接不出现（不置灰、不留空位）；支持但读不到 ⇒ 显示「未知」
（绝不用 0 或默认值充数）。

**版位代价**：Windows 展开面板因此**加高 20px（380 → 400）** —— 不加的话监控页实得高度 308 扣掉状态行
只剩 288，会从「让位档」掉到「紧凑档」（CPU 走势曲线没了、进程榜卡也矮一截）。
加 20px 后仍是让位档：**加功能不以牺牲既有内容为代价**。

**已知的一处底部结构差异（mac 多一行，如实公开）**：mac 端在状态行**下面**还有一行
「读数正常 / ⚠ …」的**采样健康提示**（把「读不到」的项和原因列出来，而不是在卡片里显示假的 0）。
Windows 端没有这一行 —— 同样的信息在 Windows 是**就地显示**的（温度位显示「需管理员」「缺驱动」）。
两边的**最后一行都是底部状态行**，但 mac 的底部多一行诊断提示。
把它挪到状态行之上需要额外给栅格让 2px（mac 的栅格高度刚好占满），**代价大于收益**，故保持现状、公开差异。

### 7.2 收起视图集：**明确归为平台专属**

Windows 独有的 4 个视图（`monitor` 高密度读数带 / `devices` 设备电量行 / `clipboard` 剪贴板提示行 /
`window` 活动窗口）**确定为 Windows 平台专属** —— 不收掉、也不给 mac 补。
差异就公开在 §1 的表里（清单里它们是 `PlatformSpecific` / `WindowsOnly`）。
因此计划里的 **C3「mac 收起视图补 devices」不再需要**（mac 的设备电量在**展开页设备页**里已有）。
两端的收起视图顺序仍以 mac 为准（`dash` / `clock` / `pet` / `combo`），Windows 的 4 个专属项排在后面。

### 7.3 展开页监控页的卡片分组：**两端统一**

展开页监控页的卡片分组此前是**两端各写一套**（mac「占用 / 温度 / 上下速度 / 占用最高」vs
Windows「CPU / 内存 / 上下速度 / 占用最高」）—— 信息量接近，但同一块页面看起来像两个产品。
**统一成 mac 的分组**，两端唯一真相源是
`src/Island.Core/Capabilities/MonitorPageLayoutSpec.cs`（卡片槽 + 每个卡的行槽 + 行标签 + 平台可用性）。

| 卡 | 标题 | 行 | Windows | macOS |
|---|---|---|---|---|
| 1 | 占用 | CPU / 内存 / 磁盘 | 三行全有 | 三行全有 |
| 2 | 温度 | CPU / GPU / 存储（SSD） | **CPU / GPU / 主板**（SSD 整行不出现；用同样平台专属的主板温度补位 ⇒ 两端行数一致、卡底不留空） | 三行全有 |
| 3 | 上下速度 | 下行 / 上行 | 两行全有 | 两行全有 |
| 4 | 占用最高 | 进程榜 | 进程榜 | 进程榜 |

**平台专属读数保留，且挂在同一类位置**：卡头右上角的附加读数（Windows 挂 CPU 频率、mac 挂 GPU 占用）；
Windows 的 GPU 显存并进 GPU 温度那一行（mac 无显存可读）；温度卡的第三行也是平台专属补位
（Windows = 主板温度，mac = 存储温度）⇒ **两端温度卡都是 3 行、卡底不留空**。
**部件形态与配色身份各端保留**（Windows 进度条 + 走势曲线 / 暗色科技；mac 环表盘 / 按资源配色）—— 那是视觉身份，不是分组。

**门禁（两端各一套，同一套判定思路）**：
- Windows：`tools/visual-probe/VisualProbeCore.cs` —— 卡片与行带几何**由产品代码给出**
  （`MonitorPage.PlannedCards`），断言「卡片清单与规格一致」「画出来的行 == 规格里该平台的行」
  「每一行该在的那一带里必须有绘制」；规格过滤由 `tests/Island.Tests/MonitorPageLayoutTests.cs` 锁住。
- macOS：`tools/avalonia-probe/AvaloniaVisualCore.cs` —— 同样按规格逐行算行带并断言「那一带里有内容」
  （底色取**该区域众数色**，避免把卡外那截算成内容）；规格过滤与「源码绘制顺序 == 规格顺序」
  由 `tests/Island.Tests/MonitorPageLayoutMacTests.cs` 锁住。

两个探针的这条门禁都**做过「改坏即红」的实测**：把某一行的绘制去掉（或让规格与绘制对不上），
对应断言变红、探针退出码非 0。

---

## 8. 主题 token 语义口径（统一语义名，公开取值差异）

**真相源**：`src/Island.Core/Capabilities/ThemeTokenManifest.cs`　**门禁**：`tests/Island.Tests/ThemeTokenTests.cs`（声明层）　·　`tests/Island.Tests/ThemeTokenUsageTests.cs`（用法层）

两端各有一份 Palette（Windows `src/Island.App/Themes/Theme.cs`、macOS `src/Island.Avalonia/Themes.cs`），
其中 **12 个颜色 token 两端同名**，但**每套主题下取值都不同**。本项目的口径是：

- **统一的是「语义名」，不是「具体色值」** —— 同一个 token 名在两端指的是**同一个语义角色**
  （两端 `Accent` 都是「唯一强调色」，两端 `Down` 都是「跌=绿」）。
- **取值按设计不同，是有意保留的差异，不是缺陷、不是待办** —— Windows 端保留「暗色科技：近黑中性底 +
  单一强调色 + 一屏一主角」的配色身份，macOS 端保留「Apple 系统色：中性色阶 + 一个个性强调色」的材质分层。
  强行统一色值 = 把一端的配色体系搬进另一端 = 违背已定的产品决策，**本轮明确不做**。

### 8.1 两端共享的 12 个 token

| 语义键 | 语义说明 | Windows 属性 | mac 属性 | 两端取值是否相同 |
|---|---|---|---|---|
| `surface` | 窗口底色（最底层）：所有元素画在它之上 | `Surface` | `Surface` | 否（按设计不同：近黑暗色科技底 vs Apple 冷中性底） |
| `surface-alt` | 次级容器底色：卡片 / 分组内小块 | `SurfaceAlt` | `SurfaceAlt` | 否（按设计不同；层级角色也不对称：Windows 端当卡片底用，mac 端让给 `elevated`） |
| `text-primary` | 主文字色：标题与关键读数 | `Text` | `Text` | 否（按设计不同） |
| `text-secondary` | 次级文字色：说明文案与单位 | `TextDim` | `TextDim` | 否（按设计不同） |
| `text-tertiary` | 三级文字色：弱化提示（10px 说明文字也要读得清） | `TextTertiary` | `TextTertiary` | 否（按设计不同） |
| `accent` | 唯一强调色：选中态、焦点、关键数据发光点 | `Accent` | `Accent` | 否（按设计不同：薄荷青/柠檬黄等 vs 系统蓝） |
| `rim` | 描边色：卡片 / 容器的轮廓 | `Rim` | `Rim` | 否（按设计不同） |
| `track` | 轨道色：进度条与表盘的未填充部分 | `Track` | `Track` | 否（按设计不同：Windows 端半透明白，mac 端实色中性阶） |
| `hairline` | 发丝线：分组之间、卡片内部的 1px 分隔线 | `Hairline` | `Hairline` | 否（按设计不同） |
| `warning` | 告警色：读数偏高 / 需要注意（非致命） | `Warning` | `Warning` | 否（按设计不同） |
| `up` | 上升色：涨=红（中国股市习惯），并兼作危险级别状态色 | `Up` | `Up` | 否（按设计不同；**语义两端一致：都是涨=红**） |
| `down` | 下降色：跌=绿 | `Down` | `Down` | 否（按设计不同；**语义两端一致：都是跌=绿**） |

### 8.2 平台专属 token

| 语义键 | 语义说明 | Windows 属性 | mac 属性 | 说明 |
|---|---|---|---|---|
| `elevated` | 抬升层底色：卡片 / 浮起层 | — | `Elevated` | 仅 macOS 有：Windows 端无抬升层 token，卡片底由 `surface-alt` 承担 |
| `fill` | 控件内嵌填充：进度槽、输入框底 | — | `Fill` | 仅 macOS 有 |
| `selection` | 选中态的柔和底色：页签胶囊等 | — | `Selection` | 仅 macOS 有：Windows 端选中态直接用 `accent` 表达 |
| `square-corners` | 直角开关：像素主题用 3px 微圆角的直角形态 | `SquareCorners` | — | 仅 Windows 有，且**不是颜色**（是 bool）；mac 端圆角固定 |

### 8.3 门禁怎么守

`ThemeTokenTests` 断言：清单键唯一且每个 token 都有语义说明；两端 Palette 源码里**声明了的每个颜色/开关 token
都已登记进清单**，且**清单登记的每个属性在源码里真的存在**
⇒ **新增或删除一个 token 而不登记 ⇒ 门禁红**（与 `CapabilityManifestTests` 同一范式）。

### 8.4 界面元素 → 两端统一语义 token（渲染层用法门禁）

**真相源**：`src/Island.Core/Capabilities/ThemeTokenManifest.cs` 的 `ElementTokens` 表
· **门禁**：`tests/Island.Tests/ThemeTokenUsageTests.cs`

§8.1～8.2 管的是"两端**声明**了哪些 token"（不缺项）；本表管的是"**同一个界面元素在两端用了哪个 token**"
（不串角色）。门禁按**绘制调用点**回读两端渲染源码核对（不是数 `pal.` 出现次数 —— 计数法太脆），
所以这一层能抓住"Windows 用 `pal.Text`、mac 用 `pal.TextDim`"这种**清单层查不出来**的漂移。

| 界面元素 | 统一语义 token | Windows 绘制点（`MonitorPage.cs`） | macOS 绘制点（`IslandCanvas.cs`） |
|---|---|---|---|
| 卡片标题 | `text-primary`（`Text`） | `CardTitle(...)` 的 4 处调用（占用 / 温度 / 上下速度 / 占用最高卡） | `Card(...)` 局部函数 |
| 读数行标签 | `text-secondary`（`TextDim`） | 读数行 + 速度行各 1 处 | `Row` / `Speed` 各 1 处 |
| 底部状态行 | `text-tertiary`（`TextTertiary`） | 标签 / 等宽读数 / 普通读数 3 处 | 1 处（标签与读数合成一段） |
| 空态 / 未采集提示 | `text-tertiary`（`TextTertiary`） | 进程榜空态 1 处 | 进程榜空态 1 处 |
| 进程榜条目名称 | `text-primary`（`Text`） | 1 处 | 1 处 |
| 进程榜条目数值 | `text-secondary`（`TextDim`） | 1 处 | 1 处 |
| 应用图标首字母兜底 | `text-secondary`（`TextDim`） | 1 处 | 1 处（2026-11-26 由 `text-primary` 改齐：它是图标的替身，应比应用名更安静） |
| 排序分段控件选中段底 | `accent`（`Accent`） | 2 处分支 | 1 处 |
| 排序分段控件选中段文字 | `surface`（`Surface`） | 1 处 | 1 处 |

**本表只列"两端本来就该一致"的元素。** 两端**按设计不同**的地方不在表里（也不该硬拉成一致）：
mac 端的读数行数值用「按资源配色」（`ResColor`），Windows 端用「一屏一主角」（CPU 用 `accent`、其余用
`text-primary`）；mac 端卡片图标用资源色 / `accent`，Windows 端卡片图标用 `text-primary`。
这些是 §8 已拍板的配色体系差异（Windows 暗色科技 vs mac 按资源配色），**不是漂移、不改**。

---

## 9. 外设协议表（2.4G 接收器电量）

**真相源**：`src/Island.App/Services/Peripheral/PeripheralProtocol.cs`（表结构 + 三层校验）
· `src/Island.App/Services/Peripheral/PeripheralProtocolTable.cs`（校验层）
· **完整文档**：[`docs/peripheral-protocols.md`](peripheral-protocols.md)（怎么加一条品牌、怎么验证、验证不过怎么排查）

| 项 | 现状（2026-11-26） |
|---|---|
| 内置表条数 | **1 条**：`lenovo-idea5003`（联想 Idea5003 2.4G 接收器） |
| 该条验证状态 | **已实机验证**：读出 58%（**推断值**：偏移 5；依据同型号蓝牙鼠标系统缓存 59%、差 1pp、闲置衰减一致；**未做同刻交叉验证**） |
| 其它品牌 | 罗技 / 雷蛇 / VGN 等 **未实机验证** ⇒ 只提供**候选模板**（`docs/peripheral-protocols.md` §7），**不进匹配表、不输出读数** |
| 内置表准入 | 只装载 `verification = verified` 的条目 —— 没验过的协议**不会生效**（设备显示"未知"，而不是一个假读数） |
| L1 标准 usage | 设备描述符里声明了标准电量 usage 的**不需要写表项**（支持更多接收器最省事的一条路） |
| 覆盖语义 | 用户条目在前、内置条目一条不删；**逐设备逐字段匹配** ⇒ 同 key 但字段不同 ⇒ 覆盖**不生效**（工具会指出哪里对不上） |

> **底线**：没实机验证过的协议，**宁可显示"未知"，也不显示一个看着很正常的假百分比**。
> 每条表项都带验证状态（`verification` / `verificationNote` / `verifiedOn` / `verifiedDevice`），
> 自检工具（`hid-probe --selftest`）会逐条打印出来。

---

