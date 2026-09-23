<#
.SYNOPSIS
Extracts embedded assets from a verified EXE into a new test-owned directory.

.DESCRIPTION
Runs only --version with a test-only startup hook. Does not install or start host services.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][ValidatePattern('\A[A-Fa-f0-9]{64}\z')][string]$ExpectedSha256
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Embedded payload checks require Windows x64.' }
$source = (Get-Item -LiteralPath $Executable).FullName
& "$PSScriptRoot/verify-release.ps1" -ReleaseDirectory (Split-Path -Parent $source) -ExpectedSha256 $ExpectedSha256
$destinationRoot = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationRoot) { throw 'The test payload destination must not exist.' }
$testRoot = (New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) (
            'fiddler-payload-probe-' + [guid]::NewGuid().ToString('N')))).FullName
try {
    $probeOutput = Join-Path $testRoot 'probe'
    & dotnet build "$PSScriptRoot/../tests/FiddlerClassicCLI.DistributionProbe/FiddlerClassicCLI.DistributionProbe.csproj" `
        --configuration Release --output $probeOutput --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The embedded payload probe did not compile.' }
    New-Item -ItemType Directory -Path $destinationRoot | Out-Null
    $copy = Join-Path $destinationRoot 'fiddler-classic-cli.exe'
    Copy-Item -LiteralPath $source -Destination $copy
    if ((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -ne $ExpectedSha256) {
        throw 'The test executable changed while being copied.'
    }
    $start = [Diagnostics.ProcessStartInfo]::new($copy)
    $start.ArgumentList.Add('--version')
    $start.WorkingDirectory = $destinationRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['DOTNET_STARTUP_HOOKS'] = Join-Path $probeOutput 'FiddlerClassicCLI.DistributionProbe.dll'
    $start.Environment['FIDDLER_CLASSIC_DISTRIBUTION_HASHES'] = '{}'
    $start.Environment['FIDDLER_CLASSIC_DISTRIBUTION_OUTPUT'] = $destinationRoot
    $start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $testRoot 'native'
    $start.Environment['DOTNET_ROOT'] = Join-Path $testRoot 'no-shared-runtime'
    $start.Environment['DOTNET_ROOT_X64'] = Join-Path $testRoot 'no-shared-runtime'
    $child = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(45000)) {
            $child.Kill($true)
            $child.WaitForExit()
            throw 'The test-owned payload probe timed out and was stopped.'
        }
        $output = $stdout.GetAwaiter().GetResult()
        $errorOutput = $stderr.GetAwaiter().GetResult()
        if ($child.ExitCode -ne 0 -or $output -notmatch 'DISTRIBUTION_ASSETS_OK') {
            throw "The embedded payload probe failed: $errorOutput"
        }
    }
    finally { $child.Dispose() }
    Write-Host "Extracted test-only assets from the verified executable to $destinationRoot."
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'fiddler-payload-probe-*') {
        throw 'Refusing to remove an unexpected payload probe directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
