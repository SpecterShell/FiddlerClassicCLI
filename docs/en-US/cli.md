# Fiddler Classic CLI

**English (en-US)** | [简体中文 (zh-CN)](../zh-CN/cli.md)

The `fiddler-classic` executable provides commands with human-readable output to inspect and control a running Fiddler Classic 5.x or 6.x instance. Add `--json` to metadata commands for machine-readable output.

## Background daemon

Bridge-backed commands send requests to a persistent background daemon. On the first operation, the CLI starts a hidden copy of itself through the internal daemon entry point, waits for its named pipe, and sends the operation. Later invocations reuse the same process.

Fiddler Classic supports only Windows. The daemon uses a Windows named pipe restricted to the current Windows user; it does not use a Unix-domain socket. The daemon is not a service and is not registered to start with Windows. The installed Fiddler extension starts or discovers the daemon when Fiddler loads so the management tab can report live state.

Use these commands to manage the daemon explicitly:

```powershell
fiddler-classic daemon start
fiddler-classic daemon status
fiddler-classic daemon stop
```

Starting the daemon does not launch Fiddler, attach the system proxy, or alter certificate trust. `daemon status --json` returns its process ID, start time, pipe name, host version, capabilities, and managed HTTP state.

## Command usage

The [Feature Types and Native Compatibility](feature-types.md) document classifies each feature's behavior. List filters, wait cursors, diffing, and payload chunking are Custom operations on native Fiddler evidence. They do not modify Fiddler's Filters, Compare, or Inspector UI state.

```text
fiddler-classic doctor [--output <absolute-path>] [--json]
fiddler-classic status [--json]
fiddler-classic app detect [--path PATH] [--json]
fiddler-classic app open [--path PATH] [--json]
fiddler-classic app close [--pid PID] [--timeout SECONDS] [--yes] [--json]
fiddler-classic app restart [--pid PID | --path PATH] [--timeout SECONDS] [--yes] [--json]
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

Required identifiers and paths are positional arguments. Optional values use flags. To supply multiple headers, repeat `-H` or `--header`.

## Fiddler application

The `app` commands manage Fiddler Classic itself. They use Windows file, registry, and process APIs without contacting the bridge or starting the daemon. They are CLI-only; no corresponding MCP tools are exposed. An installed extension may start its daemon when Fiddler loads, as usual.

`app detect` lists installations in the standard per-user and Program Files locations, followed by registered Windows App Paths entries. It also lists running Fiddler processes owned by the current user in the current Windows session. Discovery reads executable versions without launching them and reports whether each is supported. Only Classic 5.x and 6.x are accepted for lifecycle operations. Use `--path` with an absolute path named `Fiddler.exe` to check a custom installation; it replaces the installation search without hiding running processes. No installer is downloaded or run.

```powershell
fiddler-classic app detect --json
fiddler-classic app open --path "C:\Tools\Fiddler\Fiddler.exe"
```

`app open` leaves an existing supported process unchanged. Otherwise, it launches the selected executable with `-noattach`, which suppresses system-proxy attachment at startup. Without `--path`, it uses the first supported installation found. The command returns when Windows accepts the launch; check `status` or `doctor` after Fiddler loads to verify bridge readiness. An existing process keeps its capture settings. [Telerik documents the `-noattach` option](https://www.telerik.com/fiddler/fiddler-classic/documentation/knowledge-base/configure-fiddler-and-upstream-proxy-to-work-on-same-machine).

Save needed captures before `app close` or `app restart`. Both require confirmation; non-interactive callers must pass `--yes` (or `-y`). Closing stops any active capture and may discard unsaved sessions. The CLI requests a normal window close and waits for exit. It does not save captures, force-kill Fiddler, or dismiss native prompts; `--yes` only confirms the CLI operation. `--timeout` defaults to 10 seconds and accepts 1 through 60. A modal dialog can prevent the close request or keep the process alive. Resolve it in Fiddler, then use `app detect` before retrying. A close already requested can still complete after timeout or cancellation. [Windows normal-close behavior](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.closemainwindow?view=net-10.0).

```powershell
fiddler-classic app close --pid 1234 --timeout 30 --yes
fiddler-classic app restart --yes
```

`app restart` validates the running executable before closing it and relaunches that same path only after exit. A timeout or cancellation prevents the replacement from launching. The new process uses `-noattach`; captures and the previous capture state are not restored. If Fiddler is stopped, restart opens the selected installation. `app close` succeeds without changes when no process is running, but an explicitly selected missing PID is an error.

When several Fiddler processes are running, select one with `--pid` for close or restart. Restart's `--pid` and `--path` are mutually exclusive. Opening or restarting a different installation while one is running is rejected; close the existing one explicitly first. Inaccessible process ownership or executable metadata causes an error, and the CLI does not elevate itself. Concurrent lifecycle commands are rejected while another holds the current-user operation lock. Closing Fiddler does not stop its daemon or the managed MCP HTTP listener.

With `--json`, detection returns `installations` and `processes` arrays. Both contain `executablePath`, `version`, and `supported`; process records also contain `processId`. Lifecycle results contain `action`, `changed`, `running`, `processId`, and `executablePath`. A successful launch reports `running: true` without guaranteeing continued process or bridge availability.

Detection returns exit code 3 only when both arrays are empty, otherwise 0. Lifecycle failures return 2 for invalid options, paths, or unsupported versions; 4 for missing installations, inaccessible processes, or launch failures; 5 for missing confirmation, conflicts, or an unknown PID; and 6 for exit timeout. Parser and runtime errors use the usual JSON error object on stderr when `--json` is supplied.

## Filters and live watching

Session list, watch, and HAR export use the same filters: IDs, method, host, URL, status, response MIME type, process, exact header name, header value, duration, HTTP protocol, combined body size, and error state.

`--body-contains` performs an exact UTF-8 byte search in the request or response body prefix selected by `--body-direction`. `--body-search-bytes` defaults to 65,536 and is capped at 1,048,576 bytes per session. The command searches body bytes only when `--body-contains` is present.

`sessions watch` starts after the current newest ID unless `--after-id` is supplied. It outputs completed matches until it reaches `--count`, the 1-60 second inactivity timeout expires, or the process is cancelled. `--jsonl` outputs one session object per line.

## Bounded session summaries

`sessions list --summary` and `summarize_network_requests` aggregate one metadata page using the existing filters and ordering. The limit defaults to 100 and accepts values from 1 through 1,000. It limits returned records without limiting the bridge's metadata filter scan. Summary mode rejects header-name, header-value, and body-content searches.

```powershell
fiddler-classic sessions list --host example.test --limit 200 --summary --json
```

The result reports `scope: "returned_sessions"`, the limit, matched and returned counts, ID bounds, and `truncated`. All counts and totals cover only returned records. `byHost` groups names without regard to case; `byStatus` uses a separate null group for missing status. Request and response byte totals count captured bodies, including those from incomplete sessions, and exclude headers and wire overhead.

Timing statistics use finite, nonnegative durations from completed sessions in the returned records. They include count, minimum, maximum, median, and nearest-rank p95 in milliseconds. Statistics are null when there are no samples; MCP serialization may omit null fields. Summaries do not read headers or bodies or retrieve later pages. Hostnames and activity counts may still be sensitive.

## Diagnostic files

```powershell
fiddler-classic doctor --output C:\Temp\fiddler-diagnostics.json --json
```

With `--output`, `doctor` creates a new JSON file containing numeric component versions, expected protocol versions, known capabilities, listener state, and predefined errors with guidance on resolving them. It excludes traffic, headers, bodies, credentials, client identities, LAN addresses, local paths, raw error text, and arbitrary version suffixes. It never starts a daemon or creates configuration. When the daemon is stopped, saved listener settings remain unknown.

Specify an absolute path to a regular file in an existing writable directory. The command rejects existing files, stdout (`-`), devices, and alternate data streams. It publishes the complete report atomically. Successful export returns exit code 0 even when the report records an unavailable component; `--json` prints a receipt with the path and format. Invalid paths return 2; failure to create a file returns 5. Without `--output`, the command uses its existing health checks and exit codes. Review diagnostic files before sharing them.

## Mutation and correlation

`sessions remove --ids ...` removes only the selected sessions and fails if any ID is missing. It has the same interactive confirmation behavior as `sessions clear`.

Replay and request composition normally return as soon as the request is accepted. Add `--wait` to wait for the first completed request whose ID follows the operation's baseline ID and whose method and URL match the operation. A timeout does not revoke an already accepted request.

## Export, diff, and WebSocket

`sessions export <id> --format curl|raw-http` reproduces one sensitive request. Raw HTTP preserves binary body bytes; cURL rejects binary bodies that cannot be represented safely as one shell command. `sessions export --format har` exports the filtered session set to an absolute `.har` path. Overwriting an existing file requires `--overwrite` and confirmation.

`sessions diff` reports differences in metadata, ordered request/response headers, duration, and SHA-256 request/response body hashes without printing bodies. `sessions websocket ... list` returns frame direction, opcode, timestamp, length, continuation, and final-frame state. The `get` command streams a complete frame payload from an optional byte offset.

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

`--disable-on-match` disables a rule after its first match. Per-rule `--latency-ms` applies only when the engine's latency switch is enabled. Rule IDs remain stable while their rule objects remain loaded; list rules again after loading a FARX file.

FARX paths must be absolute. Import adds rules; replacement is destructive:

```powershell
fiddler-classic autoresponder rules save C:\captures\rules.farx
fiddler-classic autoresponder rules save C:\captures\rules.farx --overwrite --yes
fiddler-classic autoresponder rules load C:\captures\rules.farx
fiddler-classic autoresponder rules load C:\captures\rules.farx --replace --yes
```

The bridge passes native actions to Fiddler unchanged. They can redirect, synthesize, delay, drop, reset, or load a response from a local path accessible to the current user.

## Breakpoints

Arms match future traffic at either the request or response stage. By default, an arm matches once and automatically resumes a managed pause after 30 seconds. Use `--persistent` to retain an arm and `--hold 1..300` to change the timeout in seconds.

```powershell
fiddler-classic breakpoints arm request --method POST --host example.test --hold 45
fiddler-classic breakpoints arm response --status 500 --content-type json
fiddler-classic breakpoints arms
fiddler-classic breakpoints disarm <arm-id>
```

Both stages use the same method, host, URL, process, and stage-header filters. Status and content type are valid only for response arms. `disarm` prevents future matches but does not resume an existing pause.

Use the monotonically increasing sequence as the wait cursor:

```powershell
fiddler-classic breakpoints list
fiddler-classic breakpoints wait --after-sequence 0 --timeout 30
fiddler-classic breakpoints show <breakpoint-id>
```

`show` returns metadata and exact headers without body bytes. The `sessions body` commands can read a paused session by its session ID. Each mutation is checked against the current stage:

```powershell
fiddler-classic breakpoints update <breakpoint-id> --method PUT --url https://example.test/new --set-header "X-Test: yes" --body-file .\body.bin
fiddler-classic breakpoints update <breakpoint-id> --status 201 --reason Created --remove-header Content-Encoding --body "done"
fiddler-classic breakpoints resume <breakpoint-id>
fiddler-classic breakpoints abort <breakpoint-id> --yes
```

Request pauses accept changes to the method, URL, request headers, and request body. Response pauses accept changes to the status, reason, response headers, and response body. A body update replaces the complete body and is capped at 4 MiB. The list also includes manual breakpoints created in the Fiddler UI, but only managed arms receive an automatic hold timeout.

## MCP protocol

`mcp stdio`, foreground `mcp http`, and the daemon-managed HTTP listener use the official C# SDK 2.2.0. Both transports support revision `2026-07-28`, with regression coverage for the `2025-11-25` and `2025-06-18` handshakes. The 36 tools, confirmation arguments, structured results, and 64 KiB payload limit apply across these revisions.

Revision `2026-07-28` permits calls without `initialize`. Use `server/discover` to inspect capabilities and supported modern revisions. Each request supplies `io.modelcontextprotocol/protocolVersion` and `io.modelcontextprotocol/clientCapabilities` in `params._meta`; clients should also supply `io.modelcontextprotocol/clientInfo`. A compatible SDK constructs this metadata.

For HTTP, send `MCP-Protocol-Version` and `Mcp-Method` on each request, plus `Mcp-Name` for `tools/call`. These headers must match the body. Include both `application/json` and `text/event-stream` in `Accept`. The endpoint is stateless: it issues no protocol session IDs and has no standalone GET event stream or DELETE session route. TCP connections remain visible in the management tab.

Ordinary modern results carry `resultType: "complete"`. Discovery and tool listings use `ttlMs: 0` and `cacheScope: "private"`; these hints do not permit caching captured traffic. Closing an HTTP response stream cancels pending work, including the bridge exchange. An operation already dispatched may have completed.

| HTTP response | Meaning |
| --- | --- |
| 400, JSON-RPC `-32020` | A required modern header is missing or differs from the body. |
| 400, JSON-RPC `-32022` | The requested revision is unsupported; `error.data` gives `requested` and `supported`. |
| 404, JSON-RPC `-32601` | The RPC method is unknown. |
| 401 | A bearer credential is missing or invalid. |
| 403 | The request contains an `Origin` header. Browser origins are not authorized, even with a valid bearer credential. |
| 405 | The HTTP method has no endpoint route, including GET and DELETE. |

These transport responses are separate from CLI exit codes. Older clients continue to use `initialize` and their negotiated revision's request format. For wire details, see the [MCP specification](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http) and [C# SDK versioning guide](https://csharp.sdk.modelcontextprotocol.io/v2/versioning.html).

## Managed MCP HTTP

The foreground `mcp http` server binds only to loopback and exits with a clear bind error if another listener owns its port. The daemon-managed listener is persistent and disabled by default. Inspecting saved state does not start a stopped daemon:

```powershell
fiddler-classic mcp service status
fiddler-classic mcp service configure --bind loopback --port 8877
fiddler-classic mcp service enable
fiddler-classic mcp service disable
```

Bind mode `loopback` uses `127.0.0.1`; `all` uses IPv4 `0.0.0.0`. Disable the service before changing the bind mode or port. Enabling `all` requires confirmation at an interactive warning or `--yes` because bearer credentials travel over plain HTTP and can be reused if observed. Disabling a stopped service still updates saved state. Disabling the service while connections are active requires confirmation.

Offline administration requires both an unanswered connection attempt and exclusive access to the daemon's ownership pipe. A timeout from a connected or busy daemon produces an error (`timeout`, exit code 6). In that case, administration does not read saved state as if it were live, write configuration offline, or launch another daemon. Retry the status query before taking further action. Malformed responses and disconnected peers also produce errors.

The default CLI token is available through `config token show|rotate`. Named clients receive independent 256-bit tokens:

```powershell
fiddler-classic mcp clients list
fiddler-classic mcp clients authorize --name "Local agent"
fiddler-classic mcp clients deauthorize <client-id> --yes
```

The command prints an authorization token once and stores only its SHA-256 hash. Names contain 1 to 64 characters and must be unique without regard to case. Up to 64 named clients are allowed. Deauthorizing the `default` client rotates the default CLI token. Deauthorization requires confirmation and aborts active connections for that credential; an operation already dispatched may have completed.

Foreground and managed listeners use the same configured credentials. Rotation and revocation take effect on subsequent requests without a listener restart. Connection inspection and forced disconnection cover only the daemon-managed listener; revocation does not abort a foreground operation that has already been dispatched.

Connection inspection returns only operational metadata:

```powershell
fiddler-classic mcp connections list
fiddler-classic mcp connections disconnect <connection-id> --yes
```

Records include the connection ID, remote endpoint, authorization and client state, connection and activity times, and active and total request counts. They never include URLs, headers, tokens, or captured traffic. Disconnecting requires confirmation and aborts the selected transport connection.

The **Fiddler Classic CLI** tab provides the same controls and displays clients, connections, errors, and component versions. Its Tools menu entry focuses the tab. These controls manage MCP HTTP and do not change Fiddler's capture proxy. The extension starts or discovers the daemon during load, even when the tab is never opened. Each refresh rereads the configured host and running daemon versions, including after a daemon restart. Unloading cancels extension work and leaves the daemon running.

While the service is disabled, refresh preserves pending bind and port edits. Click **Apply** before enabling the service. If another client enables it, the fields show the active settings and become read-only. Refresh also preserves grid selection and scroll position. If the selected client or connection disappears, the tab clears that selection and disables its action button.

The tab uses a custom terminal icon with a white `>_` prompt, drawn at Fiddler's tab-icon size without an external image file. Service labels and values align in columns, with separate Bind and Port rows. Buttons and long status or version text wrap as the pane narrows; scroll down when the sections no longer fit vertically. The extension leaves Fiddler's DPI compatibility settings unchanged.

Tab control requests and daemon startup each have a ten-second deadline. After a timeout, check service status before retrying because the operation may have completed. Configuration transactions wait up to five seconds for another CLI or daemon update; failure to acquire the lock in that time reports `timeout` (CLI exit code 6).

The tab always provides a loopback URL and a **Copy loopback** button. In `all` mode it also shows up to eight distinct IPv4 URLs from active non-loopback adapters, each with its own **Copy LAN** button. These address hints do not test reachability; the user remains responsible for routing and Windows Firewall. Service JSON contains `endpoint` as bind metadata and `loopbackEndpoint` and `lanEndpoints` for client URLs; use the latter fields when connecting clients. Disabled service settings can still have address hints.

Controls have accessible names, explicit tab order, and keyboard mnemonics. Use Tab/Shift+Tab to move between controls, Alt+B for bind mode, Alt+P for port, and the underlined button letters for actions. Layout tests cover narrow panes and enlarged fonts without changing Fiddler's DPI settings.

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

If a client cannot negotiate MCP, check that its configured executable and the running daemon use the intended installed build. Restart the relevant server process after an upgrade. For revision `2026-07-28`, inspect a 400 response's JSON-RPC error before retrying; missing headers are not evidence that the server requires an older protocol. HTTP 403 indicates an `Origin` header, which native clients should omit. Do not enable CORS to bypass this restriction.

Check daemon, service, and bridge status when a command cannot connect:

```powershell
fiddler-classic daemon status
fiddler-classic mcp service status
fiddler-classic doctor
```

Restart only the CLI daemon when its pipe is stale:

```powershell
fiddler-classic daemon stop
fiddler-classic daemon start
```

Restart an older daemon when the Fiddler tab reports that managed HTTP capability is missing. Restart Fiddler Classic only when `doctor` reports that the extension bridge is not connected. The daemon and managed listener can run while Fiddler is closed; bridge operations will return an unavailable or timeout error with guidance on resolving it.
