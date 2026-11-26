# 阶段 F 验证矩阵 —— 怎么跑、报告放哪、怎么回填

这个目录是**阶段 F（F1–F4 多机型验证）**的归档地：每台机器跑一次
`tools\verify-matrix.ps1`，产出一份 Markdown 报告，报告里逐项写清楚
「检查项 / 判据 / 实测值 / 结论 / 原始证据」。

> 一句话口径：**脚本不发明规则**。每一项判据都来自产品真实实现路径
> （`ThermalService` / `CpuTemperatureRule` / `HardwareSensorRules` /
> `GpuUsageReader` / `PeripheralProtocols`），跑的是产品那份代码，不是"看起来像"的替身。

## 目录里有什么

| 文件 | 是什么 |
| --- | --- |
| `README.md` | 本文件：用法与回填流程 |
| `axis-coverage-matrix.md` | **四轴 × 检查项**的覆盖矩阵：每格标「本机已验 / 本机不适用 / 待其它机型验」 |
| `2026-11-26-Intel-R-Core-TM-Ultra-5-225H.md` | 首份报告（旧版脚本：只报告覆盖情况，不做专项检查）—— 保留作对照基线 |
| `2026-11-26-Intel-R-Core-TM-Ultra-5-225H-axis.md` | 升级后脚本（逐轴专项检查）在本机的报告 |
| `2026-11-27-Intel-R-Core-TM-Ultra-5-225H.md` | **最新**：逐轴五态（通过 / 失败 / 读不到 / 无样本 / 不适用）的本机报告 —— 对应 v1.1.3 |
| `windows-selftest-2026-11-26.md` | Windows 端功能自检（普通权限） |
| `windows-selftest-2026-11-26-nonadmin.md` | 同上，明确标注「非管理员」口径的对照报告 |
| `windows-selftest-2026-11-26-elevated.md` | 同上，**提权**运行（WLAN / 蓝牙开关往返真的切过、真的还原过） |
| `windows-selftest-2026-11-27.md` | **最新**：v1.1.3 这一版的自检（16 项，失败 0，需人工 4） |

### 本批报告对应的版本

现有报告均为 **v1.1.3**（2026-11-27 发布）这一轮的验证结果；
对应安装包见 <https://github.com/nightcat7788/MyIsland/releases/tag/v1.1.3>。

## 怎么跑

在**仓库根目录**执行：

~~~powershell
# 最常见的一次
powershell -ExecutionPolicy Bypass -File tools\verify-matrix.ps1

# 指定报告落盘位置（相对路径按仓库根解析）
powershell -ExecutionPolicy Bypass -File tools\verify-matrix.ps1 -OutFile docs\verification\2026-11-27-amd-laptop.md

# 指定 dotnet（**必须给全路径**：系统 dotnet 没有 SDK）
powershell -ExecutionPolicy Bypass -File tools\verify-matrix.ps1 -Dotnet D:\YQDESK\.dotnet\dotnet.exe

# 只采画像、不跑探针（探针相关项会标「未执行」）
powershell -ExecutionPolicy Bypass -File tools\verify-matrix.ps1 -SkipProbes
~~~

### 参数

| 参数 | 作用 |
| --- | --- |
| `-OutFile <路径>` | 报告落盘路径。相对路径按**仓库根**解析；不给就自动落到 `docs\verification`，文件名 = `yyyy-MM-dd-<CPU 型号 slug>.md`；**同名已存在时自动加 `-2` / `-3` 后缀，不覆盖旧报告** |
| `-Dotnet <全路径>` | dotnet 可执行文件全路径。脚本自动找 `D:\YQDESK\.dotnet\dotnet.exe` → `%USERPROFILE%\.dotnet\dotnet.exe` → `C:\Program Files\dotnet\dotnet.exe`，都要有 SDK（`--list-sdks` 非空） |
| `-SkipProbes` | 跳过探针（不构建、不跑 lhm-probe / hid-probe）。需要探针的检查项会标「**未执行**」 |

### 脚本会做什么

1. 采本机画像：CPU 厂商（注册表 `VendorIdentifier`，与产品 `InpOutSmnService.IsAmdCpu` 同源）/ 型号 / 核数、
   显卡清单、有无电池（`Win32_Battery` + `MSBatteryClass`）、主板、内存条、是否管理员。
2. 构建并跑三个探针：
   - `tools\lhm-probe\lhm-probe.dll matrix` —— CPU + GPU + 主板传感器快照（JSON）。
     开启的硬件类别与产品 `ThermalService.OpenComputer` **完全一致**；取值走产品的
     `CpuTemperatureRule` / `HardwareSensorRules`；显卡名称与占用直接跑
     `Island.Native\CounterSampleCache`（产品那份代码）。
   - `tools\hid-probe\hid-probe.dll --selftest` —— 协议表自检 + 真机命中报告 + 与产品路径的两路复核。
   - `tools\hid-probe\hid-probe.dll unknown` —— 未识别设备摘要（一行一设备：命中层 + VID/PID + 描述符）。
3. 逐轴执行专项检查并出报告。

### 退出码

| 退出码 | 含义 |
| --- | --- |
| `0` | 本机**适用且有样本**的检查全部通过 |
| `1` | 有本机适用的检查**失败**或**读不到** |
| `2` | **缺工具链**：dotnet 找不到 / 探针构建失败 / 探针跑不起来 |

> 注意：`0` **不等于**「矩阵全部验证通过」。报告里「不适用」「无样本」「需人工介入」
> 三种状态的格子都**不算验证通过**，只是本机没有验证条件 —— 哪些格子还欠着，看
> `axis-coverage-matrix.md`。

## 报告里怎么读

每项检查有一个**结论**标签，含义严格区分：

| 标签 | 含义 |
| --- | --- |
| **通过** | 检查真跑了，判据满足 |
| **失败** | 检查真跑了，判据没满足（退出码 1） |
| **读不到** | 检查适用、但读不到值。报告会写清是**缺权限 / 无该传感器 / 未采集**哪一种（退出码 1） |
| **无样本** | 检查适用，但这台机器上没有可验证的对象（例如没插未收录的接收器）。**不算通过** |
| **需人工介入** | 必须由人做（涉及未签名驱动 / 用户显式开关 / 换硬件） |
| **未执行** | 探针被 `-SkipProbes` 跳过，或工具链缺失 |
| **不适用** | 这台机器不具备该轴的验证条件（例如 Intel 机器上的 F1） |

**读不到 ≠ 0。** 报告不会用 0 或默认值充数：传感器有条目但读不到值，写「（空：读不到 ——
缺权限 / 未采集）」；压根没有条目，写「无该传感器」。

## 怎么回填（在别的机型上跑完之后）

1. 在目标机器上跑一次脚本（见上面「怎么跑」）。
2. 拿到报告后，对照 `axis-coverage-matrix.md`：
   - 该格的「本机状态」从「待其它机型验」改成「已验（机器 + 日期）」；
   - 结论写「通过 / 失败 / 读不到」，**失败和读不到都要写清原因**。
3. 把新报告放进 `docs\verification`（脚本默认就这么放），并在
   `axis-coverage-matrix.md` 里补上对应行。
4. 命中 **L4**（没被认出来的 2.4G 接收器）时：报告里已经写好了该设备的
   VID/PID + 描述符摘要（usage page:usage + 报告长度），
   按 `docs\peripheral-protocols.md` 的模板补一条协议表项，再复测一次。

## 各轴需要什么机器 / 什么人工动作

| 轴 | 需要什么 | 能不能全自动 |
| --- | --- | --- |
| **F1** AMD 平台 | 一台 AMD 机器（Zen 最好，有 CCD 条目） | 大部分能自动；**「SMN 直读实际可读性」不行** —— 那条通道要加载未签名驱动并映射物理内存，2026-11-23 造成过三次 0x1EA 蓝屏，必须由用户在设置中心显式开启 + 管理员运行产品来验，脚本不代劳 |
| **F2** Intel 桌面 | 一台**台式机**（无内置电池、无核显） | 能自动 |
| **F3** 核显 + 独显 | 一台同时插着核显与独显的机器（混合显卡笔记本，或核显 + 桌面独显） | 能自动 |
| **F4** 其它 2.4G 接收器 | 一款**协议表里还没有**的 2.4G 接收器 | 能自动（脚本会直接给出补表项所需的 VID/PID/描述符） |

## 前置条件

- **dotnet**：必须有 SDK。脚本会自己找，找不到就用 `-Dotnet` 指定全路径。
- **管理员权限**：只影响 CPU MSR 温度能不能读到。**不是**管理员时报告写「读不到（缺权限）」，
  这是事实陈述、不是失败 —— 产品此时应当显示「--」。
- 脚本**只做采集与报告，不修改任何产品代码**。

## macOS 上的核对（C1）

阶段 F 是「多机型」，C1 是「macOS 真机」—— 两件事的流程不同，但回填到同一个目录：

| 项 | 阶段 F（多机型） | C1（macOS 真机） |
|---|---|---|
| 在哪跑 | 对应的 Windows 机器 | 你的 Mac |
| 跑什么 | `powershell -ExecutionPolicy Bypass -File tools\verify-matrix.ps1` | `bash tools/mac-selfcheck.sh` |
| 产物 | `docs/verification/<机型>-<日期>.md` | `docs/verification/mac-<日期>.md` |

### C1 脚本验什么

三件事，每件都给出「**通过 / 不通过 / 没法核对**」三态 —— 没法核对**不会被写成通过**：

1. **① Feature 报告第 0 字节是不是报告 ID** —— 自动比对探针输出里报告的第 0 字节与请求的报告 ID。
2. **② `thermal: cpu source` 是否指向 `PMU tdie`** —— 解析探针输出的那一行（口径严格：选中的不是
   `PMU tdie` 即判不通过，报告会打印实测来源供人判断）。
3. **③ 设备页读不到电量时显示「未知」而不是 0%** —— 分三段验：
   - **静态**：源码里读不到的分支写的是 `LevelText = "未知"`，渲染优先用 LevelText 而不是百分比；
   - **运行时**：探针输出里没有假读数；
   - **画面**：**脚本替不了人看屏幕** —— 报告里留了勾选位，请打开应用的设备页确认显示的是「未知」两个字。

### 用法（在你的 Mac 上，仓库根目录）

~~~bash
bash tools/mac-selfcheck.sh
~~~

脚本**幂等**（重复跑不产生垃圾文件），失败退出码非 0；
缺 clang / Xcode 命令行工具 / 探针编译不过时，会**如实报告缺什么**，不会跳过装作通过。
把产出的 `docs/verification/mac-<日期>.md` 提交回仓库（或直接贴给我）即可回填 C1。
