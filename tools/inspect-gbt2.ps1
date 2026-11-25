$lib = "C:\Program Files\Gigabyte\Control Center\Lib\MBEasyTune\Gigabyte.HardwareMonitor.dll"
$asm = [System.Reflection.Assembly]::LoadFrom($lib)
$t = $asm.GetType("Gigabyte.HardwareMonitor.Hardware.Processor.AmdCPUHardwareMonitor")
"=== AmdCPUHardwareMonitor 成员 ==="
"基类: $($t.BaseType.FullName)"
$t.GetConstructors() | ForEach-Object { "ctor: $($_.ToString())" }
$t.GetMethods([System.Reflection.BindingFlags]"Public,Instance,Static,DeclaredOnly") | ForEach-Object { "method: $($_.ToString())" }
$t.GetProperties() | ForEach-Object { "prop: $($_.ToString())" }
$t.GetEvents() | ForEach-Object { "event: $($_.ToString())" }
""
"=== TemperatureSensorData 结构 ==="
$d = $asm.GetType("Gigabyte.HardwareMonitor.Hardware.TemperatureSensorData")
$d.GetProperties() | ForEach-Object { "prop: $($_.ToString())" }
$d.GetFields() | ForEach-Object { "field: $($_.ToString())" }
