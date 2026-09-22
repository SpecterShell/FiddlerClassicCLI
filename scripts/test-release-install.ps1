<#
.SYNOPSIS
Checks root-installer fresh install, same-version reinstall, and side-by-side upgrade in a temporary directory.

.PARAMETER ReleaseDirectory
The verified release ZIP and SHA256SUMS. Real release executables run only on disposable GitHub-hosted runners.

.PARAMETER FixtureOnly
Uses version-only executables for both versions. Safe locally: neither executable can access CLI services or config.
#>
[CmdletBinding()]
param(
    [string]$ReleaseDirectory = "$PSScriptRoot/../artifacts/release",
    [switch]$FixtureOnly
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Installer checks require Windows.' }
if (-not $FixtureOnly -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted')) {
    throw 'Real release install checks require a disposable GitHub-hosted runner. Use -FixtureOnly locally.'
}
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testRoot = (New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) (
    'fiddler-release-install-' + [guid]::NewGuid().ToString('N')))).FullName
$installRoot = Join-Path $testRoot 'installed versions'
$profileRoot = Join-Path $testRoot 'profile'
$originalUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$originalProcessPath = $env:Path

# Hashes every payload file so a reinstall or upgrade cannot silently change an earlier installation.
function Get-PayloadHashes {
    param([string]$Directory)
    $result = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($Directory, $file.FullName)
        $result[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return ($result | ConvertTo-Json -Compress)
}

# Runs only an explicit installer source with an isolated process environment and bounded lifetime.
function Invoke-TestInstaller {
    param([string]$Installer, [string]$Package)
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh -ErrorAction Stop).Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', $Installer,
        '-InstallRoot', $installRoot, '-NoPathUpdate')) { $start.ArgumentList.Add($argument) }
    if ($Package) {
        $start.ArgumentList.Add('-PackagePath')
        $start.ArgumentList.Add($Package)
    }
    foreach ($entry in @{
        USERPROFILE = $profileRoot; LOCALAPPDATA = "$profileRoot/Local"; APPDATA = "$profileRoot/Roaming"
        TEMP = "$profileRoot/Temp"; TMP = "$profileRoot/Temp"
        FIDDLER_CLASSIC_PIPE_NAME = ('installer-' + [guid]::NewGuid().ToString('N'))
        FIDDLER_CLASSIC_DAEMON_PIPE_NAME = ('installer-daemon-' + [guid]::NewGuid().ToString('N'))
    }.GetEnumerator()) { $start.Environment[$entry.Key] = $entry.Value }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            throw 'Temporary installer check timed out.'
        }
        if ($process.ExitCode -ne 0) { throw "Installer failed: $($errorOutput.GetAwaiter().GetResult())" }
        $lines = $output.GetAwaiter().GetResult().Trim() -split '\r?\n'
        $executable = $lines[-1]
        $prefix = [IO.Path]::GetFullPath($installRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not [IO.Path]::GetFullPath($executable).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            throw 'Installer did not return an executable beneath the temporary install root.'
        }
        return Split-Path -Parent $executable
    }
    finally { $process.Dispose() }
}

# Builds an inert prior-version payload with the actual root installer and skill assets.
function New-FixturePayload {
    param([string]$Version, [string]$Directory, [string]$Title = 'Installer fixture')
    & dotnet build "$repositoryRoot/tests/FiddlerClassic.ReleaseFixture/FiddlerClassic.ReleaseFixture.csproj" `
        --configuration Release --output $Directory "-p:Version=$Version" "-p:AssemblyTitle=$Title" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Installer fixture build failed.' }
    foreach ($name in @('install.ps1', 'LICENSE', 'skills', 'docs')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $name) -Destination $Directory -Recurse
    }
    New-Item -ItemType Directory -Path "$Directory/bridge" | Out-Null
    foreach ($name in @('FiddlerClassic.Bridge.dll', 'FiddlerClassic.Protocol.dll')) {
        [IO.File]::WriteAllText((Join-Path "$Directory/bridge" $name), 'Inert installer fixture; never loaded.')
    }
}

try {
    foreach ($directory in @($installRoot, "$profileRoot/Local", "$profileRoot/Roaming", "$profileRoot/Temp")) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $priorPayload = Join-Path $testRoot 'prior payload'
    New-FixturePayload -Version '0.0.0-installer-fixture' -Directory $priorPayload
    if ($FixtureOnly) {
        $payload = Join-Path $testRoot 'current payload'
        New-FixturePayload -Version '1.0.0-installer-fixture' -Directory $payload
        $ReleaseDirectory = (New-Item -ItemType Directory -Path "$testRoot/release").FullName
        Compress-Archive -Path "$payload/*" -DestinationPath "$ReleaseDirectory/fiddler-classic-win-x64.zip"
        $hash = (Get-FileHash -LiteralPath "$ReleaseDirectory/fiddler-classic-win-x64.zip" -Algorithm SHA256).Hash
        [IO.File]::WriteAllText("$ReleaseDirectory/SHA256SUMS", "$hash  fiddler-classic-win-x64.zip`n")
    }
    & "$PSScriptRoot/verify-release.ps1" -ReleaseDirectory $ReleaseDirectory
    $archive = (Resolve-Path -LiteralPath "$ReleaseDirectory/fiddler-classic-win-x64.zip").Path
    $expanded = Join-Path $testRoot 'expanded release'
    Expand-Archive -LiteralPath $archive -DestinationPath $expanded
    $installer = Join-Path $expanded 'install.ps1'
    $prior = Invoke-TestInstaller -Installer $installer -Package $priorPayload
    $priorHashes = Get-PayloadHashes $prior
    $installed = Invoke-TestInstaller -Installer $installer -Package $archive
    if ($prior -eq $installed) { throw 'Upgrade did not create a new version directory.' }
    $expectedHashes = Get-PayloadHashes $expanded
    if ((Get-PayloadHashes $installed) -cne $expectedHashes) { throw 'Installed release differs from the ZIP payload.' }
    if ((Get-PayloadHashes $prior) -cne $priorHashes) { throw 'Upgrade changed the previous version.' }
    Write-Host 'PASS fresh install and side-by-side upgrade; prior version preserved'
    # Omit PackagePath to exercise root-level adjacent-payload discovery on same-version reinstall.
    $reinstalled = Invoke-TestInstaller -Installer $installer
    if ($reinstalled -ne $installed -or (Get-PayloadHashes $installed) -cne $expectedHashes) {
        throw 'Same-version reinstall changed the version directory or release files.'
    }
    $inPlace = Invoke-TestInstaller -Installer (Join-Path $installed 'install.ps1')
    if ($inPlace -ne $installed -or (Get-PayloadHashes $installed) -cne $expectedHashes) {
        throw 'Running the installed root installer changed the version directory or release files.'
    }
    if (@(Get-ChildItem -LiteralPath $installRoot -Directory).Count -ne 2) {
        throw 'The installer produced an unexpected version directory.'
    }
    if (Test-Path -LiteralPath "$installed/skills/fiddler-classic-cli/scripts") {
        throw 'The skill package contains a nested installer.'
    }
    Write-Host 'PASS same-version reinstall, in-place rerun, and root-installer/skill packaging'
    $rebuilt = Join-Path $testRoot 'rebuilt prior payload'
    New-FixturePayload -Version '0.0.0-installer-fixture' -Directory $rebuilt -Title 'Rebuilt installer fixture'
    if ((Get-FileHash -LiteralPath "$rebuilt/fiddler-classic.exe").Hash -eq
        (Get-FileHash -LiteralPath "$prior/fiddler-classic.exe").Hash) {
        throw 'The same-version rebuild fixture must have different executable bytes.'
    }
    $replaced = Invoke-TestInstaller -Installer $installer -Package $rebuilt
    if ($replaced -ne $prior -or (Get-PayloadHashes $prior) -cne (Get-PayloadHashes $rebuilt) -or
        (Get-PayloadHashes $installed) -cne $expectedHashes) {
        throw 'A same-version rebuild failed replacement or changed the other installed version.'
    }
    Write-Host 'PASS different-byte same-version rebuild replacement; other version preserved'
    if ([Environment]::GetEnvironmentVariable('Path', 'User') -cne $originalUserPath -or $env:Path -cne $originalProcessPath) {
        throw '-NoPathUpdate changed PATH.'
    }
    Write-Host 'PASS -NoPathUpdate preserved user and process PATH'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'fiddler-release-install-*') {
        throw 'Refusing to remove an unexpected installer test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
