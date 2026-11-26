# Windows 端功能自检 —— 一条命令跑完，出一份报告。
#
# 用法（仓库根目录）：
#   powershell -ExecutionPolicy Bypass -File tools\win-selftest.ps1
#   powershell -ExecutionPolicy Bypass -File tools\win-selftest.ps1 -Mutate   # 额外做写操作往返（立即还原）
#
# 它做什么：把 Windows 独有的那批能力（亮度 / WLAN / 蓝牙 / 静音 / 锁屏 / 睡眠 / 系统主题 / 夜灯 /
# 电源计划 / 清理内存 / 网络适配器 / 剪贴板历史 / 开机自启）逐项跑一遍，报出**这台机器上的真实状态**，
# 产物写到 docs\verification\windows-selftest-<日期>.md。
#
# 调的是产品自己的服务（同一份源文件链接进工具），测的就是产品真会跑的那条路。
#
# 安全约定：默认只读；锁屏 / 睡眠 / WLAN 开关 / 蓝牙开关永不自动执行（报告里标「需人工」）。

param(
    [switch]$Mutate,
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

$dotnetExe = Resolve-Dotnet
if (-not $dotnetExe) {
    Write-Error "找不到可用的 dotnet SDK（系统 dotnet 没有 SDK）。用 -Dotnet 指定全路径。"
    exit 2
}

$proj = Join-Path $PSScriptRoot 'win-selftest\win-selftest.csproj'
$args = @('run', '--project', $proj, '-c', 'Release')
if ($Mutate) { $args += '--' ; $args += '--mutate' }

Write-Host "自检：$dotnetExe"
& $dotnetExe @args
exit $LASTEXITCODE
