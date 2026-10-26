param([string]$OfficialYamlPath)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../../scripts/PaddleModelDictionary.ps1')

function Assert-Equal($Expected, $Actual, [string]$Name) {
    if ($Expected -cne $Actual) { throw "${Name}: expected <$Expected>, actual <$Actual>" }
}

# A legacy root list must stop at its next sibling rather than absorb another list.
$legacy = @'
character_dict:
- a
- 'can''t'
other_list:
- must-not-appear
'@
Assert-Equal "a`ncan't`n" (ConvertFrom-PaddleCharacterDictionaryYaml -Lines ($legacy -split "`n")) 'legacy root list and quoted apostrophe'

# The official export uses a PostProcess child and an indentation-less sequence.
$nested = @'
PreProcess:
  unrelated:
  - must-not-appear
PostProcess:
  name: CTCLabelDecode
  character_dict:
  - '!'
  - ' '
  - "\u4e2d"
  - "\""
  use_space_char: true
  unrelated:
  - must-not-appear
NextBlock:
  items:
  - must-not-appear
'@
Assert-Equal ("!`n `n" + [char]0x4e2d + "`n" + '"' + "`n") (ConvertFrom-PaddleCharacterDictionaryYaml -Lines ($nested -split "`n")) 'PostProcess list, JSON scalar decoding, and sibling boundary'

$indented = @'
PostProcess:
  character_dict:
    - a
    # comments and blank lines do not end the sequence

    - b
OtherBlock:
  items:
    - must-not-appear
'@
Assert-Equal "a`nb`n" (ConvertFrom-PaddleCharacterDictionaryYaml -Lines ($indented -split "`n")) 'indented list and following block boundary'

$differentChild = @'
PostProcess:
  unrelated:
    character_dict:
    - must-not-appear
  character_dict:
  - correct
'@
Assert-Equal "correct`n" (ConvertFrom-PaddleCharacterDictionaryYaml -Lines ($differentChild -split "`n")) 'only direct PostProcess character_dict child'

# U+3000 is a real, unquoted dictionary label in the official export. A lazy
# empty capture followed by \s* would silently erase it and change the SHA256.
$unicodeWhitespace = @('character_dict:', ('- ' + [char]0x3000))
Assert-Equal (([string][char]0x3000) + "`n") (ConvertFrom-PaddleCharacterDictionaryYaml -Lines $unicodeWhitespace) 'unquoted Unicode whitespace is a character label'

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('qt-model-dictionary-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
try {
    $output = Join-Path $temporary 'ppocr_keys.txt'
    $smallRejected = $false
    try { Write-PaddleCharacterDictionary -Lines @('character_dict:', '- a') -Path $output | Out-Null }
    catch { $smallRejected = $_.Exception.Message -like 'generated dictionary too small*' }
    Assert-Equal $true $smallRejected 'small dictionary is rejected before writing'
    Assert-Equal $false (Test-Path $output) 'small dictionary does not publish a file'

    $many = @('PostProcess:', '  character_dict:') + @('  - a') * 10000
    Assert-Equal 10000 (Write-PaddleCharacterDictionary -Lines $many -Path $output) 'exported line count'
    $bytes = [IO.File]::ReadAllBytes($output)
    Assert-Equal 20000 $bytes.Length 'UTF8 output has no BOM and exactly one LF per scalar'
    Assert-Equal 97 ([int]$bytes[0]) 'first scalar starts the file'
    Assert-Equal 10 ([int]$bytes[1]) 'line separator is LF'
    Assert-Equal 10 ([int]$bytes[$bytes.Length - 1]) 'output ends with LF'
    Assert-Equal $false ($bytes -contains 13) 'no CR separators'

    if ($OfficialYamlPath) {
        $lines = [IO.File]::ReadAllLines((Resolve-Path $OfficialYamlPath).Path, [Text.Encoding]::UTF8)
        Assert-Equal 18708 (Write-PaddleCharacterDictionary -Lines $lines -Path $output) 'official small-rec dictionary size'
        Assert-Equal 'B5F2BFE2BDD9448429E3E82B51C789775D9B42F2403D082B00662EB77E401C5D' (Get-FileHash $output -Algorithm SHA256).Hash 'official dictionary SHA256'
    }
}
finally { Remove-Item $temporary -Recurse -Force }

Write-Host 'Model dictionary parser regressions passed.'
