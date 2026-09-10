# Publishes Govorun.App as a self-contained win-x64 build into <repo>\publish.
# After this, compile installer\govorun.iss with Inno Setup 6.
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent

dotnet publish (Join-Path $repo "src\Govorun.App") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none `
    -o (Join-Path $repo "publish")

if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Write-Host "Published to $repo\publish"
