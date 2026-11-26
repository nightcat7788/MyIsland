# 阶段 F 验证矩阵报告 —— Microsoft Windows 11 家庭版 中文版 10.0.26300

- 采集时间：2026-11-27 02:26:18
- 机器名：FLY-NB  型号：21TK  厂商：LENOVO
- CPU：Intel(R) Core(TM) Ultra 5 225H（厂商：GenuineIntel，逻辑核：14，物理核：14）
- 显卡：GameViewer Virtual Display Adapter / Intel(R) Arc(TM) 130T GPU (16GB)（核显 1 块 / 独显 0 块，判定方式：D3D 适配器的 Integrated 标志（产品 IntelComputePolicy 同一口径））
- 电池：有（本机是移动平台）（Win32_Battery 实例 1 个 / MSBatteryClass 内置电池实例 0 个）
- 主板：LENOVO LNVNB161216
- 内存条：2 条
- 管理员权限：无（MSR 温度读不到，属正常：产品此时显示 --）
- 工具链：D:\YQDESK\.dotnet\dotnet.exe

> 诚实性说明：**读不到就是读不到**（会写清是「缺权限 / 无该传感器 / 未采集」哪一种），绝不用 0 或默认值充数。
> 「不适用」「无样本」「通过」是三件不同的事，不混着写。

## 一、轴覆盖总览

| 轴 | 适用性 | 依据 | 通过 / 失败 / 读不到 / 无样本 / 未执行 / 不适用 |
| --- | --- | --- | --- |
| F1 | 不适用 | 本机 CPU 厂商 = GenuineIntel（非 AMD） | 通过 2 · 失败 0 · 读不到 0 · 无样本 0 · 未执行 0 · 不适用 3 |
| F2 | 不适用 | 本机有内置电池（移动平台） | 通过 0 · 失败 0 · 读不到 0 · 无样本 0 · 未执行 0 · 不适用 3 |
| F3 | 不适用 | 本机核显 1 块、独显 0 块（判定方式：D3D 适配器的 Integrated 标志（产品 IntelComputePolicy 同一口径）） | 通过 0 · 失败 0 · 读不到 0 · 无样本 0 · 未执行 0 · 不适用 3 |
| F4 | **适用** | 任何机器都能跑协议表自检与命中分层 | 通过 2 · 失败 0 · 读不到 0 · 无样本 1 · 未执行 0 · 不适用 0 |

## F1 · AMD 平台

- 适用性：**本机不适用** —— 本机 CPU 厂商 = GenuineIntel（非 AMD）
- 口径：判据来自产品真实路径：`src\Island.App\Services\InpOutSmnService.cs`（SMN 直读）、`CpuTemperatureRule.cs` / `HardwareSensorRules.cs`（温度取舍）、`SmnChannel.cs`（开关与生命周期）。

| # | 检查项 | 判据 | 实测值 | 结论 | 证据 |
| --- | --- | --- | --- | --- | --- |
| F1-1 | SMN 通道前置判定与驱动残留 | ① 厂商判定取自注册表 VendorIdentifier（与产品 InpOutSmnService.IsAmdCpu 同一口径）；② 非 AMD 机器上该通道在 LoadLibrary / 装驱动 / 映射物理内存**之前**就跳过；③ 机器上不应残留未签名的 inpoutx64.sys（2026-11-23 三次 0x1EA 蓝屏的物证路径）。 | vendor=GenuineIntel；SMN 开关：settings.json 里 EnableSmnDirectRead = False；未签名驱动残留 inpoutx64.sys=无；仓库 tools\inpout\inpoutx64.dll=有 | **通过** | 附录 D |
| F1-2 | SMN 直读实际可读性 | AMD 机器上：用户在设置中心显式开启 EnableSmnDirectRead + 以管理员运行时，产品应能读出 CPU 温度。 | 本机 CPU 厂商 = GenuineIntel（非 AMD）⇒ 产品 InpOutSmnService.EnsureLoaded 第一步就跳过（不碰驱动） | **不适用** | 附录 D |
| F1-3 | CCD 温度传感器 | AMD 机器上：LHM 的 CPU 温度传感器里若有 CCD 条目，必须能读到值；没有 CCD 条目就如实记「该机未暴露 CCD 传感器」（不是 0）。 | 本机是 GenuineIntel（非 AMD）⇒ 无 CCD 传感器可言 | **不适用** | 附录 A |
| F1-4 | Tctl / Tdie 取舍 | AMD 机器上：产品规则（HardwareSensorRules.Pick(CpuTemperature) / CpuTemperatureRule.Pick）选中的传感器必须符合层序 package → tdie → tctl/tdie → tctl → ccd → core max → 逐核 → average；Tctl 不得排在 Tdie 前面；「Distance to TjMax」不得被选中。 | 本机是 GenuineIntel（非 AMD）⇒ Tctl/Tdie 取舍不适用 | **不适用** | 附录 A |
| F1-5 | CPU 温度取值规则自检（无读数时必须返回「未选到」） | 产品规则在没有有效读数时必须返回「未选到」，绝不能返回 0 或默认值；两套规则（CpuTemperatureRule 与 HardwareSensorRules）口径一致。 | CPU 温度传感器 31 个条目、有效读数 0 个（缺权限 / 未采集）⇒ CpuTemperatureRule 返回「未选到」，HardwareSensorRules 返回「未选到」：**没有伪造读数**，符合预期 | **通过** | 附录 A |

## F2 · Intel 桌面平台

- 适用性：**本机不适用** —— 本机有内置电池（移动平台）
- 口径：判据来自产品真实路径：`src\Island.App\Services\DeviceService.cs`（Win32_Battery / MSBatteryClass 的内置电池判定）、`ThermalService.cs`（主板 SuperIO 温度）、`src\Island.Native\GpuUsageReader.cs`（GPU 名称选择）。

| # | 检查项 | 判据 | 实测值 | 结论 | 证据 |
| --- | --- | --- | --- | --- | --- |
| F2-1 | 无电池路径（内置电池读数应当不存在，而不是 0） | 台式机上：Win32_Battery 必须**没有实例**（不是 0%），root\WMI 的 MSBatteryClass 也不得有内置电池实例；产品 DeviceService.ReadWmiBattery 返回 null ⇒ 界面不显示电池，而不是显示 0%。 | 本机 Win32_Battery 有 1 个实例、MSBatteryClass 内置电池实例 0 个 ⇒ 是笔记本，F2 轴前提不成立 | **不适用** | 附录 D |
| F2-2 | 主板 / SuperIO 温度 | 台式机上：LHM 主板硬件（含子硬件）里的温度传感器要么读到值，要么如实显示「--」；**没有条目 ≠ 0**，绝不能显示 0 ℃。 | 本机是笔记本（F2 轴前提不成立）。顺带事实：LHM 主板硬件「Lenovo LNVNB161216」及其子硬件里**没有任何温度传感器条目** ⇒ 产品必须显示「--」（无传感器 ≠ 0） | **不适用** | 附录 A |
| F2-3 | 无核显路径下的 GPU 选择口径 | 桌面机上（只有独显）：产品显示的 GPU 名字必须是那块独显；虚拟显示器 / 基础显示适配器不得顶掉它。 | 本机是笔记本（F2 轴前提不成立）。顺带事实：显卡清单：GameViewer Virtual Display Adapter / Intel(R) Arc(TM) 130T GPU (16GB)；产品口径（CounterSampleCache.GuessGpuName：跳过 Virtual / Basic Display / Remote Display / Indirect，取第一个其余适配器）选中「Intel(R) Arc(TM) 130T GPU (16GB)」 | **不适用** | 附录 A |

## F3 · 核显 + 独显

- 适用性：**本机不适用** —— 本机核显 1 块、独显 0 块（判定方式：D3D 适配器的 Integrated 标志（产品 IntelComputePolicy 同一口径））
- 口径：判据来自产品真实路径：`src\Island.Native\GpuUsageReader.cs`（显示名与占用口径）、`src\Island.App\Services\ThermalService.cs`（多 GPU 的温度取舍与显存口径）、`HardwareSensorRules.cs`（显存语义分层）。

| # | 检查项 | 判据 | 实测值 | 结论 | 证据 |
| --- | --- | --- | --- | --- | --- |
| F3-1 | 应用实际选了哪块 GPU | 多卡时三个口径分别算出来并互相对照：① 显示名口径 = CounterSampleCache.GuessGpuName（跳过 Virtual / Basic Display / Remote Display / Indirect，取第一个其余适配器）；② 温度口径 = ThermalService 的「优先选功耗>0 的那块（独显），否则取先遇到的那块」；③ 占用口径 = PDH 最忙引擎（全体取 max，应用只显示一个百分比）。三者指向不同的物理卡时必须写进报告（事实，不是失败）。 | 本机不满足「核显 + 独显」前提（核显 1 块、独显 0 块）。顺带事实：显示名口径 →「Intel(R) Arc(TM) 130T GPU (16GB)」；温度口径（ThermalService：优先功耗>0 的那块）→ （无 —— 没有一块 GPU 有温度读数）；占用口径（PDH 最忙引擎，应用只显示一个百分比）→ 47.49%（分适配器：0x00000000_0x00010E9D=51.88%、0x00000000_0x000113E7=0%） | **不适用** | 附录 A |
| F3-2 | 显存类型 / 容量读数是否可信 | 按产品规则（HardwareSensorRules 的 GpuMemoryUsed / GpuMemoryTotal：厂商显存 → D3D 专用显存 → D3D 共享显存）选出的读数：总量必须 > 0；两者都有值时已用必须 ≤ 总量；没有该传感器就记「无该传感器」，不填 0。 | 本机不满足「核显 + 独显」前提。顺带事实：「Intel(R) Arc(TM) 130T GPU (16GB)」已用 D3D Shared Memory Used=1.23 GB / 总量 D3D Shared Memory Total=17.95 GB（核显上是 **共享内存** 口径，不是独显专用显存） | **不适用** | 附录 A |
| F3-3 | 两块 GPU 的占用 / 温度读数是否能区分 | 温度必须能区分（LHM 每块 GPU 各有自己的温度传感器）；占用的可区分性按产品现状记录（应用只显示一个百分比 = 最忙引擎 max，不做每卡拆分 —— 这是事实，不是失败）。 | 本机不满足「核显 + 独显」前提。顺带事实：温度：每块 GPU 各读各的（「Intel(R) Arc(TM) 130T GPU (16GB)」无温度传感器（读不到，不是 0））⇒ 温度**可以**区分；占用：应用只显示一个百分比（PDH 最忙引擎 max），**不能**按卡拆分 | **不适用** | 附录 A |

## F4 · 其它 2.4G 接收器

- 适用性：**本机适用** —— 任何机器都能跑协议表自检与命中分层
- 口径：判据来自产品真实路径：`src\Island.App\Services\Peripheral\*`（L1 标准 HID 电量 → L3 协议表 → L4 名称兜底）、`hid-probe --selftest`（表校验 + 两路复核）、`hid-probe unknown`（未识别设备摘要）。

| # | 检查项 | 判据 | 实测值 | 结论 | 证据 |
| --- | --- | --- | --- | --- | --- |
| F4-1 | 每台 HID 设备的命中层 | 每台插着的 HID 设备都必须能归类（L1 标准 HID 电量 / L3 厂商私有协议表 / L4 名称兜底 / 未命中）；判定口径必须是产品同一套。 | 未命中 0488:1054 「HIDI2C Device」；L3 17EF:F006 「Idea5003」（共 2 台，hid-probe unknown 退出码 0） | **通过** | 附录 C |
| F4-2 | L4（未被认出来）的设备是否给出了补协议表项所需的输入 | 每个命中 L4 的设备都必须有 VID/PID + 描述符摘要（usage page:usage + 报告长度）—— 这正是按 docs\peripheral-protocols.md 的模板补一条协议表项需要的输入；命中 L4 ⇒ 明确提示「这台没被认出来，需要补一条协议表项」。 | 本机没有命中 L4 的设备（插着的联想 Idea5003 2.4G 接收器已命中 L3 协议表项）⇒ 本格**无样本**：「换一款未收录的 2.4G 接收器」这一格仍未验证。 | **无样本** | 附录 C |
| F4-3 | 协议表自检与产品路径两路复核 | hid-probe --selftest 的协议表校验必须 0 错误，且产品路径（PeripheralBatteryService）与工具路径的读数必须一致（有没有读数 / 设备多寡不一致 = 失败；只是数值不同 = 提示）。 | --selftest 退出码 0 ⇒ 协议表：内置表 合格（错误 0，警告 0）；用户表 合格（错误 0，警告 0）；设备命中：共 2 台 —— L1 0 台 / L3 1 台 / L4 0 台 / 未命中 1 台；两路复核（工具 vs 产品，只比较读数）：一致（命中层与命中的表项产品侧不带 ⇒ 不比较）； | **通过** | 附录 B |

## 七、结论

- 逐项合计：通过 4 · 失败 0 · 读不到 0 · 无样本 1 · 需人工介入 0 · 未执行 0 · 不适用 9
- 本机**适用且有样本**的检查全部通过；「不适用」说明这台机器不具备验证条件，「无样本」说明条件具备但本机没有可验证的对象 —— 两者都**不算验证通过**。
- 本脚本不修改产品代码，只做采集、检查与报告。

## 附录 A · lhm-probe matrix（CPU + GPU + 主板传感器快照）

管理员权限：无（读 MSR 温度多半读不到）；LHM Computer 打开：成功

**CPU 温度传感器**（产品口径：HardwareSensorRules.Pick(CpuTemperature)）

| 传感器 | 读数 |
| --- | --- |
| Core Max | （空：读不到 —— 缺权限 / 未采集） |
| Core Average | （空：读不到 —— 缺权限 / 未采集） |
| P-Core #1 | （空：读不到 —— 缺权限 / 未采集） |
| P-Core #2 | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #1 | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #2 | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #3 | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #4 | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #5 | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #6 | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #7 | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #8 | （空：读不到 —— 缺权限 / 未采集） |
| P-Core #3 | （空：读不到 —— 缺权限 / 未采集） |
| P-Core #4 | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #9 | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #10 | （空：读不到 —— 缺权限 / 未采集） |
| CPU Package | （空：读不到 —— 缺权限 / 未采集） |
| P-Core #1 Distance to TjMax | （空：读不到 —— 缺权限 / 未采集） |
| P-Core #2 Distance to TjMax | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #1 Distance to TjMax | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #2 Distance to TjMax | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #3 Distance to TjMax | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #4 Distance to TjMax | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #5 Distance to TjMax | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #6 Distance to TjMax | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #7 Distance to TjMax | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #8 Distance to TjMax | （空：读不到 —— 缺权限 / 未采集） |
| P-Core #3 Distance to TjMax | （空：读不到 —— 缺权限 / 未采集） |
| P-Core #4 Distance to TjMax | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #9 Distance to TjMax | （空：读不到 —— 缺权限 / 未采集） |
| E-Core #10 Distance to TjMax | （空：读不到 —— 缺权限 / 未采集） |

- 规则选中（CpuTemperatureRule）：未选到（读数未知，界面显示 --）
- 规则选中（HardwareSensorRules，运行期用的那套）：未选到（读数未知，界面显示 --）
- CCD 条目：无（该机未暴露 CCD 传感器 —— 不是 0）

**GPU 硬件**

- 「Intel(R) Arc(TM) 130T GPU (16GB)」（GpuIntel）
  - 温度传感器：（无 —— 该卡没有温度传感器，读不到，不是 0）
  - 功耗传感器：GPU Power=0
  - 显存（SmallData）传感器：D3D Shared Memory Total=18384.419921875、D3D Shared Memory Free=(空)、D3D Shared Memory Used=1258.4140625
  - 规则选中：温度=未选到；功耗=GPU Power=0；显存已用=D3D Shared Memory Used=1258.4140625 MB；显存总量=D3D Shared Memory Total=18384.419921875 MB

**主板 / SuperIO**

- 主板硬件：Lenovo LNVNB161216
- 温度传感器条目数：0
- 规则选中：未选到（没有条目，或读不到 —— 两者都不是 0）

**显卡选择（产品口径：Island.Native\CounterSampleCache）**

- WMI 显卡清单：GameViewer Virtual Display Adapter / Intel(R) Arc(TM) 130T GPU (16GB)
- 产品选中的名字：Intel(R) Arc(TM) 130T GPU (16GB)
- 占用（应用显示的那个百分比）：47.49 %
- 分适配器 3D 占用（按产品同一套实例名分组口径算出来的，应用本身不显示）：0x00000000_0x00010E9D = 51.88%、0x00000000_0x000113E7 = 0%
- D3D 适配器（Integrated 标志 = 核显判定）：\\?\ROOT#DISPLAY#0000#{1ca05180-a699-450a-9a0c-de4fbe3ddd89} ⇒ integrated=False，软件适配器=True；\\?\ROOT#BasicRender#0000#{1ca05180-a699-450a-9a0c-de4fbe3ddd89} ⇒ integrated=False，软件适配器=True；\\?\PCI#VEN_8086&DEV_7D51&SUBSYS_384F17AA&REV_03#3&11583659&0&10#{1ca05180-a699-450a-9a0c-de4fbe3ddd89} ⇒ integrated=True，软件适配器=False

## 附录 B · hid-probe --selftest 原始输出

~~~
=== 外设协议表自检（hid-probe --selftest）===
时间：2026-11-27 02:26:17

---- 一、协议表校验 ----
[内置表]  错误 0 / 警告 0 / 信息 0  → 合格
[C:\Users\FLY\AppData\Local\MyIsland\peripheral-protocols.json]  错误 0 / 警告 0 / 信息 1  → 合格
   信息  (文件) = C:\Users\FLY\AppData\Local\MyIsland\peripheral-protocols.json：用户协议表文件不存在（当前没有用户条目，走内置表）

---- 二、生效的协议表（与产品同一套匹配规则：用户条目在前，先命中先用）----
  · lenovo-idea5003（内置表）  vid=17EF pid=F006 usage=FFCF:00A0 minReportLength=61 transport=Feature 验证=已实机验证（2026-11-26 / 联想 Idea5003 2.4G 接收器（VID 17EF / PID F006））  电量偏移=5 掩码=0xFF 量程=0..100 直读报告=193

---- 三、真机命中报告（本机当前连接的外设）----

[设备] HIDI2C Device
  VID=0488 PID=1054  集合数=5  类型=other
  集合：000D:0005(in=20,out=0,feat=257)  000D:000E(in=0,out=0,feat=2)  FF01:0002(in=0,out=0,feat=531)  FF00:0001(in=18,out=18,feat=0)  0001:0002(in=5,out=0,feat=0)
  ─ L1 标准 HID 电量：未命中（能力表里没有电量 usage：0006:0020 / 000D:003B / 0085:0065）
  ─ 命中层：未命中（没有标准 HID 电量 usage，也不在协议表里，名称也不像外设接收器）

[设备] Idea5003
  VID=17EF PID=F006  集合数=3  类型=other
  集合：FFCF:00A0(in=17,out=137,feat=61)  FF50:0003(in=0,out=0,feat=17)  00FF:0000(in=256,out=256,feat=256)
  ─ 表项对账（该设备 vid/pid 对得上的全部表项，含没被选中的）：
      · lenovo-idea5003（内置表）  vid=17EF pid=F006 usage=FFCF:00A0 minReportLength=61 transport=Feature 验证=已实机验证（2026-11-26 / 联想 Idea5003 2.4G 接收器（VID 17EF / PID F006））  电量偏移=5
  ─ L1 标准 HID 电量：未命中（能力表里没有电量 usage：0006:0020 / 000D:003B / 0085:0065）
  ─ L3 厂商私有协议表：命中「lenovo-idea5003」（来源：内置表）
      表项：vid=17EF pid=F006 usage=FFCF:00A0 minReportLength=61 transport=Feature 验证=已实机验证（2026-11-26 / 联想 Idea5003 2.4G 接收器（VID 17EF / PID F006））
      实测：Feature 报告 193 的偏移 5 处读到 0x3A（取掩码 0xFF 后 = 58）
      推断：按表项 valueOffset=5 / 掩码 0xFF / 量程 0..100 换算 ⇒ 58%（**推断值**：该字节是不是电量由表项说了算，未经设备描述符确认）
      充电位：未知（本表项未声明 chargingOffset）
      命中层：L3（厂商私有协议表）

---- 四、产品路径复核（PeripheralBatteryService：产品自己那条路）----
[log] HID 外设电量：联想 Idea5003 2.4G 接收器 58%
  名称=联想 Idea5003 2.4G 接收器 类型=other 电量=58% 档位=- 充电=False 观测于=02:26:17
  逐设备对齐（只比较读数：有没有读数 + 数值。产品的 DeviceInfo 不带 VID:PID、命中层与命中的表项 ⇒ 那些比不了，按设备名对齐）：
    · 0488:1054：工具=未命中 / 未知；产品=（无条目）⇒ ✓ 读数一致（两路都没产出读数）
    · 17EF:F006：工具=L3 / lenovo-idea5003 / 58%；产品=联想 Idea5003 2.4G 接收器 / 58% ⇒ ✓ 读数一致

---- 汇总 ----
  协议表：内置表 合格（错误 0，警告 0）；用户表 合格（错误 0，警告 0）
  设备命中：共 2 台 —— L1 0 台 / L3 1 台 / L4 0 台 / 未命中 1 台
  两路复核（工具 vs 产品，只比较读数）：一致（命中层与命中的表项产品侧不带 ⇒ 不比较）
  结论：表校验通过、两路复核一致 ⇒ 退出码 0

~~~

## 附录 C · hid-probe unknown 原始输出（未识别设备摘要）

~~~
# hid-probe unknown —— 未识别设备摘要（阶段 F / F4 的输入）
# 时间：2026-11-27 02:26:17
# 判定口径：产品同一套（L1 标准 HID 电量 → L3 厂商私有协议表 → L4 名称兜底 → 未命中）
# 字段：[DEVICE] layer=… vid=… pid=… name="…" kind=… collections=… needsProtocolEntry=…
#       [COLLECTION] usagePage=… usage=… in=… out=… feat=… vendor=…
[DEVICE] layer=未命中 vid=0488 pid=1054 name="HIDI2C Device" kind=other collections=5 needsProtocolEntry=false
  [COLLECTION] usagePage=000D usage=0005 in=20 out=0 feat=257 vendor=false product="HIDI2C Device" desc="@input.inf,%hid_device_touch_pad%;HID-compliant touch pad"
  [COLLECTION] usagePage=000D usage=000E in=0 out=0 feat=2 vendor=false product="HIDI2C Device" desc="@mtconfig.inf,%mtconfig\com1.devicedesc%;Microsoft Input Configuration Device"
  [COLLECTION] usagePage=FF01 usage=0002 in=0 out=0 feat=531 vendor=true product="HIDI2C Device" desc="@input.inf,%hid_device_vendor_defined_range%;HID-compliant vendor-defined device"
  [COLLECTION] usagePage=FF00 usage=0001 in=18 out=18 feat=0 vendor=true product="HIDI2C Device" desc="@input.inf,%hid_device_vendor_defined_range%;HID-compliant vendor-defined device"
  [COLLECTION] usagePage=0001 usage=0002 in=5 out=0 feat=0 vendor=false product="HIDI2C Device" desc="@msmouse.inf,%hid.mousedevice%;HID-compliant mouse"
[DEVICE] layer=L3 vid=17EF pid=F006 name="Idea5003" kind=other collections=3 needsProtocolEntry=false entry="lenovo-idea5003"
  [COLLECTION] usagePage=FFCF usage=00A0 in=17 out=137 feat=61 vendor=true product="Idea5003" desc="@input.inf,%hid_device_vendor_defined_range%;HID-compliant vendor-defined device"
  [COLLECTION] usagePage=FF50 usage=0003 in=0 out=0 feat=17 vendor=true product="Idea5003" desc="@input.inf,%hid_device_vendor_defined_range%;HID-compliant vendor-defined device"
  [COLLECTION] usagePage=00FF usage=0000 in=256 out=256 feat=256 vendor=false product="Idea5003" desc="@input.inf,%hid_device%;HID-compliant device"
# 合计 total=2 L1=0 L3=1 L4=0 未命中=1 needsProtocolEntry=0

~~~

## 附录 D · 本报告用到的产品口径（源码位置）

| 检查 | 产品里的依据 |
| --- | --- |
| CPU 温度取值 | src\Island.App\Services\HardwareSensorRules.cs（运行期用它）· src\Island.App\Services\CpuTemperatureRule.cs（分层口径一致） |
| CPU 温度的干扰项排除 | 同上（Distance to TjMax / SoC / VRM / 水泵 / 风扇） |
| SMN 直读 | src\Island.App\Services\InpOutSmnService.cs（先判厂商再碰驱动）· src\Island.App\Services\SmnChannel.cs（默认关闭） |
| 主板 / SuperIO 温度 | src\Island.App\Services\ThermalService.cs（主板子硬件逐个收集） |
| 显存语义分层 | src\Island.App\Services\HardwareSensorRules.cs（GpuMemoryUsed / GpuMemoryTotal） |
| GPU 名称与占用 | src\Island.Native\GpuUsageReader.cs（GuessGpuName / 最忙引擎口径） |
| 多 GPU 的温度取舍 | src\Island.App\Services\ThermalService.cs（优先功耗>0 的那块） |
| 内置电池判定 | src\Island.App\Services\DeviceService.cs（Win32_Battery + MSBatteryClass + IsInternalBattery） |
| 外设命中分层 | src\Island.App\Services\Peripheral\*（StandardHidBattery / PeripheralProtocols / PeripheralClassifier） |

## 下一步

1. 把本报告归档到 docs\verification\（同名文件已存在时脚本会自动加 -2 / -3 后缀，不覆盖旧报告）。
2. 在缺的机型上重复跑一次本脚本：AMD 机（F1）/ Intel 台式机（F2）/ 核显+独显机（F3）/ 换一款未收录的 2.4G 接收器（F4）。
3. F1 的「SMN 直读实际可读性」必须在**用户显式开启开关 + 管理员运行**的条件下由产品自测 —— 脚本不代劳（未签名驱动 + 物理内存映射 = 0x1EA 蓝屏）。
