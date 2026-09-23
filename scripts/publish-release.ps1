<#
.SYNOPSIS
Publishes the verified executable while preserving existing release metadata and assets.

.PARAMETER Tag
The existing version tag to publish.

.PARAMETER Repository
The GitHub repository in owner/name form.

.PARAMETER ReleaseDirectory
The directory containing only fiddler-classic-cli.exe.

.PARAMETER ExpectedSha256
The trusted SHA-256 digest captured from the tested build before artifact upload.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^v[0-9]+(?:\.[0-9]+){2}(?:[-.][0-9A-Za-z]+)*$')]
    [string]$Tag,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository,
    [Parameter(Mandatory)][ValidatePattern('\A[A-Fa-f0-9]{64}\z')]
    [string]$ExpectedSha256,
    [string]$ReleaseDirectory = "$PSScriptRoot/../artifacts/release"
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
& "$PSScriptRoot/verify-release.ps1" -ReleaseDirectory $ReleaseDirectory -ExpectedSha256 $ExpectedSha256
$releaseRoot = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$assetName = 'fiddler-classic-cli.exe'
$assetPath = Join-Path $releaseRoot $assetName

# Only an HTTP 404 means the release is absent. Authentication and network errors must stop publication.
$response = @(& gh api "repos/$Repository/releases/tags/$Tag" --include 2>$null)
$lookupExitCode = $LASTEXITCODE
if ($lookupExitCode -ne 0) {
    if ($response.Count -eq 0 -or $response[0] -notmatch '^HTTP/[\d.]+ 404\b') {
        throw "GitHub release lookup failed with exit code $lookupExitCode. Check authentication and connectivity."
    }
    $arguments = @('release', 'create', $Tag, $assetPath) + @(
        '--repo', $Repository, '--verify-tag', '--generate-notes', '--title', $Tag)
    if ($Tag.Contains('-')) { $arguments += '--prerelease' }
}
else {
    if ($response.Count -eq 0 -or $response[0] -notmatch '^HTTP/[\d.]+ 200\b') {
        throw 'GitHub returned an invalid release response.'
    }
    $sections = ($response -join "`n") -split '\r?\n\r?\n', 2
    if ($sections.Count -ne 2) { throw 'GitHub returned an invalid release response.' }
    $release = $sections[1] | ConvertFrom-Json
    if ($release.tag_name -cne $Tag) { throw 'GitHub returned a different release tag.' }
    if ($release.assets -isnot [array]) { throw 'GitHub returned an invalid release asset list.' }

    # Leave older remote assets untouched. A matching executable makes reruns read-only.
    $existing = @($release.assets | Where-Object { $_.name -ceq $assetName })
    if ($existing.Count -ne 0) {
        $digest = 'sha256:' + $ExpectedSha256.ToLowerInvariant()
        if ($existing.Count -ne 1 -or $existing[0].state -ne 'uploaded' -or $existing[0].digest -ne $digest) {
            throw "Release asset '$assetName' differs or has no verifiable SHA-256 digest. Existing files were not replaced."
        }
        Write-Host "Release '$Tag' already contains the verified executable."
        return
    }
    if ($release.immutable) {
        throw "Release '$Tag' is immutable and is missing '$assetName'. Publish a new version with its executable attached."
    }
    $arguments = @('release', 'upload', $Tag, $assetPath, '--repo', $Repository)
}

& gh @arguments
if ($LASTEXITCODE -ne 0) {
    throw "GitHub release publication failed with exit code $LASTEXITCODE. Existing release metadata was not changed."
}
