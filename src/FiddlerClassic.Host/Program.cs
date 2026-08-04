// Composes host services and dispatches the requested CLI, daemon, or MCP command.
using FiddlerClassic.Host.Bridge;
using FiddlerClassic.Host.Cli;
using FiddlerClassic.Host.Daemon;
using FiddlerClassic.Host.Services;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("fiddler-classic supports Windows only.");
    return ExitCodes.NotInstalled;
}

var daemonClient = new DaemonClient();
var bridgeClient = new DaemonBridgeClient(daemonClient);
var environment = new FiddlerEnvironment();
var configStore = new ConfigStore();
var actions = new CliActions(
    bridgeClient,
    new BridgeInstaller(environment),
    configStore,
    environment,
    new StatusService(bridgeClient, environment));

return CommandFactory.Create(actions, daemonClient).Parse(args).Invoke();
