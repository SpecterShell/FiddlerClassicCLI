# Fiddler Classic CLI and MCP server

**English (en-US)** | [简体中文 (zh-CN)](README.zh-CN.md)

Fiddler Classic CLI is an unofficial, Windows-only CLI and MCP integration for Fiddler Classic 5.x and 6.x. It provides scriptable access through a small in-process extension, a current-user named pipe, and a shared .NET host. Fiddler Classic remains the capture engine.

This project is not affiliated with or supported by Progress Telerik. It uses Fiddler Classic's documented [.NET extension interfaces](https://www.telerik.com/fiddler/fiddler-classic/documentation/extend-fiddler/interfaces) and follows the deployment convention used by published [Fiddler add-ons](https://www.telerik.com/fiddler/add-ons).

Each public capability is classified as Native, Native adapter, Custom, Hybrid, or Host. See [Feature types and native compatibility](docs/en-US/feature-types.md) for the full classification matrix and requirements for parity with Fiddler's native behavior.

## Requirements

- Windows x64
- Fiddler Classic 5.x or 6.x
- Fiddler running for capture and session operations
- .NET SDK 10 only when building from source. Published builds are self-contained.

The bridge build references the locally installed `Fiddler.exe`. That executable is never committed to the repository or included in release packages.

## Install

### Latest release

Run this PowerShell command to download and run the repository's installer. Review the [script](https://github.com/SpecterShell/FiddlerClassicCLI/blob/main/scripts/install.ps1) first and use only a source you trust:

```powershell
irm https://raw.githubusercontent.com/SpecterShell/FiddlerClassicCLI/main/scripts/install.ps1 | iex
```

The bootstrap downloads `fiddler-classic-cli.exe` from the latest GitHub release of `SpecterShell/FiddlerClassicCLI`, verifies it against GitHub's SHA-256 asset digest, and installs to the fixed directory `%LOCALAPPDATA%\Programs\FiddlerClassicCLI`. A missing or mismatched digest stops installation. The installer updates the current-user `PATH` and installs the Fiddler bridge by default. Installation does not start Fiddler, the daemon, or MCP servers, and does not install Agent Skills.

Each release has one asset: `fiddler-classic-cli.exe`. It embeds the .NET runtime, managed dependencies, both bridge DLLs, and `LICENSE`. .NET extracts native runtime files to `%TEMP%\.net` by default. Use `scripts/install.ps1` to install the CLI. See the [installation guide](docs/en-US/installation.md) for local EXE/ZIP/directory sources, standalone use, version selection, custom directories, and bridge opt-out.

Start or restart Fiddler explicitly after installation to load the bridge. On the first startup, Fiddler may show a separate "Caution: Unverified Extension Detected" window for each of the two installed DLLs. Review each prompt and allow loading only if you trust the files. See [first-startup approval](docs/en-US/installation.md#fiddler-bridge) for the choices. Then verify the connection from a new terminal:

```powershell
fiddler-classic-cli doctor
fiddler-classic-cli status
```

### Build from source

Run the build script to build, test, and publish from source:

```powershell
./scripts/build.ps1
```

The script writes only `fiddler-classic-cli.exe` to `artifacts/publish/win-x64`. `scripts/package.ps1` builds and verifies it, then copies the executable to `artifacts/release` as the sole release file. Install a local build for the current user with:

```powershell
./scripts/install-local.ps1
```

This script uses the existing publish output from its checkout and delegates to the shared installer. It never downloads a release or rebuilds. It accepts `-InstallDirectory`, `-NoPathUpdate`, `-SkipBridge`, and `-Json`. See [local development installation](docs/en-US/installation.md#local-development) for an isolated install example.

The installer runs `bridge install` to deploy the embedded `FiddlerClassicCLI.Bridge.dll` and `FiddlerClassicCLI.Protocol.dll` to `%USERPROFILE%\Documents\Fiddler2\Scripts` and record the host executable and version in `%LOCALAPPDATA%\FiddlerClassicCLI\bridge-host.json`. Use `scripts/install.ps1 -SkipBridge` to install only the CLI. Run `fiddler-classic-cli bridge install` separately to install or repair the bridge.

After restarting Fiddler, use the **Fiddler Classic CLI** tab or its Tools menu shortcut to manage MCP HTTP access. `bridge uninstall` removes the extension files and launch record. It prompts for confirmation in an interactive terminal and requires `--yes` when stdin is redirected.

## CLI

Run `fiddler-classic-cli` alone or with a bare command group to see its help, as with `--help`. Missing required inputs show command usage without taking action. Use `--json` for structured errors. See [command usage](docs/en-US/cli.md#command-usage) for output streams and exit codes.

```text
fiddler-classic-cli doctor
fiddler-classic-cli status [--json]
fiddler-classic-cli app detect|open|close|restart
fiddler-classic-cli capture start|stop
fiddler-classic-cli sessions list [filters] [--summary]
fiddler-classic-cli sessions watch [filters] [--after-id ID] [--timeout SECONDS] [--count N] [--jsonl]
fiddler-classic-cli sessions show <session-id>
fiddler-classic-cli sessions body <session-id> --direction request|response --output <path|->
fiddler-classic-cli sessions clear [--yes]
fiddler-classic-cli sessions remove --ids 1 2 [--yes]
fiddler-classic-cli sessions save <absolute.saz> [--ids 1 2] [--overwrite] [--yes]
fiddler-classic-cli sessions load <absolute.saz>
fiddler-classic-cli sessions replay <session-id> [--unconditional] [--wait] [--timeout SECONDS]
fiddler-classic-cli sessions export [session-id] --format curl|raw-http|har --output <path|->
fiddler-classic-cli sessions diff <left-id> <right-id>
fiddler-classic-cli sessions websocket <session-id> list|get
fiddler-classic-cli request send <url> [-X METHOD] [-H "Name: value"] [--body text|--body-file path] [--wait]
fiddler-classic-cli autoresponder status|configure
fiddler-classic-cli autoresponder rules list|add|update|move|remove|clear|save|load
fiddler-classic-cli breakpoints status|arms|arm|disarm|list|wait|show|update|resume|abort
fiddler-classic-cli bridge install|uninstall
fiddler-classic-cli daemon start|status|stop
fiddler-classic-cli mcp stdio|http
fiddler-classic-cli mcp service status|configure|enable|disable
fiddler-classic-cli mcp clients list|authorize|deauthorize
fiddler-classic-cli mcp connections list|disconnect
fiddler-classic-cli config token show|rotate
```

Use `app detect` to find existing Fiddler installations and processes. `app open` launches Fiddler with `-noattach`, or leaves an existing instance unchanged. `app close` and `app restart` require confirmation (`--yes` for scripts). Save needed captures first. They request a normal close and never force-kill Fiddler. See [application commands](docs/en-US/cli.md#fiddler-application) for custom paths, PID selection, and timeouts.

CLI commands that require the bridge automatically start a persistent background daemon and communicate with it through a Windows named pipe restricted to the current user. The installed extension also starts or discovers that daemon when Fiddler loads. Subsequent clients reuse the same process. To manage the daemon explicitly, use `daemon start`, `daemon status`, and `daemon stop`. See the [CLI guide](docs/en-US/cli.md).

`capture start` attaches Fiddler as the host Windows system proxy, and `capture stop` detaches it. A running Fiddler proxy listener can still receive explicitly routed traffic in either state. Local clients can use its loopback address. Remote clients need Fiddler configured for remote access on a reachable interface or all interfaces, plus network routing and firewall access. IPv4 `0.0.0.0` is an all-interface bind address. Clients use a concrete loopback or host address and the proxy port. These Fiddler proxy settings are independent of MCP HTTP binding. Capture commands preserve certificate trust and HTTPS-decryption settings.

`sessions list`, `sessions watch`, and HAR export share filters for IDs, method, host, URL, status, response MIME type, process, exact header name, header value, duration, HTTP protocol, combined body size, error state, and bounded request or response body content. Body search requires explicit opt-in and matches exact UTF-8 bytes. It checks the first 64 KiB by default, with a maximum of 1 MiB per session. Lists return 100 sessions by default and accept at most 1,000.

`sessions show` returns metadata and exact header name/value pairs in their original order. It does not return bodies. `sessions body` streams the complete raw payload in 256 KiB chunks. When the output is `-`, stdout contains only body bytes. Status messages and errors go to stderr.

`sessions watch` emits completed sessions as they arrive. `sessions replay --wait` and `request send --wait` correlate the operation with the first matching completed session after its baseline ID. `sessions remove` deletes only explicitly specified IDs. Export and diff output preserve sensitive evidence: cURL and raw HTTP reproduce one request, HAR exports a filtered set, and diff compares metadata, exact headers, timing, and body hashes. WebSocket payloads stream in chunks, separately from frame metadata.

`autoresponder` controls Fiddler's live AutoResponder engine. Rules have runtime IDs, preserve Fiddler evaluation order, and accept native match and action strings. FARX import adds rules. `--replace --yes` replaces the list. Saving requires an absolute `.farx` path, and replacing a file requires `--overwrite --yes`.

`breakpoints arm request|response` configures a pause for future traffic, filtered by method, host, URL, header, process, and response-only status/content-type conditions. Arms are one-shot unless `--persistent` is supplied. Managed pauses automatically resume after 30 seconds by default. The hold time is configurable within 1-300 seconds. Pending breakpoints can be inspected, updated using fields allowed at the current stage, resumed, or explicitly aborted.

`sessions list --summary` aggregates one bounded page of metadata by host and status, including captured body-byte totals and timing statistics for completed requests. `doctor --output C:\Temp\fiddler-diagnostics.json` creates a new metadata-only diagnostic report without starting the daemon. See the [CLI guide](docs/en-US/cli.md) for scope, excluded fields, and output rules.

### Exit codes

| Code | Meaning |
| ---: | --- |
| 0 | Success or help |
| 1 | Unexpected failure |
| 2 | Invalid input or protocol mismatch |
| 3 | Fiddler is not installed |
| 4 | Fiddler, bridge, or CLI daemon is unavailable |
| 5 | Rejected, missing, conflicting, or unconfirmed operation |
| 6 | Bridge, CLI daemon, or Fiddler application exit timeout |

## MCP

The host uses the official C# SDK 2.2.0 and supports MCP revision `2026-07-28` over stdio and Streamable HTTP. It also accepts the `2025-11-25` and `2025-06-18` initialization handshake. MCP revisions use dates. "v2" is the SDK's major version. See [protocol compatibility](docs/en-US/cli.md#mcp-protocol) for request requirements and errors.

### Standard I/O

Example MCP client configuration:

```json
{
  "mcpServers": {
    "fiddler-classic-cli": {
      "command": "C:\\path\\to\\fiddler-classic-cli.exe",
      "args": ["mcp", "stdio"]
    }
  }
}
```

The stdio transport reserves stdout for JSON-RPC protocol messages. Host diagnostics go to stderr.

### Streamable HTTP

```powershell
./fiddler-classic-cli.exe mcp http
```

The foreground server is stateless, binds only to loopback, and always requires authentication. Both foreground and managed HTTP servers reject requests containing an `Origin` header in every authentication mode. Anonymous managed requests also undergo `Host` validation against DNS rebinding. Browser access and CORS are disabled. The default endpoint is `http://127.0.0.1:8877/mcp`. Foreground requests require the following authorization header:

```http
Authorization: Bearer <token>
```

Read the default token with `config token show` and replace it with `config token rotate`. When authentication is required for a request, HTTP listeners accept default and named credentials and reload them on subsequent requests after rotation or revocation. The daemon can run a persistent managed listener:

```powershell
fiddler-classic-cli mcp service configure --bind loopback --port 8877
fiddler-classic-cli mcp service enable
fiddler-classic-cli mcp clients authorize --name "Local agent"
fiddler-classic-cli mcp connections list
```

Named client tokens are displayed only once. Configuration stores only their SHA-256 hashes. Managed HTTP defaults to disabled, loopback-only, and `non-loopback` authentication. This mode skips bearer checks only when both actual socket IPs are loopback, after normalizing IPv4-mapped addresses. Requests to a LAN address require a bearer credential even from the same machine. Unknown socket addresses do not qualify for the exemption; `Host`, `Forwarded`, and `X-Forwarded-For` cannot grant it. It can also listen on all IPv4 interfaces or up to 16 selected active local IPv4 addresses. Remote enablement requires confirmation because bearer credentials travel over plain HTTP and anyone who observes them can reuse them. The project does not configure TLS, firewall rules, or CORS. It stores configuration at `%LOCALAPPDATA%\FiddlerClassicCLI\config.json` under an ACL restricted to the current user.

Use `mcp service configure --authentication required|non-loopback|none` while the service is disabled. `required` checks every request, `non-loopback` is the default, and `none` skips bearer checks for all clients. Saving or enabling `none` requires confirmation because every reachable client gains full MCP access, including captured traffic and mutation tools. Saving or enabling loopback-only `non-loopback` needs no access-risk confirmation. Credentials remain saved, but anonymous requests have no credential attribution and must pass Host validation against the receiving socket. Foreground `mcp http` always requires authentication. See [managed HTTP settings](docs/en-US/cli.md#managed-mcp-http) for commands and warnings.

Local processes, including processes under other Windows accounts, can reach anonymous loopback MCP in the default `non-loopback` mode. The named pipes are restricted to the current Windows user. Local relays and reverse proxies that connect through loopback appear as loopback peers; choose `required` if loopback callers or relays must authenticate.

The management tab has a terminal icon and native **MCP**, **Named pipes**, and **Settings** subtabs. MCP starts with an **Enable MCP HTTP** checkbox and editable Bind and Port controls. **Apply** saves pending changes and restarts an enabled listener after the required confirmations. The **MCP addresses** box below Apply/Refresh groups its URLs. Clients and connections have separate boxes. Settings contains the startup policy and authentication dropdown with descriptive choices, each with its own explanation, plus component versions and project and documentation links. Startup can enable the service, disable it, or restore its last state. The default is `last-state`, which leaves a new installation disabled.

The selected-interface checklist uses native text rendering to match surrounding controls. Action buttons share consistent heights and wrap as the pane narrows. Refresh updates changed cells while preserving edits, selections, and scrolling. Actions remain available during background reads and wait for an in-flight read before running. The controls support keyboard access, and button tooltips explain each action. Clipboard buttons sit beside each available loopback or LAN URL and briefly show **Copied!** after a successful copy. Their width follows the displayed caption. See [managed HTTP controls](docs/en-US/cli.md#managed-mcp-http) for instructions on applying settings and handling timeouts, and for address limitations.

The read-only [Named pipes subtab](docs/en-US/installation.md#named-pipes) shows bridge and daemon pipe paths with copy buttons immediately to their right, listener and status-check results, protocol versions, and daemon process details. URLs and pipe addresses support text selection and Ctrl+C. **Refresh pipes** reads status without starting the daemon or changing listeners.

### Inspection tools

The MCP interface uses small, composable list and detail calls, following the pattern used by [Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp) with adaptations for Fiddler's process-wide session model. See [the design notes](docs/en-US/design.md) for the tool workflow and the reasons for these differences.

- `get_status`
- `start_capture`
- `stop_capture`
- `list_network_requests`
- `summarize_network_requests`
- `wait_for_network_request`
- `get_network_request`
- `get_network_request_body`
- `clear_network_requests`
- `remove_network_requests`
- `save_network_archive`
- `load_network_archive`
- `replay_network_request`
- `send_request`
- `diff_network_requests`
- `list_websocket_messages`
- `get_websocket_message`

`list_network_requests` returns stable `nextMaxRequestId` or `nextMinRequestId` continuation bounds when more matches exist. `wait_for_network_request` waits up to 60 seconds for a request after the specified request ID, excluding that ID. Request details exclude headers unless `includeHeaders=true`. HTTP bodies and WebSocket payloads are available only through their bounded payload tools. Each MCP payload call returns at most 64 KiB with explicit text/base64 metadata. Clearing, selective removal, and archive replacement require explicit confirmation arguments.

### AutoResponder tools

- `get_autoresponder_status`
- `configure_autoresponder`
- `list_autoresponder_rules`
- `add_autoresponder_rule`
- `update_autoresponder_rule`
- `move_autoresponder_rule`
- `remove_autoresponder_rule`
- `clear_autoresponder_rules`
- `save_autoresponder_rules`
- `load_autoresponder_rules`

Rule mutation tools carry open-world annotations because native Fiddler actions can redirect, synthesize, delay, or drop traffic, or read responses from local files. Removal, clearing, file replacement, and full-list replacement carry destructive annotations and require explicit confirmation arguments.

### Breakpoint tools

- `list_network_breakpoint_arms`
- `arm_network_breakpoint`
- `disarm_network_breakpoint`
- `list_pending_network_breakpoints`
- `wait_for_network_breakpoint`
- `get_network_breakpoint`
- `update_network_breakpoint`
- `resume_network_breakpoint`
- `abort_network_breakpoint`

Breakpoint list and detail calls omit bodies. Read paused payloads through `get_network_request_body`. Pass a complete base64 body to `update_network_breakpoint` only when replacing the body. Aborting requires `confirm=true`.

## Agent Skills

The repository contains the English Agent Skill package at `skills/fiddler-classic-cli`. It follows the `SKILL.md` convention and includes discovery metadata in `agents/openai.yaml`. When the CLI is missing, the skill guides an approved download of the latest executable through `scripts/install.ps1`. Obtain the skill from the source repository and configure it separately. CLI installation does not copy skills into an agent's global configuration. The skill directs CLI tasks to short reference documents for installation, diagnostics and application lifecycle, traffic inspection, session actions, AutoResponder, and breakpoints.

## Security

Captured traffic is evidence. The bridge does not redact, normalize, or silently transform headers or bodies. Explicit output can contain passwords, cookies, bearer tokens, API keys, and personal data. Protect terminal output, MCP transcripts, body files, and SAZ archives as sensitive evidence.

- The bridge and CLI daemon named pipes permit only the current Windows user.
- Managed MCP HTTP is disabled and loopback-only by default, with `non-loopback` authentication. Remote enablement and saving or enabling `none` require explicit risk confirmation. Selected bindings retain exact IPv4 addresses and fail if an address is unavailable.
- Authenticated HTTP requests require a 256-bit bearer credential. Named client tokens are stored only as SHA-256 hashes. The default CLI token remains readable under a current-user ACL. The `none` mode grants full MCP access to every reachable client. The `non-loopback` exemption grants the same access only when both socket IPs are loopback. Origin rejection and Host validation still apply.
- Input validation checks header names and values and rejects CRLF injection.
- Frames, request bodies, result counts, and MCP body chunks are bounded.
- Managed breakpoint arms default to one match, and their pauses automatically resume after a bounded hold timeout.
- The bridge preserves AutoResponder action strings exactly. These actions can affect network traffic or access paths available to the current user.
- `capture start|stop` only attaches or detaches Fiddler as the host Windows system proxy. Explicitly routed traffic can still reach its running proxy listener after detachment.
- The project never installs or trusts a root certificate.

See [SECURITY.md](SECURITY.md) for operational guidance.

## Development

```powershell
dotnet build ./FiddlerClassicCLI.slnx
dotnet test ./tests/FiddlerClassicCLI.Tests/FiddlerClassicCLI.Tests.csproj
dotnet publish ./src/FiddlerClassicCLI.Host/FiddlerClassicCLI.Host.csproj -c Release -p:PublishProfile=win-x64
./scripts/package.ps1
```

If Fiddler is installed elsewhere, pass `-p:FiddlerInstallDir="C:\path\to\Fiddler"`.

## License

Copyright 2026 SpecterShell. Licensed under the [Apache License 2.0](LICENSE).
