# Full installer build: self-contained publish + Inno Setup compile.
# Output: installer\output\GovorunSetup-<version>.exe
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent

if (-not (Test-Path (Join-Path $repo "models\encoder-model.int8.onnx"))) {
    throw "Model weights missing - run tools\download-model.ps1 first"
}

if (-not (Test-Path (Join-Path $repo "assets\vcruntime\vcruntime140.dll"))) {
    & (Join-Path $PSScriptRoot "get-vcruntime.ps1")
}

& (Join-Path $PSScriptRoot "publish.ps1")

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "ISCC.exe not found - install Inno Setup 6" }

& $iscc (Join-Path $repo "installer\govorun.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

Get-ChildItem (Join-Path $repo "installer\output") | ForEach-Object {
    Write-Host ("Installer: {0} ({1:N0} MB)" -f $_.FullName, ($_.Length / 1MB))
}
