---
name: fiddler-classic-cli
description: Operate and troubleshoot the unofficial Fiddler Classic CLI and MCP server on Windows. Use when an agent needs to inspect or watch HTTP(S) requests and WebSocket frames, read exact headers or bounded payload chunks, export or compare evidence, replay or compose requests, manage capture, archives, the CLI daemon, or the bridge, or diagnose connectivity.
---

# Fiddler Classic CLI

## Overview

Use `fiddler-classic` to inspect and control a running Fiddler Classic 5.x instance. Prefer MCP tools when the server is configured; use the CLI for shell workflows, diagnostics, and complete body extraction to a file.

This integration is Windows-only. It does not launch Fiddler, trust certificates, or enable HTTPS decryption automatically.

## Ensure the CLI Is Available

Resolve the executable before starting a workflow:

```powershell
$command = Get-Command fiddler-classic -ErrorAction SilentlyContinue
$cliPath = if ($command) { $command.Source } else { $null }
```

If it is missing, obtain explicit user approval before downloading files, writing under the current user's local application data, or changing the current-user `PATH`. Set `$skillRoot` to the absolute directory containing this loaded `SKILL.md`, then run its bundled `scripts/install.ps1` and keep the returned executable path:

```powershell
$cliPath = (& "$skillRoot/scripts/install.ps1" | Select-Object -Last 1)
& $cliPath --version
```

The installer first looks for an adjacent self-contained publish directory. It can install a downloaded release ZIP only when a sibling `SHA256SUMS` file verifies it:

```powershell
$cliPath = (& "$skillRoot/scripts/install.ps1" -PackagePath $trustedPackagePath | Select-Object -Last 1)
```

It can also resolve a checksum-verified GitHub release when the trusted source repository is known. Pass the repository as `owner/name`; never guess it from the project name:

```powershell
$cliPath = (& "$skillRoot/scripts/install.ps1" -Repository $trustedRepository | Select-Object -Last 1)
```

For a private GitHub repository, provide `GITHUB_TOKEN` through the process environment and do not print it. If working in a source checkout without a published payload, run `./scripts/build.ps1` and then rerun the bundled installer. Do not bypass checksum verification for downloaded binaries.

Use the resolved `$cliPath` for the rest of the workflow because a long-running agent process may retain its earlier `PATH` value.

## Verify the Integration

Before an operational workflow, verify both layers:

```powershell
& $cliPath daemon status
& $cliPath doctor
```

The daemon starts automatically for bridge-backed commands. If `doctor` reports that the bridge is not installed, obtain approval to install the extension, run `& $cliPath bridge install`, then restart Fiddler Classic and rerun `doctor`. Do not reinstall or restart processes when diagnostics already report a healthy connection.

## Inspect Traffic

Use the narrowest operation that answers the question:

1. List summaries with `sessions list --json` or `list_network_requests`.
2. Read one transaction with `sessions show <id> --json` or `get_network_request`. Request headers only when needed by setting `includeHeaders=true` in MCP.
3. Read bodies only when needed. The CLI requires `--direction request|response --output <absolute-path|->`. MCP uses `get_network_request_body` with at most 65,536 bytes per call.
4. Use `sessions watch` or `wait_for_network_request` when waiting for new completed traffic. Prefer this over repeatedly listing the full capture.

Example CLI inspection:

```powershell
& $cliPath sessions list --host example.test --limit 20 --json
& $cliPath sessions show 42 --json
& $cliPath sessions body 42 --direction response --output "C:/Temp/response.bin"
```

Use metadata filters before body search. Header, timing, protocol, body-size, error-state, and MIME filters are inexpensive. `--body-contains` searches exact UTF-8 bytes only within the explicitly bounded request or response prefix.

Keep the same filters while paging. For MCP newest-first results, pass `nextMaxRequestId` back as `maxRequestId`; for oldest-first results, pass `nextMinRequestId` back as `minRequestId`. For body chunks, advance `offset` by `bytesReturned` until `eof=true`. Decode `base64` only when the result encoding says `base64`.

## Control and Generate Traffic

Run `capture start` only when the user intends to attach Fiddler as the system proxy. Run `capture stop` when asked to detach it. These commands do not change certificate trust.

Use `sessions replay <id>` or `replay_network_request` for a captured transaction. Use `request send <url>` or `send_request` for a composed request. Add `--wait` or `wait=true` when the resulting completed request is needed immediately; treat timeout as an observation timeout because the queued network operation may still run.

Use `sessions websocket <id> list` or `list_websocket_messages` before reading frame payloads. Continue MCP payload reads with `offset + bytesReturned` until EOF. Use `sessions diff` or `diff_network_requests` to compare bodies by hash without exposing their content.

## Preserve Evidence

Treat headers, bodies, MCP transcripts, and SAZ files as sensitive. The bridge deliberately preserves credentials, cookies, tokens, and payload bytes without redaction.

- Do not print body bytes or raw headers unless they are necessary for the task.
- Use synthetic traffic for demonstrations and tests.
- Require explicit user intent before clearing requests, replacing an archive, changing system proxy state, or uninstalling the bridge.
- Pass `--yes` only after that intent is established. For MCP, set the corresponding confirmation argument only then.
- Use absolute `.saz` paths. Do not overwrite an existing archive unless explicitly requested.
- Use `sessions remove --ids ...` or `remove_network_requests` when only selected evidence should be deleted; never substitute a full clear.
- cURL, raw HTTP, and HAR exports contain unredacted headers and payloads. Prefer an explicit file over terminal or transcript output.

## Troubleshoot

If an operation cannot connect:

1. Run `daemon status` and `doctor`.
2. Restart only the daemon when its pipe is stale: `daemon stop`, then `daemon start`.
3. Restart Fiddler only when the extension bridge is installed but not connected.
4. Confirm that Fiddler Classic 5.x is running as the same Windows user.

Use `--json` for scripts and agent parsing. Keep stdout untouched when running `mcp stdio`; diagnostics belong on stderr.
