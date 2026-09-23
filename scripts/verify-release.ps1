<#
.SYNOPSIS
Verifies the sole Windows x64 executable before artifact upload or publication.

.PARAMETER ReleaseDirectory
The directory containing only fiddler-classic-cli.exe.

.PARAMETER ExpectedSha256
The trusted build digest, required by callers that download or publish an artifact.
#>
[CmdletBinding()]
param(
    [string]$ReleaseDirectory = "$PSScriptRoot/../artifacts/release",
    [ValidatePattern('\A[A-Fa-f0-9]{64}\z')]
    [string]$ExpectedSha256
)

$ErrorActionPreference = 'Stop'
$directory = Get-Item -LiteralPath $ReleaseDirectory -Force
if (-not $directory.PSIsContainer -or
    ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The release directory must be a regular directory.'
}
$entries = @(Get-ChildItem -LiteralPath $directory.FullName -Force)
if ($entries.Count -ne 1 -or $entries[0].Name -cne 'fiddler-classic-cli.exe' -or
    $entries[0].PSIsContainer -or ($entries[0].Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The release directory must contain only fiddler-classic-cli.exe, with no links or additional files.'
}

# Check bounded PE headers on every verifier platform, including the Linux release job.
$executable = $entries[0]
$stream = [IO.File]::OpenRead($executable.FullName)
$reader = [IO.BinaryReader]::new($stream)
try {
    if ($stream.Length -lt 154 -or $reader.ReadUInt16() -ne 0x5A4D) {
        throw 'The release executable is not a Windows x64 PE file.'
    }
    $stream.Position = 0x3C
    $headerOffset = $reader.ReadInt32()
    if ($headerOffset -lt 64 -or $headerOffset -gt ($stream.Length - 26)) {
        throw 'The release executable has an invalid PE header offset.'
    }
    $stream.Position = $headerOffset
    if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
        throw 'The release executable is not a Windows x64 PE file.'
    }
    $stream.Position = $headerOffset + 24
    if ($reader.ReadUInt16() -ne 0x20B) {
        throw 'The release executable is not a Windows x64 PE file.'
    }
}
finally {
    $reader.Dispose()
    $stream.Dispose()
}

$hash = (Get-FileHash -LiteralPath $executable.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ExpectedSha256 -and $hash -ne $ExpectedSha256) {
    throw "SHA-256 verification failed for '$($executable.Name)'."
}
Write-Host "Verified $($executable.Name) (SHA-256: $hash)."
