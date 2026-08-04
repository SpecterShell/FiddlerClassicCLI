# Feature Types and Native Compatibility

**English (en-US)** | [Simplified Chinese (zh-CN)](../zh-CN/feature-types.md)

Every public feature is classified by where its behavior comes from. The type describes the implementation and compatibility contract, not whether the feature is available through both CLI and MCP.

## Types

| Type | Meaning |
| --- | --- |
| Native | Delegates the operation to a Fiddler Classic API or UI command. Fiddler owns the resulting behavior and state. |
| Native adapter | Uses live Fiddler objects, events, flags, or engines while adding transport-safe IDs, projections, validation, or bounded payload access. Native side effects and evidence are preserved. |
| Custom | Implements an agent-oriented operation that has no equivalent Fiddler command. Its documented CLI/MCP contract is authoritative and it does not alter similarly named Fiddler UI settings. |
| Hybrid | Reads native Fiddler state through the bridge and completes orchestration or serialization in the external host. |
| Host | Runs outside Fiddler and does not use the Fiddler extension API. |

## Feature Matrix

| Feature | CLI or MCP surface | Type | Compatibility behavior |
| --- | --- | --- | --- |
| Offline and live status | `status`, `get_status` | Hybrid | Installation and process discovery are host-side. Proxy, listener, HTTPS decryption, version, and session counts come from the live Fiddler instance. |
| System proxy capture | `capture start\|stop`, `start_capture`, `stop_capture` | Native | Calls Fiddler's attach or detach command and returns the resulting native proxy state. It does not change certificate trust. |
| Session snapshot and details | `sessions list\|show`, `list_network_requests`, `get_network_request` | Native adapter | Reads Fiddler's current session list and preserves native IDs, states, metadata, and exact header order. |
| Session query filters and pagination | List filters and MCP continuation bounds | Custom | Filters a snapshot without changing Fiddler's Filters tab, visibility rules, or retained sessions. Matching and ID-bound pagination follow this project's documented semantics. |
| Session waiting and watching | `sessions watch`, `wait_for_network_request` | Custom | Observes Fiddler completion events and returns the first completed match after an exclusive session ID. It does not create a Fiddler UI filter. |
| Request and response body access | `sessions body`, `get_network_request_body` | Native adapter | Copies exact bytes from the native `Session` body arrays. Chunking, offsets, and text/base64 metadata are transport behavior only; payload bytes are not decoded or normalized. |
| Session clearing and removal | `sessions clear\|remove`, `clear_network_requests`, `remove_network_requests` | Native | Invokes Fiddler session-list removal commands. Reported counts reflect the sessions Fiddler actually removed. |
| SAZ archives | `sessions save\|load`, `save_network_archive`, `load_network_archive` | Native | Uses Fiddler's SAZ reader and writer. Path validation and overwrite confirmation are bridge safety controls. |
| Request replay | `sessions replay`, `replay_network_request` | Native | Uses Fiddler's reissue operation, including its native unconditional-replay option. Optional result correlation is custom host orchestration. |
| Request composition | `request send`, `send_request` | Native adapter | Validates and builds one HTTP request, then submits it through Fiddler's native request-issue API. It does not claim parity with Composer options such as redirects, automatic authentication, or Scratchpad batches. |
| cURL, raw HTTP, and HAR export | `sessions export` | Hybrid | Reads exact native session evidence through the bridge, then serializes the selected format in the host. These are not Fiddler transcoder plug-ins. |
| Session diff | `sessions diff`, `diff_network_requests` | Custom | Compares metadata, ordered headers, duration, and exact body hashes. It does not invoke Fiddler's visual Compare command or an external diff application. |
| WebSocket inspection | `sessions websocket`, `list_websocket_messages`, `get_websocket_message` | Native adapter | Reads Fiddler's native WebSocket frame collection and exact payload bytes. Pagination and bounded payload chunks are custom transport behavior. |
| AutoResponder settings and execution | `autoresponder status\|configure`, corresponding MCP tools | Native | Reads and changes Fiddler's live AutoResponder engine. Fiddler interprets match and action strings; the bridge does not implement a second rule engine. |
| AutoResponder rule management | Rule list, add, update, move, remove, and clear tools | Native adapter | Uses live Fiddler rule objects. Priority changes use Fiddler's native promote/demote operations so evaluation order and grouping behavior remain Fiddler-owned. Runtime IDs are bridge-only handles. |
| FARX persistence | AutoResponder rule `save` and `load` tools | Native | Uses Fiddler's native FARX save, import, and replacement operations. The bridge adds path validation, rollback, limits, and confirmations. |
| Native breakpoint handling | Breakpoint show, update, resume, and abort tools | Native adapter | Uses Fiddler's request/response hand-tamper states, native session headers and bodies, `ThreadResume`, and native abort behavior. Manual Fiddler breakpoints remain discoverable. |
| Breakpoint arms and waits | Breakpoint arm, disarm, list, wait, and hold timeout tools | Custom | Adds bounded filters, opaque IDs, sequence cursors, one-shot behavior, and automatic resume. A match activates the native breakpoint by setting Fiddler's documented breakpoint flag. |
| Diagnostics and installation | `doctor`, `bridge install\|uninstall` | Host | Discovers files, processes, versions, pipes, and configuration or copies bridge assemblies. The connectivity probe is the only live bridge portion. |
| Daemon, MCP transports, and token management | `daemon`, `mcp`, `config token` | Host | Implements local IPC, JSON-RPC transports, loopback HTTP authentication, and current-user configuration outside Fiddler. |

## Native Parity Rules

### Captured Evidence

The bridge treats the live `Session` as the source of truth. Detail calls preserve Fiddler header order and values. Body and WebSocket payload APIs copy exact byte ranges and never silently decode, decompress, redact, or re-encode captured evidence.

Custom list filters are query-time predicates. They intentionally do not reproduce or modify the Fiddler Filters tab, whose settings can hide, flag, block, or mutate traffic.

### AutoResponder

Fiddler evaluates every match and action. The bridge preserves the engine's current switches, rule order, enabled state, comments, disable-on-match setting, latency, and imported-response marker. Rule moves call the same promote/demote behavior used by Fiddler instead of directly reordering the backing collection.

Opaque rule IDs are valid only for the currently loaded rule objects. Loading or replacing a rule set can assign new IDs without changing Fiddler's rule contents or order.

### Breakpoints

The bridge activates request and response pauses with Fiddler's `x-breakrequest` and `x-breakresponse` flags and observes the native hand-tamper state transitions. Mutations are applied to the paused native `Session`, and resume or abort delegates to Fiddler.

Arms, filter matching, sequence cursors, and automatic hold timeouts are custom safety and automation features. They determine when to activate a native pause but do not replace Fiddler's breakpoint mechanism.

### Replay and Composition

Replay delegates to Fiddler's reissue command. Request sending delegates to `FiddlerObject.utilIssueRequest` after validation and raw request construction. The optional `--wait` behavior correlates a later session by baseline ID, method, and URL; this correlation is not a transaction handle supplied by Fiddler.

## Verification

Automated tests verify protocol framing, exact body chunking, filter semantics, pagination, output formats, safety confirmation, CLI parsing, and MCP transports. Opt-in Windows integration tests verify behavior against the installed Fiddler Classic instance:

| Environment variable | Native compatibility coverage |
| --- | --- |
| `FIDDLER_CLASSIC_INTEGRATION=1` | Status, request issue, session evidence, SAZ, replay, and result waiting |
| `FIDDLER_CLASSIC_AUTOMATION_INTEGRATION=1` | AutoResponder settings, native rule priority, FARX restore, request/response breakpoint mutation, resume, and timeout |
| `FIDDLER_CLASSIC_DESTRUCTIVE_INTEGRATION=1` | Native session clearing and SAZ restoration |
| `FIDDLER_CLASSIC_PROXY_INTEGRATION=1` | Native proxy attach/detach with original-state restoration |

The bridge targets Fiddler Classic 5.x and its native-adapter behavior is tested against `5.0.20262.6151`.
