# Installation

**English (en-US)** | [简体中文 (zh-CN)](../zh-CN/installation.md)

Fiddler Classic CLI ships as one self-contained Windows x64 executable, `fiddler-classic-cli.exe`. It embeds the .NET runtime, managed dependencies, bridge DLLs, and `LICENSE`. When it runs, .NET extracts native runtime files to `%TEMP%\.net` by default. The install directory defaults to `%LOCALAPPDATA%\Programs\FiddlerClassicCLI`. Updates use the same executable path.

## Latest release

Run the bootstrap in PowerShell. This command executes the [repository's installer](https://github.com/SpecterShell/FiddlerClassicCLI/blob/main/scripts/install.ps1), so review the script first and use only a source you trust:

```powershell
irm https://raw.githubusercontent.com/SpecterShell/FiddlerClassicCLI/main/scripts/install.ps1 | iex
```

By default, the bootstrap downloads the asset named exactly `fiddler-classic-cli.exe` from the latest GitHub release of `SpecterShell/FiddlerClassicCLI`. It requires a valid `sha256` asset digest from GitHub and verifies the downloaded bytes before execution. A missing executable, missing digest, or checksum mismatch stops installation. It installs the CLI and Fiddler bridge and adds the fixed install directory to the current-user `PATH`. It does not start Fiddler, the CLI daemon, or MCP servers, install Agent Skills, or change certificate trust and HTTPS-decryption settings.

Use the installed path directly in a terminal or agent process that has not refreshed its environment:

```powershell
$cliPath = Join-Path $env:LOCALAPPDATA "Programs/FiddlerClassicCLI/fiddler-classic-cli.exe"
& $cliPath --version
```

Running components retain their loaded code after installation. Restart Fiddler, the daemon, and any MCP servers or clients explicitly when an update needs to take effect. Save needed captures before closing or restarting Fiddler.

## Review the installer before running

To download the script for inspection, save it to a unique temporary path:

```powershell
$installerPath = Join-Path ([IO.Path]::GetTempPath()) ("fiddler-classic-cli-" + [guid]::NewGuid() + ".ps1")
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/SpecterShell/FiddlerClassicCLI/main/scripts/install.ps1" -OutFile $installerPath -ErrorAction Stop
Get-Content -LiteralPath $installerPath
```

After reviewing the script, invoke it and remove the temporary copy:

```powershell
& $installerPath
Remove-Item -LiteralPath $installerPath
```

## Bootstrap options

`scripts/install.ps1` installs the CLI. Without `-PackagePath`, the script downloads the selected GitHub release executable, regardless of the working directory.

| Parameter | Behavior |
| --- | --- |
| `-PackagePath PATH` | Installs an explicitly selected local executable, directory, or ZIP with an adjacent checksum manifest. |
| `-Repository owner/name` | Selects a GitHub repository. Defaults to `SpecterShell/FiddlerClassicCLI`. |
| `-Version TAG` | Selects an exact release tag. Omit it for the latest release. |
| `-InstallDirectory PATH` | Selects a dedicated absolute local installation directory. Defaults to `%LOCALAPPDATA%\Programs\FiddlerClassicCLI`. |
| `-NoPathUpdate` | Leaves `PATH` unchanged. |
| `-SkipBridge` | Installs only the CLI, leaving any existing bridge and host launch record unchanged. |
| `-Json` | Returns a structured installation result. |

`-PackagePath` cannot be combined with `-Repository` or `-Version`. From the source checkout, install a local publish directory with:

```powershell
./scripts/install.ps1 -PackagePath ./artifacts/publish/win-x64
```

An explicit local EXE is a trusted source and does not require an adjacent checksum manifest:

```powershell
./scripts/install.ps1 -PackagePath "C:/Downloads/fiddler-classic-cli.exe"
```

A local ZIP requires `SHA256SUMS` in the same directory, with a matching checksum for the exact ZIP filename. Local directories are trusted inputs. Current releases provide only the EXE, and the bootstrap downloads only that asset. GitHub downloads always require verification against GitHub's SHA-256 asset digest. For a private GitHub repository, provide `GITHUB_TOKEN` through the process environment and keep it out of logs.

To select another install directory without changing `PATH`:

```powershell
./scripts/install.ps1 -InstallDirectory "C:/Tools/FiddlerClassicCLI" -NoPathUpdate
```

## Local development

From the source checkout, publish the current code and install that build:

```powershell
./scripts/build.ps1
./scripts/install-local.ps1
```

`scripts/install-local.ps1` uses `artifacts/publish/win-x64/fiddler-classic-cli.exe` relative to its own checkout, regardless of the working directory. It calls the shared installer with that local EXE as `-PackagePath`. It never builds or downloads a release. If the publish output is missing, it stops and asks you to run `scripts/build.ps1`.

The default installation updates `%LOCALAPPDATA%\Programs\FiddlerClassicCLI`, the current-user PATH, and the Fiddler bridge. It replaces the installed CLI even when the local build reports the same version. Save needed captures and close Fiddler normally before replacing a loaded bridge. Stop any daemon or MCP server using the installed executable before updating it. Start those components explicitly after installation.

The script forwards `-InstallDirectory`, `-NoPathUpdate`, `-SkipBridge`, and `-Json` to the shared installer. To keep the usual CLI installation, PATH, and bridge unchanged, install into a separate directory:

```powershell
./scripts/install-local.ps1 -InstallDirectory "$env:LOCALAPPDATA/Programs/FiddlerClassicCLI-Dev" -NoPathUpdate -SkipBridge
```

Use the executable in that directory directly. Omitting `-SkipBridge` deploys the development bridge and points Fiddler's host launch record at the development executable. `-Json` returns the same installation receipt as the shared installer, and failures return a nonzero exit code.

## Use a standalone executable

The sole release asset is `fiddler-classic-cli.exe`. Releases have no ZIP or separate checksum file. Use the bootstrap for automatic download verification, or compare a manually downloaded EXE's SHA-256 with the asset digest returned by GitHub before running it. Documentation and Agent Skill files are available in the source repository.

Place a verified executable in a permanent directory, then deploy its embedded bridge:

```powershell
./fiddler-classic-cli.exe bridge install
```

`bridge install` deploys the extension and records the current executable path. It leaves the CLI in place and keeps `PATH` unchanged. Keep that executable at the recorded path so Fiddler can start the daemon. To copy a local executable into the fixed installation directory and update `PATH`, use `scripts/install.ps1` with `-PackagePath` as shown above.

The PowerShell installer and `bridge install` do not start Fiddler, the daemon, or MCP servers or set up Agent Skills. Obtain `skills/fiddler-classic-cli` from the source repository for separate, user-directed setup.

## Installation errors

The installer rolls back a failed CLI file transaction. If bridge setup fails after the CLI files are committed, the verified CLI remains installed and the script returns a nonzero exit code. Bridge files may have changed. Save needed captures, close Fiddler normally, then retry `fiddler-classic-cli bridge install` using the installed executable.

## Fiddler bridge

The default installation extracts the embedded `FiddlerClassicCLI.Bridge.dll` and `FiddlerClassicCLI.Protocol.dll` into `%USERPROFILE%\Documents\Fiddler2\Scripts`. It writes `%LOCALAPPDATA%\FiddlerClassicCLI\bridge-host.json` with the installed host path and version. This current-user launch record lets the extension find the host when Fiddler later loads.

If you used `scripts/install.ps1 -SkipBridge` or need to repair the bridge, run:

```powershell
& $cliPath bridge install
```

On the first Fiddler startup after bridge installation, an extension approval window titled "Caution: Unverified Extension Detected" may appear separately for `FiddlerClassicCLI.Bridge.dll` and `FiddlerClassicCLI.Protocol.dll`. Each prompt asks permission to load the named DLL from the Fiddler Scripts directory.

Check the full path and file name in each window. Choose `Allow` only if you trust the installed file. Choose `Always allow` if you also want Fiddler to remember that approval. If the file or its source is unfamiliar, choose `Do not allow` and check the installation source before proceeding. Handle both prompts in Fiddler yourself. The installer and CLI leave these trust decisions to you, and the bridge may remain unavailable while approval is pending or denied.

After explicitly starting or restarting Fiddler Classic and handling any extension prompts, verify the connection:

```powershell
& $cliPath doctor
& $cliPath status --json
```

The **Fiddler Classic CLI** tab and its Tools menu shortcut manage MCP HTTP access separately from Fiddler's capture proxy. Bridge installation does not install Fiddler or change the system proxy, certificates, or HTTPS decryption. `bridge uninstall` removes the extension files and launch record after confirmation.

In **MCP HTTP service**, the status light is green while listening, red when stopped or disabled, and gray before a status check or when a check fails. The text beside it describes the state. Each loopback or LAN URL has a clipboard-icon copy button immediately beside it. Long URLs wrap as the pane narrows. When space is limited, copy buttons show just the icon and retain their tooltip and keyboard shortcut. The bind dropdown fits its choices using the font Windows actually renders.

Use `& $cliPath app detect --json` to locate Fiddler, or add `--path "C:/Tools/Fiddler/Fiddler.exe"` for a custom installation. If Fiddler is closed, `& $cliPath app open` starts it with `-noattach`. To restart it from the CLI, save needed captures first, then run `& $cliPath app restart --yes`. Native dialogs may need attention. See [application commands](cli.md#fiddler-application) for process selection and timeouts.

### Named pipes

The tab's read-only **Named pipes** section is near **Versions**, separate from MCP HTTP controls. It shows the full bridge and daemon paths as `\\.\pipe\<name>`, each with a copy button. It also lists the supported bridge protocol (v3), daemon protocol (v1), and the access restriction **Current Windows user only**. Windows ACLs enforce this restriction.

Bridge status reflects the local listener lifecycle: `Stopped`, `Starting`, `Listening`, `Retrying`, or `Unavailable`. Check the displayed last listener error if it cannot listen. A successful daemon status check shows `Responding`, its PID, and its UTC start time (`StartedAtUtc`). A timeout or unavailable response leaves the daemon's state unknown and clears the PID and start time to `Unknown`. A failed check alone cannot establish that the daemon has stopped. Check the host installation with `doctor` and retry the status check.

Status refreshes every two seconds without overlapping requests. **Refresh pipes** reads only the local bridge snapshot and daemon status. It never starts the daemon or configures listeners. The extension still starts or discovers the daemon during initialization, independently of this button or tab selection. This section exposes no credentials, tokens, or traffic contents and keeps no pipe connection counts or history.

## Build and package

Building requires the .NET 10 SDK and a local Fiddler Classic 5.x or 6.x installation:

```powershell
./scripts/build.ps1
```

Create the release executable with:

```powershell
./scripts/package.ps1
```

For a tagged build, pass the tag's semantic version without the leading `v`:

```powershell
./scripts/package.ps1 -Version "0.3.0-preview.1"
```

Normal builds clean `artifacts/publish/win-x64` before publishing and leave only `fiddler-classic-cli.exe` there. The packaging script verifies that output, clears the generated `artifacts/release` directory, and copies only the executable. `-SkipBuild` verifies and copies the existing publish output without rebuilding it. Compilation intermediates and test files stay outside both output directories.

Release verification requires exactly one Windows x64 PE executable with the expected filename. It rejects additional files or directories, reparse points, and malformed executable headers, and checks SHA-256 when an expected digest is supplied. `./scripts/test-release-verifier.ps1` exercises these rejection rules. `./scripts/test-single-file.ps1` checks standalone execution and embedded resources, and `./scripts/test-release-install.ps1` checks installation in temporary locations. These checks must preserve the user's installed CLI, bridge, and environment.

## GitHub Actions

The repository's [Build and Release workflow](../../.github/workflows/build-release.yml) uses a GitHub-hosted `windows-2025` runner for compilation and an Ubuntu runner for release publication.

The build job performs these steps:

1. Installs the SDK selected by `global.json`.
2. Installs WinGet package `Telerik.Fiddler.Classic` at version `6.0.20261.7291`. If WinGet is unavailable or does not install the exact `Fiddler.exe` version, the job installs the pinned Chocolatey package instead.
3. Verifies `Fiddler.exe` at `%LOCALAPPDATA%\Programs\Fiddler` before compilation.
4. Runs `scripts/package.ps1`, which tests the solution, publishes the self-contained host, and verifies that the publish and release directories each contain only `fiddler-classic-cli.exe`.
5. Runs release rejection, standalone execution, and temporary install/reinstall/upgrade checks. It records SHA-256 values for the executable and its embedded bridge as job outputs.
6. Uploads only `fiddler-classic-cli.exe` as a 14-day workflow artifact. Digests pass to downstream jobs as workflow metadata.

The build job runs for pull requests, pushes to `main`, tags matching `v*`, and manual dispatches. Its token has read-only repository access.

For a version tag, the release job downloads the executable produced by that same build and runs `scripts/publish-release.ps1` using the workflow's `GITHUB_TOKEN` with `contents: write`. The script verifies the single-file output and its expected SHA-256 before contacting GitHub. It uploads only `fiddler-classic-cli.exe`. If the release already exists, it preserves its title, notes, draft status, and prerelease setting. Otherwise, it creates a release after verifying that the tag exists. Stable tags use the form `v0.3.0`. A suffix such as `v0.3.0-preview.1` marks a newly created release as a prerelease.

On a rerun, an existing `fiddler-classic-cli.exe` asset must have an uploaded state and a SHA-256 digest matching the local file. The script stops if it differs or lacks a verifiable digest. It never replaces existing files, and an immutable release must already contain the matching executable. Authentication and connection failures stop publication. `scripts/test-release-publication.ps1` checks these decisions with a fake GitHub CLI and a local executable fixture on both workflow platforms, without making GitHub requests.

Every workflow runs a metadata-only compatibility matrix against `5.0.20253.3311` and `6.0.20261.7291`. Each runner downloads the same release EXE and checks its SHA-256 before extracting its embedded bridge into a test directory. It then checks the bridge hash and resolves direct API references without executing Fiddler code. Both matrix jobs must pass before publication. This check does not verify native binding policy, reflection-by-name behavior, or UI correctness.

The manual workflow includes a `run_fiddler_compatibility` option. Selecting it also runs native Fiddler in the matrix for lifecycle and evidence checks. The matrix compiles only the checker and test probe for each reference. It never rebuilds the production bridge or protocol DLLs.

The native probe checks tab and Tools-menu registration, daemon startup while the tab remains unselected, duplicate loading, controlled in-memory session inspection, exact binary body ranges, and unload cleanup. Its script rejects local profiles and requires explicit opt-in to run on a disposable runner. It does not send traffic or attach the system proxy. A successful local build does not establish that these native matrix checks have run.

The normal build uses Fiddler as a compile reference. Only explicitly enabled compatibility jobs use it as a native test host. Release artifacts never include `Fiddler.exe`, test probes, generated credentials, or native profiles.
