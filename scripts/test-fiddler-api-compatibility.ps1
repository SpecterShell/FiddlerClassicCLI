<#
.SYNOPSIS
Resolves the shipped bridge's direct native API references using reflection-only metadata loading.

.DESCRIPTION
Does not launch Fiddler, invoke native code, access named pipes, or read or write user configuration.
Runtime assembly binding, reflection-by-name, native UI behavior, and lifecycle need the opt-in CI probe.
#>
[CmdletBinding()]
param(
    [string]$ReleaseDirectory = "$PSScriptRoot/../artifacts/release",
    [string]$FiddlerInstallDir = "$env:LOCALAPPDATA/Programs/Fiddler",
    [Parameter(Mandatory)][ValidateSet('5.0.20253.3311', '6.0.20261.7291')][string]$ExpectedFiddlerVersion,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedBridgeSha256
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Reflection-only compatibility checks require Windows .NET Framework.' }
& "$PSScriptRoot/verify-release.ps1" -ReleaseDirectory $ReleaseDirectory
$testRoot = (New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) (
    'fiddler-api-compatibility-' + [guid]::NewGuid().ToString('N')))).FullName
try {
    $payload = Join-Path $testRoot 'payload'
    Expand-Archive -LiteralPath "$ReleaseDirectory/fiddler-classic-win-x64.zip" -DestinationPath $payload
    & dotnet build "$PSScriptRoot/../tests/FiddlerClassic.ApiCompatibility/FiddlerClassic.ApiCompatibility.csproj" `
        --configuration Release --output "$testRoot/check" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'API compatibility checker build failed.' }
    & "$testRoot/check/FiddlerClassic.ApiCompatibility.exe" "$payload/bridge/FiddlerClassic.Bridge.dll" `
        "$FiddlerInstallDir/Fiddler.exe" $ExpectedFiddlerVersion $ExpectedBridgeSha256
    if ($LASTEXITCODE -ne 0) { throw 'The shipped bridge failed direct native API compatibility checks.' }
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'fiddler-api-compatibility-*') {
        throw 'Refusing to remove an unexpected API compatibility directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
