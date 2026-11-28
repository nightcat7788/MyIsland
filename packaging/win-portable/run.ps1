# Island memory-load launcher (PowerShell 7 / pwsh)
# Host pwsh.exe is Microsoft-signed (WDAC allows) and .NET 8 based.
param(
    [string]$Dir = ""
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Dir)) {
    if (Test-Path (Join-Path $PSScriptRoot 'Island.App.dll')) {
        $Dir = $PSScriptRoot
    } else {
        $Dir = Join-Path $PSScriptRoot 'publish\win-x64'
    }
}
$Dir = [System.IO.Path]::GetFullPath($Dir)
$appDll = Join-Path $Dir 'Island.App.dll'
if (-not (Test-Path -LiteralPath $appDll)) { throw "DLL not found: $appDll" }

# 1) Only load Microsoft-signed managed framework DLLs into Default ALC;
#    skip third-party unsigned DLLs (handled by IslandAlc) and native PE binaries.
$skipPatterns = @(
    'Island.*', 'NAudio*', 'LibreHardwareMonitorLib*', 'HidSharp*',
    'BlackSharp*', 'DiskInfoToolkit*', 'RAMSPDToolkit*'
)
Get-ChildItem -LiteralPath $Dir -Filter *.dll | Where-Object {
    $name = $_.Name
    foreach ($pat in $skipPatterns) { if ($name -like $pat) { return $false } }
    return ($name -like 'System.*' -or $name -like 'Microsoft.*' -or $name -like 'WindowsBase*' -or
            $name -like 'Presentation*' -or $name -like 'ReachFramework*' -or $name -like 'UIAutomation*' -or
            $name -like 'DirectWriteForwarder*')
} | Sort-Object Length | ForEach-Object {
    try { [System.Reflection.Assembly]::LoadFrom($_.FullName) | Out-Null } catch {}
}

$env:ISLAND_DIR = $Dir

# STA on the host main thread is required by WPF
[System.Threading.Thread]::CurrentThread.ApartmentState = [System.Threading.ApartmentState]::STA

# 2) Bootstrap inside IslandAlc
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($appDll))
$appType = $asm.GetType('Island.App.App')
$boot = $appType.GetMethod('Bootstrap', [type[]]@([string]))
if (-not $boot) { throw "Bootstrap entry not found" }

"Island starting..."
$boot.Invoke($null, @($Dir))
"Island exited."
