# Session action commands

These commands change Fiddler state, create sensitive files, or generate traffic. Confirm the requested scope before running them.

## `sessions clear`

Permanently remove every captured session, but only when the user requests it. Usage: `& $cliPath sessions clear [--yes] [--json]`. Prefer selective removal when IDs are known. Example: `& $cliPath sessions clear --yes --json`.

## `sessions remove`

Permanently remove selected sessions and leave the rest unchanged. Usage: `& $cliPath sessions remove --ids <id...> [--yes] [--json]`. The operation fails if any requested ID is missing. Example: `& $cliPath sessions remove --ids 41 42 --yes --json`.

## `sessions save`

Save all or selected sessions to a SAZ archive. Usage: `& $cliPath sessions save <absolute.saz> [--ids <id...>] [--overwrite] [--yes] [--json]`. Replacing an existing archive requires both `--overwrite` and confirmation. Example: `& $cliPath sessions save "C:/Captures/sample.saz" --ids 41 42 --json`.

## `sessions load`

Load sessions from an existing SAZ archive into Fiddler. Usage: `& $cliPath sessions load <absolute.saz> [--json]`. Treat the archive as sensitive evidence. Example: `& $cliPath sessions load "C:/Captures/sample.saz" --json`.

## `sessions replay`

Queue a captured session for replay through Fiddler. Usage: `& $cliPath sessions replay <session-id> [--unconditional] [--wait] [--timeout 1..60] [--json]`. `--unconditional` removes conditional request headers; a wait timeout does not cancel an accepted replay. Example: `& $cliPath sessions replay 42 --wait --timeout 30 --json`.

## `sessions export`

Export one request as cURL or raw HTTP, or a filtered set of sessions as HAR. Usage: `& $cliPath sessions export [session-id] --format curl|raw-http|har --output <path|-> [filters] [--overwrite] [--yes]`. cURL and raw HTTP require an ID; HAR omits it. Example: `& $cliPath sessions export --format har --host example.test --output "C:/Captures/example.har"`.

## `request send`

Compose and send an HTTP or HTTPS request through Fiddler. Usage: `& $cliPath request send <url> [-X METHOD] [-H "Name: value"] [--body text|--body-file path] [--wait] [--timeout 1..60] [--json]`. Repeat `-H` for ordered headers; body files are capped at 4 MiB. Example: `& $cliPath request send "https://example.test/api" -X POST -H "Content-Type: application/json" --body '{}' --wait --json`.
