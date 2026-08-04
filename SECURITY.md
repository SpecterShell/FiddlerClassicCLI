# Security Policy

**English (en-US)** | [简体中文 (zh-CN)](SECURITY.zh-CN.md)

## Sensitive Capture Data

Fiddler sessions may contain credentials, cookies, tokens, private request bodies, and personal information. This project preserves requested data exactly and does not silently redact it. Protect CLI output, MCP transcripts, SAZ/FARX files, and extracted body files as sensitive evidence.

Do not expose the MCP HTTP endpoint through port forwarding, reverse proxies, wildcard bindings, or firewall rules. The supported endpoint is loopback-only and requires its bearer token.

## Local Trust Boundary

- The Fiddler bridge named pipe is scoped to the current Windows user through an explicit ACL.
- The CLI daemon named pipe uses the runtime's current-user-only pipe restriction.
- The HTTP token and its containing configuration directory are scoped to the current Windows user.
- The HTTP server binds to `127.0.0.1` and does not enable CORS.
- Bridge installation writes only to the current user's Fiddler Scripts directory.
- Fiddler Classic and its extension run with the privileges of the interactive user.

Any process already running as the same Windows user can normally access that user's files and local IPC endpoints. The bridge is not a sandbox against same-user malware.

The CLI daemon only relays the versioned bridge protocol and supports local status and shutdown controls. It does not launch Fiddler, modify proxy settings on startup, or install certificates.

## Traffic Mutation

AutoResponder match and action strings are passed to Fiddler exactly. Native actions can redirect, synthesize, delay, drop, or reset traffic, and can read response files available to the current Windows user. Review agent-proposed actions and paths before enabling a rule.

Breakpoint mutation changes an in-flight request or response before it continues. Managed arms are one-shot by default and automatically resume after a bounded hold timeout. Manual pauses created in Fiddler have no bridge-imposed timeout. Disarming an arm does not resume a session that is already paused.

## Certificate Handling

This project does not install, generate, or trust certificates. HTTPS decryption remains an explicit Fiddler Classic configuration choice.

## Destructive Operations

Clearing sessions or rules, deleting a rule, replacing SAZ/FARX files or the complete rule list, and aborting a paused session require explicit confirmation. CLI automation must pass `--yes` where applicable. MCP callers must pass the corresponding confirmation argument.

## Reporting

Do not include captured traffic, tokens, SAZ files, or other secrets in a public report. Provide a minimal reproduction with synthetic data.
