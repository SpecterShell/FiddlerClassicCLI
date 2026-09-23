<#
.SYNOPSIS
Tests the fixed-directory installer using inert executables and private temporary directories.

.PARAMETER FixtureOnly
Retained for CI callers. All normal test cases use fixtures and never install into a live profile.

.PARAMETER LegacyPackage
Optional, explicitly trusted historical release ZIP with an adjacent SHA256SUMS. Runs only
--version with -SkipBridge and -NoPathUpdate in a temporary directory.
#>
[CmdletBinding()]
param([switch]$FixtureOnly, [string]$LegacyPackage)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Installer checks require Windows.' }
# Exercise the minimum supported runtime, including its native process argument and UTF-8 behavior.
if ($PSVersionTable.PSVersion.Major -ne 5) {
    $arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-FixtureOnly')
    if ($LegacyPackage) { $arguments += @('-LegacyPackage', $LegacyPackage) }
    & "$env:WINDIR/System32/WindowsPowerShell/v1.0/powershell.exe" @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Windows PowerShell 5.1 installer tests failed.' }
    return
}

$installer = [IO.Path]::GetFullPath("$PSScriptRoot/install.ps1")
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
# Load pure helpers without evaluating the bootstrap or touching the user PATH.
foreach ($function in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    Invoke-Expression $function.Extent.Text
}
$utf8 = New-Object Text.UTF8Encoding($false)
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('fiddler-installer-test-' + [guid]::NewGuid().ToString('N'))
New-PrivateDirectory $testRoot
$originalUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$originalProcessPath = $env:Path
$unicodeName = 'installed ' + [char]0x6D4B + [char]0x8BD5
$destination = Join-Path $testRoot $unicodeName

function Assert {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

# Compiles only this inert program with Framework CodeDOM, outside all repository build outputs.
function New-CliFixture {
    param([string]$Directory, [string]$Version, [string]$Mode = 'success')
    New-PrivateDirectory $Directory
    $typeName = 'Fixture' + [guid]::NewGuid().ToString('N')
    $code = @"
using System;
using System.Text;
using System.IO;
public static class $typeName {
    public static int Main(string[] args) {
        Console.OutputEncoding = new UTF8Encoding(false);
        var mode = "$Mode";
        if (args.Length == 1 && args[0] == "--version") {
            if (mode == "flood") { Console.WriteLine(new string('x', 70000)); return 0; }
            if (mode == "timeout") { System.Threading.Thread.Sleep(35000); return 0; }
            if (mode == "installed-version-failure" && !AppDomain.CurrentDomain.BaseDirectory.Contains(".fiddler-classic-cli-install-")) {
                Console.WriteLine("0.0.0-wrong"); return 0;
            }
            Console.WriteLine("$Version"); return 0;
        }
        if (args.Length == 3 && args[0] == "bridge" && args[1] == "install" && args[2] == "--json") {
            if (mode == "bridge-failure") { Console.Error.WriteLine("Fixture bridge is locked. Close Fiddler normally and retry."); return 5; }
            Console.WriteLine("{\"fixture\":true}"); return 0;
        }
        Console.Error.WriteLine("Unexpected fixture command."); return 9;
    }
}
"@
    Add-Type -TypeDefinition $code -OutputAssembly "$Directory/fiddler-classic-cli.exe" -OutputType ConsoleApplication
    [IO.File]::WriteAllText("$Directory/zz-runtime.dll", $Version, $utf8)
}

# Captures the complete JSON receipt and stderr from a real PS5.1 process.
function Invoke-TestInstaller {
    param([string]$Package, [string]$Target = $destination, [switch]$SkipBridge, [switch]$Fail,
        [string]$ErrorContains, [switch]$ViaExpression, [string]$WorkingDirectory)
    $Target = [IO.Path]::GetFullPath($Target)
    $arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass')
    if ($ViaExpression) {
        $command = '& ([scriptblock]::Create([IO.File]::ReadAllText(''' + $installer.Replace("'", "''") +
            '''))) -PackagePath ''' + $Package.Replace("'", "''") + ''' -InstallDirectory ''' +
            $Target.Replace("'", "''") + ''' -NoPathUpdate -Json'
        if ($SkipBridge) { $command += ' -SkipBridge' }
        $arguments += @('-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)))
    } else {
        $arguments += @('-File', $installer, '-PackagePath', $Package, '-InstallDirectory', $Target, '-NoPathUpdate', '-Json')
        if ($SkipBridge) { $arguments += '-SkipBridge' }
    }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = "$PSHOME/powershell.exe"
    if ($WorkingDirectory) { $start.WorkingDirectory = $WorkingDirectory }
    $start.Arguments = ($arguments | ForEach-Object {
            $escaped = [regex]::Replace($_, '(\\*)"', '$1$1\"')
            '"' + [regex]::Replace($escaped, '(\\+)$', '$1$1') + '"'
        }) -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Installer fixture timed out.' }
        $stdout = $output.GetAwaiter().GetResult()
        $stderr = $errors.GetAwaiter().GetResult()
        if ($Fail) {
            Assert ($process.ExitCode -ne 0 -and -not $stdout) "Expected a failure with empty stdout. Received: $stdout $stderr"
            Assert (-not $stderr.Contains('Rollback failed')) $stderr
            if ($ErrorContains) { Assert ($stderr.Contains($ErrorContains)) "Expected '$ErrorContains'. Received: $stderr" }
            return
        }
        Assert ($process.ExitCode -eq 0 -and -not $stderr) "Installer failed: $stderr"
        Assert (-not $stdout.StartsWith([string][char]0xFEFF, [StringComparison]::Ordinal)) 'JSON output contains a BOM.'
        $receipt = $stdout | ConvertFrom-Json
        Assert (($receipt.PSObject.Properties.Name -join ',') -ceq 'installDirectory,executablePath,version,bridgeInstalled,pathUpdated') 'Unexpected JSON receipt fields or extra output.'
        Assert ($receipt.installDirectory -ceq $Target) 'Unicode installation path was corrupted.'
        Assert ($receipt.executablePath -ceq (Join-Path $Target 'fiddler-classic-cli.exe')) 'Incorrect installed filename.'
        Assert (-not $receipt.pathUpdated) 'NoPathUpdate reported a PATH change.'
        Assert ($receipt.bridgeInstalled -eq (-not $SkipBridge)) 'Incorrect bridge installation receipt.'
        return $receipt
    } finally { $process.Dispose() }
}

function New-Checksum {
    param([string]$Archive)
    $hash = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path (Split-Path -Parent $Archive) 'SHA256SUMS'),
        "$hash  $([IO.Path]::GetFileName($Archive))" + [Environment]::NewLine, $utf8)
}

# Runs the real HTTP download helper against queued in-memory responses, with no socket access.
function Initialize-DownloadFixture {
    Add-Type -AssemblyName System.Net.Http
    Add-Type -ReferencedAssemblies System.Net.Http -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
public sealed class InstallerDownloadFixture : HttpMessageHandler {
    public readonly Queue<HttpResponseMessage> Responses = new Queue<HttpResponseMessage>();
    public readonly List<string> Urls = new List<string>();
    public readonly List<string> Accepts = new List<string>();
    public readonly List<bool> Authorized = new List<bool>();
    public readonly List<bool> Cancellable = new List<bool>();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
        Urls.Add(request.RequestUri.AbsoluteUri);
        Accepts.Add(request.Headers.Accept.ToString());
        Authorized.Add(request.Headers.Authorization != null &&
            request.Headers.Authorization.ToString() == "Bearer inert-fixture-token");
        Cancellable.Add(token.CanBeCanceled);
        if (Responses.Count == 0) throw new InvalidOperationException("Unexpected fixture download.");
        return Task.FromResult(Responses.Dequeue());
    }
}
'@
}

function New-ReleaseFixture {
    param([string]$Repository = 'fixture-owner/fixture-repo', [string]$Tag = 'v2.0.0-fixture')
    return @{ draft = $false; tag_name = $Tag; assets = @(@{
                name = 'fiddler-classic-cli.exe'; state = 'uploaded'; id = 123
                digest = 'sha256:' + $fixtureDigest
                browser_download_url = "https://github.com/$Repository/releases/download/$Tag/fiddler-classic-cli.exe"
            }) }
}

# Fails on any unplanned download, including a checksum manifest, and preserves the caller's token.
function Invoke-ReleaseFixture {
    param($Release, [string]$Version = 'latest', [string]$Repository = 'fixture-owner/fixture-repo',
        [string]$ErrorContains, [int]$RequestCount = 2, [switch]$Authenticated,
        [string[]]$Redirects = @(), [long]$DeclaredAssetSize = 0)
    $directory = Join-Path $testRoot ('download-' + [guid]::NewGuid().ToString('N'))
    New-PrivateDirectory $directory
    $handler = New-Object InstallerDownloadFixture
    $metadata = New-Object Net.Http.HttpResponseMessage([Net.HttpStatusCode]::OK)
    $metadata.Content = New-Object Net.Http.ByteArrayContent(,$utf8.GetBytes(($Release | ConvertTo-Json -Depth 8)))
    $handler.Responses.Enqueue($metadata)
    foreach ($redirect in $Redirects) {
        $response = New-Object Net.Http.HttpResponseMessage([Net.HttpStatusCode]::Found)
        $response.Headers.Location = [uri]$redirect
        $handler.Responses.Enqueue($response)
    }
    $binary = New-Object Net.Http.HttpResponseMessage([Net.HttpStatusCode]::OK)
    $binary.Content = New-Object Net.Http.ByteArrayContent(,$fixtureBytes)
    if ($DeclaredAssetSize) { $binary.Content.Headers.ContentLength = $DeclaredAssetSize }
    $handler.Responses.Enqueue($binary)
    $httpClient = New-Object Net.Http.HttpClient($handler)
    $previousToken = $env:GITHUB_TOKEN
    try {
        $env:GITHUB_TOKEN = if ($Authenticated) { 'inert-fixture-token' } else { $null }
        $failure = $null
        $result = $null
        try { $result = Receive-InstallerRelease $Repository $Version $directory }
        catch { $failure = $_.Exception.Message }
        if ($ErrorContains) {
            Assert ($failure -and $failure.Contains($ErrorContains)) "Expected '$ErrorContains'. Received: $failure"
            Assert (-not $result) 'Rejected download returned an executable path.'
        } else {
            Assert (-not $failure) "Fixture download failed: $failure"
            Assert ($result -ceq (Join-Path $directory 'fiddler-classic-cli.exe')) 'Download returned an unexpected filename.'
            Assert ((Get-FileHash -LiteralPath $result).Hash -ieq $fixtureDigest) 'Verified fixture bytes changed.'
        }
        Assert ($handler.Urls.Count -eq $RequestCount) "Expected $RequestCount requests, received $($handler.Urls.Count)."
        $route = if ($Version -eq 'latest') { 'latest' } else { 'tags/' + [uri]::EscapeDataString($Version) }
        Assert ($handler.Urls[0] -ceq "https://api.github.com/repos/$Repository/releases/$route") 'Incorrect repository or latest/tag route.'
        Assert ($handler.Accepts[0] -ceq 'application/vnd.github+json') 'Release lookup did not request JSON.'
        for ($i = 0; $i -lt $handler.Urls.Count; $i++) {
            $address = [uri]$handler.Urls[$i]
            Assert ($handler.Authorized[$i] -eq ($Authenticated -and $address.Host -eq 'api.github.com')) 'Token scope changed on an asset request or redirect.'
            Assert ($handler.Cancellable[$i]) 'Download request has no cancellation deadline.'
            Assert (-not $handler.Urls[$i].Contains('SHA256SUMS')) 'Installer downloaded a remote checksum manifest.'
        }
        if ($handler.Urls.Count -gt 1) {
            $assetUri = if ($Authenticated) { "https://api.github.com/repos/$Repository/releases/assets/123" }
                else { $Release.assets[0].browser_download_url }
            Assert ($handler.Urls[1] -ceq $assetUri) 'Installer selected the wrong asset URL.'
            if ($Authenticated) { Assert ($handler.Accepts[1] -ceq 'application/octet-stream') 'Authenticated asset request did not request binary content.' }
        }
    } finally {
        $env:GITHUB_TOKEN = $previousToken
        $httpClient.Dispose()
        while ($handler.Responses.Count) { $handler.Responses.Dequeue().Dispose() }
    }
}

try {
    New-CliFixture "$testRoot/prior payload" '1.0.0-fixture'
    New-CliFixture "$testRoot/current payload" '2.0.0-fixture'
    New-CliFixture "$testRoot/rebuilt payload" '2.0.0-fixture'
    New-CliFixture "$testRoot/bridge failure" '3.0.0-fixture' 'bridge-failure'
    New-CliFixture "$testRoot/version failure" '3.0.0-fixture' 'installed-version-failure'
    New-CliFixture "$testRoot/output flood" '3.0.0-fixture' 'flood'
    New-CliFixture "$testRoot/timeout" '3.0.0-fixture' 'timeout'
    Initialize-DownloadFixture
    $fixtureBytes = [IO.File]::ReadAllBytes("$testRoot/current payload/fiddler-classic-cli.exe")
    $fixtureDigest = (Get-FileHash "$testRoot/current payload/fiddler-classic-cli.exe").Hash.ToLowerInvariant()
    Invoke-ReleaseFixture (New-ReleaseFixture)
    Invoke-ReleaseFixture (New-ReleaseFixture) -Version 'v2.0.0-fixture'
    Invoke-ReleaseFixture (New-ReleaseFixture -Repository 'other-owner/other-repo') -Repository 'other-owner/other-repo' -Authenticated
    $release = New-ReleaseFixture
    $release.assets[0].digest = 'sha256:' + $fixtureDigest.ToUpperInvariant()
    $release.assets += @{ name = 'SHA256SUMS' }, @{ name = 'fiddler-classic-win-x64.zip' }
    Invoke-ReleaseFixture $release
    Write-Host 'PASS EXE-only latest, explicit tag, custom repository, authenticated download, and no manifest download'

    foreach ($digest in @($null, '', ('sha512:' + $fixtureDigest), ('SHA256:' + $fixtureDigest),
            ('sha256:' + ('a' * 63)), ('sha256:' + ('a' * 65)), ('sha256:' + ('z' * 64)),
            "sha256:$fixtureDigest`n")) {
        $release = New-ReleaseFixture
        $release.assets[0].digest = $digest
        Invoke-ReleaseFixture $release -ErrorContains 'requires a GitHub asset digest' -RequestCount 1
    }
    $release = New-ReleaseFixture
    $release.assets[0].Remove('digest')
    Invoke-ReleaseFixture $release -ErrorContains 'requires a GitHub asset digest' -RequestCount 1
    $release = New-ReleaseFixture
    $release.assets[0].digest = @('sha256:' + $fixtureDigest)
    Invoke-ReleaseFixture $release -ErrorContains 'requires a GitHub asset digest' -RequestCount 1
    $release = New-ReleaseFixture
    $release.assets[0].digest = 'sha256:' + ('0' * 64)
    Invoke-ReleaseFixture $release -ErrorContains 'SHA-256 verification failed'
    Write-Host 'PASS missing, malformed, and mismatched digest rejection before executable handoff'

    foreach ($name in @('fiddler-classic-win-x64.zip', 'fiddler-classic-cli-windows-x64.zip',
            'Fiddler-Classic-CLI.exe', 'fiddler-classic-cli.exe.zip', 'renamed.exe', $null)) {
        $release = New-ReleaseFixture
        $release.assets[0].name = $name
        Invoke-ReleaseFixture $release -ErrorContains 'release lacks standalone fiddler-classic-cli.exe' -RequestCount 1
    }
    $release = New-ReleaseFixture
    $release.assets = @()
    Invoke-ReleaseFixture $release -ErrorContains 'release lacks standalone fiddler-classic-cli.exe' -RequestCount 1
    $release = New-ReleaseFixture
    $release.assets += $release.assets[0].Clone()
    $release.assets[1].state = 'starter'
    Invoke-ReleaseFixture $release -ErrorContains 'duplicate release assets' -RequestCount 1
    foreach ($state in @('starter', 'Uploaded', '', $null)) {
        $release = New-ReleaseFixture
        $release.assets[0].state = $state
        Invoke-ReleaseFixture $release -ErrorContains 'not fully uploaded' -RequestCount 1
    }
    $release = New-ReleaseFixture
    $release.draft = $true
    Invoke-ReleaseFixture $release -ErrorContains 'unexpected release' -RequestCount 1
    $release = New-ReleaseFixture
    $release.tag_name = ''
    Invoke-ReleaseFixture $release -ErrorContains 'unexpected release' -RequestCount 1
    Invoke-ReleaseFixture (New-ReleaseFixture) -Version 'v1.0.0-fixture' -ErrorContains 'unexpected release' -RequestCount 1
    Write-Host 'PASS exact asset name, no remote ZIP fallback, duplicate asset, upload state, draft, and tag rejection'

    $assetBase = 'https://github.com/fixture-owner/fixture-repo/releases/download/v2.0.0-fixture/fiddler-classic-cli.exe'
    foreach ($url in @($assetBase.Replace('https:', 'http:'), $assetBase.Replace('github.com/', 'github.com:444/'),
            $assetBase.Replace('github.com/', 'user@github.com/'), $assetBase.Replace('github.com/', 'example.com/'),
            $assetBase.Replace('fixture-owner/', 'other-owner/'), $assetBase.Replace('fixture-repo/', 'other-repo/'),
            $assetBase.Replace('v2.0.0-fixture/', 'v1.0.0-fixture/'), $assetBase.Replace('.exe', '.zip'),
            ($assetBase + '?download=1'), ($assetBase + '#fragment'), '/relative.exe', 'https://[', '', $null)) {
        $release = New-ReleaseFixture
        $release.assets[0].browser_download_url = $url
        Invoke-ReleaseFixture $release -Authenticated -ErrorContains 'asset URL must use GitHub HTTPS' -RequestCount 1
    }
    foreach ($id in @(0, -1, '123/other', 'invalid', $null)) {
        $release = New-ReleaseFixture
        $release.assets[0].id = $id
        Invoke-ReleaseFixture $release -Authenticated -ErrorContains 'invalid asset ID' -RequestCount 1
    }
    Invoke-ReleaseFixture (New-ReleaseFixture) -DeclaredAssetSize (512MB + 1) -ErrorContains 'size limit'
    Invoke-ReleaseFixture (New-ReleaseFixture) -Authenticated -Redirects @('https://release-assets.githubusercontent.com/inert.exe') -RequestCount 3
    foreach ($redirect in @('http://github.com/inert.exe', 'https://example.com/inert.exe', 'https://user@github.com/inert.exe')) {
        Invoke-ReleaseFixture (New-ReleaseFixture) -Redirects @($redirect) -ErrorContains 'GitHub HTTPS URLs'
    }
    Invoke-ReleaseFixture (New-ReleaseFixture) -Redirects (@('https://github.com/redirect') * 6) -RequestCount 7 -ErrorContains 'redirect limit'
    Write-Host 'PASS repository/tag URL checks, authenticated asset IDs, size bounds, redirects, and API-only token forwarding'

    $first = Invoke-TestInstaller "$testRoot/prior payload"
    Assert ($first.version -eq '1.0.0-fixture') 'Fresh install reported the wrong version.'
    Assert ((Get-Acl -LiteralPath $destination).AreAccessRulesProtected) 'Fresh installation ACL is not protected.'
    [IO.File]::WriteAllText("$destination/unrelated.txt", 'keep this', $utf8)
    New-PrivateDirectory "$destination/0.9.0"
    Copy-Item -LiteralPath "$testRoot/prior payload/fiddler-classic-cli.exe" -Destination "$destination/0.9.0/fiddler-classic.exe"
    [IO.File]::WriteAllText("$destination/0.9.0/install.ps1", 'old version marker', $utf8)
    $priorHash = (Get-FileHash "$destination/0.9.0/fiddler-classic.exe").Hash
    $null = Invoke-TestInstaller "$testRoot/prior payload"
    $null = Invoke-TestInstaller '.' -WorkingDirectory "$testRoot/prior payload"
    Invoke-TestInstaller '' -Fail -ErrorContains 'PackagePath'
    $null = Invoke-TestInstaller $destination
    $null = Invoke-TestInstaller "$destination/fiddler-classic-cli.exe" -ViaExpression
    Write-Host 'PASS PS5.1 fresh, reinstall, in-place executable, and scriptblock invocation with Unicode and spaces'

    $null = Invoke-TestInstaller "$testRoot/current payload"
    $beforeRebuild = (Get-FileHash "$destination/fiddler-classic-cli.exe").Hash
    $null = Invoke-TestInstaller "$testRoot/rebuilt payload"
    Assert ((Get-FileHash "$destination/fiddler-classic-cli.exe").Hash -ne $beforeRebuild) 'Same-version rebuild did not replace executable bytes.'
    Assert ((Get-FileHash "$destination/0.9.0/fiddler-classic.exe").Hash -eq $priorHash) 'Upgrade changed an old version directory.'
    Assert ([IO.File]::ReadAllText("$destination/unrelated.txt") -eq 'keep this') 'Upgrade changed unrelated files.'
    Write-Host 'PASS fixed-directory upgrade and same-version replacement preserve unrelated files and version directories'

    $beforeFailure = (Get-FileHash "$destination/fiddler-classic-cli.exe").Hash
    Invoke-TestInstaller "$testRoot/bridge failure" -Fail -ErrorContains 'Close Fiddler normally and retry'
    Assert ((Get-FileHash "$destination/fiddler-classic-cli.exe").Hash -eq (Get-FileHash "$testRoot/bridge failure/fiddler-classic-cli.exe").Hash) 'Bridge failure rolled back the verified CLI.'
    $null = Invoke-TestInstaller "$testRoot/rebuilt payload"
    Invoke-TestInstaller "$testRoot/version failure" -Fail -ErrorContains 'version verification'
    Assert ((Get-FileHash "$destination/fiddler-classic-cli.exe").Hash -eq $beforeFailure) 'Installed version failure did not roll back the executable.'
    Invoke-TestInstaller "$testRoot/output flood" -Fail -ErrorContains '64 KiB'
    Invoke-TestInstaller "$testRoot/timeout" -Fail -ErrorContains 'timed out after 30 seconds'
    $lock = [IO.File]::Open("$destination/zz-runtime.dll", [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { Invoke-TestInstaller "$testRoot/prior payload" -Fail }
    finally { $lock.Dispose() }
    Assert ((Get-FileHash "$destination/fiddler-classic-cli.exe").Hash -eq $beforeFailure) 'Locked companion DLL did not roll back an earlier executable replacement.'
    $null = Invoke-TestInstaller "$testRoot/bridge failure/fiddler-classic-cli.exe" -Target "$testRoot/skip bridge" -SkipBridge
    Write-Host 'PASS bridge partial failure retains CLI, file transaction rollback, output and time bounds, and SkipBridge'

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = "$testRoot/fiddler-classic-cli-windows-x64.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory("$testRoot/current payload", $archive)
    New-Checksum $archive
    $null = Invoke-TestInstaller $archive
    [IO.File]::WriteAllText("$testRoot/SHA256SUMS", ('0' * 64) + '  fiddler-classic-cli-windows-x64.zip', $utf8)
    Invoke-TestInstaller $archive -Fail -ErrorContains 'SHA-256'
    New-Checksum $archive
    $manifestText = [IO.File]::ReadAllText("$testRoot/SHA256SUMS")
    [IO.File]::WriteAllText("$testRoot/SHA256SUMS", $manifestText + $manifestText, $utf8)
    Invoke-TestInstaller $archive -Fail -ErrorContains 'duplicate'
    [IO.File]::WriteAllText("$testRoot/SHA256SUMS", $manifestText.Replace('fiddler-classic-cli-windows-x64.zip', 'fiddler-classic-win-x64.zip'), $utf8)
    Invoke-TestInstaller $archive -Fail -ErrorContains 'exact entry'
    Remove-Item -LiteralPath "$testRoot/SHA256SUMS"
    Invoke-TestInstaller $archive -Fail
    Write-Host 'PASS ZIP install and missing, wrong, duplicate, and renamed checksum rejection'

    $unsafeCases = @('../escape', '/absolute', 'C:/absolute', 'a/file:stream', 'a/../escape', 'a/file.', 'NUL.txt')
    $index = 0
    foreach ($entry in $unsafeCases) {
        $badZip = "$testRoot/unsafe-$index.zip"
        $zip = [IO.Compression.ZipFile]::Open($badZip, [IO.Compression.ZipArchiveMode]::Create)
        try { $null = $zip.CreateEntry($entry) } finally { $zip.Dispose() }
        New-Checksum $badZip
        Invoke-TestInstaller $badZip -Fail -ErrorContains 'unsafe Windows path'
        $index++
    }
    Invoke-TestInstaller "$testRoot/prior payload" -Target ([IO.Path]::GetPathRoot($testRoot)) -Fail -ErrorContains 'dedicated installation directory'
    $junction = "$testRoot/junction"
    $null = New-Item -ItemType Junction -Path $junction -Target $destination
    try { Invoke-TestInstaller "$testRoot/prior payload" -Target $junction -Fail -ErrorContains 'Reparse points' }
    finally { [IO.Directory]::Delete($junction) }
    Write-Host 'PASS archive traversal, broad destination, and reparse-point rejection'

    $pathValue = 'C:\unrelated;;' + $destination + ';' + "$destination/0.9.0" + ';%WINDIR%\System32;relative'
    $expectedPath = $destination + ';C:\unrelated;;%WINDIR%\System32;relative'
    Assert ((Get-InstallerPath $pathValue $destination) -ceq $expectedPath) 'PATH normalization lost unrelated entries.'
    Assert ((Get-InstallerPath $expectedPath $destination) -ceq $expectedPath) 'PATH normalization is not idempotent.'
    Assert ((Get-InstallerPath "$destination/8.0.0" $destination) -ceq "$destination;$destination/8.0.0") 'PATH normalization removed an unrecognized version directory.'
    Assert ([Environment]::GetEnvironmentVariable('Path', 'User') -ceq $originalUserPath) 'Tests changed the user PATH.'
    Assert ($env:Path -ceq $originalProcessPath) 'Tests changed the process PATH.'
    Assert (@(Get-ChildItem -LiteralPath $destination -Force -Directory | Where-Object Name -like '.fiddler-classic-cli-install-*').Count -eq 0) 'Successful rollback left staging directories.'
    Write-Host 'PASS idempotent PATH mapping and NoPathUpdate without registry writes'

    if ($LegacyPackage) {
        $legacyTarget = "$testRoot/historical apphost"
        $null = Invoke-TestInstaller ([IO.Path]::GetFullPath($LegacyPackage)) -Target $legacyTarget -SkipBridge
        foreach ($name in @('fiddler-classic.dll', 'fiddler-classic.deps.json', 'fiddler-classic.runtimeconfig.json')) {
            Assert (Test-Path -LiteralPath "$legacyTarget/$name") "Historical apphost companion was renamed or omitted: $name"
        }
        Assert (-not (Test-Path -LiteralPath "$legacyTarget/skills")) 'Historical ZIP installed agent skills.'
        Write-Host 'PASS historical apphost renamed while companion DLL and metadata filenames are preserved'
    }
} finally {
    $full = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + '\'
    if (-not $full.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($full) -notmatch '^fiddler-installer-test-[a-f0-9]{32}$') { throw 'Unexpected installer test directory.' }
    Assert-NoReparsePoint $full
    Remove-Item -LiteralPath $full -Recurse -Force
}
