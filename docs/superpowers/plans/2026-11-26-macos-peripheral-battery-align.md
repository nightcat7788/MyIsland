# macOS 对齐：外设电量 + CPU 温度口径 —— Implementation Plan

**Goal:** 让 mac 版与 Windows 版在外设电量（2.4G 接收器 / HID 标准 usage / 只识别兜底）
与 CPU 温度取值口径上**行为一致**，且判定规则**只有一份**（共享层）。

**规格：** `docs/superpowers/specs/2026-11-26-macos-peripheral-battery-align-design.md`（下称「规格」）

## Global Constraints

- **一份规则**：协议表、三层校验、usage 白名单、识别兜底、类型词表全部来自
  `src/Island.App/Services/Peripheral/`，mac 工程按既有先例（`ThermalService.cs` / `Settings.cs`）链接。
- **探针只搬运**：Swift/ObjC 探针不判断「哪个字节是电量」。
- **不伪装**：读不到 ⇒ 「未知」，不显示 0%。
- **构建门禁**：`dotnet build Island.sln -c Release` 必须 **0 警告 0 错误**（含 mac 工程）。
- **测试门禁**：`Island.Tests` 必须全过，退出码 0。
- **命令统一用**：`D:\YQDESK\.dotnet\dotnet.exe`。
- **编码**：UTF-8 无 BOM、**LF 行尾**。
- ⚠️ **原生探针（Swift/ObjC）本机无法编译**，代码由用户在 Mac 上验证 —— 探针代码要格外保守。

---

### Task 1: 共享层补齐（mac 侧要用到的东西）

**Files:**
- Create: `src/Island.App/Services/Peripheral/HidReportDescriptor.cs`（最小 HID 报告描述符解析器）
- Create: `tests/Island.Tests/HidDescriptorTests.cs`
- Modify: `src/Island.App/Services/Peripheral/PeripheralProtocol.cs`（`DisplayName` + `ReloadUserTableFile`）
- Modify: `src/Island.App/Services/Settings.cs`（公开 `DataDir`）
- Modify: `tests/Island.Tests/Island.Tests.csproj`、`src/Island.Avalonia/Island.Avalonia.csproj`（链接）

- [x] Step 1: 描述符解析器（位偏移 / 位宽 / 逻辑值域 / 报告 ID / 报告类型）
- [x] Step 2: 协议表显示名与用户表装载（两端同一份语义）
- [x] Step 3: 链接进 mac 工程 + 单元测试

### Task 2: mac 原生探针扩展

**Files:** `tools/device-battery/device-battery.swift`

- [x] Step 1: IOHIDManager 枚举 HID 设备（VID/PID/usage/产品名/序列号/报告描述符）
- [x] Step 2: 读 Feature 报告原始字节（第 0 字节是报告 ID）
- [x] Step 3: 按规格 §3 契约输出 HIDDEV / HIDFTR 行
- [x] Step 4: 读不到就跳过、绝不崩；与已有蓝牙设备去重
- ⚠️ 本机无法编译 → 用户在 Mac 上验证

### Task 3: mac 温度探针口径对齐

**Files:** `tools/thermal-probe/thermal.m`

- [x] Step 1: CPU 通道从「求平均」改成「按优先级选一个」（规格 §6）
- [x] Step 2: 排除 Distance/TjMax 等干扰项
- [x] Step 3: 输出格式一字不改；选中的传感器写 stderr
- ⚠️ 本机无法编译 → 用户在 Mac 上验证

### Task 4: mac C# 侧接线

**Files:** `src/Island.Avalonia/DeviceBattery.cs`、`src/Island.Avalonia/IslandCanvas.cs`

- [x] Step 1: `DeviceBattery.Device` 补 `LevelText`（与 Windows `DeviceInfo` 同义）
- [x] Step 2: 解析探针新输出 → 聚合 HID 设备 → 应用共享规则（L1 → L3 → L4）
- [x] Step 3: 设备页渲染：未知态（空环 + 「未知」+ 「电量未知」）、类型词表统一
- [x] Step 4: 用户协议表从共享数据目录装载

### Task 5: mac 构建链补洞

**Files:** `tools/build-mac-probes.sh`（新）、`packaging/macos/publish.sh`

- [x] Step 1: 原生探针的编译脚本（此前只有源码、没有可复现的编译路径）
- [x] Step 2: publish.sh 在探针缺失时自动编译

### Task 6: 验收与回填

- [x] 构建 0 警告 0 错误
- [x] `Island.Tests` 全过
- [ ] ⚠️ mac 真机验证（用户侧：编译两个原生探针 + 核对设备页）

---

## 实现回填（2026-11-26）

| 项 | 结果 |
|---|---|
| 共享层 | `HidReportDescriptor`（描述符解析：报告 ID/类型/usage/位偏移/位宽/逻辑值域）、`DisplayName`、`ReloadUserTableFile`、`Settings.DataDir` |
| mac 探针 | `device-battery.swift`：IOHIDManager 枚举 + Feature 报告读取 + HIDDEV/HIDFTR 契约输出 |
| mac 温度探针 | `thermal.m`：CPU 通道改为按优先级选一个（PMU tdie → tdie → pACC → eACC → SMC 兜底），排除干扰项 |
| mac C# | `DeviceBattery.cs` 重写（三层判定走共享规则）、`IslandCanvas.cs` 设备页未知态与词表统一 |
| 构建链 | `tools/build-mac-probes.sh` + `publish.sh` 自动编译 |
| 验证 | 构建 0 警告 0 错误；`Island.Tests` 全过；**原生探针待用户在 Mac 上编译验证** |
