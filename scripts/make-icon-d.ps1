# 从风格 D 高清图生成多尺寸 QuickTranslate.ico
# 用法: powershell -ExecutionPolicy Bypass -File scripts/make-icon-d.ps1
Add-Type -AssemblyName System.Drawing

$src = Join-Path (Get-Location) "assets\icons\icon-styleD-minimal-tray.jpg"
$out = Join-Path (Get-Location) "assets\icons\QuickTranslate.ico"
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

if (-not (Test-Path $src)) { Write-Host "源图不存在: $src" -ForegroundColor Red; exit 1 }

$source = [System.Drawing.Image]::FromFile($src)

function ConvertTo-IcoEntryBytes {
    param([System.Drawing.Bitmap]$bmp)
    # BITMAPINFOHEADER (40) + BGRA pixels(自底向上) + AND mask(1bpp)
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $pixelBytes = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixelBytes, 0, $pixelBytes.Length)
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER
    $bw.Write([int]40)          # biSize
    $bw.Write([int]$w)          # biWidth
    $bw.Write([int]($h * 2))    # biHeight (XOR + AND)
    $bw.Write([int16]1)         # biPlanes
    $bw.Write([int16]32)        # biBitCount
    $bw.Write([int]0)           # biCompression BI_RGB
    $bw.Write([int]0)           # biSizeImage
    $bw.Write([int]0)           # biXPelsPerMeter
    $bw.Write([int]0)           # biYPelsPerMeter
    $bw.Write([int]0)           # biClrUsed
    $bw.Write([int]0)           # biClrImportant
    # 像素 BGRA 自底向上
    for ($y = $h - 1; $y -ge 0; $y--) {
        $rowStart = $y * $stride
        for ($x = 0; $x -lt $w; $x++) {
            $i = $rowStart + $x * 4
            $b = $pixelBytes[$i]
            $g = $pixelBytes[$i + 1]
            $r = $pixelBytes[$i + 2]
            $a = $pixelBytes[$i + 3]
            $bw.Write([byte]$b); $bw.Write([byte]$g); $bw.Write([byte]$r); $bw.Write([byte]$a)
        }
    }
    # AND mask: 1bpp, 每行对齐 32bit, 全 0
    $andStride = [math]::Ceiling($w / 32.0) * 4
    $andRow = New-Object byte[] $andStride
    for ($y = 0; $y -lt $h; $y++) { $bw.Write($andRow) }
    $bw.Flush()
    return $ms.ToArray()
}

$entries = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($source, 0, 0, $s, $s)
    $g.Dispose()
    $bytes = ConvertTo-IcoEntryBytes -bmp $bmp
    $bmp.Dispose()
    Write-Host "size=$s dataLen=$($bytes.Length)"
    $entries += [pscustomobject]@{ Size = $s; Data = $bytes }
}

$outMs = New-Object System.IO.MemoryStream
$bwOut = New-Object System.IO.BinaryWriter($outMs)
$bwOut.Write([int16]0)                    # reserved
$bwOut.Write([int16]1)                    # type = icon
$bwOut.Write([int16]$entries.Count)       # count

$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dims = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bwOut.Write([byte]$dims)                        # width
    $bwOut.Write([byte]$dims)                        # height
    $bwOut.Write([byte]0)                            # colorCount
    $bwOut.Write([byte]0)                            # reserved
    $bwOut.Write([int16]1)                           # planes
    $bwOut.Write([int16]32)                          # bpp
    $bwOut.Write([int]$e.Data.Length)                # size
    $bwOut.Write([int]$offset)                       # offset
    $offset += $e.Data.Length
}
foreach ($e in $entries) { $outMs.Write($e.Data, 0, $e.Data.Length) }
$bwOut.Flush()
[System.IO.File]::WriteAllBytes($out, $outMs.ToArray())
$source.Dispose()

$sizeKb = [math]::Round((Get-Item $out).Length / 1KB, 1)
Write-Host "已生成 $out  ($sizeKb KB, $($entries.Count) 个尺寸: $($sizes -join '/'))" -ForegroundColor Green