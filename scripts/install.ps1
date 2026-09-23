<#
.SYNOPSIS
Installs Fiddler Classic CLI and its bridge for the current Windows user.

.DESCRIPTION
Downloads the latest release from SpecterShell/FiddlerClassicCLI by default. Uses one fixed
directory and preserves unrelated files and older version directories. Requires Windows
PowerShell 5.1 or newer. Never launches Fiddler or configures agent skills.

.PARAMETER PackagePath
An explicitly trusted single-file executable or publish directory, or a ZIP with an adjacent
SHA256SUMS file. Local executables and directories are trusted without a checksum.

.PARAMETER Repository
The trusted GitHub owner/name. Defaults to SpecterShell/FiddlerClassicCLI.

.PARAMETER Version
The release tag to download. Defaults to latest.

.PARAMETER InstallDirectory
The fixed installation directory. Defaults to LOCALAPPDATA/Programs/FiddlerClassicCLI.

.PARAMETER NoPathUpdate
Preserves the user and process PATH.

.PARAMETER SkipBridge
Installs only the CLI. Otherwise runs the installed CLI with bridge install --json.

.PARAMETER Json
Writes only the installation receipt to stdout. Errors go to stderr with a nonzero exit code.
#>
[CmdletBinding()]
param(
    [string]$PackagePath,
    [string]$Repository = 'SpecterShell/FiddlerClassicCLI',
    [string]$Version = 'latest',
    [string]$InstallDirectory = "$env:LOCALAPPDATA/Programs/FiddlerClassicCLI",
    [switch]$NoPathUpdate,
    [switch]$SkipBridge,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$utf8 = New-Object Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
$errorWriter = New-Object IO.StreamWriter([Console]::OpenStandardError(), $utf8)
$errorWriter.AutoFlush = $true
[Console]::SetError($errorWriter)

# Rejects ambiguous Windows names before resolving or creating anything.
function Get-SafePath {
    param([string]$Path)
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path.Substring(3) -match '[\x00-\x1f\x7f<>:"|?*]') {
        throw 'Use an absolute local Windows path without wildcards or alternate data streams.'
    }
    foreach ($part in ($Path.Substring(3).TrimEnd('\', '/') -split '[\\/]')) {
        if ($part -in @('.', '..') -or $part -match '[. ]$' -or
            $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
            throw 'The path contains an unsafe Windows name.'
        }
    }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if ($full -match '^[A-Za-z]:$') { throw 'Choose a dedicated installation directory below a user-owned directory.' }
    Assert-NoReparsePoint $full
    return $full
}

# Checks every existing ancestor, including junctions above a nonexistent destination.
function Assert-NoReparsePoint {
    param([string]$Path)
    for ($current = $Path; $current; $current = Split-Path -Parent $current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Reparse points are not supported: '$current'."
            }
        }
    }
}

# Creates private staging and rollback directories with their ACL in place at creation.
function New-PrivateDirectory {
    param([string]$Path)
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.SetOwner($sid)
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($identity in @($sid.Value, 'S-1-5-18', 'S-1-5-32-544')) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            (New-Object Security.Principal.SecurityIdentifier($identity)), 'FullControl',
            'ContainerInherit, ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    if ($PSVersionTable.PSVersion.Major -le 5) {
        $null = [IO.Directory]::CreateDirectory($Path, $acl)
    } else {
        [IO.FileSystemAclExtensions]::Create((New-Object IO.DirectoryInfo($Path)), $acl)
    }
}

# Rejects writable destinations owned by other ordinary users.
function Assert-PrivateDestination {
    param([string]$Path)
    $acl = Get-Acl -LiteralPath $Path
    $allowed = @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-18', 'S-1-5-32-544')
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $allowed) {
        throw 'The installation directory must belong to the current user or a Windows administrator.'
    }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and
            -not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and
            $rule.IdentityReference.Value -notin $allowed -and
            ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]'Write, Delete, ChangePermissions, TakeOwnership')) {
            throw 'The installation destination is writable by another user. Choose a private directory.'
        }
    }
}

# Bounds elapsed time and both output streams. Only a child started here may be terminated.
function Invoke-InstallerCli {
    param([string]$Executable, [string]$Arguments)
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = $Arguments
    $start.WorkingDirectory = Split-Path -Parent $Executable
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = [Diagnostics.Process]::Start($start)
    try {
        $readers = @($process.StandardOutput, $process.StandardError)
        $buffers = @((New-Object char[] 4096), (New-Object char[] 4096))
        $texts = @((New-Object Text.StringBuilder), (New-Object Text.StringBuilder))
        $pending = @($readers[0].ReadAsync($buffers[0], 0, 4096), $readers[1].ReadAsync($buffers[1], 0, 4096))
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while ($pending[0] -or $pending[1] -or -not $process.HasExited) {
            if ($clock.Elapsed.TotalSeconds -ge 30) { throw "CLI '$Arguments' timed out after 30 seconds." }
            for ($i = 0; $i -lt 2; $i++) {
                if ($pending[$i] -and $pending[$i].IsCompleted) {
                    $count = $pending[$i].GetAwaiter().GetResult()
                    $pending[$i] = $null
                    if ($count) {
                        if ($texts[$i].Length + $count -gt 65536) { throw 'CLI output exceeded 64 KiB.' }
                        $null = $texts[$i].Append($buffers[$i], 0, $count)
                        $pending[$i] = $readers[$i].ReadAsync($buffers[$i], 0, 4096)
                    }
                }
            }
            Start-Sleep -Milliseconds 10
        }
        if ($process.ExitCode -ne 0) {
            throw "CLI '$Arguments' failed with exit code $($process.ExitCode): $($texts[1].ToString().Trim())"
        }
        return $texts[0].ToString().Trim()
    } finally {
        if (-not $process.HasExited) { $process.Kill(); $null = $process.WaitForExit(1000) }
        $process.Dispose()
    }
}

# Downloads only from GitHub over HTTPS, with a deadline, byte bound, and bounded redirects.
function Receive-InstallerFile {
    param([string]$Uri, [string]$Destination, [long]$MaxBytes, [switch]$BinaryAsset)
    $timeout = New-Object Threading.CancellationTokenSource
    $timeout.CancelAfter(120000)
    try {
        for ($redirect = 0; $redirect -le 5; $redirect++) {
            $address = [uri]$Uri
            if ($address.Scheme -ne 'https' -or $address.Port -ne 443 -or $address.UserInfo -or
                $address.Host -notin @('api.github.com', 'github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com')) {
                throw 'Release downloads must use GitHub HTTPS URLs.'
            }
            $request = New-Object Net.Http.HttpRequestMessage([Net.Http.HttpMethod]::Get, $address)
            $request.Headers.UserAgent.ParseAdd('fiddler-classic-cli-installer')
            if ($address.Host -eq 'api.github.com') {
                $accept = if ($BinaryAsset) { 'application/octet-stream' } else { 'application/vnd.github+json' }
                $request.Headers.Accept.ParseAdd($accept)
                if ($env:GITHUB_TOKEN) { $request.Headers.Authorization = New-Object Net.Http.Headers.AuthenticationHeaderValue('Bearer', $env:GITHUB_TOKEN) }
            }
            $response = $null
            try {
                $response = $httpClient.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $timeout.Token).GetAwaiter().GetResult()
                if ([int]$response.StatusCode -in @(301, 302, 303, 307, 308)) {
                    if (-not $response.Headers.Location) { throw 'GitHub returned a redirect without a location.' }
                    $Uri = (New-Object uri($address, $response.Headers.Location)).AbsoluteUri
                    continue
                }
                $null = $response.EnsureSuccessStatusCode()
                if ($response.Content.Headers.ContentLength -gt $MaxBytes) { throw 'The download exceeds its size limit.' }
                $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                $outputStream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew)
                try {
                    $buffer = New-Object byte[] 81920
                    $total = 0L
                    while (($count = $inputStream.ReadAsync($buffer, 0, $buffer.Length, $timeout.Token).GetAwaiter().GetResult()) -gt 0) {
                        $total += $count
                        if ($total -gt $MaxBytes) { throw 'The download exceeds its size limit.' }
                        $outputStream.Write($buffer, 0, $count)
                    }
                } finally { $outputStream.Dispose(); $inputStream.Dispose() }
                return
            } finally { if ($response) { $response.Dispose() }; $request.Dispose() }
        }
        throw 'GitHub exceeded the redirect limit.'
    } finally { $timeout.Dispose() }
}

# Downloads and verifies the one standalone executable before any CLI invocation.
function Receive-InstallerRelease {
    param([string]$Repository, [string]$Version, [string]$Destination)
    $releaseRoute = if ($Version -eq 'latest') { 'latest' } else { 'tags/' + [uri]::EscapeDataString($Version) }
    Receive-InstallerFile "https://api.github.com/repos/$Repository/releases/$releaseRoute" "$Destination/release.json" 4MB
    $release = [IO.File]::ReadAllText("$Destination/release.json") | ConvertFrom-Json
    if ($release.draft -or [string]::IsNullOrWhiteSpace($release.tag_name) -or
        ($Version -ne 'latest' -and $release.tag_name -cne $Version)) { throw 'GitHub returned an unexpected release.' }
    $found = @($release.assets | Where-Object { $_.name -ceq 'fiddler-classic-cli.exe' })
    if ($found.Count -gt 1) { throw 'GitHub returned duplicate release assets named fiddler-classic-cli.exe.' }
    if ($found.Count -eq 0) {
        throw 'The release lacks standalone fiddler-classic-cli.exe. Choose a release that includes this executable, or explicitly supply a trusted local package with -PackagePath.'
    }
    $asset = $found[0]
    if ($asset.state -cne 'uploaded') { throw 'The release asset is not fully uploaded.' }
    if ($asset.digest -isnot [string] -or $asset.digest -cnotmatch '\Asha256:([A-Fa-f0-9]{64})\z') {
        throw 'The release executable requires a GitHub asset digest in sha256:<64hex> form. Choose a release with a valid digest.'
    }
    $expectedHash = $matches[1]
    $downloadUri = $null
    $expectedPath = "/$Repository/releases/download/$([uri]::EscapeDataString($release.tag_name))/fiddler-classic-cli.exe"
    if (-not [uri]::TryCreate([string]$asset.browser_download_url, [UriKind]::Absolute, [ref]$downloadUri) -or
        $downloadUri.Scheme -ne 'https' -or $downloadUri.Port -ne 443 -or
        $downloadUri.UserInfo -or $downloadUri.Host -ne 'github.com' -or
        $downloadUri.AbsolutePath -cne $expectedPath -or $downloadUri.Query -or $downloadUri.Fragment) {
        throw 'The release asset URL must use GitHub HTTPS and match the selected repository, tag, and filename.'
    }
    $source = Join-Path $Destination 'fiddler-classic-cli.exe'
    if ($env:GITHUB_TOKEN) {
        if ([string]$asset.id -notmatch '^[1-9][0-9]*$') { throw 'GitHub returned an invalid asset ID.' }
        Receive-InstallerFile "https://api.github.com/repos/$Repository/releases/assets/$($asset.id)" $source 512MB -BinaryAsset
    } else {
        Receive-InstallerFile $downloadUri.AbsoluteUri $source 512MB
    }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $expectedHash) {
        throw 'SHA-256 verification failed for fiddler-classic-cli.exe.'
    }
    return $source
}

# Explicit local ZIP input requires one checksum entry with its exact filename.
function Confirm-InstallerChecksum {
    param([string]$Archive, [string]$Manifest)
    Assert-NoReparsePoint $Manifest
    if (-not (Test-Path -LiteralPath $Manifest -PathType Leaf) -or (Get-Item -LiteralPath $Manifest).Length -gt 1MB) {
        throw 'A checksum manifest is required and must be at most 1 MiB.'
    }
    $entries = New-Object 'Collections.Generic.Dictionary[string,string]' ([StringComparer]::Ordinal)
    foreach ($line in [IO.File]::ReadAllLines($Manifest)) {
        if (-not $line.Trim()) { continue }
        if ($line -notmatch '^([A-Fa-f0-9]{64})\s+\*?([^\r\n]+)$') { throw 'SHA256SUMS contains an invalid entry.' }
        if ($entries.ContainsKey($matches[2])) { throw 'SHA256SUMS contains a duplicate entry.' }
        $entries.Add($matches[2], $matches[1].ToLowerInvariant())
    }
    $name = [IO.Path]::GetFileName($Archive)
    if (-not $entries.ContainsKey($name)) { throw "SHA256SUMS has no exact entry for '$name'." }
    $actual = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $entries[$name]) {
        throw "SHA-256 verification failed for '$name'."
    }
}

# Validates the entire ZIP before extraction, including entries that are not runtime files.
function Expand-InstallerArchive {
    param([string]$Archive, [string]$Destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        if ($zip.Entries.Count -gt 10000) { throw 'The archive exceeds 10000 entries.' }
        $names = @{}
        $total = 0L
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            $parts = $name.TrimEnd('/').Split('/')
            $total += $entry.Length
            if ($total -gt 1GB -or $entry.Length -gt 512MB) { throw 'The extracted archive exceeds its size limit.' }
            if (-not $name -or $name.StartsWith('/') -or $name -match '[\x00-\x1f\x7f<>:"|?*]' -or
                @($parts | Where-Object { $_ -in @('', '.', '..') -or $_ -match '[. ]$' -or
                    $_ -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' }).Count -or
                $parts -icontains 'Fiddler.exe') { throw 'The archive contains an unsafe Windows path or a Telerik executable.' }
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000 -or
                ($entry.ExternalAttributes -band [int][IO.FileAttributes]::ReparsePoint)) { throw 'Archive links are not supported.' }
            $key = $name.TrimEnd('/')
            if ($names.ContainsKey($key)) { throw 'The archive contains duplicate Windows paths.' }
            $names[$key] = $name.EndsWith('/')
        }
        foreach ($name in @($names.Keys)) {
            $parent = $name
            while ($parent.Contains('/')) {
                $parent = $parent.Substring(0, $parent.LastIndexOf('/'))
                if ($names.ContainsKey($parent) -and -not $names[$parent]) { throw 'The archive contains a file/directory collision.' }
            }
        }
        [IO.Compression.ZipFileExtensions]::ExtractToDirectory($zip, $Destination)
    } finally { $zip.Dispose() }
}

# Preserves PATH spelling and order outside the fixed entry and recognized old installations.
function Get-InstallerPath {
    param([string]$Value, [string]$Directory)
    $kept = New-Object 'Collections.Generic.List[string]'
    foreach ($entry in ($Value -split ';')) {
        $remove = $false
        try {
            $candidate = [Environment]::ExpandEnvironmentVariables($entry.Trim().Trim('"'))
            $candidate = Get-SafePath $candidate
            $remove = $candidate -ieq $Directory
            if (-not $remove -and (Split-Path -Parent $candidate) -ieq $Directory -and
                (Split-Path -Leaf $candidate) -match '^\d+(?:\.\d+){1,3}(?:[-+_][0-9A-Za-z._-]+)?$' -and
                (Test-Path -LiteralPath "$candidate/install.ps1" -PathType Leaf) -and
                ((Test-Path -LiteralPath "$candidate/fiddler-classic.exe" -PathType Leaf) -or
                    (Test-Path -LiteralPath "$candidate/fiddler-classic-cli.exe" -PathType Leaf))) { $remove = $true }
        } catch { $remove = $false }
        if (-not $remove) { $kept.Add($entry) }
    }
    if ([string]::IsNullOrEmpty($Value)) { return $Directory }
    return (@($Directory) + $kept.ToArray()) -join ';'
}

$stage = $null
$lock = $null
$httpClient = $null
$changes = New-Object 'Collections.Generic.List[object]'
$createdDirectories = New-Object 'Collections.Generic.List[string]'
$preserveStage = $false
$bridgeAttempted = $false
$cliCommitted = $false
$pathAttempted = $false
$failure = $null
$receipt = $null
try {
    if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem) { throw 'fiddler-classic-cli requires 64-bit Windows.' }
    if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Repository must use GitHub owner/name form.' }
    if ($PSBoundParameters.ContainsKey('PackagePath') -and [string]::IsNullOrWhiteSpace($PackagePath)) { throw 'PackagePath must not be empty.' }
    if ($PackagePath -and ($PSBoundParameters.ContainsKey('Version') -or $PSBoundParameters.ContainsKey('Repository'))) {
        throw 'PackagePath cannot be combined with Version or Repository.'
    }
    if ($Version -ne 'latest' -and $Version -notmatch '^v?\d+(?:\.\d+){2}(?:[-.][0-9A-Za-z]+)*$') { throw 'Version must be latest or a release tag.' }
    $InstallDirectory = Get-SafePath $InstallDirectory
    foreach ($broad in @([IO.Path]::GetPathRoot($InstallDirectory), $env:USERPROFILE, $env:LOCALAPPDATA,
            $env:APPDATA, $env:ProgramFiles, [Environment]::GetFolderPath('ProgramFilesX86'), $env:WINDIR,
            [IO.Path]::GetTempPath(), "$env:LOCALAPPDATA/Programs")) {
        if ($broad) {
            $blocked = [IO.Path]::GetFullPath($broad).TrimEnd('\', '/')
            if ($blocked -ieq $InstallDirectory -or $blocked.StartsWith($InstallDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Choose a dedicated installation directory below a user-owned directory.'
            }
        }
    }
    if (-not (Test-Path -LiteralPath $InstallDirectory)) { New-PrivateDirectory $InstallDirectory }
    Assert-PrivateDestination $InstallDirectory
    $lockPath = Join-Path $InstallDirectory '.fiddler-classic-cli.install.lock'
    Assert-NoReparsePoint $lockPath
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $stage = Join-Path $InstallDirectory ('.fiddler-classic-cli-install-' + [guid]::NewGuid().ToString('N'))
    New-PrivateDirectory $stage
    New-PrivateDirectory "$stage/payload"
    New-PrivateDirectory "$stage/rollback"
    if ($PackagePath) {
        $resolvedSource = Resolve-Path -LiteralPath $PackagePath
        if ($resolvedSource.Provider.Name -ne 'FileSystem') { throw 'PackagePath must identify a filesystem path.' }
        $source = Get-SafePath $resolvedSource.ProviderPath
        if (-not (Test-Path -LiteralPath $source)) { throw 'PackagePath does not exist.' }
        if ([IO.File]::Exists($source) -and [IO.Path]::GetExtension($source) -ieq '.zip') {
            # Check and execute the same private snapshot, even if the source is replaced concurrently.
            Copy-Item -LiteralPath $source -Destination (Join-Path $stage ([IO.Path]::GetFileName($source)))
            $manifest = Join-Path (Split-Path -Parent $source) 'SHA256SUMS'
            Assert-NoReparsePoint $manifest
            Copy-Item -LiteralPath $manifest -Destination "$stage/SHA256SUMS"
            $source = Join-Path $stage ([IO.Path]::GetFileName($source))
            Confirm-InstallerChecksum $source "$stage/SHA256SUMS"
        }
    } else {
        Add-Type -AssemblyName System.Net.Http
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $handler = New-Object Net.Http.HttpClientHandler
        $handler.AllowAutoRedirect = $false
        $httpClient = New-Object Net.Http.HttpClient($handler)
        $source = Receive-InstallerRelease $Repository $Version $stage
    }
    if ([IO.File]::Exists($source) -and [IO.Path]::GetExtension($source) -ieq '.zip') {
        Expand-InstallerArchive $source "$stage/expanded"
        $source = "$stage/expanded"
        if (-not ([IO.File]::Exists("$source/fiddler-classic-cli.exe") -or [IO.File]::Exists("$source/fiddler-classic.exe"))) {
            $children = @(Get-ChildItem -LiteralPath $source -Force)
            if ($children.Count -eq 1 -and $children[0].PSIsContainer) { $source = $children[0].FullName }
        }
    }
    if ([IO.Directory]::Exists($source)) {
        $executable = 'fiddler-classic-cli.exe'
        if (-not [IO.File]::Exists("$source/$executable")) { $executable = 'fiddler-classic.exe' }
        if (-not [IO.File]::Exists("$source/$executable")) { throw 'The payload has no Fiddler Classic CLI executable.' }
        # Legacy apphosts keep their original DLL, deps, and runtimeconfig names.
        $files = @(Get-ChildItem -LiteralPath $source -File -Force | Where-Object {
                $_.Name -eq $executable -or $_.Extension -ieq '.dll' -or $_.Name -match '\.(deps\.json|runtimeconfig(?:\.dev)?\.json|exe\.config)$'
            })
        foreach ($file in $files) {
            Assert-NoReparsePoint $file.FullName
            $name = if ($file.Name -eq $executable) { 'fiddler-classic-cli.exe' } else { $file.Name }
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path "$stage/payload" $name)
        }
        if (Test-Path -LiteralPath "$source/bridge") {
            Assert-NoReparsePoint "$source/bridge"
            New-PrivateDirectory "$stage/payload/bridge"
            foreach ($name in @('FiddlerClassicCLI.Bridge.dll', 'FiddlerClassicCLI.Protocol.dll')) {
                Assert-NoReparsePoint "$source/bridge/$name"
                Copy-Item -LiteralPath "$source/bridge/$name" -Destination "$stage/payload/bridge/$name"
            }
        }
    } elseif ([IO.Path]::GetExtension($source) -ieq '.exe') {
        Copy-Item -LiteralPath $source -Destination "$stage/payload/fiddler-classic-cli.exe"
    } else { throw 'PackagePath must identify a trusted executable, publish directory, or checksum ZIP.' }
    $reportedVersion = Invoke-InstallerCli "$stage/payload/fiddler-classic-cli.exe" '--version'
    if ($reportedVersion -notmatch '^\d+(?:\.\d+){1,3}(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') { throw 'The candidate CLI reported an invalid version.' }
    $files = @(Get-ChildItem -LiteralPath "$stage/payload" -File -Recurse)
    foreach ($file in $files) {
        $relative = $file.FullName.Substring(([IO.Path]::GetFullPath("$stage/payload")).Length + 1)
        $target = Join-Path $InstallDirectory $relative
        Assert-NoReparsePoint $target
        $parent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $parent)) { New-PrivateDirectory $parent; $createdDirectories.Add($parent) }
        Assert-PrivateDestination $parent
        if ([IO.File]::Exists($target)) {
            Assert-PrivateDestination $target
            if ((Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash) { continue }
            $backup = Join-Path "$stage/rollback" ([string]$changes.Count)
            [IO.File]::Replace($file.FullName, $target, $backup, $true)
        } else {
            $backup = $null
            [IO.File]::Move($file.FullName, $target)
        }
        $changes.Add([pscustomobject]@{ Target = $target; Backup = $backup })
    }
    $installedExecutable = Join-Path $InstallDirectory 'fiddler-classic-cli.exe'
    if ((Invoke-InstallerCli $installedExecutable '--version') -cne $reportedVersion) { throw 'The installed CLI failed version verification.' }
    $pathUpdated = $false
    if (-not $NoPathUpdate) {
        $oldUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        $oldProcessPath = $env:Path
        $newUserPath = Get-InstallerPath $oldUserPath $InstallDirectory
        $newProcessPath = Get-InstallerPath $oldProcessPath $InstallDirectory
        $pathAttempted = $true
        if ($newUserPath -cne $oldUserPath) { [Environment]::SetEnvironmentVariable('Path', $newUserPath, 'User'); $pathUpdated = $true }
        if ($newProcessPath -cne $oldProcessPath) { $env:Path = $newProcessPath; $pathUpdated = $true }
    }
    # Bridge deployment has its own side effects. Keep the verified CLI if bridge setup fails.
    $cliCommitted = $true
    if (-not $SkipBridge) {
        $bridgeAttempted = $true
        $null = Invoke-InstallerCli $installedExecutable 'bridge install --json'
    }
    $receipt = [ordered]@{ installDirectory = $InstallDirectory; executablePath = $installedExecutable;
        version = $reportedVersion; bridgeInstalled = -not $SkipBridge.IsPresent; pathUpdated = $pathUpdated }
} catch {
    $failure = $_.Exception.Message
    if ($pathAttempted -and -not $cliCommitted) {
        try { [Environment]::SetEnvironmentVariable('Path', $oldUserPath, 'User'); $env:Path = $oldProcessPath }
        catch { $failure += " PATH restoration failed: $($_.Exception.Message)" }
    }
    for ($i = $changes.Count - 1; -not $cliCommitted -and $i -ge 0; $i--) {
        $change = $changes[$i]
        try {
            Assert-NoReparsePoint $change.Target
            if ($change.Backup) {
                if ([IO.File]::Exists($change.Target)) { [IO.File]::Replace($change.Backup, $change.Target, [NullString]::Value, $true) }
                else { [IO.File]::Move($change.Backup, $change.Target) }
            } else { Remove-Item -LiteralPath $change.Target -Force }
        } catch { $preserveStage = $true; $failure += " Rollback failed for '$($change.Target)': $($_.Exception.Message)" }
    }
    foreach ($directory in $createdDirectories) {
        if ([IO.Directory]::Exists($directory) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { [IO.Directory]::Delete($directory) }
    }
    if ($bridgeAttempted) {
        $failure = "CLI installed at '$installedExecutable'. Bridge setup failed: $failure Close Fiddler normally and run '$installedExecutable bridge install --json' to retry. Bridge files may have changed."
    }
} finally {
    if ($httpClient) { $httpClient.Dispose() }
    if ($stage -and -not $preserveStage) {
        try {
            Assert-NoReparsePoint $stage
            $stageFull = [IO.Path]::GetFullPath($stage)
            if ((Split-Path -Parent $stageFull) -ine $InstallDirectory -or (Split-Path -Leaf $stageFull) -notmatch '^\.fiddler-classic-cli-install-[a-f0-9]{32}$') { throw 'Unexpected staging directory.' }
            Remove-Item -LiteralPath $stageFull -Recurse -Force
        } catch { [Console]::Error.WriteLine("Private staging retained at '$stage': $($_.Exception.Message)") }
    }
    if ($preserveStage) { [Console]::Error.WriteLine("Rollback files retained at '$stage'.") }
    if ($lock) { $lock.Dispose() }
}
if ($failure) { [Console]::Error.WriteLine($failure); exit 1 }
if ($Json) { [Console]::WriteLine(($receipt | ConvertTo-Json -Compress)) }
else { [Console]::WriteLine("Installed fiddler-classic-cli $($receipt.version) to $($receipt.installDirectory).") }
