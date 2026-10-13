<#
  Draws the app icon (white Android-head outline on a dark grey tile) and writes it as a macOS .icns file.
  macOS icons sit on a transparent margin (~10%), unlike the Windows icon, which is why this is a separate render.

  Usage: powershell -ExecutionPolicy Bypass -File make-icns.ps1 -OutPath ..\ApkGenerator.Mac\Assets\AppIcon.icns
#>
param([Parameter(Mandatory)][string]$OutPath)

Add-Type -AssemblyName System.Drawing

function P($x, $y) { New-Object System.Drawing.PointF ([single]$x), ([single]$y) }

function New-Frame([int]$size) {
    $k = $size / 256.0
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"; $g.PixelOffsetMode = "HighQuality"; $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform([single]$k, [single]$k)

    # tile: 10% margin, rounded corners
    $m = 26; $w = 256 - 2 * $m; $r = $w * 0.225; $d = 2 * $r
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc($m, $m, $d, $d, 180, 90); $tile.AddArc($m + $w - $d, $m, $d, $d, 270, 90)
    $tile.AddArc($m + $w - $d, $m + $w - $d, $d, $d, 0, 90); $tile.AddArc($m, $m + $w - $d, $d, $d, 90, 90); $tile.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 46, 46, 48))), $tile)

    # glyph, scaled to fit inside the smaller tile
    $g.TranslateTransform(128, 128); $g.ScaleTransform(0.84, 0.84); $g.TranslateTransform(-128, -128)

    $white = [System.Drawing.Color]::White
    $sw = [math]::Max(13.0, 1.5 / $k / 0.84)
    $pen = New-Object System.Drawing.Pen $white, $sw
    $pen.StartCap = "Round"; $pen.EndCap = "Round"; $pen.LineJoin = "Round"

    $cx = 128; $cy = 170; $rad = 82
    $head = New-Object System.Drawing.Drawing2D.GraphicsPath
    $head.AddArc($cx - $rad, $cy - $rad, 2 * $rad, 2 * $rad, 180, 180); $head.CloseFigure()
    $g.DrawPath($pen, $head)
    $g.DrawLine($pen, $cx - 40, $cy - 72, $cx - 66, $cy - 112)
    $g.DrawLine($pen, $cx + 40, $cy - 72, $cx + 66, $cy - 112)
    $er = [math]::Max(9.0, 1.0 / $k / 0.84)
    $wb = New-Object System.Drawing.SolidBrush $white
    $g.FillEllipse($wb, $cx - 30 - $er, $cy - 40 - $er, 2 * $er, 2 * $er)
    $g.FillEllipse($wb, $cx + 30 - $er, $cy - 40 - $er, 2 * $er, 2 * $er)
    $g.Dispose()
    return $bmp
}

# icns "types" that hold PNG data: (size, four-character code)
$types = @( @(16, 'icp4'), @(32, 'icp5'), @(64, 'icp6'), @(128, 'ic07'), @(256, 'ic08'), @(512, 'ic09'), @(1024, 'ic10') )

function BE32([int]$n) { [BitConverter]::GetBytes([System.Net.IPAddress]::HostToNetworkOrder($n)) }   # big-endian 32-bit

$chunks = New-Object System.Collections.Generic.List[byte[]]
foreach ($pair in $types) {
    $size = $pair[0]
    $bmp = New-Frame $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $png = $ms.ToArray()
    $chunk = [System.Text.Encoding]::ASCII.GetBytes($pair[1]) + (BE32 ($png.Length + 8)) + $png
    $chunks.Add($chunk)
}
$body = [byte[]]@()
foreach ($c in $chunks) { $body += $c }
$icns = [System.Text.Encoding]::ASCII.GetBytes('icns') + (BE32 ($body.Length + 8)) + $body

New-Item -ItemType Directory -Force (Split-Path -Parent ([IO.Path]::GetFullPath($OutPath))) | Out-Null
[System.IO.File]::WriteAllBytes([IO.Path]::GetFullPath($OutPath), $icns)
"Wrote $OutPath ($([math]::Round($icns.Length / 1KB)) KB)"
