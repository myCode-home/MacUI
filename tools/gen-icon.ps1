Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public class DpiIcon { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }'
[DpiIcon]::SetProcessDPIAware() | Out-Null

$outDir = (Join-Path $PSScriptRoot '..\src\MacUI\icon')
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function New-IconPng([int]$size, [string]$path) {
    # 4x 超采样绘制,再高质量缩到目标尺寸
    $ss = 4
    $S = $size * $ss
    $bmp = New-Object System.Drawing.Bitmap($S, $S)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # ---- macOS 风格 squircle 底:蓝→靛紫渐变 ----
    $pad = [int]($S * 0.045)
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($S - 2*$pad), ($S - 2*$pad))
    $radius = $S * 0.225
    $path2 = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r2 = $radius * 2
    $path2.AddArc($rect.X, $rect.Y, $r2, $r2, 180, 90)
    $path2.AddArc($rect.Right - $r2, $rect.Y, $r2, $r2, 270, 90)
    $path2.AddArc($rect.Right - $r2, $rect.Bottom - $r2, $r2, $r2, 0, 90)
    $path2.AddArc($rect.X, $rect.Bottom - $r2, $r2, $r2, 90, 90)
    $path2.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 0x5A, 0xC8, 0xFA),   # 顶部天蓝
        [System.Drawing.Color]::FromArgb(255, 0x5E, 0x5C, 0xE6),   # 底部靛紫
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($brush, $path2)

    # ---- 白色放大镜(Spotlight,本工具核心)----
    $white = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
    $stroke = $S * 0.072
    $cx = $S * 0.455
    $cy = $S * 0.44
    $ringR = $S * 0.205
    $pen = New-Object System.Drawing.Pen($white, $stroke)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawEllipse($pen, ($cx - $ringR), ($cy - $ringR), ($ringR * 2), ($ringR * 2))

    # 手柄:从环边缘 45° 方向伸出
    $a = [Math]::PI / 4
    $x1 = $cx + $ringR * [Math]::Cos($a)
    $y1 = $cy + $ringR * [Math]::Sin($a)
    $x2 = $cx + ($ringR + $S * 0.145) * [Math]::Cos($a)
    $y2 = $cy + ($ringR + $S * 0.145) * [Math]::Sin($a)
    $g.DrawLine($pen, $x1, $y1, $x2, $y2)
    $pen.Dispose()

    # 缩到目标尺寸
    $small = New-Object System.Drawing.Bitmap($size, $size)
    $g2 = [System.Drawing.Graphics]::FromImage($small)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g2.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g2.DrawImage($bmp, 0, 0, $size, $size)
    $small.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)

    $g2.Dispose(); $small.Dispose(); $g.Dispose(); $bmp.Dispose()
    $brush.Dispose(); $hlBrush.Dispose(); $path2.Dispose()
}

# 生成各尺寸 PNG
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
foreach ($s in $sizes) {
    New-IconPng -size $s -path (Join-Path $outDir "icon-$s.png")
    Write-Output ("generated icon-$s.png")
}
