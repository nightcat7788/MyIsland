# 外设协议表：给外设电量"加一条品牌"的可验证做法

> **真相源**
> · 表结构 / 装载 / 三层校验：`src/Island.App/Services/Peripheral/PeripheralProtocol.cs`
> · 校验层（字段级报错、覆盖关系）：`src/Island.App/Services/Peripheral/PeripheralProtocolTable.cs`
> · 自检工具：`tools/hid-probe`（`--selftest`）
> · 用户表：`%LOCALAPPDATA%\MyIsland\peripheral-protocols.json`（macOS：应用数据目录下的同名文件）
>
> **这份文档解决什么**：以前"给协议表加一条品牌"是**盲写** —— 填完只能靠用户自己看界面像不像对。
> 现在它**可验证**：每条表项都必须自报**验证状态**（已实机验证 / 未验证），
> 自检工具会把状态和证据逐条打出来，未验证的条目**不允许**进内置表。

---

## 0. 现状（2026-11-26）

| 条目 | 在哪 | 验证状态 | 证据 |
|---|---|---|---|
| `lenovo-idea5003` | **内置表** | **已实机验证** | 2026-11-26 · 联想 Idea5003 2.4G 接收器（VID `17EF` / PID `F006`）· 读出 **58%**（**推断值**：偏移 5；依据是同型号蓝牙鼠标系统缓存 59%、差 1pp、闲置衰减一致；**未做同刻交叉验证**） |
| `logitech-hidpp-battery` | 候选（**不进匹配表**） | **未验证** | 本机没有罗技设备；HID++ 请求帧按公开协议写，回包里的电池字节位置**未确认** |
| `razer-8byte-battery` | 候选（**不进匹配表**） | **未验证** | 纯骨架：命令帧与电量偏移都**未探测** |

**内置表目前只有 1 条。** 别的品牌**没有实机验证过**，所以它们只是**候选模板**：
不进匹配表、不参与读数。要真正支持，得在**有那台设备**的机器上验证（§6）。

---

## 1. 验证状态：每条表项都必须自报家门

| 字段 | 取值 | 含义 |
|---|---|---|
| `verification` | `"verified"` / `"unverified"`（大小写不敏感，也认 `"已实机验证"` / `"未验证"`，也认 `1` / `0`） | 这条协议**有没有在真机上回包确认过**。**默认 `unverified`** —— 没验过就必须这么写 |
| `verificationNote` | 自由文本 | 验证证据：什么时候、在哪台设备上、回包读到什么 |
| `verifiedOn` | `"YYYY-MM-DD"` | 验证日期 |
| `verifiedDevice` | 自由文本 | 验证时用的设备型号 |

**规则（代码里强制，不是口头约定）**

1. **内置表只装载 `verified` 的条目**（`PeripheralProtocols.IsBuiltInEligible`）。
   有人不小心把一条没验过的表项写进内置表 ⇒ 它**不会生效**，设备回落到 L4 兜底显示"未知"，
   **不会**输出一个没验证过的读数。
2. **校验层会点名**：`verification` 为 `unverified` ⇒ 警告（提醒先实机验证）；
   标成 `verified` 却没写 `verificationNote` ⇒ 警告（别人没法判断它是不是真验过）。
3. **自检工具逐条打印**：`hid-probe --selftest` 的"生效的协议表"一节里，每条都会带
   `验证=已实机验证（2026-11-26 / 联想 Idea5003 2.4G 接收器）` 或 `验证=未验证`。

> **为什么不能拿"行业惯例值"当读数**：偏移猜错 → 读出另一个字节 → 界面显示一个**看着很正常的假百分比**。
> 这比"读不到"糟糕得多（读不到至少是诚实的）。所以本项目的底线是：
> **没验过的协议，宁可显示"未知"，也不显示一个数字。**

---

## 2. 判定顺序：三层（谁先命中谁说了算）

| 层 | 依据 | 要不要协议表 | 证据等级 |
|---|---|---|---|
| **L1** | 设备报告描述符里声明的**标准电量 usage**（`0x06:0x20` / `0x0D:0x3B` / `0x85:0x0065`） | **不要** | 实测（系统按描述符解析，不猜偏移） |
| **L3** | 协议表（内置表 + 用户表） | 要 | 推断（偏移是表项说了算） |
| **L4** | 名称里像"接收器/无线/2.4G" | 不要 | 未知（只认设备，**不报电量**） |

**L1 是最省事的一条路**：设备自己在描述符里声明了标准电量 usage，**一条表项都不用写**就能读出电量。
所以"支持更多接收器"的第一件事是 **`hid-probe usage <序号>` 看一下 L1 能不能命中**，
命中了就别去写协议表。

**三层校验（协议表命中之后）**：回显 → 范围 → 充电位，**任何一层不过就当"读不到"**，
绝不返回 0% 或 100% 充数。这次新增的字段（`echoOffset` / `chargingBitOffset`）**没有放松**这三条，
只是让"回显在第几个字节""充电位是第几位"能表达出来。

---

## 3. 表在哪、怎么被装载

* **内置表**：代码里（`PeripheralProtocols.BuiltinEntries()`），只收 `verified` 的条目。
* **用户表**：`%LOCALAPPDATA%\MyIsland\peripheral-protocols.json`（macOS：应用数据目录下的同名文件）。
  一个 JSON 数组，每个元素一条表项。文件坏了 ⇒ **保留旧表**（不会把能用的配置弄没）。
* **生效顺序**：**用户条目在前，内置条目在后**，内置条目**一条都不删**。

---

## 4. 覆盖语义（2026-11-26 修复轮更正过，**别再写成"同 key 就整条替换"**）

覆盖**不是**"按 key 整条替换"，而是**逐设备、逐字段匹配**：

* 生效与否取决于**每一台设备**能不能被这条用户条目匹配上
  （`vid` / `pid` / `usagePage` / `usage` / `minReportLength` 等字段逐个比）。
* 用户写一条**同 key 但字段不同**的条目 ⇒ 它**匹配不上**那台设备 ⇒ **不生效**，
  产品实际仍会用内置条目。工具会明确报"**不生效 / 哪里对不上**"并拦门禁。
* 只有当用户条目**确实匹配得上**时，它才会顶掉内置条目在那台设备上的效果。
* 用户条目匹配不上时，内置条目照常顶上 ⇒ 设备不会被一条写歪的用户条目弄丢。

**要改内置条目的某个字段？把匹配条件一起写上**（`vid` / `pid` / `usagePage` / `usage` / `minReportLength`），
否则覆盖不会生效。自检工具会告诉你**具体哪里对不上**（`vid 不一致` / `usage 不一致` / …）。

---

## 5. 表结构：逐字段怎么填

| 字段 | 类型 | 默认 | 含义 / 怎么填 |
|---|---|---|---|
| `key` | 字符串 | **必填** | 唯一键，也是覆盖内置条目的依据 |
| `vendor` / `model` | 字符串 | `""` | 界面显示名（`vendor + model`）；都空就退回设备自身名字 |
| `kind` | 字符串 | `"other"` | `mouse` / `keyboard` / `headset` / `gamepad` / `other`（只影响图标） |
| `vid` / `pid` | 整数 | `0` | USB 厂商 / 产品 ID，**16 位**。`hid-probe scan` 抄 |
| `usagePage` / `usage` | 整数 / null | `null` | 控制集合的 usage；`null` = 不限（**不限更容易命中，但歧义校验会拦**） |
| `minReportLength` | 整数 | `0` | 集合 output 报告的最小长度（不够长就不可能是这个协议） |
| `transport` | 字符串 / 整数 | `"Control"` | `Control` / `Interrupt` / `Feature`（大小写不敏感，也认 `0`/`1`/`2`） |
| `preCommands` | 字符串数组 | `[]` | 正式查询前要先发的握手命令，每条一个十六进制串（如 `["05 01"]`） |
| `command` | 字符串 / 字节数组 | `[]` | 电量查询命令（**首字节是报告 ID**），如 `"11 03 00 0F ..."` |
| `echo` | 字符串 / 字节数组 | `[]` | 回包里必须出现的字节序列（回显校验）；空 = 不校验 |
| `echoOffset` | 整数 | `-1` | **新增**：回显必须**精确落在**这个偏移（`-1` = 不限位置）。回包里别处碰巧有同样的字节时用它防误认 |
| `featureReportId` | 整数 | `-1` | ≥ 0 = **直读**这个 Feature 报告并解析（不发命令）。不少厂商把配对设备的状态挂在 Feature 报告里（联想就是这样） |
| `valueOffset` | 整数 | `-1` | 电量字段在回包里的偏移（**首字节是报告 ID**）；`-1` = 这条协议不报电量 |
| `valueMask` | 整数 | `255` | 取位后再比范围（如 `15` = 取低半字节，HID++ 的电池档位常这样） |
| `valueMin` / `valueMax` | 整数 | `0` / `100` | 原始值量程；百分比 = `(raw - min) × 100 / (max - min)`（档位 0..4 也能换算成百分比） |
| `chargingOffset` | 整数 / null | `null` | 充电位所在字节；`null` = 协议不报充电状态 |
| `chargingMask` | 整数 | `1` | 充电位掩码，**只能是 1**（只认明确的 0/1；0 会永远读成"未充电"= 谎报，>1 会取到 2/3 让整帧作废） |
| `chargingBitOffset` | 整数 | `-1` | **新增**：充电位在该字节里的**位号**（0..7，`-1` = 走 `chargingMask` 老路）。HID++ 的充电标志常在第 4 位，没有它表达不出来 |
| `chargingIsOne` | 布尔 | `true` | 位为 1 表示在充电（反过来就写 `false`） |
| `milliVoltsOffset` | 整数 / null | `null` | 电压字段偏移（大端 16 位）；`null` = 不报电压 |
| `milliVoltsMask` / `milliVoltsScale` | 整数 | `65535` / `1` | 电压掩码与换算系数（原始值 × 系数 = 毫伏） |
| `verification` | 字符串 / 整数 | `"unverified"` | **新增**：验证状态（见 §1） |
| `verificationNote` / `verifiedOn` / `verifiedDevice` | 字符串 | `""` | **新增**：验证证据 |
| `preCommands` 之外的**未知字段** | — | — | 校验层会**警告**"未知字段（可能拼错了）"，不会被静默忽略 |

> 字节字段两种写法都认：`"C1 02 3A"`（十六进制串）或 `[193, 2, 58]`（数字数组）。

---

## 6. 加一条品牌：完整流程（探测 → 填表 → 验证 → 定版）

### 6.1 探测（在那台设备插着的机器上做）

```powershell
# 0) 列出所有 HID 集合，抄下它的序号、VID:PID、usage、报告长度
D:\YQDESK\.dotnet\dotnet.exe run --project tools\hid-probe\hid-probe.csproj -c Release -- scan

# 1) 先看 L1 能不能直接命中（能命中就别写协议表）
D:\YQDESK\.dotnet\dotnet.exe run --project tools\hid-probe\hid-probe.csproj -c Release -- usage <序号>

# 2) 有哪些 Feature 报告 ID / 报告多长
D:\YQDESK\.dotnet\dotnet.exe run --project tools\hid-probe\hid-probe.csproj -c Release -- feat <序号>
D:\YQDESK\.dotnet\dotnet.exe run --project tools\hid-probe\hid-probe.csproj -c Release -- scanfeat <序号>

# 3) 把特征报告按偏移逐字节列出来（找电量字节就靠它）
D:\YQDESK\.dotnet\dotnet.exe run --project tools\hid-probe\hid-probe.csproj -c Release -- off <序号>

# 4) 发一帧命令看回包（control / interrupt / feature 三种传输都试）
D:\YQDESK\.dotnet\dotnet.exe run --project tools\hid-probe\hid-probe.csproj -c Release -- probe <序号> "11 03 00 0F 00 00 00 00 00 00 00 00 00 00 00 00"

# 5) 批量试常见的"读状态"帧（只发查询帧，不写配置、不升级固件）
D:\YQDESK\.dotnet\dotnet.exe run --project tools\hid-probe\hid-probe.csproj -c Release -- sweep <序号>

# 6) 看设备主动上报什么（有的设备不收命令，只主动发）
D:\YQDESK\.dotnet\dotnet.exe run --project tools\hid-probe\hid-probe.csproj -c Release -- watch <序号> 3000

# 7) 全表自检（表校验 + 真机命中 + 工具/产品两路复核）
D:\YQDESK\.dotnet\dotnet.exe run --project tools\hid-probe\hid-probe.csproj -c Release -- --selftest
```

### 6.2 填表（写进用户表 JSON，先别改代码）

```json
[
  {
    "key": "my-vendor-receiver",
    "vendor": "某厂商",
    "model": "某接收器",
    "kind": "other",
    "vid": 4660,
    "pid": 62723,
    "usagePage": 65292,
    "usage": 160,
    "minReportLength": 64,
    "transport": "Feature",
    "command": "11 03 00 0F 00 00 00 00 00 00 00 00 00 00 00 00",
    "echo": "00 0F",
    "echoOffset": 2,
    "valueOffset": 8,
    "valueMask": 15,
    "valueMin": 0,
    "valueMax": 4,
    "chargingOffset": 8,
    "chargingBitOffset": 4,
    "verification": "unverified",
    "verificationNote": "2026-11-26 在 XXX 机器上探测：回包偏移 8 的低 4 位是电池档位，待与厂商 App / 系统显示交叉核对。"
  }
]
```

### 6.3 验证（在**有那台设备**的机器上）

1. `--selftest` 跑一遍：**表校验 0 错误**、**表项对账**那一节里偏移没有越界、
   "真机命中报告"里命中了 L3 并读出电量。
2. **两路复核**：工具路径与产品路径（`PeripheralBatteryService`）读数一致 ⇒ 退出码 0。
3. **交叉核对**：把读数与设备自己的 App / 系统设置里的电量对一下
   （差值 > 1 个百分点就说明偏移或量程还不对，**别急着把它标成 verified**）。
4. **充/放电各看一次**：充电位（`chargingOffset` / `chargingBitOffset`）要能来回变，
   不变的说明充电位找错了。

### 6.4 定版

* **验证通过** ⇒ 把 `verification` 改成 `"verified"`，补齐 `verificationNote` / `verifiedOn` / `verifiedDevice`。
  想让**所有人**都用上，就提一个 PR 把这条加进**内置表**（`PeripheralProtocols.BuiltinEntries()`）；
  内置表**只收 `verified` 的条目**，没标 `verified` 的会被装载层直接挡掉。
* **验证不过** ⇒ 别写进内置表。留在用户表里继续调，调通了再定版。

---

## 7. 候选条目（**未验证**，只当模板）

### 7.1 罗技 HID++（候选：`logitech-hidpp-battery`）

* **现状**：本机没有罗技设备，**未验证**。
* **协议背景**：HID++ 2.0 用 16 字节的 vendor feature report（报告 ID `0x11`），
  电量走"统一电池状态"功能（功能号 `0x0F`）。回包里会**在固定位置回显**特征索引 + 功能号。
* **能表达**：`transport: "Feature"`、`command`（16 字节请求帧）、`echo` + `echoOffset`（回显卡死位置）、
  `valueMask: 15`（低半字节）、`chargingBitOffset`（充电标志不在最低位）。
* **还不能表达 / 要靠实机确认**：
  * 回包里**电池字节的准确偏移**（固件不同可能不一样）；
  * **档位量程**（0..4 档位还是 0..100 百分比）；
  * **特征索引**是软件索引（随固件变），静态表项只能写死当前固件的值。

### 7.2 雷蛇 8 字节私有报告（候选：`razer-8byte-battery`）

* **现状**：本机没有雷蛇设备，**未验证**。
* **协议背景**：部分雷蛇接收器用 8 字节的私有报告。
* **能表达**：`minReportLength: 8`、`command`（8 字节帧）、`valueOffset` / `valueMask`。
* **还不能表达 / 要靠实机确认**：**命令帧**与**电量字节**都要靠 `probe` / `sweep` 探测。
  候选条目里命令帧是空的 ⇒ 照抄进用户表只会显示"未知"，**不会读出假读数**。

### 7.3 VGN 等其它 2.4G 接收器

没有公开协议文档 ⇒ **没有现成模板**，走 §6 的通用流程：
`scan` 抄 VID:PID → `off` / `watch` 找电量字节 → `probe` 试命令帧 → 填表 → 验证。
**不要**照抄别人的偏移：不同品牌的"偏移 5"完全是两回事。

---

## 8. 验证不通过怎么排查

| 症状 | 最可能的原因 | 怎么办 |
|---|---|---|
| 自检报"覆盖**不生效**" | 用户条目的 `vid`/`pid`/`usage`/`minReportLength` 与内置条目对不上 | 按工具指出的那一项改齐（覆盖是逐设备逐字段匹配的） |
| 自检报"偏移越界" | `valueOffset` / `chargingOffset` 超过报告长度 | `off <序号>` 看真实长度，改偏移 |
| 命中了协议表但"电量：未知" | 三层校验有一层没过（回显不匹配 / 值越界 / 充电位取值非法） | `probe` 看真实回包；先对回显，再对偏移，最后对量程 |
| 读数一直在 0% / 100% | 偏移取到了报告 ID 字节（偏移 0）或常量字节 | 用 `off` 的逐字节输出核对偏移；确认后**必须**重新验证 |
| 读数"看着很合理但不对" | 偏移偏了 1 个字节 / 量程写错（档位当百分比） | 与设备 App 的显示交叉核对；`valueMin`/`valueMax` 要按**真实量程**写 |
| 命令发出去没回包 | 传输方式不对 / 命令帧长度不等于集合的 output 报告长度 | `probe` 会把三种传输都试一遍；看哪一路有回包 |
| 充电状态永远是"未充电" | 充电位找错（`chargingOffset` 指错字节，或该用 `chargingBitOffset`） | 充/放电各看一次；注意 `chargingMask` 只认 `1` |

---

## 9. 已知限制（说清楚，别假装没有）

1. **静态表项**：HID++ 这类协议的**特征索引随固件变**，静态表项只能写死当前固件的值；固件升级后可能要改表。
2. **一个接收器配多只设备**：回包里靠**设备索引**区分，静态表项区分不了 ⇒ 这类设备只能读到一个值。
3. **电量字段只能取一个字节里的低位**（`valueMask` 掩码）。**高半字节**（高 4 位）暂不支持。
4. **macOS 侧只支持直读 Feature 报告**（`featureReportId`），命令模式（`command`）的 mac 探针还没做。
5. **未验证的条目一律不产出读数**（内置表直接不装载；用户表里未验证的条目产品也会照用，
   但校验层会一直警告，文档要求先实机确认再采信）。
6. **本机只有一台联想 Idea5003 接收器**，其它品牌的实机验证必须在**有那些设备的机器**上做。

---

## 10. 门禁

| 门禁 | 怎么跑 | 期望 |
|---|---|---|
| 表结构 / 装载 / 校验层 | `dotnet run --project tests\Island.Tests\Island.Tests.csproj -c Release` | 全过、退出码 0（含：新表结构、旧 JSON 向后兼容、验证状态字段的断言） |
| 真机自检 | `dotnet run --project tools\hid-probe\hid-probe.csproj -c Release -- --selftest` | 表校验 0 错误、两路复核一致、退出码 0 |
| 产品端到端 | `dotnet run --project tools\hid-probe\hid-probe.csproj -c Release -- e2e` | 本机联想接收器读出 **58%**（回归基准） |

`--selftest` 的输出里，每条表项都会带**验证状态**；
"表项对账"那一节会把**没被选中的**候选表项也拿来对着真机校验偏移越界；
"两路复核"会把工具路径与产品路径逐设备对齐，对不上就**拦门禁**（退出码非 0）。
