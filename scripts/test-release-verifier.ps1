<#
.SYNOPSIS
Exercises executable-only release validation with inert synthetic Windows x64 PE fixtures.
No fixture executables are launched.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$testDirectoryName = 'fiddler-release-verifier-' + [guid]::NewGuid().ToString('N')
$testRoot = (New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) $testDirectoryName)).FullName
$linkPaths = [Collections.Generic.List[string]]::new()
$testResults = @{ Passed = 0; Skipped = 0 }
$executableName = 'fiddler-classic-cli.exe'
$shapeError = 'must contain only fiddler-classic-cli.exe'
$peError = 'not a Windows x64 PE file'
$offsetError = 'invalid PE header offset'

# The little-endian fixture contains only the bounded headers inspected by the verifier.
$validBytes = [byte[]]::new(256)
$validBytes[0] = 0x4d
$validBytes[1] = 0x5a
$validBytes[0x3c] = 128
$validBytes[128] = 0x50
$validBytes[129] = 0x45
$validBytes[132] = 0x64
$validBytes[133] = 0x86
$validBytes[152] = 0x0b
$validBytes[153] = 0x02

# Creates an isolated fixture. An empty filename leaves its release directory empty.
function New-ReleaseFixture {
    param([string]$Name, [byte[]]$Bytes = $validBytes, [string]$FileName = $executableName)
    $directory = (New-Item -ItemType Directory -Path (Join-Path $testRoot $Name)).FullName
    if ($FileName) { [IO.File]::WriteAllBytes((Join-Path $directory $FileName), $Bytes) }
    return $directory
}

# Checks the rejection cause so setup errors cannot satisfy negative assertions.
function Test-VerifierCase {
    param([string]$Name, [string]$Directory, [string]$ExpectedError, [string]$ExpectedSha256)
    $arguments = @{ ReleaseDirectory = $Directory }
    if ($PSBoundParameters.ContainsKey('ExpectedSha256')) { $arguments.ExpectedSha256 = $ExpectedSha256 }
    $failure = $null
    try { & "$PSScriptRoot/verify-release.ps1" @arguments }
    catch { $failure = $_.Exception.Message }
    if ($ExpectedError) {
        if (-not $failure -or $failure -notlike "*$ExpectedError*") {
            throw "Case '$Name' expected '$ExpectedError'. Received '$failure'."
        }
    }
    elseif ($failure) { throw "Case '$Name' unexpectedly failed: $failure" }
    $testResults.Passed++
    Write-Host "PASS $Name"
}

# Link targets stay inside the test tree. Unsupported creation skips only the link-specific assertion.
function Test-SymlinkCase {
    param([string]$Name, [string]$TargetDirectory, [switch]$DirectoryLink)
    if ($DirectoryLink) {
        $directory = Join-Path $testRoot $Name
        $linkPath = $directory
        $targetPath = $TargetDirectory
        $expectedError = 'must be a regular directory'
    }
    else {
        $directory = New-ReleaseFixture -Name $Name -FileName ''
        $linkPath = Join-Path $directory $executableName
        $targetPath = Join-Path $TargetDirectory $executableName
        $expectedError = $shapeError
    }
    $linkPaths.Add($linkPath)
    try {
        $null = New-Item -ItemType SymbolicLink -Path $linkPath -Target $targetPath -ErrorAction Stop
    }
    catch {
        $testResults.Skipped++
        Write-Host "SKIP $Name (symbolic-link creation unavailable: $($_.Exception.Message))"
        return
    }
    Test-VerifierCase -Name $Name -Directory $directory -ExpectedError $expectedError
}

try {
    $validDirectory = New-ReleaseFixture -Name 'valid'
    $hash = (Get-FileHash -LiteralPath (Join-Path $validDirectory $executableName) -Algorithm SHA256).Hash
    Test-VerifierCase -Name 'sole-executable' -Directory $validDirectory
    Test-VerifierCase -Name 'expected-sha256' -Directory $validDirectory -ExpectedSha256 $hash.ToLowerInvariant()
    Test-VerifierCase -Name 'uppercase-sha256' -Directory $validDirectory -ExpectedSha256 $hash
    Test-VerifierCase -Name 'sha256-mismatch' -Directory $validDirectory -ExpectedSha256 ('0' * 64) `
        -ExpectedError 'SHA-256 verification failed'
    foreach ($invalidHash in @(('a' * 63), ('a' * 65), ('g' * 64), (('a' * 64) + "`n"))) {
        Test-VerifierCase -Name 'invalid-sha256' -Directory $validDirectory -ExpectedSha256 $invalidHash -ExpectedError 'ExpectedSha256'
    }

    Test-VerifierCase -Name 'missing-executable' -Directory (New-ReleaseFixture 'missing' -FileName '') -ExpectedError $shapeError
    Test-VerifierCase -Name 'renamed-executable' -Directory (New-ReleaseFixture 'renamed' -FileName 'fiddler-classic.exe') -ExpectedError $shapeError
    Test-VerifierCase -Name 'incorrect-filename-case' -Directory (New-ReleaseFixture 'filename-case' -FileName 'FIDDLER-CLASSIC-CLI.EXE') -ExpectedError $shapeError
    Test-VerifierCase -Name 'file-as-release-directory' -Directory (Join-Path $validDirectory $executableName) `
        -ExpectedError 'must be a regular directory'

    foreach ($length in @(0, 1, 63, 153)) {
        $bytes = [byte[]]::new($length)
        [Array]::Copy($validBytes, $bytes, $length)
        Test-VerifierCase -Name "truncated-$length" -Directory (New-ReleaseFixture "truncated-$length" -Bytes $bytes) -ExpectedError $peError
    }
    foreach ($change in @(
            @{ Name = 'dos-magic'; Offset = 0; Bytes = [byte[]]@(0, 0); Error = $peError }
            @{ Name = 'pe-magic'; Offset = 128; Bytes = [byte[]]@(0, 0, 0, 0); Error = $peError }
            @{ Name = 'machine-x86'; Offset = 132; Bytes = [byte[]]@(0x4c, 0x01); Error = $peError }
            @{ Name = 'machine-arm64'; Offset = 132; Bytes = [byte[]]@(0x64, 0xaa); Error = $peError }
            @{ Name = 'optional-magic-pe32'; Offset = 152; Bytes = [byte[]]@(0x0b, 0x01); Error = $peError }
            @{ Name = 'optional-magic-zero'; Offset = 152; Bytes = [byte[]]@(0, 0); Error = $peError }
            @{ Name = 'offset-negative'; Offset = 0x3c; Bytes = [byte[]]@(255, 255, 255, 255); Error = $offsetError }
            @{ Name = 'offset-zero'; Offset = 0x3c; Bytes = [byte[]]@(0, 0, 0, 0); Error = $offsetError }
            @{ Name = 'offset-inside-dos'; Offset = 0x3c; Bytes = [byte[]]@(63, 0, 0, 0); Error = $offsetError }
            @{ Name = 'offset-past-header-bound'; Offset = 0x3c; Bytes = [byte[]]@(231, 0, 0, 0); Error = $offsetError }
            @{ Name = 'offset-at-eof'; Offset = 0x3c; Bytes = [byte[]]@(0, 1, 0, 0); Error = $offsetError }
            @{ Name = 'offset-int32-max'; Offset = 0x3c; Bytes = [byte[]]@(255, 255, 255, 127); Error = $offsetError }
        )) {
        $bytes = [byte[]]$validBytes.Clone()
        $change.Bytes.CopyTo($bytes, $change.Offset)
        Test-VerifierCase -Name $change.Name -Directory (New-ReleaseFixture $change.Name -Bytes $bytes) -ExpectedError $change.Error
    }

    foreach ($extra in @('fiddler-classic-cli-windows-x64.zip', 'SHA256SUMS', 'Fiddler.exe', 'coreclr.dll',
            'fiddler-classic-cli.pdb', 'fiddler-classic-cli.deps.json', 'fiddler-classic-cli.runtimeconfig.json',
            'fiddler-classic-cli.exe.config')) {
        $name = "extra-$extra"
        $directory = New-ReleaseFixture $name
        [IO.File]::WriteAllBytes((Join-Path $directory $extra), [byte[]]::new(0))
        Test-VerifierCase -Name $name -Directory $directory -ExpectedError $shapeError
    }
    $directory = New-ReleaseFixture 'extra-directory'
    $null = New-Item -ItemType Directory -Path (Join-Path $directory 'nested')
    Test-VerifierCase -Name 'extra-directory' -Directory $directory -ExpectedError $shapeError

    $directory = New-ReleaseFixture 'directory-as-executable' -FileName ''
    $null = New-Item -ItemType Directory -Path (Join-Path $directory $executableName)
    Test-VerifierCase -Name 'directory-as-executable' -Directory $directory -ExpectedError $shapeError

    $directory = New-ReleaseFixture 'hidden-extra-file'
    $hiddenPath = Join-Path $directory '.hidden'
    [IO.File]::WriteAllBytes($hiddenPath, [byte[]]::new(0))
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        [IO.File]::SetAttributes($hiddenPath, [IO.FileAttributes]::Hidden)
    }
    if (-not ((Get-Item -LiteralPath $hiddenPath -Force).Attributes -band [IO.FileAttributes]::Hidden)) {
        throw 'The hidden-file fixture was not hidden on this platform.'
    }
    Test-VerifierCase -Name 'hidden-extra-file' -Directory $directory -ExpectedError $shapeError

    Test-SymlinkCase -Name 'executable-symlink' -TargetDirectory $validDirectory
    Test-SymlinkCase -Name 'release-directory-symlink' -TargetDirectory $validDirectory -DirectoryLink
    Write-Host "Passed $($testResults.Passed) verifier fixtures. Skipped $($testResults.Skipped) unsupported symlink fixtures."
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -cne $testDirectoryName -or
        ((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing to remove an unexpected verifier test directory.'
    }
    # Unlink known links without recursion before removing the private fixture tree.
    foreach ($linkPath in $linkPaths) {
        if (-not [IO.Path]::GetFullPath($linkPath).StartsWith(
                $resolved + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove a link outside the verifier test directory.'
        }
        $link = Get-Item -LiteralPath $linkPath -Force -ErrorAction SilentlyContinue
        if ($link) {
            if (-not ($link.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Refusing to remove a verifier fixture link that was replaced.'
            }
            Remove-Item -LiteralPath $linkPath -Force
        }
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
