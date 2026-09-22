---
name: fiddler-classic-cli
description: Operate and troubleshoot the unofficial Fiddler Classic CLI on Windows. Use when an agent needs to inspect or watch HTTP(S) requests and WebSocket frames, read exact headers or bodies, export or compare evidence, replay or compose requests, manage capture, archives, AutoResponder rules, breakpoints, the Fiddler application, the CLI daemon, or the bridge, or diagnose connectivity.
---

# Fiddler Classic CLI

Use `fiddler-classic` to inspect and control a running Fiddler Classic 5.x or 6.x instance. The integration supports Windows only. It does not launch Fiddler, trust certificates, or enable HTTPS decryption automatically.

## Resolve the CLI

Find the executable before running commands:

```powershell
$command = Get-Command fiddler-classic -ErrorAction SilentlyContinue
$cliPath = if ($command) { $command.Source } else { $null }
```

If it is missing, obtain explicit approval before downloading files, writing under the current user's local application data, or changing the current-user `PATH`. Set `$skillRoot` to the absolute directory containing this loaded `SKILL.md`. In a repository checkout or extracted release, the installer is two levels above the skill directory:

```powershell
$distributionRoot = Split-Path -Parent (Split-Path -Parent $skillRoot)
$installerPath = Join-Path $distributionRoot "install.ps1"
$cliPath = (& $installerPath | Select-Object -Last 1)
& $cliPath --version
```

If `$installerPath` is absent, ask the user for an extracted release or trusted source checkout. The installer accepts `-PackagePath` for a local release ZIP or extracted payload and `-Repository owner/name` for a checksum-verified release. Never guess the repository or bypass checksum verification. Provide `GITHUB_TOKEN` through the process environment for a private repository and do not print it.

Use `$cliPath` for subsequent commands; a long-running agent process may still have the previous `PATH` value.

## Verify the connection

Run both checks before using bridge-backed commands:

```powershell
& $cliPath daemon status
& $cliPath doctor
```

Commands that access the bridge start the daemon as needed. If `doctor` reports that the bridge is missing, obtain approval, run `& $cliPath bridge install`, restart Fiddler Classic, and rerun `doctor`. Do not reinstall or restart healthy components.

Use `app detect` for read-only installation and process discovery without the bridge. Run `app open` only when the user asks to open Fiddler; new launches use `-noattach`. Obtain approval before `app close` or `app restart` and ask the user to save needed captures. These commands request a normal close and never force-kill Fiddler or dismiss its dialogs.

## Choose a command reference

Read only the reference needed for the task. Use `& $cliPath <command> --help` for the complete option list:

- [Diagnostics and runtime](references/diagnostics-and-runtime.md): health, status, Fiddler application lifecycle, bridge installation, and daemon control.
- [Capture and inspection](references/capture-and-inspection.md): proxy capture, session inspection, body extraction, comparison, and WebSocket payloads.
- [Session actions](references/session-actions.md): deletion, archives, replay, export, and composed requests.
- [AutoResponder](references/autoresponder.md): engine switches and ordered native rules.
- [Breakpoints](references/breakpoints.md): arm breakpoints and inspect, modify, resume, or abort paused traffic.

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
