# Island memory-load launcher (PowerShell 7 / pwsh)
# Host pwsh.exe is Microsoft-signed (WDAC allows) and .NET 8 based.
# Island.App.dll is byte-loaded; its third-party deps are byte-loaded by
# App.Bootstrap() itself (same Default ALC), avoiding pwsh JIT-context issues.
param(
    [string]$Dir = "$PSScriptRoot\publish\win-x64"
)

$ErrorActionPreference = 'Stop'
$appDll = Join-Path $Dir 'Island.App.dll'
if (-not (Test-Path $appDll)) { throw "DLL not found: $appDll" }

# 1) Microsoft-signed framework DLLs load from disk (WDAC-safe)
Get-ChildItem $Dir -Filter *.dll | Where-Object { $_.Name -notlike 'Island.*' -and $_.Name -notlike 'NAudio*' -and $_.Name -notlike 'LibreHardwareMonitorLib*' -and $_.Name -notlike 'HidSharp*' } |
    Sort-Object Length | ForEach-Object {
        try { [System.Reflection.Assembly]::LoadFrom($_.FullName) | Out-Null } catch {}
    }

# tell the app where its files live (memory-load mode BaseDirectory = pwsh home)
$env:ISLAND_DIR = $Dir

# STA on the host main thread is required by WPF
[System.Threading.Thread]::CurrentThread.ApartmentState = [System.Threading.ApartmentState]::STA

# 2) Load Island.App into the DEFAULT ALC (only to reach Bootstrap; it has no
#    third-party refs). Bootstrap re-loads the app inside a private ALC where
#    all deps resolve from bytes, then blocks on the WPF message pump.
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($appDll))
$appType = $asm.GetType('Island.App.App')
$boot = $appType.GetMethod('Bootstrap', [type[]]@([string]))
if (-not $boot) { throw "Bootstrap entry not found" }

"Island starting..."
$boot.Invoke($null, @($Dir))
"Island exited."
