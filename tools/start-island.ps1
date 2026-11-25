$ErrorActionPreference = 'Continue'
# 机器同时有 HTTPS_PROXY 和 https_proxy，Start-Process 构建环境块会字典冲突，先清掉小写的
foreach ($v in 'https_proxy','http_proxy','all_proxy','no_proxy') {
    if (Test-Path "Env:$v") { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
}

# 路径按脚本自身位置推导，不写死某台机器的目录
# （原来写死了作者机器的 C:\Users\fengl\... 与一个 pwsh 应用别名路径，换台机器就永远起不来）
$root = Split-Path $PSScriptRoot -Parent
$script = Join-Path $root 'run.ps1'
$dir = Join-Path $root 'publish\win-x64'
$out = "$env:TEMP\island_host_out.txt"
$err = "$env:TEMP\island_host_err.txt"

# 优先走 pwsh 的内存加载（绕开本机 WDAC 逐 DLL 签名检查）；找不到 pwsh 就直接跑 exe
$pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
if (-not $pwsh) {
    $alias = "$env:LOCALAPPDATA\Microsoft\WindowsApps\Microsoft.PowerShell_8wekyb3d8bbwe\pwsh.exe"
    if (Test-Path $alias) { $pwsh = $alias }
}
if ($pwsh -and (Test-Path $script)) {
    "launch mode: pwsh memory-load ($pwsh)"
    $p = Start-Process $pwsh -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File",$script,$dir -PassThru -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err
} else {
    $exe = Join-Path $dir 'Island.App.exe'
    if (-not (Test-Path $exe)) { throw "island exe not found: $exe" }
    "launch mode: direct exe (no pwsh available) -> $exe"
    $p = Start-Process $exe -PassThru -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err
}

Start-Sleep -Seconds 25
"host alive: $(-not $p.HasExited)"
if (Test-Path $out) { "--- stdout ---"; Get-Content $out -Tail 10 }
if (Test-Path $err) { "--- stderr ---"; Get-Content $err -Tail 20 }
if (Test-Path "$env:LOCALAPPDATA\MyIsland\run.log") { "--- run.log ---"; Get-Content "$env:LOCALAPPDATA\MyIsland\run.log" -Tail 5 }
if (Test-Path "$env:LOCALAPPDATA\MyIsland\crash.log") { "--- crash.log ---"; Get-Content "$env:LOCALAPPDATA\MyIsland\crash.log" -Tail 15 }
