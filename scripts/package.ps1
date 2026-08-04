<#
.SYNOPSIS
Builds the self-contained Windows release and its checksum manifest.

.PARAMETER Configuration
The .NET build configuration.

.PARAMETER FiddlerInstallDir
The directory containing the local Fiddler.exe reference used to compile the bridge.

.PARAMETER Version
An optional package version supplied to MSBuild, such as a release tag without its leading v.

.PARAMETER SkipBuild
Packages the existing publish directory without rebuilding it.
#>
param(
    [string]$Configuration = "Release",
    [string]$FiddlerInstallDir = "$env:LOCALAPPDATA/Programs/Fiddler",
    [string]$Version,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $repositoryRoot "artifacts/publish/win-x64"
$releaseDirectory = Join-Path $repositoryRoot "artifacts/release"
$assetName = "fiddler-classic-win-x64.zip"
$assetPath = Join-Path $releaseDirectory $assetName
$checksumPath = Join-Path $releaseDirectory "SHA256SUMS"

if (-not $SkipBuild) {
    & "$PSScriptRoot/build.ps1" `
        -Configuration $Configuration `
        -FiddlerInstallDir $FiddlerInstallDir `
        -Version $Version
}

if (-not (Test-Path "$publishDirectory/fiddler-classic.exe" -PathType Leaf)) {
    throw "The published CLI was not found at '$publishDirectory'. Run scripts/build.ps1 first."
}

New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
Compress-Archive -Path "$publishDirectory/*" -DestinationPath $assetPath -CompressionLevel Optimal -Force

$hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText($checksumPath, "$hash  $assetName`n", [System.Text.UTF8Encoding]::new($false))

& "$PSScriptRoot/verify-release.ps1" -ReleaseDirectory $releaseDirectory

Write-Host "Release asset: $assetPath"
Write-Host "Checksums:    $checksumPath"
