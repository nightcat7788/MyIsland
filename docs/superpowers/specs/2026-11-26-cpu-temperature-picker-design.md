# Windows CPU 温度取值（对齐 LiteMonitor 的传感器匹配口径）—— 设计规格

日期：2026-11-26
状态：**已实现并在本机验证**
范围：Windows 版（`src/Island.App/Services/ThermalService.cs` + 新增 `CpuTemperatureRule.cs`）
参照项目：[Diorser/LiteMonitor](https://github.com/Diorser/LiteMonitor)（MIT）
mac 侧：`src/Island.Avalonia/**` **零改动**（该工程通过 `Compile Include` 链接 App 的 `ThermalService.cs`，
改动自动同步到 mac 构建，但 mac 工程自身源文件一行未动）

---

## 1. 背景

MyIsland 的 CPU 温度原来是一句"名字白名单"：

```csharp
cpuTemp ??= Pick(hw, SensorType.Temperature, "Core Average", "CPU Package", "Package", "Tdie", "Tctl");
```

`Pick` 是**包含匹配**、按名单顺序取第一个命中的。这条规则有三个实际问题：

1. **取到的不是标准口径**。本机（Intel Core Ultra 5 225H）同时有 `CPU Package`（58）与
   `Core Average`（49），旧规则先命中 `Core Average`，界面显示的是**平均核心温度**，
   比封装温度低 9°C。HWiNFO / Core Temp 等工具显示的"CPU Package"才是行业标准口径；
   均值会**低估降频风险**（岛体的"高温"状态是按温度阈值触发的）。
2. **一旦要加"包含 core 之类的兜底"，就会误选**。Intel 每核都有
   `P-Core #n Distance to TjMax` —— 名字里带 core，但它是**离最高温度还有多远**，不是温度。
   旧名单恰好没包含它，所以问题没暴露；改成更宽的规则就会踩中。
3. **只认固定名字，覆盖面窄**。AMD Zen 是 `Core (Tdie)` / `Core (Tctl)` / `Core (Tctl/Tdie)`，
   Intel 是 `CPU Package` / `Core Max` / `Core Average` / `P-Core #n` / `E-Core #n`，
   还有只暴露逐核读数的机型 —— 名字对不上就整个读不到（显示 "--"）。

LiteMonitor（MIT）在这个点上做了两件值得吸收的事：

| LiteMonitor 的做法 | 位置 | 吸收方式 |
|---|---|---|
| CPU 温度**黄金规则**：`package` / `average` / `tctl` / `tdie` / `ccd` / `cores` | `SensorMatcher.cs` | 吸收，但**分层顺序重新排过**（见 §3） |
| **排除干扰项**：`Distance`（距离不是温度）、`SOC`、`VRM`、`Pump`、`Liquid`、`Coolant` | `SensorMatcher.cs` | 全部吸收 |
| **多核温度取最大值**（排除 Average/Max 后逐核取最大） | `ComponentProcessor.cs` | 吸收，作为兜底层 |
| 驱动侧：启动前检查并安装 PawnIO、处理"需重启" | `DriverInstaller.cs` | **不吸收**：MyIsland 已有 `PawnIoInstaller`（内嵌官方签名安装器 + SHA-256 校验 + 静默安装 + 运行期自愈重建），比 LiteMonitor 的"从镜像下载 zip"更可控 |

LiteMonitor **没有**而 MyIsland 保留的两点：

- **Tdie 优先于 Tctl**：AMD 的 Tctl 是风扇控制用的保守值，比真实核心温度高 10–27°C
  （依据：LibreHardwareMonitor v0.9.6 `Hardware/Cpu/Amd17Cpu.cs`，2026-11-24 核对）。
  LiteMonitor 是 tctl 在前，MyIsland 保持 Tdie 在前。
- **取值来源显式返回**（选了哪个传感器、依据哪一层），写进诊断日志 —— 排障时能直接看出
  "这个 58°C 是 CPU Package 还是 Tdie"，而不是猜。

---

## 2. 本机实测（2026-11-26，thermal-diag.log 原始值）

本机 CPU：`Intel(R) Core(TM) Ultra 5 225H`（P 核 + E 核混合架构），PawnIO 2.2.0.0。

LHM 0.9.6 暴露的全部 CPU 温度传感器：

| 传感器 | 读数 | 说明 |
|---|---|---|
| `CPU Package` | **58** | 封装温度（标准口径） |
| `Core Max` | 58 | LHM 聚合的最热核心 |
| `Core Average` | 49 | LHM 聚合的平均值（**旧规则取的就是这个**） |
| `P-Core #1..#4` / `E-Core #1..#10` | 44–58 | 逐核读数 |
| `P-Core #n Distance to TjMax` / `E-Core #n Distance to TjMax` | 47–61 | **距离不是温度**（干扰项） |

旧规则 → **49**（Core Average）；新规则 → **58**（CPU Package）。

---

## 3. 新规则（`src/Island.App/Services/CpuTemperatureRule.cs`）

**纯函数、无硬件库依赖**，可链进零 NuGet 的 `Island.Tests` 断言。

### 3.1 先剔除坏值与干扰项

- 坏值：`≤ 0.01` 或 `> 125`（物理上限）→ 丢弃
- 干扰项（名字命中即丢弃）：`distance`、`tjmax`、`soc`、`vrm`、`pump`、`liquid`、`coolant`、`fan`

### 3.2 分层取值（同层取最大值）

| 层 | 判据 | 来源标签 | 为什么在这一层 |
|---|---|---|---|
| ① | 名字含 `package` | `Package` | 行业标准口径（LiteMonitor 黄金规则第一条） |
| ② | 含 `tdie` 且不含 `tctl` | `Tdie` | AMD 硅片温度，比 Tctl 准 10–27°C |
| ③ | 同时含 `tctl` 与 `tdie` | `TctlTdie` | Zen 移动端的合并值 |
| ④ | 含 `tctl` | `Tctl` | 风扇控制用的保守值，虚高，放最后 |
| ⑤ | 含 `ccd` | `Ccd` | AMD 多 CCD |
| ⑥ | 含 `core max` / `max core` | `CoreMax` | LHM 自己聚合的最热核心 |
| ⑦ | 名字含 `core`/`cpu` 且**不是**任何聚合量 | `PerCoreMax` | 只有逐核读数时的兜底（LiteMonitor 的多核最大口径） |
| ⑧ | 含 `average` | `Average` | 最后兜底：均值会低估降频风险 |

**全部层都没命中 → 返回 null → 界面显示 "--"**，绝不用 0 或默认值充数。

### 3.3 与 LiteMonitor 的差异（有意为之）

| 点 | LiteMonitor | MyIsland | 理由 |
|---|---|---|---|
| package vs average | average 在 package 之后（package 在前） | **package 在最前** | 标准口径；均值低估降频风险 |
| tctl vs tdie | tctl 在前 | **tdie 在前** | Tctl 虚高 10–27°C |
| 取值来源 | 只给数字 | **同时给出传感器名 + 来源层** | 诊断日志能直接看出读数出处 |

---

## 4. 接线

- `ThermalService.SampleCore()` 的 CPU 分支改为：收集 CPU 节点全部 `SensorType.Temperature`
  传感器（含名字与读数）→ `CpuTemperatureRule.Pick()` → 取值 + 记录来源。
- 兜底链不变：LHM → （显式开启且为 AMD 时）SMN 直读 → WMI 热区。
- 诊断日志新增一行 `CPU 温度来源：<传感器名> (<来源层>)`，随首次采样落盘。

---

## 5. 验收标准

1. `dotnet build Island.sln -c Release`：**0 警告 0 错误**
2. `Island.Tests`：**全过**，退出码 0（新增 20 条 CPU 温度用例）
3. `src/Island.Avalonia/**`：**零改动**
4. 本机读数从 49（Core Average）变为 58（CPU Package）
5. 干扰项（Distance to TjMax / SoC / VRM / 水泵）**绝不出现在读数里**（有用例守着）
6. 读不到就是"--"，不出现假读数（有用例守着）

---

## 6. 明确不做

- **不改驱动方案**：不做 LiteMonitor 的"从镜像下载驱动 zip"；沿用内嵌官方签名安装器 + SHA-256 校验。
- **不引入 CPU 频率/功耗的额外规则**：本轮只管温度。
- **不做"保留上次读数"**：读数短暂为空时直接显示 "--"，不拿旧值冒充当前值
  （LiteMonitor 有 `lastValidMap` 那种做法，本项目不采用，避免把陈旧读数当成当前状态）。
