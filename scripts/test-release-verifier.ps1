<#
.SYNOPSIS
Exercises release ZIP validation using synthetic temporary archives without executing their contents.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$testRoot = (New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) (
    'fiddler-release-verifier-' + [guid]::NewGuid().ToString('N')))).FullName
$required = @(
    'fiddler-classic.exe', 'LICENSE', 'install.ps1',
    'bridge/FiddlerClassic.Bridge.dll', 'bridge/FiddlerClassic.Protocol.dll',
    'skills/fiddler-classic-cli/SKILL.md',
    'skills/fiddler-classic-cli/references/diagnostics-and-runtime.md',
    'skills/fiddler-classic-cli/references/capture-and-inspection.md',
    'skills/fiddler-classic-cli/references/session-actions.md',
    'skills/fiddler-classic-cli/references/autoresponder.md',
    'skills/fiddler-classic-cli/references/breakpoints.md',
    'docs/en-US/installation.md', 'docs/zh-CN/installation.md'
)

# Creates inert entries and checks that rejection has the expected cause, not a missing-file accident.
function Test-ArchiveCase {
    param([string]$Name, [string[]]$Entries, [string]$ExpectedError, [switch]$Symlink, [switch]$BadHash)
    $caseRoot = (New-Item -ItemType Directory -Path (Join-Path $testRoot $Name)).FullName
    $zipPath = Join-Path $caseRoot 'fiddler-classic-win-x64.zip'
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entryName in $Entries) {
            $entry = $zip.CreateEntry($entryName)
            if ($Symlink -and $entryName -eq 'link') { $entry.ExternalAttributes = -1577123840 }
        }
    }
    finally { $zip.Dispose() }
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    if ($BadHash) { $hash = '0' * 64 }
    [IO.File]::WriteAllText((Join-Path $caseRoot 'SHA256SUMS'), "$hash  fiddler-classic-win-x64.zip`n")
    $failure = $null
    try { & "$PSScriptRoot/verify-release.ps1" -ReleaseDirectory $caseRoot }
    catch { $failure = $_.Exception.Message }
    if ($ExpectedError) {
        if (-not $failure -or $failure -notlike "*$ExpectedError*") {
            throw "Case '$Name' expected '$ExpectedError'; received '$failure'."
        }
    }
    elseif ($failure) { throw "Case '$Name' unexpectedly failed: $failure" }
    Write-Host "PASS $Name"
}

try {
    Test-ArchiveCase -Name valid -Entries $required
    $index = 0
    foreach ($forbidden in @('Fiddler.exe', 'nested/FIDDLER.EXE', 'a/b/fIdDlEr.ExE', 'a\b\Fiddler.exe')) {
        Test-ArchiveCase -Name "telerik-$index" -Entries ($required + $forbidden) -ExpectedError 'redistribute Fiddler.exe'
        $index++
    }
    foreach ($unsafe in @('../escape', 'a/../../escape', '/absolute', '\absolute', 'C:/absolute',
        'C:relative', '//server/share/file', 'a\..\escape', './alias', 'a//alias', 'a/file:stream',
        'a/file.', 'a/file ', 'NUL.txt', 'a/COM1', "a/control`ncharacter")) {
        Test-ArchiveCase -Name "unsafe-$index" -Entries ($required + $unsafe) -ExpectedError 'unsafe Windows path'
        $index++
    }
    Test-ArchiveCase -Name duplicate -Entries ($required + 'INSTALL.PS1') -ExpectedError 'duplicate Windows paths'
    Test-ArchiveCase -Name collision -Entries ($required + 'bridge') -ExpectedError 'file/directory path collision'
    Test-ArchiveCase -Name installer-directory -Entries (@($required | Where-Object { $_ -ne 'install.ps1' }) + 'install.ps1/') -ExpectedError "missing 'install.ps1'"
    Test-ArchiveCase -Name symlink -Entries ($required + 'link') -Symlink -ExpectedError 'symbolic links'
    $nestedInstaller = @('skills', 'fiddler-classic-cli', 'scripts', 'install.ps1') -join '/'
    Test-ArchiveCase -Name nested-installer -Entries ($required + $nestedInstaller) -ExpectedError 'scripts inside'
    Test-ArchiveCase -Name missing-installer -Entries @($required | Where-Object { $_ -ne 'install.ps1' }) -ExpectedError "missing 'install.ps1'"
    Test-ArchiveCase -Name missing-skill -Entries @($required | Where-Object { $_ -notlike '*/SKILL.md' }) -ExpectedError "missing 'skills/fiddler-classic-cli/SKILL.md'"
    Test-ArchiveCase -Name checksum -Entries $required -BadHash -ExpectedError 'SHA-256 verification failed'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'fiddler-release-verifier-*') {
        throw 'Refusing to remove an unexpected verifier test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
