<#
.SYNOPSIS
Installs a self-contained Fiddler Classic CLI release for the current Windows user.

.DESCRIPTION
Uses an adjacent published payload when available. It can also install a local release ZIP or
download a checksum-verified release from GitHub. Each version is installed in its own directory
under the current user's local application data directory.

.PARAMETER PackagePath
The path to a release ZIP or an extracted self-contained publish directory.

.PARAMETER Repository
The trusted GitHub repository in owner/name form. When omitted, the installer attempts to read an
origin URL from the Git checkout containing this skill.

.PARAMETER Version
The GitHub release tag to install. The default value selects the latest published release.

.PARAMETER InstallRoot
The parent directory for versioned CLI installations.

.PARAMETER NoPathUpdate
Skips current-user PATH changes. This is useful for isolated tests and managed environments.
#>
[CmdletBinding()]
param(
    [string]$PackagePath,
    [ValidatePattern("^[^/\\\s]+/[^/\\\s]+$")]
    [string]$Repository,
    [string]$Version = "latest",
    [string]$InstallRoot = "$env:LOCALAPPDATA/Programs/FiddlerClassicCLI",
    [switch]$NoPathUpdate
)

$ErrorActionPreference = "Stop"
$assetName = "fiddler-classic-win-x64.zip"
$checksumName = "SHA256SUMS"
$temporaryDirectory = $null

# Walks from the skill directory toward the filesystem root to find a published CLI payload.
function Find-LocalPayload {
    $current = Get-Item -LiteralPath $PSScriptRoot
    while ($null -ne $current) {
        $directExecutable = Join-Path $current.FullName "fiddler-classic.exe"
        if (Test-Path -LiteralPath $directExecutable -PathType Leaf) {
            return $current.FullName
        }

        $sourcePublishDirectory = Join-Path $current.FullName "artifacts/publish/win-x64"
        if (Test-Path -LiteralPath "$sourcePublishDirectory/fiddler-classic.exe" -PathType Leaf) {
            return $sourcePublishDirectory
        }

        $current = $current.Parent
    }

    return $null
}

# Reads a GitHub owner/name identifier from the nearest Git origin without changing the checkout.
function Find-GitHubRepository {
    $current = Get-Item -LiteralPath $PSScriptRoot
    while ($null -ne $current) {
        if (Test-Path -LiteralPath (Join-Path $current.FullName ".git")) {
            $origin = & git -C $current.FullName remote get-url origin 2>$null
            if ($LASTEXITCODE -eq 0 -and $origin) {
                $match = [regex]::Match(
                    $origin.Trim(),
                    "github\.com(?::|/)(?<repository>[^/\s]+/[^/\s]+?)(?:\.git)?$")
                if ($match.Success) {
                    return $match.Groups["repository"].Value
                }
            }

            return $null
        }

        $current = $current.Parent
    }

    return $null
}

# Returns request headers for public or token-authenticated GitHub release API calls.
function Get-GitHubHeaders {
    $headers = @{
        Accept = "application/vnd.github+json"
        "User-Agent" = "fiddler-classic-installer"
        "X-GitHub-Api-Version" = "2022-11-28"
    }

    if ($env:GITHUB_TOKEN) {
        $headers.Authorization = "Bearer $env:GITHUB_TOKEN"
    }

    return $headers
}

# Verifies that a downloaded release asset has the exact digest listed for its filename.
function Confirm-PackageChecksum {
    param(
        [Parameter(Mandatory)]
        [string]$ArchivePath,
        [Parameter(Mandatory)]
        [string]$ManifestPath
    )

    $archiveName = [System.IO.Path]::GetFileName($ArchivePath)
    $expectedHash = $null
    foreach ($line in Get-Content -LiteralPath $ManifestPath) {
        if ($line -match "^([A-Fa-f0-9]{64})\s+\*?(.+)$" -and $matches[2].Trim() -eq $archiveName) {
            $expectedHash = $matches[1].ToLowerInvariant()
            break
        }
    }

    if (-not $expectedHash) {
        throw "The checksum manifest does not contain an exact entry for '$archiveName'."
    }

    $actualHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Checksum verification failed for '$archiveName'."
    }

    Write-Host "Verified SHA-256 for $archiveName."
}

# Resolves a release through the GitHub API and downloads both the package and checksum manifest.
function Receive-GitHubPackage {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryName,
        [Parameter(Mandatory)]
        [string]$ReleaseVersion,
        [Parameter(Mandatory)]
        [string]$DestinationDirectory
    )

    $escapedVersion = [uri]::EscapeDataString($ReleaseVersion)
    if ($ReleaseVersion -eq "latest") {
        $releaseUri = "https://api.github.com/repos/$RepositoryName/releases/latest"
    }
    else {
        $releaseUri = "https://api.github.com/repos/$RepositoryName/releases/tags/$escapedVersion"
    }

    $headers = Get-GitHubHeaders
    Write-Host "Resolving release from $RepositoryName..."
    $release = Invoke-RestMethod -Uri $releaseUri -Headers $headers -Method Get
    $archiveAsset = @($release.assets | Where-Object { $_.name -eq $assetName })
    $checksumAsset = @($release.assets | Where-Object { $_.name -eq $checksumName })
    if ($archiveAsset.Count -ne 1 -or $checksumAsset.Count -ne 1) {
        throw "The release must contain exactly one '$assetName' asset and one '$checksumName' asset."
    }

    $archivePath = Join-Path $DestinationDirectory $assetName
    $manifestPath = Join-Path $DestinationDirectory $checksumName
    Invoke-WebRequest -Uri $archiveAsset[0].browser_download_url -Headers $headers -OutFile $archivePath
    Invoke-WebRequest -Uri $checksumAsset[0].browser_download_url -Headers $headers -OutFile $manifestPath
    Confirm-PackageChecksum -ArchivePath $archivePath -ManifestPath $manifestPath
    return $archivePath
}

# Expands a release archive and accepts either a root payload or one containing directory.
function Expand-PackagePayload {
    param(
        [Parameter(Mandatory)]
        [string]$ArchivePath,
        [Parameter(Mandatory)]
        [string]$DestinationDirectory
    )

    $expandedDirectory = Join-Path $DestinationDirectory "expanded"
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $expandedDirectory -Force
    if (Test-Path -LiteralPath "$expandedDirectory/fiddler-classic.exe" -PathType Leaf) {
        return $expandedDirectory
    }

    $children = @(Get-ChildItem -LiteralPath $expandedDirectory -Directory)
    if ($children.Count -eq 1 -and
        (Test-Path -LiteralPath (Join-Path $children[0].FullName "fiddler-classic.exe") -PathType Leaf)) {
        return $children[0].FullName
    }

    throw "The release archive does not contain a self-contained fiddler-classic.exe payload."
}

# Executes the candidate CLI and returns its version only when the process exits successfully.
function Get-PayloadVersion {
    param(
        [Parameter(Mandatory)]
        [string]$PayloadDirectory
    )

    $executable = Join-Path $PayloadDirectory "fiddler-classic.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "The CLI executable was not found in '$PayloadDirectory'."
    }

    $reportedVersion = (& $executable --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $reportedVersion) {
        throw "The candidate fiddler-classic.exe did not report a valid version."
    }

    return $reportedVersion
}

# Replaces managed CLI PATH entries with the newly installed version for this user and process.
function Set-ManagedPath {
    param(
        [Parameter(Mandatory)]
        [string]$InstalledDirectory
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\', '/')
    $managedPrefix = "$normalizedRoot\"

    $userEntries = @([Environment]::GetEnvironmentVariable("Path", "User") -split ";" |
        Where-Object { $_ -and -not ([System.IO.Path]::GetFullPath($_).StartsWith($managedPrefix, [StringComparison]::OrdinalIgnoreCase)) })
    $newUserPath = (@($InstalledDirectory) + $userEntries) -join ";"
    [Environment]::SetEnvironmentVariable("Path", $newUserPath, "User")

    $processEntries = @($env:Path -split ";" |
        Where-Object { $_ -and -not ([System.IO.Path]::GetFullPath($_).StartsWith($managedPrefix, [StringComparison]::OrdinalIgnoreCase)) })
    $env:Path = (@($InstalledDirectory) + $processEntries) -join ";"
}

if ($env:OS -ne "Windows_NT" -or -not [Environment]::Is64BitOperatingSystem) {
    throw "fiddler-classic requires 64-bit Windows."
}

if ($PackagePath -and $Repository) {
    throw "Specify PackagePath or Repository, not both."
}

try {
    $payloadDirectory = $null
    if ($PackagePath) {
        $resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
        if (Test-Path -LiteralPath $resolvedPackage -PathType Container) {
            $payloadDirectory = $resolvedPackage
        }
        elseif ([System.IO.Path]::GetExtension($resolvedPackage) -ieq ".zip") {
            $manifestPath = Join-Path (Split-Path -Parent $resolvedPackage) $checksumName
            if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
                throw "A local release ZIP requires a sibling '$checksumName' manifest."
            }

            Confirm-PackageChecksum -ArchivePath $resolvedPackage -ManifestPath $manifestPath
            $temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
            New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
            $payloadDirectory = Expand-PackagePayload -ArchivePath $resolvedPackage -DestinationDirectory $temporaryDirectory
        }
        else {
            throw "PackagePath must identify a release ZIP or an extracted publish directory."
        }
    }
    else {
        $payloadDirectory = Find-LocalPayload
        if (-not $payloadDirectory) {
            if (-not $Repository) {
                $Repository = Find-GitHubRepository
            }

            if (-not $Repository) {
                throw "No local payload or GitHub origin was found. Supply PackagePath or a trusted Repository in owner/name form."
            }

            $temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
            New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
            $archivePath = Receive-GitHubPackage `
                -RepositoryName $Repository `
                -ReleaseVersion $Version `
                -DestinationDirectory $temporaryDirectory
            $payloadDirectory = Expand-PackagePayload -ArchivePath $archivePath -DestinationDirectory $temporaryDirectory
        }
    }

    $reportedVersion = Get-PayloadVersion -PayloadDirectory $payloadDirectory
    if ($reportedVersion -notmatch "^[0-9]+(?:\.[0-9]+){1,3}(?:[-.][0-9A-Za-z]+)*$" -or
        $reportedVersion.Contains("..")) {
        throw "The candidate CLI reported an invalid version directory name."
    }

    $safeVersion = [regex]::Replace($reportedVersion, "[^0-9A-Za-z._-]", "_")
    $installedDirectory = Join-Path ([System.IO.Path]::GetFullPath($InstallRoot)) $safeVersion
    New-Item -ItemType Directory -Path $installedDirectory -Force | Out-Null

    foreach ($item in Get-ChildItem -LiteralPath $payloadDirectory -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $installedDirectory -Recurse -Force
    }

    $installedExecutable = Join-Path $installedDirectory "fiddler-classic.exe"
    $installedVersion = (& $installedExecutable --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $installedVersion -ne $reportedVersion) {
        throw "The installed CLI failed version verification."
    }

    if (-not $NoPathUpdate) {
        Set-ManagedPath -InstalledDirectory $installedDirectory
    }

    Write-Host "Installed fiddler-classic $installedVersion to $installedDirectory"
    if ($NoPathUpdate) {
        Write-Host "PATH was not changed."
    }
    else {
        Write-Host "Updated the current-user PATH and this PowerShell session."
    }

    Write-Output $installedExecutable
}
finally {
    if ($temporaryDirectory -and (Test-Path -LiteralPath $temporaryDirectory)) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
