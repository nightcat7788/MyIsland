# macOS 对齐：外设电量（2.4G 接收器）+ CPU 温度取值口径

日期：2026-11-26
状态：设计已定；Windows 侧已完成（`d1820e2` / `a2bee97`），mac 侧实施中
范围：`src/Island.Avalonia/`（mac 侧）+ `tools/device-battery/` + `tools/thermal-probe/` + 共享层
参照：Windows 侧规格 `2026-11-26-windows-peripheral-battery-design.md` 与 `2026-11-26-cpu-temperature-picker-design.md`

---

## 1. 背景：两端现在的差距

| 能力 | Windows | macOS（改动前） |
|---|---|---|
| 2.4G 接收器电量（厂商私有 HID 协议） | ✓ L3 协议表，联想 Idea5003 实测 58% | ✗ 只有蓝牙 HID 的 `BatteryPercent` + 内置电池 |
| 标准 HID 电量 usage | ✓ L1 | ✗ |
| 只识别不读数（显示「未知」） | ✓ L4 | ✗ |
| CPU 温度取值 | Package 优先、排除 `Distance to TjMax` 等干扰项（`CpuTemperatureRule`） | 把 `PMU tdie`/`pACC`/`eACC` **求平均** |
| 设备类型词表 | mouse/keyboard/headset/gamepad/battery/ble/other | 探针直出中文（蓝牙/HID/内置） |

「两端口径一致」在总纲 §1 里是硬要求：**同一功能行为一致**。
2.4G 接收器读电量**不是平台专属能力**（macOS 的 IOHIDManager 同样能枚举 HID 设备、读 Feature 报告），
所以这一项要**真对齐**，不是「公开差异后各做各的」。

---

## 2. 核心架构：探针只搬运，判定在共享层

这是本设计最重要的一条 —— 否则两端会长出两套会漂移的实现。

```text
mac 原生探针 (Swift)              共享层 (C#, 两端同一份)           Windows
────────────────────────────────────────────────────────────────────────
island-devices                    PeripheralProtocols（协议表）       HidBus/HidChannel
 · 读蓝牙 HID / 内置电池  ──▶     PeripheralProtocolReader（三层校验） ◀─ L3 读命令回包
 · 枚举 HID 设备          ──▶     StandardHidBattery（usage 白名单）  ◀─ L1 用 HidP 解析
 · 读 Feature 报告原始字节 ──▶   PeripheralClassifier（识别兜底）    ◀─ L4
 · 读报告描述符           ──▶     HidReportDescriptor（描述符解析）
   （**不做任何判定**）             └─ 两端共用同一份规则
```

- **协议表**（VID/PID/集合/报告 ID/偏移/掩码/值域）是纯数据，两端共用同一份 C# 定义
  （`src/Island.App/Services/Peripheral/PeripheralProtocol.cs`，mac 工程按既有先例链它 ——
  和 `ThermalService.cs`/`Settings.cs` 同一个模式）。
- **用户协议表**：两端同一个文件名、同一份语义 —— `<数据目录>/peripheral-protocols.json`
  （数据目录 = Windows 的 `%LOCALAPPDATA%\MyIsland`、mac 的 `~/Library/Application Support/MyIsland`
  —— .NET 的 `SpecialFolder.LocalApplicationData` 在两端就是这个语义 ✓）。
- **探针不做判定**：它只搬运「设备身份 + 报告描述符 + Feature 报告原始字节」，
  绝不判断「哪个字节是电量」—— 那套规则在共享层里，与 Windows 端同一份代码。

---

## 3. 探针输出契约

每行一条，TAB 分隔。已有那类行保持不变：

```text
<类型>\t<名称>\t<电量%>\t<充电中 0|1>\t<设备ID>
```

新增两类（**USB / 2.4G 接收器的原始信息**）：

```text
HIDDEV\t<vid>\t<pid>\t<usagePage>\t<usage>\t<产品名>\t<序列号>\t<描述符hex>
HIDFTR\t<vid>\t<pid>\t<reportId>\t<报告字节hex>
```

- `vid`/`pid`：4 位十六进制大写无前缀；`usagePage`/`usage`：十六进制大写不补零。
- `报告字节hex` 的 **第 0 字节是报告 ID**（与 Windows `HidD_GetFeature` 同一约定 ——
  协议表里的偏移量是相对「含报告 ID 的整包」算的，漏掉这个字节会整体错位）。
- 描述符 hex 为空串时，该行以 TAB 结尾（字段数不变）。

---

## 4. mac 侧的三层判定（与 Windows 端同序）

顺序：**L1 → L3 → L4**，先命中先用（与 `PeripheralBatteryService.ProbeGroup` 一致）。

### L1 标准 HID 电量 usage

macOS 没有 `HidP_GetCaps` / `HidP_GetUsageValue`，所以共享层新增
**最小 HID 报告描述符解析器**（`HidReportDescriptor`）：

- 解析报告描述符（item 列表）→ 每个字段的（报告 ID、报告类型、usage page/usage、
  **位偏移**、位宽、逻辑值域）；
- 在 Feature 报告的字段里找电量 usage（`0x06:0x20` / `0x0D:0x3B` / `0x85:0x0065`）；
- 按位偏移从原始报告里取值（报告第 0 字节是报告 ID，字段偏移要 +8 位）；
- 按 LogicalMin/Max 换算百分比；**描述符没声明值域 ⇒ 不换算**（未知就是未知）。

### L3 厂商私有协议

- 用共享的 `PeripheralProtocols.Match(vid, pid, usagePage, usage, reportLength)` 匹配；
- 取 `FeatureReportId` 对应的那份 Feature 报告原始字节；
- 用共享的 `PeripheralProtocolReader.TryParse` 解析 —— **三层校验**（回显 / 值域 / 充电位），
  与 Windows 端同一段代码；
- 显示名用共享的 `PeripheralProtocols.DisplayName`（厂商 + 型号），两端列表才对得上。

### L4 只识别不读数

- 协议匹配上了但这次读不到 → 显示**「未知」**（不是 0%）；
- 没匹配任何协议、但设备看起来是无线外设（共享 `PeripheralClassifier.LooksWireless`）→ 也显示「未知」。

---

## 5. 「未知」怎么表示（两端一个语义）

Windows 的 `DeviceInfo` 用 `LevelText`（非 null 时界面显示文字而不是百分比）。
mac 的 `DeviceBattery.Device` 补一个同义的 `LevelText`，设备页渲染对齐：

| 情形 | 环 | 大字 | 状态行 |
|---|---|---|---|
| 有百分比 | 按百分比 | `58%` | 充电中 / 电量低 / 电量偏低 / 正常 |
| 只识别到、读不到电量 | 空环（暗色） | `未知` | `电量未知` |

⚠️ 「未知」必须排在百分数判断**前面**（Windows 端已经因为这个顺序问题被审查抓过一次：
`StatusText` 里 `ObservedAt` 排在 `LevelText == "未知"` 前面，导致「未知」分支是死代码）。

---

## 6. CPU 温度口径对齐（②）

Windows 端已把 CPU 温度改成「**选一个** + 排除干扰项」（规格 `2026-11-26-cpu-temperature-picker-design.md`）：
封装/Package 优先，Tdie 优先于 Tctl，`Distance to TjMax` 这类**距离不是温度**的必须排除。

mac 端原来把 `PMU tdie`（CPU die 温度）、`pACC MTR Temp Sensor`（P 核簇）、
`eACC MTR Temp Sensor`（E 核簇）**求平均** —— 同一颗 CPU 两端会显示不同数字。

改成**按优先级选一个**：

1. `PMU tdie`（CPU die 温度 —— 与 Windows 的 Package/Tdie 同一类口径）
2. 名字含 `tdie`
3. `pACC MTR Temp Sensor`
4. `eACC MTR Temp Sensor`
5. 兜底：SMC 的 `Tp*` 平均（维持现状）

**永远排除**：名字含 `distance`/`tjmax`/`gpu`/`fan`/`airflow`/`smc`/`proximity`/`virtual`/`liquid`/`coolant`/`pump`/`vrm`/`soc`。

探针输出格式 `CPU=<c> GPU=<c> SSD=<c>` **一个字不改**（C# 侧按它解析）；
选中了哪个传感器写 stderr（只作诊断）。

---

## 7. 设备类型词表统一

两端用同一套 kind：`mouse` / `keyboard` / `headset` / `gamepad` / `battery` / `ble` / `other`。
mac 侧原来的中文 kind（蓝牙/HID/内置）由共享的 `PeripheralClassifier.KindOf` 取代，
设备页按同一套词表渲染（mac 侧没有鼠标/键盘的矢量图标，非电池设备仍用圆盘 —— 视觉差异属平台专属）。

---

## 8. 验证与已知限制

| 项 | 状态 |
|---|---|
| 共享层（协议表 / 描述符解析 / 三层校验 / 识别兜底） | **本机可验证**：纯逻辑 + 单元测试 |
| mac C# 接线（`DeviceBattery.cs` / 设备页渲染） | **本机可验证**：`dotnet build Island.sln` 0 警告 0 错误 |
| mac 原生探针（Swift / ObjC） | ⚠️ **本机无法编译**（没有 macOS SDK）：代码由用户在 Mac 上编译验证 |
| 真机行为 | ⚠️ 同上：探针的枚举/读取结果需在 Mac 上核对 |

**这也是本阶段最大的风险点**：原生探针的正确性只能靠用户在 Mac 上验证。
因此探针代码要求：① 只用已验证过的 IOKit API；② 不确定处在注释里写明；③ 读不到就跳过、绝不崩。

---

## 9. 验收标准

1. `dotnet build Island.sln -c Release`：**0 警告 0 错误**（含 mac 工程）。
2. `Island.Tests`：**全过**，退出码 0（含描述符解析器的新增用例）。
3. **mac 侧源码不再是「另一套规则」**：协议表、三层校验、usage 白名单、识别兜底、类型词表
   全部来自共享层（`src/Island.App/Services/Peripheral/`），mac 工程按既有先例链接。
4. 探针输出契约与 §3 一致；**探针里没有「哪个字节是电量」的判断**。
5. 「未知」在两端都是「未知」（不显示 0%）。
6. 用户协议表在两端同一文件名、同一语义（`peripheral-protocols.json`）。
7. `src/Island.App/**` 与 `src/Island.Avalonia/**` 之外无意外改动。

---

## 10. 明确不做

- **不改 Windows 侧的判定规则**（共享层动了，Windows 跟着走 —— 这正是「一份规则」的意义）。
- **不在 mac 侧实现「命令→回包」型协议**：mac 的 IOHIDManager 读 Feature 报告足够覆盖
  「厂商把状态挂在 Feature 报告里」这类（联想 Idea5003 就是），
  真需要发命令的协议等有 mac 真机再单独设计。
- **不给 mac 补 SMN 直读之类的平台专属能力**。
- **不动展开面板页签顺序**（那是总纲阶段 2 的范围）。
