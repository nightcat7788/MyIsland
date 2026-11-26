# 阶段 F 验证矩阵执行器（逐轴专项检查版 · 2026-11-26 task-13）。
#
# 用法（仓库根执行）：
#   powershell -ExecutionPolicy Bypass -File tools\verify-matrix.ps1
#   powershell -ExecutionPolicy Bypass -File tools\verify-matrix.ps1 -OutFile docs\verification\2026-11-26-intel-laptop.md
#   powershell -ExecutionPolicy Bypass -File tools\verify-matrix.ps1 -Dotnet "D:\path\to\dotnet.exe"
#   powershell -ExecutionPolicy Bypass -File tools\verify-matrix.ps1 -SkipProbes
#
# 它做什么：
#   1. 采集本机画像（CPU 厂商/型号/核数、显卡清单、有无电池、主板、内存条、操作系统版本）。
#   2. 跑真实探针（判据全部建立在产品真实路径上，不自己另发明规则）：
#        lhm-probe matrix     —— CPU + GPU + 主板传感器快照。开启的硬件类别与产品
#                                ThermalService.OpenComputer 完全一致；取值走产品的
#                                CpuTemperatureRule / HardwareSensorRules；显卡名称与占用
#                                直接跑 Island.Native 的 CounterSampleCache（产品那份代码）。
#        hid-probe --selftest —— 协议表自检 + 真机命中报告 + 与产品路径的两路复核。
#        hid-probe unknown    —— 未识别设备摘要（一行一设备：命中层 + VID/PID + 描述符），
#                                这正是 F4「需要补协议表项」的输入。
#   3. **逐轴执行专项检查**：F1 AMD / F2 Intel 桌面 / F3 核显+独显 / F4 其它 2.4G 接收器，
#      每一项都写清楚：检查项 / 判据 / 实测值 / 结论 / 原始证据。
#   4. 输出 Markdown 报告（默认落到 docs\verification\），并打印落盘路径与逐项结论。
#
# 诚实性底线：
#   · 读不到就说读不到，并写清是「缺权限 / 无该传感器 / 未采集」哪一种；
#   · 绝不用 0 或默认值充数；
#   · 「本机不适用」「无样本」「验证通过」三者分开写 —— 无样本不冒充通过。
#
# 退出码：
#   0 —— 本机**适用且有样本**的检查全部通过；
#   1 —— 有适用检查失败或读不到；
#   2 —— 缺工具链（dotnet 找不到 / 探针构建失败 / 探针跑不起来）。
#
# ⚠️ 关于权限：读 MSR 温度需要 PawnIO 驱动 + 管理员权限。没有权限时报告会写「读不到（缺权限）」，
#    这属于事实陈述，不是失败；产品的对应表现是显示「--」。
# ⚠️ 关于 SMN：本脚本**绝不加载** inpoutx64.dll、不装未签名驱动、不映射物理内存 ——
#    那条通道在 2026-11-23 造成过三次 0x1EA 蓝屏。SMN 的实际可读性必须由用户在设置中心
#    显式开启后、以管理员运行产品来验证（报告里列为「需人工介入」）。

param(
    [string]$OutFile = "",
    [switch]$SkipProbes,
    [string]$Dotnet = ""
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$lf = [Environment]::NewLine

# ---------------------------------------------------------------- 检查清单

$script:checks = New-Object System.Collections.ArrayList

function Add-Check {
    param(
        [string]$Axis,
        [string]$Id,
        [string]$Item,
        [string]$Criterion,
        [string]$Measured,
        [string]$Status,
        [string]$Evidence = ""
    )
    $null = $script:checks.Add([pscustomobject]@{
        Axis = $Axis; Id = $Id; Item = $Item; Criterion = $Criterion
        Measured = $Measured; Status = $Status; Evidence = $Evidence
    })
}

# Markdown 单元格：压成一行 + 转义竖线
function Fmt {
    param([string]$Text)
    if ($null -eq $Text) { return '' }
    $t = $Text -replace '\r?\n', ' '
    $t = $t -replace '\|', '\|'
    return $t.Trim()
}

# 解析 "key=value" / key="value" 的一行（hid-probe unknown 的输出格式）
function Parse-Kv {
    param([string]$Text)
    $h = @{}
    if ([string]::IsNullOrEmpty($Text)) { return $h }
    foreach ($m in [regex]::Matches($Text, '([A-Za-z_][A-Za-z0-9_]*)=("[^"]*"|\S+)')) {
        $v = $m.Groups[2].Value
        if ($v.Length -ge 2 -and $v.StartsWith('"') -and $v.EndsWith('"')) {
            $v = $v.Substring(1, $v.Length - 2)
        }
        $h[$m.Groups[1].Value] = $v
    }
    return $h
}

# CPU 温度传感器的分层（抄自产品 HardwareSensorRules.CpuTemperatureTiers / CpuTemperatureRule）。
# 用途是**核对产品规则自己的输出**（选了哪一层对不对），不用于替代产品规则做选择。
function Get-CpuTempTier {
    param([string]$Name)
    if ([string]::IsNullOrEmpty($Name)) { return 0 }
    $n = $Name.ToLowerInvariant()
    if ($n.Contains('package')) { return 1 }
    if ($n.Contains('tdie') -and -not $n.Contains('tctl')) { return 2 }
    if ($n.Contains('tctl') -and $n.Contains('tdie')) { return 3 }
    if ($n.Contains('tctl')) { return 4 }
    if ($n.Contains('ccd')) { return 5 }
    if ($n.Contains('core max') -or $n.Contains('max core')) { return 6 }
    $agg = @('average', 'max', 'package', 'tdie', 'tctl', 'ccd')
    $isAgg = $false
    foreach ($a in $agg) { if ($n.Contains($a)) { $isAgg = $true } }
    if (-not $isAgg -and ($n.Contains('core') -or $n.Contains('cpu'))) { return 7 }
    if ($n.Contains('average')) { return 8 }
    return 0
}

# 干扰项（抄自产品 CpuTemperatureRule.Distractors / HardwareSensorRules 的排除清单）
function Test-Distractor {
    param([string]$Name)
    if ([string]::IsNullOrEmpty($Name)) { return $false }
    $n = $Name.ToLowerInvariant()
    foreach ($bad in @('distance', 'tjmax', 'soc', 'vrm', 'pump', 'liquid', 'coolant', 'fan')) {
        if ($n.Contains($bad)) { return $true }
    }
    return $false
}

# 跑外部程序：把 stderr 也收进来，但不让它变成终止错误（$ErrorActionPreference=Stop 时会）。
function Invoke-Native {
    param([string]$FilePath, [string[]]$Arguments)
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $FilePath @Arguments 2>&1 | Out-String
        $code = $LASTEXITCODE
        return [pscustomobject]@{ Output = $out; ExitCode = $code }
    } finally {
        $ErrorActionPreference = $old
    }
}

function Get-Profile {
    $p = [ordered]@{}
    $p.OS = Get-CimInstance Win32_OperatingSystem
    $p.CPU = @(Get-CimInstance Win32_Processor)
    $p.GPU = @(Get-CimInstance Win32_VideoController)
    $p.Battery = @(Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue)
    $p.Board = Get-CimInstance Win32_BaseBoard -ErrorAction SilentlyContinue
    $p.Mem = @(Get-CimInstance Win32_PhysicalMemory -ErrorAction SilentlyContinue)
    $p.Model = Get-CimInstance Win32_ComputerSystem
    return $p
}

# MSBatteryClass（产品 DeviceService.SampleBluetoothBattery 用的同一个类）
function Get-MsBatteryClass {
    try {
        return @(Get-CimInstance -Namespace 'root\WMI' -ClassName 'MSBatteryClass' -ErrorAction Stop)
    } catch {
        return @()
    }
}

function Resolve-Dotnet {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($Dotnet) {
            if (-not (Test-Path $Dotnet)) {
                throw ("找不到指定的 dotnet：" + $Dotnet + "（-Dotnet 给的必须是可执行文件全路径）")
            }
            $sdks = & $Dotnet --list-sdks 2>$null
            if (-not $sdks) { throw ("指定的 dotnet 没有可用的 SDK：" + $Dotnet + "（--list-sdks 为空）") }
            return $Dotnet
        }
        foreach ($c in @('D:\YQDESK\.dotnet\dotnet.exe', (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'), 'C:\Program Files\dotnet\dotnet.exe')) {
            if (Test-Path $c) {
                $sdks = & $c --list-sdks 2>$null
                if ($sdks) { return $c }
            }
        }
        $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($cmd) {
            $sdks = & $cmd.Source --list-sdks 2>$null
            if ($sdks) { return $cmd.Source }
        }
        return ""
    } finally {
        $ErrorActionPreference = $old
    }
}

# ---------------------------------------------------------------- 本机画像

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

$prof = Get-Profile
$cpu = @($prof.CPU)[0]
$gpuNames = @($prof.GPU | ForEach-Object { $_.Name } | Where-Object { $_ })
$batteryCount = @($prof.Battery).Count
$hasBattery = $batteryCount -gt 0
$msBattery = Get-MsBatteryClass
$internalBattery = @($msBattery | Where-Object {
    $i = [string]$_.InstanceName
    $l = $i.ToLowerInvariant()
    $l.Contains('microsoft') -or $l.Contains('acpi') -or $l.Contains('pnp0c0a')
})

# CPU 厂商：与产品 InpOutSmnService.IsAmdCpu() 同源（注册表 VendorIdentifier）
$vendorId = ''
try {
    $vendorId = [string](Get-ItemProperty -Path 'HKLM:\HARDWARE\DESCRIPTION\System\CentralProcessor\0' -Name 'VendorIdentifier' -ErrorAction Stop).VendorIdentifier
} catch { $vendorId = '' }
$isAmd = ($vendorId -match 'AMD') -or ($cpu.Name -match 'AMD|Ryzen')
$isIntel = (-not $isAmd) -and (($vendorId -match 'Intel') -or ($cpu.Name -match 'Intel|Core|Ultra'))

# ---------------------------------------------------------------- 跑探针

$dotnetExe = ''
$toolchainError = ''
$lhmJson = $null
$lhmRaw = ''
$selftestText = ''
$selftestExit = -1
$unknownText = ''
$unknownExit = -1
$buildLog = @()

if ($SkipProbes) {
    $toolchainError = ''
} else {
    try {
        $dotnetExe = Resolve-Dotnet
    } catch {
        $toolchainError = $_.Exception.Message
    }
    if ((-not $toolchainError) -and (-not $dotnetExe)) {
        $toolchainError = '找不到可用的 dotnet SDK（系统 dotnet 没有 SDK）。请用 -Dotnet 指定全路径，例如 D:\YQDESK\.dotnet\dotnet.exe'
    }
    if (-not $toolchainError) {
        foreach ($proj in @('tools\lhm-probe\lhm-probe.csproj', 'tools\hid-probe\hid-probe.csproj')) {
            $projPath = Join-Path $repo $proj
            $b = Invoke-Native $dotnetExe @('build', $projPath, '-c', 'Release', '--nologo', '-v', 'quiet')
            if ($b.ExitCode -ne 0) {
                $toolchainError = ("探针构建失败：" + $proj + $lf + $b.Output.Trim())
                break
            }
            $buildLog += ($proj + " ⇒ 构建通过")
        }
    }
    if (-not $toolchainError) {
        $lhmDll = Join-Path $repo 'tools\lhm-probe\bin\Release\net8.0-windows\lhm-probe.dll'
        $hidDll = Join-Path $repo 'tools\hid-probe\bin\Release\net8.0-windows10.0.19041.0\hid-probe.dll'
        $jsonPath = Join-Path ([System.IO.Path]::GetTempPath()) ('lhm-matrix-' + [guid]::NewGuid().ToString('N') + '.json')
        try {
            $lhmRun = Invoke-Native $dotnetExe @($lhmDll, 'matrix', $jsonPath)
            $lhmRaw = $lhmRun.Output
            if ($lhmRun.ExitCode -ne 0) {
                $toolchainError = ("lhm-probe matrix 执行失败（退出码 " + $lhmRun.ExitCode + "）：" + $lhmRaw.Trim())
            } elseif (Test-Path $jsonPath) {
                $raw = [System.IO.File]::ReadAllText($jsonPath)
                try {
                    $lhmJson = $raw | ConvertFrom-Json
                } catch {
                    $toolchainError = "lhm-probe matrix 的 JSON 解析失败：" + $_.Exception.Message
                }
            } else {
                $toolchainError = 'lhm-probe matrix 没有产出 JSON（传感器快照缺失）'
            }
        } finally {
            if (Test-Path $jsonPath) { Remove-Item $jsonPath -Force -ErrorAction SilentlyContinue }
        }
    }
    if (-not $toolchainError) {
        $selRun = Invoke-Native $dotnetExe @($hidDll, '--selftest')
        $selftestText = $selRun.Output
        $selftestExit = $selRun.ExitCode
        $unkRun = Invoke-Native $dotnetExe @($hidDll, 'unknown')
        $unknownText = $unkRun.Output
        $unknownExit = $unkRun.ExitCode
    }
}

# ---------------------------------------------------------------- 派生事实

$gpuDisplayName = ''
$gpuUsage = $null
$videoControllers = @()
$igpuCount = 0
$dgpuCount = 0
$gpuDetectSource = '未知'
$cpuValidTemps = @()
$cpuAllTemps = @()
$cpuRulePick = $null
$cpuProductPick = $null
$ccdSensors = @()
$mbTemperatureEntries = 0
$mbTempPick = $null
$gpus = @()

if ($lhmJson) {
    if ($lhmJson.gpuSelection) {
        $gpuDisplayName = [string]$lhmJson.gpuSelection.displayName
        $gpuUsage = $lhmJson.gpuSelection.usagePercent
        $videoControllers = @($lhmJson.gpuSelection.videoControllers)
        $adapters = @($lhmJson.gpuSelection.d3dAdapters)
        $real = @($adapters | Where-Object { $_.isSoftware -ne $true })
        if ($real.Count -gt 0) {
            $gpuDetectSource = 'D3D 适配器的 Integrated 标志（产品 IntelComputePolicy 同一口径）'
            foreach ($a in $real) {
                if ($a.integrated -eq $true) { $igpuCount++ }
                elseif ($a.integrated -eq $false) { $dgpuCount++ }
            }
        } else {
            $gpuDetectSource = '显卡名字面启发式（D3D 探测不可用时的退路）'
            foreach ($n in $gpuNames) {
                if ($n -match 'Intel.*(Graphics|UHD|Iris|Arc)') { $igpuCount++ }
                elseif ($n -match 'NVIDIA|GeForce|Radeon RX|Radeon Pro') { $dgpuCount++ }
            }
        }
    }
    if ($lhmJson.cpu) {
        $cpuAllTemps = @($lhmJson.cpu.temperatureSensors)
        $cpuValidTemps = @($cpuAllTemps | Where-Object { $null -ne $_.value -and [double]$_.value -gt 0.01 })
        $cpuRulePick = $lhmJson.cpu.rulePick
        $cpuProductPick = $lhmJson.cpu.productPick
        $ccdSensors = @($lhmJson.cpu.ccdSensors)
    }
    if ($lhmJson.motherboard) {
        $mbTemperatureEntries = @($lhmJson.motherboard.temperatureSensors).Count
        foreach ($sub in @($lhmJson.motherboard.subHardware)) {
            $mbTemperatureEntries += @($sub.temperatureSensors).Count
        }
        $mbTempPick = $lhmJson.motherboard.temperaturePick
    }
    $gpus = @($lhmJson.gpus)
}

$hasIgpu = $igpuCount -gt 0
$hasDgpu = $dgpuCount -gt 0

# hid-probe unknown 的输出解析
$unknownDevices = @()
if ($unknownText) {
    $cur = $null
    foreach ($line in ($unknownText -split '\r?\n')) {
        if ($line -match '^\[DEVICE\]\s+(.*)$') {
            $kv = Parse-Kv $Matches[1]
            $cur = [pscustomobject]@{
                Layer = [string]$kv['layer']; Vid = [string]$kv['vid']; Pid = [string]$kv['pid']
                Name = [string]$kv['name']; Kind = [string]$kv['kind']; Collections = [string]$kv['collections']
                NeedsEntry = ([string]$kv['needsProtocolEntry'] -eq 'true'); Entry = [string]$kv['entry']
                Detail = @()
            }
            $unknownDevices += $cur
        } elseif ($line -match '^\s+\[COLLECTION\]\s+(.*)$' -and $null -ne $cur) {
            $cur.Detail += (Parse-Kv $Matches[1])
        }
    }
}

# ---------------------------------------------------------------- F1 · AMD 平台

$f1Applicable = $isAmd
$f1Why = if ($isAmd) { "本机 CPU 厂商 = $vendorId（AMD）" } else { "本机 CPU 厂商 = $vendorId（非 AMD）" }
$cF1 = '① 厂商判定取自注册表 VendorIdentifier（与产品 InpOutSmnService.IsAmdCpu 同一口径）；② 非 AMD 机器上该通道在 LoadLibrary / 装驱动 / 映射物理内存**之前**就跳过；③ 机器上不应残留未签名的 inpoutx64.sys（2026-11-23 三次 0x1EA 蓝屏的物证路径）。'
$cF2 = 'AMD 机器上：用户在设置中心显式开启 EnableSmnDirectRead + 以管理员运行时，产品应能读出 CPU 温度。'
$cF3 = 'AMD 机器上：LHM 的 CPU 温度传感器里若有 CCD 条目，必须能读到值；没有 CCD 条目就如实记「该机未暴露 CCD 传感器」（不是 0）。'
$cF4 = 'AMD 机器上：产品规则（HardwareSensorRules.Pick(CpuTemperature) / CpuTemperatureRule.Pick）选中的传感器必须符合层序 package → tdie → tctl/tdie → tctl → ccd → core max → 逐核 → average；Tctl 不得排在 Tdie 前面；「Distance to TjMax」不得被选中。'
$cF5 = '产品规则在没有有效读数时必须返回「未选到」，绝不能返回 0 或默认值；两套规则（CpuTemperatureRule 与 HardwareSensorRules）口径一致。'

# F1-1 SMN 通道的前置判定与驱动残留（任何机器都适用：安全侧恒真检查）
$inpoutSys = Join-Path $env:SystemRoot 'System32\drivers\inpoutx64.sys'
$hasDriverResidue = Test-Path $inpoutSys
$repoInpout = Join-Path $repo 'tools\inpout\inpoutx64.dll'
$hasInpoutDll = Test-Path $repoInpout
$smnSetting = '未设置（产品默认 EnableSmnDirectRead=false）'
$settingsPath = Join-Path $env:LOCALAPPDATA 'MyIsland\settings.json'
if (Test-Path $settingsPath) {
    try {
        $s = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($s.PSObject.Properties.Name -contains 'EnableSmnDirectRead') {
            $smnSetting = "settings.json 里 EnableSmnDirectRead = " + [string]$s.EnableSmnDirectRead
        } else {
            $smnSetting = 'settings.json 里没有 EnableSmnDirectRead 字段（用产品默认值 false）'
        }
    } catch { $smnSetting = 'settings.json 读不出来（按产品默认值 false 处理）' }
}
$f1Measured = "vendor=$vendorId；SMN 开关：$smnSetting；未签名驱动残留 inpoutx64.sys=" + $(if ($hasDriverResidue) { '有' } else { '无' }) +
    "；仓库 tools\inpout\inpoutx64.dll=" + $(if ($hasInpoutDll) { '有' } else { '无（产品本来就不随包发布它）' })
Add-Check 'F1' 'F1-1' 'SMN 通道前置判定与驱动残留' $cF1 $f1Measured $(if ($hasDriverResidue) { '失败' } else { '通过' }) '附录 D'

# F1-2 SMN 直读的实际可读性（只有 AMD 机器上谈得上）
if (-not $isAmd) {
    Add-Check 'F1' 'F1-2' 'SMN 直读实际可读性' $cF2 "本机 CPU 厂商 = $vendorId（非 AMD）⇒ 产品 InpOutSmnService.EnsureLoaded 第一步就跳过（不碰驱动）" '不适用' '附录 D'
} else {
    Add-Check 'F1' 'F1-2' 'SMN 直读实际可读性' $cF2 '本脚本**不代劳**：加载 inpoutx64.dll 会装未签名驱动并映射 PCIe 物理内存，2026-11-23 造成过三次 0x1EA 蓝屏。需人工介入：用户在设置中心开启后由产品自测。' '需人工介入' '附录 D'
}

# F1-3 CCD 温度传感器（只有 AMD 机器上谈得上）
if (-not $isAmd) {
    Add-Check 'F1' 'F1-3' 'CCD 温度传感器' $cF3 "本机是 $vendorId（非 AMD）⇒ 无 CCD 传感器可言" '不适用' '附录 A'
} elseif ($null -eq $lhmJson) {
    Add-Check 'F1' 'F1-3' 'CCD 温度传感器' $cF3 '探针未产出传感器快照' '未执行' '附录 A'
} elseif ($ccdSensors.Count -eq 0) {
    Add-Check 'F1' 'F1-3' 'CCD 温度传感器' $cF3 '本机 LHM 的 CPU 温度传感器里没有 CCD 条目 ⇒ 该机未暴露 CCD 传感器（不是 0）。本格无样本：CCD 温度这一格仍未被验证。' '无样本' '附录 A'
} else {
    $ccdValid = @($ccdSensors | Where-Object { $null -ne $_.value })
    if ($ccdValid.Count -eq $ccdSensors.Count) {
        Add-Check 'F1' 'F1-3' 'CCD 温度传感器' $cF3 ("CCD 条目 " + $ccdSensors.Count + " 个，全部有读数：" + (($ccdSensors | ForEach-Object { $_.name + '=' + $_.value }) -join '、')) '通过' '附录 A'
    } else {
        Add-Check 'F1' 'F1-3' 'CCD 温度传感器' $cF3 ("CCD 条目 " + $ccdSensors.Count + " 个，但读不到（缺权限 / 未采集）") '读不到' '附录 A'
    }
}

# F1-4 Tctl / Tdie 取舍（只有 AMD 机器上谈得上）
$ruleTier = 0
$ruleProblems = @()
if ($cpuRulePick -and $cpuRulePick.sensorName) {
    $ruleTier = Get-CpuTempTier $cpuRulePick.sensorName
    if (Test-Distractor $cpuRulePick.sensorName) { $ruleProblems += ("选中的「" + $cpuRulePick.sensorName + "」是干扰项（距离 / SoC / VRM / 水泵 / 风扇）") }
    $best = 99
    foreach ($s in $cpuValidTemps) {
        $t = Get-CpuTempTier $s.name
        if ($t -gt 0 -and $t -lt $best) { $best = $t }
    }
    if ($best -lt 99 -and $ruleTier -ne $best) {
        $ruleProblems += ("选中的层（第 " + $ruleTier + " 层）不是候选里最高的层（第 " + $best + " 层）")
    }
}
if (-not $isAmd) {
    Add-Check 'F1' 'F1-4' 'Tctl / Tdie 取舍' $cF4 "本机是 $vendorId（非 AMD）⇒ Tctl/Tdie 取舍不适用" '不适用' '附录 A'
} elseif ($null -eq $lhmJson) {
    Add-Check 'F1' 'F1-4' 'Tctl / Tdie 取舍' $cF4 '探针未产出传感器快照' '未执行' '附录 A'
} elseif ($cpuValidTemps.Count -eq 0) {
    Add-Check 'F1' 'F1-4' 'Tctl / Tdie 取舍' $cF4 ('CPU 温度传感器有 ' + $cpuAllTemps.Count + ' 个条目，但**没有有效读数**（缺权限 / 未采集）⇒ 规则返回「未选到」，取舍本格无样本') '无样本' '附录 A'
} elseif ($null -eq $cpuRulePick -or -not $cpuRulePick.sensorName) {
    Add-Check 'F1' 'F1-4' 'Tctl / Tdie 取舍' $cF4 ('有 ' + $cpuValidTemps.Count + ' 个有效读数，但产品规则返回「未选到」⇒ 规则没选中任何传感器') '失败' '附录 A'
} elseif ($ruleProblems.Count -gt 0) {
    Add-Check 'F1' 'F1-4' 'Tctl / Tdie 取舍' $cF4 ("规则选中「" + $cpuRulePick.sensorName + "」（第 " + $ruleTier + " 层），但：" + ($ruleProblems -join '；')) '失败' '附录 A'
} else {
    Add-Check 'F1' 'F1-4' 'Tctl / Tdie 取舍' $cF4 ("规则选中「" + $cpuRulePick.sensorName + "」= " + $cpuRulePick.value + "（第 " + $ruleTier + " 层：package=1 / tdie=2 / tctl-tdie=3 / tctl=4 / ccd=5 / core-max=6 / 逐核=7 / average=8）；候选里最高层也是第 " + $ruleTier + " 层 ⇒ 取舍正确") '通过' '附录 A'
}

# F1-5 CPU 温度取值规则的自检（任何机器都适用）
if ($null -eq $lhmJson) {
    Add-Check 'F1' 'F1-5' 'CPU 温度取值规则自检（无读数时必须返回「未选到」）' $cF5 '探针未产出传感器快照' '未执行' '附录 A'
} elseif ($cpuValidTemps.Count -eq 0) {
    $ruleName = if ($cpuRulePick -and $cpuRulePick.sensorName) { $cpuRulePick.sensorName } else { '未选到' }
    $prodName = if ($cpuProductPick -and $cpuProductPick.sensorName) { $cpuProductPick.sensorName } else { '未选到' }
    Add-Check 'F1' 'F1-5' 'CPU 温度取值规则自检（无读数时必须返回「未选到」）' $cF5 ('CPU 温度传感器 ' + $cpuAllTemps.Count + ' 个条目、有效读数 0 个（缺权限 / 未采集）⇒ CpuTemperatureRule 返回「' + $ruleName + '」，HardwareSensorRules 返回「' + $prodName + '」：**没有伪造读数**，符合预期') '通过' '附录 A'
} else {
    $problems = @()
    if ($null -eq $cpuRulePick -or -not $cpuRulePick.sensorName) {
        $problems += '有有效读数但 CpuTemperatureRule 未选中任何传感器'
    } else {
        if (Test-Distractor $cpuRulePick.sensorName) { $problems += 'CpuTemperatureRule 选中了干扰项' }
        if ($cpuProductPick -and $cpuProductPick.sensorName -and $cpuProductPick.sensorName -ne $cpuRulePick.sensorName) {
            $problems += ("两套规则选了不同的传感器：" + $cpuRulePick.sensorName + ' vs ' + $cpuProductPick.sensorName)
        }
    }
    if ($problems.Count -gt 0) {
        Add-Check 'F1' 'F1-5' 'CPU 温度取值规则自检' $cF5 ($problems -join '；') '失败' '附录 A'
    } else {
        Add-Check 'F1' 'F1-5' 'CPU 温度取值规则自检（无读数时必须返回「未选到」）' $cF5 ("有效读数 " + $cpuValidTemps.Count + " 个；两套规则一致选中「" + $cpuRulePick.sensorName + "」= " + $cpuRulePick.value) '通过' '附录 A'
    }
}

# ---------------------------------------------------------------- F2 · Intel 桌面平台

$f2Applicable = (-not $hasBattery)
$f2Why = if ($hasBattery) { "本机有内置电池（移动平台）" } else { "本机无内置电池（桌面平台）" }
$c21 = '台式机上：Win32_Battery 必须**没有实例**（不是 0%），root\WMI 的 MSBatteryClass 也不得有内置电池实例；产品 DeviceService.ReadWmiBattery 返回 null ⇒ 界面不显示电池，而不是显示 0%。'
$c22 = '台式机上：LHM 主板硬件（含子硬件）里的温度传感器要么读到值，要么如实显示「--」；**没有条目 ≠ 0**，绝不能显示 0 ℃。'
$c23 = '桌面机上（只有独显）：产品显示的 GPU 名字必须是那块独显；虚拟显示器 / 基础显示适配器不得顶掉它。'

# F2-1 无电池路径
if ($hasBattery) {
    Add-Check 'F2' 'F2-1' '无电池路径（内置电池读数应当不存在，而不是 0）' $c21 ("本机 Win32_Battery 有 " + $batteryCount + " 个实例、MSBatteryClass 内置电池实例 " + $internalBattery.Count + " 个 ⇒ 是笔记本，F2 轴前提不成立") '不适用' '附录 D'
} elseif ($batteryCount -eq 0 -and $internalBattery.Count -eq 0) {
    Add-Check 'F2' 'F2-1' '无电池路径（内置电池读数应当不存在，而不是 0）' $c21 'Win32_Battery 实例 0 个、MSBatteryClass 内置电池实例 0 个 ⇒ 内置电池读数**不存在**（不是 0%）' '通过' '附录 D'
} else {
    Add-Check 'F2' 'F2-1' '无电池路径（内置电池读数应当不存在，而不是 0）' $c21 ("Win32_Battery 实例 " + $batteryCount + " 个、MSBatteryClass 内置电池实例 " + $internalBattery.Count + " 个 ⇒ 与「无电池」不符") '失败' '附录 D'
}

# F2-2 主板 / SuperIO 温度
if ($null -eq $lhmJson) {
    $mbMeasured = '探针未产出传感器快照'
} elseif ($mbTemperatureEntries -eq 0) {
    $mbMeasured = ("LHM 主板硬件「" + $lhmJson.motherboard.hardwareName + "」及其子硬件里**没有任何温度传感器条目** ⇒ 产品必须显示「--」（无传感器 ≠ 0）")
} elseif ($mbTempPick -and $mbTempPick.sensorName) {
    $mbMeasured = ("主板温度条目 " + $mbTemperatureEntries + " 个；产品规则选中「" + $mbTempPick.sensorName + "」= " + $mbTempPick.value + " ℃")
} else {
    $mbMeasured = ("主板温度条目 " + $mbTemperatureEntries + " 个，但**读不到值**（缺权限 / 未采集）⇒ 产品必须显示「--」，不得显示 0")
}
if ($hasBattery) {
    Add-Check 'F2' 'F2-2' '主板 / SuperIO 温度' $c22 ("本机是笔记本（F2 轴前提不成立）。顺带事实：" + $mbMeasured) '不适用' '附录 A'
} elseif ($null -eq $lhmJson) {
    Add-Check 'F2' 'F2-2' '主板 / SuperIO 温度' $c22 $mbMeasured '未执行' '附录 A'
} elseif ($mbTemperatureEntries -eq 0) {
    Add-Check 'F2' 'F2-2' '主板 / SuperIO 温度' $c22 $mbMeasured '通过' '附录 A'
} elseif ($mbTempPick -and $mbTempPick.sensorName) {
    Add-Check 'F2' 'F2-2' '主板 / SuperIO 温度' $c22 $mbMeasured '通过' '附录 A'
} else {
    Add-Check 'F2' 'F2-2' '主板 / SuperIO 温度' $c22 $mbMeasured '读不到' '附录 A'
}

# F2-3 无核显路径下的 GPU 选择口径
if ($null -eq $lhmJson) {
    $gpuSelMeasured = '探针未产出传感器快照'
} else {
    $vc = if ($videoControllers.Count) { $videoControllers -join ' / ' } else { '（未采集到）' }
    $gpuSelMeasured = ("显卡清单：" + $vc + "；产品口径（CounterSampleCache.GuessGpuName：跳过 Virtual / Basic Display / Remote Display / Indirect，取第一个其余适配器）选中「" + $gpuDisplayName + "」")
}
if ($hasBattery) {
    Add-Check 'F2' 'F2-3' '无核显路径下的 GPU 选择口径' $c23 ("本机是笔记本（F2 轴前提不成立）。顺带事实：" + $gpuSelMeasured) '不适用' '附录 A'
} elseif ($null -eq $lhmJson) {
    Add-Check 'F2' 'F2-3' '无核显路径下的 GPU 选择口径' $c23 $gpuSelMeasured '未执行' '附录 A'
} elseif ([string]::IsNullOrEmpty($gpuDisplayName) -or $gpuDisplayName -eq 'GPU') {
    Add-Check 'F2' 'F2-3' '无核显路径下的 GPU 选择口径' $c23 ($gpuSelMeasured + ' ⇒ 没有选出真实适配器') '失败' '附录 A'
} elseif ($gpuDisplayName -match 'Virtual|Basic Display|Remote Display|Indirect') {
    Add-Check 'F2' 'F2-3' '无核显路径下的 GPU 选择口径' $c23 ($gpuSelMeasured + ' ⇒ **选中了虚拟显示器**，不对') '失败' '附录 A'
} else {
    Add-Check 'F2' 'F2-3' '无核显路径下的 GPU 选择口径' $c23 ($gpuSelMeasured + ' ⇒ 选中的是真实适配器，虚拟显示器没有顶掉它') '通过' '附录 A'
}

# ---------------------------------------------------------------- F3 · 核显 + 独显

$f3Applicable = ($hasIgpu -and $hasDgpu)
$f3Why = if ($f3Applicable) { "本机核显 $igpuCount 块、独显 $dgpuCount 块" } else { "本机核显 $igpuCount 块、独显 $dgpuCount 块（判定方式：$gpuDetectSource）" }
$c31 = '多卡时三个口径分别算出来并互相对照：① 显示名口径 = CounterSampleCache.GuessGpuName（跳过 Virtual / Basic Display / Remote Display / Indirect，取第一个其余适配器）；② 温度口径 = ThermalService 的「优先选功耗>0 的那块（独显），否则取先遇到的那块」；③ 占用口径 = PDH 最忙引擎（全体取 max，应用只显示一个百分比）。三者指向不同的物理卡时必须写进报告（事实，不是失败）。'
$c32 = '按产品规则（HardwareSensorRules 的 GpuMemoryUsed / GpuMemoryTotal：厂商显存 → D3D 专用显存 → D3D 共享显存）选出的读数：总量必须 > 0；两者都有值时已用必须 ≤ 总量；没有该传感器就记「无该传感器」，不填 0。'
$c33 = '温度必须能区分（LHM 每块 GPU 各有自己的温度传感器）；占用的可区分性按产品现状记录（应用只显示一个百分比 = 最忙引擎 max，不做每卡拆分 —— 这是事实，不是失败）。'

if ($null -eq $lhmJson) {
    $selDetail = '探针未产出传感器快照'
    $memDetail = '探针未产出传感器快照'
    $splitDetail = '探针未产出传感器快照'
} else {
    $thermalChoice = if ($lhmJson.thermalGpuChoice) { $lhmJson.thermalGpuChoice.hardwareName } else { '（无 —— 没有一块 GPU 有温度读数）' }
    $perAdapter = ''
    if (@($lhmJson.gpuSelection.perAdapter).Count -gt 0) {
        $perAdapter = (@($lhmJson.gpuSelection.perAdapter) | ForEach-Object { $_.adapterKey + '=' + $_.percent + '%' }) -join '、'
    } else { $perAdapter = '（无分适配器读数）' }
    $selDetail = ("显示名口径 →「" + $gpuDisplayName + "」；温度口径（ThermalService：优先功耗>0 的那块）→ " + $thermalChoice +
        "；占用口径（PDH 最忙引擎，应用只显示一个百分比）→ " + [string]$gpuUsage + "%（分适配器：" + $perAdapter + "）")
    $parts = @()
    foreach ($g in $gpus) {
        $used = if ($g.memoryUsedPick -and $g.memoryUsedPick.sensorName) { [string]$g.memoryUsedPick.sensorName + '=' + [math]::Round([double]$g.memoryUsedPick.value / 1024.0, 2) + ' GB' } else { '未选到' }
        $total = if ($g.memoryTotalPick -and $g.memoryTotalPick.sensorName) { [string]$g.memoryTotalPick.sensorName + '=' + [math]::Round([double]$g.memoryTotalPick.value / 1024.0, 2) + ' GB' } else { '未选到' }
        $parts += ("「" + $g.hardwareName + "」已用 " + $used + " / 总量 " + $total)
    }
    $memDetail = if ($parts.Count) { $parts -join '；' } else { '（LHM 没枚举到任何 GPU 硬件）' }
    $tempPer = @()
    foreach ($g in $gpus) {
        $t = if ($g.temperaturePick -and $g.temperaturePick.sensorName) { [string]$g.temperaturePick.sensorName + '=' + $g.temperaturePick.value } else { '无温度传感器（读不到，不是 0）' }
        $tempPer += ("「" + $g.hardwareName + "」" + $t)
    }
    $splitDetail = ("温度：每块 GPU 各读各的（" + ($tempPer -join '；') + "）⇒ 温度**可以**区分；占用：应用只显示一个百分比（PDH 最忙引擎 max），**不能**按卡拆分")
}

if (-not $f3Applicable) {
    Add-Check 'F3' 'F3-1' '应用实际选了哪块 GPU' $c31 ("本机不满足「核显 + 独显」前提（核显 " + $igpuCount + " 块、独显 " + $dgpuCount + " 块）。顺带事实：" + $selDetail) '不适用' '附录 A'
    Add-Check 'F3' 'F3-2' '显存类型 / 容量读数是否可信' $c32 ("本机不满足「核显 + 独显」前提。顺带事实：" + $memDetail + "（核显上是 **共享内存** 口径，不是独显专用显存）") '不适用' '附录 A'
    Add-Check 'F3' 'F3-3' '两块 GPU 的占用 / 温度读数是否能区分' $c33 ("本机不满足「核显 + 独显」前提。顺带事实：" + $splitDetail) '不适用' '附录 A'
} elseif ($null -eq $lhmJson) {
    Add-Check 'F3' 'F3-1' '应用实际选了哪块 GPU' $c31 $selDetail '未执行' '附录 A'
    Add-Check 'F3' 'F3-2' '显存类型 / 容量读数是否可信' $c32 $memDetail '未执行' '附录 A'
    Add-Check 'F3' 'F3-3' '两块 GPU 的占用 / 温度读数是否能区分' $c33 $splitDetail '未执行' '附录 A'
} else {
    Add-Check 'F3' 'F3-1' '应用实际选了哪块 GPU' $c31 $selDetail '通过' '附录 A'
    Add-Check 'F3' 'F3-2' '显存类型 / 容量读数是否可信' $c32 $memDetail '通过' '附录 A'
    Add-Check 'F3' 'F3-3' '两块 GPU 的占用 / 温度读数是否能区分' $c33 $splitDetail '通过' '附录 A'
}

# ---------------------------------------------------------------- F4 · 其它 2.4G 接收器

$c41 = '每台插着的 HID 设备都必须能归类（L1 标准 HID 电量 / L3 厂商私有协议表 / L4 名称兜底 / 未命中）；判定口径必须是产品同一套。'
$c42 = '每个命中 L4 的设备都必须有 VID/PID + 描述符摘要（usage page:usage + 报告长度）—— 这正是按 docs\peripheral-protocols.md 的模板补一条协议表项需要的输入；命中 L4 ⇒ 明确提示「这台没被认出来，需要补一条协议表项」。'
$c43 = 'hid-probe --selftest 的协议表校验必须 0 错误，且产品路径（PeripheralBatteryService）与工具路径的读数必须一致（有没有读数 / 设备多寡不一致 = 失败；只是数值不同 = 提示）。'

# F4-1 每台 HID 设备的命中层
if ($unknownDevices.Count -gt 0) {
    $devList = ($unknownDevices | ForEach-Object { $_.Layer + ' ' + $_.Vid + ':' + $_.Pid + ' 「' + $_.Name + '」' }) -join '；'
} else {
    $devList = '（hid-probe unknown 没有输出任何设备行）'
}
if ($unknownExit -lt 0) {
    Add-Check 'F4' 'F4-1' '每台 HID 设备的命中层' $c41 $devList '未执行' '附录 C'
} elseif ($unknownDevices.Count -eq 0) {
    Add-Check 'F4' 'F4-1' '每台 HID 设备的命中层' $c41 ($devList + '（hid-probe unknown 退出码 ' + $unknownExit + '）') '失败' '附录 C'
} else {
    Add-Check 'F4' 'F4-1' '每台 HID 设备的命中层' $c41 ($devList + '（共 ' + $unknownDevices.Count + ' 台，hid-probe unknown 退出码 ' + $unknownExit + '）') '通过' '附录 C'
}

# F4-2 L4 设备的补表项输入
$l4Devices = @($unknownDevices | Where-Object { $_.Layer -eq 'L4' })
$l4Detail = ''
foreach ($d in $l4Devices) {
    $cols = @($d.Detail | ForEach-Object { $_.usagePage + ':' + $_.usage + '(in=' + $_.in + ',out=' + $_.out + ',feat=' + $_.feat + ')' })
    $l4Detail += ("VID " + $d.Vid + " / PID " + $d.Pid + "「" + $d.Name + "」集合：" + ($cols -join ' ') + "；")
}
if ($unknownExit -lt 0) {
    Add-Check 'F4' 'F4-2' 'L4（未被认出来）的设备是否给出了补协议表项所需的输入' $c42 '探针未执行' '未执行' '附录 C'
} elseif ($l4Devices.Count -eq 0) {
    Add-Check 'F4' 'F4-2' 'L4（未被认出来）的设备是否给出了补协议表项所需的输入' $c42 '本机没有命中 L4 的设备（插着的联想 Idea5003 2.4G 接收器已命中 L3 协议表项）⇒ 本格**无样本**：「换一款未收录的 2.4G 接收器」这一格仍未验证。' '无样本' '附录 C'
} else {
    Add-Check 'F4' 'F4-2' 'L4（未被认出来）的设备是否给出了补协议表项所需的输入' $c42 ("命中 L4 的设备 " + $l4Devices.Count + " 台 —— **需要按 docs\peripheral-protocols.md 的模板补协议表项**：" + $l4Detail) '通过' '附录 C'
}

# F4-3 协议表自检与产品路径两路复核
$selSummary = ''
if ($selftestText) {
    foreach ($line in ($selftestText -split '\r?\n')) {
        if ($line -match '^\s+设备命中：' -or $line -match '^\s+两路复核' -or $line -match '^\s+协议表：') {
            $selSummary += ($line.Trim() + '；')
        }
    }
}
if ($selftestExit -lt 0) {
    Add-Check 'F4' 'F4-3' '协议表自检与产品路径两路复核' $c43 '探针未执行' '未执行' '附录 B'
} elseif ($selftestExit -eq 0) {
    Add-Check 'F4' 'F4-3' '协议表自检与产品路径两路复核' $c43 ('--selftest 退出码 0 ⇒ ' + $selSummary) '通过' '附录 B'
} else {
    Add-Check 'F4' 'F4-3' '协议表自检与产品路径两路复核' $c43 ('--selftest 退出码 ' + $selftestExit + ' ⇒ ' + $selSummary) '失败' '附录 B'
}

# ---------------------------------------------------------------- 汇总与退出码

$countBy = @{}
foreach ($s in @('通过', '失败', '读不到', '无样本', '需人工介入', '未执行', '不适用')) {
    $countBy[$s] = @($script:checks | Where-Object { $_.Status -eq $s }).Count
}
$blocking = @($script:checks | Where-Object { $_.Status -eq '失败' -or $_.Status -eq '读不到' })

$exitCode = 0
if ($toolchainError) { $exitCode = 2 }
elseif ($blocking.Count -gt 0) { $exitCode = 1 }

# ---------------------------------------------------------------- 出报告

$lines = @()
$lines += "# 阶段 F 验证矩阵报告 —— $($prof.OS.Caption) $($prof.OS.Version)"
$lines += ""
$lines += "- 采集时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$lines += "- 机器名：$($env:COMPUTERNAME)  型号：$($prof.Model.Model)  厂商：$($prof.Model.Manufacturer)"
$lines += "- CPU：$($cpu.Name)（厂商：$vendorId，逻辑核：$($cpu.NumberOfLogicalProcessors)，物理核：$($cpu.NumberOfCores)）"
$lines += "- 显卡：$(if ($gpuNames.Count) { $gpuNames -join ' / ' } else { '（未采集到）' })（核显 $igpuCount 块 / 独显 $dgpuCount 块，判定方式：$gpuDetectSource）"
$lines += "- 电池：$(if ($hasBattery) { '有（本机是移动平台）' } else { '无（台式机）' })（Win32_Battery 实例 $batteryCount 个 / MSBatteryClass 内置电池实例 $($internalBattery.Count) 个）"
$lines += "- 主板：$($prof.Board.Manufacturer) $($prof.Board.Product)"
$lines += "- 内存条：$(@($prof.Mem).Count) 条"
$lines += "- 管理员权限：$(if ($admin) { '有' } else { '无（MSR 温度读不到，属正常：产品此时显示 --）' })"
$lines += "- 工具链：$(if ($SkipProbes) { '已按 -SkipProbes 跳过探针' } elseif ($dotnetExe) { $dotnetExe } else { '（未使用）' })"
$lines += ""
$lines += "> 诚实性说明：**读不到就是读不到**（会写清是「缺权限 / 无该传感器 / 未采集」哪一种），绝不用 0 或默认值充数。"
$lines += "> 「不适用」「无样本」「通过」是三件不同的事，不混着写。"

$lines += ""
$lines += "## 一、轴覆盖总览"
$lines += ""
$lines += "| 轴 | 适用性 | 依据 | 通过 / 失败 / 读不到 / 无样本 / 未执行 / 不适用 |"
$lines += "| --- | --- | --- | --- |"
foreach ($axis in @('F1', 'F2', 'F3', 'F4')) {
    $ac = @($script:checks | Where-Object { $_.Axis -eq $axis })
    $why = switch ($axis) {
        'F1' { $f1Why }
        'F2' { $f2Why }
        'F3' { $f3Why }
        default { '任何机器都能跑协议表自检与命中分层' }
    }
    $applicable = switch ($axis) {
        'F1' { $f1Applicable }
        'F2' { $f2Applicable }
        'F3' { $f3Applicable }
        default { $true }
    }
    $app = if ($applicable) { '**适用**' } else { '不适用' }
    $sum = @()
    foreach ($st in @('通过', '失败', '读不到', '无样本', '未执行', '不适用')) {
        $sum += ("$st " + @($ac | Where-Object { $_.Status -eq $st }).Count)
    }
    $lines += "| $axis | $app | $why | $($sum -join ' · ') |"
}
$lines += ""

foreach ($axisInfo in @(
    @{ Id = 'F1'; Title = 'F1 · AMD 平台'; Applicable = $f1Applicable; Why = $f1Why
       Intro = '判据来自产品真实路径：`src\Island.App\Services\InpOutSmnService.cs`（SMN 直读）、`CpuTemperatureRule.cs` / `HardwareSensorRules.cs`（温度取舍）、`SmnChannel.cs`（开关与生命周期）。' }
    @{ Id = 'F2'; Title = 'F2 · Intel 桌面平台'; Applicable = $f2Applicable; Why = $f2Why
       Intro = '判据来自产品真实路径：`src\Island.App\Services\DeviceService.cs`（Win32_Battery / MSBatteryClass 的内置电池判定）、`ThermalService.cs`（主板 SuperIO 温度）、`src\Island.Native\GpuUsageReader.cs`（GPU 名称选择）。' }
    @{ Id = 'F3'; Title = 'F3 · 核显 + 独显'; Applicable = $f3Applicable; Why = $f3Why
       Intro = '判据来自产品真实路径：`src\Island.Native\GpuUsageReader.cs`（显示名与占用口径）、`src\Island.App\Services\ThermalService.cs`（多 GPU 的温度取舍与显存口径）、`HardwareSensorRules.cs`（显存语义分层）。' }
    @{ Id = 'F4'; Title = 'F4 · 其它 2.4G 接收器'; Applicable = $true; Why = '任何机器都能跑协议表自检与命中分层'
       Intro = '判据来自产品真实路径：`src\Island.App\Services\Peripheral\*`（L1 标准 HID 电量 → L3 协议表 → L4 名称兜底）、`hid-probe --selftest`（表校验 + 两路复核）、`hid-probe unknown`（未识别设备摘要）。' }
)) {
    $ac = @($script:checks | Where-Object { $_.Axis -eq $axisInfo.Id })
    $lines += "## $($axisInfo.Title)"
    $lines += ""
    $app = if ($axisInfo.Applicable) { '**本机适用**' } else { '**本机不适用**' }
    $lines += "- 适用性：$app —— $($axisInfo.Why)"
    $lines += "- 口径：$($axisInfo.Intro)"
    $lines += ""
    $lines += "| # | 检查项 | 判据 | 实测值 | 结论 | 证据 |"
    $lines += "| --- | --- | --- | --- | --- | --- |"
    foreach ($c in $ac) {
        $lines += "| $($c.Id) | $(Fmt $c.Item) | $(Fmt $c.Criterion) | $(Fmt $c.Measured) | **$($c.Status)** | $(Fmt $c.Evidence) |"
    }
    $lines += ""
}

$lines += "## 七、结论"
$lines += ""
$lines += "- 逐项合计：通过 $($countBy['通过']) · 失败 $($countBy['失败']) · 读不到 $($countBy['读不到']) · 无样本 $($countBy['无样本']) · 需人工介入 $($countBy['需人工介入']) · 未执行 $($countBy['未执行']) · 不适用 $($countBy['不适用'])"
if ($toolchainError) {
    $lines += "- **缺工具链**：" + $toolchainError
} elseif ($blocking.Count -gt 0) {
    $lines += "- **有本机适用的检查没通过**：" + (($blocking | ForEach-Object { $_.Id + '（' + $_.Status + '）' }) -join '、')
} else {
    $lines += "- 本机**适用且有样本**的检查全部通过；「不适用」说明这台机器不具备验证条件，「无样本」说明条件具备但本机没有可验证的对象 —— 两者都**不算验证通过**。"
}
$lines += "- 本脚本不修改产品代码，只做采集、检查与报告。"
$lines += ""

$lines += "## 附录 A · lhm-probe matrix（CPU + GPU + 主板传感器快照）"
$lines += ""
if ($null -eq $lhmJson) {
    $lines += "> 没有传感器快照：" + $(if ($toolchainError) { $toolchainError } else { '探针未执行（-SkipProbes）' })
} else {
    $lines += "管理员权限：" + $(if ($lhmJson.admin) { '有' } else { '无（读 MSR 温度多半读不到）' }) + "；LHM Computer 打开：" + $(if ($lhmJson.computerOpened) { '成功' } else { '失败：' + $lhmJson.openError })
    $lines += ""
    $lines += "**CPU 温度传感器**（产品口径：HardwareSensorRules.Pick(CpuTemperature)）"
    $lines += ""
    $lines += "| 传感器 | 读数 |"
    $lines += "| --- | --- |"
    foreach ($s in $cpuAllTemps) {
        $v = if ($null -eq $s.value) { '（空：读不到 —— 缺权限 / 未采集）' } else { [string]$s.value }
        $lines += "| $(Fmt $s.name) | $v |"
    }
    $lines += ""
    $lines += "- 规则选中（CpuTemperatureRule）：" + $(if ($cpuRulePick -and $cpuRulePick.sensorName) { $cpuRulePick.sensorName + ' = ' + $cpuRulePick.value + '（来源 ' + $cpuRulePick.source + '）' } else { '未选到（读数未知，界面显示 --）' })
    $lines += "- 规则选中（HardwareSensorRules，运行期用的那套）：" + $(if ($cpuProductPick -and $cpuProductPick.sensorName) { $cpuProductPick.sensorName + ' = ' + $cpuProductPick.value } else { '未选到（读数未知，界面显示 --）' })
    $lines += "- CCD 条目：" + $(if ($ccdSensors.Count) { ($ccdSensors | ForEach-Object { $_.name + ' = ' + $(if ($null -eq $_.value) { '（空）' } else { $_.value }) }) -join '、' } else { '无（该机未暴露 CCD 传感器 —— 不是 0）' })
    $lines += ""
    $lines += "**GPU 硬件**"
    $lines += ""
    if ($gpus.Count -eq 0) {
        $lines += "- （LHM 没枚举到任何 GPU 硬件）"
    } else {
        foreach ($g in $gpus) {
            $lines += "- 「$($g.hardwareName)」（$($g.hardwareType)）"
            $lines += "  - 温度传感器：" + $(if (@($g.temperatureSensors).Count) { (@($g.temperatureSensors) | ForEach-Object { $_.name + '=' + $(if ($null -eq $_.value) { '(空)' } else { $_.value }) }) -join '、' } else { '（无 —— 该卡没有温度传感器，读不到，不是 0）' })
            $lines += "  - 功耗传感器：" + $(if (@($g.powerSensors).Count) { (@($g.powerSensors) | ForEach-Object { $_.name + '=' + $(if ($null -eq $_.value) { '(空)' } else { $_.value }) }) -join '、' } else { '（无）' })
            $lines += "  - 显存（SmallData）传感器：" + $(if (@($g.smallDataSensors).Count) { (@($g.smallDataSensors) | ForEach-Object { $_.name + '=' + $(if ($null -eq $_.value) { '(空)' } else { $_.value }) }) -join '、' } else { '（无）' })
            $lines += "  - 规则选中：温度=" + $(if ($g.temperaturePick) { $g.temperaturePick.sensorName + '=' + $g.temperaturePick.value } else { '未选到' }) +
                "；功耗=" + $(if ($g.powerPick) { $g.powerPick.sensorName + '=' + $g.powerPick.value } else { '未选到' }) +
                "；显存已用=" + $(if ($g.memoryUsedPick) { $g.memoryUsedPick.sensorName + '=' + $g.memoryUsedPick.value + ' MB' } else { '未选到' }) +
                "；显存总量=" + $(if ($g.memoryTotalPick) { $g.memoryTotalPick.sensorName + '=' + $g.memoryTotalPick.value + ' MB' } else { '未选到' })
        }
    }
    $lines += ""
    $lines += "**主板 / SuperIO**"
    $lines += ""
    $lines += "- 主板硬件：" + $(if ($lhmJson.motherboard) { $lhmJson.motherboard.hardwareName } else { '（LHM 没枚举到主板）' })
    $lines += "- 温度传感器条目数：" + $mbTemperatureEntries
    $lines += "- 规则选中：" + $(if ($mbTempPick -and $mbTempPick.sensorName) { $mbTempPick.sensorName + ' = ' + $mbTempPick.value } else { '未选到（没有条目，或读不到 —— 两者都不是 0）' })
    $lines += ""
    $lines += "**显卡选择（产品口径：Island.Native\CounterSampleCache）**"
    $lines += ""
    $lines += "- WMI 显卡清单：" + $(if ($videoControllers.Count) { $videoControllers -join ' / ' } else { '（未采集到）' })
    $lines += "- 产品选中的名字：$gpuDisplayName"
    $lines += "- 占用（应用显示的那个百分比）：$gpuUsage %"
    $lines += "- 分适配器 3D 占用（按产品同一套实例名分组口径算出来的，应用本身不显示）：" + $(if (@($lhmJson.gpuSelection.perAdapter).Count) { (@($lhmJson.gpuSelection.perAdapter) | ForEach-Object { $_.adapterKey + ' = ' + $_.percent + '%' }) -join '、' } else { '（无）' })
    $lines += "- D3D 适配器（Integrated 标志 = 核显判定）：" + $(if (@($lhmJson.gpuSelection.d3dAdapters).Count) { (@($lhmJson.gpuSelection.d3dAdapters) | ForEach-Object { $_.id + ' ⇒ integrated=' + $(if ($null -eq $_.integrated) { '未知' } else { $_.integrated }) + '，软件适配器=' + $_.isSoftware }) -join '；' } else { '（无）' })
}
$lines += ""

$lines += "## 附录 B · hid-probe --selftest 原始输出"
$lines += ""
$lines += "~~~"
$lines += ($selftestText -replace '\r\n', [string][char]10)
$lines += "~~~"
$lines += ""

$lines += "## 附录 C · hid-probe unknown 原始输出（未识别设备摘要）"
$lines += ""
$lines += "~~~"
$lines += ($unknownText -replace '\r\n', [string][char]10)
$lines += "~~~"
$lines += ""

$lines += "## 附录 D · 本报告用到的产品口径（源码位置）"
$lines += ""
$lines += "| 检查 | 产品里的依据 |"
$lines += "| --- | --- |"
$lines += "| CPU 温度取值 | `src\Island.App\Services\HardwareSensorRules.cs`（运行期用它）· `src\Island.App\Services\CpuTemperatureRule.cs`（分层口径一致） |"
$lines += "| CPU 温度的干扰项排除 | 同上（Distance to TjMax / SoC / VRM / 水泵 / 风扇） |"
$lines += "| SMN 直读 | `src\Island.App\Services\InpOutSmnService.cs`（先判厂商再碰驱动）· `src\Island.App\Services\SmnChannel.cs`（默认关闭） |"
$lines += "| 主板 / SuperIO 温度 | `src\Island.App\Services\ThermalService.cs`（主板子硬件逐个收集） |"
$lines += "| 显存语义分层 | `src\Island.App\Services\HardwareSensorRules.cs`（GpuMemoryUsed / GpuMemoryTotal） |"
$lines += "| GPU 名称与占用 | `src\Island.Native\GpuUsageReader.cs`（GuessGpuName / 最忙引擎口径） |"
$lines += "| 多 GPU 的温度取舍 | `src\Island.App\Services\ThermalService.cs`（优先功耗>0 的那块） |"
$lines += "| 内置电池判定 | `src\Island.App\Services\DeviceService.cs`（Win32_Battery + MSBatteryClass + IsInternalBattery） |"
$lines += "| 外设命中分层 | `src\Island.App\Services\Peripheral\*`（StandardHidBattery / PeripheralProtocols / PeripheralClassifier） |"
$lines += ""

$lines += "## 下一步"
$lines += ""
$lines += "1. 把本报告归档到 docs\verification\（同名文件已存在时脚本会自动加 -2 / -3 后缀，不覆盖旧报告）。"
$lines += "2. 在缺的机型上重复跑一次本脚本：AMD 机（F1）/ Intel 台式机（F2）/ 核显+独显机（F3）/ 换一款未收录的 2.4G 接收器（F4）。"
$lines += "3. F1 的「SMN 直读实际可读性」必须在**用户显式开启开关 + 管理员运行**的条件下由产品自测 —— 脚本不代劳（未签名驱动 + 物理内存映射 = 0x1EA 蓝屏）。"

# ---------------------------------------------------------------- 落盘

$outPath = $OutFile
if (-not $outPath) {
    $slug = ($cpu.Name -replace '[^A-Za-z0-9]+', '-').Trim('-')
    if (-not $slug) { $slug = 'machine' }
    $base = (Get-Date -Format 'yyyy-MM-dd') + '-' + $slug
    $outPath = Join-Path (Join-Path $repo 'docs\verification') ($base + '.md')
    $n = 2
    while (Test-Path $outPath) {
        $outPath = Join-Path (Join-Path $repo 'docs\verification') ($base + '-' + $n + '.md')
        $n++
    }
} elseif (-not [System.IO.Path]::IsPathRooted($outPath)) {
    $outPath = Join-Path $repo $outPath
}
$outDir = Split-Path -Parent $outPath
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
[System.IO.File]::WriteAllLines($outPath, $lines, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ("报告已写入：" + $outPath)
Write-Host "逐轴结论："
foreach ($axis in @('F1', 'F2', 'F3', 'F4')) {
    $ac = @($script:checks | Where-Object { $_.Axis -eq $axis })
    $parts = @()
    foreach ($st in @('通过', '失败', '读不到', '无样本', '未执行', '不适用')) {
        $n = @($ac | Where-Object { $_.Status -eq $st }).Count
        if ($n -gt 0) { $parts += "$st $n" }
    }
    Write-Host ("  - " + $axis + "：" + ($parts -join ' · '))
}
Write-Host ("合计：通过 " + $countBy['通过'] + " · 失败 " + $countBy['失败'] + " · 读不到 " + $countBy['读不到'] + " · 无样本 " + $countBy['无样本'] + " · 需人工介入 " + $countBy['需人工介入'] + " · 未执行 " + $countBy['未执行'] + " · 不适用 " + $countBy['不适用'])
if ($toolchainError) {
    Write-Host ("缺工具链：" + $toolchainError)
    Write-Host "退出码 2"
    exit 2
}
if ($blocking.Count -gt 0) {
    Write-Host ("未通过项：" + (($blocking | ForEach-Object { $_.Id + '（' + $_.Status + '）' }) -join '、'))
    Write-Host "退出码 1"
    exit 1
}
Write-Host "退出码 0"
exit 0
