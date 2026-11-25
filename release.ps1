param(
    [string]$Version = "",
    [string]$CertPfx = "",
    [string]$PasswordEnv = "MYISLAND_SIGN_PWD",
    [string]$CertThumbprint = "",
    [string]$TsaUrl = "http://timestamp.digicert.com",
    [string]$OutDir = "",
    [string]$Stage = "",
    [string]$Dotnet = "",
    [string]$Iscc = "",
    [switch]$SkipPublish,
    [switch]$SkipTests,
    [switch]$SkipZip,
    [switch]$SkipInstaller,
    [switch]$RequireInstaller,
    [switch]$StopRunning
)

# MyIsland 发布流水线：publish -> 签主 exe -> ISCC 编译并签名安装包 -> 便携 zip -> 校验清单 -> 门禁
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File release.ps1
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.0.2
#   powershell -ExecutionPolicy Bypass -File release.ps1 -CertPfx D:\keys\mine.pfx   # 密码取自 MYISLAND_SIGN_PWD
#   powershell -ExecutionPolicy Bypass -File release.ps1 -CertThumbprint <HEX>
#   powershell -ExecutionPolicy Bypass -File release.ps1 -SkipInstaller             # 没装 Inno Setup：只出便携包
#   powershell -ExecutionPolicy Bypass -File release.ps1 -RequireInstaller          # 必须有安装包；缺 ISCC 直接失败
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Dotnet D:\sdk\dotnet.exe # 指定带 SDK 的 dotnet
#
# 没装 Inno Setup 7 时脚本不会在第 0 步就退出：自动降级为「只出已签名的便携 zip + 校验清单」，
# 并在日志和最终输出里明确写出本次没有安装包；用 -RequireInstaller 可以要求缺 ISCC 时直接失败。
#
# 证书来源与 sign.ps1 一致：PFX / 证书库 thumbprint / 默认自签 CN=MyIsland。
# 换公有 CA 证书时只改这两个参数，流程不动。
#
# 默认每次发布到独立暂存目录；SkipPublish 仍复用 publish\release 或显式 Stage。
# 不动开发用的 publish\win-x64：
# 正在运行的实例锁着它，signtool 签不了被占用的文件（报 "file is being used by another process"），
# 而提权实例非提权脚本根本杀不掉。暂存目录彻底绕开这个耦合，也就不需要 UAC。

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$repo = $root
$issFile = Join-Path $root 'installer\MyIsland.iss'
$signPs1 = Join-Path $root 'sign.ps1'
$tpFile = Join-Path $env:TEMP 'island-sign-thumbprint.txt'
if (-not $OutDir) { $OutDir = Join-Path $repo 'dist' }
if (-not $Stage) {
    if ($SkipPublish) { $Stage = Join-Path $root 'publish\release' }
    else { $Stage = Join-Path $root ("publish\release-" + [guid]::NewGuid().ToString('N')) }
}

function Assert-SafeStage([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "暂存路径不是目录：$Path" }
    $entries = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction Stop)
    $bad = @($entries | Where-Object {
        ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -or
        (-not $_.PSIsContainer -and ($_.Name -like 'inpout*.dll' -or
            $_.Extension -in '.pfx', '.p12', '.pdb', '.key', '.pem', '.snk', '.pvk'))
    })
    if ($bad.Count -gt 0) {
        throw ("暂存目录含禁止发布的文件或链接；已中止，未删除任何文件：" + ($bad.FullName -join '; '))
    }
}

# 复用目录必须在 publish、签名、安装包及 zip 读取前通过检查。
Assert-SafeStage $Stage

function Step($n, $m) { Write-Host ""; Write-Host "=== [$n] $m ===" -ForegroundColor Cyan }

# ---- 0. 工具定位 ----
Step '0/7' '定位工具链'

# dotnet 发现：可配置 + 必须真的有 SDK。
# 只看 exe 存在不够 —— 很多机器的 C:\Program Files\dotnet 只装了运行时没有 SDK，
# 而 dotnet --list-sdks 对这种机器返回空输出（这正是判据，不能改成"文件在不在"）。
# 顺序：显式 -Dotnet → $env:DOTNET_ROOT → PATH 上的 dotnet → 仓库及上级的 .dotnet 目录 → 原有两个候选。
function Resolve-DotnetSdk {
    $tried = New-Object System.Collections.ArrayList
    $candidates = New-Object System.Collections.ArrayList
    if ($Dotnet) {
        [void]$candidates.Add($Dotnet)
    } else {
        if ($env:DOTNET_ROOT) { [void]$candidates.Add((Join-Path $env:DOTNET_ROOT 'dotnet.exe')) }
        $cmd = Get-Command 'dotnet' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cmd) { [void]$candidates.Add($cmd.Source) }
        # 仓库根及上级的 .dotnet 目录：SDK 常放在仓库外的同级/上级目录里
        $d = $root
        for ($i = 0; $i -lt 6; $i++) {
            [void]$candidates.Add((Join-Path $d '.dotnet\dotnet.exe'))
            $parent = Split-Path -Parent $d
            if (-not $parent -or $parent -eq $d) { break }
            $d = $parent
        }
        [void]$candidates.Add('C:\Program Files\dotnet\dotnet.exe')
        [void]$candidates.Add((Join-Path $env:USERPROFILE 'dotnet-sdk\dotnet.exe'))
    }
    foreach ($c in $candidates) {
        if (-not $c) { continue }
        if ($tried -contains $c) { continue }
        if (-not (Test-Path -LiteralPath $c -PathType Leaf)) {
            [void]$tried.Add("$c （文件不存在）"); continue
        }
        $sdks = @()
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $sdks = @(& $c --list-sdks 2>&1 | Where-Object { $_ -is [string] -and $_.Trim() -ne '' })
        } catch {
            $sdks = @()
        } finally {
            $ErrorActionPreference = $prev
        }
        if ($sdks.Count -eq 0) {
            [void]$tried.Add("$c （存在，但没有安装 SDK）"); continue
        }
        return [pscustomobject]@{ Path = $c; Sdks = $sdks }
    }
    throw ("找不到带 SDK 的 dotnet。已尝试：" + ($tried -join ' | ') + "。可用 -Dotnet <dotnet.exe 路径> 显式指定。")
}
$dotnetInfo = Resolve-DotnetSdk
$dotnetExe = $dotnetInfo.Path

# ISCC（Inno Setup 7）是**可选**的：没装也能出已签名的便携 zip + 校验清单。
$isccExe = $Iscc
if (-not $isccExe) {
    $isccExe = "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe"
    if (-not (Test-Path $isccExe)) {
        foreach ($c in 'C:\Program Files (x86)\Inno Setup 7\ISCC.exe', 'C:\Program Files\Inno Setup 7\ISCC.exe') {
            if (Test-Path $c) { $isccExe = $c; break }
        }
    }
}
$isccFound = Test-Path -LiteralPath $isccExe -PathType Leaf
if ($SkipInstaller -and $RequireInstaller) {
    throw "-SkipInstaller 与 -RequireInstaller 不能同时使用（一个要求跳过安装包，一个要求必须有安装包）"
}
$buildInstaller = $true
if ($RequireInstaller -and -not $isccFound) {
    throw ("找不到 ISCC.exe（Inno Setup 7），而你要求本次必须产出安装包（-RequireInstaller）。" +
           "已尝试：$isccExe。请安装 Inno Setup 7，或去掉 -RequireInstaller 改为只出便携包（-SkipInstaller）。")
}
if ($SkipInstaller) {
    $buildInstaller = $false
    Write-Host "按 -SkipInstaller 跳过安装包编译" -ForegroundColor Yellow
} elseif (-not $isccFound) {
    $buildInstaller = $false
    Write-Host ""
    Write-Host "警告：找不到 ISCC.exe（Inno Setup 7）—— 本次发布不会产出安装包。" -ForegroundColor Yellow
    Write-Host "      只会产出已签名的便携 zip + SHA256SUMS.txt。装好 Inno Setup 7 后重跑即可出安装包。" -ForegroundColor Yellow
}
$pf86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
$signTool = (Get-ChildItem (Join-Path $pf86 'Windows Kits\10\bin') -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
             Where-Object { $_.DirectoryName -match 'x64$' } | Sort-Object FullName -Descending | Select-Object -First 1).FullName
if (-not $signTool) { throw "找不到 signtool.exe（请先装 Windows SDK）" }
$isccDisplay = if ($isccFound) { $isccExe } else { '(未找到 —— 本次不产出安装包)' }
Write-Host "dotnet  : $dotnetExe  (SDK: $($dotnetInfo.Sdks -join ', '))"
Write-Host "ISCC    : $isccDisplay"
Write-Host "signtool: $signTool"
Write-Host "暂存目录: $Stage"
Write-Host "输出    : $OutDir"

# ---- 1. 版本号 ----
Step '1/7' '确定版本号'
if (-not $Version) {
    # 版本号唯一来源：仓库根的 Directory.Build.props（全解决方案共用属性，MSBuild 自动套用）。
    # 旧布局里版本散在各个 csproj / 安装包脚本里，改一处漏一处；现在只有 props 一处。
    $props = Join-Path $root 'Directory.Build.props'
    if (Test-Path $props) {
        $m = Select-String -Path $props -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
        if ($m) { $Version = $m.Matches[0].Groups[1].Value }
    }
}
if (-not $Version) {
    $csproj = Join-Path $root 'src\Island.App\Island.App.csproj'
    if (Test-Path $csproj) {
        $m = Select-String -Path $csproj -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
        if ($m) { $Version = $m.Matches[0].Groups[1].Value }
    }
}
if (-not $Version -and (Test-Path $issFile)) {
    # csproj 里没写 <Version> 时，以安装包脚本里声明的版本为准
    $m = Select-String -Path $issFile -Pattern '^#define AppVersion\s+"([^"]+)"' | Select-Object -First 1
    if ($m) { $Version = $m.Matches[0].Groups[1].Value }
}
if (-not $Version) { $Version = '0.0.0' }
$Version = ($Version -split '[+\-]')[0]
Write-Host "版本    : $Version"

# ---- 2. 可选：停掉运行中的实例（默认不动它）----
if ($StopRunning) {
    Step '2/7' '停止运行中的实例'
    $running = Get-Process -Name 'Island.App' -ErrorAction SilentlyContinue
    if ($running) {
        $ids = @($running.Id)
        foreach ($proc in $running) { try { $proc | Stop-Process -Force -ErrorAction Stop } catch { } }
        $dl = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $dl) {
            if (-not (Get-Process -Name 'Island.App' -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 300
        }
        $still = Get-Process -Name 'Island.App' -ErrorAction SilentlyContinue
        if ($still) {
            throw ("有实例(pid " + ($still.Id -join ',') + ")停不掉 —— 多半是提权运行的。请用管理员身份运行本脚本，或先从托盘退出 MyIsland。")
        }
        Write-Host "已停止：$($ids -join ',')"
    } else { Write-Host "没有运行中的实例" }
}

# ---- 3. 测试（门禁）----
if (-not $SkipTests) {
    Step '3/7' '运行测试'
    & $dotnetExe run --project (Join-Path $root 'tests\Island.Tests\Island.Tests.csproj') -c Release 2>&1 |
        Select-Object -Last 3 | ForEach-Object { Write-Host "  $_" }
    if ($LASTEXITCODE -ne 0) { throw "测试失败（exit $LASTEXITCODE），发布中止" }
}

# ---- 4. publish 到暂存目录 ----
if (-not $SkipPublish) {
    Step '4/7' 'publish 主程序（到暂存目录）'
    New-Item -ItemType Directory -Path $Stage -Force | Out-Null
    & $dotnetExe publish (Join-Path $root 'src\Island.App\Island.App.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $Stage 2>&1 |
        Select-String -Pattern 'error|-> ' | Select-Object -Last 3 | ForEach-Object { Write-Host "  $($_.Line)" }
    if ($LASTEXITCODE -ne 0) { throw "publish 失败" }
    # 非 zh/en 的卫星资源由 installer\MyIsland.iss 的 Excludes 排除
    Write-Host "暂存目录：$Stage"
}

Assert-SafeStage $Stage

# ---- 5. 签主 exe ----
Step '5/7' '签名主程序'
$mainExe = Join-Path $Stage 'Island.App.exe'
if (-not (Test-Path $mainExe)) { throw "暂存目录里没有 Island.App.exe：$Stage（先跑一次 publish，或去掉 -SkipPublish）" }
if (Test-Path $tpFile) { Remove-Item $tpFile -Force -ErrorAction SilentlyContinue }
# ⚠️ 用 -File 传参时空字符串会被丢弃，可选参数必须"非空才传"
$signArgs = @('-ExecutionPolicy', 'Bypass', '-File', $signPs1,
              '-Target', $mainExe, '-TsaUrl', $TsaUrl, '-ThumbprintOut', $tpFile)
if ($CertPfx) { $signArgs += @('-CertPfx', $CertPfx) }
if ($PasswordEnv) { $signArgs += @('-PasswordEnv', $PasswordEnv) }
if ($CertThumbprint) { $signArgs += @('-CertThumbprint', $CertThumbprint) }
& powershell.exe @signArgs
if ($LASTEXITCODE -ne 0) { throw "签名主程序失败（exit $LASTEXITCODE）" }
if (-not (Test-Path $tpFile)) { throw "签名脚本没有回写证书指纹" }
$thumb = [System.IO.File]::ReadAllText($tpFile).Trim()
Write-Host "证书指纹：$thumb"

# ---- 6. ISCC：编译安装包（编译期由 .iss 的 SignTool= 签名）----
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$setup = $null
if (-not $buildInstaller) {
    # 降级路径：明说"这次没有安装包"，不让人以为出了却找不到
    Step '6/7' '编译并签名安装包（本次跳过）'
    Write-Host "本次跳过安装包编译 —— 不会生成 MyIsland-Setup-$Version.exe" -ForegroundColor Yellow
} else {
    Step '6/7' '编译并签名安装包'
    # Inno 7 的 SignTool= 只认"命名工具"：名字必须在 ISCC 命令行用 --signtool=名字=命令 定义。
    # 工具路径用 8.3 短路径（没有空格，避免 Inno 解析命令行的坑）；短路径不可用时退回加引号的长路径。
    $short = & {
        $sb = New-Object System.Text.StringBuilder 260
        if (-not ('SP3' -as [type])) {
            Add-Type -TypeDefinition 'using System;using System.Text;using System.Runtime.InteropServices;public class SP3{[DllImport("kernel32.dll",CharSet=CharSet.Auto)]public static extern int GetShortPathName(string l,StringBuilder s,int c);}' -ErrorAction SilentlyContinue
        }
        if ([SP3]::GetShortPathName($signTool, $sb, 260) -gt 0) { $sb.ToString() } else { $signTool }
    }
    if ($short -match '\s') { $short = '"' + $short + '"' }
    # 单引号拼接：$f 必须原样传给 Inno（它是 Inno 的文件占位符，不是 PowerShell 变量）
    $signDef = '--signtool=myisland=' + $short + ' sign /sha1 ' + $thumb + ' /fd sha256 /tr ' + $TsaUrl + ' /td sha256 $f'
    $isccArgs = @($signDef, "/DMyIslandSignTool=myisland", "/DAppVersion=$Version",
                  "/DOutDir=$OutDir", "/DSrcDir=$Stage", $issFile)
    Assert-SafeStage $Stage
    & $isccExe $isccArgs
    if ($LASTEXITCODE -ne 0) { throw "ISCC 失败（exit $LASTEXITCODE）" }
    $setup = Join-Path $OutDir "MyIsland-Setup-$Version.exe"
    if (-not (Test-Path $setup)) { throw "安装包没生成：$setup" }
    Write-Host "安装包：$setup"
}

# ---- 7. 便携 zip ----
$zip = Join-Path $OutDir "MyIsland-$Version-portable.zip"
if (-not $SkipZip) {
    Step '7/7' '打包便携版'
    Assert-SafeStage $Stage
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # 先写临时文件再覆盖：ZipFile.CreateFromDirectory 不允许目标已存在
    $zipTmp = Join-Path $OutDir "portable-tmp.zip"
    if (Test-Path $zipTmp) { Move-Item $zipTmp (Join-Path $env:TEMP "stale-$([guid]::NewGuid().ToString('N')).zip") -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($Stage, $zipTmp, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Move-Item $zipTmp $zip -Force
    Write-Host "便携包：$zip"
}

# ---- 8. 校验清单 + 门禁 ----
Step '8/8' '生成校验清单并自检'
function SigLine([string]$p) {
    $ext = [System.IO.Path]::GetExtension($p).ToLowerInvariant()
    # ZIP 格式本身不支持 Authenticode —— 不能拿"没签名"当不合格
    $signable = ($ext -eq '.exe' -or $ext -eq '.dll')
    $status = '(不适用)'
    $signer = ''
    $stamp = 'n/a'
    if ($signable) {
        $s = Get-AuthenticodeSignature $p
        $status = $s.Status
        $signer = if ($s.SignerCertificate) { $s.SignerCertificate.Subject } else { '' }
        $stamp = 'no'
        try {
            $o = & $signTool verify /pa /v $p 2>&1
            if ($o | Select-String -Pattern 'timestamped' -Quiet) { $stamp = 'yes' }
        } catch { }
    }
    return [pscustomobject]@{
        File     = Split-Path $p -Leaf
        Size     = [math]::Round((Get-Item $p).Length / 1MB, 1)
        SHA256   = (Get-FileHash $p -Algorithm SHA256).Hash
        Signable = $signable
        Status   = $status
        Signer   = $signer
        Stamp    = $stamp
    }
}
# 清单只列**实际存在**的产物：降级（没装 Inno Setup）时不含安装包，
# 也不得因为缺安装包而报错 —— 门禁只对列出来的产物判定。
$artifacts = @()
if ($setup) { $artifacts += $setup }
if (-not $SkipZip) { $artifacts += $zip }
$artifacts += $mainExe
$report = $artifacts | ForEach-Object { SigLine $_ }
$report | Format-Table -AutoSize | Out-String -Width 220 | Write-Host

$lines = @('# MyIsland 发布校验清单', '', "版本：$Version", "生成时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')", '')
if (-not $buildInstaller) {
    $lines += "# 注意：本次发布没有安装包（MyIsland-Setup-$Version.exe 未生成）；下面只有便携 zip 与主 exe"
    $lines += ''
}
foreach ($r in $report) {
    $lines += "$($r.SHA256)  $($r.File)  [签名=$($r.Status) 签名者=$($r.Signer) 时间戳=$($r.Stamp)]"
}
$lines += ''
$lines += '# 便携 zip 本身无法被签名（ZIP 格式不支持），以包内 Island.App.exe 的签名与上面的 SHA256 为准'
[System.IO.File]::WriteAllLines((Join-Path $OutDir 'SHA256SUMS.txt'), $lines, (New-Object System.Text.UTF8Encoding($true)))
Write-Host "清单：$(Join-Path $OutDir 'SHA256SUMS.txt')"

# 门禁只校验"能签名"的产物：exe/dll 必须 Valid + 带 RFC3161 时间戳；
# zip 天然不可签名，只报哈希，不算不合格
$bad = $report | Where-Object { $_.Signable -and ($_.Status -ne 'Valid' -or $_.Stamp -ne 'yes') }
if ($bad) {
    Write-Host ""
    Write-Host "门禁未通过：以下产物签名缺失/无效/无时间戳" -ForegroundColor Red
    $bad | ForEach-Object { Write-Host "  $($_.File)  Status=$($_.Status) 时间戳=$($_.Stamp)" -ForegroundColor Red }
    exit 1
}
Write-Host ""
Write-Host "门禁通过：全部产物均已签名且带 RFC3161 时间戳" -ForegroundColor Green
if ($buildInstaller) {
    Write-Host "产物：安装包 + 便携 zip + 校验清单（$OutDir）"
} else {
    Write-Host "注意：本次发布**没有安装包** —— MyIsland-Setup-$Version.exe 未生成（未找到 ISCC.exe 或指定了 -SkipInstaller）" -ForegroundColor Yellow
    Write-Host "产物：便携 zip + 校验清单（$OutDir）"
}
