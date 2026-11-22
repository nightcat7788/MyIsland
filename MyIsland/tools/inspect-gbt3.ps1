$lib = "C:\Program Files\Gigabyte\Control Center\Lib\MBEasyTune\Gigabyte.HardwareMonitor.dll"
$asm = [System.Reflection.Assembly]::LoadFrom($lib)
$base = $asm.GetType("Gigabyte.HardwareMonitor.Hardware.Processor.AmdCPUHardwareMonitor")
"目标抽象类: $($base.FullName) (abstract=$($base.IsAbstract))"
""
"=== 全程序集找具体子类 ==="
foreach ($t in $asm.GetTypes()) {
    $cur = $t
    while ($cur -and $cur -ne [object]) {
        if ($cur -eq $base) { "$($t.FullName) (abstract=$($t.IsAbstract))"; break }
        $cur = $cur.BaseType
    }
}
""
"=== 温度发射器体系（TemperatureInputTransmitterBase 的子类）==="
$tx = $asm.GetType("Gigabyte.HardwareMonitor.Hardware.TemperatureInputTransmitterBase")
foreach ($t in $asm.GetTypes()) {
    if ($t.BaseType -and ($t.BaseType -eq $tx -or $t.BaseType.FullName -match "Transmitter")) { "$($t.FullName) (abstract=$($t.IsAbstract))" }
}
