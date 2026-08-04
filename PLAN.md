# Fiddler Classic CLI and MCP Server

## Summary

- Create an unofficial Windows-only integration supporting Fiddler Classic 5.x.
- Build both a CLI and MCP server over one shared client, backed by a thin Fiddler add-on. This follows Classic's documented [.NET extension model](https://www.telerik.com/fiddler/fiddler-classic/documentation/extend-fiddler/interfaces) and established [add-on deployment pattern](https://www.telerik.com/fiddler/add-ons).
- Use a bidirectional named-pipe bridge because the bundled [`ExecAction.exe`](https://www.telerik.com/fiddler/fiddler-classic/documentation/knowledge-base/execaction) can submit QuickExec commands but cannot return session data.

## Implementation

- Create `FiddlerClassic.Protocol` for versioned request/response DTOs and length-prefixed JSON framing.
- Create a `net462` `IFiddlerExtension` bridge referencing the locally installed `Fiddler.exe`. It exposes session, proxy, archive, replay, compose, AutoResponder, and breakpoint operations through a current-user-only named pipe.
- Marshal every Fiddler API call onto the UI thread; copy only requested body chunks to avoid blocking or retaining large payloads.
- Create a self-contained .NET 10 `win-x64` host using `System.CommandLine`, `ModelContextProtocol`, and `ModelContextProtocol.AspNetCore`.
- Run bridge-backed CLI operations through an auto-started background daemon over a current-user-only Windows named pipe. Keep MCP stdio and HTTP transports independent.
- Add `bridge install|uninstall` commands targeting `%USERPROFILE%\Documents\Fiddler2\Scripts`, plus `doctor` for installation, version, process, pipe, and configuration checks.
- Publish a portable directory containing the host and bridge artifacts. Do not redistribute `Fiddler.exe` or create an MSI/service.

## Public Interfaces

- Executable: `fiddler-classic`.
- CLI commands: `doctor`, `status`, `capture start|stop`, `sessions list|watch|show|body|clear|remove|save|load|replay|export|diff|websocket`, `request send`, `autoresponder`, `breakpoints`, `bridge install|uninstall`, `daemon start|status|stop`, `mcp stdio|http`, and `config token show|rotate`.
- `sessions list` supports ID bounds, method, host, URL, status, content type, and process filters; default 100 results, maximum 1,000, newest first.
- `sessions show` returns metadata and exact headers but no bodies. `sessions body` requires request/response direction and an explicit output path or `-`, streaming the complete raw payload in chunks.
- Archive commands accept validated absolute `.saz` paths. Existing files are rejected unless `--overwrite` is supplied.
- Clear/uninstall commands prompt interactively and require `--yes` in non-interactive use.
- Replay and compose operations are queued through Fiddler and return acceptance information, with optional bounded waits for a correlated completed transaction.
- AutoResponder commands and MCP tools configure the live engine, manage exact native match/action strings in evaluation order, and import or replace absolute FARX files.
- Breakpoint commands and MCP tools arm bounded request/response filters, list live pauses, apply stage-specific mutations, and resume or abort sessions. Managed pauses automatically resume after a bounded hold timeout.
- MCP tools expose session inspection, archive, replay, compose, AutoResponder, and breakpoint workflows as small safety-annotated operations.
- MCP details return metadata by default and raw headers only when `includeHeaders=true`. Bodies are available solely through `get_network_request_body`, capped at 64 KiB per call with offset, total length, EOF, content type, and text/base64 encoding metadata.
- Support stdio and stateless Streamable HTTP, following the official [C# SDK transports](https://csharp.sdk.modelcontextprotocol.io/concepts/transports/transports.html). HTTP defaults to `http://127.0.0.1:8877/mcp` and requires a generated bearer token stored with current-user permissions.

## Security And Failures

- Preserve captured evidence exactly; do not silently redact or transform headers and bodies. Documentation will warn that explicit output may contain credentials, cookies, and tokens.
- Apply current-user access controls to both named pipes and the HTTP token file; bind HTTP only to loopback and disable CORS.
- Require explicit confirmation for destructive clearing, deletion, archive/FARX replacement, full rule-list replacement, and breakpoint abort. Annotate network-affecting tools as open-world.
- Reject malformed headers, CRLF injection, unsupported URLs, oversized frames, missing sessions, invalid body ranges, invalid FARX paths, stale rule/breakpoint IDs, stage-invalid mutations, and bridge protocol mismatches with stable error codes.
- `status` and `doctor` work while Fiddler is offline; other operations return actionable unavailable, timeout, or version errors.
- Capture start/stop only attaches or detaches Fiddler as the system proxy. The integration never installs or trusts certificates automatically.

## Test Plan

- Unit-test framing limits, serialization round trips, filtering, pagination, body chunking, raw request construction, SAZ/FARX path validation, token handling, CLI parsing, exit codes, MCP annotations, AutoResponder forwarding, and breakpoint input handling.
- Test the CLI daemon lifecycle and relay against a fake named-pipe bridge, and test both MCP transports, including HTTP requests with missing, invalid, and valid bearer tokens.
- Verify stdio writes protocol messages only to stdout and sends diagnostics to stderr.
- Add opt-in Windows integration tests for bridge loading, status, session capture, details, large/binary body transfer, clear, save/load SAZ, replay, compose, AutoResponder, and request/response breakpoints against a local HTTP server.
- Keep system-proxy-changing tests manual or explicitly enabled, preserving and restoring the original proxy state.
- Acceptance requires successful build, automated tests, self-contained publish, bridge installation, and smoke testing against installed Fiddler Classic `5.0.20262.6151`.

## Assumptions

- The project supports Fiddler Classic 5.x and requires Fiddler to be running for operational commands.
- Persistent UI filters, reverse proxy management, certificate trust, Fiddler 4, Windows services, and automatic Fiddler launching are out of scope.
- MCP archive tools may access any absolute `.saz` path permitted to the current Windows user.
- AutoResponder tools may access any absolute `.farx` path and native rule action path permitted to the current Windows user.
