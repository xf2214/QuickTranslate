$ErrorActionPreference = "Stop"
$tmp = Join-Path $env:TEMP ("bwtest_" + [guid]::NewGuid().ToString("N").Substring(0,8) + ".bin")
$utf8 = New-Object System.Text.UTF8Encoding($false)
$fs = [System.IO.File]::Create($tmp)
$bw = New-Object System.IO.BinaryWriter($fs, $utf8, $false)

$w = "x"
$p = [char]0x00F0 + [char]0x04D9   # ðә
$t = "y"

Write-Host ("Test: w='{0}' p='{1}' (strLen={2}) t='{3}'" -f $w, $p, $p.Length, $t)

$wB = $utf8.GetBytes($w)
$pB = $utf8.GetBytes($p)
$tB = $utf8.GetBytes($t)

Write-Host ("wB: {0}" -f (($wB | ForEach-Object { "{0:X2}" -f $_ }) -join " "))
Write-Host ("pB: {0}" -f (($pB | ForEach-Object { "{0:X2}" -f $_ }) -join " "))
Write-Host ("tB: {0}" -f (($tB | ForEach-Object { "{0:X2}" -f $_ }) -join " "))
Write-Host ("pB TYPE = {0}, LEN={1}" -f $pB.GetType().FullName, $pB.Length)

$bw.Write([int32]$wB.Length); $bw.Write($wB)
$bw.Write([int32]$pB.Length); if ($pB.Length -gt 0) { $bw.Write($pB) }
$bw.Write([int32]$tB.Length); $bw.Write($tB)

$bw.Dispose(); $fs.Dispose()

$all = [System.IO.File]::ReadAllBytes($tmp)
Write-Host ("`nWrote {0} bytes total -> {1}" -f $all.Length, $tmp)
Write-Host "Hex dump:"
for ($i = 0; $i -lt $all.Length; $i += 16) {
    $n = [Math]::Min(16, $all.Length - $i)
    $hex = for ($j = 0; $j -lt $n; $j++) { "{0:X2}" -f $all[$i+$j] }
    Write-Host ("  {0:X4}: {1}" -f $i, ($hex -join " "))
}
# Search
$pattern = [byte[]](0xC3, 0xB0, 0xD3, 0x99)
$found = $false
for ($i = 0; $i -lt $all.Length - 3; $i++) {
    if ($all[$i] -eq $pattern[0] -and $all[$i+1] -eq $pattern[1] -and $all[$i+2] -eq $pattern[2] -and $all[$i+3] -eq $pattern[3]) {
        Write-Host ("FOUND pattern at offset {0:X4}" -f $i)
        $found = $true; break
    }
}
if (-not $found) { Write-Host "PATTERN NOT FOUND (BUG!)" -ForegroundColor Red }
Remove-Item $tmp -ErrorAction SilentlyContinue
