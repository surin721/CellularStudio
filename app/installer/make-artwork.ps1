# Generates the WiX dialog artwork (banner 493x58, dialog 493x312) in the app's brand style.
param([string]$OutDir = $PSScriptRoot)
Add-Type -AssemblyName System.Drawing

$accentA = [System.Drawing.Color]::FromArgb(255, 0x5C, 0x7C, 0xFA)
$accentB = [System.Drawing.Color]::FromArgb(255, 0x3B, 0x5B, 0xDB)
$iconFont = if ((New-Object System.Drawing.Text.InstalledFontCollection).Families.Name -contains 'Segoe Fluent Icons') { 'Segoe Fluent Icons' } else { 'Segoe MDL2 Assets' }
$tower = [string][char]0xEC05

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}

function Draw-AppTile($g, [float]$x, [float]$y, [float]$size, [bool]$onBlue) {
    $path = New-RoundRect $x $y $size $size ($size * 0.24)
    if ($onBlue) {
        $b = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(50, 255, 255, 255))
    } else {
        $b = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF $x, $y, $size, $size), $accentA, $accentB, 45
    }
    $g.FillPath($b, $path)
    $f = New-Object System.Drawing.Font $iconFont, ([float]($size * 0.5)), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString($tower, $f, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF $x, ($y + $size * 0.03), $size, $size), $sf)
}

# ---- Dialog (welcome / finish): brand panel on the left 164 px, white elsewhere (WiX draws text on the right)
$bmp = New-Object System.Drawing.Bitmap 493, 312
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::White)
$panel = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle 0, 0, 164, 312), $accentA, $accentB, 70
$g.FillRectangle($panel, 0, 0, 164, 312)
# subtle signal arcs
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(28, 255, 255, 255)), 10
foreach ($r in 70, 120, 170) { $g.DrawArc($pen, (82 - $r), (250 - $r), ($r * 2), ($r * 2), 200, 140) }
Draw-AppTile $g 50 58 64 $true
$title = New-Object System.Drawing.Font 'Segoe UI Semibold', 15, ([System.Drawing.GraphicsUnit]::Pixel)
$sub = New-Object System.Drawing.Font 'Segoe UI', 11, ([System.Drawing.GraphicsUnit]::Pixel)
$sfc = New-Object System.Drawing.StringFormat; $sfc.Alignment = 'Center'
$g.DrawString('Cellular Studio', $title, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 136, 164, 24), $sfc)
$dot = [string][char]0x00B7   # literal "·" would be mis-read by PowerShell 5.1 (BOM-less UTF-8 script)
$g.DrawString("SMS $dot Balance $dot Modem", $sub, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(210, 255, 255, 255))), (New-Object System.Drawing.RectangleF 0, 160, 164, 20), $sfc)
$g.Dispose()
$bmp.Save((Join-Path $OutDir 'dialog.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()

# ---- Banner (inner pages): white, thin accent line at the bottom, app tile on the right
$bmp = New-Object System.Drawing.Bitmap 493, 58
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::White)
Draw-AppTile $g 437 11 36 $false
$g.Dispose()
$bmp.Save((Join-Path $OutDir 'banner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()
"artwork written to $OutDir"
