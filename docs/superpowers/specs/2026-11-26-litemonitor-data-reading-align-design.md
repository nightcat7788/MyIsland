# 采集口径对齐 LiteMonitor（CPU 占用 / 频率 / 运行时长 / 网速 / 传感器匹配）—— 设计规格

日期：2026-11-26
状态：设计已定；计数器口径已改完并实测通过
范围：`src/Island.Native/SystemMonitor.cs`（计数器）+ `src/Island.App/Services/`（LHM 传感器匹配）
参照：[Diorser/LiteMonitor](https://github.com/Diorser/LiteMonitor)（MIT）的 `PerformanceCounterManager` / `SensorMap` / `SensorMatcher` / `HardwareRules` / `ComponentProcessor`

---

## 1. 问题：读数确实不对（实测）

在本机（Intel Core Ultra 5 225H）同时读两个计数器，连续 5 轮：

| 计数器 | 读数 |
|---|---|
| `Processor Information` / `% Processor Utility` | **47–54%** |
| `Processor Information` / `% Processor Time` | **26–28%** |
| `Processor` / `% Processor Time` | 26–28%（同上） |
| `Processor Information` / `Actual Frequency` | 2355–2585 MHz |
| `Processor Information` / `Processor Frequency` | 1328 MHz（基准） |
| `Memory` / `Available MBytes` | 16267 MB |
| `System` / `System Up Time` | 228386 s（≈63.4 h） |
| `SMB Client Shares` / `Read Bytes/sec` | **本机该类别不存在** |

**结论**：MyIsland 用的是 `% Processor Time`（**占用时间占比**），
而任务管理器与 LiteMonitor 用的是 `% Processor Utility`（**按实际频率折算的利用率**）。
这台机器会睿频（实测 2.3–2.6 GHz，基准 1.33 GHz），两者相差近一倍 ⇒
**岛体显示的 CPU 占用明显偏低**。这就是「数据不对」的主要来源。

---

## 2. 逐项对比与裁决

| 读数 | LiteMonitor | MyIsland（改前） | 裁决 |
|---|---|---|---|
| CPU 占用 | `Processor Information` / `% Processor Utility`（回退链：Utility → Time → legacy） | `Processor` / `% Processor Time` | **改**：换成 Utility 优先 + 同款回退链 |
| CPU 频率 | `Actual Frequency` → `Processor Frequency` → `% Processor Performance`×基准 | 只有 `% Processor Performance`×基准（**估算**） | **改**：加 `Actual Frequency` 真值优先，估算兜底 |
| 内存占用 | `Memory` / `Available MBytes`，(总-可用)/总 | `GlobalMemoryStatusEx`（ullAvailPhys） | 已等价，不动 |
| 磁盘活动% | `PhysicalDisk` / `% Disk Time` `_Total` | 同 | 已一致 |
| 磁盘读写 | `PhysicalDisk` Read/Write Bytes/sec `_Total` | 同（+逐盘） | 已一致 |
| 运行时长 | `System` / `System Up Time`（不含休眠） | `Environment.TickCount64`（**含休眠**） | **改**：PDH 优先，回退 TickCount64 |
| 网速 | LHM 网卡传感器 + 排除虚拟网卡；SMB 计数器分离内网流量 | IP Helper 全接口累计（含虚拟网卡） | **改**：加虚拟网卡名过滤；**SMB 不采纳**（本机无该类别，且 LiteMonitor 自己也只是可选） |
| 核心柱 | 逐核占用 | 逐核占用 | 已一致（本机两个类别都是 14 个逻辑核） |
| GPU | LHM | PDH `\GPU Engine` | 口径不同但都对，不动 |

---

## 3. LHM 传感器匹配：从名字白名单改成规则

LiteMonitor 不写死传感器名，而是三层规则：

| 层 | LiteMonitor | 本项目的做法 |
|---|---|---|
| 匹配 | `SensorMatcher.Match`：名字 → 标准语义 | `HardwareSensorRules.KindOf` |
| 裁决 | `HardwareRules`：独显 > 核显、共享/专用显存 | 同类多候选取值口径（CPU 温度：package > tdie > tctl/tdie > tctl > 逐核最大） |
| 建表 | `SensorMap`：遍历所有硬件的传感器绑到标准键 | `ThermalService` 按规则选 |

**统一排除的干扰项**（LiteMonitor 的清单 + MyIsland 已有的经验）：
`distance`（`Distance to TjMax` 是**离最高温度还有多远**，不是温度！）
、`tjmax`、`soc`、`vrm`、`pump`、`liquid`、`coolant`、`fan`。

---

## 4. 明确不采纳的（以及原因）

| LiteMonitor 的做法 | 不采纳的原因 |
|---|---|
| LHM 启用**存储传感器**（`IsStorageEnabled = true`） | **本机 2026-11-22 实测：LHM 的 NVMe SMART IOCTL 在这台 Lenovo secured-core 上触发 0x1EA 蓝屏两次**（见 `ThermalService` 的注释）。为此 SSD 温度长期显示 `--`，这是「如实未知」而不是「假读数」 |
| LHM 启用**内存 / 网卡 / 控制器**传感器 | 都需要内核驱动访问，本项目走的是「零驱动、免管理员」路线；未在本机验证过，不冒蓝屏风险 |
| SMB 计数器分离内网流量 | 本机 `SMB Client Shares` 类别不存在（实测抛 InvalidOperationException），LiteMonitor 自己也把它做成可选 |

---

## 5. 验收标准

1. `dotnet build Island.sln -c Release`：**0 警告 0 错误**
2. `Island.Tests`：**全过**
3. **CPU 占用与任务管理器同口径**：实测岛体读数应落在 `% Processor Utility` 的量级（本机约 47–66%，而改前约 26–28%）
4. **CPU 频率用真值**：`Actual Frequency` 可用时直接用它（本机约 2.2–2.6 GHz），不可用才回退估算
5. **虚拟网卡不计入网速**：Hyper-V / VMware / VirtualBox / WSL / 隧道 / 容器 网卡命中特征词即跳过
6. **LHM 传感器按规则选**，且 `Distance to TjMax` 之类干扰项**绝不出现在读数里**

## 6. 后续可做（不在本轮）

- 「硬件详情」面板：把 LHM 的**全部**传感器列出来（LiteMonitor 的 `HardwareInfoForm` 思路）
- 内存 / 存储传感器的安全启用方案（先在别的机型上验证蓝屏风险，再决定）
