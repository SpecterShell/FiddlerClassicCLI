// Composes host services and dispatches the requested CLI, daemon, or MCP command.
using System.CommandLine;
using FiddlerClassicCLI.Host.Bridge;
using FiddlerClassicCLI.Host.Cli;
using FiddlerClassicCLI.Host.Daemon;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("fiddler-classic-cli supports Windows only.");
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
    if (!json)
    {
        // Keep usage guidance away from stdout, which may be reserved for exact payload bytes.
        parsed.Invoke(new InvocationConfiguration { Output = Console.Error, Error = Console.Error });
        return ExitCodes.Usage;
    }
    return CliOutput.Error(new BridgeClientException(ErrorCodes.InvalidRequest,
        string.Join(Environment.NewLine, parsed.Errors.Select(error => error.Message))), json);
}
return parsed.Invoke();
