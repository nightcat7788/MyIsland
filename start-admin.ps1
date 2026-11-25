param([switch]$NoBuild)
$ErrorActionPreference = 'Continue'
# MyIsland admin launcher: elevates via UAC, then starts the island host.
# Usage: right-click run with PowerShell, or: powershell -ExecutionPolicy Bypass -File start-admin.ps1

$root = $PSScriptRoot

# already elevated? -> just start
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    # re-launch self elevated (UAC prompt once)
    # 关键修复：是否构建由调用方决定。原实现无条件给重开的进程传 -NoBuild，
    # 于是"跑了 start-admin.ps1 却从没构建过"——用户以为在跑新版本，其实在跑旧产物。
    $relaunch = @("-ExecutionPolicy", "Bypass", "-File", ('"{0}"' -f (Join-Path $root 'start-admin.ps1')))
    if ($NoBuild) { $relaunch += "-NoBuild" }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $relaunch -WindowStyle Hidden
    exit
}

# elevated from here: proxy env collision guard (machine has duplicate-case vars)
foreach ($v in 'https_proxy','http_proxy','all_proxy','no_proxy') {
    if (Test-Path "Env:$v") { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
}

# stop old island (non-elevated or elevated host alike)
$runLog = "$env:LOCALAPPDATA\MyIsland\run.log"
if (Test-Path $runLog) {
    $last = Select-String -Path $runLog -Pattern 'pid=(\d+)' | Select-Object -Last 1
    if ($last) {
        try { Stop-Process -Id ([int]$last.Matches[0].Groups[1].Value) -Force -ErrorAction Stop } catch {}
    }
}
Start-Sleep -Milliseconds 800

# ---- .NET SDK 定位 ----
# 系统目录常常只装了运行时（没有 SDK），直接 publish 会报 "No .NET SDKs were found"；
# 原实现写死 "C:\Program Files\dotnet\dotnet.exe" 且没有回落，导致构建静默失败、
# 随后用旧产物启动。这里与 dev.ps1 保持同一套逻辑：系统 SDK -> 用户目录 SDK。
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$hasSdk = $false
if (Test-Path $dotnet) { $hasSdk = [bool](& $dotnet --list-sdks 2>$null) }
if (-not $hasSdk) {
    $userSdk = "$env:USERPROFILE\dotnet-sdk\dotnet.exe"
    if (Test-Path $userSdk) { $dotnet = $userSdk; $hasSdk = [bool](& $dotnet --list-sdks 2>$null) }
}
if (-not $hasSdk) {
    Write-Error "no .NET SDK found (system or $env:USERPROFILE\dotnet-sdk) - cannot build"
    exit 1
}

if (-not $NoBuild) {
    $pubDir = "$root\publish\win-x64"
    & $dotnet publish "$root\src\Island.App\Island.App.csproj" -c Release -r win-x64 --self-contained true -o $pubDir 2>&1 |
        Select-Object -Last 3 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { Write-Error "publish FAILED - aborting"; exit 1 }
    Write-Output "published -> $pubDir"
} else { Write-Output "build skipped (-NoBuild)" }

# ---- 启动宿主：优先 pwsh 内存加载，拿不到 pwsh 就直跑 exe ----
# 旧实例 PID 传给新实例：提权实例会等旧进程真正退出后再接管单实例互斥体，
# 两个实例就不会互相把对方当成"第二实例"而一起消失（2026-11-23 审查 P1-17）。
if ($last -and $last.Matches[0].Groups[1].Value) { $env:ISLAND_ELEVATED_RESTART = $last.Matches[0].Groups[1].Value }
# 原实现写死了带随机后缀的 pwsh 应用别名路径（Microsoft.PowerShell_8wekyb3d8bbwepwsh.exe），
# 那是本机安装实例的私有路径，换机器必失效；而且失败后仍打印 "started ELEVATED"。
$pubDir = "$root\publish\win-x64"
$pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
if (-not $pwsh) {
    $hit = Get-ChildItem -Path "$env:LOCALAPPDATA\Microsoft\WindowsApps" -Filter 'pwsh.exe' -Recurse -ErrorAction SilentlyContinue |
           Where-Object { $_.DirectoryName -like '*Microsoft.PowerShell*' } | Select-Object -First 1
    if ($hit) { $pwsh = $hit.FullName }
}

$started = $null
if ($pwsh -and (Test-Path "$root\run.ps1")) {
    Write-Output "launch mode: pwsh memory-load ($pwsh)"
    $started = Start-Process $pwsh -ArgumentList @("-NoProfile","-ExecutionPolicy","Bypass","-File",('"{0}"' -f (Join-Path $root 'run.ps1')),('"{0}"' -f $pubDir)) -WindowStyle Hidden -PassThru
} else {
    $exe = Join-Path $pubDir 'Island.App.exe'
    if (-not (Test-Path $exe)) { Write-Error "island exe not found: $exe"; exit 2 }
    Write-Output "launch mode: direct exe (no pwsh available) -> $exe"
    $started = Start-Process $exe -WindowStyle Hidden -PassThru
}

if ($started) {
    Start-Sleep -Milliseconds 600
    if ($started.HasExited) {
        Write-Error "Island host exited immediately (exit code $($started.ExitCode)) - see %LOCALAPPDATA%\MyIsland\crash.log"
        exit 3
    }
    Write-Output "Island started ELEVATED (pid $($started.Id); CPU temp should be readable now)"
} else {
    Write-Error "failed to start island host"
    exit 4
}
