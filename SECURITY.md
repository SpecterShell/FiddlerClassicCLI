# Security policy

**English (en-US)** | [简体中文 (zh-CN)](SECURITY.zh-CN.md)

## Sensitive capture data

Fiddler sessions may contain credentials, cookies, tokens, private request bodies, and personal information. This project preserves the requested data exactly, without silent redaction. Protect CLI output, MCP transcripts, SAZ/FARX files, and extracted body files as sensitive evidence.

Managed MCP HTTP is disabled and loopback-only by default, with `non-loopback` authentication. It can also bind IPv4 `0.0.0.0` or up to 16 selected active local IPv4 addresses. Remote enablement requires explicit risk confirmation. Remote mode uses plain HTTP, so anyone who observes a bearer credential can reuse it. Restrict access along the network path and prefer loopback when remote access is unnecessary. Selected bindings retain their exact addresses and fail if an address becomes unavailable, without falling back to a broader binding.

Managed authentication supports `required`, `non-loopback` (the default for fresh or unset configuration), and `none`. `required` checks every request. `non-loopback` skips bearer checks only when both actual remote and local socket IPs are loopback, after normalizing IPv4-mapped addresses. A request to a LAN address requires a bearer credential even when sent locally. Unknown socket addresses require authentication. `Host`, `Forwarded`, and `X-Forwarded-For` never grant the exemption. Saving or enabling `none` requires confirmation: every reachable client then has full MCP access, including captured traffic and mutation tools. Local processes, including processes under other Windows accounts, can reach anonymous loopback MCP and receive the same full access under the default `non-loopback` exemption. The named pipes remain restricted to the current Windows user. Local relays or reverse proxies that connect through loopback appear as loopback peers. Choose `required` if loopback callers or relays must authenticate. Tool confirmation arguments still apply, and any connected client can supply them. Saved credentials do not restrict anonymous access. Foreground `mcp http` always requires authentication and remains loopback-only.

Disable the managed service before changing its bindings, port, or authentication. Startup policy applies when the daemon starts and when Fiddler loads the extension. The default `last-state` preserves the saved desired state. The `enabled` and `disabled` policies override it at startup. Saving a policy that automatically enables remote access or mode `none` requires confirmation. Saving or enabling loopback-only `non-loopback` needs no access-risk confirmation. Existing explicit legacy config-file `HttpRequireAuthentication` values load as `required` for `true` and `none` for `false`.

Session summaries include hostnames and activity counts. They omit headers and bodies. Treat them as sensitive metadata. `doctor --output` writes a diagnostic report limited to numeric versions, known capabilities, listener state, and fixed errors. The report excludes traffic, credentials, client identities, LAN addresses, local paths, and raw exception text. Review reports before sharing them publicly.

## Local trust boundary

- An explicit ACL restricts the Fiddler bridge named pipe to the current Windows user.
- The CLI daemon named pipe uses the runtime's current-user-only pipe restriction.
- Access to HTTP configuration and the default token retained for compatibility is restricted to the current Windows user. Named client tokens are stored only as SHA-256 hashes.
- HTTP requests require a bearer credential in `required` mode and for requests that do not qualify for the `non-loopback` exemption. Mode `none` skips bearer checks for all clients. Both foreground and managed HTTP servers reject requests containing an `Origin` header with HTTP 403 in every authentication mode, including requests with valid credentials. Anonymous requests also require a Host header matching the receiving socket's actual local IP address and port, or `localhost` on loopback, to prevent DNS rebinding. Custom DNS names are rejected for anonymous requests. The server does not enable CORS or configure TLS, certificates, or Windows Firewall.
- `scripts/install.ps1` writes to `%LOCALAPPDATA%\Programs\FiddlerClassicCLI` by default, updates the current-user `PATH`, deploys embedded bridge assemblies to the user's Fiddler Scripts directory, and records the installed host path. `-NoPathUpdate` and `-SkipBridge` omit the corresponding changes.
- Fiddler Classic and its extension run with the privileges of the interactive user.

The bootstrap downloads `fiddler-classic-cli.exe` from the selected GitHub repository and requires a valid SHA-256 asset digest from GitHub. It verifies the downloaded bytes before execution and stops if the digest is missing or does not match. This check depends on the selected repository and GitHub metadata being trusted. Explicit local EXEs and directories are trusted inputs. Explicit local ZIPs require an adjacent `SHA256SUMS` with a matching entry for the exact filename. Run scripts and releases only from sources you trust. Updates replace the CLI at its fixed path and deploy the bridge unless skipped. Installation does not start Fiddler, the daemon, or MCP servers, install Agent Skills, or change certificate trust and HTTPS decryption.

Any process already running as the same Windows user can normally access that user's files and local IPC endpoints. The bridge provides no sandbox protection against malware running as that user.

The CLI daemon relays the versioned bridge protocol and can run the managed MCP HTTP listener. It tracks only connection metadata needed for operation and never retains request URLs, headers, tokens, bodies, or captured traffic. It does not launch Fiddler, modify proxy settings on startup, or install certificates.

Authenticated HTTP listeners reload default and named credentials after configuration changes. Revoked credentials fail subsequent authentication. Deauthorization aborts associated connections in the daemon-managed listener. It cannot restrict anonymous access, undo operations already dispatched, or abort foreground connections. Anonymous connection records contain no token-client identity. Administrative commands cannot fall back to offline operation when the daemon is unresponsive.

MCP client metadata, including `clientInfo` and capabilities, is untrusted protocol data. It does not grant authorization or replace bearer authentication. Modern HTTP requests require matching protocol and method headers, plus a matching tool-name header for tool calls. The SDK rejects inconsistent values before dispatch.

## Proxy attachment and reachability

`capture start` and `capture stop` attach or detach Fiddler as the host Windows system proxy. Detachment leaves a running Fiddler proxy listener available to explicitly routed traffic. Local clients can use loopback. Remote clients require Fiddler configured for remote access on a reachable interface or all interfaces, with routing and firewall rules that allow the connection. IPv4 `0.0.0.0` is an all-interface bind address. Clients use a concrete loopback or host address and the proxy port.

Fiddler proxy access is configured separately from MCP HTTP binding and authentication. Capture commands preserve certificate trust and HTTPS-decryption settings. The CLI does not add network routes or firewall rules. Stopping system-proxy capture does not block clients that explicitly use the Fiddler listener.

## Traffic mutation

The bridge passes AutoResponder match and action strings to Fiddler unchanged. Native actions can redirect, synthesize, delay, drop, or reset traffic, and can read response files available to the current Windows user. Review actions and paths proposed by an agent before enabling a rule.

Breakpoint mutation changes an in-flight request or response before processing continues. Managed arms are one-shot by default, and their pauses resume automatically after a bounded hold timeout. The bridge imposes no timeout on manual pauses created in Fiddler. Disarming an arm does not resume an already paused session.

## Certificate handling

This project does not install, generate, or trust certificates. HTTPS decryption requires explicit configuration in Fiddler Classic.

## Destructive operations

`app close` and `app restart` require confirmation because normal Fiddler shutdown stops active capture and can discard unsaved sessions. Save needed evidence first. The CLI does not save captures, force-kill processes, or dismiss native dialogs. It targets only verified processes owned by the current user in the current Windows session. Explicit opens and restarts use `-noattach`. Restart does not restore captures or the previous capture state. A timed-out or cancelled close can still complete later, so check `app detect` before retrying.

Clearing sessions or rules, deleting a rule, replacing SAZ/FARX files or the complete rule list, and aborting a paused session require explicit confirmation. Managed HTTP also requires confirmation to enable remote access or mode `none`, save mode `none`, save settings that permit such access at startup, revoke credentials, disconnect a connection, or disable the service while clients are connected. CLI automation must pass `--yes` where applicable. MCP callers must pass the corresponding confirmation argument.

Revoking a client's credential aborts its associated connections. Disconnecting a connection aborts the selected transport connection. An operation already dispatched may have completed before the abort.

## Reporting

Do not include captured traffic, tokens, SAZ files, or other secrets in a public report. Provide a minimal reproduction with synthetic data.
