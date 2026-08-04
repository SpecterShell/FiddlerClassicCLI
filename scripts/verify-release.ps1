<#
.SYNOPSIS
Verifies a packaged Fiddler Classic CLI release before artifact upload or publication.

.PARAMETER ReleaseDirectory
The directory containing fiddler-classic-win-x64.zip and SHA256SUMS.
#>
[CmdletBinding()]
param(
    [string]$ReleaseDirectory = "$PSScriptRoot/../artifacts/release"
)

$ErrorActionPreference = "Stop"
$resolvedReleaseDirectory = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$assetName = "fiddler-classic-win-x64.zip"
$assetPath = Join-Path $resolvedReleaseDirectory $assetName
$checksumPath = Join-Path $resolvedReleaseDirectory "SHA256SUMS"

if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
    throw "Release asset '$assetName' was not found in '$resolvedReleaseDirectory'."
}

if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
    throw "Release checksum manifest was not found in '$resolvedReleaseDirectory'."
}

$manifestLines = @(Get-Content -LiteralPath $checksumPath | Where-Object { $_.Trim() })
if ($manifestLines.Count -ne 1 -or
    $manifestLines[0] -notmatch "^([A-Fa-f0-9]{64})\s+\*?(.+)$" -or
    $matches[2].Trim() -ne $assetName) {
    throw "SHA256SUMS must contain one exact entry for '$assetName'."
}

$expectedHash = $matches[1].ToLowerInvariant()
$actualHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    throw "SHA-256 verification failed for '$assetName'."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($assetPath)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    $requiredEntries = @(
        "fiddler-classic.exe",
        "LICENSE",
        "install.ps1",
        "bridge/FiddlerClassic.Bridge.dll",
        "bridge/FiddlerClassic.Protocol.dll",
        "skills/fiddler-classic-cli/SKILL.md",
        "skills/fiddler-classic-cli/scripts/install.ps1",
        "docs/en-US/installation.md",
        "docs/zh-CN/installation.md"
    )

    foreach ($requiredEntry in $requiredEntries) {
        if ($entryNames -notcontains $requiredEntry) {
            throw "Release archive is missing '$requiredEntry'."
        }
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Verified $assetName ($actualHash)."
