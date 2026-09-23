// Exercises the published CLI process against fake bridge and daemon peers.
using System.Diagnostics;
using System.Text.Json;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

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
        var executable = HostExecutable.Resolve();
        var startInfo = new ProcessStartInfo(executable, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the CLI host.");
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
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
    [InlineData("sessions show --json")]
    [InlineData("sessions body 1 --output - --json")]
    [InlineData("request send --json")]
    [InlineData("mcp clients authorize --json")]
    [InlineData("app --unknown --json")]
    [InlineData("sessions export --output - --json")]
    [InlineData("--json sessions remove --ids")]
    [InlineData("sessions export invalid --output - --json")]
    [InlineData("sessions export --format invalid --output - --json")]
    [InlineData("--json sessions export --format")]
    [InlineData("--json mcp service configure --port")]
    [InlineData("mcp service configure --port invalid --json")]
    [InlineData("--json mcp service configure --bind")]
    [InlineData("mcp service configure --bind invalid --json")]
    [InlineData("--json mcp http --port")]
    [InlineData("--json sessions list --limit")]
    [InlineData("sessions list --limit invalid --json")]
    [InlineData("--json sessions list --min-id")]
    [InlineData("sessions list --min-id invalid --json")]
    [InlineData("--json sessions list --min-duration-ms")]
    [InlineData("--json sessions list --min-body-bytes")]
    [InlineData("--json sessions list --body-search-bytes")]
    [InlineData("--json breakpoints arm request --hold")]
    [InlineData("breakpoints arm request --hold invalid --json")]
    [InlineData("--json breakpoints wait --timeout")]
    [InlineData("--json breakpoints update 1 --body")]
    [InlineData("--json autoresponder rules add test *drop --latency-ms")]
    [InlineData("--json autoresponder rules update 1 --latency-ms")]
    [InlineData("autoresponder rules update 1 --latency-ms invalid --json")]
    [InlineData("--json autoresponder rules update 1 --match")]
    [InlineData("install --json")]
    public async Task ParserFailuresUseTheStableJsonErrorContract(string arguments)
    {
        var result = await RunCli(arguments);
        Assert.Equal(2, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.StandardOutput));
        using var document = JsonDocument.Parse(result.StandardError);
        Assert.Equal(ErrorCodes.InvalidRequest, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>
    /// Verifies incomplete groups render the same help as an explicit help request.
    /// </summary>
    /// <param name="arguments">The command group with no selected operation.</param>
    [Theory]
    [InlineData("")]
    [InlineData("app")]
    [InlineData("capture")]
    [InlineData("sessions")]
    [InlineData("sessions websocket 1")]
    [InlineData("request")]
    [InlineData("autoresponder")]
    [InlineData("autoresponder rules")]
    [InlineData("breakpoints")]
    [InlineData("bridge")]
    [InlineData("daemon")]
    [InlineData("mcp")]
    [InlineData("mcp service")]
    [InlineData("mcp clients")]
    [InlineData("mcp connections")]
    [InlineData("config")]
    [InlineData("config token")]
    public async Task CommandGroupsShowHelp(string arguments)
    {
        var result = await RunCli(arguments);
        var help = await RunCli(arguments + " --help");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, help.ExitCode);
        Assert.Equal(help.StandardOutput, result.StandardOutput);
        Assert.Contains("Usage:", result.StandardOutput);
        Assert.Contains("fiddler-classic-cli", result.StandardOutput);
        Assert.True(string.IsNullOrWhiteSpace(result.StandardError));
    }

    /// <summary>
    /// Verifies missing or invalid inputs retain a usage error and place help on stderr.
    /// </summary>
    /// <param name="arguments">An incomplete or invalid invocation that must never execute an operation.</param>
    /// <param name="command">The command whose help should accompany the error.</param>
    [Theory]
    [InlineData("sessions show", "sessions show")]
    [InlineData("sessions body 1 --output -", "sessions body")]
    [InlineData("sessions remove", "sessions remove")]
    [InlineData("sessions export --output -", "sessions export")]
    [InlineData("sessions export 1 --format har --output -", "sessions export")]
    [InlineData("sessions export invalid --output -", "sessions export")]
    [InlineData("sessions export --format invalid --output -", "sessions export")]
    [InlineData("sessions websocket", "sessions websocket")]
    [InlineData("request send", "request send")]
    [InlineData("mcp clients authorize", "mcp clients authorize")]
    [InlineData("doctor --output", "doctor")]
    [InlineData("app --unknown", "app")]
    [InlineData("app launch", "app")]
    [InlineData("mcp service configure --port 65536", "mcp service configure")]
    [InlineData("mcp service configure", "mcp service configure")]
    [InlineData("mcp service configure --port", "mcp service configure")]
    [InlineData("mcp service configure --bind", "mcp service configure")]
    [InlineData("sessions list --limit", "sessions list")]
    [InlineData("sessions list --min-id", "sessions list")]
    [InlineData("breakpoints arm request --hold", "breakpoints arm")]
    [InlineData("breakpoints update 1 --body", "breakpoints update")]
    [InlineData("autoresponder rules update 1 --latency-ms", "autoresponder rules update")]
    [InlineData("install", "")]
    public async Task InvalidInputsShowHelpWithoutContaminatingStdout(string arguments, string command)
    {
        var result = await RunCli(arguments);
        var help = await RunCli(command + " --help");

        Assert.Equal(2, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.StandardOutput));
        Assert.Contains(help.StandardOutput.Trim(), result.StandardError);
    }

    private static async Task StopDaemon()
    {
        var result = await RunCli("daemon stop --json");
        Assert.Equal(0, result.ExitCode);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
