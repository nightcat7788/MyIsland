param([switch]$NoBuild)
$ErrorActionPreference = 'Continue'
# MyIsland dev loop: stop old island -> build & publish -> start new one -> verify heartbeat
# Usage: powershell -ExecutionPolicy Bypass -File dev.ps1 [-NoBuild]

$root = $PSScriptRoot
# 优先系统 SDK；系统只装了运行时（无 SDK）时回落到用户目录 SDK（dotnet-install 装的位置）
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$hasSdk = (Test-Path $dotnet) -and [bool](& $dotnet --list-sdks 2>$null)
if (-not $hasSdk) {
    $userSdk = "$env:USERPROFILE\dotnet-sdk\dotnet.exe"
    if (Test-Path $userSdk) { $dotnet = $userSdk } else { throw "no .NET SDK found (system or $userSdk)" }
}
# NuGet 机器级路径解析需要这些标准变量（某些精简 shell 会缺）
foreach ($kv in @{
    'ProgramFiles'      = "$env:SystemDrive\Program Files"
    'ProgramFiles(x86)' = "$env:SystemDrive\Program Files (x86)"
    'ProgramData'       = "$env:SystemDrive\ProgramData"
    'PUBLIC'            = "$env:SystemDrive\Users\Public"
}) {
    if ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($kv.Key))) {
        [Environment]::SetEnvironmentVariable($kv.Key, $kv.Value, 'Process')
    }
}
$pubDir = "$root\publish\win-x64"
$runLog = "$env:LOCALAPPDATA\MyIsland\run.log"

# -- 0. avoid Start-Item proxy env collision (machine has HTTPS_PROXY and https_proxy) --
foreach ($v in 'https_proxy','http_proxy','all_proxy','no_proxy') {
    if (Test-Path "Env:$v") { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
}

# -- 1. stop old island host (pid comes from the last heartbeat line) --
if (Test-Path $runLog) {
    $last = Select-String -Path $runLog -Pattern 'pid=(\d+)' | Select-Object -Last 1
    if ($last) {
        $oldPid = [int]$last.Matches[0].Groups[1].Value
        try {
            Stop-Process -Id $oldPid -Force -ErrorAction Stop
            "[1/4] stopped old island host (pid $oldPid)"
        } catch { "[1/4] old host $oldPid not running" }
    }
} else { "[1/4] no previous island" }
Start-Sleep -Milliseconds 800

# -- 2. build & publish --
if (-not $NoBuild) {
    "[2/4] building (Release, self-contained win-x64)..."
    & $dotnet publish "$root\src\Island.App\Island.App.csproj" -c Release -r win-x64 --self-contained true -o $pubDir 2>&1 |
        Select-String -Pattern 'error|->' | Select-Object -Last 3 | ForEach-Object { $_.Line }
    if ($LASTEXITCODE -ne 0) { throw "publish FAILED" }
    # trim non zh/en satellite dirs (size only; harmless if it fails)
    foreach ($l in 'cs','de','es','fr','it','ja','ko','pl','pt-BR','ru','tr','zh-Hant') {
        $d = "$pubDir\$l"
        if (Test-Path $d) { try { Remove-Item $d -Recurse -Force -ErrorAction Stop } catch {} }
    }
    "[2/4] publish ok -> $pubDir"
} else { "[2/4] skipped build (-NoBuild)" }

# -- 3. start new island --
"[3/4] starting island..."
$before = if (Test-Path $runLog) { (Get-Item $runLog).LastWriteTime } else { [datetime]::MinValue }
& powershell.exe -ExecutionPolicy Bypass -File "$root\tools\start-island.ps1"

# -- 4. verify heartbeat got a NEW line --
$deadline = (Get-Date).AddSeconds(40)
$newLine = $null
while ((Get-Date) -lt $deadline) {
    if ((Test-Path $runLog) -and ((Get-Item $runLog).LastWriteTime -gt $before)) {
        $newLine = Get-Content $runLog -Tail 1
        break
    }
    Start-Sleep -Seconds 2
}
if ($newLine) {
    "[4/4] OK - island is UP: $newLine"
} else {
    "[4/4] WARNING - no new heartbeat within 40s; check %LOCALAPPDATA%\MyIsland\crash.log"
}
