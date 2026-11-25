# CPU 温度取值（对齐 LiteMonitor 口径）Implementation Plan

**Goal:** 把 Windows 版的 CPU 温度从"名字白名单"升级成"分层匹配 + 显式排除干扰项"的纯规则，
取标准口径的封装温度，读不到就是"--"，绝不出现假读数。

**参照：** [Diorser/LiteMonitor](https://github.com/Diorser/LiteMonitor)（MIT）的
`SensorMatcher.Match` 与 `ComponentProcessor.GetCpuTemp`。

**规格：** `docs/superpowers/specs/2026-11-26-cpu-temperature-picker-design.md`（下称「规格」）

## Global Constraints

- **mac 侧零改动**：`src/Island.Avalonia/**` 的 `git diff` 必须为空。
  （该工程通过 `Compile Include` 链接 App 的 `ThermalService.cs`，改动会自动同步到 mac 构建，
  但 mac 工程自身源文件一行不动 —— 这不算改动 mac 侧。）
- **不伪装**：读不到 / 校验不过 → 显示 "--"，不用 0、默认值或陈旧值充数。
- **纯规则优先**：取值规则必须是不依赖硬件库的纯文件，才能链进零 NuGet 的 `Island.Tests` 断言。
- **构建门禁**：`dotnet build Island.sln -c Release` 必须 **0 警告 0 错误**。
- **测试门禁**：`Island.Tests` 必须全过，退出码 0。
- **命令统一用**：`D:\YQDESK\.dotnet\dotnet.exe`（系统 dotnet 无 SDK）。
- **编码**：UTF-8 无 BOM、**LF 行尾**。

---

### Task 1: 抽纯规则 `CpuTemperatureRule`

**Files:**
- Create: `src/Island.App/Services/CpuTemperatureRule.cs`
- Create: `tests/Island.Tests/CpuTemperatureTests.cs`
- Modify: `tests/Island.Tests/Island.Tests.csproj`、`tests/Island.Tests/Program.cs`

**Produces:** `CpuTemperatureCandidate` / `CpuTemperaturePick` / `CpuTemperatureSource` / `CpuTemperatureRule.Pick()`

- [x] Step 1: 先写用例（本机真实传感器全集 → 期望选中 `CPU Package` = 58）
- [x] Step 2: 实现分层规则与干扰项排除
- [x] Step 3: 接入测试工程并注册

### Task 2: 接入 `ThermalService`

**Files:** Modify `src/Island.App/Services/ThermalService.cs`

- [x] Step 1: CPU 分支改用 `CpuTemperatureRule.Pick()`，替代名字白名单
- [x] Step 2: 诊断日志增加「CPU 温度来源」一行

### Task 3: 真机验证

**Files:** Create `tools/lhm-probe/`（开发期侦察工具，不进 `Island.sln`）

- [x] Step 1: 打印 LHM 看到的全部 CPU 温度传感器 + 规则命中结果
- [x] Step 2: 本机验证：旧口径 49（Core Average）→ 新口径 58（CPU Package）

### Task 4: 验收与回填

- [x] 构建 0 警告 0 错误
- [x] `Island.Tests` 全过（新增 20 条）
- [x] `src/Island.Avalonia/**` 零改动

---

## 实现回填（2026-11-26）

| 项 | 结果 |
|---|---|
| 新增文件 | `src/Island.App/Services/CpuTemperatureRule.cs`、`tests/Island.Tests/CpuTemperatureTests.cs`、`tools/lhm-probe/` |
| 修改 | `src/Island.App/Services/ThermalService.cs`（取值改走规则 + 诊断日志加来源行）、`tests/Island.Tests/Island.Tests.csproj`、`tests/Island.Tests/Program.cs` |
| 取值结果 | 本机 Intel Core Ultra 5 225H：**49（Core Average）→ 58（CPU Package）** |
| 用例 | 新增 20 条：分层优先级 / 干扰项排除 / 坏值与空输入 |
| 构建 | 0 警告 0 错误 |
| 测试 | `Island.Tests` 全过 |
| mac 侧 | `src/Island.Avalonia/**` 零改动 |

### 与本计划初稿的偏离

1. **没做"保留上次读数"**：LiteMonitor 有 `lastValidMap`（读数短暂为空时沿用旧值）。
   本项目不采用 —— 拿陈旧值当当前值违反「不伪装」。读不到就是 "--"。
2. **没动驱动方案**：LiteMonitor 的 `DriverInstaller` 从镜像下载驱动 zip；
   MyIsland 已有内嵌官方签名安装器 + SHA-256 校验 + 运行期自愈重建，更可控，不换。
