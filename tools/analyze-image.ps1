param(
    # 待分析的截图路径（必填）
    [Parameter(Mandatory = $true, HelpMessage = '要分析的 PNG/JPG 路径')]
    [string]$ImagePath,
    # 亮色阈值（0-255），用于定位卡片/高亮区域
    [int]$Threshold = 150
)
$ErrorActionPreference = 'Stop'
[void][System.Reflection.Assembly]::LoadFrom("$Env:windir\Microsoft.NET\Framework64\v4.0.30319\System.Drawing.dll")
if (-not (Test-Path $ImagePath)) { throw "图片不存在：$ImagePath" }
$img = [System.Drawing.Image]::FromFile($ImagePath)
$bmp = New-Object System.Drawing.Bitmap($img)
# 找亮色区域（超过阈值的像素）的包围盒，按行扫描密度
$thresh = $Threshold
$minX = $img.Width; $maxX = 0; $minY = $img.Height; $maxY = 0
for ($y = 0; $y -lt $img.Height; $y += 4) {
    for ($x = 0; $x -lt $img.Width; $x += 4) {
        $c = $bmp.GetPixel($x, $y)
        $v = ($c.R + $c.G + $c.B) / 3
        if ($v -gt $thresh) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}
"亮色区域包围盒: X $minX..$maxX  Y $minY..$maxY"
# 在包围盒内采样每列亮色像素高度，找卡片分栏
$cols = @()
for ($x = $minX; $x -le $maxX; $x += [Math]::Max(8, [int](($maxX - $minX) / 40))) {
    $cnt = 0
    for ($y = $minY; $y -le $maxY; $y += 4) {
        $c = $bmp.GetPixel($x, $y)
        if (($c.R + $c.G + $c.B) / 3 -gt $thresh) { $cnt++ }
    }
    $cols += "${x}:${cnt}"
}
$cols -join " "
$img.Dispose()