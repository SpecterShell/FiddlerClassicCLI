# Fiddler Classic CLI Contributor Guide

This file applies to the entire repository. It records the engineering rules that
must remain true while building the CLI, MCP server, protocol, and Fiddler Classic
extension.

## Project Scope

- Support Windows x64 and Fiddler Classic 5.x and 6.x. Do not introduce claims of support
  for Fiddler Everywhere, Fiddler Classic 4.x, or non-Windows capture engines.
- Keep Fiddler Classic as the capture and traffic-mutation engine. The extension is
  a thin automation bridge; the host provides CLI, MCP, daemon, validation, and
  serialization behavior.
- Do not launch Fiddler automatically, install a service or MSI, or install,
  generate, or trust certificates.
- Only explicit `app open` or `app restart` commands may launch Fiddler, using
  `-noattach`. Close and restart require confirmation and normal window shutdown.
  Verify the process owner and Windows session; never force-kill or dismiss native dialogs.
- Do not redistribute `Fiddler.exe` or any Telerik binaries.
- The code, tests, public docs, and this guide define the current implementation.

## Architecture Boundaries

### `FiddlerClassic.Protocol`

- Keep this project free of Fiddler references and host-specific dependencies.
- It targets both `net462` and `net10.0`; changes must compile on both targets.
- Put versioned wire DTOs, operation names, error codes, framing, pipe names, and
  shared input validation here.
- Use length-prefixed JSON frames and enforce frame-size limits before allocating
  or deserializing payloads.
- Never put live Fiddler objects, framework-specific exceptions, streams, or
  unbounded payloads in protocol DTOs.

### `FiddlerClassic.Bridge`

- Keep the bridge on `net462` and compatible with the installed Fiddler Classic
  5.x and 6.x extension model.
- This is the only project that may reference `Fiddler.exe` or directly use
  Fiddler APIs and objects.
- Marshal every read or mutation of Fiddler UI state, sessions, AutoResponder
  rules, and breakpoint state through `FiddlerThread.Invoke`.
- Snapshot transport-safe values while on the UI thread. Do not retain or expose
  mutable native objects after leaving it.
- Keep pipe work off the UI thread. Pipe servers must remain current-user-only and
  must stop cleanly when the extension unloads.
- Copy only the body or WebSocket byte range requested by the caller. Avoid
  retaining complete payloads unless a bounded operation explicitly replaces or
  composes one.
- Reflection against Fiddler internals is a last resort. Isolate it, fail with an
  actionable stable error when the expected member is unavailable, and cover its
  behavior with an opt-in integration test.

### `FiddlerClassic.Host`

- Keep the host on `net10.0`. It owns the executable, CLI, daemon, MCP transports,
  installation, configuration, authentication, and host-side export logic.
- Access Fiddler traffic and automation APIs only through `IBridgeClient`; do not
  add Fiddler assembly references to the host. Explicit application lifecycle
  commands use Windows process APIs and must remain independent of bridge IPC.
- Reuse shared client and service behavior across CLI and MCP surfaces. Avoid two
  implementations of the same operation.
- Preserve cancellation and timeout behavior through every IPC and network layer.
- Keep daemon and bridge pipe names deterministic per Windows user.

## Native Behavior and Feature Types

- Classify every public capability as `Native`, `Native adapter`, `Custom`,
  `Hybrid`, or `Host` in `docs/en-US/feature-types.md` and its zh-CN translation.
- For `Native` features, call the Fiddler API or UI command that owns the behavior.
- For `Native adapter` features, preserve Fiddler side effects and evidence while
  adding only bounded transport access, IDs, projections, or validation.
- Do not reimplement a Fiddler rule engine, breakpoint engine, replay algorithm,
  archive format, or proxy behavior in the host or bridge.
- When Fiddler has no equivalent operation, implement a clearly named `Custom` or
  `Host` feature and document its exact semantics. Do not imply that it changes a
  similarly named Fiddler UI setting.
- Preserve native ordering and state. Examples include ordered headers,
  AutoResponder evaluation order, hand-tamper states, and actual session removal
  counts.
- Treat replay and composition as queued Fiddler operations. Optional result
  correlation is host orchestration, not a native transaction handle.
- When changing behavior based on Fiddler objects, compare it with the equivalent
  Fiddler UI operation and add a live compatibility test where practical.

## Protocol and Public Surface Changes

- Update protocol DTOs, constants, dispatcher handling, bridge client behavior,
  CLI commands, MCP tools, tests, and documentation together.
- The project is pre-release. When an interface changes, update every caller and
  remove superseded aliases, fields, commands, and migration text in the same
  change.
- Increment the bridge or daemon protocol version when peers can no longer safely
  understand one another. Return `protocol_mismatch` instead of guessing.
- Use stable error codes from `ErrorCodes`. Map expected failures to documented CLI
  exit codes and structured MCP errors; do not expose implementation exceptions as
  contracts.
- Validate unsupported URLs, malformed headers, CRLF injection, missing sessions,
  invalid offsets, count limits, oversized frames, and path constraints before an
  operation reaches Fiddler.
- Preserve header order and duplicate header values. Do not replace ordered header
  collections with dictionaries.
- Return metadata separately from payloads. MCP payload reads are bounded to 64
  KiB per call and report offset, bytes returned, total length, EOF, content type,
  and text/base64 encoding metadata.
- CLI body and WebSocket output must stream exact bytes in chunks to an explicit
  path or `-`; do not embed complete bodies in routine JSON metadata.

## Security and Evidence Integrity

- Captured traffic is evidence. Never silently redact, decompress, decode,
  normalize, reorder, or re-encode requested headers, bodies, or WebSocket bytes.
- Do not write captured credentials, cookies, tokens, bodies, bearer tokens, SAZ
  contents, or FARX contents to diagnostics or test logs.
- Scope named pipes, token files, configuration directories, and bridge deployment
  to the current Windows user.
- Keep managed MCP HTTP disabled and bound to `127.0.0.1` by default. Permit IPv4
  `0.0.0.0` only after explicit plaintext-credential confirmation. Require bearer
  authentication and do not enable CORS or configure TLS or firewall rules.
- Keep destructive operations explicitly confirmed. Non-interactive CLI callers
  use `--yes`; MCP callers use the corresponding confirmation argument.
- Mark MCP tools accurately as read-only, destructive, idempotent, and open-world.
  Add or update annotation tests whenever a tool changes.
- Validate archive and rule-set paths as absolute `.saz` or `.farx` paths. Reject
  existing destinations unless overwrite and confirmation requirements are met.
- `capture start` and `capture stop` may only attach or detach Fiddler as the system
  proxy. Preserve certificate and HTTPS-decryption settings.

## CLI and MCP Contracts

- Keep human-readable CLI output useful, and provide stable `--json` or JSONL
  output where documented for automation.
- Reserve stdout for the declared data stream. `mcp stdio` writes JSON-RPC only to
  stdout, and `--output -` writes payload bytes only to stdout. Send diagnostics
  and progress to stderr in both cases.
- `doctor` and offline portions of `status` must work when Fiddler is not running.
  Operational commands must return actionable unavailable, timeout, or version
  errors.
- Keep MCP inspection composable: list summaries, fetch one detail record, then
  request bounded payload chunks only when needed.
- Use request-ID bounds for mutable-session pagination. Do not replace them with
  page indexes that can duplicate or skip sessions as traffic arrives.
- Require explicit filters and bounds for expensive body searches and large result
  sets.
- Keep CLI and MCP semantics aligned unless their transport boundaries require a
  documented difference.

## Code and Comment Style

- Follow the repository settings: nullable reference types, implicit usings,
  latest C# language version, deterministic builds, and warnings as errors.
- Use centrally managed package versions in `Directory.Packages.props`.
- Start every `.cs` file with a concise comment explaining its responsibility.
- Add XML documentation to medium-to-large methods and constructors. Document each
  parameter and any non-obvious return, exception, threading, or ownership rule.
- Write comments that explain constraints and intent, especially around Fiddler
  threading, evidence preservation, native delegation, and security boundaries.
- Keep code, identifiers, comments, scripts, skills, and `AGENTS.md` in English.
- Default to ASCII in code and scripts. Unicode is appropriate in localized
  documentation and test data that specifically exercises encoding.
- Prefer existing repository patterns and focused edits. Add an abstraction only
  when it removes meaningful duplication or protects an ownership boundary.
- Do not mix unrelated refactors, formatting churn, or generated-file changes into
  a feature or fix.

## Documentation and Localization

- Maintain English and Simplified Chinese versions of README and documentation
  content. Keep their structure, commands, limits, and safety statements aligned.
- Do not translate skills, code, comments, command names, MCP tool names, protocol
  fields, or examples whose literal spelling matters.
- Document every new public CLI command, option, MCP tool, limit, confirmation,
  output boundary, error behavior, and feature type.
- Update `README.md`, `README.zh-CN.md`, `docs/en-US`, and `docs/zh-CN` in the same
  change when public behavior changes.
- Do not publish claims that are supported only by a plan. Verify behavior in code
  and tests first.

## Testing Rules

- Add focused unit tests for protocol framing, serialization, validation,
  filtering, pagination, chunking, output formats, CLI parsing, exit codes, MCP
  annotations, authentication, and error mapping as affected by a change.
- Exercise bridge-independent CLI and both MCP transports against fake named-pipe
  peers. Verify that stdio diagnostics never contaminate stdout.
- Add bridge tests for dispatch, framing failures, protocol mismatch, and pipe
  lifecycle behavior without requiring a live Fiddler process where possible.
- Use synthetic loopback traffic in integration tests. Never rely on external
  services or real credentials.
- Live tests are opt-in and must preserve and restore the affected Fiddler state in
  `finally` cleanup, including on assertion failure:

| Variable | Scope |
| --- | --- |
| `FIDDLER_CLASSIC_INTEGRATION=1` | Status, compose, session evidence, SAZ, replay, and result waiting |
| `FIDDLER_CLASSIC_AUTOMATION_INTEGRATION=1` | AutoResponder and breakpoint mutation with state restoration |
| `FIDDLER_CLASSIC_DESTRUCTIVE_INTEGRATION=1` | Session clearing and SAZ restoration |
| `FIDDLER_CLASSIC_PROXY_INTEGRATION=1` | Proxy attach/detach with original-state restoration |

- Never enable destructive, automation, or proxy integration tests implicitly.
- Scale test coverage with the blast radius. Protocol and shared-client changes
  require broader CLI, MCP, and bridge verification than isolated formatting or
  documentation edits.

## Build and Release

- Use the SDK pinned in `global.json` and the locally installed Fiddler Classic
  reference at `%LOCALAPPDATA%\Programs\Fiddler\Fiddler.exe` by default.
- Pass `-p:FiddlerInstallDir="C:\path\to\Fiddler"` when the reference is installed
  elsewhere.
- Use these repository entry points:

```powershell
dotnet build ./FiddlerClassicCLI.slnx
dotnet test ./FiddlerClassicCLI.slnx
./scripts/build.ps1
./scripts/package.ps1
```

- `scripts/build.ps1` is the normal release verification path: it tests the
  solution and publishes the self-contained `win-x64` host.
- A release package must contain the host, bridge and protocol artifacts, docs,
  license, install script, and Agent Skill, but never `Fiddler.exe`.
- Keep release archives reproducible enough to verify through `SHA256SUMS` and
  `scripts/verify-release.ps1`.
- GitHub Actions builds on `windows-2025`. Install the Fiddler compile reference
  with WinGet first and Chocolatey only as the fallback.
- Pin third-party GitHub Actions to full commit SHAs.

## Repository Hygiene and Definition of Done

- Inspect `git status` before editing and before reporting completion. Preserve
  user changes and never revert unrelated work.
- Do not commit `artifacts`, `bin`, `obj`, local tokens, logs, capture exports, SAZ
  archives, HAR files, or extracted request/response bodies.
- Do not commit or publish secrets or captured production traffic, including in
  fixtures, snapshots, issue text, or examples.
- A change is complete only when its implementation, affected CLI and MCP
  surfaces, feature classification, English and zh-CN docs, tests, and packaging
  consequences have all been considered.
- Report the exact verification performed. If a live Fiddler or state-changing
  test was not run, say so explicitly.
