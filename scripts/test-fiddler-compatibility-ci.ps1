<#
.SYNOPSIS
Runs the shipped bridge in an explicitly opted-in, disposable GitHub-hosted Fiddler process.

.PARAMETER ReleaseDirectory
The already-built release ZIP and checksum manifest. The bridge is never rebuilt by this script.

.PARAMETER ExpectedBridgeSha256
The bridge digest recorded by the single release-producing job.

.PARAMETER ExpectedFiddlerVersion
The exact native file version installed on this matrix runner.

.PARAMETER EnableDisposableRunner
Explicit permission to use this disposable runner's profile and launch its native Fiddler installation.
#>
[CmdletBinding()]
param(
    [string]$ReleaseDirectory = "$PSScriptRoot/../artifacts/release",
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedBridgeSha256,
    [Parameter(Mandatory)][ValidateSet('5.0.20253.3311', '6.0.20261.7291')][string]$ExpectedFiddlerVersion,
    [switch]$EnableDisposableRunner
)

$ErrorActionPreference = 'Stop'
if (-not $EnableDisposableRunner -or $env:GITHUB_ACTIONS -ne 'true' -or
    $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'Compatibility tests require explicit opt-in on a disposable GitHub-hosted Windows runner.'
}
if (Get-Process -Name Fiddler, fiddler-classic -ErrorAction SilentlyContinue) {
    throw 'Refusing to run alongside an existing Fiddler or CLI process.'
}
# Native special-folder APIs do not reliably honor USERPROFILE/LOCALAPPDATA environment overrides.
# These paths belong to the disposable runner account. Never run this script on a developer profile.
$configDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FiddlerClassicCLI'
$scriptsDirectory = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Fiddler2/Scripts'
if (Test-Path -LiteralPath $configDirectory) { throw 'The disposable profile must not have existing CLI configuration.' }
$ownedNames = @('FiddlerClassic.Bridge.dll', 'FiddlerClassic.Protocol.dll', 'FiddlerClassic.CompatibilityProbe.dll')
foreach ($name in $ownedNames) {
    if (Test-Path -LiteralPath (Join-Path $scriptsDirectory $name)) {
        throw 'The disposable profile already has bridge or probe files; refusing to overwrite them.'
    }
}
$fiddler = Join-Path $env:LOCALAPPDATA 'Programs/Fiddler/Fiddler.exe'
if (-not (Test-Path -LiteralPath $fiddler) -or
    [Diagnostics.FileVersionInfo]::GetVersionInfo($fiddler).FileVersion -ne $ExpectedFiddlerVersion) {
    throw 'The native Fiddler file version does not match the exact matrix version.'
}
& "$PSScriptRoot/verify-release.ps1" -ReleaseDirectory $ReleaseDirectory
$testRoot = (New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) (
    'fiddler-compatibility-' + [guid]::NewGuid().ToString('N')))).FullName
$savedEnvironment = @{}
$nativeProcess = $null
$cli = $null
$bridgeInstalled = $false
try {
    $payload = Join-Path $testRoot 'payload'
    Expand-Archive -LiteralPath "$ReleaseDirectory/fiddler-classic-win-x64.zip" -DestinationPath $payload
    $bridge = Join-Path $payload 'bridge/FiddlerClassic.Bridge.dll'
    if ((Get-FileHash -LiteralPath $bridge -Algorithm SHA256).Hash -ne $ExpectedBridgeSha256) {
        throw 'The matrix input is not the bridge from the release-producing job.'
    }
    # Build only the test probe against this installed native API and the released protocol DLL.
    $probeOutput = Join-Path $testRoot 'probe'
    & dotnet build "$PSScriptRoot/../tests/FiddlerClassic.CompatibilityProbe/FiddlerClassic.CompatibilityProbe.csproj" `
        --configuration Release --output $probeOutput "-p:FiddlerInstallDir=$(Split-Path -Parent $fiddler)" `
        "-p:ReleasedBridgeDirectory=$payload/bridge" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Compatibility probe build failed.' }
    if (Test-Path -LiteralPath "$probeOutput/Fiddler.exe") { throw 'Probe output must not copy Telerik binaries.' }

    foreach ($entry in @{
        FIDDLER_CLASSIC_PIPE_NAME = ('compatibility-' + [guid]::NewGuid().ToString('N'))
        FIDDLER_CLASSIC_DAEMON_PIPE_NAME = ('compatibility-daemon-' + [guid]::NewGuid().ToString('N'))
        FIDDLER_CLASSIC_COMPATIBILITY_CI = '1'
        FIDDLER_CLASSIC_COMPATIBILITY_RESULT = "$testRoot/result.json"
        FIDDLER_CLASSIC_EXPECTED_BRIDGE_SHA256 = $ExpectedBridgeSha256
    }.GetEnumerator()) {
        $savedEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    $cli = Join-Path $payload 'fiddler-classic.exe'
    $bridgeInstalled = $true
    & $cli bridge install | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Bridge deployment in the disposable account failed.' }
    Copy-Item -LiteralPath "$probeOutput/FiddlerClassic.CompatibilityProbe.dll" -Destination $scriptsDirectory
    $portReservation = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $portReservation.Start()
    $port = $portReservation.LocalEndpoint.Port
    $portReservation.Stop()
    # This is a test-only native launch. The production host and installer never launch Fiddler.
    $nativeProcess = Start-Process -FilePath $fiddler -ArgumentList @(
        '-noattach', '-quiet', '-noversioncheck', '-noscript', "-port:$port") -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (-not (Test-Path -LiteralPath "$testRoot/result.json") -and [DateTime]::UtcNow -lt $deadline) {
        if ($nativeProcess.HasExited) { throw 'Native Fiddler exited before the compatibility probe completed.' }
        Start-Sleep -Milliseconds 200
    }
    if (-not (Test-Path -LiteralPath "$testRoot/result.json")) {
        throw 'Native compatibility probe timed out. No live compatibility result is available.'
    }
    $result = Get-Content -LiteralPath "$testRoot/result.json" -Raw | ConvertFrom-Json
    foreach ($check in $result.Passed) { Write-Host "PASS $check" }
    if (-not $result.Success) { throw "Compatibility failure: $($result.Failure)" }
    if ((Get-FileHash -LiteralPath $bridge -Algorithm SHA256).Hash -ne $ExpectedBridgeSha256) {
        throw 'The shipped bridge changed during the compatibility run.'
    }
    Write-Host "PASS Fiddler $ExpectedFiddlerVersion with shipped bridge SHA-256 $ExpectedBridgeSha256"
}
finally {
    # Stop only processes belonging to this test; no process-name kill or profile reset is used.
    if ($nativeProcess) {
        if (-not $nativeProcess.HasExited) {
            $null = $nativeProcess.CloseMainWindow()
            if (-not $nativeProcess.WaitForExit(5000)) { $nativeProcess.Kill($true); $nativeProcess.WaitForExit() }
        }
        $nativeProcess.Dispose()
    }
    try {
        if ($bridgeInstalled -and $cli) {
            & $cli daemon stop | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'The isolated compatibility daemon did not stop cleanly.' }
            & $cli bridge uninstall --yes | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'The disposable bridge deployment did not clean up.' }
        }
    }
    finally {
        foreach ($entry in $savedEnvironment.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
        $probePath = Join-Path $scriptsDirectory 'FiddlerClassic.CompatibilityProbe.dll'
        if (Test-Path -LiteralPath $probePath) { Remove-Item -LiteralPath $probePath -Force }
        $resolved = [IO.Path]::GetFullPath($testRoot)
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolved) -notlike 'fiddler-compatibility-*') {
            throw 'Refusing to remove an unexpected compatibility directory.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
        # Generated native settings and CLI credentials die with the runner; never upload them as artifacts.
    }
}
