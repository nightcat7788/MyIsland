param(
    # 待检查的发布目录；默认取本仓库 MyIsland\publish\win-x64（脚本位于 MyIsland\tools 下）
    [string]$Dir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish\win-x64')
)
$ErrorActionPreference = 'Continue'
$thirdParty = @('Island.App.dll','LibreHardwareMonitorLib.dll','NAudio.dll','NAudio.Core.dll','NAudio.Wasapi.dll','NAudio.WinMM.dll','NAudio.Midi.dll','HidSharp.dll')
foreach ($name in $thirdParty) {
    $p = Join-Path $Dir $name
    if (Test-Path $p) {
        try {
            $a = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($p))
            "LOADED: $($a.FullName)"
        } catch {
            # Windows PowerShell 5.1 没有 ?? 运算符，这里显式分支取内层异常消息
            $inner = $_.Exception.InnerException
            $msg = if ($inner) { $inner.Message } else { $_.Exception.Message }
            "FAIL: $name -> $msg"
        }
    } else { "MISSING: $name" }
}