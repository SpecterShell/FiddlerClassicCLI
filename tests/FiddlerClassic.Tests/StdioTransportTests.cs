// Verifies that the stdio MCP transport keeps protocol output isolated on stdout.
using System.Diagnostics;
using System.Text.Json;
using FiddlerClassic.Host.Mcp;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Tests;

public sealed class StdioTransportTests
{
    /// <summary>
    /// Verifies initialization, tool discovery, and a tool call produce only JSON-RPC messages on stdout.
    /// </summary>
    [Fact]
    public async Task WritesOnlyJsonRpcMessagesToStdout()
    {
        var executable = Path.Combine(Path.GetDirectoryName(typeof(FiddlerTools).Assembly.Location)!, "fiddler-classic.exe");
        var startInfo = new ProcessStartInfo(executable, "mcp stdio")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the stdio MCP host.");

        try
        {
            await process.StandardInput.WriteLineAsync("""
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"tests","version":"1.0"}}}
                """);
            await process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            var initializeLine = await ReadLine(process, TestContext.Current.CancellationToken);
            using var initialize = JsonDocument.Parse(initializeLine);
            Assert.Equal(1, initialize.RootElement.GetProperty("id").GetInt32());

            await process.StandardInput.WriteLineAsync("""
                {"jsonrpc":"2.0","method":"notifications/initialized"}
                """);
            await process.StandardInput.WriteLineAsync("""
                {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
                """);
            await process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            var toolsLine = await ReadLine(process, TestContext.Current.CancellationToken);
            using var tools = JsonDocument.Parse(toolsLine);

            Assert.Equal(2, tools.RootElement.GetProperty("id").GetInt32());
            Assert.Equal(35, tools.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength());

            var pipeTask = FakePipeServer.ServeOnceAsync(request => FakePipeServer.Success(
                request,
                new ListSessionsResponse
                {
                    TotalMatched = 1,
                    Sessions = new List<SessionSummary> { new() { Id = 23, Method = "GET", Url = "https://example.test/" } }
                }));
            await process.StandardInput.WriteLineAsync("""
                {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_network_requests","arguments":{"pageSize":1}}}
                """);
            await process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            var callLine = await ReadLine(process, TestContext.Current.CancellationToken);
            var bridgeRequest = await pipeTask;
            using var call = JsonDocument.Parse(callLine);

            Assert.Equal(Operations.ListSessions, bridgeRequest.Operation);
            Assert.Equal(3, call.RootElement.GetProperty("id").GetInt32());
            Assert.Contains("example.test", callLine);
            var structured = call.RootElement.GetProperty("result").GetProperty("structuredContent");
            Assert.Equal(23, structured.GetProperty("requests")[0].GetProperty("requestId").GetInt32());
            Assert.False(structured.TryGetProperty("sessions", out _));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
        }
    }

    /// <summary>
    /// Reads one protocol line and includes stderr diagnostics when the host exits early.
    /// </summary>
    /// <param name="process">The running stdio MCP host process.</param>
    /// <param name="cancellationToken">Cancels the stdout or stderr read.</param>
    private static async Task<string> ReadLine(Process process, CancellationToken cancellationToken)
    {
        var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
        if (line is null)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"The stdio MCP host exited without a response. {error}");
        }

        return line;
    }
}
