<#
.SYNOPSIS
Tests publication decisions with a fake GitHub CLI and an existing local release package.

.PARAMETER ReleaseDirectory
The directory containing a package produced by scripts/package.ps1. No GitHub requests are made.
#>
[CmdletBinding()]
param([string]$ReleaseDirectory = "$PSScriptRoot/../artifacts/release")

$ErrorActionPreference = 'Stop'
$releaseRoot = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$assetPaths = @('fiddler-classic-win-x64.zip', 'SHA256SUMS') | ForEach-Object { Join-Path $releaseRoot $_ }
$assetRecords = @($assetPaths | ForEach-Object {
        @{ name = [IO.Path]::GetFileName($_); state = 'uploaded';
            digest = 'sha256:' + (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
$previousExitCode = $global:LASTEXITCODE
$publicationTestState = @{ Calls = $null; Tag = ''; Status = 0; MutationExit = 0; Body = '' }

# Shadows the executable for this script's child calls. Unexpected commands fail rather than reach GitHub.
function gh {
    $publicationTestState.Calls.Add(@($args))
    if ($args[0] -eq 'api') {
        $expected = @('api', "repos/test-owner/test-repo/releases/tags/$($publicationTestState.Tag)", '--include')
        if (($args | ConvertTo-Json -Compress) -cne ($expected | ConvertTo-Json -Compress)) {
            throw 'Unexpected release lookup arguments.'
        }
        $global:LASTEXITCODE = if ($publicationTestState.Status -eq 200) { 0 } else { 1 }
        if ($publicationTestState.Status -eq 0) { return }
        "HTTP/2.0 $($publicationTestState.Status)"
        'Content-Type: application/json'
        ''
        $publicationTestState.Body
        return
    }
    if ($args[0] -ne 'release' -or $args[1] -notin @('create', 'upload')) {
        throw 'Unexpected GitHub mutation command.'
    }
    $global:LASTEXITCODE = $publicationTestState.MutationExit
}

# Checks exact mutation arguments, including asset selection and the absence of overwrite or metadata edits.
function Test-PublicationCase {
    param(
        [string]$Name, [int]$Status = 200, [object[]]$Existing = @(),
        [string[]]$ExpectedMutation = @(), [string]$ExpectedError,
        [string]$Tag = 'v1.2.3', [int]$MutationExit = 0,
        [switch]$Immutable, [string]$Body
    )
    $publicationTestState.Calls = [Collections.Generic.List[object]]::new()
    $publicationTestState.Tag = $Tag
    $publicationTestState.Status = $Status
    $publicationTestState.MutationExit = $MutationExit
    $publicationTestState.Body = if ($Body) { $Body } else {
        @{ tag_name = $Tag; assets = @($Existing); immutable = $Immutable.IsPresent;
            name = 'User title'; body = 'User notes'; draft = $true; prerelease = $false } |
            ConvertTo-Json -Depth 5 -Compress
    }
    $failure = $null
    try {
        & "$PSScriptRoot/publish-release.ps1" -Tag $Tag -Repository 'test-owner/test-repo' -ReleaseDirectory $releaseRoot
    }
    catch { $failure = $_.Exception.Message }
    if ($ExpectedError) {
        if (-not $failure -or $failure -notlike "*$ExpectedError*") {
            throw "Case '$Name' expected '$ExpectedError'; received '$failure'."
        }
    }
    elseif ($failure) { throw "Case '$Name' unexpectedly failed: $failure" }
    $expectedCallCount = if ($ExpectedMutation.Count) { 2 } else { 1 }
    if ($publicationTestState.Calls.Count -ne $expectedCallCount -or ($ExpectedMutation.Count -and
            ($publicationTestState.Calls[1] | ConvertTo-Json -Compress) -cne ($ExpectedMutation | ConvertTo-Json -Compress))) {
        throw "Case '$Name' made unexpected GitHub calls."
    }
    Write-Host "PASS $Name"
}

try {
    $upload = @('release', 'upload', 'v1.2.3') + $assetPaths + @('--repo', 'test-owner/test-repo')
    Test-PublicationCase -Name existing-release -ExpectedMutation $upload
    Test-PublicationCase -Name existing-release-upload-failure -ExpectedMutation $upload -MutationExit 1 -ExpectedError 'publication failed'
    Test-PublicationCase -Name identical-rerun -Existing $assetRecords
    Test-PublicationCase -Name immutable-identical-rerun -Existing $assetRecords -Immutable
    Test-PublicationCase -Name immutable-missing-assets -Immutable -ExpectedError 'is immutable'
    Test-PublicationCase -Name partial-upload -Existing @($assetRecords[0]) -ExpectedMutation @(
        'release', 'upload', 'v1.2.3', $assetPaths[1], '--repo', 'test-owner/test-repo')

    foreach ($state in @('different', 'missing-digest', 'incomplete')) {
        $record = @{ name = 'SHA256SUMS'; state = 'uploaded'; digest = $assetRecords[1].digest }
        if ($state -eq 'different') { $record.digest = 'sha256:' + ('0' * 64) }
        if ($state -eq 'missing-digest') { $record.digest = $null }
        if ($state -eq 'incomplete') { $record.state = 'starter' }
        Test-PublicationCase -Name $state -Existing @($record) -ExpectedError 'no verifiable SHA-256 digest'
    }
    foreach ($tag in @('v1.2.3', 'v1.2.3-preview.1')) {
        $create = @('release', 'create', $tag) + $assetPaths + @(
            '--repo', 'test-owner/test-repo', '--verify-tag', '--generate-notes', '--title', $tag)
        if ($tag.Contains('-')) { $create += '--prerelease' }
        Test-PublicationCase -Name "create-$tag" -Status 404 -Tag $tag -ExpectedMutation $create
        Test-PublicationCase -Name "create-failure-$tag" -Status 404 -Tag $tag -ExpectedMutation $create -MutationExit 1 -ExpectedError 'publication failed'
    }
    foreach ($status in @(0, 401, 403, 500)) {
        Test-PublicationCase -Name "lookup-failure-$status" -Status $status -ExpectedError 'lookup failed'
    }
    Test-PublicationCase -Name malformed-response -Body '{invalid' -ExpectedError 'JSON'
    Test-PublicationCase -Name wrong-tag -Body '{"tag_name":"v9.9.9","assets":[]}' -ExpectedError 'different release tag'
}
finally { $global:LASTEXITCODE = $previousExitCode }
