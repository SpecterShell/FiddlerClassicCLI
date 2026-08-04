# CLI and MCP Design

**English (en-US)** | [简体中文 (zh-CN)](../zh-CN/design.md)

This integration borrows the agent-facing principles and list/detail tool pattern from [Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp), while preserving Fiddler Classic's terminology and process-wide capture model.

## Feature Classification

Features are classified as Native, Native adapter, Custom, Hybrid, or Host according to the source of their behavior. The [feature type and native compatibility matrix](feature-types.md) defines the parity contract for every CLI and MCP area. Custom query, wait, diff, and transport operations are specified independently and do not claim to reproduce similarly named Fiddler UI tabs.

## Inspection Workflow

Use the smallest tool that answers the current question:

1. Call `list_network_requests` to filter summaries and obtain request IDs.
2. Call `get_network_request` for one transaction. Headers remain opt-in through `includeHeaders=true`.
3. Call `get_network_request_body` only when payload bytes are needed. Read at most 64 KiB and continue with `offset + bytesReturned` until `eof=true`.
4. Call `wait_for_network_request` when an agent needs the next completed match instead of polling the full list.

The CLI exposes the same progression as `sessions list`, `sessions show`, and `sessions body`. Human-readable output is the default; `--json` provides stable structured output for scripts. Complete CLI bodies go to an explicit file or stdout instead of being embedded in a metadata result.

Bridge-backed CLI commands use an auto-started background daemon. Each invocation sends one framed request over a current-user-only Windows named pipe, and the daemon relays the existing versioned bridge protocol to Fiddler. The daemon does not own capture state; Fiddler remains the source of truth.

Completed-session notifications feed a bounded in-process ID buffer. Wait operations sleep outside Fiddler's UI thread, then marshal only the matching snapshot back to the UI thread. Request composition and replay return a baseline session ID plus expected method and URL; optional wait behavior uses those hints to locate the first matching completed session.

## AutoResponder Model

The bridge works with Fiddler's live `AutoResponder` instance on the UI thread. It assigns opaque IDs to loaded rule objects, exposes their exact match/action text, and changes priority through Fiddler's native promote/demote operations. Import uses Fiddler's FARX importer; replacement uses its rule loader. FARX bytes stay on disk and are represented only by validated absolute paths.

The bridge does not interpret native actions. This preserves Fiddler behavior and avoids a second, divergent rule engine. It also means mutation tools are open-world operations: an action may affect network traffic or read a local response file using the current user's access.

## Breakpoint Model

An arm is a bounded in-memory filter for future request or response headers. On a match, the extension sets Fiddler's `x-breakrequest` or `x-breakresponse` session flag and tracks the transition into its hand-tamper state. One-shot arms are removed as soon as they match.

Pending breakpoint IDs identify live pauses. A monotonically increasing sequence supports wait cursors even when the same Fiddler session pauses once for its request and later for its response. Managed pauses have a 1-300 second hold and call `ThreadResume` when it expires. Pauses created manually in Fiddler are discoverable and controllable but have no bridge-imposed timeout.

Mutation is allowed only while the session remains in the expected hand-tamper state. Request pauses accept method, URL, headers, and body; response pauses accept status, reason, headers, and body. Body replacement is bounded to 4 MiB. Resume and abort remove the pending handle before changing Fiddler state so concurrent callers cannot act twice.

## Pagination

Fiddler captures can change while an agent is inspecting them. `list_network_requests` therefore uses inclusive request-ID bounds instead of page indexes. When more filtered matches exist, the response includes one continuation value:

- Newest-first results return `nextMaxRequestId`; pass it back as `maxRequestId` with the same filters.
- Oldest-first results return `nextMinRequestId`; pass it back as `minRequestId` with the same filters.

This avoids duplicates caused by newly arriving sessions shifting numeric pages.

## Output Boundaries

- Lists contain metadata, never headers or bodies.
- Details contain exact ordered headers only when requested, never bodies.
- MCP body results are bounded chunks with explicit encoding metadata.
- WebSocket lists contain frame metadata; payloads use a separate 64 KiB MCP chunk API.
- CLI body output preserves the complete raw payload without normalization.
- Large archives are represented by absolute `.saz` paths rather than inline data.
- FARX rule sets are represented by absolute `.farx` paths rather than inline data.
- Breakpoint details contain metadata and opt-in headers; existing bounded body tools read paused payloads.
- cURL, raw HTTP, HAR, and diff are explicit sensitive-evidence operations. Diff returns body hashes rather than body bytes.

These boundaries keep routine calls token-efficient without changing captured evidence.

## Safety Model

Tool annotations distinguish read-only inspection, destructive deletion or in-flight mutation, and open-world network operations. Clearing, selective removal, archive/FARX replacement, rule deletion, and session abort require explicit confirmation where applicable. Captured headers and bodies are never silently redacted, so callers must treat explicit evidence output as sensitive.

The host connects to the user's running Fiddler Classic instance. It does not launch Fiddler automatically, attach the system proxy without a command, or install and trust certificates.

## Intentional Differences

Chrome DevTools MCP uses page-scoped `list_network_requests` and `get_network_request` tools. This project uses the same request-centric MCP vocabulary, but queries Fiddler Classic's process-wide session list. A separate `get_network_request_body` tool prevents an innocent metadata call from returning large or sensitive payloads. The human CLI retains `sessions` because that matches Fiddler Classic's UI terminology.
