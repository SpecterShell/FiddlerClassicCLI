# Installation

**English (en-US)** | [简体中文 (zh-CN)](../zh-CN/installation.md)

Fiddler Classic CLI is distributed as a self-contained Windows x64 directory. By default, the installer writes to the current user's local application data directory and updates their `PATH`. Install the Fiddler bridge separately.

## Release package

Each release contains two assets:

- `fiddler-classic-win-x64.zip`
- `SHA256SUMS`

Before extracting the ZIP, verify its SHA-256 digest against the entry in `SHA256SUMS` with the exact same filename. Run the bundled installer from the extracted directory:

```powershell
$cliPath = (./install.ps1 | Select-Object -Last 1)
& $cliPath --version
```

The installer copies the complete payload to `%LOCALAPPDATA%\Programs\FiddlerClassicCLI\<version>` and replaces managed Fiddler Classic CLI entries in the current-user `PATH` with that version directory. It also updates `PATH` in the PowerShell process that invokes the script. In long-running agent processes that may have cached their environment, use the returned `$cliPath`.

Before replacing an existing installation, stop its foreground MCP servers and run `daemon stop`. After installation, restart MCP clients with the returned executable path and run `daemon start` from that build if needed. Running processes retain their loaded SDK; copying files alone does not update MCP support. If the install path changed, follow the bridge installation steps below to update its host launch record.

To leave `PATH` unchanged or select another current-user directory, run:

```powershell
$cliPath = (./install.ps1 -InstallRoot "C:/Tools/FiddlerClassicCLI" -NoPathUpdate | Select-Object -Last 1)
```

## Agent bootstrap

The installer is at the root of both the repository and the extracted release. After loading the included skill, locate that shared root from the directory containing `SKILL.md`:

```powershell
$distributionRoot = Split-Path -Parent (Split-Path -Parent $skillRoot)
$installerPath = Join-Path $distributionRoot "install.ps1"
```

In an extracted release, running `$installerPath` without source arguments installs the payload beside the installer. A separately installed copy of the skill does not contain the installer. If `$installerPath` is absent, use an extracted release or a trusted source checkout.

The root installer can download a release from a trusted GitHub repository. Specify the repository explicitly in `owner/name` form:

```powershell
$cliPath = (& $installerPath -Repository $trustedRepository | Select-Object -Last 1)
& $cliPath --version
```

The installer locates the release through the GitHub API and downloads `fiddler-classic-win-x64.zip` and `SHA256SUMS`. It requires a manifest entry with the exact filename and verifies SHA-256 before extraction. To install a specific version, set `-Version` to the exact release tag. For a private repository, set `GITHUB_TOKEN` in the process environment; the installer does not print it.

For a previously downloaded ZIP, keep `SHA256SUMS` in the same directory and run:

```powershell
$cliPath = (& $installerPath -PackagePath $trustedPackagePath | Select-Object -Last 1)
```

## Fiddler bridge

After installing the CLI, install the extension files:

```powershell
& $cliPath bridge install
```

Restart Fiddler Classic so it loads the extension, then verify all components:

```powershell
& $cliPath doctor
& $cliPath status --json
```

`bridge install` copies `FiddlerClassic.Bridge.dll` and `FiddlerClassic.Protocol.dll` to `%USERPROFILE%\Documents\Fiddler2\Scripts`. It atomically writes `%LOCALAPPDATA%\FiddlerClassicCLI\bridge-host.json`, recording the exact installed host executable and version so the extension can start or find the daemon. Only the current user can access this file; `bridge uninstall` removes it.

After Fiddler restarts, the **Fiddler Classic CLI** tab appears in the main tab strip. A shortcut in the Tools menu selects the tab. The tab manages MCP HTTP access without changing Fiddler's capture proxy. Bridge installation does not install Fiddler, change the system proxy, or configure certificate trust.

Use `& $cliPath app detect --json` to locate Fiddler, or add `--path "C:/Tools/Fiddler/Fiddler.exe"` for a custom installation. If Fiddler is closed, `& $cliPath app open` starts it without attaching the system proxy. To restart it from the CLI, save needed captures first, then run `& $cliPath app restart --yes`. Native dialogs may still need attention. See [application commands](cli.md#fiddler-application) for process selection and timeout behavior.

## Build and package

Building requires the .NET 10 SDK and a local Fiddler Classic 5.x or 6.x installation:

```powershell
./scripts/build.ps1
```

Create the release ZIP and checksum manifest with:

```powershell
./scripts/package.ps1
```

For a tagged build, pass the tag's semantic version without the leading `v`:

```powershell
./scripts/package.ps1 -Version "0.3.0-preview.1"
```

The packaging script writes release assets to `artifacts/release`. Archive verification rejects `Fiddler.exe` regardless of directory depth or filename case. It also rejects unsafe Windows paths, duplicate paths, symbolic links, and file/directory collisions. The archive must contain the root installer and all five skill references, and must not contain scripts inside the skill. Normal builds clean the fixed publish directory before publishing; `-SkipBuild` packages the existing directory.

Run `./scripts/test-release-verifier.ps1` to test archive rejection cases. `./scripts/test-release-install.ps1 -FixtureOnly` safely checks fresh installation, same-version reinstall, and side-by-side upgrade using inert fixtures that only report their version. It hashes installed files, preserves the prior version, and verifies that `-NoPathUpdate` leaves both PATH values unchanged. CI runs the same installer checks against the actual release ZIP.

## GitHub Actions

The repository's [Build and Release workflow](../../.github/workflows/build-release.yml) uses a GitHub-hosted `windows-2025` runner for compilation and an Ubuntu runner for release publication.

The build job performs these steps:

1. Installs the SDK selected by `global.json`.
2. Installs WinGet package `Telerik.Fiddler.Classic` at version `6.0.20261.7291`. If WinGet is unavailable or does not install the exact `Fiddler.exe` version, the job installs the pinned Chocolatey package instead.
3. Verifies `Fiddler.exe` at `%LOCALAPPDATA%\Programs\Fiddler` before compilation.
4. Runs `scripts/package.ps1`, which executes all automated tests, publishes the self-contained host, creates the ZIP and checksum manifest, and verifies the archive contract.
5. Runs the archive rejection and temporary install/reinstall/upgrade checks, then records the shipped bridge's SHA-256.
6. Uploads `fiddler-classic-win-x64.zip` and `SHA256SUMS` as a 14-day workflow artifact.

The build job runs for pull requests, pushes to `main`, tags matching `v*`, and manual dispatches. Its token has read-only repository access.

For a version tag, the release job downloads the artifact produced by that same build and runs `scripts/publish-release.ps1` using the workflow's `GITHUB_TOKEN` with `contents: write`. The script verifies the package before contacting GitHub. If the release already exists, it preserves its title, notes, draft status, and prerelease setting and uploads only missing assets. Otherwise, it creates a release after verifying that the tag exists. Stable tags use the form `v0.3.0`; a suffix such as `v0.3.0-preview.1` marks a newly created release as a prerelease.

On a rerun, existing assets must have an uploaded state and a SHA-256 digest matching the local files. The script stops before uploading if an asset differs or lacks a verifiable digest. It never replaces existing files, and immutable releases must already contain both matching assets. Authentication and connection failures stop publication. `scripts/test-release-publication.ps1` checks these decisions with a fake GitHub CLI and a local package on both workflow platforms, without making GitHub requests.

Every workflow runs a metadata-only compatibility matrix against `5.0.20253.3311` and `6.0.20261.7291`. Each runner downloads the same release ZIP, checks its bridge hash, and resolves direct API references without executing Fiddler code. Both matrix jobs must pass before publication. This check does not verify native binding policy, reflection-by-name behavior, or UI correctness.

The manual workflow includes a `run_fiddler_compatibility` option. Selecting it also runs native Fiddler in the matrix for lifecycle and evidence checks. The matrix compiles only the checker and test probe for each reference; it never rebuilds the production bridge or protocol DLLs.

The native probe checks tab and Tools-menu registration, daemon startup while the tab remains unselected, duplicate loading, controlled in-memory session inspection, exact binary body ranges, and unload cleanup. Its script rejects local profiles and requires explicit opt-in to run on a disposable runner. It does not send traffic or attach the system proxy. A successful local build does not establish that these native matrix checks have run.

The normal build uses Fiddler as a compile reference. Only explicitly enabled compatibility jobs use it as a native test host. Release artifacts never include `Fiddler.exe`, test probes, generated credentials, or native profiles.
