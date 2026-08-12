Param([string]$CsvDir = "")
$ErrorActionPreference = "Stop"
if ([string]::IsNullOrEmpty($CsvDir)) {
    # Derive from script location: <repo>/scripts -> <repo>/assets/dictionaries/_ecdict_work
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $CsvDir = Join-Path $repoRoot 'assets\dictionaries\_ecdict_work'
}
Add-Type -AssemblyName Microsoft.VisualBasic
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# --- trimmed CSV ---
$trimmed = Join-Path $CsvDir 'ecdict.trimmed.csv'
Write-Host "Trimmed CSV = $trimmed (exists=$(Test-Path -LiteralPath $trimmed))"
Write-Host "`n[1] Trimmed CSV parse top 5 rows via TextFieldParser:"
$p = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser($trimmed, $utf8NoBom)
$p.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
$p.SetDelimiters(',')
$p.HasFieldsEnclosedInQuotes = $true
$p.TrimWhiteSpace = $false
$hdr = $p.ReadFields()
Write-Host ("   HEADER count={0}: {1}" -f $hdr.Count, ($hdr -join ' | '))
for ($i = 0; $i -lt 5; $i++) {
    $f = $p.ReadFields()
    Write-Host ("   row {0} fieldCount={1}" -f $i, $f.Count)
    for ($j = 0; $j -lt $f.Count; $j++) {
        $val = [string]$f[$j]
        Write-Host ("     col{0} len={1,5}: {2}" -f $j, $val.Length, $(if ($val.Length -gt 100) { $val.Substring(0,100) + "..." } else { $val }))
    }
}
$p.Close(); $p.Dispose()
