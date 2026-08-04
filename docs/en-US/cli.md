# Fiddler Classic CLI

**English (en-US)** | [简体中文 (zh-CN)](../zh-CN/cli.md)

The `fiddler-classic` executable provides human-readable commands for inspecting and controlling a running Fiddler Classic 5.x instance. Add `--json` to metadata commands for machine-readable output.

## Background Daemon

Bridge-backed commands act as clients to a persistent background daemon. On the first operation, the CLI starts a hidden copy of itself with the internal daemon entry point, waits for its named pipe, and then sends the operation. Later invocations reuse the same process.

Fiddler Classic is Windows-only, so the daemon uses a Windows named pipe rather than a Unix-domain socket. The pipe is restricted to the current Windows user. The daemon is not a service and is not registered to start with Windows.

Manage it explicitly:

```powershell
fiddler-classic daemon start
fiddler-classic daemon status
fiddler-classic daemon stop
```

Starting the daemon does not launch Fiddler, attach the system proxy, or alter certificate trust. `daemon status --json` returns its process ID, start time, pipe name, and host version.

## Command Usage

Feature behavior is classified in [Feature Types and Native Compatibility](feature-types.md). In particular, list filters, wait cursors, diffing, and payload chunking are Custom operations over native Fiddler evidence; they do not modify Fiddler's Filters, Compare, or Inspector UI state.

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

Required identifiers and paths are positional arguments. Optional values use flags. Repeated headers use repeated `-H` or `--header` values.

## Filters and Live Watching

Session list, watch, and HAR export share filters for IDs, method, host, URL, status, response MIME type, process, exact header name, header value, duration, HTTP protocol, combined body size, and error state.

`--body-contains` performs an exact UTF-8 byte search in the request or response prefix selected by `--body-direction`. `--body-search-bytes` defaults to 65,536 and is capped at 1,048,576 bytes per session. No body bytes are searched unless `--body-contains` is present.

`sessions watch` starts after the current newest ID unless `--after-id` is supplied. It emits completed matches until `--count` is reached, the 1-60 second inactivity timeout expires, or the process is cancelled. `--jsonl` emits one session object per line.

## Mutation and Correlation

`sessions remove --ids ...` removes only the selected sessions and fails if any ID is missing. It has the same interactive confirmation behavior as `sessions clear`.

Replay and composed requests normally return acceptance immediately. Add `--wait` to wait for the first completed request after the operation's baseline ID that matches its method and URL. A timeout does not revoke an already accepted request.

## Export, Diff, and WebSocket

`sessions export <id> --format curl|raw-http` reproduces one sensitive request. Raw HTTP preserves binary body bytes; cURL rejects binary bodies that cannot be represented safely as one shell command. `sessions export --format har` exports the filtered session set to an absolute `.har` path. Existing files require `--overwrite` and confirmation.

`sessions diff` reports metadata, ordered request/response header, duration, and SHA-256 request/response body differences without printing bodies. `sessions websocket ... list` returns frame direction, opcode, timestamp, length, continuation, and final-frame state. The `get` command streams a complete frame payload from an optional byte offset.

## AutoResponder

Inspect and configure the live engine:

```powershell
fiddler-classic autoresponder status
fiddler-classic autoresponder configure --enable --permit-fallthrough --accept-connects --use-latency
```

Rules use native Fiddler match and action strings. The bridge assigns each loaded rule a runtime ID and preserves zero-based evaluation order:

```powershell
fiddler-classic autoresponder rules list
fiddler-classic autoresponder rules add "EXACT:https://example.test/api" "*drop"
fiddler-classic autoresponder rules update <rule-id> --action "*reset" --comment "temporary"
fiddler-classic autoresponder rules move <rule-id> 0
fiddler-classic autoresponder rules remove <rule-id> --yes
```

`--disable-on-match` disables a rule after its first match. Per-rule `--latency-ms` is used only when the engine's latency switch is enabled. Rule IDs remain stable while those rule objects remain loaded; list rules again after loading a FARX file.

FARX paths must be absolute. Import is additive, while replacement is destructive:

```powershell
fiddler-classic autoresponder rules save C:\captures\rules.farx
fiddler-classic autoresponder rules save C:\captures\rules.farx --overwrite --yes
fiddler-classic autoresponder rules load C:\captures\rules.farx
fiddler-classic autoresponder rules load C:\captures\rules.farx --replace --yes
```

Native actions are passed to Fiddler exactly. They can redirect, synthesize, delay, drop, reset, or source a response from a local path accessible to the current user.

## Breakpoints

Arms match future traffic at either the request or response stage. They are one-shot by default and automatically resume a managed pause after 30 seconds. Use `--persistent` to retain an arm and `--hold 1..300` to change the timeout in seconds.

```powershell
fiddler-classic breakpoints arm request --method POST --host example.test --hold 45
fiddler-classic breakpoints arm response --status 500 --content-type json
fiddler-classic breakpoints arms
fiddler-classic breakpoints disarm <arm-id>
```

Method, host, URL, process, and stage-header filters are shared. Status and content type are valid only for response arms. `disarm` prevents future matches but does not resume an existing pause.

Use the monotonically increasing sequence as the wait cursor:

```powershell
fiddler-classic breakpoints list
fiddler-classic breakpoints wait --after-sequence 0 --timeout 30
fiddler-classic breakpoints show <breakpoint-id>
```

`show` returns metadata and exact headers without body bytes. Existing `sessions body` commands can read a paused session by its session ID. Mutations are stage checked:

```powershell
fiddler-classic breakpoints update <breakpoint-id> --method PUT --url https://example.test/new --set-header "X-Test: yes" --body-file .\body.bin
fiddler-classic breakpoints update <breakpoint-id> --status 201 --reason Created --remove-header Content-Encoding --body "done"
fiddler-classic breakpoints resume <breakpoint-id>
fiddler-classic breakpoints abort <breakpoint-id> --yes
```

Request pauses accept method, URL, request headers, and request body. Response pauses accept status, reason, response headers, and response body. Bodies are complete replacements capped at 4 MiB. Manual breakpoints created in the Fiddler UI are listed too, but only managed arms receive an automatic hold timeout.

## Output

Metadata commands print concise human-readable output by default:

```powershell
fiddler-classic sessions list --host example.test --limit 20
```

Use `--json` for scripts:

```powershell
fiddler-classic sessions list --host example.test --limit 20 --json
```

`sessions body` and `sessions websocket ... get` write complete raw bytes to the required `--output` path. With `--output -`, stdout contains only payload bytes and diagnostics go to stderr.

## Troubleshooting

Inspect both layers when a command cannot connect:

```powershell
fiddler-classic daemon status
fiddler-classic doctor
```

Restart only the CLI daemon when its pipe is stale:

```powershell
fiddler-classic daemon stop
fiddler-classic daemon start
```

Restart Fiddler Classic only when `doctor` reports that the extension bridge is not connected. The daemon can run while Fiddler is closed; operational commands will return an actionable unavailable or timeout error.
