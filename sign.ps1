# Island 一键签名脚本
# 用法：powershell -ExecutionPolicy Bypass -File sign.ps1 [目标目录或文件]
# 首次运行自动创建自签证书；之后复用同一证书，签名结果稳定。
#
# 说明（三级方案）：
#  1. 自签证书（本脚本默认）：本机/家庭机器开发调试够用。
#     注意：自签证书只在"生成它的机器"上有效；换机器需把证书导出（下方有命令）并导入
#     目标机器的 CurrentUser\My（签名人）+ TrustedPeople（信任它签的程序）。
#  2. 公司内部 CA：若公司有内部代码签名 CA，找 IT 申请证书后替换脚本里的证书查找逻辑。
#     WDAC"企业签名级别"通常认这种。
#  3. 公共 CA（正式分发）：需要购买 OV/EV 代码签名证书（DigiCert/Sectigo/GlobalSign 等），
#     EV 首次即有 SmartScreen 信誉。签名命令相同，只是 -Certificate 换成买的证书。

param(
    [string]$Target = "$PSScriptRoot\publish\win-x64"
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

    # 导出 .pfx 供其他机器复用（可注释掉）
    $pfx = "$PSScriptRoot\MyIslandDev.pfx"
    $pwd = Read-Host "设置 pfx 导出密码（直接回车跳过导出）"
    if ($pwd) {
        $sec = ConvertTo-SecureString $pwd -AsPlainText -Force
        Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $sec
        Write-Host "已导出 $pfx（拷到别的机器：先导入 CurrentUser\My，再导入 TrustedPeople）"
    }
}

Write-Host "使用证书: $($cert.Subject) $($cert.Thumbprint)"

# ---- 2. 收集目标文件 ----
$files = @()
if (Test-Path $Target -PathType Leaf) {
    $files = @($Target)
} else {
    # 签所有自产程序集 + 单文件 exe（框架 DLL 是微软签过名的，不用动）
    $files = Get-ChildItem $Target -Recurse -Include 'Island.*.exe','Island.*.dll' |
        Where-Object { $_.FullName -notmatch '\\(cs|de|es|fr|it|ja|ko|pl|pt-BR|ru|tr|zh-Hant)\\' } |
        Select-Object -ExpandProperty FullName
}
if (-not $files) { Write-Error "目标下没有 Island.* 文件: $Target"; exit 1 }
Write-Host "待签名 $($files.Count) 个文件"

# ---- 3. 逐个签名 ----
foreach ($f in $files) {
    $r = Set-AuthenticodeSignature -FilePath $f -Certificate $cert -HashAlgorithm SHA256
    if ($r.SignerCertificate) {
        Write-Host "  [OK] $(Split-Path $f -Leaf)" -ForegroundColor Green
    } else {
        Write-Host "  [失败] $(Split-Path $f -Leaf): $($r.StatusMessage)" -ForegroundColor Red
    }
}

Write-Host "`n完成。验证：Get-AuthenticodeSignature <文件> | fl Status, SignerCertificate"
