# Isolated test: pack a tiny CSV with 2 rows and inspect packed structure
$ErrorActionPreference = "Stop"
$work = Join-Path $env:TEMP ("ecdict_pack_test_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$csvPath = Join-Path $work "mini.csv"
$packedPath = Join-Path $work "mini.packed"

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName Microsoft.VisualBasic

# --- build test CSV: word,phonetic,translation ---
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('word,phonetic,translation')
# word "the": phonetic empty, translation "art. 那/v. 是..."
$t1 = "art. 那/v. 是, 表示, 在`n[计] 后端, 总线允许"
$t1Q = '"' + ($t1 -replace '"','""') + '"'
[void]$sb.AppendLine(('"the","",' + $t1Q))
# word "be": phonetic "/bi/", translation "v. 是, 存在"
$t2 = 'v. 是, 存在'
$t2Q = '"' + ($t2 -replace '"','""') + '"'
[void]$sb.AppendLine(('"be","/bi/",' + $t2Q))
[System.IO.File]::WriteAllText($csvPath, $sb.ToString(), $utf8NoBom)
Write-Host "Wrote test CSV -> $csvPath"

# --- RUN EXACT SAME PACK LOGIC (from download-ecdict-lite Invoke-Pack) ---
$actualCount = 0
$fs = [System.IO.File]::Create($packedPath)
$bw = New-Object System.IO.BinaryWriter($fs, $utf8NoBom, $false)
try {
    $magicBytes = [byte[]](0x31, 0x44, 0x43, 0x45)
    $bw.Write($magicBytes)
    $bw.Write([int32]1)
    $countPos = $bw.BaseStream.Position
    $bw.Write([int32]0)

    $parser = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser($csvPath, $utf8NoBom)
    $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
    $parser.SetDelimiters(',')
    $parser.HasFieldsEnclosedInQuotes = $true
    $parser.TrimWhiteSpace = $false
    [void]$parser.ReadFields()

    while (-not $parser.EndOfData) {
        $fields = $parser.ReadFields()
        if ($fields.Count -lt 3) { continue }
        $w = [string]$fields[0]
        $p = [string]$fields[1]
        $t = [string]$fields[2]
        if ([string]::IsNullOrWhiteSpace($w) -or [string]::IsNullOrWhiteSpace($t)) { continue }
        Write-Host ("PACK: word='{0}'  phoneticLen={1}  translationLen={2}" -f $w, $p.Length, $t.Length)

        $wBytes = $utf8NoBom.GetBytes($w)
        $pBytes = if ([string]::IsNullOrEmpty($p)) { [byte[]]@() } else { $utf8NoBom.GetBytes($p) }
        $tBytes = $utf8NoBom.GetBytes($t)
        if ($tBytes.Length -eq 0) { continue }

        Write-Host ("   -> wBytes {0}, pBytes {1}, tBytes {2}" -f $wBytes.Length, $pBytes.Length, $tBytes.Length)

        $bw.Write([int32]$wBytes.Length); $bw.Write($wBytes)
        $bw.Write([int32]$pBytes.Length); if ($pBytes.Length -gt 0) { $bw.Write($pBytes) }
        $bw.Write([int32]$tBytes.Length); $bw.Write($tBytes)
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
Write-Host "`nActual count written = $actualCount"
Write-Host "Packed size = $((Get-Item -LiteralPath $packedPath).Length) bytes"
$hexPath = Join-Path $work "mini.hex.txt"
& "$env:SystemRoot\System32\certutil.exe" -encodehex $packedPath $hexPath 12 | Out-Null
Write-Host "`n--- hex dump (first 128 bytes from certutil): ---"
Get-Content -LiteralPath $hexPath -TotalCount 10
Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
