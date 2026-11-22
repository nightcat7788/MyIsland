$ErrorActionPreference = 'Continue'
# 机器同时有 HTTPS_PROXY 和 https_proxy，Start-Process 构建环境块会字典冲突，先清掉小写的
foreach ($v in 'https_proxy','http_proxy','all_proxy','no_proxy') {
    if (Test-Path "Env:$v") { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
}
$alias = "$env:LOCALAPPDATA\Microsoft\WindowsApps\Microsoft.PowerShell_8wekyb3d8bbwe\pwsh.exe"
$script = "C:\Users\fengl\WorkBuddy\2026-11-21-13-46-35\MyIsland\run.ps1"
$dir = "C:\Users\fengl\WorkBuddy\2026-11-21-13-46-35\MyIsland\publish\win-x64"
$out = "$env:TEMP\island_host_out.txt"
$err = "$env:TEMP\island_host_err.txt"
$p = Start-Process $alias -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File",$script,$dir -PassThru -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err
Start-Sleep -Seconds 25
"host alive: $(-not $p.HasExited)"
if (Test-Path $out) { "--- stdout ---"; Get-Content $out -Tail 10 }
if (Test-Path $err) { "--- stderr ---"; Get-Content $err -Tail 20 }
if (Test-Path "$env:LOCALAPPDATA\MyIsland\run.log") { "--- run.log ---"; Get-Content "$env:LOCALAPPDATA\MyIsland\run.log" }
if (Test-Path "$env:LOCALAPPDATA\MyIsland\crash.log") { "--- crash.log ---"; Get-Content "$env:LOCALAPPDATA\MyIsland\crash.log" -Tail 15 }
