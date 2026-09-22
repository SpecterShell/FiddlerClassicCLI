# Capture and inspection commands

Use the narrowest command that answers the question. Read summaries before headers, and read bodies only when needed.

## `capture start`

Attach Fiddler as the Windows system proxy when the user requests capture. Usage: `& $cliPath capture start [--json]`. It does not change certificate trust or HTTPS decryption settings. Example: `& $cliPath capture start --json`.

## `capture stop`

Detach Fiddler from the Windows system proxy when the user asks to stop capture. Usage: `& $cliPath capture stop [--json]`. It preserves certificate and HTTPS settings. Example: `& $cliPath capture stop --json`.

## `sessions list`

Find captured sessions or summarize a bounded page of results. Usage: `& $cliPath sessions list [filters] [--limit 1..1000] [--oldest-first] [--summary] [--json]`. Lists accept metadata, header, and bounded body-prefix filters. `--summary` accepts metadata filters only and reports host/status counts, captured body-byte totals, completed timings, and truncation; totals cover returned records only. Example: `& $cliPath sessions list --host example.test --limit 20 --summary --json`.

## `sessions watch`

Wait for newly completed sessions that match the supplied filters. Usage: `& $cliPath sessions watch [filters] [--after-id ID] [--timeout 1..60] [--count N] [--jsonl]`. Without `--after-id`, watching starts after the current newest session; the timeout is an inactivity timeout. Example: `& $cliPath sessions watch --host example.test --count 1 --timeout 30 --jsonl`.

## `sessions show`

Inspect one session's metadata and exact ordered headers without reading its body. Usage: `& $cliPath sessions show <session-id> [--json]`. Fetch bodies separately to avoid exposing unnecessary evidence. Example: `& $cliPath sessions show 42 --json`.

## `sessions body`

Stream a complete raw request or response body. Usage: `& $cliPath sessions body <session-id> --direction request|response --output <path|->`. The command preserves exact bytes; `--output -` reserves stdout for those bytes. Example: `& $cliPath sessions body 42 --direction response --output "C:/Temp/response.bin"`.

## `sessions diff`

Compare two sessions without printing body content. Usage: `& $cliPath sessions diff <left-session-id> <right-session-id> [--json]`. It compares metadata, ordered headers, duration, and body hashes. Example: `& $cliPath sessions diff 41 42 --json`.

## `sessions websocket <session-id> list`

List WebSocket frame metadata before reading payloads. Usage: `& $cliPath sessions websocket <session-id> list [--offset N] [--limit 1..1000] [--json]`. Use a zero-based message offset for pagination. Example: `& $cliPath sessions websocket 42 list --limit 100 --json`.

## `sessions websocket <session-id> get`

Stream one complete WebSocket frame payload. Usage: `& $cliPath sessions websocket <session-id> get <message-id> [--offset N] --output <path|->`. Use the message ID returned by `list`; `--offset` resumes at a payload byte offset. Example: `& $cliPath sessions websocket 42 get 7 --output "C:/Temp/frame.bin"`.
