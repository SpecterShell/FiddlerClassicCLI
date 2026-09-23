<#
.SYNOPSIS
Builds and stages the standalone Windows executable as the only release file.

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
$executableName = "fiddler-classic-cli.exe"
$executablePath = Join-Path $releaseDirectory $executableName

if (-not $SkipBuild) {
    & "$PSScriptRoot/build.ps1" `
        -Configuration $Configuration `
        -FiddlerInstallDir $FiddlerInstallDir `
        -Version $Version
}

& "$PSScriptRoot/verify-release.ps1" -ReleaseDirectory $publishDirectory
$hash = (Get-FileHash -LiteralPath (Join-Path $publishDirectory $executableName) -Algorithm SHA256).Hash
& "$PSScriptRoot/reset-artifact-directory.ps1" -Directory 'release'
Copy-Item -LiteralPath (Join-Path $publishDirectory $executableName) -Destination $executablePath -Force
& "$PSScriptRoot/verify-release.ps1" -ReleaseDirectory $releaseDirectory -ExpectedSha256 $hash

Write-Host "Release executable: $executablePath"
