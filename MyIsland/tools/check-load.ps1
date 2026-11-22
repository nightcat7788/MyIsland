$ErrorActionPreference = 'Continue'
$Dir = "C:\Users\fengl\WorkBuddy\2026-11-21-13-46-35\MyIsland\publish\win-x64"
$thirdParty = @('Island.App.dll','LibreHardwareMonitorLib.dll','NAudio.dll','NAudio.Core.dll','NAudio.Wasapi.dll','NAudio.WinMM.dll','NAudio.Midi.dll','HidSharp.dll')
foreach ($name in $thirdParty) {
    $p = Join-Path $Dir $name
    if (Test-Path $p) {
        try {
            $a = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($p))
            "LOADED: $($a.FullName)"
        } catch { "FAIL: $name -> $($_.Exception.InnerException.Message ?? $_.Exception.Message)" }
    } else { "MISSING: $name" }
}
