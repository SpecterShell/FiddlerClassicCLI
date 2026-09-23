# Clears only the fixed generated publish or release directory, refusing redirected paths.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('publish/win-x64', 'release')]
    [string]$Directory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$target = [IO.Path]::GetFullPath((Join-Path $artifactsRoot $Directory))
if (-not $target.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to clean outside the repository artifacts directory.'
}

# Check ancestors and descendants before recursive removal so junctions cannot redirect cleanup.
$ancestor = $target
while ($ancestor) {
    if (Test-Path -LiteralPath $ancestor) {
        $item = Get-Item -LiteralPath $ancestor -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to clean a redirected or non-directory path: '$ancestor'."
        }
    }
    $ancestor = Split-Path -Parent $ancestor
}
if (Test-Path -LiteralPath $target) {
    $links = @(Get-ChildItem -LiteralPath $target -Force -Recurse | Where-Object {
            $_.Attributes -band [IO.FileAttributes]::ReparsePoint
        })
    if ($links.Count -ne 0) { throw "Refusing to clean links inside '$target'." }
    Remove-Item -LiteralPath $target -Recurse -Force
}
New-Item -ItemType Directory -Path $target | Out-Null
