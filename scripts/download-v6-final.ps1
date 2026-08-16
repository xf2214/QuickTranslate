param(
    [string]$TargetDir = 'E:\翻译\assets\models'
)
Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System.IO.Compression.FileSystem
$ErrorActionPreference = 'Stop'

function Sz([long]$b) {
    if ($b -ge 1GB) { return '{0:N2}GB' -f ($b / 1GB) }
    if ($b -ge 1MB) { return '{0:N2}MB' -f ($b / 1MB) }
    if ($b -ge 1KB) { return '{0:N1}KB' -f ($b / 1KB) }
    return "${b}B"
}

function DLWithRetry([string]$url, [string]$outFile, [int]$maxTries = 5) {
    $name = Split-Path $outFile -Leaf
    for ($try = 1; $try -le $maxTries; $try++) {
        Write-Host ("[TRY {0}/{1}] DL {2}" -f $try, $maxTries, $name) -ForegroundColor Yellow
        Write-Host ("    URL: {0}" -f $url) -ForegroundColor Gray
        $client = $null
        try {
            $client = New-Object System.Net.Http.HttpClient
            $client.Timeout = [timespan]::FromMinutes(25)
            $client.DefaultRequestHeaders.UserAgent.ParseAdd('QuickTranslate-ModelDL/1.1')
            $existLen = 0
            if (Test-Path $outFile) { $existLen = (Get-Item $outFile).Length }

            $reqMsg = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $url)
            if ($existLen -gt 0) {
                Write-Host ("    resume from {0}" -f (Sz $existLen)) -ForegroundColor Gray
                $reqMsg.Headers.Range = New-Object System.Net.Http.Headers.RangeHeaderValue($existLen, $null)
            }

            $resp = $client.SendAsync($reqMsg, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).Result
            Write-Host ("    HTTP {0}" -f [int]$resp.StatusCode) -ForegroundColor Gray
            if ($resp.StatusCode -eq [System.Net.HttpStatusCode]::RequestedRangeNotSatisfiable) {
                Write-Host "    (already complete)" -ForegroundColor Gray
                return $true
            }
            $resp.EnsureSuccessStatusCode()
            $netLen = $resp.Content.Headers.ContentLength

            if ($resp.StatusCode -eq [System.Net.HttpStatusCode]::PartialContent -and $existLen -gt 0) {
                $fs = [System.IO.File]::Open($outFile, [System.IO.FileMode]::Append)
            } else {
                if (Test-Path $outFile) { Remove-Item $outFile -Force }
                $fs = [System.IO.File]::Create($outFile)
                $existLen = 0
            }
            try {
                $st = $resp.Content.ReadAsStreamAsync().Result
                $buf = New-Object byte[] 524288
                $read = 0
                $chunkBytes = 0
                $sw = [Diagnostics.Stopwatch]::StartNew()
                while (($read = $st.Read($buf, 0, $buf.Length)) -gt 0) {
                    $fs.Write($buf, 0, $read)
                    $chunkBytes += $read
                    if ($sw.ElapsedMilliseconds -gt 4000) {
                        $sofar = $existLen + $chunkBytes
                        $spd = [long]($chunkBytes / $sw.Elapsed.TotalSeconds)
                        $remain = $existLen + $netLen
                        if ($netLen) {
                            $pct = [int](100 * $chunkBytes / $netLen)
                            Write-Host ("    {0}/{1} [{2}%] @ {3}/s" -f (Sz $sofar), (Sz $remain), $pct, (Sz $spd)) -ForegroundColor Gray
                        } else {
                            Write-Host ("    {0} @ {1}/s" -f (Sz $sofar), (Sz $spd)) -ForegroundColor Gray
                        }
                        $sw.Restart()
                        $chunkBytes = 0
                    }
                }
                $fs.Flush()
            } finally { $fs.Dispose() }
            $fin = (Get-Item $outFile).Length
            Write-Host ("    OK -> {0}" -f (Sz $fin)) -ForegroundColor Green
            return $true
        } catch {
            $msg = $_.Exception.Message
            if ($_.Exception.InnerException) { $msg = "{0} | inner: {1}" -f $msg, $_.Exception.InnerException.Message }
            Write-Host ("    FAIL: {0}" -f $msg) -ForegroundColor Red
            if ($try -lt $maxTries) {
                Write-Host "    sleep 5s before retry..." -ForegroundColor DarkYellow
                Start-Sleep -Seconds 5
            }
        } finally { if ($client) { $client.Dispose() } }
    }
    return $false
}

function VerifyZip([string]$path) {
    try { $z = [IO.Compression.ZipFile]::OpenRead($path); $z.Dispose(); return $true } catch { return $false }
}

function ExtractAndPlace([string]$zipPath, [string]$kind, [string]$TargetDir, [string]$tmpDir) {
    if (-not (VerifyZip $zipPath)) { throw "zip broken: $kind" }
    $sub = Join-Path $tmpDir ("{0}-extract" -f $kind)
    if (Test-Path $sub) { Remove-Item $sub -Recurse -Force }
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $sub)
    Write-Host ("Extract {0} -> listing:" -f $kind) -ForegroundColor Cyan
    Get-ChildItem $sub -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($sub.Length)
        Write-Host ("   {0,12}  {1}" -f (Sz $_.Length), $rel) -ForegroundColor Gray
    }
    # biggest onnx -> det/rec.onnx
    $onnx = Get-ChildItem $sub -Recurse -Filter '*.onnx' | Sort-Object Length -Descending | Select-Object -First 1
    if (-not $onnx) { throw ("no onnx found in {0} zip" -f $kind) }
    $dest = Join-Path $TargetDir ("{0}.onnx" -f $kind)
    if (Test-Path $dest) { Remove-Item $dest -Force }
    Copy-Item $onnx.FullName $dest -Force
    Write-Host ("==> placed {0}.onnx ({1})" -f $kind, (Sz (Get-Item $dest).Length)) -ForegroundColor Green

    # dictionary: 从 rec zip 的 inference.yml (PostProcess.character_dict) 生成。
    # 注意：字典必须与 rec.onnx 配套（v6 medium = 18708 字符 + blank + 空格 = 18710 类），
    # 不能使用 ppocr_keys_v1.txt（6623 行），否则 CTC 解码全乱码。
    if ($kind -eq 'rec') {
        $yml = Get-ChildItem $sub -Recurse -Include '*.yml', '*.yaml' | Select-Object -First 1
        if (-not $yml) { throw "rec zip has no inference.yml; cannot generate dictionary" }
        $kp = Join-Path $TargetDir 'ppocr_keys.txt'
        $inDict = $false
        $sb = New-Object System.Text.StringBuilder
        foreach ($line in [IO.File]::ReadAllLines($yml.FullName)) {
            if ($line -match '^[A-Za-z_]') {
                $inDict = $line.TrimStart().StartsWith('character_dict:')
                continue
            }
            if (-not $inDict) { continue }
            if ($line -match '^\s*-\s+(.+?)\s*$') {
                $v = $Matches[1]
                if ($v.Length -ge 2 -and $v.StartsWith("'") -and $v.EndsWith("'")) {
                    $v = $v.Substring(1, $v.Length - 2).Replace("''", "'")
                } elseif ($v.Length -ge 2 -and $v.StartsWith('"') -and $v.EndsWith('"')) {
                    $v = $v.Substring(1, $v.Length - 2)
                }
                [void]$sb.Append($v); [void]$sb.Append("`n")
            }
        }
        $dictText = $sb.ToString()
        if ($dictText.Length -lt 10000) { throw "generated dictionary too small ($($dictText.Length) chars)" }
        [IO.File]::WriteAllText($kp, $dictText, (New-Object System.Text.UTF8Encoding($false)))
        $lineCount = ($dictText -split "`n").Count - 1
        Write-Host ("==> generated ppocr_keys.txt ({0}, {1} lines, from {2})" -f (Sz (Get-Item $kp).Length), $lineCount, $yml.Name) -ForegroundColor Green
    }

    # optional cls.onnx: any other onnx smaller than 25MB that looks like classifier
    $cls = Get-ChildItem $sub -Recurse -Filter '*.onnx' |
        Where-Object { $_.FullName -ne $onnx.FullName -and $_.Length -lt 25MB } |
        Sort-Object Length | Select-Object -First 1
    if ($cls) {
        $cp = Join-Path $TargetDir 'cls.onnx'
        if (-not (Test-Path $cp)) {
            Copy-Item $cls.FullName $cp -Force
            Write-Host ("==> placed cls.onnx ({0}, src={1})" -f (Sz (Get-Item $cp).Length), $cls.Name) -ForegroundColor Green
        }
    }
}

# ======== MAIN ========
New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
$tmpDir = Join-Path $TargetDir '_dl_v2'
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
Remove-Item (Join-Path $TargetDir '_dl_tmp') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $TargetDir '_dl_tmp2') -Recurse -Force -ErrorAction SilentlyContinue

$proxy = ''  # direct access; set 'https://ghproxy.net/' to use proxy
$items = @(
    @{ Name = 'det'; Url = $proxy + 'https://github.com/HoVDuc/ppocrv5-onnx/releases/download/v1.1.0/PP-OCRv6_medium_det_onnx.zip' },
    @{ Name = 'rec'; Url = $proxy + 'https://github.com/HoVDuc/ppocrv5-onnx/releases/download/v1.1.0/PP-OCRv6_medium_rec_onnx.zip' }
)

foreach ($it in $items) {
    $zipOut = Join-Path $tmpDir ("{0}.zip" -f $it.Name)
    $ok = DLWithRetry $it.Url $zipOut 5
    if (-not $ok) { throw ("FATAL download failed: {0}" -f $it.Name) }
    if (-not (VerifyZip $zipOut)) {
        Write-Host ("zip verify failed; delete and retry {0}" -f $it.Name) -ForegroundColor Red
        Remove-Item $zipOut -Force
        $ok = DLWithRetry $it.Url $zipOut 3
        if (-not $ok -or -not (VerifyZip $zipOut)) { throw ("FATAL zip still broken: {0}" -f $it.Name) }
    }
    ExtractAndPlace $zipOut $it.Name $TargetDir $tmpDir
}

Write-Host "`n======== Final assets/models ========" -ForegroundColor Cyan
Get-ChildItem $TargetDir -File | Where-Object { $_.Name -notlike '_dl*' } | ForEach-Object {
    Write-Host ("   {0,-18} {1}" -f $_.Name, (Sz $_.Length))
}
Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "ALL DONE" -ForegroundColor Green
exit 0
