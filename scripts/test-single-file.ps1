<#
.SYNOPSIS
Checks an EXE-only deployment, embedded assets, and temporary installs with PATH and bridge setup skipped.

.PARAMETER ReleaseDirectory
The directory containing only the standalone executable.
#>
[CmdletBinding()]
param([string]$ReleaseDirectory = "$PSScriptRoot/../artifacts/release")

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Standalone executable checks require Windows x64.' }
& "$PSScriptRoot/verify-release.ps1" -ReleaseDirectory $ReleaseDirectory
$releaseRoot = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$testRoot = (New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) (
            'fiddler-single-file-' + [guid]::NewGuid().ToString('N')))).FullName

# Runs informational commands or explicitly isolated installs. Environment changes apply only to this child.
function Invoke-IsolatedProcess {
    param([string[]]$Arguments, [string]$Executable = $standalone, [string]$HookPath, [string]$AssetHashes)
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment.Remove('DOTNET_STARTUP_HOOKS') | Out-Null
    $start.Environment.Remove('FIDDLER_CLASSIC_DISTRIBUTION_OUTPUT') | Out-Null
    # Windows PowerShell must resolve its own Framework-compatible modules when launched from pwsh.
    $start.Environment.Remove('PSModulePath') | Out-Null
    $start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $testRoot 'native'
    $start.Environment['DOTNET_ROOT'] = Join-Path $testRoot 'no-shared-runtime'
    $start.Environment['DOTNET_ROOT_X64'] = Join-Path $testRoot 'no-shared-runtime'
    if ($HookPath) {
        $start.Environment['DOTNET_STARTUP_HOOKS'] = $HookPath
        $start.Environment['FIDDLER_CLASSIC_DISTRIBUTION_HASHES'] = $AssetHashes
    }
    $child = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(45000)) {
            $child.Kill($true)
            $child.WaitForExit()
            throw "Test process $($Arguments[0]) timed out. The test-owned child was stopped."
        }
        $output = $stdout.GetAwaiter().GetResult()
        $errorOutput = $stderr.GetAwaiter().GetResult()
        if ($child.ExitCode -ne 0) {
            $details = if ([string]::IsNullOrWhiteSpace($errorOutput)) { $output } else { $errorOutput }
            throw "Test process $($Arguments[0]) failed: $details"
        }
        return $output
    }
    finally {
        $child.Dispose()
    }
}

try {
    $standaloneDirectory = (New-Item -ItemType Directory -Path (Join-Path $testRoot 'standalone')).FullName
    $standalone = Join-Path $standaloneDirectory 'fiddler-classic-cli.exe'
    Copy-Item -LiteralPath (Join-Path $releaseRoot 'fiddler-classic-cli.exe') -Destination $standalone
    $version = Invoke-IsolatedProcess '--version'
    if ($version -notmatch '^\d+\.\d+\.\d+') { throw 'The standalone version response is invalid.' }
    $help = Invoke-IsolatedProcess '--help'
    if ($help -notmatch 'Usage:' -or $help -notmatch 'fiddler-classic-cli') {
        throw 'The standalone help response is invalid.'
    }
    # CoreCLR is statically linked into the Windows single-file host. The ASP.NET native library extracts.
    if (-not (Get-ChildItem -LiteralPath (Join-Path $testRoot 'native') -Filter 'aspnetcorev2_inprocess.dll' -Force -Recurse -File)) {
        throw 'The standalone executable did not extract its native ASP.NET Core library.'
    }

    $expected = @{}
    foreach ($name in @('FiddlerClassicCLI.Bridge.dll', 'FiddlerClassicCLI.Protocol.dll')) {
        $source = "$PSScriptRoot/../src/FiddlerClassicCLI.Bridge/bin/Release/net462/$name"
        $expected[$name] = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    }

    # A test-only startup hook reads the executing bundle. It is kept outside the EXE-only directory.
    $probeOutput = Join-Path $testRoot 'probe'
    & dotnet build "$PSScriptRoot/../tests/FiddlerClassicCLI.DistributionProbe/FiddlerClassicCLI.DistributionProbe.csproj" `
        --configuration Release --output $probeOutput --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The standalone resource probe did not compile.' }
    $probeResult = Invoke-IsolatedProcess '--version' -HookPath (Join-Path $probeOutput 'FiddlerClassicCLI.DistributionProbe.dll') `
        -AssetHashes ($expected | ConvertTo-Json -Compress)
    if ($probeResult -notmatch 'DISTRIBUTION_ASSETS_OK') { throw 'The embedded resource probe did not run.' }
    $remaining = @(Get-ChildItem -LiteralPath $standaloneDirectory -Force)
    if ($remaining.Count -ne 1 -or $remaining[0].Name -cne 'fiddler-classic-cli.exe') {
        throw 'The standalone deployment must contain only its executable.'
    }

    $previousTestHost = [Environment]::GetEnvironmentVariable('FIDDLER_CLASSIC_TEST_HOST_EXE', 'Process')
    $previousBundleDirectory = [Environment]::GetEnvironmentVariable('DOTNET_BUNDLE_EXTRACT_BASE_DIR', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('FIDDLER_CLASSIC_TEST_HOST_EXE', $standalone, 'Process')
        [Environment]::SetEnvironmentVariable('DOTNET_BUNDLE_EXTRACT_BASE_DIR', (Join-Path $testRoot 'native'), 'Process')
        & dotnet test "$PSScriptRoot/../tests/FiddlerClassicCLI.Tests/FiddlerClassicCLI.Tests.csproj" `
            --configuration Release --no-build --no-restore `
            --filter 'FullyQualifiedName~CliProcessTests|FullyQualifiedName~McpStdioProtocolTests|FullyQualifiedName~StdioTransportTests'
        if ($LASTEXITCODE -ne 0) { throw 'The standalone executable failed the offline CLI/stdio transport tests.' }
    }
    finally {
        [Environment]::SetEnvironmentVariable('FIDDLER_CLASSIC_TEST_HOST_EXE', $previousTestHost, 'Process')
        [Environment]::SetEnvironmentVariable('DOTNET_BUNDLE_EXTRACT_BASE_DIR', $previousBundleDirectory, 'Process')
    }

    $installDirectory = Join-Path $testRoot ('installed CLI ' + [char]0x6D4B + [char]0x8BD5)
    $installedExecutable = Join-Path $installDirectory 'fiddler-classic-cli.exe'
    $powershell = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $installer = [IO.Path]::GetFullPath("$PSScriptRoot/install.ps1")
    # Copy the development entry point into a test checkout to exercise its fixed source path.
    $localCheckout = Join-Path $testRoot 'local checkout'
    $localScripts = (New-Item -ItemType Directory -Path (Join-Path $localCheckout 'scripts') -Force).FullName
    Copy-Item -LiteralPath $installer -Destination (Join-Path $localScripts 'install.ps1')
    $localInstaller = Join-Path $localScripts 'install-local.ps1'
    Copy-Item -LiteralPath "$PSScriptRoot/install-local.ps1" -Destination $localInstaller
    $localArguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $localInstaller,
        '-InstallDirectory', $installDirectory, '-NoPathUpdate', '-SkipBridge', '-Json')
    try {
        $null = Invoke-IsolatedProcess -Arguments $localArguments -Executable $powershell
        throw 'The development installer accepted a missing local publish output.'
    }
    catch {
        if ($_.Exception.Message -notlike '*Local publish output is missing:*') { throw }
    }
    $localPublish = (New-Item -ItemType Directory -Path (Join-Path $localCheckout 'artifacts/publish/win-x64') -Force).FullName
    Copy-Item -LiteralPath $standalone -Destination (Join-Path $localPublish 'fiddler-classic-cli.exe')
    try {
        $null = Invoke-IsolatedProcess -Arguments @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
            '-File', $localInstaller, '-InstallDirectory', 'relative-path', '-NoPathUpdate', '-SkipBridge') -Executable $powershell
        throw 'The development installer did not propagate the shared installer failure.'
    }
    catch {
        if ($_.Exception.Message -notlike '*Use an absolute local Windows path*') { throw }
    }
    $userPathBefore = [Environment]::GetEnvironmentVariable('Path', 'User')
    $expectedExecutableHash = (Get-FileHash -LiteralPath $standalone -Algorithm SHA256).Hash
    foreach ($source in @($standalone, $installedExecutable, $null)) {
        $installArguments = if ($source) {
            @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $installer,
                '-PackagePath', $source, '-InstallDirectory', $installDirectory, '-NoPathUpdate', '-SkipBridge', '-Json')
        } else { $localArguments }
        $receipt = Invoke-IsolatedProcess -Arguments $installArguments -Executable $powershell | ConvertFrom-Json
        if ($receipt.InstallDirectory -ne $installDirectory -or $receipt.ExecutablePath -ne $installedExecutable -or
            $receipt.BridgeInstalled -ne $false -or $receipt.PathUpdated -ne $false) {
            throw 'The temporary PowerShell installation receipt does not match the requested isolated operation.'
        }
        if ((Get-FileHash -LiteralPath $installedExecutable -Algorithm SHA256).Hash -ne $expectedExecutableHash) {
            throw 'Installation changed the standalone executable bytes.'
        }
        $installedVersion = Invoke-IsolatedProcess '--version' -Executable $installedExecutable
        if ($installedVersion.Trim() -ne $version.Trim()) { throw 'Installation changed the reported version.' }
        if ([Environment]::GetEnvironmentVariable('Path', 'User') -cne $userPathBefore) {
            throw 'Installation changed the user PATH despite -NoPathUpdate.'
        }
    }
    Write-Host 'PASS standalone CLI/stdio, native extraction, embedded assets, temporary installation, in-place reinstall, and local development installer.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'fiddler-single-file-*') {
        throw 'Refusing to remove an unexpected standalone test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
