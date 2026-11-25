param([switch]$NoBuild)
$ErrorActionPreference = 'Continue'
# MyIsland dev loop: stop old island -> run tests -> build & publish -> start new one -> verify heartbeat
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
# ⚠️ 这里必须显式遍历 .Keys 再用索引取值：直接 foreach ($kv in @{...}) 在本机
#    枚举出来的是表本身而非键值对，$kv.Key 为空 ⇒ SetEnvironmentVariable 抛
#    "String cannot be of zero length"（Parameter name: variable）。
$stdEnvFallback = @{
    'ProgramFiles'      = "$env:SystemDrive\Program Files"
    'ProgramFiles(x86)' = "$env:SystemDrive\Program Files (x86)"
    'ProgramData'       = "$env:SystemDrive\ProgramData"
    'PUBLIC'            = "$env:SystemDrive\Users\Public"
}
foreach ($envName in $stdEnvFallback.Keys) {
    if (-not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($envName))) { continue }
    $fallbackValue = $stdEnvFallback[$envName]
    if ([string]::IsNullOrEmpty($fallbackValue)) { continue }
    [Environment]::SetEnvironmentVariable($envName, $fallbackValue, 'Process')
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
            "[1/5] stopped old island host (pid $oldPid)"
        } catch { "[1/5] old host $oldPid not running" }
    }
} else { "[1/5] no previous island" }
Start-Sleep -Milliseconds 800

# -- 2. run tests (gate: fail => abort before publish) --
"[2/5] running tests (Island.Tests)..."
$testOut = & $dotnet run --project "$root\tests\Island.Tests\Island.Tests.csproj" -c Release 2>&1
$testCode = $LASTEXITCODE
if ($testCode -ne 0) {
    $testOut | Select-Object -Last 25 | ForEach-Object { "  $_" }
    throw "tests FAILED (exit $testCode) - publish aborted"
}
$testOut | Select-Object -Last 3 | ForEach-Object { "  $_" }
"[2/5] tests ok"

# -- 3. build & publish --
if (-not $NoBuild) {
    # 3a. 全解决方案门禁：Island.Avalonia（跨平台工程）也必须编得过，且 0 编译/分析器警告。
    #     只发布主工程会把跨平台工程烂在一边，直到有人跑 Mac 构建才炸。
    "[3/5] building solution (Island.App + Island.Avalonia)..."
    $slnOut = & $dotnet build "$root\Island.sln" -c Release 2>&1
    if ($LASTEXITCODE -ne 0) {
        $slnOut | Select-Object -Last 30 | ForEach-Object { "  $_" }
        throw "solution build FAILED - publish aborted"
    }
    # 只统计编译器/分析器告警（CS/CA/IL/XA），NuGet 的 NU* 提示不计入零警告口径
    $warnLines = $slnOut | Select-String -Pattern ': warning (CS|CA|IL|XA)[0-9]{4}'
    if ($warnLines) {
        $warnLines | Select-Object -First 20 | ForEach-Object { "  $($_.Line)" }
        throw "solution build produced $($warnLines.Count) warning(s) - publish aborted"
    }
    "[3/5] solution build ok (0 warning)"

    "[3/5] publishing (Release, self-contained win-x64)..."
    & $dotnet publish "$root\src\Island.App\Island.App.csproj" -c Release -r win-x64 --self-contained true -o $pubDir 2>&1 |
        Select-String -Pattern 'error|->' | Select-Object -Last 3 | ForEach-Object { $_.Line }
    if ($LASTEXITCODE -ne 0) { throw "publish FAILED" }
    # trim non zh/en satellite dirs (size only; harmless if it fails)
    foreach ($l in 'cs','de','es','fr','it','ja','ko','pl','pt-BR','ru','tr','zh-Hant') {
        $d = "$pubDir\$l"
        if (Test-Path $d) { try { Remove-Item $d -Recurse -Force -ErrorAction Stop } catch {} }
    }
    "[3/5] publish ok -> $pubDir"
} else { "[3/5] skipped build (-NoBuild)" }

# -- 4. start new island --
"[4/5] starting island..."
$before = if (Test-Path $runLog) { (Get-Item $runLog).LastWriteTime } else { [datetime]::MinValue }
& powershell.exe -ExecutionPolicy Bypass -File "$root\tools\start-island.ps1"

# -- 5. verify heartbeat got a NEW line --
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
    "[5/5] OK - island is UP: $newLine"
} else {
    "[5/5] WARNING - no new heartbeat within 40s; check %LOCALAPPDATA%\MyIsland\crash.log"
}
