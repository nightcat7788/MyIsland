# Island 一键签名脚本
# 用法：powershell -ExecutionPolicy Bypass -File sign.ps1 [目标目录或文件] [-ExportCer]
# 首次运行自动创建自签证书；之后复用同一证书，签名结果稳定。
#
# 安全说明：
#  1. 私钥留在创建它的机器的 CurrentUser\My 存储区，绝不向仓库目录写出 .pfx 私钥文件。
#  2. 如需在测试机信任该签名，仅用 -ExportCer 导出不含私钥的公钥证书 (.cer)，
#     在测试机导入 CurrentUser\Root 与 CurrentUser\TrustedPeople（切勿跨机器分发私钥）。
#  3. 正式对外分发请使用受信任公共 CA 的 OV/EV 代码签名证书并附带时间戳服务。

param(
    [string]$Target = "$PSScriptRoot\publish\win-x64",
    [string]$TimestampServer = "",
    [switch]$ExportCer
)

$ErrorActionPreference = 'Stop'
$subject = 'CN=MyIsland Dev'

# ---- 1. 找或建证书 ----
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
    Select-Object -First 1

if (-not $cert) {
    Write-Host "未找到 '$subject' 证书，创建新的（有效期 2 年）..." -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyUsage DigitalSignature -KeyUsageProperty Sign `
        -NotAfter (Get-Date).AddYears(2)
    Write-Host "已创建: $($cert.Thumbprint)" -ForegroundColor Green
}

if ($ExportCer) {
    $cer = Join-Path $PSScriptRoot 'MyIslandDev.cer'
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Write-Host "已导出公钥证书（不含私钥）: $cer" -ForegroundColor Cyan
}

Write-Host "使用证书: $($cert.Subject) $($cert.Thumbprint)"

# ---- 2. 收集目标文件 ----
$files = @()
if (Test-Path -LiteralPath $Target -PathType Leaf) {
    $files = @((Resolve-Path -LiteralPath $Target).Path)
} else {
    # 签自产程序集、单文件包及未签名的第三方托管依赖（跳过微软已签名框架 DLL）
    $includePatterns = @(
        'Island.*.exe', 'Island.*.dll', 'MyIsland-*.exe',
        'NAudio*.dll', 'LibreHardwareMonitorLib*.dll', 'HidSharp*.dll',
        'BlackSharp*.dll', 'DiskInfoToolkit*.dll', 'RAMSPDToolkit*.dll'
    )
    $files = Get-ChildItem -Path $Target -Recurse -Include $includePatterns |
        Where-Object { $_.FullName -notmatch '\\(cs|de|es|fr|it|ja|ko|pl|pt-BR|ru|tr|zh-Hant)\\' } |
        Select-Object -ExpandProperty FullName
}
if (-not $files) { Write-Error "目标下没有可签名的文件: $Target"; exit 1 }
Write-Host "待签名 $($files.Count) 个文件"

# ---- 3. 逐个签名 ----
$untrustedCount = 0
foreach ($f in $files) {
    $signArgs = @{ FilePath = $f; Certificate = $cert; HashAlgorithm = 'SHA256' }
    if (-not [string]::IsNullOrWhiteSpace($TimestampServer)) { $signArgs['TimestampServer'] = $TimestampServer }
    $r = Set-AuthenticodeSignature @signArgs
    if ($r.Status -eq 'Valid') {
        Write-Host "  [OK] $(Split-Path $f -Leaf)" -ForegroundColor Green
    } elseif ($r.SignerCertificate -and $r.Status -eq 'UnknownError') {
        $untrustedCount++
        Write-Host "  [已签/未信任根] $(Split-Path $f -Leaf)（自签证书尚未导入 CurrentUser\Root）" -ForegroundColor Yellow
    } else {
        Write-Host "  [失败] $(Split-Path $f -Leaf): $($r.Status) $($r.StatusMessage)" -ForegroundColor Red
    }
}

if ($untrustedCount -gt 0) {
    Write-Host "`n提示：本机验证自签证书需将其公钥导入 CurrentUser\Root 与 CurrentUser\TrustedPeople（可加 -ExportCer 导出公钥）。" -ForegroundColor Yellow
}
Write-Host "完成。验证：Get-AuthenticodeSignature <文件> | Format-List Status, SignerCertificate"
