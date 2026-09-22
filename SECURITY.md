# Security policy

**English (en-US)** | [简体中文 (zh-CN)](SECURITY.zh-CN.md)

## Sensitive capture data

Fiddler sessions may contain credentials, cookies, tokens, private request bodies, and personal information. This project preserves the requested data exactly, without silent redaction. Protect CLI output, MCP transcripts, SAZ/FARX files, and extracted body files as sensitive evidence.

MCP HTTP is disabled and binds only to loopback by default. Binding the managed listener to IPv4 `0.0.0.0` requires explicit confirmation of a warning. Remote mode uses plain HTTP, so anyone who observes a bearer credential can reuse it. Restrict access along the network path and prefer loopback when remote access is unnecessary.

Session summaries omit headers and bodies but retain hostnames and activity counts. Treat them as sensitive metadata. `doctor --output` writes a diagnostic report limited to numeric versions, known capabilities, listener state, and fixed errors. The report excludes traffic, credentials, client identities, LAN addresses, local paths, and raw exception text. Review reports before sharing them publicly.

## Local trust boundary

- An explicit ACL restricts the Fiddler bridge named pipe to the current Windows user.
- The CLI daemon named pipe uses the runtime's current-user-only pipe restriction.
- Access to HTTP configuration and the default token retained for compatibility is restricted to the current Windows user. Named client tokens are stored only as SHA-256 hashes.
- Every HTTP request requires a bearer credential. Both HTTP modes reject requests containing an `Origin` header with HTTP 403, including requests with valid credentials. No browser origins are authorized. The server does not enable CORS or configure TLS, certificates, or Windows Firewall.
- Bridge installation writes extension assemblies to the current user's Fiddler Scripts directory and records how to launch the host in the current user's local application data.
- Fiddler Classic and its extension run with the privileges of the interactive user.

Any process already running as the same Windows user can normally access that user's files and local IPC endpoints. The bridge provides no sandbox protection against malware running as that user.

The CLI daemon relays the versioned bridge protocol and can run the managed MCP HTTP listener. It tracks only connection metadata needed for operation and never retains request URLs, headers, tokens, bodies, or captured traffic. It does not launch Fiddler, modify proxy settings on startup, or install certificates.

Foreground and managed HTTP listeners reload default and named credentials after configuration changes. Revoked credentials fail subsequent authentication. Deauthorization aborts associated connections in the daemon-managed listener; it cannot undo operations already dispatched or abort foreground connections. Administrative commands cannot fall back to offline operation when the daemon is unresponsive.

MCP client metadata, including `clientInfo` and capabilities, is untrusted protocol data. It does not grant authorization or replace bearer authentication. Modern HTTP requests require matching protocol and method headers, plus a matching tool-name header for tool calls; the SDK rejects inconsistent values before dispatch.

## Traffic mutation

The bridge passes AutoResponder match and action strings to Fiddler unchanged. Native actions can redirect, synthesize, delay, drop, or reset traffic, and can read response files available to the current Windows user. Review actions and paths proposed by an agent before enabling a rule.

Breakpoint mutation changes an in-flight request or response before processing continues. Managed arms are one-shot by default, and their pauses resume automatically after a bounded hold timeout. The bridge imposes no timeout on manual pauses created in Fiddler. Disarming an arm does not resume an already paused session.

## Certificate handling

This project does not install, generate, or trust certificates. HTTPS decryption requires explicit configuration in Fiddler Classic.

## Destructive operations

`app close` and `app restart` require confirmation because normal Fiddler shutdown stops active capture and can discard unsaved sessions. Save needed evidence first. The CLI does not save captures, force-kill processes, or dismiss native dialogs. It targets only verified processes owned by the current user in the current Windows session. Explicit opens and restarts use `-noattach`; restart does not restore captures or the previous capture state. A timed-out or cancelled close can still complete later, so check `app detect` before retrying.

Clearing sessions or rules, deleting a rule, replacing SAZ/FARX files or the complete rule list, and aborting a paused session require explicit confirmation. Managed HTTP also requires confirmation to enable remote access, revoke credentials, disconnect a connection, or disable the service while clients are connected. CLI automation must pass `--yes` where applicable. MCP callers must pass the corresponding confirmation argument.

Revoking a client's credential aborts its associated connections. Disconnecting a connection aborts the selected transport connection. An operation already dispatched may have completed before the abort.

## Reporting

Do not include captured traffic, tokens, SAZ files, or other secrets in a public report. Provide a minimal reproduction with synthetic data.
