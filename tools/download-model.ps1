# Downloads the Parakeet TDT 0.6B V3 ONNX weights (int8) into <repo>\models.
# Source: https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx
param(
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) "models")
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$base = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main"
$files = @(
    "nemo128.onnx",
    "vocab.txt",
    "config.json",
    "encoder-model.int8.onnx",
    "decoder_joint-model.int8.onnx"
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
        # BITS is much faster than Invoke-WebRequest for the 650 MB encoder
        Start-BitsTransfer -Source "$base/$f" -Destination $tmp
    } catch {
        Invoke-WebRequest "$base/$f" -OutFile $tmp
    }
    Move-Item $tmp $dest
}

Write-Host "Model files ready in $OutDir"
