
$ErrorActionPreference = 'Stop'
$dll = "C:/Users/fengl/WorkBuddy/2026-11-21-13-46-35/MyIsland/tools/LogoGen/bin/Debug/net8.0-windows/LogoGen.dll"
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($dll))
$prog = $asm.GetType("Program", $false)
$flags = [System.Reflection.BindingFlags]"Static,Public,NonPublic"
$m = $prog.GetMethod("Main", $flags)
$m.Invoke($null, $null)
