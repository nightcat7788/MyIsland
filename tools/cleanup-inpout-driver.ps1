# 清理 2026-11-23 三次 0x1EA 蓝屏留下的未签名内核驱动 inpoutx64.sys
# 应用侧已修复(先判 CPU 厂商再碰驱动 + 默认关闭 + 不再随包发布该 dll),
# 这里负责清掉机器上已经落地的残留文件。
# 注意:路径硬编码 + 多重删除手段 —— 上一版用 Join-Path 拼路径时静默判成了"不存在"。
$ErrorActionPreference = 'Continue'
$out = Join-Path $env:TEMP 'island-cleanup-inpout.txt'
function Log([string]$m) { "[$(Get-Date -Format 'HH:mm:ss')] $m" | Out-File -FilePath $out -Append -Encoding utf8 }
"--- cleanup v2 start $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ---" | Out-File -FilePath $out -Encoding utf8

Log ("elevated = " + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
Log ("SystemRoot = '" + $env:SystemRoot + "'")

$sys = 'C:\Windows\System32\drivers\inpoutx64.sys'
Log ("target = " + $sys)
Log ("Test-Path -LiteralPath = " + (Test-Path -LiteralPath $sys))
Log ("Test-Path -Path       = " + (Test-Path -Path $sys))
try { $f = Get-Item -LiteralPath $sys -ErrorAction Stop; Log ("Get-Item ok: size=" + $f.Length) }
catch { Log ("Get-Item failed: " + $_.Exception.Message) }

# 先停服务（若注册着）
$svc = Get-Service -Name 'inpoutx64' -ErrorAction SilentlyContinue
if ($svc) {
    Log ("service found, status=" + $svc.Status)
    if ($svc.Status -ne 'Stopped') {
        try { Stop-Service -Name 'inpoutx64' -Force -ErrorAction Stop; Log 'service stopped' }
        catch { Log ("stop failed: " + $_.Exception.Message) }
    }
} else { Log 'service: not registered' }

# 删除：依次尝试 Remove-Item / cmd del / File.Delete
$removed = $false
try {
    Remove-Item -LiteralPath $sys -Force -ErrorAction Stop
    Log 'removed via Remove-Item'
    $removed = $true
} catch {
    Log ("Remove-Item failed: " + $_.Exception.Message)
}
if (-not $removed) {
    try {
        & cmd.exe /c ('del /f /q "' + $sys + '"') 2>&1 | ForEach-Object { Log ("cmd del: " + $_) }
        $removed = -not (Test-Path -LiteralPath $sys)
        Log ("cmd del result: removed=" + $removed)
    } catch { Log ("cmd del failed: " + $_.Exception.Message) }
}
if (-not $removed) {
    try {
        [System.IO.File]::Delete($sys)
        $removed = -not (Test-Path -LiteralPath $sys)
        Log ("File.Delete result: removed=" + $removed)
    } catch { Log ("File.Delete failed: " + $_.Exception.Message) }
}

# 服务注册项
$key = 'HKLM:\SYSTEM\CurrentControlSet\Services\inpoutx64'
if (Test-Path -LiteralPath $key) {
    try { Remove-Item -LiteralPath $key -Recurse -Force -ErrorAction Stop; Log 'service key removed' }
    catch { Log ("key remove failed: " + $_.Exception.Message) }
} else { Log 'service key: absent' }

Log ("FINAL file exists = " + (Test-Path -LiteralPath $sys))
Log '--- cleanup v2 end ---'
