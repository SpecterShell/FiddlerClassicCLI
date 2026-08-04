param(
    [string]$Configuration = "Release",
    [string]$FiddlerInstallDir = "$env:LOCALAPPDATA/Programs/Fiddler",
    [string]$Version
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
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

$publishArguments = @($commonArguments) + @("-p:PublishProfile=win-x64")
& dotnet publish "$repositoryRoot/src/FiddlerClassic.Host/FiddlerClassic.Host.csproj" @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE. Stop a daemon running from the publish directory and retry."
}

Write-Host "Published to $repositoryRoot/artifacts/publish/win-x64"
