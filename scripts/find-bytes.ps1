Param(
    [string]$PackedPath,
    [byte[]]$Pattern = @(0xC3, 0xB0, 0xD3, 0x99)   # UTF-8 of "ðә" = phonetic of word "the"
)
$ErrorActionPreference = "Stop"
$all = [System.IO.File]::ReadAllBytes($PackedPath)
Write-Host ("Searching packed file [{0}] ({1} bytes) for pattern [{2}]..." -f $PackedPath, $all.Length, (($Pattern | ForEach-Object { "{0:X2}" -f $_ }) -join " "))
$found = New-Object 'System.Collections.Generic.List[int]'
for ($i = 0; $i -lt $all.Length - $Pattern.Length; $i++) {
    $match = $true
    for ($j = 0; $j -lt $Pattern.Length; $j++) {
        if ($all[$i+$j] -ne $Pattern[$j]) { $match = $false; break }
    }
    if ($match) { $found.Add($i); if ($found.Count -ge 5) { break } }
}
if ($found.Count -eq 0) { Write-Host "   NOT FOUND anywhere in the packed file!" -ForegroundColor Red }
else { Write-Host ("   Found at offsets: {0}" -f ($found -join ', ')) }

# Also search for the next word's pBytes pattern "bi:" (UTF-8 = 62 69 3A)
$nextPat = [byte[]](0x62, 0x69, 0x3A)
Write-Host ("`nSearching for 'bi:' pattern [62 69 3A]")
$f2 = New-Object 'System.Collections.Generic.List[int]'
for ($i = 0; $i -lt [Math]::Min($all.Length,256) - $nextPat.Length; $i++) {
    $m=$true
    for ($j=0;$j -lt $nextPat.Length;$j++){if($all[$i+$j] -ne $nextPat[$j]){$m=$false;break}}
    if($m){$f2.Add($i);if($f2.Count -ge 3){break}}
}
Write-Host ("   First 256 bytes occurrences: {0}" -f ($f2 -join ', '))
# End of script
