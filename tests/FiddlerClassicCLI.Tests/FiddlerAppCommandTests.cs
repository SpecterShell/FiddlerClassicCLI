// Tests app command parsing, JSON boundaries, and lifecycle invocation using only inert process leases.
using System.CommandLine;
using System.Text.Json;
using FiddlerClassicCLI.Host.Cli;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed class FiddlerAppCommandTests
{
    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 3)]
    public async Task DetectReportsInstallationAndProcessDataWithoutMutation(bool installed, bool running, int exitCode)
    {
        var platform = new FakeFiddlerAppPlatform();
        if (!installed) platform.Installations.Clear();
        if (running) platform.Processes.Add(new());

        var result = await InvokeAsync(platform, "app", "detect", "--json");

        Assert.Equal(exitCode, result.Code);
        Assert.Empty(result.Error);
        using var document = JsonDocument.Parse(result.Output);
        var installations = document.RootElement.GetProperty("installations").EnumerateArray().ToArray();
        var processes = document.RootElement.GetProperty("processes").EnumerateArray().ToArray();
        Assert.Equal(installed ? 1 : 0, installations.Length);
        Assert.Equal(running ? 1 : 0, processes.Length);
        foreach (var item in installations.Concat(processes))
        {
            Assert.Equal(FakeFiddlerAppPlatform.DefaultPath, item.GetProperty("executablePath").GetString());
            Assert.Equal("6.0.20261.7291", item.GetProperty("version").GetString());
            Assert.True(item.GetProperty("supported").GetBoolean());
        }
        if (running)
        {
            Assert.Equal(42, Assert.Single(processes).GetProperty("processId").GetInt32());
            Assert.True(Assert.Single(platform.Processes).Disposed);
        }
        Assert.Equal(0, platform.Acquisitions);
        AssertNoMutation(platform);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenUsesExplicitPathOrReportsAnExistingProcess(bool alreadyRunning)
    {
        const string selected = @"D:\Selected Fiddler\Fiddler.exe";
        var platform = new FakeFiddlerAppPlatform();
        platform.Installations.Add(new(selected, "5.0.0.0", true));
        if (alreadyRunning) platform.Processes.Add(new(path: selected));

        var result = await InvokeAsync(platform, "--json", "app", "open", "--path", selected);

        AssertResult(result, new("open", !alreadyRunning, true, alreadyRunning ? 42 : 1234, selected));
        Assert.Equal(1, platform.Acquisitions);
        Assert.False(platform.Locked);
        if (alreadyRunning)
        {
            AssertNoMutation(platform);
            Assert.True(Assert.Single(platform.Processes).Disposed);
        }
        else
        {
            Assert.Equal(1, platform.Starts);
            Assert.Equal(selected, platform.StartedPath);
            Assert.Equal(new[] { "start" }, platform.Events);
        }
    }

    [Theory]
    [InlineData("close")]
    [InlineData("restart")]
    public async Task ConfirmedCloseAndRestartSelectThePidAndWaitForExit(string command)
    {
        var platform = new FakeFiddlerAppPlatform();
        var untouched = new FakeFiddlerAppProcess(7);
        var selected = new FakeFiddlerAppProcess
        {
            OnClose = () => platform.Events.Add("close"),
            Wait = token =>
            {
                token.ThrowIfCancellationRequested();
                platform.Events.Add("exit");
                return Task.CompletedTask;
            }
        };
        platform.Processes.AddRange([untouched, selected]);

        var result = await InvokeAsync(platform, "app", command, "--pid", "42", "--timeout", "1", "--yes", "--json");

        var restart = command == "restart";
        AssertResult(result, new(command, true, restart, restart ? 1234 : 42, FakeFiddlerAppPlatform.DefaultPath));
        Assert.Equal(restart ? new[] { "close", "exit", "start" } : ["close", "exit"], platform.Events);
        Assert.Equal(restart ? 1 : 0, platform.Starts);
        Assert.Equal(restart ? FakeFiddlerAppPlatform.DefaultPath : null, platform.StartedPath);
        Assert.Equal(0, untouched.CloseRequests);
        Assert.Equal(1, selected.CloseRequests);
        Assert.All(platform.Processes, process => Assert.True(process.Disposed));
        Assert.Equal(1, platform.Acquisitions);
        Assert.False(platform.Locked);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("restart")]
    public async Task RedirectedInputRequiresYesWithoutPromptingOrMutation(string command)
    {
        Assert.True(Console.IsInputRedirected, "Run these CLI tests with redirected standard input.");
        var platform = new FakeFiddlerAppPlatform();
        platform.Processes.Add(new());

        var result = await InvokeAsync(platform, "app", command, "--json");

        AssertError(result, 5, ErrorCodes.ConfirmationRequired);
        Assert.DoesNotContain("[y/N]", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, platform.Acquisitions);
        Assert.False(Assert.Single(platform.Processes).Disposed);
        AssertNoMutation(platform);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("restart")]
    public async Task UnknownPidReturnsRejectedWithoutClosingOrLaunching(string command)
    {
        var platform = new FakeFiddlerAppPlatform();
        platform.Processes.Add(new());

        var result = await InvokeAsync(platform, "app", command, "--pid", "99", "--yes", "--json");

        AssertError(result, 5, ErrorCodes.NotFound);
        AssertNoMutation(platform);
    }

    [Theory]
    [InlineData("detect")]
    [InlineData("open")]
    [InlineData("restart")]
    public void MalformedPathIsRejectedBeforeInvokingHandlers(string command)
    {
        var platform = new FakeFiddlerAppPlatform();
        string[] arguments = ["app", command, "--path", "relative/Fiddler.exe", "--json"];
        if (command == "restart") arguments = [.. arguments, "--yes"];

        var result = CreateRoot(platform).Parse(arguments);
        Assert.Contains(result.Errors, error => error.Message.Contains("absolute file path", StringComparison.Ordinal));
        AssertNoMutation(platform);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("close")]
    [InlineData("restart")]
    public async Task UnsupportedVersionReturnsUsageWithoutMutation(string command)
    {
        var platform = new FakeFiddlerAppPlatform();
        platform.Installations[0] = new(FakeFiddlerAppPlatform.DefaultPath, "4.0.0.0", false);
        if (command != "open") platform.Processes.Add(new()
        {
            Info = new(42, FakeFiddlerAppPlatform.DefaultPath, "4.0.0.0", false)
        });
        string[] arguments = ["app", command, "--json"];
        if (command != "open") arguments = [.. arguments, "--yes"];

        AssertError(await InvokeAsync(platform, arguments), 2, ErrorCodes.InvalidRequest);
        AssertNoMutation(platform);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("restart")]
    public async Task MissingInstallationReturnsUnavailableWithoutLaunching(string command)
    {
        var platform = new FakeFiddlerAppPlatform();
        platform.Installations.Clear();
        string[] arguments = ["app", command, "--json"];
        if (command == "restart") arguments = [.. arguments, "--yes"];

        AssertError(await InvokeAsync(platform, arguments), 4, ErrorCodes.Unavailable);
        AssertNoMutation(platform);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("restart")]
    public async Task ExitTimeoutReturnsSixAndNeverLaunchesAReplacement(string command)
    {
        var platform = new FakeFiddlerAppPlatform();
        var process = new FakeFiddlerAppProcess { Wait = token => Task.Delay(Timeout.Infinite, token) };
        platform.Processes.Add(process);

        var result = await InvokeAsync(platform, "app", command, "--yes", "--timeout", "1", "--json");

        AssertError(result, 6, ErrorCodes.Timeout);
        Assert.Equal(1, process.CloseRequests);
        Assert.False(process.HasExited);
        Assert.True(process.Disposed);
        Assert.Equal(0, platform.Starts);
        Assert.Null(platform.StartedPath);
        Assert.False(platform.Locked);
    }

    [Theory]
    [InlineData("app close --timeout 0", "Timeout must be between 1 and 60 seconds.")]
    [InlineData("app close --timeout 61", "Timeout must be between 1 and 60 seconds.")]
    [InlineData("app restart --timeout 0", "Timeout must be between 1 and 60 seconds.")]
    [InlineData("app restart --timeout 61", "Timeout must be between 1 and 60 seconds.")]
    [InlineData("app close --timeout invalid", "invalid")]
    [InlineData("app restart --timeout invalid", "invalid")]
    [InlineData("app close --timeout", "--timeout")]
    [InlineData("app restart --timeout", "--timeout")]
    [InlineData("app close --pid invalid", "invalid")]
    [InlineData("app restart --pid invalid", "invalid")]
    [InlineData("app close --pid", "--pid")]
    [InlineData("app restart --pid", "--pid")]
    [InlineData("app close --pid 0", "PID must be a positive integer.")]
    [InlineData("app close --pid -1", "PID must be a positive integer.")]
    [InlineData("app restart --pid 0", "PID must be a positive integer.")]
    [InlineData("app restart --pid -1", "PID must be a positive integer.")]
    [InlineData("app restart --pid 42 --path C:\\Test\\Fiddler.exe", "--path and --pid cannot be combined.")]
    [InlineData("app detect --path", "--path")]
    [InlineData("app open --path", "--path")]
    [InlineData("app restart --path", "--path")]
    [InlineData("app restart --path --yes --json", "absolute file path")]
    [InlineData("app detect --force", "--force")]
    [InlineData("app open --force", "--force")]
    [InlineData("app close --yes --force", "--force")]
    [InlineData("app restart --yes --force", "--force")]
    public void InvalidOptionsProduceParserErrorsWithoutInvocation(string commandLine, string message)
    {
        var platform = new FakeFiddlerAppPlatform();
        var parse = CreateRoot(platform).Parse(commandLine.Split(' '));

        // Program maps parser errors to usage. Invoking the bare root would bypass that wrapper.
        Assert.Contains(parse.Errors, error => error.Message.Contains(message, StringComparison.Ordinal));
        Assert.Equal(0, platform.Acquisitions);
        AssertNoMutation(platform);
    }

    private static RootCommand CreateRoot(FakeFiddlerAppPlatform platform)
    {
        var json = new Option<bool>("--json") { Recursive = true };
        var root = new RootCommand();
        root.Options.Add(json);
        root.Subcommands.Add(FiddlerAppCommands.Create(new FiddlerAppService(platform), json));
        return root;
    }

    /// <summary>Invokes only fake-backed handlers, captures both streams, and restores the shared console.</summary>
    /// <param name="platform">In-memory installations and process leases. No native backend is constructed.</param>
    /// <param name="arguments">Separate CLI tokens, preserving paths containing spaces.</param>
    /// <returns>The handler exit code and unmodified stdout and stderr.</returns>
    private static async Task<(int Code, string Output, string Error)> InvokeAsync(
        FakeFiddlerAppPlatform platform, params string[] arguments)
    {
        var parse = CreateRoot(platform).Parse(arguments);
        Assert.Empty(parse.Errors);
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalIn = Console.In;
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var input = new StringReader(string.Empty);
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            Console.SetIn(input);
            var code = await parse.InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Console.SetIn(originalIn);
        }
    }

    private static void AssertResult((int Code, string Output, string Error) result, FiddlerAppResult expected)
    {
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(expected, document.RootElement.Deserialize<FiddlerAppResult>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static void AssertError((int Code, string Output, string Error) result, int exitCode, string errorCode)
    {
        Assert.Equal(exitCode, result.Code);
        Assert.Empty(result.Output);
        using var document = JsonDocument.Parse(result.Error);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(errorCode, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
    }

    private static void AssertNoMutation(FakeFiddlerAppPlatform platform)
    {
        Assert.Equal(0, platform.Starts);
        Assert.Null(platform.StartedPath);
        Assert.Empty(platform.Events);
        Assert.False(platform.Locked);
        Assert.All(platform.Processes, process =>
        {
            Assert.Equal(0, process.CloseRequests);
            Assert.False(process.HasExited);
        });
    }
}
