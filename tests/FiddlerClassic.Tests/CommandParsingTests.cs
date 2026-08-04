// Verifies CLI command parsing and stable exit-code mappings.
using FiddlerClassic.Host.Cli;
using FiddlerClassic.Host.Daemon;
using FiddlerClassic.Host.Services;

namespace FiddlerClassic.Tests;

public sealed class CommandParsingTests
{
    [Theory]
    [InlineData("doctor")]
    [InlineData("status --json")]
    [InlineData("capture start")]
    [InlineData("capture stop")]
    [InlineData("sessions list --min-id 1 --max-id 10 --method GET --host example --url path --status 200 --content-type json --process test --limit 10 --oldest-first")]
    [InlineData("sessions list --header-name Authorization --header-value Bearer --min-duration-ms 10 --max-duration-ms 500 --protocol HTTP/1.1 --min-body-bytes 1 --max-body-bytes 4096 --errors-only --body-contains needle --body-direction response --body-search-bytes 4096")]
    [InlineData("sessions watch --after-id 10 --timeout 5 --count 2 --jsonl --host example")]
    [InlineData("sessions show 1")]
    [InlineData("sessions body 1 --direction response --output -")]
    [InlineData("sessions clear --yes")]
    [InlineData("sessions remove --ids 1 2 --yes")]
    [InlineData("sessions save C:\\temp\\capture.saz --ids 1 2 --overwrite --yes")]
    [InlineData("sessions load C:\\temp\\capture.saz")]
    [InlineData("sessions replay 1 --unconditional --wait --timeout 20")]
    [InlineData("sessions export 1 --format curl --output -")]
    [InlineData("sessions export --format har --output C:\\temp\\capture.har --host example")]
    [InlineData("sessions diff 1 2")]
    [InlineData("sessions websocket 1 list --offset 0 --limit 10")]
    [InlineData("sessions websocket 1 get 2 --offset 0 --output -")]
    [InlineData("request send https://example.test/ --method POST --header Content-Type:text/plain --body hello --wait --timeout 20")]
    [InlineData("autoresponder status")]
    [InlineData("autoresponder configure --enable --permit-fallthrough --accept-connects --use-latency")]
    [InlineData("autoresponder rules list")]
    [InlineData("autoresponder rules add EXACT:https://example.test/ *drop --comment test --disable-on-match --latency-ms 50")]
    [InlineData("autoresponder rules update abc123 --match example --action *reset --disable --clear-comment --keep-enabled --latency-ms 0")]
    [InlineData("autoresponder rules move abc123 0")]
    [InlineData("autoresponder rules remove abc123 --yes")]
    [InlineData("autoresponder rules clear --yes")]
    [InlineData("autoresponder rules save C:\\temp\\rules.farx --overwrite --yes")]
    [InlineData("autoresponder rules load C:\\temp\\rules.farx --replace --yes")]
    [InlineData("breakpoints status")]
    [InlineData("breakpoints arms")]
    [InlineData("breakpoints arm request --method GET --host example.test --header-name X-Test --header-value yes --hold 30")]
    [InlineData("breakpoints arm response --status 500 --content-type json --persistent")]
    [InlineData("breakpoints disarm abc123")]
    [InlineData("breakpoints list --stage request --session-id 1")]
    [InlineData("breakpoints wait --after-sequence 1 --timeout 10 --stage response")]
    [InlineData("breakpoints show abc123")]
    [InlineData("breakpoints update abc123 --method POST --url https://example.test/ --set-header X-Test:yes --remove-header X-Old --body hello")]
    [InlineData("breakpoints resume abc123")]
    [InlineData("breakpoints abort abc123 --yes")]
    [InlineData("bridge install")]
    [InlineData("bridge uninstall --yes")]
    [InlineData("daemon start")]
    [InlineData("daemon status --json")]
    [InlineData("daemon stop")]
    [InlineData("mcp stdio")]
    [InlineData("mcp http --port 8878")]
    [InlineData("config token show")]
    [InlineData("config token rotate")]
    public void ParsesApprovedCommands(string commandLine)
    {
        var root = CreateRoot();
        var result = root.Parse(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("sessions list --limit 0")]
    [InlineData("sessions list --limit 1001")]
    [InlineData("sessions body 1 --direction sideways --output -")]
    [InlineData("sessions body 1 --direction response")]
    [InlineData("sessions watch --timeout 0")]
    [InlineData("sessions list --errors-only --successful-only")]
    [InlineData("autoresponder configure")]
    [InlineData("autoresponder configure --enable --disable")]
    [InlineData("autoresponder rules update abc123")]
    [InlineData("autoresponder rules update abc123 --comment value --clear-comment")]
    [InlineData("breakpoints arm request --hold 0")]
    [InlineData("breakpoints update abc123 --body value --body-file body.bin")]
    public void RejectsInvalidCommandInput(string commandLine)
    {
        var result = CreateRoot().Parse(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData(FiddlerClassic.Protocol.ErrorCodes.Unavailable, ExitCodes.Unavailable)]
    [InlineData(FiddlerClassic.Protocol.ErrorCodes.Timeout, ExitCodes.Timeout)]
    [InlineData(FiddlerClassic.Protocol.ErrorCodes.NotFound, ExitCodes.Rejected)]
    [InlineData(FiddlerClassic.Protocol.ErrorCodes.InvalidRequest, ExitCodes.Usage)]
    public void MapsStableBridgeErrorsToExitCodes(string error, int expected)
    {
        Assert.Equal(expected, ExitCodes.ForBridgeError(error));
    }

    /// <summary>
    /// Builds a complete command tree with isolated test dependencies and short daemon timeouts.
    /// </summary>
    private static System.CommandLine.RootCommand CreateRoot()
    {
        var bridge = new TestBridgeClient();
        var environment = new FiddlerEnvironment();
        var config = new ConfigStore(Path.Combine(Path.GetTempPath(), "FiddlerClassicTests", Guid.NewGuid().ToString("N")));
        var actions = new CliActions(
            bridge,
            new BridgeInstaller(environment),
            config,
            environment,
            new StatusService(bridge, environment));
        var daemonClient = new DaemonClient(
            "fiddler-classic.exe",
            DaemonPipeNames.ForCurrentUser(),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        return CommandFactory.Create(actions, daemonClient);
    }
}
