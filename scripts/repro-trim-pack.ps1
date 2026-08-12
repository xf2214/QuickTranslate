# Reproduce: run only TRIM + PACK steps on existing ecdict.csv,
# output WorkDir to ASCII temp so we can inspect trimmed CSV + debug pack parse.

$ErrorActionPreference = "Stop"
$TopN = 500000

# Existing paths
$RepoRoot = Split-Path -Parent $PSScriptRoot
$CsvPath = Join-Path $RepoRoot 'assets\dictionaries\ecdict.csv'
$OutDir  = Join-Path $RepoRoot 'assets\dictionaries'
$DictDir = $OutDir

# ASCII workdir (no encoding issues)
$WorkDir  = Join-Path $env:TEMP ('ecdict_work_ascii_' + [guid]::NewGuid().ToString('N').Substring(0,8))
$TrimmedCsvPath = Join-Path $WorkDir 'ecdict-lite.trimmed.csv'
$PackedPath      = Join-Path $WorkDir 'ecdict-lite.debug.packed'

New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
Write-Host "WorkDir = $WorkDir"
Write-Host "CsvPath = $CsvPath (exists=$(Test-Path -LiteralPath $CsvPath))"

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName Microsoft.VisualBasic

# ============================================================
# STEP 3 TRIM (exact same logic as Invoke-Trim in download script)
# ============================================================
Write-Host "[TRIM] Parsing original CSV..."
$rows = New-Object 'System.Collections.Generic.List[object]'
$parser = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser($CsvPath, $utf8NoBom)
$parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
$parser.SetDelimiters(',')
$parser.HasFieldsEnclosedInQuotes = $true
$parser.TrimWhiteSpace = $false
[void]$parser.ReadFields()   # skip header

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$total = 0
while (-not $parser.EndOfData) {
    try {
        $fields = $parser.ReadFields()
    } catch {
        continue
    }
    $total++
    if ($fields.Count -lt 10) { continue }
    $word = $fields[0]
    if ([string]::IsNullOrWhiteSpace($word)) { continue }
    $phonetic    = $fields[1]
    $translation = $fields[3]
    if ([string]::IsNullOrWhiteSpace($translation)) { continue }
    [int]$bnc = 0; [int]$frq = 0
    [void][int]::TryParse($fields[8].Trim(), [ref]$bnc)
    [void][int]::TryParse($fields[9].Trim(), [ref]$frq)
    if ($bnc -le 0 -and $frq -le 0) { $score = [int]::MaxValue }
    elseif ($bnc -le 0) { $score = $frq }
    elseif ($frq -le 0) { $score = $bnc }
    else { $score = [Math]::Min($bnc, $frq) }
    $rows.Add([pscustomobject]@{
        Score = $score
        Word = $word
        Phonetic = $phonetic
        Translation = $translation
    })
}
$parser.Close(); $parser.Dispose()
$sw.Stop()
Write-Host ("       parsed $total rows -> $($rows.Count) candidates in {0:F1}s" -f $sw.Elapsed.TotalSeconds)
Write-Host "       sorting..."
$sorted = $rows | Sort-Object -Property Score
$kept = $sorted | Select-Object -First $TopN

Write-Host "       writing trimmed CSV..."
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('word,phonetic,translation')
foreach ($r in $kept) {
    $w = $r.Word -replace '"','""'
    $p = if ($r.Phonetic) { $r.Phonetic -replace '"','""' } else { '' }
    $t = $r.Translation -replace '"','""'
    [void]$sb.AppendLine(('"{0}","{1}","{2}"' -f $w, $p, $t))
}
[System.IO.File]::WriteAllText($TrimmedCsvPath, $sb.ToString(), $utf8NoBom)
Write-Host "       trimmed CSV = $TrimmedCsvPath"

# ============================================================
# INSPECT TRIMMED CSV FIRST 3 ROWS via TextFieldParser
# ============================================================
Write-Host "`n[INSPECT TRIMMED CSV] TextFieldParser parse top 5 rows:"
$p2 = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser($TrimmedCsvPath, $utf8NoBom)
$p2.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
$p2.SetDelimiters(',')
$p2.HasFieldsEnclosedInQuotes = $true
$p2.TrimWhiteSpace = $false
$hdr = $p2.ReadFields()
Write-Host ("   HEADER count={0}: {1}" -f $hdr.Count, ($hdr -join ' | '))
for ($i = 0; $i -lt 5; $i++) {
    $f = $p2.ReadFields()
    Write-Host ("   row {0} fieldCount={1}" -f $i, $f.Count)
    for ($j = 0; $j -lt $f.Count; $j++) {
        $val = [string]$f[$j]
        Write-Host ("     col{0} len={1,5}: {2}" -f $j, $val.Length, $(if ($val.Length -gt 80) { $val.Substring(0,80) + "..." } else { $val }))
    }
}
$p2.Close(); $p2.Dispose()

# ============================================================
# STEP 4 PACK (exact same logic, but dump per-entry w/p/t lengths for first 5)
# ============================================================
Write-Host "`n[PACK] Writing binary packed + dumping per-entry parse for top 5:"
$actualCount = 0
$fs = [System.IO.File]::Create($PackedPath)
$bw = New-Object System.IO.BinaryWriter($fs, $utf8NoBom, $false)
try {
    $magicBytes = [byte[]](0x31, 0x44, 0x43, 0x45)
    $bw.Write($magicBytes)
    $bw.Write([int32]1)
    $countPos = $bw.BaseStream.Position
    $bw.Write([int32]0)

    $parser3 = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser($TrimmedCsvPath, $utf8NoBom)
    $parser3.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
    $parser3.SetDelimiters(',')
    $parser3.HasFieldsEnclosedInQuotes = $true
    $parser3.TrimWhiteSpace = $false
    [void]$parser3.ReadFields()

    while (-not $parser3.EndOfData) {
        try {
            $fields = $parser3.ReadFields()
        } catch {
            continue
        }
        if ($fields.Count -lt 3) { continue }
        $w = [string]$fields[0]
        $p = [string]$fields[1]
        $t = [string]$fields[2]
        if ([string]::IsNullOrWhiteSpace($w) -or [string]::IsNullOrWhiteSpace($t)) { continue }
        if ($actualCount -lt 5) {
            Write-Host ("   entry{0}: word='{1}' pLenStr={2} tLenStr={3}" -f $actualCount, $w, $p.Length, $t.Length)
        }
        $wBytes = $utf8NoBom.GetBytes($w)
        $pBytes = if ([string]::IsNullOrEmpty($p)) { [byte[]]@() } else { $utf8NoBom.GetBytes($p) }
        $tBytes = $utf8NoBom.GetBytes($t)
        if ($tBytes.Length -eq 0) { continue }
        if ($actualCount -lt 5) {
            Write-Host ("     -> wB={0} pB={1} tB={2}" -f $wBytes.Length, $pBytes.Length, $tBytes.Length)
        }
        $bw.Write([int32]$wBytes.Length); $bw.Write($wBytes)
        $bw.Write([int32]$pBytes.Length); if ($pBytes.Length -gt 0) { $bw.Write($pBytes) }
        $bw.Write([int32]$tBytes.Length); $bw.Write($tBytes)
        $actualCount++
    }
    $parser3.Close(); $parser3.Dispose()
    $end = $bw.BaseStream.Position
    $bw.BaseStream.Position = $countPos
    $bw.Write([int32]$actualCount)
    $bw.BaseStream.Position = $end
} finally {
    $bw.Dispose(); $fs.Dispose()
}
Write-Host "       wrote $actualCount entries"
$sz = (Get-Item -LiteralPath $PackedPath).Length
Write-Host "       size = {0:F1} MB" -f ($sz/1MB)

# Final hex dump of first 64 bytes of resulting packed
Write-Host "`n[PACKED HEX] first 64 bytes:"
$all = [System.IO.File]::ReadAllBytes($PackedPath)
for ($i = 0; $i -lt [Math]::Min(64,$all.Length); $i += 16) {
    $n = [Math]::Min(16, $all.Length - $i)
    $hex = for ($j = 0; $j -lt $n; $j++) { "{0:X2}" -f $all[$i+$j] }
    $asc = for ($j = 0; $j -lt $n; $j++) { $b = $all[$i+$j]; if ($b -ge 32 -and $b -le 126) { [char]$b } else { "." } }
    Write-Host ("   {0:X4}: {1,-47} {2}" -f $i, ($hex -join " "), (-join $asc))
}

Write-Host "`nDone. Trimmed CSV and Packed saved under: $WorkDir"
