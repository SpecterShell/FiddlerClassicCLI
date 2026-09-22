# Fiddler Classic CLI and MCP server

**English (en-US)** | [简体中文 (zh-CN)](README.zh-CN.md)

Fiddler Classic CLI is an unofficial, Windows-only CLI and MCP integration for Fiddler Classic 5.x and 6.x. It provides scriptable access through a small in-process extension, a current-user named pipe, and a shared .NET host. Fiddler Classic remains the capture engine.

This project is not affiliated with or supported by Progress Telerik. It uses Fiddler Classic's documented [.NET extension interfaces](https://www.telerik.com/fiddler/fiddler-classic/documentation/extend-fiddler/interfaces) and follows the deployment convention used by published [Fiddler add-ons](https://www.telerik.com/fiddler/add-ons).

Each public capability is classified as Native, Native adapter, Custom, Hybrid, or Host. See [Feature types and native compatibility](docs/en-US/feature-types.md) for the full classification matrix and requirements for parity with Fiddler's native behavior.

## Requirements

- Windows x64
- Fiddler Classic 5.x or 6.x
- Fiddler running for capture and session operations
- .NET SDK 10 only when building from source; published builds are self-contained

The bridge build references the locally installed `Fiddler.exe`. That executable is never committed to the repository or included in release packages.

## Install

### Release package

Each release contains `fiddler-classic-win-x64.zip` and `SHA256SUMS`. Verify the ZIP against the manifest, extract it, and run the bundled current-user installer from the extracted directory:

```powershell
./install.ps1
fiddler-classic --version
```

The installer copies the self-contained distribution to `%LOCALAPPDATA%\Programs\FiddlerClassicCLI\<version>` and updates the current-user `PATH`. For Agent workflows, it can also install a local release ZIP or a checksum-verified GitHub release. See the [installation guide](docs/en-US/installation.md) for these installation methods and private-repository authentication.

Install the Fiddler extension, restart Fiddler Classic, and verify the connection:

```powershell
fiddler-classic bridge install
fiddler-classic doctor
fiddler-classic status
```

### Build from source

Run the build script to build, test, and publish from source:

```powershell
./scripts/build.ps1
```

The script writes the self-contained distribution to `artifacts/publish/win-x64`. Install it for the current user with:

```powershell
./artifacts/publish/win-x64/install.ps1
```

The `bridge install` command copies only these files to `%USERPROFILE%\Documents\Fiddler2\Scripts`:

- `FiddlerClassic.Bridge.dll`
- `FiddlerClassic.Protocol.dll`

The command also records the installed host executable and version in `%LOCALAPPDATA%\FiddlerClassicCLI\bridge-host.json`. After restarting Fiddler, use the **Fiddler Classic CLI** tab or its Tools menu shortcut to manage MCP HTTP access. Use `bridge uninstall` to remove the extension files and launch record. The uninstall command prompts for confirmation in an interactive terminal and requires `--yes` when stdin is redirected.

## CLI

```text
fiddler-classic doctor
fiddler-classic status [--json]
fiddler-classic app detect|open|close|restart
fiddler-classic capture start|stop
fiddler-classic sessions list [filters] [--summary]
fiddler-classic sessions watch [filters] [--after-id ID] [--timeout SECONDS] [--count N] [--jsonl]
fiddler-classic sessions show <session-id>
fiddler-classic sessions body <session-id> --direction request|response --output <path|->
fiddler-classic sessions clear [--yes]
fiddler-classic sessions remove --ids 1 2 [--yes]
fiddler-classic sessions save <absolute.saz> [--ids 1 2] [--overwrite] [--yes]
fiddler-classic sessions load <absolute.saz>
fiddler-classic sessions replay <session-id> [--unconditional] [--wait] [--timeout SECONDS]
fiddler-classic sessions export [session-id] --format curl|raw-http|har --output <path|->
fiddler-classic sessions diff <left-id> <right-id>
fiddler-classic sessions websocket <session-id> list|get
fiddler-classic request send <url> [-X METHOD] [-H "Name: value"] [--body text|--body-file path] [--wait]
fiddler-classic autoresponder status|configure
fiddler-classic autoresponder rules list|add|update|move|remove|clear|save|load
fiddler-classic breakpoints status|arms|arm|disarm|list|wait|show|update|resume|abort
fiddler-classic bridge install|uninstall
fiddler-classic daemon start|status|stop
fiddler-classic mcp stdio|http
fiddler-classic mcp service status|configure|enable|disable
fiddler-classic mcp clients list|authorize|deauthorize
fiddler-classic mcp connections list|disconnect
fiddler-classic config token show|rotate
```

Use `app detect` to find existing Fiddler installations and processes. `app open` launches Fiddler with `-noattach`, or leaves an existing instance unchanged. `app close` and `app restart` require confirmation (`--yes` for scripts); save needed captures first. They request a normal close and never force-kill Fiddler. See [application commands](docs/en-US/cli.md#fiddler-application) for custom paths, PID selection, and timeouts.

CLI commands that require the bridge automatically start a persistent background daemon and communicate with it through a Windows named pipe restricted to the current user. The installed extension also starts or discovers that daemon when Fiddler loads. Subsequent clients reuse the same process. To manage the daemon explicitly, use `daemon start`, `daemon status`, and `daemon stop`; see the [CLI guide](docs/en-US/cli.md).

`sessions list`, `sessions watch`, and HAR export share filters for IDs, method, host, URL, status, response MIME type, process, exact header name, header value, duration, HTTP protocol, combined body size, error state, and bounded request or response body content. Body search requires explicit opt-in and matches exact UTF-8 bytes. It checks the first 64 KiB by default, with a maximum of 1 MiB per session. Lists return 100 sessions by default and accept at most 1,000.

`sessions show` returns metadata and exact header name/value pairs in their original order. It does not return bodies. `sessions body` streams the complete raw payload in 256 KiB chunks. When the output is `-`, stdout contains only body bytes; status messages and errors go to stderr.

`sessions watch` emits completed sessions as they arrive. `sessions replay --wait` and `request send --wait` correlate the operation with the first matching completed session after its baseline ID. `sessions remove` deletes only explicitly specified IDs. Export and diff output preserve sensitive evidence: cURL and raw HTTP reproduce one request, HAR exports a filtered set, and diff compares metadata, exact headers, timing, and body hashes. WebSocket payloads stream in chunks, separately from frame metadata.

`autoresponder` controls Fiddler's live AutoResponder engine. Rules have runtime IDs, preserve Fiddler evaluation order, and accept native match and action strings. FARX import adds rules; `--replace --yes` replaces the list. Saving requires an absolute `.farx` path, and replacing a file requires `--overwrite --yes`.

`breakpoints arm request|response` configures a pause for future traffic, filtered by method, host, URL, header, process, and response-only status/content-type conditions. Arms are one-shot unless `--persistent` is supplied. Managed pauses automatically resume after 30 seconds by default; the hold time is configurable within 1-300 seconds. Pending breakpoints can be inspected, updated using fields allowed at the current stage, resumed, or explicitly aborted.

`sessions list --summary` aggregates one bounded page of metadata by host and status, including captured body-byte totals and timing statistics for completed requests. `doctor --output C:\Temp\fiddler-diagnostics.json` creates a new metadata-only diagnostic report without starting the daemon. See the [CLI guide](docs/en-US/cli.md) for scope, excluded fields, and output rules.

### Exit codes

| Code | Meaning |
| ---: | --- |
| 0 | Success |
| 1 | Unexpected failure |
| 2 | Invalid input or protocol mismatch |
| 3 | Fiddler is not installed |
| 4 | Fiddler, bridge, or CLI daemon is unavailable |
| 5 | Rejected, missing, conflicting, or unconfirmed operation |
| 6 | Bridge, CLI daemon, or Fiddler application exit timeout |

## MCP

The host uses the official C# SDK 2.2.0 and supports MCP revision `2026-07-28` over stdio and Streamable HTTP. It also accepts the `2025-11-25` and `2025-06-18` initialization handshake. MCP revisions use dates; "v2" is the SDK's major version. See [protocol compatibility](docs/en-US/cli.md#mcp-protocol) for request requirements and errors.

### Standard I/O

Example MCP client configuration:

```json
{
  "mcpServers": {
    "fiddler-classic": {
      "command": "C:\\path\\to\\fiddler-classic.exe",
      "args": ["mcp", "stdio"]
    }
  }
}
```

The stdio transport reserves stdout for JSON-RPC protocol messages. Host diagnostics go to stderr.

### Streamable HTTP

```powershell
./fiddler-classic.exe mcp http
```

The foreground server is stateless and binds only to loopback. Both HTTP modes reject requests containing an `Origin` header with HTTP 403; browser access and CORS are disabled. The default endpoint is `http://127.0.0.1:8877/mcp`. Requests require the following authorization header:

```http
Authorization: Bearer <token>
```

Read the default token with `config token show` and replace it with `config token rotate`. Foreground and managed HTTP listeners accept default and named credentials and reload them on subsequent requests after rotation or revocation. The daemon can run a persistent managed listener:

```powershell
fiddler-classic mcp service configure --bind loopback --port 8877
fiddler-classic mcp service enable
fiddler-classic mcp clients authorize --name "Local agent"
fiddler-classic mcp connections list
```

Named client tokens are displayed only once; configuration stores only their SHA-256 hashes. Binding the managed service to `0.0.0.0` requires explicit confirmation because bearer credentials travel over plain HTTP and anyone who observes them can reuse them. The project does not configure TLS, firewall rules, or CORS. It stores configuration at `%LOCALAPPDATA%\FiddlerClassicCLI\config.json` under an ACL restricted to the current user.

The management tab has a terminal icon. Its buttons wrap as the pane narrows, and it preserves edits and selections during refresh. The controls support keyboard access, with separate copy buttons for loopback and available LAN address hints. See [managed HTTP controls](docs/en-US/cli.md#managed-mcp-http) for instructions on applying settings and handling timeouts, and for address limitations.

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

The repository and published distribution include the English Agent Skill package at `skills/fiddler-classic-cli`. It follows the `SKILL.md` convention and includes discovery metadata in `agents/openai.yaml`. When distributed with the project, the skill locates the shared `install.ps1` in the root directory. It directs CLI tasks to short reference documents for diagnostics and application lifecycle, traffic inspection, session actions, AutoResponder, and breakpoints.

## Security

Captured traffic is evidence. The bridge does not redact, normalize, or silently transform headers or bodies. Explicit output can contain passwords, cookies, bearer tokens, API keys, and personal data. Protect terminal output, MCP transcripts, body files, and SAZ archives as sensitive evidence.

- The bridge and CLI daemon named pipes permit only the current Windows user.
- MCP HTTP is disabled and loopback-only by default. Binding to IPv4 `0.0.0.0` requires an explicit plaintext credential warning.
- Every HTTP request requires a 256-bit bearer credential. Named client tokens are stored only as SHA-256 hashes. The default CLI token, retained for compatibility, remains readable under a current-user ACL.
- Input validation checks header names and values and rejects CRLF injection.
- Frames, request bodies, result counts, and MCP body chunks are bounded.
- Managed breakpoint arms default to one match, and their pauses automatically resume after a bounded hold timeout.
- The bridge preserves AutoResponder action strings exactly. These actions can affect network traffic or access paths available to the current user.
- `capture start|stop` only attaches or detaches Fiddler as the system proxy.
- The project never installs or trusts a root certificate.

See [SECURITY.md](SECURITY.md) for operational guidance.

## Development

```powershell
dotnet build ./FiddlerClassicCLI.slnx
dotnet test ./tests/FiddlerClassic.Tests/FiddlerClassic.Tests.csproj
dotnet publish ./src/FiddlerClassic.Host/FiddlerClassic.Host.csproj -c Release -p:PublishProfile=win-x64
./scripts/package.ps1
```

If Fiddler is installed elsewhere, pass `-p:FiddlerInstallDir="C:\path\to\Fiddler"`.

The [Build and Release workflow](.github/workflows/build-release.yml) runs on pull requests, pushes to `main`, version tags, and manual dispatches. It installs the pinned Fiddler Classic compile reference on a `windows-2025` runner, trying WinGet first and Chocolatey if WinGet is unavailable or unsuccessful. It then runs all automated tests, creates the self-contained package, verifies its checksum and required contents, and uploads the two release assets. A tag matching `v*` publishes the tested assets as a GitHub release; tags containing a prerelease suffix such as `v0.3.0-preview.1` create a prerelease.

Release checks cover archive rejection rules, temporary installation, reinstallation and upgrades, and direct API metadata compatibility of the same distributed bridge DLL with Fiddler `5.0.20253.3311` and `6.0.20261.7291`. The manual `run_fiddler_compatibility` option also runs native UI checks on disposable runners, including startup with the tab hidden and cleanup on unload. See the [installation guide](docs/en-US/installation.md#github-actions) for details of the checks that require explicit opt-in. Release artifacts exclude Telerik binaries, native profiles, and test probes.

Automated tests use a fake named-pipe peer and exercise both MCP transports. Real Fiddler tests are opt-in:

| Environment variable | Coverage |
| --- | --- |
| `FIDDLER_CLASSIC_INTEGRATION=1` | Status, compose, capture inspection, large/binary body chunks, SAZ save, replay |
| `FIDDLER_CLASSIC_AUTOMATION_INTEGRATION=1` | AutoResponder backup/restore, FARX replacement, request/response breakpoint mutation, automatic resume |
| `FIDDLER_CLASSIC_DESTRUCTIVE_INTEGRATION=1` | Clear and SAZ restore |
| `FIDDLER_CLASSIC_PROXY_INTEGRATION=1` | System proxy attach/detach with original-state restoration |

Run opt-in tests only in a controlled Fiddler profile. The automation test temporarily replaces and restores the AutoResponder rule list and sends loopback traffic through live breakpoints. The destructive test backs up and restores the session list and modifies active capture evidence during the test.

## License

Copyright 2026 SpecterShell. Licensed under the [Apache License 2.0](LICENSE).
