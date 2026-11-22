$lib = "C:\Program Files\Gigabyte\Control Center\Lib\MBEasyTune\Gigabyte.HardwareMonitor.dll"
$asm = [System.Reflection.Assembly]::LoadFrom($lib)
"已加载: $($asm.FullName)"
"=== 公开类型 ==="
$asm.GetExportedTypes() | ForEach-Object { $_.FullName }
