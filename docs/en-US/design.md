# CLI and MCP design

**English (en-US)** | [简体中文 (zh-CN)](../zh-CN/design.md)

This integration follows the agent-facing principles and list/detail tool pattern of [Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp). It preserves Fiddler Classic's terminology and process-wide capture model.

## Feature classification

Features are classified as Native, Native adapter, Custom, Hybrid, or Host according to the source of their behavior. The [feature type and native compatibility matrix](feature-types.md) defines the parity contract for each CLI and MCP area. Custom query, wait, diff, and transport operations have independent specifications and make no claim to reproduce similarly named Fiddler UI tabs.

## Distribution and installation

The published Windows x64 host is a self-contained single-file executable. It embeds the .NET runtime, managed dependencies, bridge and protocol DLLs, and `LICENSE`. The `bridge install` command deploys the embedded DLLs. .NET extracts native runtime files to `%TEMP%\.net` by default when the executable runs. The publish and release directories each contain only this EXE, and GitHub releases upload only this file. Documentation and Agent Skills remain in the source repository. CI passes executable and embedded-bridge hashes between jobs as metadata.

`scripts/install.ps1` downloads the exact `fiddler-classic-cli.exe` asset from the latest release of `SpecterShell/FiddlerClassicCLI` by default. It requires GitHub's SHA-256 asset digest and verifies the bytes before execution. An explicit `-PackagePath` selects a trusted local executable or directory, or a ZIP that requires an adjacent `SHA256SUMS` entry matching its exact filename. The script uses the fixed default directory `%LOCALAPPDATA%\Programs\FiddlerClassicCLI`, updates the current-user PATH, and deploys the bridge unless those steps are explicitly omitted. It does not start Fiddler, the daemon, or MCP servers or set up Agent Skills. See [installation](installation.md) for options and output.

## Inspection workflow

The official C# SDK 2.2.0 handles MCP serialization and transport lifecycle. Revision `2026-07-28` uses per-request metadata and optional `server/discover` discovery. Older clients use the initialization handshake. HTTP uses `HttpServerSessionMode.Stateless` for both foreground and daemon-managed listeners. Bridge protocol 3 and daemon protocol 1 are independent of the MCP revision.

Wire tests send raw JSON-RPC over both transports to verify discovery, tool schemas and safety annotations, version errors, and bounded structured results. SDK-client tests pin each of `2026-07-28`, `2025-11-25`, and `2025-06-18` so automatic fallback cannot hide a regression. HTTP tests also cover header/body consistency and cancellation through to a pending bridge exchange. No optional Tasks, Apps, or server-to-client interaction capabilities are advertised by this project.

Choose the tool with the narrowest scope that answers the current question:

1. Call `list_network_requests` to filter summaries and obtain request IDs.
2. Call `get_network_request` for one transaction. Headers require an explicit request through `includeHeaders=true`.
3. Call `get_network_request_body` only when payload bytes are needed. Read at most 64 KiB and continue with `offset + bytesReturned` until `eof=true`.
4. Call `wait_for_network_request` to obtain the next completed match without polling the full list.

The CLI provides the same inspection sequence through `sessions list`, `sessions show`, and `sessions body`. Human-readable output is the default. `--json` provides stable structured output for scripts. The CLI writes complete bodies to an explicit file or stdout and never embeds them in a metadata result.

The bare executable and command groups display their own help on stdout and exit 0, as with `--help`. Missing required inputs and other parser errors display command help and errors on stderr and exit 2 without actions. With `--json`, stderr contains only the structured error. Parser errors leave stdout empty so payload streams remain safe.

Bridge-backed CLI commands use a background daemon that starts automatically. Each invocation sends one framed request over a Windows named pipe restricted to the current user, and the daemon relays the existing versioned bridge protocol to Fiddler. Fiddler owns the capture state. The daemon relays requests without owning that state.

The daemon also owns the optional persistent MCP HTTP listener. The Fiddler extension accesses it through the same current-user control pipe. Foreground `mcp http` and the daemon listener share one Kestrel host implementation. Foreground HTTP always requires authentication and binds only to loopback. The Fiddler pane uses native MCP, Named pipes, and Settings subtabs. MCP places an enable checkbox above editable binding controls, with addresses, clients, and connections in separate boxes. Settings holds startup and authentication dropdowns with descriptive choices, versions, and project and documentation links. The selected-interface checklist sets `UseCompatibleTextRendering=false` to match surrounding native controls. The tab runs control and process work outside the UI thread and refreshes every two seconds without overlapping requests. It marshals only control updates back to Fiddler. Unloading the extension removes the tab and menu item. The daemon and listener keep running.

Each tab control exchange has a ten-second deadline covering connection, request writing, and response reading. Cancellation closes the pipe to interrupt pending .NET Framework I/O. Startup discovery has its own ten-second deadline. Rejecting a daemon `stop` envelope does not shut down the process. The buffered status grids match rows by stable ID, write only changed cells, and update membership without rebuilding the table. Sorting runs when membership or values in the sorted column change. Selection and viewport follow the same IDs. If the selected item has been removed, the selection remains empty. Refresh preserves pending edits while the listener is enabled or disabled.

The panel coalesces background reads and keeps action buttons available during polling. An explicit action captures its inputs before awaiting the current read, blocks further actions, and suppresses polling until it completes. Disposal cancels pending reads and drops actions that are still waiting. Layout, action handling, and refresh coordination live in separate panel files. Shared native button and action-row controls keep panel and dialog sizing consistent without adding a UI dependency.

`bridge install` writes a current-user launch record outside Fiddler's Scripts directory. The record identifies the exact host executable and version that the extension may start. Daemon status reports support for the managed listener settings through the `managed-http-v3` capability. The extension asks the user to restart a daemon that lacks it. The daemon envelope remains version 1, the pipe name is unchanged, and the bridge remains protocol version 3.

The extension's startup task runs independently of tab creation or selection. It starts or discovers the daemon and applies the HTTP startup policy, including to an already-running daemon. Daemon startup also applies the policy. `enabled` starts the listener, `disabled` stops it, and the default `last-state` uses the saved desired enabled state. Saving a policy does not immediately change the running listener. Unloading cancels extension work without stopping the daemon. Each tab refresh reads the launch record and daemon status outside the UI thread to keep the displayed versions current.

The read-only Named pipes subtab combines a local bridge listener snapshot with the existing daemon status response. It shows full pipe paths, supported protocol versions, current-user ACL scope, and the daemon PID. Bridge lifecycle state and the last listener error come from the bridge server. A daemon status failure clears process details to `Unknown` and reports the check failure without inferring that the daemon has stopped. The subtab exposes no credentials or traffic and adds no pipe connection tracking.

Pipe status uses the tab's two-second, nonoverlapping refresh. The separate **Refresh pipes** action reads only the bridge snapshot and daemon status. It does not invoke startup or listener configuration. Daemon startup remains the responsibility of extension initialization. The display uses the existing bridge v3 and daemon v1 protocols.

Discovery reports the daemon as stopped only if the connection attempt times out before connecting and it acquires the existing daemon ownership pipe. Offline administration retains ownership throughout its read or configuration transaction to prevent a concurrent daemon startup. Timeouts after connection, rejected status requests, and malformed responses propagate as errors. Shutdown waits for ownership release after the stop acknowledgment.

Managed HTTP defaults to disabled, loopback-only, and `non-loopback` authentication. Saved configuration contains its desired enabled state, bind mode, selected IPv4 addresses, port, startup mode, `HttpAuthenticationMode` string, default token, and named client records. The wire DTO property `AuthenticationMode` accepts `required`, `non-loopback`, and `none`. Fresh or unset configuration uses `non-loopback`. Loading explicit legacy config-file `HttpRequireAuthentication` values preserves `true` as `required` and `false` as `none`. Wire and CLI service JSON use `authenticationMode`. The default token remains available for display. Named tokens are generated from 256 random bits, displayed once, and stored only as SHA-256 hashes. The authentication layer compares hashes in fixed time and reloads credentials after an atomic configuration replacement.

Bindings are `loopback` (`127.0.0.1`), `all` (`IPAddress.Any`, or `0.0.0.0`), or `selected`. Selected mode accepts up to 16 explicit active local IPv4 addresses, all on the same port. Configuration stores addresses rather than adapter identities. Switching to loopback or all-interface mode clears the saved selection. The listener never substitutes an address or widens a binding when an adapter disappears or its address changes. A bind failure leaves the daemon available and reports the error. The daemon accepts binding, port, and authentication changes only while the service is disabled. The panel lets users edit bindings while enabled and applies them through the existing stop/configure/start controls. It reads fresh state and collects the required access and disconnection confirmations before stopping. Unchanged bindings avoid a restart. A failed step ends the sequence and refreshes state without retrying a mutation. Startup-only changes are allowed while running.

Status returns listener bindings in `bindAddresses` and `endpoints`, with the first binding also in `bindAddress` and `endpoint`. Running status uses the applied binding, port, and `authenticationMode` snapshot, even if the file has been edited directly. Startup policy and desired enabled state remain current with saved preferences. Status supplies up to 64 selectable `availableInterfaces` records containing `address` and `adapterName`. `loopbackEndpoint` is empty when selected bindings exclude loopback. `lanEndpoints` lists selected non-loopback URLs or up to eight active-adapter hints in all-interface mode. These values do not prove remote reachability.

A current-user named mutex protects each complete configuration read-modify-write transaction across processes and Windows sessions. Its name derives from the normalized configuration path. A transaction waits at most five seconds to acquire it. Writes use atomic replacement and current-user ACLs. If a process exits while holding the mutex, the next owner reloads the last complete configuration file.

Credential snapshots read token hashes and the configuration file timestamp under the same mutex. A concurrent rotation cannot associate a newer timestamp with older credentials. Foreground and managed requests that require authentication share this reload behavior for default and named credentials. The daemon's connection controls apply only to its own listener. Foreground requests already dispatched can continue after revocation. Mode `required` checks every request. Mode `non-loopback` bypasses bearer checks only when both actual remote and local socket IPs are loopback, after normalizing IPv4-mapped addresses. A request to a LAN address requires authentication even when sent locally. Unknown socket addresses fail closed. Host, Forwarded, and X-Forwarded-For never grant this exemption. Mode `none` bypasses bearer checks for all clients. All modes preserve credentials. Bypassed requests are anonymous and have no token-client attribution, even if they carry a token. Revoking a credential cannot restrict anonymous access.

Kestrel connection middleware records only connection IDs, remote endpoints, authorization and client state, connection and activity times, and active and total request counts. It retains no URL, header, token, body, or captured traffic. Disconnecting aborts one transport connection. Revoking a credential aborts every active connection associated with it. An operation already dispatched may have completed.

Completed-session notifications populate a bounded in-process ID buffer. Wait operations sleep outside Fiddler's UI thread, then marshal only the matching snapshot back to the UI thread. Request composition and replay return a baseline session ID and the expected method and URL. The optional wait uses these values to locate the first matching completed session.

## Capture attachment

`capture start` and `capture stop` call Fiddler's native attach and detach operations for the host Windows system proxy. They preserve certificate trust and HTTPS-decryption settings. A running Fiddler proxy listener can still receive explicitly routed traffic while detached, through loopback or a reachable interface configured for remote access. An IPv4 `0.0.0.0` binding listens on all IPv4 interfaces. Clients connect to a concrete host address and proxy port, subject to routing and firewall rules. MCP HTTP binding controls a separate listener and does not configure Fiddler's proxy access.

## Fiddler application lifecycle

`FiddlerEnvironment` shares read-only installation discovery between diagnostics, bridge installation, and the CLI-only `app` commands. `FiddlerAppService` coordinates selection, confirmation, and bounded exit waits through a small Windows process interface. Its tests use fake process handles without launching or closing the user's Fiddler instance.

The Windows backend verifies the process owner SID, Windows session, executable path, and supported version before selecting a target. It retains a native process handle through the operation to prevent PID reuse from changing the target. A current-user-only ownership pipe serializes lifecycle mutations across CLI processes without a thread-affine mutex. No bridge or daemon protocol changes are needed.

Explicit launches pass only `-noattach`. Close uses `Process.CloseMainWindow` and a cancellable exit wait. Native dialogs remain the user's responsibility. Restart validates the executable before closing and launches the same path only after exit. Neither command saves captures or stops the daemon. Timeouts and cancellations stop waiting without terminating Fiddler or launching a replacement. Full command and error contracts are in the [CLI guide](cli.md#fiddler-application).

## AutoResponder model

The bridge accesses Fiddler's live `AutoResponder` instance on the UI thread. It assigns opaque IDs to loaded rule objects, exposes their exact match/action text, and changes priority through Fiddler's native promote/demote operations. Import uses Fiddler's FARX importer. Replacement uses its rule loader. FARX bytes remain on disk. The protocol represents them only by validated absolute paths.

The bridge leaves native action interpretation to Fiddler. This preserves Fiddler behavior and avoids a second, divergent rule engine. Mutation tools are therefore open-world operations: an action may affect network traffic or read a local response file with the current user's permissions.

## Breakpoint model

An arm is a bounded in-memory filter for future request or response headers. On a match, the extension sets Fiddler's `x-breakrequest` or `x-breakresponse` session flag and tracks the transition into its hand-tamper state. One-shot arms are removed as soon as they match.

Pending breakpoint IDs identify live pauses. A monotonically increasing sequence supports wait cursors even when the same Fiddler session pauses once for its request and later for its response. Managed pauses have a 1-300 second hold, after which the bridge calls `ThreadResume`. The bridge can discover and control pauses created manually in Fiddler. It imposes no timeout on these pauses.

The bridge allows mutation only while the session remains in the expected hand-tamper state. Request pauses accept changes to the method, URL, headers, and body. Response pauses accept changes to the status, reason, headers, and body. Body replacement is limited to 4 MiB. Resume and abort remove the pending handle before changing Fiddler state so concurrent callers cannot act twice.

## Pagination

Fiddler captures can change while an agent is inspecting them. `list_network_requests` uses inclusive request-ID bounds to paginate the results, without page indexes. When more filtered matches exist, the response includes one continuation value:

- Newest-first results return `nextMaxRequestId`. Pass it back as `maxRequestId` with the same filters.
- Oldest-first results return `nextMinRequestId`. Pass it back as `minRequestId` with the same filters.

Request-ID bounds prevent duplicates that would occur if new sessions shifted numeric page boundaries.

## Output boundaries

- Lists contain metadata and exclude headers and bodies.
- Details include exact ordered headers only when requested and always exclude bodies.
- MCP body results are bounded chunks with explicit encoding metadata.
- WebSocket lists contain frame metadata. Payloads use a separate 64 KiB MCP chunk API.
- CLI body output preserves the complete raw payload without normalization.
- Large archives use absolute `.saz` paths. Archive data is not sent inline.
- FARX rule sets use absolute `.farx` paths. Rule-set data is not sent inline.
- Breakpoint details contain metadata and opt-in headers. Existing bounded body tools read paused payloads.
- cURL, raw HTTP, HAR, and diff are explicit sensitive-evidence operations. Diff reports body comparisons through hashes and omits the body bytes.

These boundaries reduce token use in routine calls without changing captured evidence.

## Metadata aggregation and diagnostics

`SessionSummaryService` handles both `sessions list --summary` and `summarize_network_requests`. It makes one bounded list call, rejects header/body searches, and aggregates only returned records. Captured request/response body lengths exclude headers and wire overhead. Finite durations from completed requests supply the median and nearest-rank p95. Matched/returned counts and truncation indicate when results are partial. The result limit applies to output without limiting the metadata scan used to match filters.

`DiagnosticExportService` probes the bridge and daemon directly through read-only operations. It never invokes the auto-starting relay or reads configuration. A separate report DTO permits only numeric versions, known capabilities, listener state, and predefined errors. Listener metadata includes the allowlisted `bindMode`, `startupMode`, and `authenticationMode` strings. It excludes selected IPs, endpoints, adapter names, traffic, token fields, client names, local paths, and raw exceptions. Saved listener settings remain unknown when the daemon is stopped. The service publishes the file atomically without overwriting an existing file. The output-path receipt is separate from the report.

CLI commands are organized by diagnostics/lifecycle, session inspection, session actions, WebSockets, requests, and HTTP administration. They share option and output helpers. Dispatcher partials separate inspection, mutations, projections, completion waits, and WebSocket access while preserving the UI-thread gateway. The management panel separates layout from interaction logic.

## Safety model

Tool annotations distinguish read-only inspection, destructive deletion or in-flight mutation, and open-world network operations. Clearing, selective removal, archive/FARX replacement, rule deletion, and session abort require explicit confirmation where applicable. Managed HTTP also requires confirmation for remote enablement, saving or enabling `none`, startup settings that permit such access, client revocation, connection disconnection, and disabling the service while clients are connected. Captured headers and bodies are never silently redacted, so callers must treat explicit evidence output as sensitive.

The host connects to the user's running Fiddler Classic instance. It does not launch Fiddler automatically, attach the system proxy without a command, or install or trust certificates. Managed HTTP uses plain HTTP and defaults to `non-loopback` authentication. Remote enablement warns that observed bearer credentials can be reused. Saving or enabling `none` requires explicit acknowledgment that every reachable client receives full MCP capabilities, including access to captured traffic and mutation tools. Saving or enabling loopback-only `non-loopback` needs no access-risk warning. Its exempt requests have the same full MCP access. Local processes, including those under other Windows accounts, can use anonymous loopback MCP; named pipes remain current-user-only. Local relays and reverse proxies connecting through loopback appear as loopback peers. Use `required` if loopback callers or relays must authenticate. Existing tool confirmation arguments still apply, and any connected client can supply them.

Both foreground and managed HTTP servers reject all Origin-bearing requests in every authentication mode. Anonymous managed requests, including loopback exemptions, also require a Host header matching the receiving socket's actual IP address and port, or `localhost` on a loopback socket, to prevent DNS rebinding. The project has no CORS, TLS, certificate, or firewall automation. Client-supplied MCP metadata does not grant access or establish client identity. Authenticated requests derive their identity from bearer credentials. Unauthenticated managed requests remain anonymous. Foreground `mcp http` always requires authentication.

## Intentional differences

Chrome DevTools MCP scopes `list_network_requests` and `get_network_request` to a page. This project uses the same request-centric MCP vocabulary to query Fiddler Classic's process-wide session list. A separate `get_network_request_body` tool prevents a routine metadata call from returning large or sensitive payloads. The human-readable CLI uses `sessions` to match Fiddler Classic's UI terminology.
