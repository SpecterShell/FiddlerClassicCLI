# Fiddler Classic CLI

**English (en-US)** | [简体中文 (zh-CN)](../zh-CN/cli.md)

The `fiddler-classic-cli` executable provides commands with human-readable output to inspect and control a running Fiddler Classic 5.x or 6.x instance. Add `--json` to metadata commands for machine-readable output.

## Background daemon

Bridge-backed commands send requests to a persistent background daemon. On the first operation, the CLI starts a hidden copy of itself through the internal daemon entry point, waits for its named pipe, and sends the operation. Later invocations reuse the same process.

Fiddler Classic supports only Windows. The daemon uses a Windows named pipe restricted to the current Windows user. The daemon is not a service and is not registered to start with Windows. The installed Fiddler extension starts or discovers the daemon when Fiddler loads so the management tab can report live state.

Use these commands to manage the daemon explicitly:

```powershell
fiddler-classic-cli daemon start
fiddler-classic-cli daemon status
fiddler-classic-cli daemon stop
```

Starting the daemon does not launch Fiddler, attach the system proxy, or alter certificate trust. `daemon status --json` returns its process ID, start time, pipe name, host version, capabilities, and managed HTTP state.

## Command usage

Running `fiddler-classic-cli` alone shows top-level help. A bare command group, such as `fiddler-classic-cli sessions` or `fiddler-classic-cli autoresponder rules`, shows that group's help. These calls behave like `--help`: they write help to stdout, exit 0, and perform no action.

Missing required inputs and other parser errors write the relevant command's help and errors to stderr and exit 2 without executing an action. With `--json`, stderr contains only the structured error, with no prose help. Parser errors leave stdout empty, including when `--output -` is present.

Value-taking options require a value whenever supplied. Omitting an optional option keeps its documented default.

The [Feature Types and Native Compatibility](feature-types.md) document classifies each feature's behavior. List filters, wait cursors, diffing, and payload chunking are Custom operations on native Fiddler evidence. They do not modify Fiddler's Filters, Compare, or Inspector UI state.

```text
fiddler-classic-cli doctor [--output <absolute-path>] [--json]
fiddler-classic-cli status [--json]
fiddler-classic-cli app detect [--path PATH] [--json]
fiddler-classic-cli app open [--path PATH] [--json]
fiddler-classic-cli app close [--pid PID] [--timeout SECONDS] [--yes] [--json]
fiddler-classic-cli app restart [--pid PID | --path PATH] [--timeout SECONDS] [--yes] [--json]
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

Required identifiers and paths are positional arguments. Optional values use flags. To supply multiple headers, repeat `-H` or `--header`.

## Fiddler application

The `app` commands manage Fiddler Classic itself. They use Windows file, registry, and process APIs without contacting the bridge or starting the daemon. These commands are available only through the CLI. An installed extension may start its daemon when Fiddler loads, as usual.

`app detect` lists installations in the standard per-user and Program Files locations, followed by registered Windows App Paths entries. It also lists running Fiddler processes owned by the current user in the current Windows session. Discovery reads executable versions without launching them and reports whether each is supported. Only Classic 5.x and 6.x are accepted for lifecycle operations. Use `--path` with an absolute path named `Fiddler.exe` to check a custom installation. It replaces the installation search without hiding running processes. No installer is downloaded or run.

```powershell
fiddler-classic-cli app detect --json
fiddler-classic-cli app open --path "C:\Tools\Fiddler\Fiddler.exe"
```

`app open` leaves an existing supported process unchanged. Otherwise, it launches the selected executable with `-noattach`, which suppresses system-proxy attachment at startup. Without `--path`, it uses the first supported installation found. The command returns when Windows accepts the launch. Check `status` or `doctor` after Fiddler loads to verify bridge readiness. An existing process keeps its capture settings. [Telerik documents the `-noattach` option](https://www.telerik.com/fiddler/fiddler-classic/documentation/knowledge-base/configure-fiddler-and-upstream-proxy-to-work-on-same-machine).

Save needed captures before `app close` or `app restart`. Both require confirmation. Non-interactive callers must pass `--yes` (or `-y`). Closing stops any active capture and may discard unsaved sessions. The CLI requests a normal window close and waits for exit. It does not save captures, force-kill Fiddler, or dismiss native prompts. `--yes` only confirms the CLI operation. `--timeout` defaults to 10 seconds and accepts 1 through 60. A modal dialog can prevent the close request or keep the process alive. Resolve it in Fiddler, then use `app detect` before retrying. A close already requested can still complete after timeout or cancellation. [Windows normal-close behavior](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.closemainwindow?view=net-10.0).

```powershell
fiddler-classic-cli app close --pid 1234 --timeout 30 --yes
fiddler-classic-cli app restart --yes
```

`app restart` validates the running executable before closing it and relaunches that same path only after exit. A timeout or cancellation prevents the replacement from launching. The new process uses `-noattach`. Restart does not restore captures or the previous capture state. If Fiddler is stopped, restart opens the selected installation. `app close` succeeds without changes when no process is running. An explicitly selected missing PID is an error.

When several Fiddler processes are running, select one with `--pid` for close or restart. Restart's `--pid` and `--path` are mutually exclusive. Opening or restarting a different installation while one is running is rejected. Close the existing one explicitly first. Inaccessible process ownership or executable metadata causes an error, and the CLI does not elevate itself. Concurrent lifecycle commands are rejected while another holds the current-user operation lock. Closing Fiddler does not stop its daemon or the managed MCP HTTP listener.

With `--json`, detection returns `installations` and `processes` arrays. Both contain `executablePath`, `version`, and `supported`. Process records also contain `processId`. Lifecycle results contain `action`, `changed`, `running`, `processId`, and `executablePath`. A successful launch reports `running: true` without guaranteeing continued process or bridge availability.

Detection returns exit code 3 only when both arrays are empty, otherwise 0. Lifecycle failures return 2 for invalid options, paths, or unsupported versions. Code 4 covers missing installations, inaccessible processes, or launch failures. Code 5 covers missing confirmation, conflicts, or an unknown PID. An exit timeout returns 6. Parser and runtime errors use the usual JSON error object on stderr when `--json` is supplied.

## Capture and proxy routing

`capture start` attaches Fiddler as the system proxy on the Windows host where Fiddler runs. `capture stop` detaches it. Both commands preserve certificate trust and HTTPS-decryption settings.

Explicitly routed traffic can reach a running Fiddler proxy listener whether system-proxy capture is attached or detached. A local client can use the listener's loopback address and proxy port. For remote clients, Fiddler must be configured for remote access on a reachable interface or all interfaces. IPv4 `0.0.0.0` binds all IPv4 interfaces. Clients use a concrete host address and the configured proxy port. Routing and firewall rules must also allow the connection.

Fiddler's proxy listener and the MCP HTTP listener have separate bindings and ports. Changing `mcp service` settings does not configure Fiddler's proxy listener or route client traffic through it. The CLI does not add network routes or firewall rules. To stop explicitly routed traffic, change the client's routing or stop the Fiddler proxy listener through an authorized action.

## Filters and live watching

Session list, watch, and HAR export use the same filters: IDs, method, host, URL, status, response MIME type, process, exact header name, header value, duration, HTTP protocol, combined body size, and error state.

`--body-contains` performs an exact UTF-8 byte search in the request or response body prefix selected by `--body-direction`. `--body-search-bytes` defaults to 65,536 and is capped at 1,048,576 bytes per session. The command searches body bytes only when `--body-contains` is present.

`sessions watch` starts after the current newest ID unless `--after-id` is supplied. It outputs completed matches until it reaches `--count`, the 1-60 second inactivity timeout expires, or the process is cancelled. `--jsonl` outputs one session object per line.

## Bounded session summaries

`sessions list --summary` and `summarize_network_requests` aggregate one metadata page using the existing filters and ordering. The limit defaults to 100 and accepts values from 1 through 1,000. It limits returned records without limiting the bridge's metadata filter scan. Summary mode rejects header-name, header-value, and body-content searches.

```powershell
fiddler-classic-cli sessions list --host example.test --limit 200 --summary --json
```

The result reports `scope: "returned_sessions"`, the limit, matched and returned counts, ID bounds, and `truncated`. All counts and totals cover only returned records. `byHost` groups names without regard to case. `byStatus` uses a separate null group for missing status. Request and response byte totals count captured bodies, including those from incomplete sessions, and exclude headers and wire overhead.

Timing statistics use finite, nonnegative durations from completed sessions in the returned records. They include count, minimum, maximum, median, and nearest-rank p95 in milliseconds. Statistics are null when there are no samples. MCP serialization may omit null fields. Summaries do not read headers or bodies or retrieve later pages. Hostnames and activity counts may still be sensitive.

## Diagnostic files

```powershell
fiddler-classic-cli doctor --output C:\Temp\fiddler-diagnostics.json --json
```

With `--output`, `doctor` creates a new JSON file containing numeric component versions, expected protocol versions, known capabilities, listener state, and predefined errors with guidance on resolving them. Listener metadata includes allowlisted `bindMode`, `startupMode`, and `authenticationMode` strings. It excludes selected IPs, endpoint URLs, adapter names, traffic, headers, bodies, credentials, client identities, local paths, raw error text, and arbitrary version suffixes. It never starts a daemon or creates configuration. When the daemon is stopped, saved listener settings remain unknown.

Specify an absolute path to a regular file in an existing writable directory. The command rejects existing files, stdout (`-`), devices, and alternate data streams. It publishes the complete report atomically. Successful export returns exit code 0 even when the report records an unavailable component. `--json` prints a receipt with the path and format. Invalid paths return 2. Failure to create a file returns 5. Without `--output`, the command uses its existing health checks and exit codes. Review diagnostic files before sharing them.

## Mutation and correlation

`sessions remove --ids ...` removes only the selected sessions and fails if any ID is missing. It has the same interactive confirmation behavior as `sessions clear`.

Replay and request composition normally return as soon as the request is accepted. Add `--wait` to wait for the first completed request whose ID follows the operation's baseline ID and whose method and URL match the operation. A timeout does not revoke an already accepted request.

## Export, diff, and WebSocket

`sessions export <id> --format curl|raw-http` reproduces one sensitive request. Raw HTTP preserves binary body bytes. cURL rejects binary bodies that cannot be represented safely as one shell command. `sessions export --format har` exports the filtered session set to an absolute `.har` path. Overwriting an existing file requires `--overwrite` and confirmation.

`sessions diff` reports differences in metadata, ordered request/response headers, duration, and SHA-256 request/response body hashes without printing bodies. `sessions websocket ... list` returns frame direction, opcode, timestamp, length, continuation, and final-frame state. The `get` command streams a complete frame payload from an optional byte offset.

## AutoResponder

Inspect and configure the live engine:

```powershell
fiddler-classic-cli autoresponder status
fiddler-classic-cli autoresponder configure --enable --permit-fallthrough --accept-connects --use-latency
```

Rules use native Fiddler match and action strings. The bridge assigns each loaded rule a runtime ID and preserves zero-based evaluation order:

```powershell
fiddler-classic-cli autoresponder rules list
fiddler-classic-cli autoresponder rules add "EXACT:https://example.test/api" "*drop"
fiddler-classic-cli autoresponder rules update <rule-id> --action "*reset" --comment "temporary"
fiddler-classic-cli autoresponder rules move <rule-id> 0
fiddler-classic-cli autoresponder rules remove <rule-id> --yes
```

`--disable-on-match` disables a rule after its first match. Per-rule `--latency-ms` applies only when the engine's latency switch is enabled. Rule IDs remain stable while their rule objects remain loaded. List rules again after loading a FARX file.

FARX paths must be absolute. Import adds rules. Replacement is destructive:

```powershell
fiddler-classic-cli autoresponder rules save C:\captures\rules.farx
fiddler-classic-cli autoresponder rules save C:\captures\rules.farx --overwrite --yes
fiddler-classic-cli autoresponder rules load C:\captures\rules.farx
fiddler-classic-cli autoresponder rules load C:\captures\rules.farx --replace --yes
```

The bridge passes native actions to Fiddler unchanged. They can redirect, synthesize, delay, drop, reset, or load a response from a local path accessible to the current user.

## Breakpoints

Arms match future traffic at either the request or response stage. By default, an arm matches once and automatically resumes a managed pause after 30 seconds. Use `--persistent` to retain an arm and `--hold 1..300` to change the timeout in seconds.

```powershell
fiddler-classic-cli breakpoints arm request --method POST --host example.test --hold 45
fiddler-classic-cli breakpoints arm response --status 500 --content-type json
fiddler-classic-cli breakpoints arms
fiddler-classic-cli breakpoints disarm <arm-id>
```

Both stages use the same method, host, URL, process, and stage-header filters. Status and content type are valid only for response arms. `disarm` prevents future matches. Existing pauses remain paused.

Use the monotonically increasing sequence as the wait cursor:

```powershell
fiddler-classic-cli breakpoints list
fiddler-classic-cli breakpoints wait --after-sequence 0 --timeout 30
fiddler-classic-cli breakpoints show <breakpoint-id>
```

`show` returns metadata and exact headers without body bytes. The `sessions body` commands can read a paused session by its session ID. Each mutation is checked against the current stage:

```powershell
fiddler-classic-cli breakpoints update <breakpoint-id> --method PUT --url https://example.test/new --set-header "X-Test: yes" --body-file .\body.bin
fiddler-classic-cli breakpoints update <breakpoint-id> --status 201 --reason Created --remove-header Content-Encoding --body "done"
fiddler-classic-cli breakpoints resume <breakpoint-id>
fiddler-classic-cli breakpoints abort <breakpoint-id> --yes
```

Request pauses accept changes to the method, URL, request headers, and request body. Response pauses accept changes to the status, reason, response headers, and response body. A body update replaces the complete body and is capped at 4 MiB. The list also includes manual breakpoints created in the Fiddler UI. Automatic hold timeouts apply only to pauses from managed arms.

## MCP protocol

`mcp stdio`, foreground `mcp http`, and the daemon-managed HTTP listener use the official C# SDK 2.2.0. Both transports support revision `2026-07-28`, with regression coverage for the `2025-11-25` and `2025-06-18` handshakes. The 36 tools, confirmation arguments, structured results, and 64 KiB payload limit apply across these revisions.

Revision `2026-07-28` permits calls without `initialize`. Use `server/discover` to inspect capabilities and supported modern revisions. Each request supplies `io.modelcontextprotocol/protocolVersion` and `io.modelcontextprotocol/clientCapabilities` in `params._meta`. Clients should also supply `io.modelcontextprotocol/clientInfo`. A compatible SDK constructs this metadata.

For HTTP, send `MCP-Protocol-Version` and `Mcp-Method` on each request, plus `Mcp-Name` for `tools/call`. These headers must match the body. Include both `application/json` and `text/event-stream` in `Accept`. The endpoint is stateless: it issues no protocol session IDs and has no standalone GET event stream or DELETE session route. TCP connections remain visible in the management tab.

Ordinary modern results carry `resultType: "complete"`. Discovery and tool listings use `ttlMs: 0` and `cacheScope: "private"`. These hints do not permit caching captured traffic. Closing an HTTP response stream cancels pending work, including the bridge exchange. An operation already dispatched may have completed.

| HTTP response | Meaning |
| --- | --- |
| 400, JSON-RPC `-32020` | A required modern header is missing or differs from the body. |
| 400, JSON-RPC `-32022` | The requested revision is unsupported. `error.data` gives `requested` and `supported`. |
| 404, JSON-RPC `-32601` | The RPC method is unknown. |
| 401 | Authentication is required and a bearer credential is missing or invalid. |
| 403 | The request contains an `Origin` header, or an anonymous managed request fails Host validation. |
| 405 | The HTTP method has no endpoint route, including GET and DELETE. |

These transport responses are separate from CLI exit codes. Older clients continue to use `initialize` and their negotiated revision's request format. For wire details, see the [MCP specification](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http) and [C# SDK versioning guide](https://csharp.sdk.modelcontextprotocol.io/v2/versioning.html).

## Managed MCP HTTP

The foreground `mcp http` server binds only to loopback, always requires bearer authentication, and exits with a clear bind error if another listener owns its port. Managed settings do not change its authentication or binding. The daemon-managed listener is persistent and defaults to disabled, loopback-only, and `non-loopback` authentication. Inspecting saved state does not start a stopped daemon:

```powershell
fiddler-classic-cli mcp service status
fiddler-classic-cli mcp service configure --bind loopback --port 8877 --authentication non-loopback
fiddler-classic-cli mcp service enable
fiddler-classic-cli mcp service disable
```

`mcp service configure` accepts these options. Omitted options keep their saved values:

| Option | Values and behavior |
| --- | --- |
| `--bind` | `loopback` binds `127.0.0.1`, `all` binds IPv4 `0.0.0.0`, and `selected` binds the explicit addresses supplied with `--address`. |
| `--address` | Repeat for each selected active local IPv4 address, up to 16 unique dotted-decimal unicast addresses. Requires `--bind selected` or a saved `selected` mode. Configuration saves the actual IP addresses. |
| `--port` | A shared port from 1 through 65535 for every configured address. |
| `--startup` | `enabled`, `disabled`, or `last-state` (the default). Controls service state at startup. |
| `--authentication` | `required` checks every request, `non-loopback` (the default) exempts connections whose remote and local socket IPs are both loopback, and `none` skips bearer checks. Applies only to the managed listener. |
| `--yes` | Confirms security warnings without an interactive prompt. Required for such changes when stdin is redirected. |

Fresh or unset configuration uses `non-loopback`. Existing explicit legacy config-file `HttpRequireAuthentication` values load as `required` for `true` and `none` for `false`. The config file stores the string `HttpAuthenticationMode`. Wire and CLI service JSON use `authenticationMode`.

The `non-loopback` exemption uses both actual socket IPs after normalizing IPv4-mapped addresses. Requests to a LAN address require a bearer credential even from the same machine. If either address is unknown, authentication is required. `Host`, `Forwarded`, and `X-Forwarded-For` cannot grant the exemption. Exempt requests are anonymous, have full MCP access, and receive no credential attribution even if they carry a bearer token.

Disable the service before changing the bind mode, selected addresses, port, or authentication. A startup-only change is allowed while running. For example, after inspecting `availableInterfaces` in `mcp service status --json`, select addresses that exist on this machine:

```powershell
fiddler-classic-cli mcp service configure --bind selected --address 127.0.0.1 --address 192.168.1.10 --port 8877
fiddler-classic-cli mcp service enable --yes
```

Replace the example LAN address with an active local IPv4 address. Supplying `--bind selected` requires at least one `--address`. With selected mode already saved, `--address` replaces its address list. Switching to `loopback` or `all` clears that list. The service binds each selected address explicitly. If one is unavailable at startup, it reports a bind error and leaves the daemon available. It never substitutes another address or falls back to listening on all interfaces. Review the selection after DHCP or adapter changes.

Enabling remote access requires confirmation at an interactive warning or `--yes`. With `required` or `non-loopback`, the warning explains that bearer credentials travel over plain HTTP and can be reused if observed. With `none`, it explains that every reachable client gains full MCP access. Saving or enabling loopback-only `non-loopback` needs no access-risk confirmation. Saving or enabling `none` requires confirmation even on loopback. Disabling a stopped service still updates saved state. Disabling the service while connections are active requires confirmation.

Startup policy applies when the daemon starts and when Fiddler loads the extension, including when a daemon is already running. `enabled` starts the listener, `disabled` stops it, and `last-state` preserves the saved desired enabled state. A new installation remains disabled. Changing the policy saves it for the next startup and leaves the current service state unchanged. Use `service enable` or `service disable` for an immediate change. Any configuration update whose resulting policy is `enabled` requires confirmation if it permits remote access or uses mode `none`:

```powershell
fiddler-classic-cli mcp service configure --startup enabled --yes
fiddler-classic-cli mcp service configure --startup disabled
fiddler-classic-cli mcp service configure --startup last-state
```

To skip all managed bearer checks with `none`, first disable the service, then explicitly confirm the risk:

```powershell
fiddler-classic-cli mcp service disable --yes
fiddler-classic-cli mcp service configure --authentication none --yes
fiddler-classic-cli mcp service enable --yes
```

Every client that can reach a listener in mode `none` can read captured traffic and invoke all MCP tools, including mutations. Local processes, including processes under other Windows accounts, have the same access through the default `non-loopback` loopback exemption. Named pipes remain restricted to the current Windows user. Local relays or reverse proxies connecting through loopback appear as loopback peers. Choose `required` if loopback callers or relays must authenticate. Per-tool confirmation arguments remain required where documented, but any connected client can supply them. To require credentials for every request, disable the service and configure `--authentication required` before enabling it again. Use `--authentication non-loopback` to restore the default exemption. Named and default credentials remain saved in all modes. They do not restrict access or identify anonymous requests. All modes reject Origin-bearing requests. Anonymous requests, including loopback exemptions, must also use a Host header matching the receiving socket's actual IP address and port, or `localhost` with that port on loopback. Custom DNS names are rejected for anonymous requests. The project does not enable CORS or configure TLS or firewall rules.

Offline administration requires both an unanswered connection attempt and exclusive access to the daemon's ownership pipe. A timeout from a connected or busy daemon produces an error (`timeout`, exit code 6). In that case, administration does not read saved state as if it were live, write configuration offline, or launch another daemon. Retry the status query before taking further action. Malformed responses and disconnected peers also produce errors.

Invalid modes, ports, or address lists return exit code 2. Missing confirmation and attempts to change listener settings while enabled return 5. An enable-time bind failure returns 4 and can leave `enabled: true` with `running: false`. Check `lastError`, then disable the service before editing its settings. Startup bind failures also leave the daemon available for status and configuration. With `--json`, failures use the structured error on stderr and successful commands return the service status object.

The default CLI token is available through `config token show|rotate`. Named clients receive independent 256-bit tokens:

```powershell
fiddler-classic-cli mcp clients list
fiddler-classic-cli mcp clients authorize --name "Local agent"
fiddler-classic-cli mcp clients deauthorize <client-id> --yes
```

The command prints an authorization token once and stores only its SHA-256 hash. Names contain 1 to 64 characters and must be unique without regard to case. Up to 64 named clients are allowed. Deauthorizing the `default` client rotates the default CLI token. Deauthorization requires confirmation and aborts active connections for that credential. An operation already dispatched may have completed.

Foreground and managed requests that require authentication use the same configured credentials. Rotation and revocation take effect on subsequent requests without a listener restart. Connection inspection and forced disconnection cover only the daemon-managed listener. Revocation does not abort a foreground operation that has already been dispatched. Revoking a credential cannot block anonymous access through `none` or the `non-loopback` loopback exemption.

Connection inspection returns only operational metadata:

```powershell
fiddler-classic-cli mcp connections list
fiddler-classic-cli mcp connections disconnect <connection-id> --yes
```

Records include the connection ID, remote endpoint, authorization and client state, connection and activity times, and active and total request counts. They never include URLs, headers, tokens, or captured traffic. Requests in mode `none` and exempt loopback requests in `non-loopback` are recorded as `anonymous` without token-client attribution. Disconnecting requires confirmation and aborts the selected transport connection.

The **Fiddler Classic CLI** tab contains native **MCP**, **Named pipes**, and **Settings** subtabs. Its Tools menu entry focuses the pane. MCP has an **Enable MCP HTTP** checkbox followed by Bind, Port, and Apply/Refresh. The **MCP addresses** box below these buttons groups loopback and LAN URLs. **Authorized clients** and **Active connections** have separate boxes. The **selected** bind mode provides a checkbox list of local IPv4 addresses and adapter names. These controls manage MCP HTTP independently of Fiddler's capture proxy.

Settings contains startup and authentication dropdowns, each followed by its own explanation. Authentication choices are **Require for all** (`required`), **Non-loopback only** (`non-loopback`, the default), and **No authentication** (`none`). Click **Save settings** to apply changes. It also has a **Versions** box for Fiddler, the bridge, protocol, configured host, and running daemon, plus a **Documentation** box with project and documentation links. Each refresh rereads the configured host and running daemon versions, including after a daemon restart. Named pipes displays read-only [pipe diagnostics](installation.md#named-pipes) directly on the subtab, without a surrounding box.

The extension starts or discovers the daemon and applies the startup policy during load, even when the tab is never opened. Unloading cancels extension work and leaves the daemon running.

Bind and Port remain editable while MCP HTTP is enabled. Refresh preserves pending edits, and displayed URLs continue to reflect the applied settings. Clicking **Apply** with changed bindings stops an enabled listener, saves the settings, and starts it again. The panel confirms remote access or mode `none` and active-client disconnection before stopping it. Cancelling leaves the listener unchanged. A failed step stops the sequence, retains the edits, and displays the error. Check the checkbox and error before retrying. Applying unchanged bindings leaves the listener running. CLI configuration still requires an explicit disable first. Authentication changes also require the service to be disabled, while startup policy stays editable. Refresh preserves grid selection and scroll position. If the selected client or connection disappears, the tab clears that selection and disables its action button.

Background refreshes update changed cells and add or remove rows as clients and connections change. Existing rows keep their position unless you sort the grid. Actions remain available during a read. Clicking an action captures its target and inputs, waits for the current read, and temporarily disables further actions until it finishes. Refresh buttons stay disabled while a read is in progress.

The tab uses a custom terminal icon with a white `>_` prompt, drawn at Fiddler's tab-icon size without an external image file. Service labels and values align in columns, with separate Bind and Port rows. The selected-interface checklist uses `UseCompatibleTextRendering=false` to match the text rendering of surrounding native controls. Action buttons share font-based heights and spacing, including in the authorization dialogs. Grid rows grow with the font and use Windows colors for their backgrounds, text, and selection. Buttons and long status or version text wrap as the pane narrows. Scroll down when the sections no longer fit vertically. The extension leaves Fiddler's DPI compatibility settings unchanged.

Tab control requests and daemon startup each have a ten-second deadline. After a timeout, check service status before retrying because the operation may have completed. Configuration transactions wait up to five seconds for another CLI or daemon update. Failure to acquire the lock in that time reports `timeout` (CLI exit code 6).

Every displayed URL has an adjacent copy button. Loopback is available in `loopback` and `all` modes, and in `selected` mode when the selection includes loopback. In `all` mode, the pane also shows up to eight distinct IPv4 URL hints from active non-loopback adapters. In `selected` mode, LAN URLs correspond only to the selected addresses. These hints do not test reachability. The user remains responsible for routing and Windows Firewall. Disabled service settings can still have address hints.

Service JSON reports `bindAddresses` and `endpoints` arrays for the listener bindings. The singular `bindAddress` and `endpoint` fields describe the first binding. `availableInterfaces` lists up to 64 selectable entries with `address` and `adapterName`. While running, binding, port, and `authenticationMode` describe the settings actually applied to the listener. Otherwise, they describe saved settings. `startupMode` and the desired `enabled` state always reflect saved preferences. `loopbackEndpoint` is empty when selected bindings exclude loopback, and `lanEndpoints` contains the applicable LAN URLs. In `all` mode, `0.0.0.0` is bind metadata. Use a concrete address from the client URL fields when connecting.

Change listener settings through the panel or `mcp service configure`. Editing the configuration file directly does not reconfigure a running listener. Disable it, apply the intended changes, then enable it again.

Controls have accessible names, explicit tab order, and keyboard mnemonics. Use Tab/Shift+Tab to move between controls, Alt+B for bind mode, Alt+P for port, and the underlined button letters for actions. Layout tests cover narrow panes and enlarged fonts without changing Fiddler's DPI settings.

URLs and named-pipe addresses are read-only text fields. Drag to select part of an address, or focus it with Tab and press Ctrl+A to select the whole address, then Ctrl+C to copy. Resizing and unchanged refreshes preserve selection. The adjacent copy button always copies the full address.

## Output

Metadata commands print concise human-readable output by default:

```powershell
fiddler-classic-cli sessions list --host example.test --limit 20
```

Use `--json` for scripts:

```powershell
fiddler-classic-cli sessions list --host example.test --limit 20 --json
```

`sessions body` and `sessions websocket ... get` write complete raw bytes to the required `--output` path. With `--output -`, stdout contains only payload bytes and diagnostics go to stderr.

## Troubleshooting

After the first bridge installation, check Fiddler for "Caution: Unverified Extension Detected" windows if the tab is missing or bridge commands cannot connect. Fiddler can prompt separately for the bridge DLL and the protocol DLL. Review both files and handle the prompts manually before retrying. See [extension approval](installation.md#fiddler-bridge).

If a client cannot negotiate MCP, check that its configured executable and the running daemon use the intended installed build. Restart the relevant server process after an upgrade. For revision `2026-07-28`, inspect a 400 response's JSON-RPC error before retrying. Resolve missing-header errors using that revision's request requirements. For HTTP 403, omit `Origin` in native clients and use a displayed listener URL with its matching Host header. Neither `none` nor the `non-loopback` exemption bypasses Origin or anonymous Host checks. Do not enable CORS to bypass this restriction.

If a selected binding fails after a network change, compare saved `bindAddresses` with `availableInterfaces`. Disable the service, select the intended active local addresses, and apply the change before enabling it again. The listener will not silently widen access. If service state changes when Fiddler or the daemon starts, check **Settings** or `startupMode`. Use `last-state` to preserve the last desired state.

Check daemon, service, and bridge status when a command cannot connect:

```powershell
fiddler-classic-cli daemon status
fiddler-classic-cli mcp service status
fiddler-classic-cli doctor
```

Restart only the CLI daemon when its pipe is stale:

```powershell
fiddler-classic-cli daemon stop
fiddler-classic-cli daemon start
```

Restart an older daemon when the Fiddler tab reports that the `managed-http-v3` capability is missing. The daemon envelope remains version 1 and the bridge remains version 3. Restart Fiddler Classic only when `doctor` reports that the extension bridge is not connected. The daemon and managed listener can run while Fiddler is closed. Bridge operations will return an unavailable or timeout error with guidance on resolving it.
