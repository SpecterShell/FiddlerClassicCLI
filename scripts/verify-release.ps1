<#
.SYNOPSIS
Verifies a packaged Fiddler Classic CLI release before artifact upload or publication.

.PARAMETER ReleaseDirectory
The directory containing fiddler-classic-win-x64.zip and SHA256SUMS.
#>
[CmdletBinding()]
param(
    [string]$ReleaseDirectory = "$PSScriptRoot/../artifacts/release"
)

$ErrorActionPreference = "Stop"
$resolvedReleaseDirectory = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$assetName = "fiddler-classic-win-x64.zip"
$assetPath = Join-Path $resolvedReleaseDirectory $assetName
$checksumPath = Join-Path $resolvedReleaseDirectory "SHA256SUMS"

if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
    throw "Release asset '$assetName' was not found in '$resolvedReleaseDirectory'."
}

if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
    throw "Release checksum manifest was not found in '$resolvedReleaseDirectory'."
}

$manifestLines = @(Get-Content -LiteralPath $checksumPath | Where-Object { $_.Trim() })
if ($manifestLines.Count -ne 1 -or
    $manifestLines[0] -notmatch "^([A-Fa-f0-9]{64})\s+\*?(.+)$" -or
    $matches[2].Trim() -ne $assetName) {
    throw "SHA256SUMS must contain one exact entry for '$assetName'."
}

$expectedHash = $matches[1].ToLowerInvariant()
$actualHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    throw "SHA-256 verification failed for '$assetName'."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($assetPath)
try {
    # Apply Windows path rules on every verifier platform, including the Linux release job.
    $entryNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $fileNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        $parts = $name.TrimEnd('/').Split('/')
        if ($parts -icontains 'Fiddler.exe') {
            throw "Release archive must not redistribute Fiddler.exe."
        }

        if ([string]::IsNullOrWhiteSpace($name) -or $name.StartsWith('/') -or
            $name -match '[\x00-\x1f\x7f<>:"|?*]' -or
            @($parts | Where-Object {
                $_ -in @('', '.', '..') -or $_ -match '[. ]$' -or
                $_ -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)'
            }).Count -ne 0) {
            throw "Release archive contains an unsafe Windows path."
        }

        # Symlinks are not release payloads; extraction behavior differs across platforms.
        if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) {
            throw "Release archive must not contain symbolic links."
        }
        if (-not $entryNames.Add($name.TrimEnd('/'))) {
            throw "Release archive contains duplicate Windows paths."
        }
        if (-not $name.EndsWith('/')) { $null = $fileNames.Add($name) }
    }
    foreach ($name in $entryNames) {
        $parent = $name
        while ($parent.Contains('/')) {
            $parent = $parent.Substring(0, $parent.LastIndexOf('/'))
            if ($fileNames.Contains($parent)) {
                throw "Release archive contains a file/directory path collision."
            }
        }
    }
    $requiredEntries = @(
        "fiddler-classic.exe",
        "LICENSE",
        "install.ps1",
        "bridge/FiddlerClassic.Bridge.dll",
        "bridge/FiddlerClassic.Protocol.dll",
        "skills/fiddler-classic-cli/SKILL.md",
        "skills/fiddler-classic-cli/references/diagnostics-and-runtime.md",
        "skills/fiddler-classic-cli/references/capture-and-inspection.md",
        "skills/fiddler-classic-cli/references/session-actions.md",
        "skills/fiddler-classic-cli/references/autoresponder.md",
        "skills/fiddler-classic-cli/references/breakpoints.md",
        "docs/en-US/installation.md",
        "docs/zh-CN/installation.md"
    )

    foreach ($requiredEntry in $requiredEntries) {
        if (-not $fileNames.Contains($requiredEntry)) {
            throw "Release archive is missing '$requiredEntry'."
        }
    }

    $nestedSkillScripts = @($entryNames | Where-Object { $_ -like "skills/fiddler-classic-cli/scripts/*" })
    if ($nestedSkillScripts.Count -ne 0) {
        throw "Release archive must not contain scripts inside the Fiddler Classic CLI skill."
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Verified $assetName ($actualHash)."
