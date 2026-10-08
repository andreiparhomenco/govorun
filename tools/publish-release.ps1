# Publishes a GitHub release for the installer built by build-installer.ps1.
#
# Attaches the installer TWICE on purpose:
#   GovorunSetup-<version>.exe - permanent per-version link, for changelogs
#   GovorunSetup.exe           - what the landing page button points at, through
#                                https://github.com/<repo>/releases/latest/download/GovorunSetup.exe
# Forgetting the second name silently breaks the download button on the site, so
# this script always uploads both.
#
# Requires the GitHub CLI authenticated: gh auth login
param(
    [string]$Version,
    [switch]$Draft
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent

if (-not $Version) {
    # Single source of truth for the version number.
    $iss = Get-Content (Join-Path $repo "installer\govorun.iss")
    $match = $iss | Select-String -Pattern '#define AppVersion "([^"]+)"'
    if (-not $match) { throw "AppVersion not found in installer\govorun.iss" }
    $Version = $match.Matches[0].Groups[1].Value
}

$versioned = Join-Path $repo "installer\output\GovorunSetup-$Version.exe"
$plain = Join-Path $repo "installer\output\GovorunSetup.exe"
$notes = Join-Path $repo "installer\output\release-notes-$Version.md"

if (-not (Test-Path $versioned)) { throw "Installer not found: $versioned - run tools\build-installer.ps1 first" }
if (-not (Test-Path $notes)) { throw "Release notes not found: $notes" }

$hash = (Get-FileHash $versioned -Algorithm SHA256).Hash
Write-Host "Version:  $Version"
Write-Host ("Size:     {0:N1} MB" -f ((Get-Item $versioned).Length / 1MB))
Write-Host "SHA-256:  $hash"

# The notes must carry the checksum of the file we are actually shipping.
if (-not (Select-String -Path $notes -Pattern $hash -SimpleMatch -Quiet)) {
    throw "release-notes-$Version.md does not contain the SHA-256 of this installer. Update it first."
}

Copy-Item $versioned $plain -Force

& gh release create "v$Version" $versioned $plain `
    --title "Govorun $Version" `
    --notes-file $notes `
    @(if ($Draft) { "--draft" })
if ($LASTEXITCODE -ne 0) { throw "gh release create failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "Permanent download link for the landing page:"
Write-Host "  https://github.com/andreiparhomenco/govorun/releases/latest/download/GovorunSetup.exe"
Write-Host "Put this checksum on the landing page:"
Write-Host "  $hash"
