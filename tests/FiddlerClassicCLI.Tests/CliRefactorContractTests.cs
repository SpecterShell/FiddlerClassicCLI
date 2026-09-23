// Verifies extracted command composition, shared defaults, and filter contracts without invoking handlers.
using System.CommandLine;
using FiddlerClassicCLI.Host.Cli;
using FiddlerClassicCLI.Host.Daemon;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed class CliRefactorContractTests
{
    [Fact]
    public void CommandGroupsPreserveTheirOrderAndHiddenDaemonEntryPoint()
    {
        var root = CreateRoot();
        Assert.Equal(
            ["doctor", "status", "app", "capture", "sessions", "request", "autoresponder", "breakpoints", "bridge", "daemon", "mcp", "config"],
            root.Subcommands.Select(command => command.Name));
        var sessions = root.Subcommands.Single(command => command.Name == "sessions");
        Assert.Equal(
            ["list", "watch", "show", "body", "clear", "remove", "save", "load", "replay", "export", "diff", "websocket"],
            sessions.Subcommands.Select(command => command.Name));
        var websocket = sessions.Subcommands.Single(command => command.Name == "websocket");
        Assert.Equal(["list", "get"], websocket.Subcommands.Select(command => command.Name));
        Assert.Equal("session-id", Assert.Single(websocket.Arguments).Name);
        var mcp = root.Subcommands.Single(command => command.Name == "mcp");
        Assert.Equal(["service", "clients", "connections", "stdio", "http"], mcp.Subcommands.Select(command => command.Name));
        var daemon = root.Subcommands.Single(command => command.Name == "daemon");
        Assert.True(daemon.Subcommands.Single(command => command.Name == "run").Hidden);
    }

    [Theory]
    [InlineData("sessions list -n 10 --json")]
    [InlineData("--json sessions watch --timeout 10 --jsonl")]
    [InlineData("sessions body 1 -d response -o - --json")]
    [InlineData("sessions remove --ids 1 2 -y --json")]
    [InlineData("sessions export 1 -f raw-http -o - --json")]
    [InlineData("sessions websocket 1 list -n 10 --json")]
    [InlineData("sessions websocket 1 get 2 -o - --json")]
    [InlineData("request send https://example.test/ -X POST -H X-Test:value --body value --json")]
    [InlineData("mcp http -p 8878 --json")]
    [InlineData("mcp service configure -p 8878 --json")]
    [InlineData("mcp clients deauthorize test-client -y --json")]
    [InlineData("mcp connections disconnect test-connection -y --json")]
    public void ExtractedCommandsRetainAliasesAndRecursiveJson(string commandLine)
    {
        var root = CreateRoot();
        var result = root.Parse(commandLine.Split(' '));
        Assert.Empty(result.Errors);
        var json = Assert.IsType<Option<bool>>(root.Options.Single(option => option.Name == "--json"));
        Assert.True(json.Recursive);
        Assert.True(result.GetValue(json));
    }

    [Theory]
    [InlineData("sessions list", "--limit", ProtocolConstants.DefaultSessionLimit)]
    [InlineData("sessions export --format har --output capture.har", "--limit", ProtocolConstants.DefaultSessionLimit)]
    [InlineData("sessions websocket 1 list", "--limit", ProtocolConstants.DefaultSessionLimit)]
    [InlineData("sessions watch", "--count", 0)]
    [InlineData("sessions watch", "--timeout", ProtocolConstants.DefaultWaitMilliseconds / 1000)]
    [InlineData("sessions replay 1", "--timeout", ProtocolConstants.DefaultWaitMilliseconds / 1000)]
    [InlineData("request send https://example.test/", "--timeout", ProtocolConstants.DefaultWaitMilliseconds / 1000)]
    public void SharedIntegerDefaultsRemainUnchanged(string commandLine, string optionName, int expected)
    {
        var result = CreateRoot().Parse(commandLine.Split(' '));
        Assert.Empty(result.Errors);
        var option = Assert.IsType<Option<int>>(result.CommandResult.Command.Options.Single(option => option.Name == optionName));
        Assert.Equal(expected, result.GetValue(option));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SharedFiltersMapEveryFieldAndKeepWatchIdBoundsSeparate(bool includeIdBounds)
    {
        var command = new RootCommand();
        var filters = new SessionFilterOptions(includeIdBounds);
        filters.AddTo(command);
        var arguments = "--method POST --host example.test --url /path --status 201 --content-type json --process test-process "
            + "--header-name X-Test --header-value value --min-duration-ms 10 --max-duration-ms 500 --protocol HTTP/1.1 "
            + "--min-body-bytes 1 --max-body-bytes 4096 --errors-only --body-contains needle --body-direction request --body-search-bytes 2048";
        if (includeIdBounds)
        {
            arguments += " --min-id 1 --max-id 10";
        }

        var parsed = command.Parse(arguments.Split(' '));
        Assert.Empty(parsed.Errors);
        var request = filters.CreateRequest(parsed);
        Assert.Equal(includeIdBounds ? 1 : (int?)null, request.MinId);
        Assert.Equal(includeIdBounds ? 10 : (int?)null, request.MaxId);
        Assert.Equal("POST", request.Method);
        Assert.Equal("example.test", request.Host);
        Assert.Equal("/path", request.UrlContains);
        Assert.Equal(201, request.StatusCode);
        Assert.Equal("json", request.ContentType);
        Assert.Equal("test-process", request.Process);
        Assert.Equal("X-Test", request.HeaderName);
        Assert.Equal("value", request.HeaderValue);
        Assert.Equal(10, request.MinDurationMilliseconds);
        Assert.Equal(500, request.MaxDurationMilliseconds);
        Assert.Equal("HTTP/1.1", request.Protocol);
        Assert.Equal(1, request.MinBodyBytes);
        Assert.Equal(4096, request.MaxBodyBytes);
        Assert.True(request.IsError);
        Assert.Equal("needle", request.BodyContains);
        Assert.Equal(BodyDirections.Request, request.BodyDirection);
        Assert.Equal(2048, request.BodySearchBytes);
        Assert.Equal(includeIdBounds, command.Options.Any(option => option.Name == "--min-id"));
        Assert.Equal(includeIdBounds, command.Options.Any(option => option.Name == "--max-id"));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("--errors-only", true)]
    [InlineData("--successful-only", false)]
    public void SharedFiltersPreserveDefaultsAndTriStateErrorSelection(string arguments, bool? expectedError)
    {
        var command = new RootCommand();
        var filters = new SessionFilterOptions();
        filters.AddTo(command);
        var parsed = command.Parse(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Empty(parsed.Errors);
        var request = filters.CreateRequest(parsed);
        Assert.Null(request.MinId);
        Assert.Null(request.MaxId);
        Assert.Null(request.BodyContains);
        Assert.Equal(expectedError, request.IsError);
        Assert.Equal(BodyDirections.Response, request.BodyDirection);
        Assert.Equal(ProtocolConstants.DefaultBodySearchBytes, request.BodySearchBytes);
    }

    [Theory]
    [InlineData("--errors-only --successful-only", "--errors-only and --successful-only cannot be combined.")]
    [InlineData("--min-duration-ms -1", "Duration bounds are invalid.")]
    [InlineData("--min-duration-ms 2 --max-duration-ms 1", "Duration bounds are invalid.")]
    [InlineData("--min-body-bytes -1", "Body-size bounds are invalid.")]
    [InlineData("--min-body-bytes 2 --max-body-bytes 1", "Body-size bounds are invalid.")]
    [InlineData("--body-search-bytes -1", "Body search bytes must be between 1 and 1048576.")]
    [InlineData("--body-search-bytes 1048577", "Body search bytes must be between 1 and 1048576.")]
    public void ListWatchAndExportShareTheSameFilterErrors(string arguments, string expectedError)
    {
        foreach (var command in new[] { "sessions list", "sessions watch", "sessions export --format har --output capture.har" })
        {
            var result = CreateRoot().Parse($"{command} {arguments}".Split(' '));
            Assert.Equal(expectedError, Assert.Single(result.Errors).Message);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void WatchReplayAndCompositionShareTimeoutErrors(int seconds)
    {
        foreach (var command in new[] { "sessions watch", "sessions replay 1", "request send https://example.test/" })
        {
            var result = CreateRoot().Parse($"{command} --timeout {seconds}".Split(' '));
            Assert.Equal("Timeout must be between 1 and 60 seconds.", Assert.Single(result.Errors).Message);
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(65535, true)]
    [InlineData(0, false)]
    [InlineData(65536, false)]
    [InlineData(-1, false)]
    public void HttpTransportsSharePortValidation(int port, bool valid)
    {
        foreach (var command in new[] { "mcp http", "mcp service configure" })
        {
            var result = CreateRoot().Parse($"{command} -p {port}".Split(' '));
            if (valid)
            {
                Assert.Empty(result.Errors);
                var option = Assert.IsType<Option<int?>>(result.CommandResult.Command.Options.Single(option => option.Name == "--port"));
                Assert.Equal(port, result.GetValue(option));
            }
            else
            {
                Assert.Equal("Port must be between 1 and 65535.", Assert.Single(result.Errors).Message);
            }
        }
    }

    /// <summary>
    /// Builds commands with fake traffic access and unique, unused configuration and pipe paths.
    /// </summary>
    /// <remarks>Tests only parse commands. They never invoke handlers or create configuration files.</remarks>
    private static RootCommand CreateRoot()
    {
        var bridge = new TestBridgeClient();
        var environment = new FiddlerEnvironment();
        var config = new ConfigStore(Path.Combine(Path.GetTempPath(), "FiddlerClassicCLITests", Guid.NewGuid().ToString("N")));
        var actions = new CliActions(bridge, new BridgeInstaller(environment), config, environment, new StatusService(bridge, environment));
        var daemon = new DaemonClient(
            "unused.exe",
            $"fiddler-classic-cli.command-tests.{Guid.NewGuid():N}",
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100));
        return CommandFactory.Create(actions, daemon);
    }
}
