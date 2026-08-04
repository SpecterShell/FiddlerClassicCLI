<#
.SYNOPSIS
Installs the pinned Fiddler Classic compile reference on a GitHub-hosted Windows runner.

.PARAMETER Version
The exact package and Fiddler file version required by the build.

.PARAMETER WingetPackageId
The exact WinGet package identifier attempted first.

.PARAMETER ChocolateySource
The Chocolatey package source used when WinGet is unavailable or unsuccessful.
#>
[CmdletBinding()]
param(
    [string]$Version = "5.0.20262.6151",
    [string]$WingetPackageId = "Telerik.Fiddler.Classic",
    [string]$ChocolateySource = "https://community.chocolatey.org/api/v2/"
)

$ErrorActionPreference = "Stop"

if ($env:GITHUB_ACTIONS -ne "true") {
    throw "This installer is restricted to GitHub Actions runners."
}

$fiddlerPath = Join-Path $env:LOCALAPPDATA "Programs/Fiddler/Fiddler.exe"

# Confirms that a package manager produced the exact compile reference expected by the build.
function Test-FiddlerInstallation {
    if (-not (Test-Path -LiteralPath $fiddlerPath -PathType Leaf)) {
        return $false
    }

    $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($fiddlerPath).FileVersion
    return $fileVersion -eq $Version
}

$installedBy = $null
$winget = Get-Command winget -ErrorAction SilentlyContinue
if ($winget) {
    Write-Host "Installing Fiddler Classic $Version with WinGet..."
    & $winget.Source install `
        --id $WingetPackageId `
        --version $Version `
        --exact `
        --source winget `
        --scope user `
        --architecture x64 `
        --silent `
        --accept-package-agreements `
        --accept-source-agreements `
        --disable-interactivity

    $wingetExitCode = $LASTEXITCODE
    if (Test-FiddlerInstallation) {
        $installedBy = "WinGet"
    }
    else {
        Write-Warning "WinGet did not install the required Fiddler Classic version (exit code $wingetExitCode)."
    }
}
else {
    Write-Warning "WinGet is unavailable on this runner."
}

if (-not $installedBy) {
    $chocolatey = Get-Command choco -ErrorAction SilentlyContinue
    if (-not $chocolatey) {
        throw "WinGet was unsuccessful and Chocolatey is unavailable on this Windows runner."
    }

    Write-Host "Installing Fiddler Classic $Version with Chocolatey..."
    & $chocolatey.Source install fiddler `
        --version $Version `
        --yes `
        --no-progress `
        --limit-output `
        --source $ChocolateySource

    $chocolateyExitCode = $LASTEXITCODE
    $successfulExitCodes = @(0, 1641, 3010)
    if ($successfulExitCodes -notcontains $chocolateyExitCode) {
        throw "Chocolatey failed to install Fiddler Classic with exit code $chocolateyExitCode."
    }

    if (-not (Test-FiddlerInstallation)) {
        throw "Chocolatey completed but did not install Fiddler Classic $Version at '$fiddlerPath'."
    }

    $installedBy = "Chocolatey"
}

Write-Host "Fiddler Classic $Version is available at $fiddlerPath (installed by $installedBy)."
