# Windows 外设电量（HID 访问层 + L1/L3/L4）—— 设计规格

日期：2026-11-26
状态：**已实现并在本机跑通**（2026-11-26）；电量语义为**推断值**，未做同刻交叉验证（见 §8.5）
范围：Windows 版（`src/Island.App/`）；`src/Island.Avalonia/` **零改动**

> 本文是 Windows 侧的**专项规格**，不在
> [`2026-11-25-cross-platform-consistency-design.md`](2026-11-25-cross-platform-consistency-design.md)
> 的跨端阶段序列里：总纲 §5 的「阶段 2」指展开面板对齐，与本文不是同一件事。
> 为避免编号撞车，本文在总纲 §5.1 单列为 **W1**（Windows 外设电量）。

---

## 1. 背景

Windows 版目前的外设电量只有四条**系统数据源**（见 `src/Island.App/Services/DeviceService.cs` 顶部注释）：

| # | 数据源 | 覆盖 |
|---|---|---|
| ① | WMI `root\CIMV2` Win32_Battery | 笔记本内置电池（多块聚合成一条） |
| ② | WMI `root\WMI` MSBatteryClass | Windows 主动上报电量的蓝牙设备 |
| ③ | BLE GATT 直读（`Services/BleBatteryService.cs`） | MSBatteryClass 不上报的 BLE HID 设备 |
| ④ | XInput 档位（`Services/XInputService.cs`） | 手柄：空/低/中/满，**不换算百分比** |

**读不到的是 2.4G 接收器**（无线键鼠 / 无线耳机的 dongle）：它在 Windows 上只表现为一个 HID 设备节点，
系统不提供任何电量属性，电量藏在厂商私有协议里（罗技 HID++ / ROG / 雷蛇 / 联想 …）。
`DeviceService` 的既有注释写的是「没有对应硬件前不写」—— 现在本机就有硬件（联想 2.4G 接收器），这条链路可以做了。

但**不能为每个品牌写一份专用代码**：每加一个品牌就要改一次架构，每次改架构都要重新验证全量。
所以本阶段的目标不是「支持联想」，而是把整个能力改造成**分层探测 + 数据驱动协议表**的框架：
**新增品牌 = 填表，不是改架构**。

---

## 2. 目标

1. 读得到 2.4G 接收器等 HID 外设的电量；**读不到就显示「未知」，绝不换算充数**。
2. 整体改造成 **L1 → L4 分层探测 + 数据驱动协议表**（§3）。
3. 呈现语义与既有 `DeviceInfo` 一致：`LevelText = "未知"` 与 XInput 档位同一套规则
   （`DevicesPage.cs` 的 `StatusText`：`LevelText == "未知"` → 显示「电量未知」）。
4. macOS 侧**零改动**（`src/Island.Avalonia/**` 的 `git diff` 必须为空）。

非目标见 §10。

---

## 3. 分层探测模型

| 层 | 数据源 | 需要的协议知识 | 覆盖 |
|---|---|---|---|
| **L1** | 标准 HID 电量 usage | 零 | 极少（规范里真正定义了电量字段的设备） |
| **L2** | 系统数据源：WMI / MSBatteryClass / BLE GATT / XInput | 零 | 现有能力，**本次不改** |
| **L3** | 厂商私有协议表（数据驱动） | 查表 | 2.4G 接收器等 —— 本阶段主要目标 |
| **L4** | 通用识别兜底 | 零 | 只识别设备、**不读电量**，界面显示「未知」 |

执行顺序 **L1 → L2 → L3 → L4**，先命中的层负责该设备的读数。

去重规则：

- **L3 命中后，L4 里同 VID:PID 的条目要去重**（同一台接收器不能出现两行）。
- 同一物理外设（VID:PID）整体只出现一条；与 BLE / MSBatteryClass 已报的同名设备要去重（§9）。

「层」的判定与「设备」的判定是分开的：L1/L3 给的是**读数**，L4 给的是**设备存在性**。

---

## 4. HID 访问层设计

### 4.1 现状

访问层已在本机跑通，工作区里有四个文件（**尚未入仓**）：

| 文件 | 职责 |
|---|---|
| `src/Island.App/Services/Peripheral/HidNative.cs` | P/Invoke 声明、结构体、能力表读取 |
| `src/Island.App/Services/Peripheral/HidModels.cs` | `HidInterface` / `HidUsageRange` / `HidGroup` |
| `src/Island.App/Services/Peripheral/HidBus.cs` | 枚举 HID 顶层集合 + 按 VID:PID 分组 |
| `src/Island.App/Services/Peripheral/HidChannel.cs` | 已打开通道：三种方式发命令 / 带超时读报告 |
| `tools/hid-probe/` | 一次性探针工具 —— §8 的实机数据就是它跑出来的 |

下面 §4.2–§4.7 是这一层的设计口径与实机结论，**实现必须照此对齐**。

### 4.2 枚举

`SetupDiGetClassDevs(GUID_DEVINTERFACE_HID)` → `SetupDiEnumDeviceInterfaces` → `SetupDiGetDeviceInterfaceDetail`。

- x64 下 `cbSize = 8`（x86 是 6），`DevicePath` 从**偏移 4** 开始取
  （前 4 字节是 cbSize + 一个指针槽，x64 上指针槽占 4 字节）。
- 枚举预算 1400ms、接口数上限 512：设备多的机器上枚举本身要几十毫秒，不能拖住采样节拍。
- 单个集合读失败只跳过它自己，绝不让整轮枚举失败（一个坏设备不该让整页设备消失）。

### 4.3 独占集合：枚举阶段一律 `dwDesiredAccess = 0`

顶层集合（鼠标 / 键盘 / 笔 / 触摸屏）被系统独占，用 `GENERIC_READ|GENERIC_WRITE` 打开会得到
**ERROR_ACCESS_DENIED = 5**。

所以**枚举阶段一律用 `dwDesiredAccess = 0` 打开**：0 权限打不开写，但
**属性、字符串、能力表都能读**（`HidD_GetAttributes` / `HidD_GetProductString` / `HidD_GetManufacturerString` /
`HidD_GetSerialNumberString` / `HidD_GetPreparsedData` + `HidP_GetCaps`）。
真正要发命令时才用读写权限重开（多半失败，失败就当这条链路走不通，记日志、返回未知）。

### 4.4 能力表

- `HidD_GetPreparsedData` + `HidP_GetCaps` → UsagePage / Usage + 三种报告长度
  （`InputReportByteLength` / `OutputReportByteLength` / `FeatureReportByteLength`）。
- `HidP_GetValueCaps` → usage 范围、报告 ID、逻辑范围（LogicalMin / LogicalMax）。

⚠️ **两条实机踩坑，必须照抄**：

1. **`HidP_GetValueCaps` 的返回值是「取到的条数」，不是状态码**。
   判断成功要看**写回的 length 参数**，不能拿返回值和 `HIDP_STATUS_SUCCESS` 比。
2. **`HIDP_VALUE_CAPS` 原生是 72 字节**，结尾是 8 个 USHORT 的 union。
   显式布局少写一个 USHORT → 结构体变成 **60 字节** → 驱动按 72 字节往里写 →
   **堆损坏（STATUS_HEAP_CORRUPTION）**。
   （现状 `HidNative.HidValueCaps` 用显式布局按 72 字节排（最后字段 `FieldOffset(70)`），改动时不要压成 60。）

### 4.5 三种传输方式必须都试

| 方式 | API | 说明 |
|---|---|---|
| 控制端点 | `HidD_SetOutputReport` | 最省事，但不少设备不回应 |
| 中断 OUT | `WriteFile` 整包 | 只认中断端点的设备（部分 2.4G 键盘）只有这条路 |
| Feature | `HidD_SetFeature` / `HidD_GetFeature` | 部分厂商把命令与状态都放在 feature 报告里 |

同一条命令**只认其中一种端点**的情况很常见 ⇒ 三种都要试。
本机联想接收器的状态读取走的是 **feature** 报告（§8）。

### 4.6 读：overlapped + 超时 + 取消

`FILE_FLAG_OVERLAPPED` + `WaitForSingleObject`（毫秒级超时）+ `CancelIoEx`。

**阻塞读叠上并发写会把进程直接杀掉** —— 这是 Windows HID 的老规矩，不是危言耸听。
超时后 `CancelIoEx` 并再等一小段时间（驱动赖着不结束时，挂起的缓冲区只能保活、不能释放）。

### 4.7 帧长

- 发出去的帧要**补齐到集合声明的 output / feature report 长度**（Windows 要求整包长度）。
- **超长就拒绝，不截断** —— 截断 = 静默丢字节，设备可能把半条命令当成别的命令。

---

## 5. L1：标准 HID 电量

只承认这几组 usage 作为**百分比来源**：

| UsagePage:Usage | 名称 |
|---|---|
| `0x06:0x20` | Battery Strength |
| `0x0D:0x3B` | （蓝牙 HID 电量） |
| `0x85:0x0065` | Battery Percentage |

**明确不作为百分比来源**：

| UsagePage:Usage | 为什么不认 |
|---|---|
| `0x85:0x0001` | Battery System Status —— 位域（状态位），不是百分比 |
| `0x85:0x0066` | Battery Time Remaining —— 时间量，不是百分比 |

取值规则：

1. 用 `HidP_GetUsageValue` **按描述符解析**，不猜字节偏移；
2. 按 value caps 的 `LogicalMin` / `LogicalMax` 换算成百分比；
3. **换算结果必须落在 0..100，否则判未知**（不做任何外推）。

---

## 6. L3：厂商私有协议表（数据驱动）

### 6.1 一条记录的字段（纯数据、可序列化）

| 字段 | 含义 |
|---|---|
| `Key` | 记录唯一键（VID:PID + UsagePage:Usage），匹配与日志用 |
| `Vendor` / `Model` | 厂商名 / 型号（显示用） |
| `Kind` | 设备类型（mouse / keyboard / headset / …，沿用 `DeviceService.Glyph` 的取值） |
| `Vid` / `Pid` | USB 厂商 ID / 产品 ID（JSON 里写整数） |
| `UsagePage` / `Usage` | 匹配的 HID 集合（厂商私有集合一般在 `0xFFxx`） |
| `MinReportLength` | 该集合的最小报告长度；不足则不发命令 |
| `Transport` | `control` / `interrupt` / `feature`；三种都要试的场景填 `auto` |
| `PreCommands` | 命令前的准备序列（逐条发，可为空） |
| `Command` | 命令帧（字节数组） |
| `Echo` | 回显字节序列，**必须出现在回包里**才算这条命令被设备认了 |
| `ValueOffset` / `ValueMask` | 电量值在回包中的偏移与掩码 |
| `ValueMin` / `ValueMax` | 换算用的值域（通常 0..100） |
| `ChargingOffset` / `ChargingMask` / `ChargingIsOne` | 充电位的位置、掩码，以及「哪个值算正在充电」 |
| `MilliVoltsOffset` / `MilliVoltsMask` / `MilliVoltsScale` | 毫伏读数（备用：部分设备只报电压） |

### 6.2 解析：三层校验

`@
读回包 → ① 回显匹配：Echo 字节序列必须出现在回包里
       → ② 范围校验：按 ValueMin/ValueMax 换算后必须落在 0..100
       → ③ 充电位：只认明确的 0 / 1，其它值当未知
`@

**任何一层不过 → 返回「未知」**。
**绝不用 0 或默认值充数**（这条与既有 `DeviceInfo.ParsePercent` 的口径一致：
固件常用 255 表示「未知」，超出 0..100 一律当未知）。

### 6.3 表的来源与装载

协议表从 `%LOCALAPPDATA%\MyIsland\peripheral-protocols.json` 装载。
用户 / 维护者**填表即可新增品牌，不用改代码**；表文件不存在或解析失败时按「没有 L3 能力」处理
（不报错、不影响其它层）。

---

## 7. L4：通用识别兜底

L4 只回答「这台设备在不在」，不读电量。

- 按 **VID:PID 分组**：一台接收器会露出多个集合，设备列表里只占**一行**。
- 识别依据：名称关键词（`2.4G` / `Wireless` / `Receiver` / `Dongle` / `无线` / `接收器`）
  与厂商自定义集合（Usage page ≥ `0xFF00`）。
- 电量显示**「未知」**：复用既有 `DeviceInfo.LevelText = "未知"` 语义，
  与 XInput 的档位呈现同一套规则（`DevicesPage.cs`：`LevelText == "未知"` → 「电量未知」，配色走 `pal.TextDim`）。

---

## 8. 真机逆向记录（2026-11-26 本机实测）

本机：Windows 11 x64，设备：一台**联想 2.4G 接收器**。

### 8.1 设备标识

| 项 | 值 |
|---|---|
| 实例 ID | `USB\VID_17EF&PID_F006` |
| 产品名 | `Idea5003` |
| 厂商字符串 | `ZEPHYR` |
| 序列号 | `0123456789ABCDEF`（**占位值**，不是真实 SN） |

### 8.2 三个 HID 顶层集合

| 集合（UsagePage:Usage） | in | out | feat | value caps |
|---|---|---|---|---|
| `FFCF:00A0` | 17 | 137 | 61 | feature 报告 ID 193（usage `FFCF:0062`）；input 报告 ID 193（usage `FFCF:0066`）与 194（usage `FFCF:008A`） |
| `FF50:0003` | 0 | 0 | 17 | 无 value caps |
| `00FF:0000` | 256 | 256 | 256 | feature 报告 ID 83 与 84；input 报告 ID 81 |

三个集合共享同一个 VID:PID（同一台物理接收器），按 §7 的分组规则只占一行。

### 8.3 特征报告原始字节

相对**报告 ID** 的偏移（偏移 0 就是报告 ID 本身）：

| 偏移 | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 其余 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 值 | `0xC1`(193) | `0x01` | `0x00` | `0x00` | `0x02` | `0x3A`(58) | `0x00` | `0x00` | `0x01` | `0xB0`(176) | `0x01` | `0x03` | `0x50`(80) | 0 |

- **连续读取 6 秒内容完全不变** → 这些是静态字段（不是随时间跳变的动态值）。
- 另一个集合的 feature 报告 ID 84 内容相同，**只有 `off[1]` 不同（`0xA0`）**
  → 说明 **`off[1]` 是设备/集合选择字节**，其后是**共用状态体**。

### 8.4 独立参照

- 系统缓存的同型号**蓝牙**鼠标（`ThinkBook Bluetooth Mouse`，实例 ID 段 `VID_0217EF_PID_6186`），
  蓝牙电量属性值 = **59%**，缓存时间戳 **2026-11-07**。
  （实例 ID 里的 `0217EF` 是蓝牙地址段，**不是 USB VID 字段**；此处按原样引用。）
- 接收器状态体里的 **58** 与之只差 **1 个百分点**，方向也符合「闲置鼠标 18 天掉 1%」的衰减。
- 另一个候选值 **80** 与该参照矛盾（差 21 个百分点，且无衰减解释）。

### 8.5 结论的定性 —— **按「推断值」落地，并保留改表即改行为的通道**

| 候选项 | 状态 | 证据 | 处置 |
|---|---|---|---|
| `off[5] = 58` 是**电量百分比** | **推断成立（未做同刻交叉验证）** | 与蓝牙缓存值 59 只差 **1pp**，方向也符合「闲置鼠标 18 天掉 1%」的衰减；另一候选 80 与该参照矛盾 21pp | 协议表 `lenovo-idea5003` 按 `ValueOffset = 5` 落地 |
| `off[12] = 80` 的含义 | **未知** | 无独立参照可解释（既不像百分比，也不像电压档位） | 按「未知字段」忽略 |

**2026-11-26 的确认尝试与结果**：当场请用户核对鼠标当前电量，但**鼠标未开机、不方便查**，
因此**没有拿到同刻交叉验证**。按「不伪装」原则的处理方式：

- **不因为"拿不准"就假装没有这条能力**：58 有独立参照支撑（同一台鼠标、同一时间窗、1pp 差），
  且 80 与参照矛盾；按推断值落地比永远显示「未知」更贴近事实。
- **但保留纠错通道**：协议表是数据驱动的，`%LOCALAPPDATA%\MyIsland\peripheral-protocols.json`
  能覆盖内置条目 —— 若发现偏移 5 不对，改成偏移 12 即可，**不用改代码、不用发版**。
  （这条通道 2026-11-26 经独立审查实测后才真正成立：原先 `All` 是"内置在前、用户在后"，
  `Match` 命中即返回 ⇒ **用户条目永远不生效**，承诺是死的。已改为"用户表在前、内置表在后"，
  并补了用例；反向对照验证：把顺序改回去，那几条用例会立刻变红。
  用户条目字段不全导致匹配不上时，仍回退内置条目 —— 不因为一条写歪的用户条目把设备弄丢。）
- **读不到仍然是「未知」**：接收器没连上设备时该字段是 `0xFF`（255，超出 0..100），
  协议表的三层校验会把它判为无效 → 界面显示「未知」，绝不会显示 0%。

---

## 9. 接线与验收

### 9.1 接线点

- 新增目录 `src/Island.App/Services/Peripheral/`（2026-11-26 已全部落地）：
  · 访问层：`HidNative.cs` / `HidModels.cs` / `HidBus.cs` / `HidChannel.cs`
  · 纯规则：`PeripheralReading.cs` / `PeripheralProtocol.cs`（协议表 + 装载 + 三层校验）/
    `StandardHidBattery.cs`（L1）/ `PeripheralClassifier.cs`（L4）
  · 服务层：`PeripheralBatteryService.cs`（L1→L3→L4 编排 + 缓存 + 诊断日志）
- 由 `DeviceService` 的采样节拍驱动：**复用既有 30s / 300s 节拍**（`RetuneTimer`），
  诊断日志复用既有 `LogLine` / `LogLineThrottled`（同一个 `%LOCALAPPDATA%\MyIsland\thermal-diag.log`）。
- 结果并入 `DeviceService.Snapshot`；设备页渲染沿用既有 `DeviceInfo` 语义
  （档位 → `LevelText`，百分比 → `Percent`）。

### 9.2 去重

- 同一物理外设（VID:PID）只出现一条。
- 与 BLE / MSBatteryClass 已报的同名设备要去重（同一台设备的蓝牙版与 2.4G 版不重复列）。

### 9.3 验收标准

1. `dotnet build Island.sln -c Release`：**0 警告 0 错误**。
2. `Island.Tests`：**全过**，退出码 0。
3. `src/Island.Avalonia/**` **零改动**（`git diff` 为空）。
4. **读不到就是「未知」**，界面上不得出现假读数（不得用 0 / 默认值 / 猜测值充数）。
5. 新增的**纯规则部分**（协议表解析、三层校验、usage 白名单、去重、帧长补齐）要有单元测试。

### 9.4 本机实测结果（2026-11-26）

| 项 | 结果 |
|---|---|
| 构建 | `dotnet build Island.sln -c Release` → **0 警告 0 错误** |
| 测试 | `Island.Tests` → **398 条全过**（其中新增 42 条外设电量用例），退出码 0 |
| mac 侧 | `git status -- src/Island.Avalonia` → **空**（零改动） |
| 端到端 | 直接跑生产代码 `PeripheralBatteryService`：枚举 → 分组 → 命中协议表 → 读特征报告 → 解析 → 得 `联想 Idea5003 2.4G 接收器 58%` |
| 独占集合 | 本机 9 个 HID 集合全部枚举到；被系统独占的键鼠集合用 `dwDesiredAccess=0` 正常读到属性与能力表 |

---

## 10. 风险与「明确不做的事」

### 10.1 明确不做

- **不写没有硬件的厂商协议** —— 没有实机回包验证的协议不写进表（表里的每条记录都必须有实机证据）。
- **不做 USB 抓包式的破坏性写入** —— 只发协议表里登记过的命令，不做「乱发一串看设备动不动」的探索式写入。
- **不装驱动、不需要管理员权限** —— 全部走用户态 HID 访问层。
- **不伪造精确百分比** —— XInput 只有 4 档就显示 4 档（`LevelText`），
  读不到就显示「未知」，不换算。
- **不改 mac 侧** —— `src/Island.Avalonia/**` 零改动。

### 10.2 风险

| 风险 | 处置 |
|---|---|
| 设备休眠 / 断连时暂时读不到 | 下一轮节拍恢复；读不到就不显示数值（沿用既有「缓存新鲜期」策略） |
| 独占集合打不开（ERROR_ACCESS_DENIED=5） | 枚举用 `dwDesiredAccess=0`；发命令失败即放弃这条链路 |
| 错误命令可能让设备异常 | 只发登记过的命令；命令失败/超时立即放弃，不重试轰炸 |
| `HIDP_VALUE_CAPS` 结构体写错 | 已固化为 72 字节显式布局，改动需同步（§4.4） |
| 协议表填错导致假读数 | 三层校验（§6.2）+ 表文件可随时删除回退到 L1/L4 |
| 电量语义未经厂商确认（只拿到推断值 58） | §8.5：按推断值落地 + 用户协议表可覆盖纠错 + 读不到即「未知」 |

---

## 11. 待验证清单

| # | 待验证项 | 验证方式 | 影响 |
|---|---|---|---|
| 1 | 联想接收器 `off[5] = 58` 是否为电量百分比 | **已按推断落地**（证据链见 §8.5）。补验方式：鼠标开机时同时读 BLE GATT 电量与接收器特征报告，两者应一致 | 若证伪，用用户协议表把偏移改成 12 即可（不用改代码） |
| 2 | `off[12] = 80` 的含义 | 多时点采样 + 厂商文档 | 目前按「未知字段」忽略 |
| 3 | `off[1]`（`0x01` / `0xA0`）作为集合选择字节的通用性 | 在其它厂商设备上验证是否同样成立 | 决定表里是否需要「选择字节」字段 |
| 4 | 能否对联想接收器**写入**命令读到实时状态（现在读到的可能是静态缓存值） | 实机写入一次状态查询命令，观察数值是否随电量变化 | 决定这条链路是「实时值」还是「缓存值」 |
| 5 | L1 的 `0x0D:0x3B` usage 在真实设备上的表现 | 需要一台规范实现该 usage 的设备 | 目前仅按标准采信，未实机验证 |
| 6 | `HidP_GetUsageValue` 在 input 报告上的解析是否覆盖所有厂商 | 实机比对 | 决定 L1 覆盖率 |

> 第 1 项已按「推断值」落地（见 §8.5），其余各项在拿到结论前，相关代码路径都只能走「未知」分支，不得预填数值。
