param(
    [string]$Configuration = "Release",
    [string]$FiddlerInstallDir = "$env:LOCALAPPDATA/Programs/Fiddler",
    [string]$Version
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $repositoryRoot 'artifacts/publish/win-x64'
$commonArguments = @(
    "--configuration", $Configuration,
    "-p:FiddlerInstallDir=$FiddlerInstallDir"
)
if ($Version) {
    $commonArguments += "-p:Version=$Version"
}

& dotnet test "$repositoryRoot/FiddlerClassicCLI.slnx" @commonArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet test failed with exit code $LASTEXITCODE."
}

& "$PSScriptRoot/reset-artifact-directory.ps1" -Directory 'publish/win-x64'

$publishArguments = @($commonArguments) + @("-p:PublishProfile=win-x64")
& dotnet publish "$repositoryRoot/src/FiddlerClassicCLI.Host/FiddlerClassicCLI.Host.csproj" @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE. Stop a daemon running from the publish directory and retry."
}

& "$PSScriptRoot/verify-release.ps1" -ReleaseDirectory $publishDirectory
Write-Host "Published to $publishDirectory"
