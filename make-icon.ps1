Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256

function New-Glyph([int]$w) {
    $bmp = New-Object System.Drawing.Bitmap($w, $w, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $w / 256.0

    # Palette: light board (pops on dark desktops), dark content, accent line
    $board    = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 232, 234, 237))
    $boardDk  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 193, 198, 205))
    $clipDk   = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 60, 64, 67))
    $line     = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 60, 64, 67))
    $accent   = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 76, 194, 255))

    $x = 48 * $s; $y = 56 * $s; $bw = 160 * $s; $bh = 180 * $s; $rad = 20 * $s

    # board
    $gr = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $rad * 2
    $gr.AddArc($x, $y, $d, $d, 180, 90)
    $gr.AddArc($x + $bw - $d, $y, $d, $d, 270, 90)
    $gr.AddArc($x + $bw - $d, $y + $bh - $d, $d, $d, 0, 90)
    $gr.AddArc($x, $y + $bh - $d, $d, $d, 90, 90)
    $gr.CloseFigure()
    $g.FillPath($board, $gr)

    # lower edge shading (3D hint)
    $g.FillRectangle($boardDk, $x, $y + $bh - 12 * $s, $bw, 12 * $s)

    # clip (paper clip shape on top)
    $g.FillRectangle($clipDk, 112 * $s, 30 * $s, 32 * $s, 34 * $s)
    $g.FillRectangle($board, 116 * $s, 26 * $s, 24 * $s, 10 * $s)

    # text lines: accent top, dark below
    $g.FillRectangle($accent, 76 * $s, 104 * $s, 104 * $s, 16 * $s)
    $g.FillRectangle($line, 76 * $s, 140 * $s, 104 * $s, 16 * $s)
    $g.FillRectangle($line, 76 * $s, 176 * $s, 76 * $s, 16 * $s)

    $g.Dispose()
    $board.Dispose(); $boardDk.Dispose(); $clipDk.Dispose(); $line.Dispose(); $accent.Dispose()
    return $bmp
}

$count = $sizes.Count
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$count)

$offset = 6 + 16 * $count
$blobs = @()
foreach ($sz in $sizes) {
    $bmp = New-Glyph $sz
    $rect = New-Object System.Drawing.Rectangle(0, 0, $sz, $sz)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $sz)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)
    $bmp.Dispose()

    $rowBytes = [Math]::Ceiling($sz / 32.0) * 4
    $andMask = New-Object byte[] ($rowBytes * $sz)
    $dib = New-Object System.IO.MemoryStream
    $bw2 = New-Object System.IO.BinaryWriter($dib)
    $bw2.Write([uint32]40)
    $bw2.Write([int32]$sz)
    $bw2.Write([int32]($sz * 2))
    $bw2.Write([uint16]1)
    $bw2.Write([uint16]32)
    $bw2.Write([uint32]0)
    $bw2.Write([uint32]($sz * $sz * 4))
    $bw2.Write([int32]0); $bw2.Write([int32]0); $bw2.Write([uint32]0); $bw2.Write([uint32]0)
    $bw2.Write($bytes)
    $bw2.Write($andMask)
    $blobs += , $dib.ToArray()
    $bw2.Dispose(); $dib.Dispose()
}
for ($i = 0; $i -lt $count; $i++) {
    $sz = $sizes[$i]
    $b = $blobs[$i]
    $box = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$box); $bw.Write([byte]$box)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$b.Length); $bw.Write([uint32]$offset)
    $offset += $b.Length
}
foreach ($b in $blobs) { $bw.Write($b) }
$bw.Flush()
$out = 'C:\Users\mcqz\source\Clipwise\Clipwise.ico'
[System.IO.File]::WriteAllBytes($out, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()
"wrote $out ($( (Get-Item $out).Length ) bytes, $count sizes)"
