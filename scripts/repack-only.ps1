# Run only PACK step from existing trimmed CSV, then inspect first 64 bytes hex dump + per-entry debug top 5.

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName Microsoft.VisualBasic
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$scriptPath = $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $PSScriptRoot
$workDir = Join-Path $repoRoot 'assets\dictionaries\_ecdict_work'
$trimmedCsv = Join-Path $workDir 'ecdict.trimmed.csv'
$packedNew   = Join-Path $workDir 'ecdict-lite.NEW.packed'

Write-Host ("Trimmed CSV = {0} exists={1}" -f $trimmedCsv, (Test-Path -LiteralPath $trimmedCsv))

$actualCount = 0
$fs = [System.IO.File]::Create($packedNew)
$bw = New-Object System.IO.BinaryWriter($fs, $utf8NoBom, $false)
try {
    $magicBytes = [byte[]](0x31, 0x44, 0x43, 0x45)
    $bw.Write($magicBytes)
    $bw.Write([int32]1)
    $countPos = $bw.BaseStream.Position
    $bw.Write([int32]0)

    $parser = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser($trimmedCsv, $utf8NoBom)
    $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
    $parser.SetDelimiters(',')
    $parser.HasFieldsEnclosedInQuotes = $true
    $parser.TrimWhiteSpace = $false
    [void]$parser.ReadFields()

    while (-not $parser.EndOfData) {
        try {
            $fields = $parser.ReadFields()
        } catch {
            continue
        }
        if ($fields.Count -lt 3) { continue }
        $w = [string]$fields[0]
        $p = [string]$fields[1]
        $t = [string]$fields[2]
        if ([string]::IsNullOrWhiteSpace($w) -or [string]::IsNullOrWhiteSpace($t)) { continue }

        $wBytes = $utf8NoBom.GetBytes($w)
        $pBytes = if ([string]::IsNullOrEmpty($p)) { [byte[]]@() } else { $utf8NoBom.GetBytes($p) }
        $tBytes = $utf8NoBom.GetBytes($t)
        if ($tBytes.Length -eq 0) { continue }

        if ($actualCount -lt 5) {
            Write-Host ("   entry{0,-2}: wLen={1,3} word='{2}'  pLen={3,2} pBytesLen={4,2}  tStrLen={5,3} tBytesLen={6,4}" -f $actualCount, $wBytes.Length, $w, $p.Length, $pBytes.Length, $t.Length, $tBytes.Length)
            if ($pBytes.Length -gt 0) {
                $hex = for ($j=0; $j -lt [Math]::Min(4,$pBytes.Length); $j++) { "{0:X2}" -f $pBytes[$j] }
                Write-Host ("     pBytes first {0} hex = {1}" -f $hex.Count, ($hex -join " "))
            }
            $thex = for ($j=0; $j -lt [Math]::Min(4,$tBytes.Length); $j++) { "{0:X2}" -f $tBytes[$j] }
            Write-Host ("     tBytes first {0} hex = {1}" -f $thex.Count, ($thex -join " "))
        }

        # DO NOT use $bw.Write(byte[]) here: PowerShell overload resolution sometimes binds
        # byte arrays to the wrong overload, silently corrupting the packed binary.
        # Use the raw FileStream for byte payloads and keep BinaryWriter only for LE int32.
        $bw.Write([int32]$wBytes.Length)
        $fs.Write($wBytes, 0, $wBytes.Length)
        $bw.Write([int32]$pBytes.Length)
        if ($pBytes.Length -gt 0) { $fs.Write($pBytes, 0, $pBytes.Length) }
        $bw.Write([int32]$tBytes.Length)
        $fs.Write($tBytes, 0, $tBytes.Length)
        $actualCount++
    }
    $parser.Close(); $parser.Dispose()
    $end = $bw.BaseStream.Position
    $bw.BaseStream.Position = $countPos
    $bw.Write([int32]$actualCount)
    $bw.BaseStream.Position = $end
} finally {
    $bw.Dispose(); $fs.Dispose()
}
Write-Host "`nWrote $actualCount entries"
$sz = (Get-Item -LiteralPath $packedNew).Length
Write-Host ("New packed size = {0} bytes ({1:F1} MB)" -f $sz, ($sz/1MB))

Write-Host "`n--- HEX DUMP of NEW packed (first 64 bytes) ---"
$all = [System.IO.File]::ReadAllBytes($packedNew)
for ($i = 0; $i -lt [Math]::Min(64,$all.Length); $i += 16) {
    $n = [Math]::Min(16, $all.Length - $i)
    $hex = for ($j = 0; $j -lt $n; $j++) { "{0:X2}" -f $all[$i+$j] }
    $asc = for ($j = 0; $j -lt $n; $j++) { $b = $all[$i+$j]; if ($b -ge 32 -and $b -le 126) { [char]$b } else { "." } }
    Write-Host ("   {0:X4}: {1,-47} {2}" -f $i, ($hex -join " "), (-join $asc))
}

Write-Host "`n--- Inline search for [C3 B0 D3 99] (UTF-8 of 'ðә') ---"
$pattern = [byte[]](0xC3, 0xB0, 0xD3, 0x99)
$found = New-Object 'System.Collections.Generic.List[int]'
for ($i = 0; $i -lt [Math]::Min(400, $all.Length) - 3; $i++) {
    if ($all[$i] -eq $pattern[0] -and $all[$i+1] -eq $pattern[1] -and $all[$i+2] -eq $pattern[2] -and $all[$i+3] -eq $pattern[3]) {
        $found.Add($i)
    }
}
Write-Host ("   Pattern occurrences in first 400 bytes: {0}" -f ($found.Count))
if ($found.Count -gt 0) { Write-Host ("   Offsets: {0}" -f ($found -join ', ')) } else { Write-Host "   NOT FOUND in first 400 bytes **********************" -ForegroundColor Red }

Write-Host "New packed saved at: $packedNew"
Write-Host "You can now copy this over e:\翻译\assets\dictionaries\ecdict-lite.packed manually."
