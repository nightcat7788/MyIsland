$lib = "C:\Program Files\Gigabyte\Control Center\Lib\MBEasyTune\Gigabyte.HardwareMonitor.dll"
$asm = [System.Reflection.Assembly]::LoadFrom($lib)
$t = $asm.GetType("Gigabyte.HardwareMonitor.Hardware.Processor.AmdCPUHardwareMonitor")
$mon = [System.Activator]::CreateInstance($t)
"实例已创建: $($mon.GetType().FullName)"
$m = $t.GetMethod("GetPackageTemperature")
$val = 0.0
for ($i = 1; $i -le 3; $i++) {
    $args2 = @([single]0)
    $ok = $m.Invoke($mon, $args2)
    "[try $i] 返回=$ok 温度=$($args2[0])"
    Start-Sleep -Seconds 2
}
