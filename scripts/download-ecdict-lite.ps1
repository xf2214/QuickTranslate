#Requires -Version 5.1
<#
.SYNOPSIS
  Download the full ECDICT CSV, trim to the top N entries by bnc+frq combined
  word rank, then pack into a binary blob that QuickTranslate.EcdictLiteDictionary
  can load directly.

  Output: ..\assets\dictionaries\ecdict-lite.packed

.PARAMETER Action
  all       (default) run download -> trim -> pack
  download  just download ecdict.csv into work dir
  trim      just produce trimmed TopN csv from existing ecdict.csv
  pack      just pack an existing trimmed csv to the binary format

.PARAMETER TopN
  How many high-frequency words to keep (default 50000). Actual kept count
  may be slightly smaller because unranked entries (both bnc and frq = 0)
  are dropped.

.PARAMETER OutputDir
  Directory where ecdict-lite.packed is written. Defaults to
  <repo-root>\assets\dictionaries. A temporary *_work subdir is used.

.PARAMETER KeepCsv
  When set, the downloaded ecdict.csv (~30 MB) is NOT deleted after packing.

.EXAMPLE
  .\download-ecdict-lite.ps1                    # default 50K words
  .\download-ecdict-lite.ps1 all -TopN 300000   # bigger dictionary
#>
param(
  [ValidateSet('all','download','trim','pack')]
  [string]$Action = 'all',

  [int]$TopN = 50000,

  [string]$OutputDir,

  [switch]$KeepCsv
)

$ErrorActionPreference = 'Stop'

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
  $OutputDir = Join-Path (Split-Path -Parent $ScriptRoot) 'assets\dictionaries'
}
$WorkDir = Join-Path $OutputDir '_ecdict_work'
$CsvPath = Join-Path $WorkDir 'ecdict.csv'
$TrimmedCsvPath = Join-Path $WorkDir 'ecdict.trimmed.csv'
$PackedPath = Join-Path $OutputDir 'ecdict-lite.packed'

$CsvUrl = 'https://raw.githubusercontent.com/skywind3000/ECDICT/master/ecdict.csv'
$ExpectedHeader = 'word,phonetic,definition,translation,pos,collins,oxford,tag,bnc,frq,exchange,detail,audio'

Write-Host "==> ECDICT-lite build" -ForegroundColor Cyan
Write-Host "    Action      : $Action"
Write-Host "    TopN        : $TopN"
Write-Host "    OutputDir   : $OutputDir"
Write-Host "    WorkDir     : $WorkDir"
Write-Host ""

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

function Invoke-Download {
  if (Test-Path -LiteralPath $CsvPath) {
    Write-Host "[1/4] ecdict.csv already present, skipping download" -ForegroundColor Green
    return
  }
  Write-Host "[1/4] Downloading ECDICT CSV from primary mirror..." -ForegroundColor Cyan
  Write-Host "       URL: $CsvUrl"
  $ProgressPreference = 'SilentlyContinue'
  try {
    Invoke-WebRequest -UseBasicParsing -Uri $CsvUrl -OutFile $CsvPath
  } catch {
    Write-Host "       Primary download failed, trying mirror..." -ForegroundColor Yellow
    $MirrorUrl = 'https://raw.gitcode.com/gh_mirrors/ec/ECDICT/master/ecdict.csv'
    try {
      Invoke-WebRequest -UseBasicParsing -Uri $MirrorUrl -OutFile $CsvPath
    } catch {
      throw "Both download sources failed.`n  GitHub  : $CsvUrl`n  GitCode : $MirrorUrl"
    }
  }
  $ProgressPreference = 'Continue'
}

function Assert-CsvHeader {
  param([string]$Path)
  Write-Host "[2/4] Validating CSV header / sampling..." -ForegroundColor Cyan
  $size = (Get-Item -LiteralPath $Path).Length
  Write-Host "       size: $([math]::Round($size/1MB, 2)) MB"

  $firstLine = Get-Content -LiteralPath $Path -TotalCount 1 -Encoding UTF8
  if ($firstLine.Trim() -ne $ExpectedHeader) {
    throw "CSV header mismatch!`nExpected: $ExpectedHeader`nActual  : $($firstLine.Trim())"
  }
  Write-Host "       header: OK ($($firstLine.Split(',').Count) fields)"

  $probeLines = Get-Content -LiteralPath $Path -TotalCount 101 -Encoding UTF8 | Select-Object -Skip 1 -First 100
  $hasBncFrq = 0
  foreach ($line in $probeLines) {
    if ($line -match ',\d+,\d+,') { $hasBncFrq++; break }
  }
  Write-Host "       sample bnc/frq numeric rows: $hasBncFrq / 100"
}

function Invoke-Trim {
  Write-Host "[3/4] Parsing CSV + ranking by bnc/frq Top-$TopN (30-120s)..." -ForegroundColor Cyan

  Add-Type -AssemblyName Microsoft.VisualBasic
  $parser = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser($CsvPath, [System.Text.Encoding]::UTF8)
  $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
  $parser.SetDelimiters(',')
  $parser.HasFieldsEnclosedInQuotes = $true
  $parser.TrimWhiteSpace = $false

  [void]$parser.ReadFields()

  $rows = New-Object 'System.Collections.Generic.List[object]'
  $total = 0
  $sw = [System.Diagnostics.Stopwatch]::StartNew()

  while (-not $parser.EndOfData) {
    try {
      $fields = $parser.ReadFields()
    } catch {
      Write-Host "       WARN: skip malformed line $($parser.LineNumber): $($_.Exception.Message)" -ForegroundColor Yellow
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

  Write-Host "       parsed $total rows -> $($rows.Count) candidates in $($sw.Elapsed.TotalSeconds.ToString('0.0'))s"

  Write-Host "       sorting by rank..."
  $sorted = $rows | Sort-Object -Property Score
  Write-Host "       keeping first $TopN of $($sorted.Count)"

  $kept = $sorted | Select-Object -First $TopN

  # write trimmed csv so pack step can be re-run standalone
  $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
  $sb = New-Object System.Text.StringBuilder
  [void]$sb.AppendLine('word,phonetic,translation')
  $encUtf8 = New-Object System.Text.UTF8Encoding($false)
  foreach ($r in $kept) {
    $w = $r.Word -replace '"','""'
    $p = if ($r.Phonetic) { $r.Phonetic -replace '"','""' } else { '' }
    $t = $r.Translation -replace '"','""'
    [void]$sb.AppendLine(('"{0}","{1}","{2}"' -f $w, $p, $t))
  }
  [System.IO.File]::WriteAllText($TrimmedCsvPath, $sb.ToString(), $encUtf8)
  $sw2.Stop()
  $trimSize = (Get-Item -LiteralPath $TrimmedCsvPath).Length
  Write-Host "       trimmed csv: $([math]::Round($trimSize/1MB, 2)) MB in $($sw2.Elapsed.TotalSeconds.ToString('0.0'))s"
}

function Invoke-Pack {
  Write-Host "[4/4] Writing binary packed file -> $PackedPath" -ForegroundColor Cyan

  # format (little-endian, BinaryWriter defaults):
  #   [byte[4]] magic  = 0x31 0x44 0x43 0x45  ("ECD1" as LE read of uint32 0x45434431)
  #   [int32]  version = 1
  #   [int32]  entryCount
  #   entries repeated:
  #     [int32] wordLen, [byte[wordLen]] utf8 word
  #     [int32] phoneticLen, [byte[phoneticLen]] utf8 phonetic (may be 0)
  #     [int32] transLen, [byte[transLen]] utf8 translation
  $actualCount = 0
  $fs = [System.IO.File]::Create($PackedPath)
  $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
  $bw = New-Object System.IO.BinaryWriter($fs, $utf8NoBom, $false)
  try {
    $magicBytes = [byte[]](0x31, 0x44, 0x43, 0x45)   # "ECD1" -> LE uint 0x45434431 matches C# reader
    $bw.Write($magicBytes)
    $bw.Write([int32]1)
    $countPos = $bw.BaseStream.Position
    $bw.Write([int32]0)

    $totalKept = 0
    if (Test-Path -LiteralPath $TrimmedCsvPath) {
      # Use the same robust VisualBasic CSV parser as the trim step.
      # DO NOT use ReadAllLines + simple split: translation fields contain commas,
      # quotes and literal newlines inside quoted fields, which naive split cannot handle.
      Add-Type -AssemblyName Microsoft.VisualBasic
      $parser = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser($TrimmedCsvPath, $utf8NoBom)
      $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
      $parser.SetDelimiters(',')
      $parser.HasFieldsEnclosedInQuotes = $true
      $parser.TrimWhiteSpace = $false
      [void]$parser.ReadFields()   # skip header "word,phonetic,translation"

      while (-not $parser.EndOfData) {
        try {
          $fields = $parser.ReadFields()
        } catch {
          Write-Host "       WARN: skip malformed trimmed line $($parser.LineNumber): $($_.Exception.Message)" -ForegroundColor Yellow
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
    }
    $end = $bw.BaseStream.Position
    $bw.BaseStream.Position = $countPos
    $bw.Write([int32]$actualCount)
    $bw.BaseStream.Position = $end
  } finally {
    $bw.Dispose(); $fs.Dispose()
  }

  $packedSize = (Get-Item -LiteralPath $PackedPath).Length
  Write-Host "       wrote $actualCount entries, $([math]::Round($packedSize/1KB, 2)) KB ($([math]::Round($packedSize/1MB, 2)) MB)"
}

function Invoke-Cleanup {
  if (-not $KeepCsv -and (Test-Path -LiteralPath $WorkDir)) {
    Remove-Item -Recurse -Force -LiteralPath $WorkDir -ErrorAction SilentlyContinue
    Write-Host "       cleaned work dir"
  }
}

if ($Action -in 'all','download') { Invoke-Download }
if ($Action -in 'all','trim') {
  if (-not (Test-Path -LiteralPath $CsvPath)) { throw "ecdict.csv not found at $CsvPath (run download first)" }
  Assert-CsvHeader -Path $CsvPath
  Invoke-Trim
}
if ($Action -in 'all','pack') { Invoke-Pack }
if ($Action -eq 'all') { Invoke-Cleanup }

Write-Host ""
Write-Host "==> Done. Artifact: $PackedPath" -ForegroundColor Green
