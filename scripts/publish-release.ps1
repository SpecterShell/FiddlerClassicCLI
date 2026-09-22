<#
.SYNOPSIS
Publishes verified assets, preserving existing release metadata and matching assets.

.PARAMETER Tag
The existing version tag to publish.

.PARAMETER Repository
The GitHub repository in owner/name form.

.PARAMETER ReleaseDirectory
The directory containing the release ZIP and SHA256SUMS.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^v[0-9]+(?:\.[0-9]+){2}(?:[-.][0-9A-Za-z]+)*$')]
    [string]$Tag,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository,
    [string]$ReleaseDirectory = "$PSScriptRoot/../artifacts/release"
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
& "$PSScriptRoot/verify-release.ps1" -ReleaseDirectory $ReleaseDirectory
$releaseRoot = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$assets = @('fiddler-classic-win-x64.zip', 'SHA256SUMS') | ForEach-Object { Join-Path $releaseRoot $_ }

# Only an HTTP 404 means the release is absent; authentication and network errors must stop publication.
$response = @(& gh api "repos/$Repository/releases/tags/$Tag" --include 2>$null)
$lookupExitCode = $LASTEXITCODE
if ($lookupExitCode -ne 0) {
    if ($response.Count -eq 0 -or $response[0] -notmatch '^HTTP/[\d.]+ 404\b') {
        throw "GitHub release lookup failed with exit code $lookupExitCode. Check authentication and connectivity."
    }
    $arguments = @('release', 'create', $Tag) + $assets + @(
        '--repo', $Repository, '--verify-tag', '--generate-notes', '--title', $Tag)
    if ($Tag.Contains('-')) { $arguments += '--prerelease' }
}
else {
    $sections = ($response -join "`n") -split '\r?\n\r?\n', 2
    if ($sections.Count -ne 2) { throw 'GitHub returned an invalid release response.' }
    $release = $sections[1] | ConvertFrom-Json
    if ($release.tag_name -cne $Tag) { throw 'GitHub returned a different release tag.' }

    # Compare every existing asset before uploading anything. Never clobber published bytes.
    $missingAssets = @()
    foreach ($asset in $assets) {
        $name = [IO.Path]::GetFileName($asset)
        $existing = @($release.assets | Where-Object { $_.name -ceq $name })
        if ($existing.Count -eq 0) {
            $missingAssets += $asset
            continue
        }
        $digest = 'sha256:' + (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($existing.Count -ne 1 -or $existing[0].state -ne 'uploaded' -or $existing[0].digest -ne $digest) {
            throw "Release asset '$name' differs or has no verifiable SHA-256 digest. Existing files were not replaced."
        }
    }
    if ($missingAssets.Count -eq 0) {
        Write-Host "Release '$Tag' already contains the verified assets."
        return
    }
    if ($release.immutable) {
        throw "Release '$Tag' is immutable and is missing required assets. Publish a new version with its assets attached."
    }
    $arguments = @('release', 'upload', $Tag) + $missingAssets + @('--repo', $Repository)
}

& gh @arguments
if ($LASTEXITCODE -ne 0) {
    throw "GitHub release publication failed with exit code $LASTEXITCODE. Existing release metadata was not changed."
}
