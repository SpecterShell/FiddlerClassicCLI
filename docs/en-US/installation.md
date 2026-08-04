# Installation

**English (en-US)** | [简体中文 (zh-CN)](../zh-CN/installation.md)

Fiddler Classic CLI is distributed as a self-contained Windows x64 directory. The installer writes only to the current user's local application data and `PATH`; the Fiddler bridge is installed separately.

## Release Package

A release consists of these two assets:

- `fiddler-classic-win-x64.zip`
- `SHA256SUMS`

Verify that the SHA-256 digest of the ZIP matches its exact filename entry in `SHA256SUMS`, then extract the archive. Run the bundled installer from the extracted directory:

```powershell
$cliPath = (./install.ps1 | Select-Object -Last 1)
& $cliPath --version
```

The installer copies the complete payload to `%LOCALAPPDATA%\Programs\FiddlerClassicCLI\<version>` and places that version directory first among managed Fiddler Classic CLI entries in the current-user `PATH`. It also updates `PATH` in the PowerShell process that invokes the script. Use the returned `$cliPath` in long-running Agent processes whose environment may already be cached.

To leave `PATH` unchanged or select another current-user directory:

```powershell
$cliPath = (./install.ps1 -InstallRoot "C:/Tools/FiddlerClassicCLI" -NoPathUpdate | Select-Object -Last 1)
```

## Agent Bootstrap

The Agent Skill contains the same installer at `skills/fiddler-classic-cli/scripts/install.ps1`. When the skill is inside an extracted release, invoking the script without source arguments installs the adjacent payload.

An installed skill can download a release from a trusted GitHub repository. Supply the identity explicitly in `owner/name` form:

```powershell
$cliPath = (& "$skillRoot/scripts/install.ps1" -Repository $trustedRepository | Select-Object -Last 1)
& $cliPath --version
```

The installer resolves the release through the GitHub API, downloads `fiddler-classic-win-x64.zip` and `SHA256SUMS`, requires an exact manifest entry, and verifies SHA-256 before extraction. Set `-Version` to an exact release tag when a specific version is required. For a private repository, set `GITHUB_TOKEN` in the process environment; the installer does not print it.

For a previously downloaded ZIP, keep `SHA256SUMS` in the same directory and run:

```powershell
$cliPath = (& "$skillRoot/scripts/install.ps1" -PackagePath $trustedPackagePath | Select-Object -Last 1)
```

## Fiddler Bridge

After installing the CLI, install the extension files:

```powershell
& $cliPath bridge install
```

Restart Fiddler Classic so it loads the extension, then verify all components:

```powershell
& $cliPath doctor
& $cliPath status --json
```

`bridge install` copies `FiddlerClassic.Bridge.dll` and `FiddlerClassic.Protocol.dll` to `%USERPROFILE%\Documents\Fiddler2\Scripts`. It does not install Fiddler, change the system proxy, or configure certificate trust.

## Build and Package

Building requires the .NET 10 SDK and a local Fiddler Classic 5.x installation:

```powershell
./scripts/build.ps1
```

Create the release ZIP and checksum manifest with:

```powershell
./scripts/package.ps1
```

For a tagged build, pass the tag value without altering its semantic version text:

```powershell
./scripts/package.ps1 -Version "0.3.0-preview.1"
```

The release assets are written to `artifacts/release`.

## GitHub Actions

The repository's [Build and Release workflow](../../.github/workflows/build-release.yml) uses a GitHub-hosted `windows-2025` runner for compilation and an Ubuntu runner for release publication.

The build job performs these steps:

1. Installs the SDK selected by `global.json`.
2. Installs WinGet package `Telerik.Fiddler.Classic` at version `5.0.20262.6151`. If WinGet is unavailable or does not produce the exact `Fiddler.exe` version, the job installs the pinned Chocolatey package instead.
3. Verifies `Fiddler.exe` at `%LOCALAPPDATA%\Programs\Fiddler` before compilation.
4. Runs `scripts/package.ps1`, which executes all automated tests, publishes the self-contained host, creates the ZIP and checksum manifest, and verifies the archive contract.
5. Uploads `fiddler-classic-win-x64.zip` and `SHA256SUMS` as a 14-day workflow artifact.

The build job runs for pull requests, pushes to `main`, tags matching `v*`, and manual dispatches. Its token has read-only repository access.

For a version tag, the release job downloads the artifact produced by that same build, verifies it again, and creates a GitHub release using the workflow's `GITHUB_TOKEN` with `contents: write`. Stable tags use the form `v0.3.0`; a suffix such as `v0.3.0-preview.1` marks the release as a prerelease. The workflow verifies that the tag already exists and does not replace assets in an existing release.

Fiddler is installed only as a compile reference on the ephemeral Windows runner. `Fiddler.exe` is never copied into the publish directory, workflow artifact, or GitHub release.
