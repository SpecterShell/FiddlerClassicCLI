# Fiddler Classic CLI and MCP Server

**English (en-US)** | [简体中文 (zh-CN)](README.zh-CN.md)

An unofficial, Windows-only CLI and MCP integration for Fiddler Classic 5.x. It keeps Fiddler Classic as the capture engine and adds scriptable access through a small in-process extension, a current-user named pipe, and one shared .NET host.

This project is not affiliated with or supported by Progress Telerik. It follows Fiddler Classic's documented [.NET extension interfaces](https://www.telerik.com/fiddler/fiddler-classic/documentation/extend-fiddler/interfaces) and the deployment convention used by published [Fiddler add-ons](https://www.telerik.com/fiddler/add-ons).

Each public capability is classified as Native, Native adapter, Custom, Hybrid, or Host. See [Feature Types and Native Compatibility](docs/en-US/feature-types.md) for the complete matrix and parity contract.

## Requirements

- Windows x64
- Fiddler Classic 5.x
- Fiddler running for capture and session operations
- .NET SDK 10 only when building from source; published builds are self-contained

The bridge build references the locally installed `Fiddler.exe`. The executable is never copied into this project or its published output.

## Install

### Release Package

Each release contains `fiddler-classic-win-x64.zip` and `SHA256SUMS`. Verify the ZIP against the manifest, extract it, and run the bundled current-user installer from the extracted directory:

```powershell
./install.ps1
fiddler-classic --version
```

The installer copies the self-contained payload to `%LOCALAPPDATA%\Programs\FiddlerClassicCLI\<version>` and updates the current-user `PATH`. It can also install a local release ZIP or a checksum-verified GitHub release for Agent workflows. See the [installation guide](docs/en-US/installation.md) for those forms and private-repository authentication.

Install the Fiddler extension, restart Fiddler Classic, and verify the connection:

```powershell
fiddler-classic bridge install
fiddler-classic doctor
fiddler-classic status
```

### Build From Source

Build, test, and publish from source:

```powershell
./scripts/build.ps1
```

The self-contained distribution is written to `artifacts/publish/win-x64`. Install it for the current user with:

```powershell
./artifacts/publish/win-x64/install.ps1
```

The `bridge install` command copies only these files to `%USERPROFILE%\Documents\Fiddler2\Scripts`:

- `FiddlerClassic.Bridge.dll`
- `FiddlerClassic.Protocol.dll`

Use `bridge uninstall` to remove them. Uninstall prompts interactively and requires `--yes` when stdin is redirected.

## CLI

```text
fiddler-classic doctor
fiddler-classic status [--json]
fiddler-classic capture start|stop
fiddler-classic sessions list [filters]
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
fiddler-classic config token show|rotate
```

Bridge-backed CLI commands automatically start a persistent background daemon and communicate with it through a current-user-only Windows named pipe. Subsequent CLI invocations reuse the same daemon. Manage it explicitly with `daemon start`, `daemon status`, and `daemon stop`; see the [CLI guide](docs/en-US/cli.md).

`sessions list`, `sessions watch`, and HAR export share filters for IDs, method, host, URL, status, response MIME type, process, exact header name, header value, duration, HTTP protocol, combined body size, error state, and bounded request or response body content. Body search is opt-in, searches exact UTF-8 bytes, defaults to the first 64 KiB, and is capped at 1 MiB per session. Lists return 100 sessions by default and accept at most 1,000.

`sessions show` returns metadata and exact ordered header name/value pairs, never bodies. `sessions body` streams the complete raw payload in 256 KiB chunks. Writing to `-` reserves stdout for body bytes and sends status/errors to stderr.

`sessions watch` emits completed sessions as they arrive. `sessions replay --wait` and `request send --wait` correlate the first matching completed session after the operation's baseline ID. `sessions remove` deletes only explicit IDs. Export and diff output preserve sensitive evidence: cURL and raw HTTP reproduce one request, HAR exports a filtered set, and diff compares metadata, exact headers, timing, and body hashes. WebSocket payloads remain separate from frame metadata and are streamed in chunks.

`autoresponder` controls Fiddler's live AutoResponder engine. Rules have runtime IDs, preserve Fiddler evaluation order, and accept native match and action strings. FARX import adds rules; `--replace --yes` replaces the list. Saving requires an absolute `.farx` path, and replacing a file requires `--overwrite --yes`.

`breakpoints arm request|response` creates a future traffic pause with method, host, URL, header, process, and response-only status/content-type filters. Arms are one-shot unless `--persistent` is supplied. Managed pauses automatically resume after 30 seconds by default, with a configurable 1-300 second hold. A pending breakpoint can be inspected, mutated with stage-appropriate fields, resumed, or explicitly aborted.

### Exit Codes

| Code | Meaning |
| ---: | --- |
| 0 | Success |
| 1 | Unexpected failure |
| 2 | Invalid input or protocol mismatch |
| 3 | Fiddler is not installed |
| 4 | Fiddler, bridge, or CLI daemon is unavailable |
| 5 | Rejected, missing, conflicting, or unconfirmed operation |
| 6 | Bridge or CLI daemon timeout |

## MCP

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

The stdio transport writes JSON-RPC protocol messages only to stdout. Host diagnostics go to stderr.

### Streamable HTTP

```powershell
./fiddler-classic.exe mcp http
```

The default endpoint is `http://127.0.0.1:8877/mcp`. It is stateless, loopback-only, has no CORS policy, and requires:

```http
Authorization: Bearer <token>
```

Read the generated token with `config token show` and replace it with `config token rotate`. Configuration is stored at `%LOCALAPPDATA%\FiddlerClassicCLI\config.json` under a current-user-only ACL.

### Inspection Tools

The MCP surface follows the small, composable list/detail pattern used by [Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp), adapted to Fiddler's process-wide session model. See [the design notes](docs/en-US/design.md) for the tool workflow and deliberate differences.

- `get_status`
- `start_capture`
- `stop_capture`
- `list_network_requests`
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

`list_network_requests` returns stable `nextMaxRequestId` or `nextMinRequestId` continuation bounds when more matches exist. `wait_for_network_request` waits up to 60 seconds after an exclusive request ID. Request details exclude headers unless `includeHeaders=true`. HTTP bodies and WebSocket payloads are available only through their bounded payload tools, with a maximum of 64 KiB per MCP call and explicit text/base64 metadata. Clearing, selective removal, and archive replacement require explicit confirmation arguments.

### AutoResponder Tools

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

Rule mutation tools are annotated as open-world because native Fiddler actions can redirect, synthesize, delay, drop, or source responses from local files. Removal, clearing, file replacement, and full-list replacement use destructive annotations and explicit confirmation arguments.

### Breakpoint Tools

- `list_network_breakpoint_arms`
- `arm_network_breakpoint`
- `disarm_network_breakpoint`
- `list_pending_network_breakpoints`
- `wait_for_network_breakpoint`
- `get_network_breakpoint`
- `update_network_breakpoint`
- `resume_network_breakpoint`
- `abort_network_breakpoint`

Breakpoint list/detail calls omit bodies. Read paused payloads through `get_network_request_body`, then pass a complete base64 body to `update_network_breakpoint` only when replacement is intended. Aborting requires `confirm=true`.

## Agent Skills

The repository and published distribution include the English Agent Skill package at `skills/fiddler-classic-cli`. It follows the `SKILL.md` convention and includes `agents/openai.yaml` discovery metadata. The skill bootstraps a missing CLI from an adjacent payload, local release, or trusted GitHub release, then teaches inspection, body pagination, daemon and bridge diagnostics, proxy-state safety, and sensitive-evidence handling.

## Security

Captured traffic is evidence. The bridge does not redact, normalize, or silently transform headers or bodies. Explicit output can contain passwords, cookies, bearer tokens, API keys, and personal data. Treat terminal output, MCP transcripts, body files, and SAZ archives accordingly.

- The bridge and CLI daemon named pipes permit only the current Windows user.
- HTTP binds only to `127.0.0.1` and uses a 256-bit bearer token stored with a current-user ACL.
- Header names and values are validated and CRLF injection is rejected.
- Frames, request bodies, result counts, and MCP body chunks are bounded.
- Managed breakpoint arms default to one match, and their pauses automatically resume after a bounded hold timeout.
- AutoResponder action strings are preserved exactly and can affect network traffic or access paths available to the current user.
- `capture start|stop` only attaches or detaches Fiddler as the system proxy.
- The project never installs or trusts a root certificate.

See [SECURITY.md](SECURITY.md) for operational guidance.

## License

Copyright 2026 SpecterShell. Licensed under the [Apache License 2.0](LICENSE).

## Development

```powershell
dotnet build ./FiddlerClassicCLI.slnx
dotnet test ./tests/FiddlerClassic.Tests/FiddlerClassic.Tests.csproj
dotnet publish ./src/FiddlerClassic.Host/FiddlerClassic.Host.csproj -c Release -p:PublishProfile=win-x64
./scripts/package.ps1
```

If Fiddler is installed elsewhere, pass `-p:FiddlerInstallDir="C:\path\to\Fiddler"`.

The [Build and Release workflow](.github/workflows/build-release.yml) runs on pull requests, pushes to `main`, version tags, and manual dispatches. It installs the pinned Fiddler Classic compile reference on a `windows-2025` runner, trying WinGet first and Chocolatey if WinGet is unavailable or unsuccessful. It then runs all automated tests, creates the self-contained package, verifies its checksum and required contents, and uploads the two release assets. A tag matching `v*` publishes the tested assets as a GitHub release; tags containing a prerelease suffix such as `v0.3.0-preview.1` create a prerelease.

The workflow uses Fiddler only to compile the extension and never includes `Fiddler.exe` in its artifacts.

Automated tests use a fake named-pipe peer and exercise both MCP transports. Real Fiddler tests are opt-in:

| Environment variable | Coverage |
| --- | --- |
| `FIDDLER_CLASSIC_INTEGRATION=1` | Status, compose, capture inspection, large/binary body chunks, SAZ save, replay |
| `FIDDLER_CLASSIC_AUTOMATION_INTEGRATION=1` | AutoResponder backup/restore, FARX replacement, request/response breakpoint mutation, automatic resume |
| `FIDDLER_CLASSIC_DESTRUCTIVE_INTEGRATION=1` | Clear and SAZ restore |
| `FIDDLER_CLASSIC_PROXY_INTEGRATION=1` | System proxy attach/detach with original-state restoration |

Run gated tests only in a controlled Fiddler profile. The automation test temporarily replaces and restores the AutoResponder rule list and sends loopback traffic through live breakpoints. The destructive test backs up and restores the session list, but it still manipulates active evidence.
