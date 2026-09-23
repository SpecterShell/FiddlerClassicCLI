# AutoResponder commands

Rules use native Fiddler match expressions and action strings. List the rules before changing them because their evaluation order affects behavior.

## `autoresponder status`

Inspect AutoResponder switches and the loaded rules. Usage: `& $cliPath autoresponder status [--json]`. Run it before changing engine behavior. Example: `& $cliPath autoresponder status --json`.

## `autoresponder configure`

Change one or more AutoResponder engine switches. Usage: `& $cliPath autoresponder configure [--enable|--disable] [--permit-fallthrough|--block-unmatched] [--accept-connects|--reject-connects] [--use-latency|--ignore-latency] [--json]`. Specify at least one switch. Example: `& $cliPath autoresponder configure --enable --permit-fallthrough --json`.

## `autoresponder rules list`

Read rules in their native evaluation order and obtain their runtime IDs. Usage: `& $cliPath autoresponder rules list [--json]`. List again after loading a FARX file because IDs can change. Example: `& $cliPath autoresponder rules list --json`.

## `autoresponder rules add`

Append one native match/action rule. Usage: `& $cliPath autoresponder rules add <match> <action> [--disabled] [--comment text] [--disable-on-match] [--latency-ms N] [--json]`. Rule latency applies only when the engine latency switch is enabled. Example: `& $cliPath autoresponder rules add "EXACT:https://example.test/api" "*drop" --comment "test rule" --json`.

## `autoresponder rules update`

Replace selected fields in one rule. Usage: `& $cliPath autoresponder rules update <rule-id> [--match value] [--action value] [--enable|--disable] [--comment text|--clear-comment] [--disable-on-match|--keep-enabled] [--latency-ms N] [--json]`. Specify at least one change. Example: `& $cliPath autoresponder rules update 3 --action "*reset" --comment "temporary" --json`.

## `autoresponder rules move`

Change a rule's position in the evaluation order. Usage: `& $cliPath autoresponder rules move <rule-id> <zero-based-index> [--json]`. List the rules first and preserve the intended native order. Example: `& $cliPath autoresponder rules move 3 0 --json`.

## `autoresponder rules remove`

Remove one rule by its runtime ID. Usage: `& $cliPath autoresponder rules remove <rule-id> [--yes] [--json]`. Obtain explicit approval before removal. Example: `& $cliPath autoresponder rules remove 3 --yes --json`.

## `autoresponder rules clear`

Remove every AutoResponder rule only when the user requests it. Usage: `& $cliPath autoresponder rules clear [--yes] [--json]`. Save a FARX backup first if the user wants to retain the rules. Example: `& $cliPath autoresponder rules clear --yes --json`.

## `autoresponder rules save`

Save all loaded rules to an absolute FARX path. Usage: `& $cliPath autoresponder rules save <absolute.farx> [--overwrite] [--yes] [--json]`. Replacing a file requires `--overwrite` and confirmation. Example: `& $cliPath autoresponder rules save "C:/Captures/rules.farx" --json`.

## `autoresponder rules load`

Import rules from a FARX file or replace the current rule list. Usage: `& $cliPath autoresponder rules load <absolute.farx> [--replace] [--yes] [--json]`. Import adds to the current rules. `--replace` is destructive and requires confirmation. Example: `& $cliPath autoresponder rules load "C:/Captures/rules.farx" --json`.
