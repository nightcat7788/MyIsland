# 以管理员启动 MyIsland，并把"确实是提权进程"的证据写进日志（用于稳定性复测）
param([string]$Dir = "")
$ErrorActionPreference = 'Continue'
if (-not $Dir) { $Dir = Join-Path (Split-Path $PSScriptRoot -Parent) 'publish\win-x64' }
$out = Join-Path $env:TEMP 'island-elevated-start.txt'
function Log([string]$m) { "[$(Get-Date -Format 'HH:mm:ss.fff')] $m" | Out-File -FilePath $out -Append -Encoding utf8 }
"--- elevated start $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ---" | Out-File -FilePath $out -Encoding utf8

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Log ("elevated = " + $isAdmin)
$exe = Join-Path $Dir 'Island.App.exe'
Log ("exe = " + $exe + "  exists=" + (Test-Path -LiteralPath $exe))
if (-not (Test-Path -LiteralPath $exe)) { Log 'exe missing - abort'; exit 2 }

$p = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden
Log ("started pid=" + $p.Id + " at " + (Get-Date -Format 'HH:mm:ss.fff'))
Log '--- end ---'
