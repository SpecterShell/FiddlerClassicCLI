---
name: fiddler-classic-cli
description: Install, operate, and troubleshoot the unofficial Fiddler Classic CLI on Windows. Use when an agent needs to inspect or watch HTTP(S) requests and WebSocket frames, read exact headers or bodies, export or compare evidence, replay or compose requests, manage capture, archives, AutoResponder rules, breakpoints, the Fiddler application, the CLI daemon, or the bridge, or diagnose connectivity.
---

# Fiddler Classic CLI

Use `fiddler-classic-cli` to inspect and control a running Fiddler Classic 5.x or 6.x instance. The integration supports Windows only. It does not launch Fiddler, trust certificates, or enable HTTPS decryption automatically.

## Resolve the CLI

Find the executable before running commands:

```powershell
$command = Get-Command fiddler-classic-cli -ErrorAction SilentlyContinue
$cliPath = if ($command) { $command.Source } else { $null }
```

If it is missing, obtain approval to download the latest release and install the CLI and bridge for the current user. Use the repository's installer:

```powershell
$installerPath = Join-Path ([IO.Path]::GetTempPath()) ("fiddler-classic-cli-" + [guid]::NewGuid() + ".ps1")
try {
    Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/SpecterShell/FiddlerClassicCLI/main/scripts/install.ps1" -OutFile $installerPath -ErrorAction Stop
    $installResult = & $installerPath -Json | ConvertFrom-Json
} finally {
    Remove-Item -LiteralPath $installerPath -ErrorAction SilentlyContinue
}
$cliPath = $installResult.executablePath
& $cliPath --version
```

The bootstrap downloads the asset named exactly `fiddler-classic-cli.exe` from the latest release of `SpecterShell/FiddlerClassicCLI`. It requires GitHub's SHA-256 asset digest and verifies the downloaded bytes before execution. It installs to `%LOCALAPPDATA%\Programs\FiddlerClassicCLI`, updates the current-user `PATH`, and installs the embedded bridge by default. It does not start Fiddler or the daemon or install Agent Skills.

Use `-SkipBridge` for an approved CLI-only installation and `-NoPathUpdate` to preserve `PATH`. `-InstallDirectory` selects another absolute directory. `-Repository` and `-Version` select another trusted release, and `-Json` returns a structured result. An explicit `-PackagePath` accepts a trusted local EXE or directory, or a ZIP with an adjacent `SHA256SUMS` entry matching its exact filename. Current releases contain only the EXE. Obtain skill files from the source repository for separate, user-directed setup. In a source checkout, the installer is `../../scripts/install.ps1` relative to this skill directory.

Stop if the release EXE or digest is missing, or if downloading, verification, or installation fails. Never bypass verification. For private repositories, supply `GITHUB_TOKEN` through the process environment without printing it. Use the installed executable's absolute path for later commands because a long-running agent may retain an older `PATH`. To install a trusted executable already on disk, pass its path to the shared installer with `-PackagePath`.

## Verify the connection

After first installing the bridge, Fiddler may show a separate "Caution: Unverified Extension Detected" window for `FiddlerClassicCLI.Bridge.dll` and `FiddlerClassicCLI.Protocol.dll` at startup. Ask the user to review each file and handle the prompts in Fiddler, including any decision to use `Always allow`. Do not approve or dismiss these dialogs automatically. Wait for the user's decision before retrying bridge calls.

Run both checks before using bridge-backed commands:

```powershell
& $cliPath daemon status
& $cliPath doctor
```

Commands that access the bridge start the daemon as needed. If `doctor` reports that the bridge is missing, obtain approval, run `& $cliPath bridge install`, restart Fiddler Classic, and rerun `doctor`. Do not reinstall or restart healthy components.

Use `app detect` for read-only installation and process discovery without the bridge. Run `app open` only when the user asks to open Fiddler. New launches use `-noattach`. Obtain approval before `app close` or `app restart` and ask the user to save needed captures. These commands request a normal close and never force-kill Fiddler or dismiss its dialogs.

## Choose a command reference

Read only the reference needed for the task. Use `& $cliPath <command> --help` for the complete option list:

- [Diagnostics and runtime](references/diagnostics-and-runtime.md): health, status, Fiddler application lifecycle, bridge deployment, and daemon control.
- [Capture and inspection](references/capture-and-inspection.md): proxy capture, session inspection, body extraction, comparison, and WebSocket payloads.
- [Session actions](references/session-actions.md): deletion, archives, replay, export, and composed requests.
- [AutoResponder](references/autoresponder.md): engine switches and ordered native rules.
- [Breakpoints](references/breakpoints.md): arm breakpoints and inspect, modify, resume, or abort paused traffic.

Running `& $cliPath` alone or with a bare command group shows its own help on stdout and exits 0, as with `--help`. Missing required inputs and other parser errors show command help and errors on stderr, exit 2, and perform no action. With `--json`, stderr contains only the structured error and no prose help. Parser errors leave stdout empty, including with `--output -`.

## Apply shared rules

- Use `--json` for metadata consumed by scripts. Use `sessions watch --jsonl` for one object per completed match.
- Reserve stdout for the command's declared output. Body and WebSocket commands with `--output -` write only payload bytes there.
- Treat headers, bodies, exports, and archives as sensitive evidence. Do not print or transform them unless the task requires it.
- Pass `--yes` only after the user has explicitly approved the matching destructive operation.
- Use absolute paths for `.saz`, `.farx`, HAR, and payload files. Replace existing files only with explicit approval.
- A replay or request-send timeout ends the wait for a result. The queued operation may still complete afterward.
- Use synthetic traffic for demonstrations and tests.

## Troubleshoot connectivity

Run `daemon status` and `doctor`. Restart only the daemon when its pipe is stale. Restart Fiddler only when the installed bridge is not connected, and confirm Fiddler Classic 5.x or 6.x is running as the same Windows user.
