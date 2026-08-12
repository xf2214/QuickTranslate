#Requires -Version 5.1
param(
    [string]$TargetDir,
    [switch]$Force,
    [switch]$SkipVerify
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "Continue"

# ---- Defaults ----
if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $ProjectRoot = Split-Path -Parent $PSScriptRoot
    $TargetDir = Join-Path $ProjectRoot "assets\models"
}

Write-Host "==> PP-OCRv6 ONNX Model Downloader" -ForegroundColor Cyan
Write-Host "    Target: $TargetDir"
New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null

# ---- URLs ----
$Files = [ordered]@{
    "det.onnx"       = "https://paddleocr.bj.bcebos.com/PP-OCRv6/chinese/ch_PP-OCRv6_det_server_infer.onnx"
    "cls.onnx"       = "https://paddleocr.bj.bcebos.com/dygraph_v2.0/ch/ch_ppocr_mobile_v2.0_cls_infer.onnx"
    "rec.onnx"       = "https://paddleocr.bj.bcebos.com/PP-OCRv6/chinese/ch_PP-OCRv6_rec_server_infer.onnx"
    "ppocr_keys.txt" = "https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/main/ppocr/utils/ppocr_keys_v1.txt"
}

function Get-SizePretty {
    param([long]$Bytes)
    if ($Bytes -ge 1GB) { return "{0:N2} GB" -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return "{0:N2} MB" -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return "{0:N2} KB" -f ($Bytes / 1KB) }
    return "$Bytes B"
}

$totalDone = 0L
$fileIdx = 0
foreach ($kv in $Files.GetEnumerator()) {
    $fileIdx++
    $name = $kv.Key
    $url  = $kv.Value
    $dest = Join-Path $TargetDir $name
    $tmp  = "$dest.download"

    if ((Test-Path $dest) -and -not $Force) {
        $sz = (Get-Item $dest).Length
        Write-Host ("  [{0}/{1}] SKIP {2}  ({3})" -f $fileIdx, $Files.Count, $name, (Get-SizePretty $sz)) -ForegroundColor DarkGray
        $totalDone += $sz
        continue
    }

    Write-Host ("  [{0}/{1}] GET  {2}" -f $fileIdx, $Files.Count, $name) -ForegroundColor Yellow
    Write-Host ("        <- {0}" -f $url) -ForegroundColor Gray

    if (Test-Path $tmp) { Remove-Item $tmp -Force }

    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $wc = New-Object System.Net.WebClient
        $wc.Headers["User-Agent"] = "QuickTranslate-ModelDownloader/1.0"

        Register-ObjectEvent -InputObject $wc -EventName DownloadProgressChanged -SourceIdentifier "dl_$name" -Action {
            param($s, $e)
            $pct = if ($e.TotalBytesToReceive -gt 0) {
                [math]::Min(100, [int](100 * $e.BytesReceived / $e.TotalBytesToReceive))
            } else { 0 }
            $received = Get-SizePretty $e.BytesReceived
            $total    = if ($e.TotalBytesToReceive -gt 0) { Get-SizePretty $e.TotalBytesToReceive } else { "?" }
            Write-Progress -Activity "Downloading $name" `
                           -Status ("{0} / {1}   {2}%" -f $received, $total, $pct) `
                           -PercentComplete $pct
        } | Out-Null

        $wc.DownloadFile($url, $tmp)
        Unregister-Event -SourceIdentifier "dl_$name" -ErrorAction SilentlyContinue
        $sw.Stop()
        Write-Progress -Activity "Downloading $name" -Completed

        if (Test-Path $dest) { Remove-Item $dest -Force }
        Move-Item $tmp $dest -Force

        $sz = (Get-Item $dest).Length
        $totalDone += $sz
        $sec = [math]::Max(0.001, $sw.Elapsed.TotalSeconds)
        $speed = Get-SizePretty ([long]($sz / $sec))
        Write-Host ("        -> {0} in {1:N1}s ({2}/s)" -f (Get-SizePretty $sz), $sec, $speed) -ForegroundColor Green
    }
    catch {
        Unregister-Event -SourceIdentifier "dl_$name" -ErrorAction SilentlyContinue
        Write-Progress -Activity "Downloading $name" -Completed
        Write-Host ("        ERROR: {0}" -f $_.Exception.Message) -ForegroundColor Red
        if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
        exit 1
    }
}

Write-Host ""
Write-Host "==> Done. Total: $(Get-SizePretty $totalDone)" -ForegroundColor Cyan

# ---- SHA256 ----
if (-not $SkipVerify) {
    Write-Host ""
    Write-Host "==> SHA256 checksums:" -ForegroundColor Cyan
    $hashLines = @()
    foreach ($kv in $Files.GetEnumerator()) {
        $name = $kv.Key
        $dest = Join-Path $TargetDir $name
        if (-not (Test-Path $dest)) {
            Write-Host ("    {0,-18} MISSING" -f $name) -ForegroundColor Red
            continue
        }
        $hash = (Get-FileHash -Path $dest -Algorithm SHA256).Hash.ToLowerInvariant()
        $sz   = (Get-Item $dest).Length
        Write-Host ("    {0,-18} {1}  {2}" -f $name, (Get-SizePretty $sz), $hash) -ForegroundColor Gray
        $hashLines += [PSCustomObject]@{ file = $name; sha256 = $hash; size = $sz }
    }

    # Update version.json sha256 placeholders if present
    $vj = Join-Path $TargetDir "version.json"
    if (Test-Path $vj) {
        try {
            $data = Get-Content $vj -Raw | ConvertFrom-Json
            $changed = $false
            foreach ($h in $hashLines) {
                if     ($h.file -eq "det.onnx" -and $data.PSObject.Properties["det"]) {
                    if ([string]::IsNullOrWhiteSpace($data.det.sha256)) { $data.det.sha256 = $h.sha256; $changed = $true }
                }
                elseif ($h.file -eq "rec.onnx" -and $data.PSObject.Properties["rec"]) {
                    if ([string]::IsNullOrWhiteSpace($data.rec.sha256)) { $data.rec.sha256 = $h.sha256; $changed = $true }
                }
                elseif ($h.file -eq "cls.onnx" -and $data.PSObject.Properties["cls"]) {
                    if ([string]::IsNullOrWhiteSpace($data.cls.sha256)) { $data.cls.sha256 = $h.sha256; $changed = $true }
                }
            }
            if ($changed) {
                $data | ConvertTo-Json -Depth 5 | Set-Content -Path $vj -Encoding UTF8
                Write-Host "    updated version.json sha256 fields" -ForegroundColor DarkGreen
            }
        } catch {
            Write-Host ("    version.json update skipped: {0}" -f $_.Exception.Message) -ForegroundColor DarkYellow
        }
    }
}

Write-Host ""
Write-Host "Models are ready. Restart QuickTranslate to use PP-OCRv6 real engine." -ForegroundColor Green
