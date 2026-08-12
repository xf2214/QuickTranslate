param(
    [string]$CsvPath = "E:\翻译\assets\dictionaries\_ecdict_work\ecdict-lite.trimmed.csv"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName Microsoft.VisualBasic
$utf8 = New-Object System.Text.UTF8Encoding($false)
$parser = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser($CsvPath, $utf8)
$parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
$parser.SetDelimiters(',')
$parser.HasFieldsEnclosedInQuotes = $true
$parser.TrimWhiteSpace = $false
$hdr = $parser.ReadFields()
Write-Host ("HEADER count={0}: [{1}]" -f $hdr.Count, ($hdr -join " | "))
for ($i = 0; $i -lt 2; $i++) {
    $f = $parser.ReadFields()
    Write-Host ("`nROW {0} field count={1}" -f $i, $f.Count)
    for ($j = 0; $j -lt $f.Count; $j++) {
        $val = [string]$f[$j]
        $len = $val.Length
        $pv = if ($len -gt 90) { $val.Substring(0, 90) + "..." } else { $val }
        Write-Host ("  col{0} (len={1,4}): {2}" -f $j, $len, $pv)
    }
}
$parser.Close(); $parser.Dispose()
