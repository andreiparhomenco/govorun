# Collects the VC++ runtime DLLs (app-local deployment) into assets\vcruntime.
# onnxruntime.dll needs them; on machines without any VC++ redist installed the
# engine fails with TypeInitializationException. Shipping them next to the exe
# fixes that without requiring admin rights.
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $repo "assets\vcruntime"
New-Item -ItemType Directory -Force $outDir | Out-Null

$dlls = "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll", "vcruntime140.dll", "vcruntime140_1.dll"
foreach ($d in $dlls) {
    $src = "C:\Windows\System32\$d"
    if (-not (Test-Path $src)) { throw "$src not found - install VC++ redist on the build machine" }
    Copy-Item $src (Join-Path $outDir $d) -Force
    Write-Host ("{0} {1}" -f $d, (Get-Item $src).VersionInfo.FileVersion)
}
Write-Host "VC++ runtime collected in $outDir"
