# Downloads the GigaAM v3 E2E CTC ONNX weights (int8) into <repo>\models,
# plus the matching log-mel preprocessor.
#
# Weights: https://huggingface.co/istupakov/gigaam-v3-onnx (MIT)
# Preprocessor: the model takes 64-bin features, not a waveform, and the feature
# extractor is not published in the model repo - it ships inside the onnx-asr
# wheel (MIT, same author), which is a plain zip we unpack.
param(
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) "models")
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$base = "https://huggingface.co/istupakov/gigaam-v3-onnx/resolve/main"
$files = @(
    "v3_e2e_ctc.int8.onnx",
    "v3_e2e_ctc_vocab.txt",
    "config.json"
)

New-Item -ItemType Directory -Force $OutDir | Out-Null

foreach ($f in $files) {
    $dest = Join-Path $OutDir $f
    if (Test-Path $dest) {
        Write-Host "skip  $f (exists)"
        continue
    }
    Write-Host "fetch $f ..."
    $tmp = "$dest.part"
    try {
        # BITS is much faster than Invoke-WebRequest for the 215 MB model
        Start-BitsTransfer -Source "$base/$f" -Destination $tmp
    } catch {
        Invoke-WebRequest "$base/$f" -OutFile $tmp
    }
    Move-Item $tmp $dest
}

$preprocessor = Join-Path $OutDir "gigaam_v3.onnx"
if (Test-Path $preprocessor) {
    Write-Host "skip  gigaam_v3.onnx (exists)"
} else {
    Write-Host "fetch gigaam_v3.onnx from the onnx-asr wheel ..."
    $wheelUrl = "https://files.pythonhosted.org/packages/6a/60/2fa469a2ee674c35ab48821a1039762ae7b9d0b88188ac1012e779477f76/onnx_asr-0.12.0-py3-none-any.whl"
    $temp = Join-Path $env:TEMP ("onnx-asr-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force $temp | Out-Null
    try {
        # Expand-Archive only accepts .zip, and a wheel is a zip.
        $zip = Join-Path $temp "onnx_asr.zip"
        Invoke-WebRequest $wheelUrl -OutFile $zip
        Expand-Archive -Path $zip -DestinationPath $temp -Force
        $src = Join-Path $temp "onnx_asr\preprocessors\data\gigaam_v3.onnx"
        if (-not (Test-Path $src)) { throw "gigaam_v3.onnx not found inside the wheel" }
        Copy-Item $src $preprocessor
    } finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Model files ready in $OutDir"
