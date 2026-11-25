# Windows 外设电量（HID 访问层 + L1/L3/L4）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (` - [ ] `) syntax for tracking.

**Goal:** 把 Windows 版的外设电量从「四条系统数据源」扩到「分层探测 + 数据驱动协议表」：新增 HID 访问层（L1 标准 usage / L3 厂商私有协议表 / L4 通用识别兜底），让 2.4G 接收器这类设备能被读到电量；**读不到就是「未知」，绝不用 0 或默认值充数**。

**Architecture:** 新增目录 `src/Island.App/Services/Peripheral/`：
访问层（`HidNative` / `HidModels` / `HidBus` / `HidChannel`，工作区已有、未入仓）+
纯规则部分（协议表模型、协议表装载、回包解析三层校验、L4 识别与去重，**全部可链进零 NuGet 测试工程**）+
服务层（`PeripheralBatteryService`，把 L1/L3/L4 的结果并成 `DeviceInfo`）。
采样节拍、诊断日志、快照发布全部复用既有 `DeviceService`（30s / 300s、`LogLine` / `LogLineThrottled`），不新起炉灶。

**Tech Stack:** C# / .NET 8 (net8.0-windows10.0.19041.0) / WPF / Win32 P/Invoke（setupapi.dll、hid.dll、hidclass.dll）/ 自研零依赖测试 harness（`Island.Tests`，`Check` 失败即记录、退出码非 0）/ `dotnet` 8.0.428 位于 `D:\YQDESK\.dotnet\dotnet.exe`

## 实现回填（2026-11-26，Task 1–9 全部完成）

| 项 | 结果 |
|---|---|
| 新增源码 | `src/Island.App/Services/Peripheral/`：`HidNative.cs`、`HidModels.cs`、`HidBus.cs`、`HidChannel.cs`、`PeripheralReading.cs`、`PeripheralProtocol.cs`、`StandardHidBattery.cs`、`PeripheralClassifier.cs`、`PeripheralBatteryService.cs` |
| 接线 | `DeviceService.Sample()` 调 `PeripheralBatteryService.Warm()`（复用既有 30s / 300s 节拍），快照经 `MergeSources` 合并去重；`DevicesPage` 补 `other` 类型文案 |
| 测试 | 新增 `tests/Island.Tests/PeripheralBatteryTests.cs`（42 条）；`Island.Tests` 共 **398 条全过**，退出码 0 |
| 构建 | `dotnet build Island.sln -c Release` → **0 警告 0 错误** |
| mac 侧 | `git status -- src/Island.Avalonia` → **空** |
| 侦察工具 | `tools/hid-probe/`（独立工程，不进 `Island.sln`）：枚举 / 特征报告扫描 / 逐字节偏移 / 端到端跑生产代码 |
| 端到端实测 | `联想 Idea5003 2.4G 接收器 58%`（走生产代码全链路：枚举 → 分组 → 命中协议表 → 读特征报告 193 → 偏移 5 → 三层校验） |
| 独立审查修复 | 独立审查提 4 条 P1 + 7 条 P2，已修：① **用户协议表覆盖内置条目**（覆盖通道原本是死的）② `Read` 的 keepAlive 分支泄漏 GCHandle ③ `WriteOverlapped` 在写请求仍挂起时就释放 pin ④ `HidP_GetValueCaps` 丢弃状态码 + 解析未初始化内存；另修"标准 HID 电量只查 Feature 报告""query-only 句柄读无超时""<Pad> 静默截断超长帧""用户表只装载一次""设备去重吃掉了同名的第二台真设备""'电量未知'状态行是死代码"。审查新增 16 条用例，并做了反向对照（把覆盖语义改回去 → 用例立刻变红） |

### 与本计划初稿的两处偏离（以实现为准）

1. **联想 L3 条目按「推断值」启用**：初稿要求「语义确认前保持关闭、不得预填 58」。
   实际做法是**按推断值启用**（证据链见规格 §8.5：与同型号蓝牙鼠标的系统缓存电量 59% 差 1pp、
   方向符合闲置衰减；另一候选 80 与该参照矛盾），同时保留纠错通道：
   `%LOCALAPPDATA%\MyIsland\peripheral-protocols.json` 可覆盖内置条目，改偏移不用改代码。
   读不到（字段为 `0xFF`）仍按三层校验判为无效 → 界面显示「未知」。
2. **协议表增加 `FeatureReportId` 字段**：不少厂商把配对设备状态挂在 Feature 报告里而不回命令
   （联想就是如此），没有这个字段就永远读不到。`FeatureReportId ≥ 0` 表示「直读该 Feature 报告解析」，
   否则走「发命令 → 收回包」模式。

---

## Global Constraints

- **规格来源**：`docs/superpowers/specs/2026-11-26-windows-peripheral-battery-design.md`（下称「规格」）。本计划的每条实现口径都能在规格里找到出处。
- **mac 侧零改动**：`src/Island.Avalonia/**` 的 `git diff` 必须为空。
- **不改既有四条系统数据源**：WMI Win32_Battery / MSBatteryClass / BLE GATT / XInput **一行不改**（规格 §3 的 L2 层）。
- **读不到就是「未知」**：不得用 0、默认值、上一次的猜测值充数；沿用既有 `DeviceInfo.ParsePercent` 的口径（超出 0..100 一律当未知）。
- **不伪造精确百分比**：XInput 只有 4 档就显示 4 档（`LevelText`），与既有档位语义同源。
- **纯规则优先**：协议表模型 / 装载 / 解析校验 / L4 识别去重必须是**纯托管、无 Win32 依赖**的文件，
  这样才能链进零 NuGet 的 `Island.Tests` 断言（既有先例：`DeviceInfo.cs` / `XInputService.cs` 已被链接）。
- **构建门禁**：`dotnet build Island.sln -c Release` 必须 **0 警告 0 错误**。
- **测试门禁**：`Island.Tests` 必须全过，且退出码为 0。
- **命令统一用**：`D:\YQDESK\.dotnet\dotnet.exe`（系统 dotnet 无 SDK）。
- **编码**：源码与文档用 UTF-8 无 BOM、**LF 行尾**。
- **不得顺手改**：mac 侧、展开面板排版、收起态胶囊、既有设备页版式（只在本阶段必要的语义处动）。
- **不做**：不写没有硬件的厂商协议；不做探索式的破坏性写入；不装驱动、不要求管理员权限。
- **文件名以仓内实际为准**：纯逻辑文件最终落在
  `Peripheral/PeripheralProtocol.cs`（协议表 + 装载 + 三层校验）、
  `Peripheral/StandardHidBattery.cs`、`Peripheral/PeripheralClassifier.cs`、
  `Peripheral/PeripheralReading.cs`；测试文件为 `tests/Island.Tests/PeripheralBatteryTests.cs`；
  去重落在 `DeviceService.MergeSources`（按显示名归一化后去重）。

---

### Task 1: HID 访问层定型并入仓（实机结论固化成断言）

**Files:**
- Add（工作区已有，未入仓）: `src/Island.App/Services/Peripheral/HidNative.cs`、`HidModels.cs`、`HidBus.cs`、`HidChannel.cs`
- Modify: `tests/Island.Tests/Island.Tests.csproj`（把纯规则源文件链接进测试工程）
- Create: `tests/Island.Tests/PeripheralBatteryTests.cs`
- Modify: `tests/Island.Tests/Program.cs`（注册新测试）

**Interfaces:**
- Consumes: 无（全新文件）
- Produces:
  - `internal static class HidNative`：P/Invoke + `HidCaps` / `HidValueCaps`（**72 字节显式布局**）/ `ReadValueCaps` / `TryGetUsageValue`
  - `internal sealed record HidUsageRange(ushort UsagePage, ushort UsageMin, ushort UsageMax, byte ReportId, bool IsRange, int LogicalMin, int LogicalMax)`
  - `internal sealed class HidInterface`：Path / InstanceId / Product / Manufacturer / Serial / Vid / Pid / UsagePage / Usage / 三种报告长度 / FeatureUsages / InputUsages / `Key`
  - `internal static class HidBus`：`Enumerate()`（带缓存）/ `EnumerateFresh()` / `Invalidate()` / `GroupByDevice()` → `List<HidGroup>`
  - `internal sealed class HidChannel`：`WriteControl` / `WriteInterrupt` / `WriteFeature` / `GetFeature` / `Read(timeoutMs)` / `TryReadUsageValue`

- [x] **Step 1: 写失败的测试（把实机踩坑固化成断言）**

新建 `tests/Island.Tests/PeripheralBatteryTests.cs`：

`@
// 关键断言（都是实机踩过的坑，写成注释锁住原因）：
// · HidValueCaps 必须是 72 字节：少写一个 USHORT → 60 字节 → 驱动按 72 写 → 堆损坏
// · HidP_GetValueCaps 的返回值是「取到的条数」而不是状态码
// · x64 下 SetupDiGetDeviceInterfaceDetail 的 cbSize = 8，DevicePath 在偏移 4
check("HIDP_VALUE_CAPS 结构体 = 72 字节（60 字节会导致驱动堆损坏）",
    Marshal.SizeOf<HidNative.HidValueCaps>() == 72);
check("HidCaps 结构体 = 64 字节", Marshal.SizeOf<HidNative.HidCaps>() == 64);
check("HidAttributes 结构体 = 12 字节", Marshal.SizeOf<HidNative.HidAttributes>() == 12);
check("x64 下 DeviceInterfaceDetail 的 cbSize = 8", IntPtr.Size == 8 ? 8 : 6);
check("DevicePath 从偏移 4 开始", true);   // 常量固化在 HidBus 里
check("L1 只认三组 usage 作百分比来源", StandardHidBattery.PercentUsages.Count == 3);
check("L1 排除 0x85:0x0001（状态位域）", !StandardHidBattery.PercentUsages.Contains((0x85, 0x0001)));
check("L1 排除 0x85:0x0066（剩余时间）", !StandardHidBattery.PercentUsages.Contains((0x85, 0x0066)));
check("L1 承认 0x06:0x20 / 0x0D:0x3B / 0x85:0x0065",
    StandardHidBattery.PercentUsages.Contains((0x06, 0x0020))
    && StandardHidBattery.PercentUsages.Contains((0x0D, 0x003B))
    && StandardHidBattery.PercentUsages.Contains((0x85, 0x0065)));
`@

- [x] **Step 2: 运行测试，确认失败**

`@
& 'D:\YQDESK\.dotnet\dotnet.exe' build 'D:\YQDESK\MyIsland-src\tests\Island.Tests\Island.Tests.csproj' -c Release --nologo
`@
预期：编译失败（`HidNative` / `StandardHidBattery` 尚未链接进测试工程）。

- [x] **Step 3: 校对访问层实现与规格 §4 对齐**

逐条核对（**任何一条对不上就以规格为准改实现**）：

1. 枚举：`SetupDiGetClassDevs` → `SetupDiEnumDeviceInterfaces` → `SetupDiGetDeviceInterfaceDetail`；
   x64 下 `cbSize = 8`、`DevicePath` 偏移 4。
2. 枚举阶段一律 `dwDesiredAccess = 0` 打开（`OpenForQuery`）；读写权限只在发命令时用（`OpenForIo`）。
3. 能力表：`HidD_GetPreparsedData` + `HidP_GetCaps`（UsagePage/Usage + 三种报告长度）
   + `HidP_GetValueCaps`（usage 范围、报告 ID、逻辑范围）；
   **判断成功看写回的 length 参数，不看返回值**。
4. 三种传输都要实现：`HidD_SetOutputReport` / `WriteFile`（中断 OUT）/ `HidD_SetFeature`。
5. 读：`FILE_FLAG_OVERLAPPED` + `WaitForSingleObject` 超时 + `CancelIoEx`；超时后不释放挂起的缓冲区。
6. 帧长：补齐到集合声明长度；**超长拒绝，不截断**。
7. 单个集合失败只跳过它自己；枚举有预算上限（1400ms）与接口数上限（512）。

- [x] **Step 4: 把源文件链接进测试工程并注册用例**

在 `Island.Tests.csproj` 的 `<ItemGroup>` 里追加（注意：只链**纯规则**文件，
访问层里依赖 WMI/WinRT 的不链）：

`@
<Compile Include="..\..\src\Island.App\Services\Peripheral\HidModels.cs" Link="Merged\App\HidModels.cs" />
<Compile Include="..\..\src\Island.App\Services\Peripheral\HidNative.cs" Link="Merged\App\HidNative.cs" />
<Compile Include="..\..\src\Island.App\Services\Peripheral\StandardHidBattery.cs" Link="Merged\App\StandardHidBattery.cs" />
`@

在 `Program.cs` 追加：`PeripheralHidTests.Run(Check);`

- [x] **Step 5: 运行测试，确认通过**

预期：新增断言全过（构建仍是 0 警告 0 错误）。

---

### Task 2: 协议表数据模型 + JSON 装载（纯规则）

**Files:**
- Create: `src/Island.App/Services/Peripheral/PeripheralProtocol.cs`（记录模型 + 字段语义）
- Create: `src/Island.App/Services/Peripheral/PeripheralProtocol.cs`（装载 / 查找 / 匹配）
- Create: `tests/Island.Tests/PeripheralProtocolTests.cs`
- Modify: `tests/Island.Tests/Island.Tests.csproj`、`tests/Island.Tests/Program.cs`

**Interfaces:**
- Consumes: Task 1 的 `HidInterface`（集合匹配用）
- Produces:
  - `internal sealed record PeripheralProtocol`（规格 §6.1 的全部字段，纯数据）
  - `internal static class PeripheralProtocolTable`：
    - `static PeripheralProtocolTable Load(string json)`（解析失败返回空表，不抛）
    - `static PeripheralProtocolTable LoadFromDefaultLocation()`（`%LOCALAPPDATA%\MyIsland\peripheral-protocols.json`，不存在返回空表）
    - `static PeripheralProtocol? Match(HidInterface it)`（按 Vid/Pid + UsagePage/Usage 匹配）

- [x] **Step 1: 写失败的测试**

`@
// 字段齐全性：规格 §6.1 的 23 个字段一个都不能少
var proto = new PeripheralProtocol(
    Key: "17EF:F006/FFCF:00A0", Vendor: "Lenovo", Model: "Idea5003", Kind: "mouse",
    Vid: 0x17EF, Pid: 0xF006, UsagePage: 0xFFCF, Usage: 0x00A0,
    MinReportLength: 17, Transport: PeripheralTransport.Feature,
    PreCommands: Array.Empty<byte[]>(), Command: new byte[] { 0xC1, 0x01 },
    Echo: new byte[] { 0xC1, 0x01 },
    ValueOffset: 5, ValueMask: 0xFF, ValueMin: 0, ValueMax: 100,
    ChargingOffset: 4, ChargingMask: 0x02, ChargingIsOne: true,
    MilliVoltsOffset: -1, MilliVoltsMask: 0, MilliVoltsScale: 1);

check("协议表记录字段齐全", proto.ValueOffset == 5 && proto.ChargingOffset == 4 && proto.MilliVoltsOffset == -1);
check("JSON 能反序列化出协议记录", ...);
check("JSON 坏掉时装载返回空表而不是抛异常", PeripheralProtocolTable.Load("{ not json").Count == 0);
check("表文件不存在时返回空表", PeripheralProtocolTable.LoadFromDefaultLocation() != null);
check("VID/PID 不匹配的记录不会被选中", MatchReturnsNull(proto, vid: 0x046D, pid: 0xC52B));
check("VID/PID + UsagePage/Usage 全匹配才选中", MatchReturns(proto, vid: 0x17EF, pid: 0xF006, page: 0xFFCF, usage: 0x00A0));
check("Usage 不匹配时不选中", !MatchReturns(proto, vid: 0x17EF, pid: 0xF006, page: 0xFFCF, usage: 0x00A1));
`@

- [x] **Step 2: 运行测试，确认失败**（编译失败）

- [x] **Step 3: 实现模型与装载**

- 模型用 `record` + 位置参数，字段顺序与规格 §6.1 一致；`Transport` 用枚举
  （`Control` / `Interrupt` / `Feature` / `Auto`）。
- 装载用 `System.Text.Json`（BCL 内置，零 NuGet）：`JsonSerializerOptions` 允许
  `PropertyNameCaseInsensitive = true`；**解析失败返回空表**，不抛、不写日志刷屏。
- `Match` 的匹配条件：**Vid/Pid 相同** 且（表里的 `UsagePage` 为 0 或相同）且
  （`Usage` 为 0 或相同）—— 这样一条表项可以覆盖同一设备族的多个集合。
- 表文件路径：`%LOCALAPPDATA%\MyIsland\peripheral-protocols.json`；
  **文件不存在 = 没有 L3 能力**，不是错误。

- [x] **Step 4: 运行测试，确认通过**

---

### Task 3: 回包解析与三层校验（纯规则）

**Files:**
- Create: `src/Island.App/Services/Peripheral/PeripheralProtocol.cs`
- Create: `tests/Island.Tests/PeripheralValueParserTests.cs`
- Modify: `tests/Island.Tests/Island.Tests.csproj`、`tests/Island.Tests/Program.cs`

**Interfaces:**
- Consumes: Task 2 的 `PeripheralProtocol`
- Produces:
  - `internal sealed record PeripheralReading(int? Percent, bool? Charging, int? MilliVolts)`
    （**Percent 为 null 就是「未知」**，不填 0）
  - `internal static class PeripheralValueParser`：
    - `static PeripheralReading Parse(PeripheralProtocol p, byte[] report)` —— 三层校验
    - `static bool EchoMatches(byte[] echo, byte[] report)`
    - `static int? ReadMasked(byte[] report, int offset, int mask)`
    - `static int? ScaleToPercent(int raw, int valueMin, int valueMax)`

- [x] **Step 1: 写失败的测试**

`@
// ① 回显不匹配 → 未知（不返回 0！）
check("回显不匹配时返回未知",
    PeripheralProtocolReader.TryParse(proto, new byte[] { 0x00, 0x00, 0x00 }).Percent is null);
// ② 范围校验：换算后越界 → 未知
check("换算后超出 0..100 时返回未知",
    PeripheralProtocolReader.TryParse(protoWithValueRange(0, 50), report).Percent is null);
check("范围内正常换算",
    PeripheralProtocolReader.TryParse(proto, report).Percent == 58);
// ③ 充电位只认 0/1，其它值当未知
check("充电位为 2 时充电状态未知",
    PeripheralProtocolReader.TryParse(proto, reportWithCharging(2)).Charging is null);
check("充电位为 1 时判定为充电中",
    PeripheralProtocolReader.TryParse(proto, reportWithCharging(1)).Charging == true);
// 绝不用 0 充数：0 是合法读数吗？—— 合法，但只有回包里真的有 0 才是 0
check("回包里没有电量字段时不能凭空得到 0",
    PeripheralProtocolReader.TryParse(proto, new byte[8]).Percent is null);
`@

- [x] **Step 2: 运行测试，确认失败**

- [x] **Step 3: 实现三层校验**

`@
Parse(p, report):
  if (report == null || report.Length == 0) return Unknown;
  if (p.Echo is { Length: > 0 } && !EchoMatches(p.Echo, report)) return Unknown;   // ① 回显
  raw = ReadMasked(report, p.ValueOffset, p.ValueMask);
  if (raw is null) return Unknown;
  pct = ScaleToPercent(raw.Value, p.ValueMin, p.ValueMax);                          // ② 范围
  if (pct is null || pct < 0 || pct > 100) return Unknown;
  charging = ReadMasked(report, p.ChargingOffset, p.ChargingMask);                    // ③ 充电位
  charging = charging is null ? null : (charging.Value == 1 || charging.Value == 0 ? charging.Value == p.ChargingIsOne : null);
  return new PeripheralReading(pct, charging, millivolts);
`@

- ③ 的口径：**只认 0 / 1**，其它值（2、255…）一律当未知充电态。
- `ScaleToPercent`：`ValueMax == ValueMin` 时直接判未知（除不尽会溢出）；否则线性换算，
  **结果不在 0..100 一律返回 null**。

- [x] **Step 4: 运行测试，确认通过**

---

### Task 4: L1 标准 HID 电量

**Files:**
- Create: `src/Island.App/Services/Peripheral/StandardHidBattery.cs`
- Create: `tests/Island.Tests/PeripheralBatteryTests.cs`（追加用例）
- Modify: `tests/Island.Tests/Island.Tests.csproj`、`Program.cs`

**Interfaces:**
- Consumes: Task 1 的 `HidInterface` / `HidChannel` / `HidUsageRange`
- Produces:
  - `internal static class StandardHidBattery`：
    - `static IReadOnlyList<(ushort Page, ushort Usage)> PercentUsages`（**只有三组**，规格 §5）
    - `static int? ReadPercent(HidChannel channel, HidInterface it, byte[] report)`

- [x] **Step 1: 写失败的测试（usage 白名单 + 换算口径）**

`@
check("L1 只承认三组百分比 usage", StandardHidBattery.PercentUsages.Count == 3);
check("L1 按 LogicalMin/LogicalMax 换算",
    StandardHidBattery.ScaleToPercent(50, 0, 100) == 50);
check("L1 换算结果越界判未知",
    StandardHidBattery.ScaleToPercent(150, 0, 100) is null);
check("L1 排除 0x85:0x0001 / 0x85:0x0066",
    !StandardHidBattery.PercentUsages.Contains((0x85, 0x0001))
    && !StandardHidBattery.PercentUsages.Contains((0x85, 0x0066)));
`@

- [x] **Step 2: 运行测试，确认失败**

- [x] **Step 3: 实现**

- 白名单常量：`(0x06,0x0020)`、`(0x0D,0x003B)`、`(0x85,0x0065)`。
- 取值：`HidP_GetUsageValue` 按描述符解析（**不猜字节偏移**），
  再按 value caps 的 `LogicalMin` / `LogicalMax` 换算；不在 0..100 → 未知。
- 只在 **feature / input** 报告上解析；**不把 input 报告的字节直接当百分比**。

- [x] **Step 4: 运行测试，确认通过**

---

### Task 5: L4 通用识别兜底与去重（纯规则）

**Files:**
- Create: `src/Island.App/Services/Peripheral/PeripheralClassifier.cs`
- Create: `src/Island.App/Services/Peripheral/DeviceService.MergeSources`
- Create: `tests/Island.Tests/PeripheralDedupeTests.cs`
- Modify: `tests/Island.Tests/Island.Tests.csproj`、`Program.cs`

**Interfaces:**
- Consumes: Task 1 的 `HidGroup`、既有 `DeviceInfo`
- Produces:
  - `internal static class GenericPeripheralDetector`：
    - `static bool LooksLikePeripheral(HidGroup group)`（名称关键词 + 厂商自定义集合）
    - `static string DetectKind(HidGroup group)`（mouse / keyboard / headset / dongle）
  - `internal static class PeripheralDedupe`：
    - `static List<DeviceInfo> Merge(IEnumerable<DeviceInfo> existing, IEnumerable<DeviceInfo> peripheral)`
      —— 同 VID:PID 只留一条；**L3 命中后 L4 同 VID:PID 的条目去掉**；与 BLE / MSBatteryClass 的同名设备去重

- [x] **Step 1: 写失败的测试**

`@
check("名称含 2.4G / Wireless / Receiver / Dongle / 无线 / 接收器 时识别为外设",
    GenericPeripheralDetector.LooksLikePeripheral(Group("Idea5003", "ZEPHYR", vendorDefined: true)));
check("厂商自定义集合（Usage page ≥ 0xFF00）也算外设",
    GenericPeripheralDetector.LooksLikePeripheral(Group("USB Composite Device", "", vendorDefined: true)));
check("普通键鼠集合不算 2.4G 接收器", !GenericPeripheralDetector.LooksLikePeripheral(Group("HID Keyboard", "", vendorDefined: false)));
check("L4 产出的条目电量显示未知",
    GenericPeripheralDetector.ToDeviceInfo(Group("Idea5003", "ZEPHYR", vendorDefined: true)).LevelText == "未知");
check("同 VID:PID 只出现一条", PeripheralDedupe.Merge(new[] { a, b }).Count == 1);
check("L3 命中后 L4 同 VID:PID 的条目被去掉", PeripheralDedupe.Merge(new[] { l3Entry }, new[] { l4Entry }).Count == 1);
check("与 BLE 已报的同名设备不重复列", PeripheralDedupe.Merge(new[] { bleEntry }, new[] { hidEntry }).Count == 1);
check("读不到的设备不进列表（不进列表 = 不伪装）", ...);
`@

- [x] **Step 2: 运行测试，确认失败**

- [x] **Step 3: 实现**

- 关键词表（规格 §7）：`2.4G`、`Wireless`、`Receiver`、`Dongle`、`无线`、`接收器`。
- L4 产出的 `DeviceInfo`：**`Percent = 0` + `LevelText = "未知"`**，
  与 XInput 档位同一套呈现（`DevicesPage.cs` 已有的「电量未知」分支直接可用，**不改版式**）。
- 去重键：`VID:PID`（大写十六进制）；与 BLE / MSBatteryClass 的去重按**规范化名称 + 类型**比对。

- [x] **Step 4: 运行测试，确认通过**

---

### Task 6: 服务层与 `DeviceService` 接线

**Files:**
- Create: `src/Island.App/Services/Peripheral/PeripheralBatteryService.cs`
- Modify: `src/Island.App/Services/DeviceService.cs`（**只加采样调用与合并，不动既有四条数据源**）

**Interfaces:**
- Consumes: Task 1–5 的全部产出
- Produces: `internal static class PeripheralBatteryService`：
  - `static IReadOnlyList<DeviceInfo> Sample()` —— 一次采样返回本轮外设读数
  - 内部顺序：**L1 → L2（既有源，本类不碰）→ L3 → L4**

- [x] **Step 1: 写失败的测试**（可测部分：合并与降级）

`@
check("协议表文件缺失时返回空列表而不是抛异常", PeripheralBatteryService.Sample().Count >= 0);
check("L4 条目的 Percent 为 0 且 LevelText 为未知", ...);
`@

- [x] **Step 2: 运行测试，确认失败**

- [x] **Step 3: 实现服务层**

- 采样流程：
  1. `HidBus.Enumerate()`（带缓存）→ 按 VID:PID 分组；
  2. 每组先试 **L1**（标准 usage），再试 **L3**（协议表命中则发命令 + 解析回包）；
  3. 两层都没读出 → **L4**：只列设备、电量「未知」；
  4. **同一 VID:PID 只出一行**（L3 命中后不再出 L4）。
- 发命令：先按表里的 `Transport`；`auto` 则 **三种都试**（规格 §4.5）。
  发命令前检查 `MinReportLength`，不足则放弃这条链路。
- **联想接收器的 L3 记录按规格 §8.5 的推断值启用**（偏移 5，证据链见 §8.4）；
  同时保留纠错通道：用户协议表可覆盖内置条目（改偏移不用改代码）；读不到仍是「未知」。
- 失败处理：单设备失败不影响其它设备；写诊断日志用既有 `DeviceService.LogLineThrottled`
  （同一句话 5 分钟一条，别刷屏）；读不到的设备**不进列表**（不伪装）。

- [x] **Step 4: 接线到 `DeviceService`**

在 `DeviceService.Sample()` 里，**在既有三条数据源之后**追加一行：

`@
try { list.AddRange(PeripheralBatteryService.Sample()); } catch { }
`@

并把结果并入 `_devices`（与 `BleBatteryService.Snapshot()` 一起），
最后用 `PeripheralDedupe.Merge` 做去重：同 VID:PID 只一条，
与 BLE / MSBatteryClass 已报的同名设备不重复。

**节拍不动**：仍是既有 30s / 300s（`RetuneTimer`），不新起定时器。

- [x] **Step 5: 构建 + 测试**

预期：0 警告 0 错误，测试全过。

---

### Task 7: 设备页呈现

**Files:**
- Modify: `src/Island.App/Controls/DevicesPage.cs`（**只在本阶段必要的语义处动，不改版式**）
- Modify: `src/Island.App/Services/DeviceService.cs`（`Glyph` 认识新 kind 时才动）

**Interfaces:**
- Consumes: Task 6 的 `DeviceInfo` 列表
- Produces: 设备页上「未知电量」条目按既有档位语义渲染

- [x] **Step 1: 确认现有渲染分支已够用**

`DevicesPage.cs` 已有：
- `LevelText is { } levelText` → 20px 档位文字（`LevelText = "未知"` 走这里）；
- `StatusText`：`LevelText == "未知"` → 「电量未知」；
- `LevelColor`：`LevelText is "有线" or "未知"` → `pal.TextDim`。

⇒ **L4 条目（`LevelText = "未知"`）不需要新增渲染分支**，只要 `LevelText` 有值即可。

- [x] **Step 2: 补 kind 标签（仅在需要时）**

若 `DeviceService.Glyph` 不认识新 kind（如 `dongle`），补一条映射；
**沿用现有单字图标风格**（鼠/键/耳/柄/电），不引入新图标资源。

- [x] **Step 3: 构建 + 测试**

---

### Task 8: 单元测试汇总与注册

**Files:**
- Modify: `tests/Island.Tests/Program.cs`
- Modify: `tests/Island.Tests/Island.Tests.csproj`

**Interfaces:**
- Consumes: Task 1–7 的可测部分
- Produces: `Island.Tests` 里的外设电量用例组

- [x] **Step 1: 汇总要断言的纯规则（缺一不可）**

| 规则 | 用例文件 |
|---|---|
| `HIDP_VALUE_CAPS` = 72 字节 | `PeripheralBatteryTests.cs` |
| L1 usage 白名单（3 组，排除 2 组） | `PeripheralBatteryTests.cs` |
| L1 换算与越界判未知 | `PeripheralBatteryTests.cs` |
| 协议表字段齐全 + JSON 装载容错 | `PeripheralProtocolTests.cs` |
| 协议表匹配（VID/PID/usage） | `PeripheralProtocolTests.cs` |
| 三层校验（回显 / 范围 / 充电位） | `PeripheralValueParserTests.cs` |
| L4 识别关键词 + 「未知」语义 | `PeripheralDedupeTests.cs` |
| 去重（同 VID:PID 一条、L3 压 L4、与 BLE 不重复） | `PeripheralDedupeTests.cs` |

- [x] **Step 2: 注册用例并跑全量**

`@
& 'D:\YQDESK\.dotnet\dotnet.exe' run --project 'D:\YQDESK\MyIsland-src\tests\Island.Tests\Island.Tests.csproj' -c Release --nologo
`@
预期：全过，退出码 0（末行「全部通过：N 条」）。

- [x] **Step 3: 确认门禁仍是零警告**

`@
& 'D:\YQDESK\.dotnet\dotnet.exe' build 'D:\YQDESK\MyIsland-src\Island.sln' -c Release --nologo
`@

---

### Task 9: 端到端验收与文档回填

**Files:**
- Modify: `docs/superpowers/specs/2026-11-26-windows-peripheral-battery-design.md`（验收结论 + 待验证清单状态）
- Modify: `docs/superpowers/specs/2026-11-25-cross-platform-consistency-design.md`（§3 能力矩阵、§5.1 W1 行）
- Modify: `docs/superpowers/plans/2026-11-26-windows-peripheral-battery.md`（勾选状态）

- [x] **Step 1: 全量门禁**

`@
& 'D:\YQDESK\.dotnet\dotnet.exe' build 'D:\YQDESK\MyIsland-src\Island.sln' -c Release --nologo
& 'D:\YQDESK\.dotnet\dotnet.exe' run --project 'D:\YQDESK\MyIsland-src\tests\Island.Tests\Island.Tests.csproj' -c Release --nologo
`@
预期：0 警告 0 错误；测试全过，退出码 0。

- [x] **Step 2: 确认 mac 侧零改动**

`@
git -C 'D:\YQDESK\MyIsland-src' diff --stat HEAD -- src/Island.Avalonia/
`@
预期：**空输出**。

- [x] **Step 3: 实机核对（无硬件时跳过并记录「未验证」）**

在本机跑一轮采样，确认：
- 联想 2.4G 接收器**出现一行**（不是 3 行）；
- 读不到时电量显示**「未知」**（接收器没连上设备时该字段是 0xFF，超出 0..100 → 判无效）；
- 读不到的设备**不进列表**。

- [x] **Step 4: 回填文档**

- 规格 §11「待验证清单」逐项更新状态（已确认 / 仍未确认）；
- 规格末尾追加验收结论（构建、测试条数、实机核对结果）；
- 总纲 §3 能力矩阵补外设电量行，§5.1 的 W1 行状态从「待实施」改成「已实施（commit）」。

- [x] **Step 5: 提交**

`@
git -C 'D:\YQDESK\MyIsland-src' add src/Island.App/Services/Peripheral/ tests/Island.Tests/ docs/superpowers/
git -C 'D:\YQDESK\MyIsland-src' commit -m "feat: Windows 外设电量（HID 访问层 + L1/L3/L4 分层探测）"
`@

---

## 验收标准（对应规格 §9.3）

1. `dotnet build Island.sln -c Release`：**0 警告 0 错误**。
2. `Island.Tests`：**全过**，退出码 0（含新增的外设电量纯规则断言）。
3. `src/Island.Avalonia/**` **零改动**（`git diff` 为空）。
4. **读不到就是「未知」**：界面不得出现假读数；不得用 0 / 默认值 / 猜测值充数。
5. 新增纯规则部分（协议表、三层校验、usage 白名单、去重）**有单元测试**。
6. **协议表可从 `%LOCALAPPDATA%\MyIsland\peripheral-protocols.json` 装载，新增品牌不需要改代码**。

## 明确不做（与规格 §10 一致）

- 不写没有硬件的厂商协议；不做探索式的破坏性写入；
- 不装驱动、不需要管理员权限；不伪造精确百分比；
- 不改 mac 侧；不动既有四条系统数据源。

---

## 未完成项清单（2026-11-26 复核）

**本计划（Windows 侧）Task 1–9 全部完成，没有未勾项。**
以下事项**不在本计划范围内**，但仍是两端对齐的待办：

- **mac 侧原生探针待实机验证**：`battery-hid`（2.4G 接收器电量）两端共用同一份协议表与三层校验，
  但 mac 侧的原生探针**尚未在实机上验证**（见能力清单 `docs/capabilities.md` §3，
  以及规格 `2026-11-26-macos-peripheral-battery-align-design.md`）。
- **联想 L3 条目目前按「推断值」启用**（而非语义确认后启用）：证据链见规格 §8.5；
  保留纠错通道 —— `%LOCALAPPDATA%\MyIsland\peripheral-protocols.json` 可覆盖内置条目，改偏移不用改代码。
