# 阶段 F 验证矩阵执行器：把「多机型验证」从不可执行的复选框变成一条命令。
#
# 用法（仓库根执行）：
#   powershell -ExecutionPolicy Bypass -File tools\verify-matrix.ps1
#   powershell -ExecutionPolicy Bypass -File tools\verify-matrix.ps1 -OutFile docs\verification\2026-11-26-intel-laptop.md
#
# 它做什么：
#   1. 采集本机画像（CPU 厂商/型号/核数、显卡清单、有无电池、主板、内存条、操作系统版本）。
#   2. 跑现有侦察探针（lhm-probe 看温度传感器、hid-probe e2e 看 2.4G 接收器），把原始输出附在报告里。
#   3. 按画像判断这台机器**覆盖了验证矩阵的哪几格**（F1 AMD / F2 Intel 桌面 / F3 核显+独显 / F4 其它 2.4G 接收器），
#      并把该格的结论与证据写进报告；没覆盖到的格子明确写「未覆盖」——不假装验证过。
#   4. 输出 Markdown 报告（默认落到 docs\verification\），并打印落盘路径。
#
# ⚠️ 关于权限：读 MSR 温度需要 PawnIO 驱动 + 管理员权限。没有权限时报告里会写「读不到」，
#    这属于事实陈述，不是失败；产品的对应表现是显示「需管理员/缺驱动」。

param(
    [string]$OutFile = "",
    [switch]$SkipProbes,
    [string]$Dotnet = ""
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

function Resolve-Dotnet {
    if ($Dotnet) { return $Dotnet }
    foreach ($c in @('D:\YQDESK\.dotnet\dotnet.exe', "$env:USERPROFILE\.dotnet\dotnet.exe",
                     'C:\Program Files\dotnet\dotnet.exe')) {
        if (Test-Path $c) {
            $sdks = & $c --list-sdks 2>$null
            if ($sdks) { return $c }
        }
    }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return ""
}

function Get-Profile {
    $p = [ordered]@{}
    $p.OS = (Get-CimInstance Win32_OperatingSystem)
    $p.CPU = @(Get-CimInstance Win32_Processor)
    $p.GPU = @(Get-CimInstance Win32_VideoController)
    $p.Battery = @(Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue)
    $p.Board = Get-CimInstance Win32_BaseBoard -ErrorAction SilentlyContinue
    $p.Mem = @(Get-CimInstance Win32_PhysicalMemory -ErrorAction SilentlyContinue)
    $p.Model = (Get-CimInstance Win32_ComputerSystem)
    return $p
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

$prof = Get-Profile
$cpu = @($prof.CPU)[0]
$gpuNames = @($prof.GPU | ForEach-Object { $_.Name } | Where-Object { $_ })
$hasBattery = @($prof.Battery).Count -gt 0
$isAmd = ($cpu.Name -match 'AMD|Ryzen')
$isIntel = ($cpu.Name -match 'Intel|Core|Ultra')
$hasIgpu = @($gpuNames | Where-Object { $_ -match 'Intel.*(Graphics|UHD|Iris)|AMD.*(Radeon Graphics|Vega)' }).Count -gt 0
$hasDgpu = @($gpuNames | Where-Object { $_ -match 'NVIDIA|Radeon RX|Radeon Pro|GeForce' }).Count -gt 0

$lines = @()
$lines += "# 验证矩阵报告 —— $($prof.OS.Caption) $($prof.OS.Version)"
$lines += ""
$lines += "- 采集时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$lines += "- 机器名：$($env:COMPUTERNAME)  型号：$($prof.Model.Model)  厂商：$($prof.Model.Manufacturer)"
$lines += "- CPU：$($cpu.Name)（厂商：$($cpu.Manufacturer)，逻辑核：$($cpu.NumberOfLogicalProcessors)，物理核：$($cpu.NumberOfCores)）"
$lines += "- 显卡：$(if ($gpuNames.Count) { $gpuNames -join ' / ' } else { '（未采集到）' })"
$lines += "- 电池：$(if ($hasBattery) { '有（本机是移动平台）' } else { '无（台式机）' })"
$lines += "- 主板：$($prof.Board.Manufacturer) $($prof.Board.Product)"
$lines += "- 内存条：$(@($prof.Mem).Count) 条"
$lines += "- 管理员权限：$(if ($admin) { '有' } else { '无（MSR 温度多半读不到，属正常）' })"
$lines += ""
$lines += "## 本机覆盖了矩阵的哪几格"
$lines += ""

$covered = @()
if ($isAmd) {
    $covered += 'F1（AMD 平台：SMN 直读通道、CCD 温度、Tctl/Tdie 取舍）—— **部分覆盖**：AMD 型号可识别；温度通道是否可读取决于本机驱动与权限，见下方探针输出'
} else {
    $covered += 'F1（AMD 平台）—— **未覆盖**：本机是 ' + $cpu.Name
}
if ($isIntel -and -not $hasBattery) {
    $covered += 'F2（Intel 桌面平台：无核显、无电池、主板 SuperIO 温度）—— **可覆盖**：本机无电池'
} else {
    $covered += 'F2（Intel 桌面平台）—— **未覆盖**：本机有电池（移动平台）；桌面平台的 SuperIO 温度路径需在台式机上另跑一次'
}
if ($hasIgpu -and $hasDgpu) {
    $covered += 'F3（核显 + 独显组合：GPU 选择口径、显存类型）—— **可覆盖**：检测到 ' + ($gpuNames -join ' / ')
} else {
    $covered += 'F3（核显 + 独显组合）—— **未覆盖**：本机显卡为 ' + ($gpuNames -join ' / ')
}
$lines += $covered
$lines += ""
$lines += "## 探针原始输出"
$lines += ""


if (-not $SkipProbes) {
    $dotnetExe = Resolve-Dotnet
    if (-not $dotnetExe) {
        $lines += '> ⚠️ 找不到可用的 dotnet SDK，跳过探针执行。'
    } else {
        $lines += "使用 dotnet：``$dotnetExe``"
        $lines += ""
        $lhmOut = Join-Path $env:TEMP ('lhm-' + [guid]::NewGuid().ToString('N') + '.txt')
        try {
            & $dotnetExe run --project (Join-Path $repo 'tools\lhm-probe\lhm-probe.csproj') -c Release -- $lhmOut | Out-Null
            if (Test-Path $lhmOut) {
                $lines += '### lhm-probe（CPU 温度传感器）'
                $lines += ''
                $lines += '```'
                $lines += (Get-Content $lhmOut -Encoding UTF8 -Raw)
                $lines += '```'
            }
        } catch {
            $lines += ('> lhm-probe 执行失败：' + $_.Exception.Message)
        } finally {
            if (Test-Path $lhmOut) { Remove-Item $lhmOut -Force -ErrorAction SilentlyContinue }
        }
        $lines += ''
        $lines += '### hid-probe e2e（2.4G 接收器）'
        $lines += ''
        $lines += '```'
        try {
            $e2e = & $dotnetExe run --project (Join-Path $repo 'tools\hid-probe\hid-probe.csproj') -c Release -- e2e 2>&1
            $lines += ($e2e | Out-String)
        } catch {
            $lines += ('> hid-probe 执行失败：' + $_.Exception.Message)
        }
        $lines += '```'
    }
} else {
    $lines += '> 已按 -SkipProbes 跳过探针执行。'
}

$lines += '## 结论（诚实版）'
$lines += ''
$lines += '- 上面「覆盖了哪几格」里标 **可覆盖** 的格子，表示本机**具备验证条件**，不代表已经验证通过 ——'
$lines += '  具体结论要读探针原始输出：输出里读不到的项就是**读不到**，产品应当显示「未知/需管理员/缺驱动」。'
$lines += '- 标 **未覆盖** 的格子，需要在对应类型的另一台机器上再跑一次本脚本。'
$lines += '- 本脚本不修改产品代码，只做采集与报告。'
$lines += ''
$lines += '## 下一步（按优先级）'
$lines += ''
$lines += '1. 把本报告连同探针输出一起归档（docs\verification\ 下按日期命名）。'
$lines += '2. 在缺的机型上重复本步骤：AMD 机 / Intel 台式机 / 核显+独显机 / 换一款 2.4G 接收器。'
$outPath = $OutFile
if (-not $outPath) {
    $slug = ($cpu.Name -replace '[^A-Za-z0-9]+', '-').Trim('-')
    if (-not $slug) { $slug = 'machine' }
    $outPath = Join-Path (Join-Path $repo 'docs\verification') ((Get-Date -Format 'yyyy-MM-dd') + '-' + $slug + '.md')
}
$outDir = Split-Path -Parent $outPath
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
[System.IO.File]::WriteAllLines($outPath, $lines, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("报告已写入：" + $outPath)
Write-Host "覆盖情况："
$covered | ForEach-Object { Write-Host ("  - " + $_) }

$lines += '3. F4（其它 2.4G 接收器）：换接收器后跑 `hid-probe -- e2e`，'
$lines += '   若命中 L4 兜底（未识别），按 docs\peripheral-protocols.md 的模板补一条协议表项并复测。'
