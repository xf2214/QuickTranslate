Param([string]$PackedPath)
$ErrorActionPreference = "Stop"
$p = (Resolve-Path $PackedPath).Path
$bytes = [System.IO.File]::ReadAllBytes($p)
Write-Host ("File: {0}, Size: {1} bytes" -f $p, $bytes.Length)

# Print first 64 bytes as hex + ascii
$take = [Math]::Min(64, $bytes.Length)
Write-Host ("`nFirst {0} bytes hex/ascii:" -f $take)
for ($i = 0; $i -lt $take; $i += 16) {
    $n = [Math]::Min(16, $take - $i)
    $hex = for ($j = 0; $j -lt $n; $j++) { "{0:X2}" -f $bytes[$i+$j] }
    $asc = for ($j = 0; $j -lt $n; $j++) {
        $b = $bytes[$i+$j]
        if ($b -ge 32 -and $b -le 126) { [char]$b } else { "." }
    }
    Write-Host ("  {0:X4}: {1,-47} {2}" -f $i, ($hex -join " "), (-join $asc))
}

# Parse header manually
Write-Host "`nManual parse:"
# LE uint -> bytes: [0..3] reversed
function Read-U32LE([byte[]]$b, [int]$off) {
    return [uint32]$b[$off] -bor ([uint32]$b[$off+1] -shl 8) -bor ([uint32]$b[$off+2] -shl 16) -bor ([uint32]$b[$off+3] -shl 24)
}
function Read-I32LE([byte[]]$b, [int]$off) {
    return [BitConverter]::ToInt32($b, $off)
}

$magic = [Text.Encoding]::ASCII.GetString($bytes[0..3])
Write-Host ("  bytes 0-3 = [{0:X2} {1:X2} {2:X2} {3:X2}] = ASCII '{4}', LE uint 0x{5:X8}" -f $bytes[0],$bytes[1],$bytes[2],$bytes[3],$magic,(Read-U32LE $bytes 0))

$version = Read-I32LE $bytes 4
$count   = Read-I32LE $bytes 8
Write-Host ("  version int32@4 = {0}" -f $version)
Write-Host ("  count   int32@8 = {0}" -f $count)

# Entry 0 starts at offset 12
$pos = 12
Write-Host "`nEntry 0 at offset ${pos}:"
$wLen = Read-I32LE $bytes $pos
Write-Host ("  wLen int32@{0} = {1}" -f $pos, $wLen) ; $pos += 4
$word = [Text.Encoding]::UTF8.GetString($bytes, $pos, $wLen)
Write-Host ("  word @{0} len={1} = '{2}'" -f $pos, $wLen, $word) ; $pos += $wLen
$pLen = Read-I32LE $bytes $pos
Write-Host ("  pLen int32@{0} = {1} (0x{1:X8})" -f $pos, $pLen) ; $pos += 4
if ($pLen -gt 0) {
    $phon = [Text.Encoding]::UTF8.GetString($bytes, $pos, [Math]::Min($pLen, 32))
    $hexP = for ($j=0; $j -lt [Math]::Min($pLen,8); $j++) { "{0:X2}" -f $bytes[$pos+$j] }
    Write-Host ("  phonetic @{0} len={1}  first 8B hex=[{2}]  preview='{3}'" -f $pos,$pLen,($hexP -join " "), $phon)
    $pos += $pLen
}
$tLen = Read-I32LE $bytes $pos
Write-Host ("  tLen int32@{0} = {1} (0x{1:X8}) = {2} MB" -f $pos,$tLen,([math]::Round($tLen/1MB,2)))
$asciiT = for ($j=0;$j -lt 4;$j++) { $b = $bytes[$pos+$j]; if ($b -ge 32 -and $b -le 126) { [char]$b } else { "." } }
Write-Host ("  tLen bytes as ASCII = '{0}'" -f (-join $asciiT))
