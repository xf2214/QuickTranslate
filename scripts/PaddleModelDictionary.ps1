# Deliberately supports only Paddle's scalar character sequences, without a YAML dependency.
function ConvertFrom-PaddleCharacterDictionaryYaml {
    param([string[]]$Lines)

    $inPostProcess = $false
    $postChildIndent = -1
    $dictionaryIndent = -1
    $listIndent = -1
    $text = New-Object System.Text.StringBuilder
    foreach ($line in $Lines) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line -match '^\s*#') { continue }

        if ($dictionaryIndent -ge 0) {
            if ($line -notmatch '^( *)- +(.+?)\s*$') { break }
            $indent = $Matches[1].Length
            $value = $Matches[2]
            if ($indent -lt $dictionaryIndent -or ($listIndent -ge 0 -and $indent -ne $listIndent)) { break }
            $listIndent = $indent
            if ($value.Length -ge 2 -and $value.StartsWith("'") -and $value.EndsWith("'")) {
                $value = $value.Substring(1, $value.Length - 2).Replace("''", "'")
            }
            elseif ($value.Length -ge 2 -and $value.StartsWith('"') -and $value.EndsWith('"')) {
                # YAML's JSON-compatible quoted scalars include Unicode and escaped quotes.
                # Do not merely strip quotes: that would put literal backslash-u into CTC labels.
                $value = [string](ConvertFrom-Json -InputObject $value -ErrorAction Stop)
            }
            if ($value.Contains("`r") -or $value.Contains("`n")) { throw 'Dictionary labels cannot contain line breaks' }
            [void]$text.Append($value)
            [void]$text.Append("`n")
            continue
        }

        if ($line -match '^PostProcess:\s*(?:#.*)?$') {
            $inPostProcess = $true
            $postChildIndent = -1
            continue
        }
        if ($line -notmatch '^( *)([A-Za-z_][A-Za-z_0-9]*):') { continue }
        $indent = $Matches[1].Length
        $key = $Matches[2]
        if ($indent -eq 0) { $inPostProcess = $false }
        elseif ($inPostProcess -and $postChildIndent -lt 0) { $postChildIndent = $indent }

        if ($key -ceq 'character_dict' -and
            ($indent -eq 0 -or ($inPostProcess -and $indent -eq $postChildIndent)) -and
            $line -match '^ *character_dict:\s*(?:#.*)?$') {
            $dictionaryIndent = $indent
        }
    }
    return $text.ToString()
}

function Write-PaddleCharacterDictionary {
    param([string[]]$Lines, [string]$Path)

    $text = ConvertFrom-PaddleCharacterDictionaryYaml -Lines $Lines
    if ($text.Length -lt 10000) { throw "generated dictionary too small ($($text.Length) chars)" }
    [IO.File]::WriteAllText($Path, $text, (New-Object System.Text.UTF8Encoding($false)))
    return ($text -split "`n").Count - 1
}
