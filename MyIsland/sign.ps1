param(
    [string]$Target = "$PSScriptRoot\publish\win-x64",
    [string]$CertPfx = "",
    [string]$PasswordEnv = "MYISLAND_SIGN_PWD",
    [string]$CertThumbprint = "",
    [string]$TsaUrl = "http://timestamp.digicert.com",
    [string]$ThumbprintOut = "",
    [switch]$VerifyOnly
)

# Island 签名工具（signtool 版）
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File sign.ps1                       # 签 publish\win-x64 下的 Island*.exe
#   powershell -ExecutionPolicy Bypass -File sign.ps1 -Target <文件|目录>
#   powershell -ExecutionPolicy Bypass -File sign.ps1 -CertPfx <pfx>        # 密码取自环境变量 MYISLAND_SIGN_PWD
#   powershell -ExecutionPolicy Bypass -File sign.ps1 -CertThumbprint <HEX> # 用系统证书库里的证书
#   powershell -ExecutionPolicy Bypass -File sign.ps1 -Target <文件> -VerifyOnly
#
# 证书来源三选一（优先级从高到低）：
#   1) -CertPfx <路径>       PFX 文件；密码只从环境变量 -PasswordEnv 读（默认 MYISLAND_SIGN_PWD）
#   2) -CertThumbprint <HEX> 直接用系统证书库里的证书（公有 CA 证书 / 云签名都走这条）
#   3) 默认                  自动创建或复用自签证书 CN=MyIsland
#
# 范围：只签主程序（Island*.exe）。第三方 DLL 不签 —— 普通用户端无感知，
#       而给 NuGet 出来的 DLL 逐个签名既慢又没意义。
#
# 自签证书只能让"信任它的机器"显示为已签名；普通用户端仍会提示"未知发布者"。
# 正式分发要换成公有 CA 的 OV/EV 代码签名证书：签名命令不用改，只换证书来源参数。
#
# 时间戳用 RFC3161 公共 TSA：签名不会因为证书到期而失效，将来换证书也不用全量重签。

$ErrorActionPreference = 'Stop'

function Get-SignToolPath {
    $pf86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $roots = @(
        (Join-Path $pf86 'Windows Kits\10\bin'),
        (Join-Path $env:ProgramFiles 'Windows Kits\10\bin')
    )
    foreach ($r in $roots) {
        if (-not (Test-Path $r)) { continue }
        $hit = Get-ChildItem $r -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
               Where-Object { $_.DirectoryName -match 'x64$' } |
               Sort-Object { [version]($_.Directory.Parent.Name) } -Descending |
               Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    throw "找不到 signtool.exe：请先安装 Windows SDK（winget install Microsoft.WindowsSDK.10.0.26100 -e）"
}

function Resolve-SigningCert {
    # 1) PFX（密码只从环境变量取，绝不写进命令行/日志）
    if ($CertPfx) {
        if (-not (Test-Path $CertPfx)) { throw "PFX 不存在：$CertPfx" }
        $pwd = [Environment]::GetEnvironmentVariable($PasswordEnv)
        if ([string]::IsNullOrEmpty($pwd)) { throw "环境变量 $PasswordEnv 未设置（PFX 密码）" }
        $sec = ConvertTo-SecureString $pwd -AsPlainText -Force
        $imported = Import-PfxCertificate -FilePath $CertPfx -CertStoreLocation 'Cert:\CurrentUser\My' -Password $sec
        Write-Host "已导入 PFX：$($imported.Thumbprint)  $($imported.Subject)" -ForegroundColor Green
        return $imported
    }

    # 2) 系统证书库（公有 CA / 云签名）
    if ($CertThumbprint) {
        $c = Get-ChildItem 'Cert:\CurrentUser\My', 'Cert:\LocalMachine\My' -ErrorAction SilentlyContinue |
             Where-Object { $_.Thumbprint -eq $CertThumbprint } | Select-Object -First 1
        if (-not $c) { throw "证书库里找不到 thumbprint：$CertThumbprint" }
        if (-not $c.HasPrivateKey) { throw "证书 $CertThumbprint 没有私钥，无法签名" }
        Write-Host "使用证书库证书：$($c.Subject)" -ForegroundColor Green
        return $c
    }

    # 3) 默认自签
    #    Subject 就是用户端"发布者"那一行 —— 所以必须是 CN=MyIsland，不要带 Dev
    $subject = 'CN=MyIsland'
    $c = Get-ChildItem 'Cert:\CurrentUser\My' -CodeSigningCert -ErrorAction SilentlyContinue |
         Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) -and $_.HasPrivateKey } |
         Select-Object -First 1
    if (-not $c) {
        Write-Host "未找到 '$subject' 自签证书，创建新的（有效期 2 年）..." -ForegroundColor Yellow
        $c = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation 'Cert:\CurrentUser\My' -KeyUsage DigitalSignature -KeyUsageProperty Sign -NotAfter (Get-Date).AddYears(2)
        # 本机校验需要信任它：放 TrustedPeople（不放 Trusted Root，避免扩大本机信任面）
        # ⚠️ 不要用 New-Item 建这个存储：PS 的证书提供程序在部分环境会直接抛
        #    "Creating certificate stores under CurrentUser is not supported"
        $inTp = Get-ChildItem 'Cert:\CurrentUser\TrustedPeople' -ErrorAction SilentlyContinue |
                Where-Object { $_.Thumbprint -eq $c.Thumbprint }
        if (-not $inTp) {
            try {
                $null = Copy-Item "Cert:\CurrentUser\My\$($c.Thumbprint)" 'Cert:\CurrentUser\TrustedPeople' -ErrorAction Stop
            } catch {
                # 回落 certutil（输出是本地代码页，直接吞掉）
                $cer = Join-Path $env:TEMP "island-sign-$($c.Thumbprint).cer"
                [System.IO.File]::WriteAllBytes($cer, $c.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
                & certutil.exe -user -addstore TrustedPeople $cer | Out-Null
            }
        }
        Write-Host "已创建：$($c.Thumbprint)" -ForegroundColor Green
    }

    # 本机校验：Authenticode 的链校验要求**根受信任**，只放 TrustedPeople 不够
    #（否则状态是 UnknownError: terminated in a root certificate which is not trusted）。
    # 用 .NET 的 X509Store.Add —— 它是静默的；走 CryptoUI/IE 那条路会弹确认框，卡住脚本。
    try {
        $root = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'CurrentUser')
        $root.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $inRoot = @($root.Certificates | Where-Object { $_.Thumbprint -eq $c.Thumbprint })
        if ($inRoot.Count -eq 0) {
            $root.Add($c)
            Write-Host "已把自签证书加入 CurrentUser\Root（本机校验 Status 才会是 Valid）" -ForegroundColor DarkGray
        }
        $root.Close()
    } catch {
        Write-Host "加入 Root 存储失败（不影响签名，但本机校验会是 UnknownError）：$($_.Exception.Message)" -ForegroundColor Yellow
    }
    return $c
}

function Invoke-SignFile {
    param([string]$Path, [string]$Thumbprint, [string]$Tsa, [string]$SignTool)
    # 注意：sign 子命令没有 /nologo（那是 verify 的选项），sign 本来就是静默的
    & $SignTool sign /sha1 $Thumbprint /fd sha256 /tr $Tsa /td sha256 $Path
    if ($LASTEXITCODE -ne 0) { throw "signtool 签名失败：$Path（exit $LASTEXITCODE）" }
}

function Test-SignatureValid {
    param([string]$Path)
    $s = Get-AuthenticodeSignature $Path
    return ($s.Status -eq 'Valid' -and $null -ne $s.SignerCertificate)
}

function Get-SignatureReport {
    param([string]$Path)
    $s = Get-AuthenticodeSignature $Path
    $stamp = 'no'
    try {
        $out = & (Get-SignToolPath) verify /pa /v $Path 2>&1
        if ($out | Select-String -Pattern 'timestamped' -Quiet) { $stamp = 'yes' }
    } catch { }
    return [pscustomobject]@{
        File   = Split-Path $Path -Leaf
        Status = $s.Status
        Signer = if ($s.SignerCertificate) { $s.SignerCertificate.Subject } else { '' }
        Stamp  = $stamp
    }
}

# ---- 主流程 ----
$signTool = Get-SignToolPath
Write-Host "signtool：$signTool"

$files = @()
if (Test-Path $Target -PathType Leaf) {
    $files = @($Target)
} else {
    # 只签 Island*.exe（主程序），第三方 DLL 不签
    $files = Get-ChildItem $Target -Recurse -File -Filter 'Island*.exe' -ErrorAction SilentlyContinue |
             Select-Object -ExpandProperty FullName
}
if (-not $files -or $files.Count -eq 0) { throw "目标下没有可签名的 Island*.exe：$Target" }
Write-Host "待处理 $($files.Count) 个文件"

if ($VerifyOnly) {
    foreach ($f in $files) {
        $r = Get-SignatureReport $f
        "{0,-28} {1,-10} {2,-30} 时间戳={3}" -f $r.File, $r.Status, $r.Signer, $r.Stamp
    }
    exit 0
}

$cert = Resolve-SigningCert
Write-Host "使用证书：$($cert.Subject)  $($cert.Thumbprint)  有效期至 $($cert.NotAfter)"

# 把本次用的证书指纹写出去：release.ps1 要拿它去签 Inno 安装包
if ($ThumbprintOut) {
    [System.IO.File]::WriteAllText($ThumbprintOut, $cert.Thumbprint, (New-Object System.Text.UTF8Encoding($false)))
}

foreach ($f in $files) {
    Invoke-SignFile -Path $f -Thumbprint $cert.Thumbprint -Tsa $TsaUrl -SignTool $signTool
    $ok = Test-SignatureValid $f
    $tag = if ($ok) { 'OK  ' } else { '警告' }
    $color = if ($ok) { 'Green' } else { 'Yellow' }
    Write-Host "  [$tag] $(Split-Path $f -Leaf)" -ForegroundColor $color
}

Write-Host ""
Write-Host "签名完成。验证：Get-AuthenticodeSignature <文件> | fl Status,SignerCertificate"
