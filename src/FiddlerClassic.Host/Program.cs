// Composes host services and dispatches the requested CLI, daemon, or MCP command.
using FiddlerClassic.Host.Bridge;
using FiddlerClassic.Host.Cli;
using FiddlerClassic.Host.Daemon;
using FiddlerClassic.Host.Services;
using FiddlerClassic.Protocol;

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

var root = CommandFactory.Create(actions, daemonClient);
var parsed = root.Parse(args);
if (parsed.Errors.Count > 0)
{
    // Parser failures follow the same stderr and exit-code contract as handler validation.
    // A missing option value can prevent later tokens from becoming parsed option results.
    var json = args.Contains("--json", StringComparer.Ordinal);
    return CliOutput.Error(new BridgeClientException(ErrorCodes.InvalidRequest,
        string.Join(Environment.NewLine, parsed.Errors.Select(error => error.Message))), json);
}
return parsed.Invoke();
