# Windows 代码签名流水线设计（MyIsland）

日期：2026-11-23
状态：已与用户确认方案（官方 signtool 路线），待实现

## 背景与问题

MyIsland 目前的发布产物**完全没有数字签名**：

| 产物 | 签名状态 |
|---|---|
| `MyIsland-1.0.1.exe`（Inno 安装包） | NotSigned |
| `publish\win-x64\Island.App.exe` | NotSigned |
| `MyIsland-1.0.1-portable.zip` | 无法签名（ZIP 格式本身不支持） |

普通用户双击安装包会看到「Windows 已保护你的电脑 / 未知发布者」警告，无法作为正式分发渠道。

### 三个必须先讲清的事实

1. **自签证书不能让普通用户消除「未知发布者」警告**。那是公信力/信任链问题，只有公有 CA（OV/EV）签发的代码签名证书才有公信力。
2. **本项目当前阶段不购买证书**：先用自签证书把整条流水线跑通，并把证书来源做成可插拔的，之后换成公有 CA 只需更换证书来源，不改流程逻辑。
3. **即使将来签上公有 CA 证书**，从网上下载的新发布者仍可能被 SmartScreen 拦截（需下载量积累信誉或使用 EV）。签名解决的是「未签名」问题，SmartScreen 信誉属于证书到位后的运营工作，不在本设计范围内。

## 已确认的决策

| 决策项 | 结论 |
|---|---|
| 证书路线 | 暂不购买；流水线做成证书可插拔，先用自签证书跑通全流程 |
| 签名范围 | **仅主 exe + 安装包**；第三方 DLL 不签（普通用户端无感知） |
| 分发形态 | Inno 安装包为主 **+** 便携 zip 都要 |
| 签名工具 | **官方 signtool**（Windows SDK 10.0.26100），不自研签名工具 |

## 工具选型说明

选官方 `signtool.exe` 而不是自研 .NET 签名工具，理由：

- 行业标准，后续换云签名服务（DigiCert KeyLocker / SSL.com eSigner / Azure Trusted Signing）时仍走 signtool 这条路
- Inno Setup 原生支持 `SignTool=` 指令，安装包可在**编译时**直接签名，不必「先编译后签名」
- 支持 RFC3161 时间戳（`/tr` + `/td sha256`），签名不会因证书到期而失效 —— 将来换公有 CA 证书时不必全量重签

代价：本机需要安装 Windows SDK（winget 包 `Microsoft.WindowsSDK.10.0.26100`，约 1-2 GB）。

## 架构

```
release.ps1  (MyIsland\release.ps1)          ← 编排者，唯一入口
  │
  ├─ ① dotnet publish  → publish\win-x64\          (Release, self-contained win-x64)
  ├─ ② signtool sign   → Island.App.exe               (主 exe)
  ├─ ③ ISCC            → dist\MyIsland-Setup-x.y.z.exe
  │     └─ 编译期由 .iss 的 SignTool= 指令签安装包
  ├─ ④ 打便携 zip      → MyIsland-x.y.z-portable.zip  (内含已签名的 Island.App.exe)
  ├─ ⑤ SHA256SUMS.txt  → 每个产物的哈希 + 签名元数据
  └─ ⑥ 门禁自检        → 任一产物签名缺失/无效 ⇒ 退出码非 0
```

### 证书来源抽象（换证书即用）

`release.ps1` 支持三种证书来源，互斥、优先级从高到低：

| 参数 | 来源 | 用途 |
|---|---|---|
| `-CertPfx <path> -PasswordEnv <ENV_VAR>` | PFX 文件 | 公有 CA 导出证书、自签导出 |
| `-CertThumbprint <HEX>` | 证书库（CurrentUser\My / LocalMachine\My） | 本机安装的云签名/硬件证书 |
| 默认 | 自动创建/复用自签 `CN=MyIsland` | 当前阶段的默认路径 |

自签证书的 **Subject 就是用户端「发布者」那一行** ⇒ 默认用 `CN=MyIsland`（不使用 `CN=MyIsland Dev`，避免把「Dev」露给最终用户）。

### 密钥管理（硬约束）

- PFX **一律存放在仓库之外**：`%LOCALAPPDATA%\MyIsland\signing\`
- 密码**只走环境变量**，不写进脚本、不写进命令行、不进日志
- 仓库 `.gitignore` 增加 `*.pfx` / `*.p12` / `signing/` 规则
- 自签证书导入 `TrustedPeople` 存储，使本机校验为 Valid（不装进 Trusted Root，避免扩大信任面）

## 各组件设计

### 1. `MyIsland\sign.ps1`（改造现有脚本）

现有脚本用 `Set-AuthenticodeSignature`，**不支持时间戳**，且不处理安装包。改造为：

- 证书获取/创建（沿用现有逻辑，Subject 改为 `CN=MyIsland`）
- 用 `signtool sign /fd sha256 /tr <TSA> /td sha256 /sha1 <thumbprint>` 签单个文件
- `signtool verify /pa /v` 验证并输出签名者 + 时间戳
- 保持「目标可以是目录或单文件」的用法

### 2. `MyIsland\release.ps1`（新增，编排脚本）

参数：`-Version`、`-CertPfx`/`-PasswordEnv`/`-CertThumbprint`、`-TsaUrl`（默认 `http://timestamp.digicert.com`，RFC3161 时间戳服务）、`-SkipPublish`、`-SkipTests`、`-SkipZip`。

流程严格按上面架构图 ①~⑥ 执行，**每步失败即中止并返回非 0**。

### 3. `installer\MyIsland.iss`（改造）

- `AppPublisher`：`YQ` → `MyIsland`
- 新增条件式签名指令（保持默认构建不需要 signtool）：

```ini
#ifdef MyIslandSignTool
SignTool ={#MyIslandSignTool}
#endif
```

  ⚠️ **实测更正**：Inno Setup 7 的 `SignTool=` **不能直接写命令行**，只接受一个"已命名"的签名工具，
  名字必须在 ISCC 命令行用 `--signtool=myisland=<完整命令>` 定义（名字里不能有空格/等号）。
  直接写命令行会报 *"Value of [Setup] section directive "SignTool" is invalid"*。
  最终形态：.iss 只写名字，完整命令由 `release.ps1` 通过 ISCC 命令行注入。
- `[Files]` 排除项补上 `*.pfx`、`*.p12`、签名中间产物

### 4. 便携 zip 的诚实做法

ZIP 无法被签名 ⇒：

- zip 内放**已签名**的 `Island.App.exe`
- 同目录给 `SHA256SUMS.txt`
- 发布说明写明「以 exe 上的发布者签名为准，zip 本身不含签名」

### 5. 两处实测得出的补充约束

1. **发布到独立暂存目录**（`MyIsland\publish\release`），不碰开发用的 `publish\win-x64`：
   正在运行的实例锁着那个 exe，signtool 会报 *"The file is being used by another process"*；
   而提权实例非提权脚本根本杀不掉。暂存目录彻底解耦 ⇒ **发布流程不需要 UAC**。
2. **自签证书必须进 `CurrentUser\Root`**：只放 TrustedPeople 时，本机校验是
   *"terminated in a root certificate which is not trusted"*（Status=UnknownError）。
   用 .NET 的 `X509Store.Add`（静默；走 CryptoUI 会弹确认框卡住脚本）。
   注意这与设计初稿"不放 Trusted Root"的取舍相反 —— Authenticode 的链校验要求根受信任。

### 6. 发布自检清单（`release.ps1` 输出）

每个产物一行：`文件 | SHA256 | 签名状态 | 签名者 Subject | 时间戳时间`。
将来换成公有 CA 证书，这张表就是验收凭据。

## 错误处理

| 失败点 | 行为 |
|---|---|
| 找不到 signtool | 明确报错并提示 winget 安装命令，退出非 0 |
| 证书缺失/过期 | 报出 Subject + 有效期，退出非 0 |
| 签名失败（TSA 不可达等） | signtool 的错误原文透出，退出非 0 |
| ISCC 失败 | 透出 ISCC 输出，退出非 0 |
| 验证阶段签名状态非 Valid | 列出未通过项，退出非 0 |

## 测试与验收

1. 自签证书创建 → 签主 exe → `Get-AuthenticodeSignature` 为 **Valid**（本机 TrustedPeople）且 `signtool verify` 显示时间戳
2. ISCC 编译出的安装包 **已签名**（`Status=Valid`）
3. 便携 zip 解压后 `Island.App.exe` 的签名状态为 Valid
4. 门禁自检：故意去掉签名 → `release.ps1` 返回非 0
5. `release.ps1` 产出的 SHA256SUMS 与实际文件一致（重新计算比对）

## 明确不做（避免范围膨胀）

- EV 证书采购与企业验证流程
- Microsoft SmartScreen 信誉运营（需 OV 签名 + 下载量积累）
- CI 平台对接（GitHub Actions / Azure Pipelines）
- 给第三方 DLL（LibreHardwareMonitor / NAudio / HidSharp）签名
- 自动更新器与代码签名��的联动

## 后续（证书到位后）

1. 采购公有 CA OV/EV 代码签名证书（需营业执照/个体户 + 电话验证，1-3 个工作日）
2. 用 `-CertPfx` 或 `-CertThumbprint` 替换自签来源，流水线不变
3. 若走云签名：按厂商文档把证书放进系统证书库（`-CertThumbprint` 路径），或加厂商 SDK 分支
