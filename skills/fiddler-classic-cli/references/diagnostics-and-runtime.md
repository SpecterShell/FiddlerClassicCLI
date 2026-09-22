# Diagnostics and runtime commands

Use `$cliPath` resolved by `SKILL.md`. Add `--json` for machine-readable metadata.

## `doctor`

Diagnose problems with the installation, bridge, process, pipe, or configuration. Usage: `& $cliPath doctor [--output <absolute-path>] [--json]`. With `--output`, the command writes a new metadata-only JSON report without starting the daemon. The file excludes traffic, credentials, and local paths. Existing destinations are rejected. Without `--output`, it runs the usual health checks. Example: `& $cliPath doctor --output "C:/Temp/fiddler-diagnostics.json" --json`.

## `status`

Check the current Fiddler, bridge, proxy, and captured-session state. Usage: `& $cliPath status [--json]`. It does not change capture or launch Fiddler. Example: `& $cliPath status --json`.

## `app detect`

Find installed executables and current-user Fiddler processes in this Windows session without starting anything. Usage: `& $cliPath app detect [--path PATH] [--json]`. Searches standard folders and Windows App Paths; `--path` selects an absolute `Fiddler.exe` path with no fallback. Reports versions and 5.x/6.x support. No installation or process found returns exit code 3. Example: `& $cliPath app detect --json`.

## `app open`

Open Fiddler when the user requests it. Usage: `& $cliPath app open [--path PATH] [--json]`. New launches use `-noattach`; an existing supported instance is left unchanged. A different running installation or multiple instances cause a conflict. Use `status` or `doctor` after loading to check bridge readiness. Example: `& $cliPath app open --path "C:/Tools/Fiddler/Fiddler.exe"`.

## `app close`

Close Fiddler normally after the user has saved needed captures and approved closing it. Usage: `& $cliPath app close [--pid PID] [--timeout SECONDS] [--yes] [--json]`. Select a PID when several instances are running. Exit waits default to 10 seconds and accept 1-60. No force-kill or automatic save is performed; resolve native dialogs manually. An already stopped application needs no action. Example: `& $cliPath app close --pid 1234 --yes`.

## `app restart`

Reload Fiddler after an approved bridge update or repair. Usage: `& $cliPath app restart [--pid PID | --path PATH] [--timeout SECONDS] [--yes] [--json]`. Save captures first. Validates the executable before closing, waits for normal exit, then relaunches it with `-noattach`; timeout or cancellation prevents relaunch. Waits default to 10 seconds and accept 1-60. If stopped, starts the selected installation. Example: `& $cliPath app restart --timeout 30 --yes`.

## `bridge install`

Install or update the current-user Fiddler extension when `doctor` reports it missing or outdated. Usage: `& $cliPath bridge install [--json]`. Restart Fiddler Classic afterward so it loads the copied assemblies. Example: `& $cliPath bridge install`.

## `bridge uninstall`

Remove the current-user extension assemblies. Usage: `& $cliPath bridge uninstall [--yes] [--json]`. Obtain explicit approval before removal and use `--yes` only for an approved non-interactive call. Example: `& $cliPath bridge uninstall --yes`.

## `daemon start`

Start the persistent CLI background process before a workflow or after repairing a stale pipe. Usage: `& $cliPath daemon start [--json]`. It does not start Fiddler or attach the system proxy. Example: `& $cliPath daemon start --json`.

## `daemon status`

Check the daemon's process ID, start time, pipe, and host version. Usage: `& $cliPath daemon status [--json]`. Run it before deciding whether a restart is needed. Example: `& $cliPath daemon status --json`.

## `daemon stop`

Stop the CLI background process when its pipe is stale or the user asks to end it. Usage: `& $cliPath daemon stop [--json]`. This does not close Fiddler or detach its proxy. Example: `& $cliPath daemon stop --json`.
