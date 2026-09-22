// Checks CLI wiring, output boundaries, and stable errors for metadata-only additions.
using System.CommandLine;
using System.Text.Json;
using FiddlerClassic.Host.Cli;
using FiddlerClassic.Host.Daemon;
using FiddlerClassic.Host.Services;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Tests;

public sealed class ReadOnlyFeatureCliTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FiddlerClassicTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SummaryUsesOneListCallAndReportsItsScope(bool json)
    {
        var calls = 0;
        var bridge = new TestBridgeClient
        {
            Handler = (operation, payload) =>
        {
            Assert.Equal(Operations.ListSessions, operation);
            var request = Assert.IsType<ListSessionsRequest>(payload);
            Assert.Equal(2, request.Limit);
            Assert.Equal("example.test", request.Host);
            calls++;
            return new ListSessionsResponse
            {
                TotalMatched = 3,
                Sessions =
            [new SessionSummary { Id = 7, Host = "example.test", StatusCode = 200,
                ResponseBodyBytes = 12, IsComplete = true, DurationMilliseconds = 4 }]
            };
        }
        };
        var arguments = new List<string> { "sessions", "list", "--summary", "--host", "example.test", "--limit", "2" };
        if (json) arguments.Add("--json");
        var result = Invoke(CreateRoot(bridge), arguments.ToArray());
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        Assert.Equal(1, calls);
        if (json)
        {
            using var document = JsonDocument.Parse(result.Output);
            Assert.Equal("returned_sessions", document.RootElement.GetProperty("scope").GetString());
            Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
            Assert.Equal(12, document.RootElement.GetProperty("responseBodyBytes").GetInt64());
        }
        else
        {
            Assert.Contains("Returned 1 of 3", result.Output, StringComparison.Ordinal);
            Assert.Contains("Captured body bytes", result.Output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("--body-contains", "needle")]
    [InlineData("--header-name", "X-Test")]
    [InlineData("--header-value", "value")]
    public void InvalidSummaryFiltersReturnUsageWithoutBridgeAccess(string option, string value)
    {
        var result = Invoke(CreateRoot(new TestBridgeClient()), "sessions", "list", "--summary", option, value, "--json");
        Assert.Equal(ExitCodes.Usage, result.Code);
        Assert.Empty(result.Output);
        Assert.Contains(ErrorCodes.InvalidRequest, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("relative.json")]
    [InlineData("C:\\Temp\\NUL.json")]
    public void InvalidDiagnosticDestinationReturnsUsage(string path)
    {
        var result = Invoke(CreateRoot(new TestBridgeClient()), "doctor", "--output", path, "--json");
        Assert.Equal(ExitCodes.Usage, result.Code);
        Assert.Empty(result.Output);
        Assert.Contains(ErrorCodes.InvalidRequest, result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void DiagnosticExportCreatesOnlyAReportAndRejectsReplacements()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "report.json");
        var root = CreateRoot(new TestBridgeClient());
        var result = Invoke(root, "doctor", "--output", path, "--json");
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        using var receipt = JsonDocument.Parse(result.Output);
        Assert.Equal(path, receipt.RootElement.GetProperty("path").GetString());
        var original = File.ReadAllText(path);
        using var report = JsonDocument.Parse(original);
        Assert.Equal(1, report.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(report.RootElement.GetProperty("daemon").GetProperty("running").GetBoolean());
        Assert.DoesNotContain(_directory, original, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_directory, "config.json")));

        var repeated = Invoke(root, "doctor", "--output", path, "--json");
        Assert.Equal(ExitCodes.Rejected, repeated.Code);
        Assert.Empty(repeated.Output);
        Assert.Contains(ErrorCodes.Conflict, repeated.Error, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(path));
    }

    private RootCommand CreateRoot(TestBridgeClient bridge)
    {
        var environment = new FiddlerEnvironment();
        var actions = new CliActions(bridge, new BridgeInstaller(environment), new ConfigStore(_directory),
            environment, new StatusService(bridge, environment));
        return CommandFactory.Create(actions, new DaemonClient(Path.Combine(_directory, "must-not-launch.exe"),
            $"fiddler-classic-cli.read-only-tests.{Guid.NewGuid():N}",
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)));
    }

    private static (int Code, string Output, string Error) Invoke(RootCommand root, params string[] arguments)
    {
        var previousOutput = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var code = root.Parse(arguments).Invoke();
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
