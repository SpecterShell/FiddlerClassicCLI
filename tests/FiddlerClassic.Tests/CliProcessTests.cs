// Exercises the published CLI process against fake bridge and daemon peers.
using System.Diagnostics;
using System.Text.Json;
using FiddlerClassic.Host.Mcp;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Tests;

public sealed class CliProcessTests
{
    /// <summary>
    /// Verifies a CLI process starts the daemon, relays a list request, and emits structured output.
    /// </summary>
    [Fact]
    public async Task ListsSessionsThroughFakeNamedPipeBridge()
    {
        try
        {
            var serverTask = FakePipeServer.ServeOnceAsync(request => FakePipeServer.Success(
                request,
                new ListSessionsResponse
                {
                    TotalMatched = 1,
                    Sessions = new List<SessionSummary>
                    {
                        new() { Id = 17, Method = "GET", Url = "https://example.test/", StatusCode = 200 }
                    }
                }));

            var result = await RunCli("sessions list --limit 1 --json");
            var request = await serverTask;
            var daemonStatus = await RunCli("daemon status --json");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(Operations.ListSessions, request.Operation);
            using var output = JsonDocument.Parse(result.StandardOutput);
            Assert.Equal(17, output.RootElement.GetProperty("sessions")[0].GetProperty("id").GetInt32());
            Assert.True(string.IsNullOrWhiteSpace(result.StandardError));
            Assert.Equal(0, daemonStatus.ExitCode);
            using var daemonOutput = JsonDocument.Parse(daemonStatus.StandardOutput);
            Assert.True(daemonOutput.RootElement.GetProperty("running").GetBoolean());
        }
        finally
        {
            await StopDaemon();
        }
    }

    /// <summary>
    /// Verifies a stable bridge failure reaches stderr and maps to the documented process exit code.
    /// </summary>
    [Fact]
    public async Task MapsFakeBridgeErrorToDocumentedProcessExitCode()
    {
        try
        {
            var serverTask = FakePipeServer.ServeOnceAsync(request => new BridgeResponse
            {
                ProtocolVersion = ProtocolConstants.Version,
                RequestId = request.RequestId,
                Success = false,
                Error = new BridgeError { Code = ErrorCodes.NotFound, Message = "Session 99 was not found." }
            });

            var result = await RunCli("sessions show 99 --json");
            await serverTask;

            Assert.Equal(5, result.ExitCode);
            Assert.Contains("not_found", result.StandardError);
            Assert.True(string.IsNullOrWhiteSpace(result.StandardOutput));
        }
        finally
        {
            await StopDaemon();
        }
    }

    /// <summary>
    /// Verifies separate CLI invocations discover and reuse the same daemon process.
    /// </summary>
    [Fact]
    public async Task ReusesDaemonAcrossCliInvocations()
    {
        try
        {
            var start = await RunCli("daemon start --json");
            var status = await RunCli("daemon status --json");

            Assert.Equal(0, start.ExitCode);
            Assert.Equal(0, status.ExitCode);
            using var startJson = JsonDocument.Parse(start.StandardOutput);
            using var statusJson = JsonDocument.Parse(status.StandardOutput);
            Assert.Equal(
                startJson.RootElement.GetProperty("processId").GetInt32(),
                statusJson.RootElement.GetProperty("processId").GetInt32());
        }
        finally
        {
            await StopDaemon();
        }
    }

    /// <summary>
    /// Runs the test host as a child process and captures its exit code and separated output streams.
    /// </summary>
    /// <param name="arguments">The complete CLI argument string.</param>
    private static async Task<ProcessResult> RunCli(string arguments)
    {
        var executable = Path.Combine(Path.GetDirectoryName(typeof(FiddlerTools).Assembly.Location)!, "fiddler-classic.exe");
        var startInfo = new ProcessStartInfo(executable, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the CLI host.");
        var standardOutput = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var standardError = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    [Theory]
    [InlineData("sessions list --summary --limit 0 --json")]
    [InlineData("sessions list --summary --min-duration-ms -1 --json")]
    [InlineData("doctor --json --output")]
    [InlineData("mcp service configure --port 65536 --json")]
    [InlineData("app close --timeout invalid --yes --json")]
    [InlineData("app restart --timeout invalid --yes --json")]
    [InlineData("app restart --path --yes --json")]
    [InlineData("app detect --path relative/Fiddler.exe --json")]
    [InlineData("app open --path relative/Fiddler.exe --json")]
    [InlineData("app restart --path relative/Fiddler.exe --yes --json")]
    [InlineData("app close --pid invalid --yes --json")]
    [InlineData("app restart --pid 1 --path C:\\Test\\Fiddler.exe --yes --json")]
    [InlineData("sessions replay 1 --timeout invalid --json")]
    [InlineData("sessions watch --timeout invalid --json")]
    [InlineData("request send http://example.test --timeout invalid --json")]
    public async Task ParserFailuresUseTheStableJsonErrorContract(string arguments)
    {
        var result = await RunCli(arguments);
        Assert.Equal(2, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.StandardOutput));
        using var document = JsonDocument.Parse(result.StandardError);
        Assert.Equal(ErrorCodes.InvalidRequest, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task StopDaemon()
    {
        var result = await RunCli("daemon stop --json");
        Assert.Equal(0, result.ExitCode);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
