# CLI and MCP design

**English (en-US)** | [简体中文 (zh-CN)](../zh-CN/design.md)

This integration follows the agent-facing principles and list/detail tool pattern of [Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp). It preserves Fiddler Classic's terminology and process-wide capture model.

## Feature classification

Features are classified as Native, Native adapter, Custom, Hybrid, or Host according to the source of their behavior. The [feature type and native compatibility matrix](feature-types.md) defines the parity contract for each CLI and MCP area. Custom query, wait, diff, and transport operations have independent specifications and make no claim to reproduce similarly named Fiddler UI tabs.

## Inspection workflow

The official C# SDK 2.2.0 handles MCP serialization and transport lifecycle. Revision `2026-07-28` uses per-request metadata and optional `server/discover` discovery; older clients use the initialization handshake. HTTP uses `HttpServerSessionMode.Stateless` for both foreground and daemon-managed listeners. Bridge protocol 3 and daemon protocol 1 are independent of the MCP revision.

Wire tests send raw JSON-RPC over both transports to verify discovery, tool schemas and safety annotations, version errors, and bounded structured results. SDK-client tests pin each of `2026-07-28`, `2025-11-25`, and `2025-06-18` so automatic fallback cannot hide a regression. HTTP tests also cover header/body consistency and cancellation through to a pending bridge exchange. No optional Tasks, Apps, or server-to-client interaction capabilities are advertised by this project.

Choose the tool with the narrowest scope that answers the current question:

1. Call `list_network_requests` to filter summaries and obtain request IDs.
2. Call `get_network_request` for one transaction. Headers require an explicit request through `includeHeaders=true`.
3. Call `get_network_request_body` only when payload bytes are needed. Read at most 64 KiB and continue with `offset + bytesReturned` until `eof=true`.
4. Call `wait_for_network_request` to obtain the next completed match without polling the full list.

The CLI provides the same inspection sequence through `sessions list`, `sessions show`, and `sessions body`. Human-readable output is the default; `--json` provides stable structured output for scripts. The CLI writes complete bodies to an explicit file or stdout and never embeds them in a metadata result.

Bridge-backed CLI commands use a background daemon that starts automatically. Each invocation sends one framed request over a Windows named pipe restricted to the current user, and the daemon relays the existing versioned bridge protocol to Fiddler. Fiddler owns the capture state; the daemon does not.

The daemon also owns the optional persistent MCP HTTP listener. The Fiddler extension accesses it through the same current-user control pipe. Foreground `mcp http` and the daemon listener share one Kestrel host implementation. The Fiddler tab runs control and process work outside the UI thread and refreshes every two seconds without overlapping requests. It marshals only control updates back to Fiddler. Unloading the extension removes the tab and menu item but leaves the daemon and listener running.

Each tab control exchange has a ten-second deadline covering connection, request writing, and response reading. Cancellation closes the pipe to interrupt pending .NET Framework I/O. Startup discovery has its own ten-second deadline. Rejecting a daemon `stop` envelope does not shut down the process. Grid refresh uses stable IDs to restore selection and viewport; if the selected item has been removed, the selection remains empty. Refresh preserves pending settings while the service is disabled.

`bridge install` writes a current-user launch record outside Fiddler's Scripts directory. The record identifies the exact host executable and version that the extension may start. Daemon status reports support for managed HTTP through the additive `managed-http-v1` capability. This lets a new extension ask the user to restart an older daemon without changing daemon protocol version 1 or the existing pipe name.

The extension's startup task runs independently of tab creation or selection. Unloading cancels the task without stopping the daemon. Each tab refresh reads the launch record and daemon status outside the UI thread to keep the displayed versions current.

Discovery reports the daemon as stopped only if the connection attempt times out before connecting and it acquires the existing daemon ownership pipe. Offline administration retains ownership throughout its read or configuration transaction to prevent a concurrent daemon startup. Timeouts after connection, rejected status requests, and malformed responses propagate as errors. Shutdown waits for ownership release after the stop acknowledgment.

The HTTP service is disabled and bound to loopback by default. Saved configuration contains its enable state, bind mode, port, the default token retained for compatibility and available for display, and named client records. Named tokens are generated from 256 random bits, displayed once, and stored only as SHA-256 hashes. The authentication layer compares hashes in fixed time and reloads credentials after an atomic configuration replacement.

A current-user named mutex protects each complete configuration read-modify-write transaction across processes and Windows sessions. Its name derives from the normalized configuration path. A transaction waits at most five seconds to acquire it. Writes use atomic replacement and current-user ACLs. If a process exits while holding the mutex, the next owner reloads the last complete configuration file.

Credential snapshots read token hashes and the configuration file timestamp under the same mutex. A concurrent rotation cannot associate a newer timestamp with older credentials. Foreground and managed listeners share this reload behavior for default and named credentials. The daemon's connection controls apply only to its own listener; foreground requests already dispatched can continue after revocation.

Kestrel connection middleware records only connection IDs, remote endpoints, authorization and client state, connection and activity times, and active and total request counts. It retains no URL, header, token, body, or captured traffic. Disconnecting aborts one transport connection. Revoking a credential aborts every active connection associated with it; an operation already dispatched may have completed.

Completed-session notifications populate a bounded in-process ID buffer. Wait operations sleep outside Fiddler's UI thread, then marshal only the matching snapshot back to the UI thread. Request composition and replay return a baseline session ID and the expected method and URL. The optional wait uses these values to locate the first matching completed session.

## Fiddler application lifecycle

`FiddlerEnvironment` shares read-only installation discovery between diagnostics, bridge installation, and the CLI-only `app` commands. `FiddlerAppService` coordinates selection, confirmation, and bounded exit waits through a small Windows process interface. Its tests use fake process handles; they do not launch or close the user's Fiddler instance.

The Windows backend verifies the process owner SID, Windows session, executable path, and supported version before selecting a target. It retains a native process handle through the operation to prevent PID reuse from changing the target. A current-user-only ownership pipe serializes lifecycle mutations across CLI processes without a thread-affine mutex. No bridge or daemon protocol changes are needed.

Explicit launches pass only `-noattach`. Close uses `Process.CloseMainWindow` and a cancellable exit wait; native dialogs remain the user's responsibility. Restart validates the executable before closing and launches the same path only after exit. Neither command saves captures or stops the daemon. Timeouts and cancellations stop waiting without terminating Fiddler or launching a replacement. Full command and error contracts are in the [CLI guide](cli.md#fiddler-application).

## AutoResponder model

The bridge accesses Fiddler's live `AutoResponder` instance on the UI thread. It assigns opaque IDs to loaded rule objects, exposes their exact match/action text, and changes priority through Fiddler's native promote/demote operations. Import uses Fiddler's FARX importer; replacement uses its rule loader. FARX bytes remain on disk; the protocol represents them only by validated absolute paths.

The bridge leaves native action interpretation to Fiddler. This preserves Fiddler behavior and avoids a second, divergent rule engine. Mutation tools are therefore open-world operations: an action may affect network traffic or read a local response file with the current user's permissions.

## Breakpoint model

An arm is a bounded in-memory filter for future request or response headers. On a match, the extension sets Fiddler's `x-breakrequest` or `x-breakresponse` session flag and tracks the transition into its hand-tamper state. One-shot arms are removed as soon as they match.

Pending breakpoint IDs identify live pauses. A monotonically increasing sequence supports wait cursors even when the same Fiddler session pauses once for its request and later for its response. Managed pauses have a 1-300 second hold, after which the bridge calls `ThreadResume`. Pauses created manually in Fiddler can be discovered and controlled, but the bridge imposes no timeout on them.

The bridge allows mutation only while the session remains in the expected hand-tamper state. Request pauses accept changes to the method, URL, headers, and body; response pauses accept changes to the status, reason, headers, and body. Body replacement is limited to 4 MiB. Resume and abort remove the pending handle before changing Fiddler state so concurrent callers cannot act twice.

## Pagination

Fiddler captures can change while an agent is inspecting them. `list_network_requests` uses inclusive request-ID bounds to paginate the results, without page indexes. When more filtered matches exist, the response includes one continuation value:

- Newest-first results return `nextMaxRequestId`; pass it back as `maxRequestId` with the same filters.
- Oldest-first results return `nextMinRequestId`; pass it back as `minRequestId` with the same filters.

Request-ID bounds prevent duplicates that would occur if new sessions shifted numeric page boundaries.

## Output boundaries

- Lists contain metadata and exclude headers and bodies.
- Details include exact ordered headers only when requested and always exclude bodies.
- MCP body results are bounded chunks with explicit encoding metadata.
- WebSocket lists contain frame metadata; payloads use a separate 64 KiB MCP chunk API.
- CLI body output preserves the complete raw payload without normalization.
- Large archives use absolute `.saz` paths; archive data is not sent inline.
- FARX rule sets use absolute `.farx` paths; rule-set data is not sent inline.
- Breakpoint details contain metadata and opt-in headers; existing bounded body tools read paused payloads.
- cURL, raw HTTP, HAR, and diff are explicit sensitive-evidence operations. Diff returns body hashes rather than body bytes.

These boundaries reduce token use in routine calls without changing captured evidence.

## Metadata aggregation and diagnostics

`SessionSummaryService` handles both `sessions list --summary` and `summarize_network_requests`. It makes one bounded list call, rejects header/body searches, and aggregates only returned records. Captured request/response body lengths exclude headers and wire overhead. Finite durations from completed requests supply the median and nearest-rank p95; matched/returned counts and truncation indicate when results are partial. The result limit applies to output without limiting the metadata scan used to match filters.

`DiagnosticExportService` probes the bridge and daemon directly through read-only operations. It never invokes the auto-starting relay or reads configuration. A separate report DTO permits only numeric versions, known capabilities, listener state, and predefined errors. It cannot serialize traffic, token fields, client names, LAN addresses, local paths, or raw exceptions. Saved listener settings remain unknown when the daemon is stopped. The service publishes the file atomically without overwriting an existing file; the output-path receipt is separate from the report.

CLI commands are organized by diagnostics/lifecycle, session inspection, session actions, WebSockets, requests, and HTTP administration. They share option and output helpers. Dispatcher partials separate inspection, mutations, projections, completion waits, and WebSocket access while preserving the UI-thread gateway. The management panel separates layout from interaction logic.

## Safety model

Tool annotations distinguish read-only inspection, destructive deletion or in-flight mutation, and open-world network operations. Clearing, selective removal, archive/FARX replacement, rule deletion, and session abort require explicit confirmation where applicable. Managed HTTP also requires confirmation for remote enablement, client revocation, connection disconnection, and disabling the service while clients are connected. Captured headers and bodies are never silently redacted, so callers must treat explicit evidence output as sensitive.

The host connects to the user's running Fiddler Classic instance. It does not launch Fiddler automatically, attach the system proxy without a command, or install or trust certificates. Binding MCP HTTP to `0.0.0.0` requires an explicit IPv4 option. It uses plain HTTP with mandatory bearer authentication and no CORS, TLS, certificate, or firewall automation. Both HTTP modes reject all Origin-bearing requests with HTTP 403 because no browser origins are authorized. Client-supplied MCP metadata does not grant access or identify an authorized client; bearer authentication determines that identity.

## Intentional differences

Chrome DevTools MCP scopes `list_network_requests` and `get_network_request` to a page. This project uses the same request-centric MCP vocabulary to query Fiddler Classic's process-wide session list. A separate `get_network_request_body` tool prevents a routine metadata call from returning large or sensitive payloads. The human-readable CLI uses `sessions` to match Fiddler Classic's UI terminology.
