<#
.SYNOPSIS
Tests executable-only publication with a synthetic PE fixture and a fake GitHub CLI.
No builds, executable launches, or GitHub requests are made.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$testRoot = (New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) (
            'fiddler-release-publication-' + [guid]::NewGuid().ToString('N')))).FullName
$releaseRoot = (New-Item -ItemType Directory -Path (Join-Path $testRoot 'release')).FullName
$assetPath = Join-Path $releaseRoot 'fiddler-classic-cli.exe'
$previousExitCode = $global:LASTEXITCODE
$publicationTestState = @{ Calls = $null; Tag = ''; Status = 0; LookupExit = 0; MutationExit = 0; Body = ''; Passed = 0 }

# Shadows the executable for this script's child calls and rejects unexpected commands. No calls reach GitHub.
function gh {
    $publicationTestState.Calls.Add(@($args))
    if ($args[0] -eq 'api') {
        $expected = @('api', "repos/test-owner/test-repo/releases/tags/$($publicationTestState.Tag)", '--include')
        if (($args | ConvertTo-Json -Compress) -cne ($expected | ConvertTo-Json -Compress)) {
            throw 'Unexpected release lookup arguments.'
        }
        $global:LASTEXITCODE = $publicationTestState.LookupExit
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

# Checks exact calls, including one-asset selection and the absence of overwrite, deletion, or metadata edits.
function Test-PublicationCase {
    param(
        [string]$Name, [int]$Status = 200, [object[]]$Existing = @(),
        [string[]]$ExpectedMutation = @(), [string]$ExpectedError,
        [string]$Tag = 'v1.2.3', [int]$MutationExit = 0, [int]$LookupExit = -1,
        [switch]$Immutable, [string]$Body, [string]$ExpectedSha256 = $fixtureHash,
        [switch]$BeforeLookup
    )
    $publicationTestState.Calls = [Collections.Generic.List[object]]::new()
    $publicationTestState.Tag = $Tag
    $publicationTestState.Status = $Status
    $publicationTestState.LookupExit = if ($LookupExit -ne -1) { $LookupExit } elseif ($Status -eq 200) { 0 } else { 1 }
    $publicationTestState.MutationExit = $MutationExit
    $publicationTestState.Body = if ($Body) { $Body } else {
        @{
            tag_name = $Tag
            assets = @($Existing)
            immutable = $Immutable.IsPresent
            name = 'User title'
            body = 'User notes'
            draft = $true
            prerelease = $false
        } | ConvertTo-Json -Depth 5 -Compress
    }
    $failure = $null
    try {
        & "$PSScriptRoot/publish-release.ps1" -Tag $Tag -Repository 'test-owner/test-repo' `
            -ReleaseDirectory $releaseRoot -ExpectedSha256 $ExpectedSha256
    }
    catch { $failure = $_.Exception.Message }
    if ($ExpectedError) {
        if (-not $failure -or $failure -notlike "*$ExpectedError*") {
            throw "Case '$Name' expected '$ExpectedError'. Received '$failure'."
        }
    }
    elseif ($failure) { throw "Case '$Name' unexpectedly failed: $failure" }
    $expectedCallCount = if ($BeforeLookup) { 0 } elseif ($ExpectedMutation.Count) { 2 } else { 1 }
    if ($publicationTestState.Calls.Count -ne $expectedCallCount -or ($ExpectedMutation.Count -and
            ($publicationTestState.Calls[1] | ConvertTo-Json -Compress) -cne ($ExpectedMutation | ConvertTo-Json -Compress))) {
        throw "Case '$Name' made unexpected GitHub calls."
    }
    $publicationTestState.Passed++
    Write-Host "PASS $Name"
}

try {
    # An inert, little-endian Windows x64 PE header satisfies structural validation on every platform.
    $bytes = [byte[]]::new(256)
    $bytes[0] = 0x4d
    $bytes[1] = 0x5a
    $bytes[0x3c] = 128
    $bytes[128] = 0x50
    $bytes[129] = 0x45
    $bytes[132] = 0x64
    $bytes[133] = 0x86
    $bytes[152] = 0x0b
    $bytes[153] = 0x02
    [IO.File]::WriteAllBytes($assetPath, $bytes)
    $fixtureHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $assetRecord = @{ name = 'fiddler-classic-cli.exe'; state = 'uploaded'; digest = 'sha256:' + $fixtureHash }
    $legacyRecords = @(
        @{ name = 'fiddler-classic-cli-windows-x64.zip'; state = 'uploaded'; digest = $null }
        @{ name = 'SHA256SUMS'; state = 'uploaded'; digest = $null }
    )

    $requiredDigest = (Get-Command "$PSScriptRoot/publish-release.ps1").Parameters['ExpectedSha256']
    if (-not @($requiredDigest.Attributes | Where-Object { $_ -is [Management.Automation.ParameterAttribute] -and $_.Mandatory }).Count) {
        throw 'The publisher must require ExpectedSha256.'
    }
    Test-PublicationCase -Name trusted-digest-mismatch -ExpectedSha256 ('0' * 64) -BeforeLookup -ExpectedError 'SHA-256'
    foreach ($invalidDigest in @('', ('f' * 63), ('f' * 65), ('g' * 64), (('f' * 64) + "`n"))) {
        Test-PublicationCase -Name invalid-trusted-digest -ExpectedSha256 $invalidDigest -BeforeLookup -ExpectedError 'ExpectedSha256'
    }

    $upload = @('release', 'upload', 'v1.2.3', $assetPath, '--repo', 'test-owner/test-repo')
    Test-PublicationCase -Name existing-release-preserves-metadata -ExpectedMutation $upload
    Test-PublicationCase -Name existing-release-upload-failure -ExpectedMutation $upload -MutationExit 1 -ExpectedError 'publication failed'
    Test-PublicationCase -Name identical-rerun -Existing @($assetRecord)
    Test-PublicationCase -Name uppercase-trusted-digest -Existing @($assetRecord) -ExpectedSha256 $fixtureHash.ToUpperInvariant()
    Test-PublicationCase -Name immutable-identical-rerun -Existing @($assetRecord) -Immutable
    Test-PublicationCase -Name immutable-missing-executable -Immutable -ExpectedError 'is immutable'
    Test-PublicationCase -Name old-assets-preserved-on-upload -Existing $legacyRecords -ExpectedMutation $upload
    Test-PublicationCase -Name old-assets-preserved-on-rerun -Existing (@($assetRecord) + $legacyRecords)
    Test-PublicationCase -Name immutable-old-assets -Existing $legacyRecords -Immutable -ExpectedError 'is immutable'
    Test-PublicationCase -Name duplicate-executable -Existing @($assetRecord, $assetRecord) -ExpectedError 'no verifiable SHA-256 digest'

    foreach ($state in @('different', 'missing-digest', 'incomplete')) {
        $record = @{ name = $assetRecord.name; state = 'uploaded'; digest = $assetRecord.digest }
        if ($state -eq 'different') { $record.digest = 'sha256:' + ('0' * 64) }
        if ($state -eq 'missing-digest') { $record.digest = $null }
        if ($state -eq 'incomplete') { $record.state = 'starter' }
        Test-PublicationCase -Name "existing-executable-$state" -Existing (@($record) + $legacyRecords) `
            -ExpectedError 'no verifiable SHA-256 digest'
    }
    foreach ($tag in @('v1.2.3', 'v1.2.3-preview.1')) {
        $create = @('release', 'create', $tag, $assetPath) + @(
            '--repo', 'test-owner/test-repo', '--verify-tag', '--generate-notes', '--title', $tag)
        if ($tag.Contains('-')) { $create += '--prerelease' }
        Test-PublicationCase -Name "create-$tag" -Status 404 -Tag $tag -ExpectedMutation $create
        Test-PublicationCase -Name "create-failure-$tag" -Status 404 -Tag $tag -ExpectedMutation $create -MutationExit 1 -ExpectedError 'publication failed'
    }
    Test-PublicationCase -Name existing-prerelease-preserves-metadata -Tag 'v1.2.3-preview.1' -ExpectedMutation @(
        'release', 'upload', 'v1.2.3-preview.1', $assetPath, '--repo', 'test-owner/test-repo')
    foreach ($status in @(0, 200, 401, 403, 500)) {
        Test-PublicationCase -Name "lookup-failure-$status" -Status $status -LookupExit 1 -ExpectedError 'lookup failed'
    }
    foreach ($status in @(0, 401, 403, 404, 500)) {
        Test-PublicationCase -Name "unexpected-success-status-$status" -Status $status -LookupExit 0 -ExpectedError 'invalid release response'
    }
    Test-PublicationCase -Name malformed-response -Body '{invalid' -ExpectedError 'JSON'
    Test-PublicationCase -Name wrong-tag -Body '{"tag_name":"v9.9.9","assets":[]}' -ExpectedError 'different release tag'
    Test-PublicationCase -Name missing-assets -Body '{"tag_name":"v1.2.3"}' -ExpectedError 'invalid release asset list'
    Test-PublicationCase -Name null-assets -Body '{"tag_name":"v1.2.3","assets":null}' -ExpectedError 'invalid release asset list'
    Test-PublicationCase -Name malformed-assets -Body '{"tag_name":"v1.2.3","assets":{}}' -ExpectedError 'invalid release asset list'
    Write-Host "Passed $($publicationTestState.Passed) publication fixtures without GitHub requests."
}
finally {
    $global:LASTEXITCODE = $previousExitCode
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'fiddler-release-publication-*') {
        throw 'Refusing to remove an unexpected publication test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
