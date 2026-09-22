# Breakpoint commands

Armed breakpoints match future traffic. Paused sessions remain live Fiddler objects, so check their stage before changing them and resume or abort them promptly.

## `breakpoints status`

Check active arms and currently paused sessions together. Usage: `& $cliPath breakpoints status [--json]`. Start here when the current breakpoint state is unknown. Example: `& $cliPath breakpoints status --json`.

## `breakpoints arms`

List active request and response arms and obtain their IDs. Usage: `& $cliPath breakpoints arms [--json]`. Arms are one-shot unless created with `--persistent`. Example: `& $cliPath breakpoints arms --json`.

## `breakpoints arm`

Pause future matching traffic at the request or response stage. Usage: `& $cliPath breakpoints arm request|response [filters] [--persistent] [--hold 1..300] [--json]`. Status and content type filters apply only to response arms. The default hold is 30 seconds. Example: `& $cliPath breakpoints arm request --method POST --host example.test --hold 45 --json`.

## `breakpoints disarm`

Prevent an arm from matching future sessions. Usage: `& $cliPath breakpoints disarm <arm-id> [--json]`. Disarming does not resume a session that is already paused. Example: `& $cliPath breakpoints disarm 2 --json`.

## `breakpoints list`

List currently paused breakpoints, optionally filtered by stage or session ID. Usage: `& $cliPath breakpoints list [--stage request|response] [--session-id ID] [--json]`. The result can include pauses created manually in Fiddler. Example: `& $cliPath breakpoints list --stage response --json`.

## `breakpoints wait`

Wait for a paused breakpoint whose sequence is greater than the supplied cursor. Usage: `& $cliPath breakpoints wait [--after-sequence N] [--timeout 1..60] [--stage request|response] [--session-id ID] [--json]`. Reuse the latest returned sequence as the next cursor. Example: `& $cliPath breakpoints wait --after-sequence 0 --timeout 30 --json`.

## `breakpoints show`

Inspect the metadata and exact headers at one paused stage. Usage: `& $cliPath breakpoints show <breakpoint-id> [--json]`. Read its body through `sessions body` with the returned session ID. Example: `& $cliPath breakpoints show 5 --json`.

## `breakpoints update`

Replace selected request or response fields while a session is paused. Usage: `& $cliPath breakpoints update <breakpoint-id> [--method value] [--url value] [--status code] [--reason text] [-H "Name: value"] [--remove-header name] [--body text|--body-file path] [--json]`. Request and response fields must match the paused stage. A body option replaces the complete body and is capped at 4 MiB. Example: `& $cliPath breakpoints update 5 --status 201 --reason Created --body '{}' --json`.

## `breakpoints resume`

Continue one paused session after inspection or modification. Usage: `& $cliPath breakpoints resume <breakpoint-id> [--json]`. Resume only the intended breakpoint ID. Example: `& $cliPath breakpoints resume 5 --json`.

## `breakpoints abort`

Terminate one paused session. Usage: `& $cliPath breakpoints abort <breakpoint-id> [--yes] [--json]`. Obtain explicit approval because abort is destructive. Example: `& $cliPath breakpoints abort 5 --yes --json`.
