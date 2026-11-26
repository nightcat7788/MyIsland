# 提权跑一次 Windows 端功能自检（**写入操作往返**：WLAN 关→读回→开→读回、蓝牙关→读回→开→读回）。
#
# 为什么需要单独一个脚本：WLAN / 蓝牙的**写路径需要管理员** ——
#   · WLAN：产品用 netsh + runas 自己弹 UAC（每个动作一次）；
#   · 蓝牙：产品直接控制 bthserv 服务，需要**进程本身**是管理员。
#
# 用法（在**普通** PowerShell 里执行这一行就行，脚本会自己弹 UAC，**点「是」**）：
#
#   powershell -ExecutionPolicy Bypass -File tools\win-selftest-admin.ps1
#
# 产物：docs\verification\windows-selftest-<日期>-elevated.md
# ⚠️ 期间无线会被短暂关闭约 2 秒（脚本会立刻还原）。

param(
    [switch]$SkipBuild,
    [string]$Dotnet = ""
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $PSScriptRoot 'win-selftest\win-selftest.csproj'
$out  = Join-Path $repo ("docs\verification\windows-selftest-{0}-elevated.md" -f (Get-Date -Format 'yyyy-MM-dd'))

function Resolve-Dotnet {
    if ($Dotnet) { return $Dotnet }
    foreach ($c in @('D:\YQDESK\.dotnet\dotnet.exe', "$env:USERPROFILE\.dotnet\dotnet.exe",
                     'C:\Program Files\dotnet\dotnet.exe')) {
        if (Test-Path $c) {
            $sdks = & $c --list-sdks 2>$null
            if ($sdks) { return $c }
        }
    }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return ""
}

$dotnetExe = Resolve-Dotnet
if (-not $dotnetExe) {
    Write-Error "找不到可用的 dotnet SDK（系统 dotnet 没有 SDK）。用 -Dotnet 指定全路径。"
    exit 2
}

if (-not $SkipBuild) {
    Write-Host "构建自检工具…"
    & $dotnetExe build $proj -c Release --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Error "构建失败"; exit 2 }
}

$exe = Join-Path $PSScriptRoot 'win-selftest\bin\Release\net8.0-windows10.0.19041.0\win-selftest.exe'
if (-not (Test-Path $exe)) { Write-Error "找不到 $exe"; exit 2 }
if (Test-Path $out) { Remove-Item $out -Force }

Write-Host "即将弹出 UAC 授权框 —— **请点「是」**（否则 Windows 会在 120 秒后取消授权）。"
Write-Host "期间无线会被短暂关闭约 2 秒，脚本会立即还原。"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $p = Start-Process -FilePath $exe -ArgumentList '--mutate', '--out', $out -Verb RunAs -PassThru
    Write-Host ("已授权（耗时 {0:N1}s），等待提权进程跑完…" -f $sw.Elapsed.TotalSeconds)
} catch {
    Write-Host ("授权未通过（{0:N1}s）：{1}" -f $sw.Elapsed.TotalSeconds, $_.Exception.Message)
    Write-Host "若弹窗没出现，多半是当前会话没有可交互的桌面（锁屏 / 远程会话）。请在**本机桌面上**再跑一次。"
    exit 3
}

$deadline = (Get-Date).AddSeconds(180)
while ((Get-Date) -lt $deadline -and -not (Test-Path $out)) { Start-Sleep -Seconds 2 }
if (-not (Test-Path $out)) { Write-Error "提权进程没有产出报告"; exit 4 }
Write-Host "报告：$out"
Get-Content $out -Encoding UTF8 | Select-String -Pattern 'WLAN 开关|蓝牙开关' | ForEach-Object { $_.Line }
exit 0
