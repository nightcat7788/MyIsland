$ErrorActionPreference = 'Stop'
[void][System.Reflection.Assembly]::LoadFrom("$Env:windir\Microsoft.NET\Framework64\v4.0.30319\System.Drawing.dll")
$img = [System.Drawing.Image]::FromFile("C:\Users\fengl\.workbuddy\clipboard-images\clipboard-2026-11-21T17-29-12-707Z-0a0e1b16.png")
$bmp = New-Object System.Drawing.Bitmap($img)
# 找亮色区域（V>150 的像素）的包围盒，按行扫描密度
$thresh = 150
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
