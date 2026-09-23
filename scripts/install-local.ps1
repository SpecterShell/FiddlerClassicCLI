<#
.SYNOPSIS
Installs the existing local publish output for current-user development.

.DESCRIPTION
Run scripts/build.ps1 first. Uses artifacts/publish/win-x64/fiddler-classic-cli.exe
from this checkout and delegates installation to the adjacent install.ps1. Never builds,
downloads a release, or starts Fiddler. The default installation includes the bridge.

.PARAMETER InstallDirectory
Overrides the shared installer's fixed current-user installation directory.

.PARAMETER NoPathUpdate
Preserves the user and process PATH.

.PARAMETER SkipBridge
Installs only the CLI, leaving the existing bridge and host launch record unchanged.

.PARAMETER Json
Writes the shared installer's structured installation receipt to stdout.
#>
[CmdletBinding()]
param(
    [string]$InstallDirectory,
    [switch]$NoPathUpdate,
    [switch]$SkipBridge,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $repositoryRoot 'artifacts/publish/win-x64/fiddler-classic-cli.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Local publish output is missing: '$executable'. Run scripts/build.ps1 first."
}

& "$PSScriptRoot/install.ps1" -PackagePath $executable @PSBoundParameters
if (-not $?) { exit 1 }
