param([switch]$NoBuild)
$ErrorActionPreference = 'Continue'
# MyIsland admin launcher: elevates via UAC, then starts the island host (pwsh).
# Usage: right-click run with PowerShell, or: powershell -ExecutionPolicy Bypass -File start-admin.ps1

$root = $PSScriptRoot

# already elevated? -> just start
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    # re-launch self elevated (UAC prompt once)
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-ExecutionPolicy","Bypass","-File","$root\start-admin.ps1","-NoBuild" -WindowStyle Hidden
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

if (-not $NoBuild) {
    # build first as normal user? build is safe elevated too
    & "C:\Program Files\dotnet\dotnet.exe" publish "$root\src\Island.App\Island.App.csproj" -c Release -r win-x64 --self-contained true -o "$root\publish\win-x64" 2>&1 | Select-Object -Last 2 | ForEach-Object { "$_" }
}

$alias = "$env:LOCALAPPDATA\Microsoft\WindowsApps\Microsoft.PowerShell_8wekyb3d8bbwe\pwsh.exe"
Start-Process $alias -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$root\run.ps1","$root\publish\win-x64" -WindowStyle Hidden
"Island started ELEVATED (CPU temp should be readable now)"
