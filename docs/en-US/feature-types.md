# Feature types and native compatibility

**English (en-US)** | [Simplified Chinese (zh-CN)](../zh-CN/feature-types.md)

Each public feature is classified by the source of its behavior. The type describes its implementation and compatibility contract. It does not indicate whether both CLI and MCP provide the feature.

## Types

| Type | Meaning |
| --- | --- |
| Native | Delegates the operation to a Fiddler Classic API or UI command. Fiddler controls the resulting behavior and state. |
| Native adapter | Uses live Fiddler objects, events, flags, or engines and adds transport-safe IDs, projections, validation, or bounded payload access. It preserves native side effects and evidence. |
| Custom | Implements an agent operation for which Fiddler has no equivalent command. The documented CLI/MCP contract defines its behavior. It does not alter similarly named Fiddler UI settings. |
| Hybrid | Reads native Fiddler state through the bridge and completes orchestration or serialization in the external host. |
| Host | Runs outside Fiddler and does not use the Fiddler extension API. |

## Feature matrix

| Feature | CLI or MCP surface | Type | Compatibility behavior |
| --- | --- | --- | --- |
| Offline and live status | `status`, `get_status` | Hybrid | The host discovers installations and processes. Proxy, listener, HTTPS decryption, version, and session counts come from the live Fiddler instance. |
| System proxy capture | `capture start\|stop`, `start_capture`, `stop_capture` | Native | Calls Fiddler's attach or detach command and returns the resulting native proxy state. It does not change certificate trust. |
| Session snapshot and details | `sessions list\|show`, `list_network_requests`, `get_network_request` | Native adapter | Reads Fiddler's current session list and preserves native IDs, states, metadata, and exact header order. |
| Session query filters and pagination | List filters and MCP continuation bounds | Custom | Filters a snapshot without changing Fiddler's Filters tab, visibility rules, or retained sessions. Matching and ID-bound pagination follow this project's documented semantics. |
| Session waiting and watching | `sessions watch`, `wait_for_network_request` | Custom | Observes Fiddler completion events and returns the first completed match whose session ID exceeds the specified bound. It does not create a Fiddler UI filter. |
| Request and response body access | `sessions body`, `get_network_request_body` | Native adapter | Copies exact bytes from the native `Session` body arrays. Chunking, offsets, and text/base64 metadata apply only to transport; they do not decode or normalize payload bytes. |
| Session clearing and removal | `sessions clear\|remove`, `clear_network_requests`, `remove_network_requests` | Native | Invokes Fiddler session-list removal commands. Reported counts reflect the sessions Fiddler actually removed. |
| SAZ archives | `sessions save\|load`, `save_network_archive`, `load_network_archive` | Native | Uses Fiddler's SAZ reader and writer. Path validation and overwrite confirmation are bridge safety controls. |
| Request replay | `sessions replay`, `replay_network_request` | Native | Uses Fiddler's reissue operation, including its native unconditional-replay option. Optional result correlation is custom host orchestration. |
| Request composition | `request send`, `send_request` | Native adapter | Validates and builds one HTTP request, then submits it through Fiddler's native request-issue API. Parity with Composer options such as redirects, automatic authentication, or Scratchpad batches is not guaranteed. |
| cURL, raw HTTP, and HAR export | `sessions export` | Hybrid | Reads exact native session evidence through the bridge, then serializes the selected format in the host. These are not Fiddler transcoder plug-ins. |
| Session diff | `sessions diff`, `diff_network_requests` | Custom | Compares metadata, ordered headers, duration, and exact body hashes. It does not invoke Fiddler's visual Compare command or an external diff application. |
| WebSocket inspection | `sessions websocket`, `list_websocket_messages`, `get_websocket_message` | Native adapter | Reads Fiddler's native WebSocket frame collection and exact payload bytes. Pagination and bounded payload chunks are custom transport behavior. |
| AutoResponder settings and execution | `autoresponder status\|configure`, corresponding MCP tools | Native | Reads and changes Fiddler's live AutoResponder engine. Fiddler interprets match and action strings; the bridge does not implement a second rule engine. |
| AutoResponder rule management | Rule list, add, update, move, remove, and clear tools | Native adapter | Uses live Fiddler rule objects. Priority changes call Fiddler's native promote/demote operations, leaving evaluation order and grouping behavior under Fiddler's control. Runtime IDs are handles used only by the bridge. |
| FARX persistence | AutoResponder rule `save` and `load` tools | Native | Uses Fiddler's native FARX save, import, and replacement operations. The bridge adds path validation, rollback, limits, and confirmations. |
| Native breakpoint handling | Breakpoint show, update, resume, and abort tools | Native adapter | Uses Fiddler's request/response hand-tamper states, native session headers and bodies, `ThreadResume`, and native abort behavior. Manual Fiddler breakpoints remain discoverable. |
| Breakpoint arms and waits | Breakpoint arm, disarm, list, wait, and hold timeout tools | Custom | Adds bounded filters, opaque IDs, sequence cursors, one-shot behavior, and automatic resume. A match activates the native breakpoint by setting Fiddler's documented breakpoint flag. |
| Diagnostics and installation | `doctor`, `bridge install\|uninstall` | Host | Discovers files, processes, versions, pipes, and configuration. It copies bridge assemblies and maintains the current-user host launch record. Only the connectivity probe accesses the live bridge. |
| Fiddler application lifecycle | `app detect\|open\|close\|restart` (CLI only) | Host | Finds executables and current-user, current-session processes through Windows APIs. Explicit launches use `-noattach`; confirmed close and restart request normal window shutdown without forced termination or automatic capture saving. |
| Daemon and MCP transports | `daemon`, `mcp stdio\|http` | Host | Implements local IPC and JSON-RPC transports outside Fiddler. The C# SDK 2.2.0 supports MCP `2026-07-28` and legacy initialization; see [protocol compatibility](cli.md#mcp-protocol). The daemon can manage the HTTP listener configured in saved settings; the foreground server binds only to loopback. Both reject Origin-bearing requests. |
| Managed HTTP access | `mcp service`, `mcp clients`, `mcp connections`, `config token` | Host | Implements listener configuration, mandatory bearer authentication, named credential hashes, and active transport control outside Fiddler. It does not change the capture proxy. |
| Fiddler management UI | **Fiddler Classic CLI** tab and Tools menu entry | Hybrid | The extension displays controls and version information in Fiddler. The daemon manages persistent HTTP state, credentials, and connections. |
| Bounded session summaries | `sessions list --summary`, `summarize_network_requests` | Hybrid | Aggregates one bounded native metadata page in the host. Counts by host and status, captured body-byte totals, and timings for completed requests cover only the returned records. It does not read headers or bodies. |
| Metadata-only diagnostic files | `doctor --output` | Hybrid | Creates a report from allowlisted local, bridge, and daemon status fields without starting a daemon. The report excludes traffic, credentials, identities, local paths, and raw errors. |

## Native parity rules

### Captured evidence

The bridge uses the live `Session` as the authoritative source for captured evidence. Detail calls preserve Fiddler header order and values. Body and WebSocket payload APIs copy exact byte ranges and never silently decode, decompress, redact, or re-encode captured evidence.

Custom list filters apply only at query time. They do not reproduce or modify the Fiddler Filters tab, whose settings can hide, flag, block, or mutate traffic.

### AutoResponder

Fiddler evaluates every match and action. The bridge preserves the engine's current switches, rule order, enabled state, comments, disable-on-match setting, latency, and imported-response marker. To move rules, the bridge calls Fiddler's promote/demote operations without directly reordering the backing collection.

Opaque rule IDs are valid only for the currently loaded rule objects. Loading or replacing a rule set can assign new IDs without changing Fiddler's rule contents or order.

### Breakpoints

The bridge activates request and response pauses with Fiddler's `x-breakrequest` and `x-breakresponse` flags and observes the native hand-tamper state transitions. It applies mutations to the paused native `Session` and delegates resume or abort operations to Fiddler.

Arms, filter matching, sequence cursors, and automatic hold timeouts are custom safety and automation features. They determine when to activate a native pause but do not replace Fiddler's breakpoint mechanism.

### Replay and composition

The bridge delegates replay to Fiddler's reissue command. To send a request, it validates and constructs the raw request, then calls `FiddlerObject.utilIssueRequest`. The optional `--wait` behavior correlates a later session by baseline ID, method, and URL; this correlation is not a transaction handle supplied by Fiddler.

## Verification

Automated tests verify protocol framing, exact body chunking, filter semantics, pagination, output formats, safety confirmation, CLI parsing, and MCP transports. Opt-in Windows integration tests verify behavior against the installed Fiddler Classic instance:

| Environment variable | Native compatibility coverage |
| --- | --- |
| `FIDDLER_CLASSIC_INTEGRATION=1` | Status, request issue, session evidence, SAZ, replay, and result waiting |
| `FIDDLER_CLASSIC_AUTOMATION_INTEGRATION=1` | AutoResponder settings, native rule priority, FARX restore, request/response breakpoint mutation, resume, and timeout |
| `FIDDLER_CLASSIC_DESTRUCTIVE_INTEGRATION=1` | Native session clearing and SAZ restoration |
| `FIDDLER_CLASSIC_PROXY_INTEGRATION=1` | Native proxy attach/detach with original-state restoration |

The bridge supports Fiddler Classic 5.x and 6.x. The normal build uses reference `6.0.20261.7291`. CI requires metadata-only direct API resolution against `5.0.20253.3311` and `6.0.20261.7291` to pass before release, using SHA-256 to identify the same shipped DLL. An explicitly enabled native run also tests tab/menu registration, startup without tab selection, synthetic session evidence, and unload cleanup. See [CI verification](installation.md#github-actions). Passing metadata checks or unit tests does not establish that the native matrix has run.
