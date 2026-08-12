#Requires -Version 5.1
param(
    [string]$TargetDir,
    [switch]$Force
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "Continue"

if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $ProjectRoot = Split-Path -Parent $PSScriptRoot
    $TargetDir = Join-Path $ProjectRoot "assets\models"
}

Write-Host "==> PP-OCRv6_medium ONNX 模型下载 (GitHub HoVDuc Release)" -ForegroundColor Cyan
Write-Host "    Target: $TargetDir"
New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
$temp = Join-Path $TargetDir "_dl_tmp"
New-Item -ItemType Directory -Force -Path $temp | Out-Null

$DetZipUrl = "https://github.com/HoVDuc/ppocrv5-onnx/releases/download/v1.1.0/PP-OCRv6_medium_det_onnx.zip"
$RecZipUrl = "https://github.com/HoVDuc/ppocrv5-onnx/releases/download/v1.1.0/PP-OCRv6_medium_rec_onnx.zip"

function Get-SizePretty([long]$Bytes) {
    if ($Bytes -ge 1GB) { return "{0:N2} GB" -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return "{0:N2} MB" -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return "{0:N2} KB" -f ($Bytes / 1KB) }
    return "$Bytes B"
}

function DL([string]$url, [string]$out) {
    $name = Split-Path $out -Leaf
    if ((Test-Path $out) -and -not $Force) {
        $sz = (Get-Item $out).Length
        Write-Host "    SKIP $name  ($(Get-SizePretty $sz))" -ForegroundColor DarkGray
        return
    }
    Write-Host "    GET  $name  <- $url" -ForegroundColor Yellow
    $tmp = "$out.download"
    if (Test-Path $tmp) { Remove-Item $tmp -Force }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $wc = New-Object System.Net.WebClient
    $wc.Headers['User-Agent'] = 'QuickTranslate-ModelDL/2.0'
    Register-ObjectEvent -InputObject $wc -EventName DownloadProgressChanged -SourceIdentifier "dl_$name" -Action {
        param($s, $e)
        $pct = if ($e.TotalBytesToReceive -gt 0) { [math]::Min(100,[int](100*$e.BytesReceived/$e.TotalBytesToReceive)) } else { 0 }
        $received = Get-SizePretty $e.BytesReceived
        $total    = if ($e.TotalBytesToReceive -gt 0) { Get-SizePretty $e.TotalBytesToReceive } else { "?" }
        Write-Progress -Activity "Downloading $name" -Status ("{0} / {1}   {2}%" -f $received,$total,$pct) -PercentComplete $pct
    } | Out-Null
    try {
        $wc.DownloadFile($url, $tmp)
    } finally {
        Unregister-Event -SourceIdentifier "dl_$name" -ErrorAction SilentlyContinue
        Write-Progress -Activity "Downloading $name" -Completed
    }
    $sw.Stop()
    if (Test-Path $out) { Remove-Item $out -Force }
    Move-Item $tmp $out -Force
    $sz = (Get-Item $out).Length
    $sec = [math]::Max(0.001,$sw.Elapsed.TotalSeconds)
    $spd = Get-SizePretty ([long]($sz/$sec))
    Write-Host "    DONE  $name  $(Get-SizePretty $sz) in $($sec.ToString('N1'))s ($spd/s)" -ForegroundColor Green
}

Write-Host "  [1/2] Detector ZIP" -ForegroundColor Cyan
DL $DetZipUrl (Join-Path $temp "det.zip")
Write-Host "  [2/2] Recognizer ZIP" -ForegroundColor Cyan
DL $RecZipUrl (Join-Path $temp "rec.zip")

# ----- Extract and find .onnx / dictionary -----
Write-Host ""
Write-Host "==> Extracting archives and locating ONNX files..." -ForegroundColor Cyan

function Pick-FromZip([string]$zip, [string]$patterns, [ref]$hit) {
    $dir = Join-Path $temp ([IO.Path]::GetFileNameWithoutExtension($zip) + "_" + [guid]::NewGuid().ToString("N").Substring(0,6))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Expand-Archive -Path $zip -DestinationPath $dir -Force -ErrorAction Stop
    foreach ($pat in $patterns) {
        $f = Get-ChildItem -Path $dir -Recurse -Filter $pat | Select-Object -First 1
        if ($f) { $hit.Value = $f; return $dir }
    }
    return $dir
}

$detFile = $null
$recFile = $null
$dictFile = $null
$clsFile = $null

$detDir = Pick-FromZip (Join-Path $temp "det.zip") @("*.onnx","det.onnx") ([ref]$detFile)
Write-Host ("    Detector found: {0} ({1})" -f $detFile.FullName, (Get-SizePretty $detFile.Length)) -ForegroundColor Green

$recDir = Pick-FromZip (Join-Path $temp "rec.zip") @("*.onnx","rec.onnx") ([ref]$recFile)
Write-Host ("    Recognizer found: {0} ({1})" -f $recFile.FullName, (Get-SizePretty $recFile.Length)) -ForegroundColor Green

# Search both dirs for dictionary and cls
foreach ($d in @($detDir, $recDir)) {
    if (-not $dictFile) {
        $dictFile = Get-ChildItem -Path $d -Recurse -Include @("*keys*.txt","*dict*.txt","*.txt") | Where-Object { $_.Length -gt 1000 } | Select-Object -First 1
        if ($dictFile) { Write-Host ("    Dictionary found: {0} ({1})" -f $dictFile.FullName, (Get-SizePretty $dictFile.Length)) -ForegroundColor Green }
    }
    if (-not $clsFile) {
        $clsFile = Get-ChildItem -Path $d -Recurse -Filter "*.onnx" | Where-Object { $_.Name -match 'cls|orient' } | Select-Object -First 1
        if ($clsFile) { Write-Host ("    Classifier found: {0} ({1})" -f $clsFile.FullName, (Get-SizePretty $clsFile.Length)) -ForegroundColor Green }
    }
}

# ----- Place files -----
Write-Host ""
Write-Host "==> Placing files under $TargetDir" -ForegroundColor Cyan

$destDet  = Join-Path $TargetDir "det.onnx"
$destRec  = Join-Path $TargetDir "rec.onnx"
$destCls  = Join-Path $TargetDir "cls.onnx"
$destKeys = Join-Path $TargetDir "ppocr_keys.txt"

Copy-Item $detFile.FullName $destDet -Force
Write-Host "  -> det.onnx  ($(Get-SizePretty (Get-Item $destDet).Length))" -ForegroundColor Green
Copy-Item $recFile.FullName $destRec -Force
Write-Host "  -> rec.onnx  ($(Get-SizePretty (Get-Item $destRec).Length))" -ForegroundColor Green

if ($clsFile) {
    Copy-Item $clsFile.FullName $destCls -Force
    Write-Host "  -> cls.onnx  ($(Get-SizePretty (Get-Item $destCls).Length))" -ForegroundColor Green
} else {
    Write-Host "  !! cls.onnx  NOT available (angle classification will be skipped)" -ForegroundColor DarkYellow
    # PaddleOcrV6Engine uses optional check; we just need the file to exist for CheckModelFiles() if strict.
    # Instead, update the Engine to accept missing cls.onnx (we'll patch the code separately)
}

if ($dictFile) {
    Copy-Item $dictFile.FullName $destKeys -Force
    Write-Host ("  -> ppocr_keys.txt  ($(Get-SizePretty (Get-Item $destKeys).Length))  ({0} lines)" -f ((Get-Content $destKeys).Count)) -ForegroundColor Green
} else {
    Write-Host "  !! ppocr_keys.txt NOT in zip — using existing placeholder" -ForegroundColor DarkYellow
}

# Cleanup temp
try { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue } catch { }

Write-Host ""
Write-Host "==> Final assets/models inventory:" -ForegroundColor Cyan
Get-ChildItem $TargetDir -File | ForEach-Object { Write-Host ("  {0,-18} {1}" -f $_.Name, (Get-SizePretty $_.Length)) }

Write-Host ""
Write-Host "Done. Restart QuickTranslate for PP-OCRv6_medium engine activation." -ForegroundColor Green
