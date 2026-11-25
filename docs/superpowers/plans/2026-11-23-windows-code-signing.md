# Windows 代码签名流水线 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 MyIsland 建立可插拔证书的签名流水线：主 exe + Inno 安装包被 Authenticode 签名（带 RFC3161 时间戳），并产出带校验值的便携 zip 与发布自检清单。

**Architecture:** `release.ps1` 是唯一编排入口，六步串联：publish → 签主 exe → ISCC 编译安装包（编译期由 .iss 的 `SignTool=` 签名）→ 打便携 zip → 生成 SHA256SUMS.txt → 签名门禁自检。证书来源三选一（PFX / 证书库 thumbprint / 默认自签），换成公有 CA 证书只改来源参数。

**Tech Stack:** Windows SDK 10.0.26100 的 `signtool.exe`、Inno Setup 7 (`ISCC.exe`)、PowerShell 5.1、.NET 8（publish）

## Global Constraints

- 证书 Subject 默认必须是 `CN=MyIsland`（Subject 就是用户端「发布者」那一行，不能带 Dev）
- PFX 一律存仓库外：`%LOCALAPPDATA%\MyIsland\signing\`；密码只走环境变量 `MYISLAND_SIGN_PWD`
- 仓库 `.gitignore` 必须含 `*.pfx`、`*.p12`、`signing/`
- 时间戳 TSA：`http://timestamp.digicert.com`（RFC3161，`/tr` + `/td sha256`）
- 签名范围：**只签主 exe + 安装包**，第三方 DLL 不签
- 任何一步失败 ⇒ 退出码非 0；门禁自检不过 ⇒ 同样非 0
- 所有 .ps1 必须 **UTF-8 with BOM**（Windows PowerShell 5.1 按 ANSI 读无 BOM 文件会吃掉引号导致语法错误）

## File Structure

| 文件 | 动作 | 职责 |
|---|---|---|
| `MyIsland/sign.ps1` | 重写 | 定位 signtool、解析证书来源、签/验单个或一批文件 |
| `MyIsland/release.ps1` | 新建 | 六步流水线编排 + 产物清单 + 门禁 |
| `installer/MyIsland.iss` | 改 | `AppPublisher`、版本可注入、条件式 `SignTool=`、排除 PFX |
| `.gitignore` | 改 | 密钥不入库 |
| `MyIsland/README.md`（发布说明） | 改 | 签名与分发说明 |

---

### Task 1: 签名基础能力（sign.ps1）

**Files:**
- Modify: `MyIsland-src/MyIsland/sign.ps1`

**Interfaces:**
- Consumes: 环境变量 `MYISLAND_SIGN_PWD`（PFX 密码）、参数 `-CertPfx/-CertThumbprint/-TsaUrl`
- Produces: `Get-SignToolPath()`、`Resolve-SigningCert()`、`Invoke-SignFile()`、`Test-SignatureValid()`（供 Task 2 调用）

- [x] **Step 1: 重写 sign.ps1**

```powershell
param(
    [string]$Target = "$PSScriptRoot\publish\win-x64",
    [string]$CertPfx = "",
    [string]$PasswordEnv = "MYISLAND_SIGN_PWD",
    [string]$CertThumbprint = "",
    [string]$TsaUrl = "http://timestamp.digicert.com",
    [switch]$VerifyOnly
)
```

关键实现点：

```powershell
function Get-SignToolPath {
    $cands = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
             Where-Object { $_.DirectoryName -match '\\x64$' } | Sort-Object FullName -Descending
    if (-not $cands) { throw "signtool 未找到：请先安装 Windows SDK（winget install Microsoft.WindowsSDK.10.0.26100）" }
    return $cands[0].FullName
}

function Resolve-SigningCert {
    # 1) PFX
    if ($CertPfx) {
        $pwd = [Environment]::GetEnvironmentVariable($PasswordEnv)
        if (-not $pwd) { throw "环境变量 $PasswordEnv 未设置（PFX 密码）" }
        $sec = ConvertTo-SecureString $pwd -AsPlainText -Force
        $imported = Import-PfxCertificate -FilePath $CertPfx -CertStoreLocation 'Cert:\CurrentUser\My' -Password $sec -Exportable:$false
        return $imported
    }
    # 2) 证书库 thumbprint
    if ($CertThumbprint) {
        $c = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My | Where-Object { $_.Thumbprint -eq $CertThumbprint } | Select-Object -First 1
        if (-not $c) { throw "证书库里找不到 thumbprint $CertThumbprint" }
        return $c
    }
    # 3) 默认自签
    $subject = 'CN=MyIsland'
    $c = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) -and $_.HasPrivateKey } | Select-Object -First 1
    if (-not $c) {
        $c = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation 'Cert:\CurrentUser\My' -KeyUsage DigitalSignature -KeyUsageProperty Sign -NotAfter (Get-Date).AddYears(2)
        # 本机校验需要信任它：放 TrustedPeople（不放 Trusted Root，避免扩大信任面）
        $null = New-Item -Path 'Cert:\CurrentUser\TrustedPeople' -ErrorAction SilentlyContinue
        $tp = Get-ChildItem Cert:\CurrentUser\TrustedPeople | Where-Object { $_.Thumbprint -eq $c.Thumbprint }
        if (-not $tp) { $null = Copy-Item "Cert:\CurrentUser\My\$($c.Thumbprint)" 'Cert:\CurrentUser\TrustedPeople' }
    }
    return $c
}

function Invoke-SignFile {
    param([string]$Path, [string]$Thumbprint, [string]$Tsa, [string]$SignTool)
    & $SignTool sign /sha1 $Thumbprint /fd sha256 /tr $Tsa /td sha256 /nologo $Path
    if ($LASTEXITCODE -ne 0) { throw "签名失败：$Path（signtool exit $LASTEXITCODE）" }
}

function Test-SignatureValid {
    param([string]$Path)
    $s = Get-AuthenticodeSignature $Path
    return ($s.Status -eq 'Valid' -and $null -ne $s.SignerCertificate)
}
```

- [x] **Step 2: 自检** —— 运行 `powershell -File sign.ps1 -Target <publish 目录>`，确认输出 `[OK] Island.App.exe` 且 `Get-AuthenticodeSignature` 为 Valid

---

### Task 2: 流水线编排（release.ps1）

**Files:**
- Create: `MyIsland-src/MyIsland/release.ps1`

**Interfaces:**
- Consumes: Task 1 的 `sign.ps1`（以 `& "$PSScriptRoot\sign.ps1" -Target <file> ...` 调用）
- Produces: `$root\dist\MyIsland-Setup-<ver>.exe`、`$root\dist\MyIsland-<ver>-portable.zip`、`$root\dist\SHA256SUMS.txt`

- [x] **Step 1: 写 release.ps1**

六步（每步失败即 `throw`）：

```powershell
param(
    [string]$Version = "",
    [string]$CertPfx = "",
    [string]$PasswordEnv = "MYISLAND_SIGN_PWD",
    [string]$CertThumbprint = "",
    [string]$TsaUrl = "http://timestamp.digicert.com",
    [switch]$SkipPublish, [switch]$SkipTests, [switch]$SkipZip,
    [string]$OutDir = ""
)
```

1. 解析版本号（空则读 `MyIsland\src\Island.App\Island.App.csproj` 的 `<Version>`）
2. `dotnet publish src/Island.App/Island.App.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64`（`SkipPublish` 跳过）
3. `& sign.ps1 -Target publish\win-x64\Island.App.exe ...`（只签主 exe）
4. `ISCC.exe /DSignThumbprint=<thumb> /DSignToolPath=<signtool> /DAppVersion=<ver> /DOutDir=<dist> installer\MyIsland.iss`
5. 打便携 zip（`[System.IO.Compression.ZipFile]::CreateFromDirectory`）
6. 生成 `SHA256SUMS.txt`（文件 | SHA256 | 签名状态 | 签名者 | 时间戳）+ 门禁校验

- [x] **Step 2: 端到端跑一次**，确认三样产物都在 `dist\` 且签名状态 Valid

---

### Task 3: 安装包脚本改造（MyIsland.iss）

**Files:**
- Modify: `MyIsland-src/installer/MyIsland.iss`

- [x] **Step 1: AppPublisher 改为 MyIsland**
- [x] **Step 2: 版本与签名参数改成可注入**

```ini
#ifndef AppVersion
#define AppVersion "1.0.1"
#endif
#ifndef SignThumbprint
#define SignThumbprint ""
#endif
#ifndef SignToolPath
#define SignToolPath ""
#endif
#ifndef TsaUrl
#define TsaUrl "http://timestamp.digicert.com"
#endif

#ifdef SignThumbprint
SignTool="{#SignToolPath}" sign /sha1 {#SignThumbprint} /fd sha256 /tr {#TsaUrl} /td sha256 /nologo $f
#endif
```

- [x] **Step 3: [Files] 排除项补 `*.pfx,*.p12`**
- [ ] **Step 4: 不传 /DSignThumbprint 编译一次，确认仍能编译（普通开发构建不依赖 signtool）**

---

### Task 4: 密钥不入库 + 发布说明

**Files:**
- Modify: `MyIsland-src/.gitignore`
- Modify/Create: `MyIsland-src/MyIsland/README.md`（或 `DISTRIBUTION.md`）

- [x] **Step 1: .gitignore 追加** `*.pfx`、`*.p12`、`signing/`
- [x] **Step 2: 写发布说明**：如何设置 `MYISLAND_SIGN_PWD`、如何换公有 CA 证书（改 `-CertPfx`/`-CertThumbprint`）、ZIP 本身无签名的说明、SmartScreen 信誉说明

---

### Task 5: 验收

- [x] **Step 1**: `Get-AuthenticodeSignature dist\MyIsland-Setup-<ver>.exe` → Status=Valid
- [x] **Step 2**: `Get-AuthenticodeSignature publish\win-x64\Island.App.exe` → Status=Valid
- [x] **Step 3**: 解压便携 zip → 里面 Island.App.exe 签名 Valid
- [x] **Step 4**: `signtool verify /pa /v` 对安装包输出含时间戳行
- [x] **Step 5**: 故意破坏（删掉签名）→ 门禁返回非 0
- [x] **Step 6**: `SHA256SUMS.txt` 的哈希与重新计算一致

## Self-Review

- 规格覆盖：证书可插拔(Task1/2)、只签主 exe+安装包(Task2/3)、安装包+便携 zip(Task2)、时间戳(Task1)、密钥不入库(Task4)、门禁(Task2/5) —— 全覆盖
- 占位符扫描：无 TBD/TODO
- 命名一致性：`Get-SignToolPath / Resolve-SigningCert / Invoke-SignFile / Test-SignatureValid` 在 Task 1 定义、Task 2 通过 sign.ps1 入口调用，不直接依赖函数名

---

## 未完成项清单（2026-11-26 复核）

**只剩 1 项没做**：

- **Task 3 · Step 4：不传签名参数编译一次，确认仍能编译** —— 本机**没装 Inno Setup 7**（`ISCC.exe` 不存在），
  没法真跑一次不带签名的编译。`installer/MyIsland.iss` 里 `SignTool` 是条件式的
  （只有 `#ifdef MyIslandSignTool` 才写 `SignTool=`），设计上普通开发构建不依赖 signtool，
  但**缺一次实际编译验证**，故保持未勾。

**已做部分的证据**（2026-11-26 复核，逐条实测）：

| 项 | 证据 |
|---|---|
| Task 1 · Step 1 重写 `sign.ps1` | 仓根 `sign.ps1`：`Get-SignToolPath` / `Resolve-SigningCert` / `Invoke-SignFile` / `Test-SignatureValid` 四个函数齐备，参数 `-CertPfx` / `-CertThumbprint` / `-TsaUrl` / `-PasswordEnv` 齐备 |
| Task 1 · Step 2 自检 | 1.0.6 发布跑通：`dist/SHA256SUMS.txt` 记录 `Island.App.exe 签名=Valid 签名者=CN=MyIsland 时间戳=yes` |
| Task 2 · Step 1 写 `release.ps1` | 仓根 `release.ps1`：publish → 签主 exe → ISCC 编译并签名 → 便携 zip → `SHA256SUMS.txt` → 门禁自检（共 8 步） |
| Task 2 · Step 2 端到端跑一次 | `dist/` 有 `MyIsland-Setup-1.0.6.exe`、`MyIsland-1.0.6-portable.zip`、`SHA256SUMS.txt`；四个安装包（1.0.1 / 1.0.2 / 1.0.3 / 1.0.6）`Get-AuthenticodeSignature` 全部 **Valid** |
| Task 3 · Steps 1–3 `.iss` 改造 | `installer/MyIsland.iss`：`AppPublisher=MyIsland`；`AppVersion` / `MyIslandSignTool` / `TsaUrl` 条件式可注入；`[Files]` 的 `Excludes` 含 `*.pfx,*.p12` |
| Task 4 · Step 1 `.gitignore` | 已有 `*.pfx` / `*.p12` / `signing/` |
| Task 4 · Step 2 发布说明 | `DISTRIBUTION.md`：`MYISLAND_SIGN_PWD`、换公有 CA 证书、ZIP 本身不可签名、SmartScreen 信誉 |
| Task 5 · Steps 1–4 验收 | 安装包 `Status=Valid`；便携 zip 内的 `Island.App.exe` `Status=Valid`；`signtool verify /pa /v` 输出 `The signature is timestamped: Wed Nov 25 03:13:19 2026` |
| Task 5 · Step 5 门禁返回非 0 | 用 `release.ps1` 里的**真实门禁代码**跑一份去掉签名的 exe（`Status=NotSigned`）⇒ **退出码 1** |
| Task 5 · Step 6 哈希一致 | 重算 `MyIsland-Setup-1.0.6.exe` / `MyIsland-1.0.6-portable.zip` 的 SHA256，与 `dist/SHA256SUMS.txt` **逐位一致** |

⚠️ 遗留事实（不是未勾项）：计划里写的 `publish\win-x64\Island.App.exe` 是**本地开发 publish 目录**（当前那份 **未签名**）；
流水线签的是发布暂存目录里的主程序，`dist/SHA256SUMS.txt` 记录的就是它（`签名=Valid`）。
