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
| `runtime` | 运行时长 | 两端都有 | 口径已对齐：PDH System Up Time（不含休眠） |
| `battery-bt` | 蓝牙设备电量 | 两端都有（两端实现不同源） | Windows 走 BLE GATT 0x180F/0x2A19；mac 走 IOKit 电池属性 |
| `battery-hid` | 2.4G 接收器电量 | 两端都有 | 两端共用同一份协议表与三层校验；mac 侧原生探针待实机验证 |
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
6. **展开页监控页**：Windows 的 2×2 栅格里不显示 **功耗 / 主板温度 / SSD 温度 / 运行时长 / 今日流量**
   （2×2 栅格放不下，**收起态的 dash / combo 里仍然有**）。

---

## 6. 怎么加一项能力

1. **改清单**：在 `src/Island.Core/Capabilities/CapabilityManifest.cs` 里增删条目（键、显示名、分组、可用性、归属端、差异说明）。
2. **两端从清单取**：两端的收起视图列表 / 展开页签顺序 / 小形象款式表都从清单取，不再各写一份；
   平台不支持的项按总纲 §1 **直接不出现**（不置灰、不留空位）。
3. **门禁**：`CapabilityManifestTests` 断言清单自身完整性，并断言两端代码引用的就是清单里的值 ——
   **清单改了、两端代码没跟着改 ⇒ 门禁红**。
4. **同步本文件**：本文件是人读版本，随清单同步更新；两边对不上时**以清单为准**。

## 7. 待产品拍板

- **展开页监控是否恢复那五个读数**（功耗 / 主板温度 / SSD 温度 / 运行时长 / 今日流量）：
  2×2 栅格放不下，收起态仍有 —— 恢复则要另找版位或改栅格。
- **收起视图集怎么收口**：Windows 独有的 4 个视图（高密度读数带 / 设备电量行 / 剪贴板提示行 / 活动窗口）
  是收掉、是给 mac 补上、还是接受差异公开 —— 现在是「差异公开」的状态。

---

## 8. 主题 token 语义口径（统一语义名，公开取值差异）

**真相源**：`src/Island.Core/Capabilities/ThemeTokenManifest.cs`　**门禁**：`tests/Island.Tests/ThemeTokenTests.cs`

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

---

