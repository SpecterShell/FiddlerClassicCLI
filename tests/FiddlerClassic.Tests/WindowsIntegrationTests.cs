// Runs opt-in end-to-end tests against an installed and running Fiddler Classic instance.
using System.Net;
using System.Net.Sockets;
using FiddlerClassic.Host.Bridge;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Tests;

public sealed class WindowsIntegrationTests
{
    /// <summary>
    /// Verifies compose, capture discovery, headers, chunked binary body reads, SAZ export, and replay end to end.
    /// </summary>
    [Fact]
    [Trait("Category", "FiddlerIntegration")]
    public async Task CapturesDetailsLargeBinaryBodyArchiveReplayAndCompose()
    {
        RequireFlag("FIDDLER_CLASSIC_INTEGRATION");
        using var bridgePipe = new InstalledBridgePipeScope();
        var client = new NamedPipeBridgeClient(TimeSpan.FromSeconds(10));
        var status = await client.SendAsync<EmptyRequest, StatusResponse>(
            Operations.GetStatus,
            new EmptyRequest(),
            TestContext.Current.CancellationToken);
        Assert.True(status.BridgeConnected);

        var port = GetFreePort();
        var marker = Guid.NewGuid().ToString("N");
        var url = $"http://127.0.0.1:{port}/{marker}";
        var body = Enumerable.Range(0, 192 * 1024).Select(index => (byte)(index % 251)).ToArray();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var responder = RespondAsync(listener, body, requests: 2, TestContext.Current.CancellationToken);

        var queued = await client.SendAsync<SendRequestRequest, QueuedResponse>(
            Operations.SendRequest,
            new SendRequestRequest
            {
                Url = url,
                Method = "GET",
                Headers = new List<HeaderDto> { new() { Name = "X-Fiddler-Integration", Value = marker } }
            },
            TestContext.Current.CancellationToken);
        Assert.True(queued.Accepted);
        Assert.Equal(url, queued.ExpectedUrl);

        var waited = await client.SendAsync<WaitForSessionRequest, WaitForSessionResponse>(
            Operations.WaitForSession,
            new WaitForSessionRequest
            {
                AfterId = queued.BaselineSessionId,
                TimeoutMilliseconds = 5000,
                Filters = new ListSessionsRequest { UrlContains = marker }
            },
            TestContext.Current.CancellationToken);
        var session = Assert.IsType<SessionSummary>(waited.Session);
        var details = await client.SendAsync<GetSessionDetailsRequest, SessionDetails>(
            Operations.GetSessionDetails,
            new GetSessionDetailsRequest { SessionId = session.Id, IncludeHeaders = true },
            TestContext.Current.CancellationToken);
        Assert.NotNull(details.ResponseHeaders);

        var filtered = await client.SendAsync<ListSessionsRequest, ListSessionsResponse>(
            Operations.ListSessions,
            new ListSessionsRequest
            {
                HeaderName = "X-Fiddler-Integration",
                HeaderValue = marker,
                Protocol = "HTTP/1.1",
                ContentType = "application/octet-stream",
                MinBodyBytes = body.Length,
                IsError = false,
                Limit = 1
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(session.Id, Assert.Single(filtered.Sessions).Id);

        var capturedBody = await ReadCompleteBody(
            client,
            session.Id,
            BodyDirections.Response,
            TestContext.Current.CancellationToken);
        Assert.Equal(body, capturedBody);

        var archivePath = Path.Combine(Path.GetTempPath(), $"fiddler-classic-{marker}.saz");
        try
        {
            var archive = await client.SendAsync<SaveSessionsRequest, ArchiveResponse>(
                Operations.SaveSessions,
                new SaveSessionsRequest { Path = archivePath, SessionIds = new[] { session.Id } },
                TestContext.Current.CancellationToken);
            Assert.Equal(1, archive.SessionCount);
            Assert.True(File.Exists(archivePath));

            var replay = await client.SendAsync<ReplaySessionRequest, QueuedResponse>(
                Operations.ReplaySession,
                new ReplaySessionRequest { SessionId = session.Id },
                TestContext.Current.CancellationToken);
            Assert.True(replay.Accepted);
            var replayed = await client.SendAsync<WaitForSessionRequest, WaitForSessionResponse>(
                Operations.WaitForSession,
                new WaitForSessionRequest
                {
                    AfterId = replay.BaselineSessionId,
                    TimeoutMilliseconds = 5000,
                    Filters = new ListSessionsRequest { UrlContains = marker }
                },
                TestContext.Current.CancellationToken);
            var replayedSession = Assert.IsType<SessionSummary>(replayed.Session);
            var diff = await client.SendAsync<DiffSessionsRequest, SessionDiffResponse>(
                Operations.DiffSessions,
                new DiffSessionsRequest { LeftSessionId = session.Id, RightSessionId = replayedSession.Id },
                TestContext.Current.CancellationToken);
            Assert.Equal(session.Id, diff.LeftSessionId);
            await responder;
        }
        finally
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }
    }

    /// <summary>
    /// Verifies confirmed clearing can restore the original session set from a temporary SAZ backup.
    /// </summary>
    [Fact]
    [Trait("Category", "FiddlerDestructiveIntegration")]
    public async Task ClearsAndRestoresSessionsThroughSaz()
    {
        RequireFlag("FIDDLER_CLASSIC_DESTRUCTIVE_INTEGRATION");
        using var bridgePipe = new InstalledBridgePipeScope();
        var client = new NamedPipeBridgeClient(TimeSpan.FromSeconds(15));
        var archivePath = Path.Combine(Path.GetTempPath(), $"fiddler-classic-backup-{Guid.NewGuid():N}.saz");
        var cleared = false;

        try
        {
            var saved = await client.SendAsync<SaveSessionsRequest, ArchiveResponse>(
                Operations.SaveSessions,
                new SaveSessionsRequest { Path = archivePath },
                TestContext.Current.CancellationToken);
            Assert.True(saved.SessionCount > 0);

            var result = await client.SendAsync<ClearSessionsRequest, ClearSessionsResponse>(
                Operations.ClearSessions,
                new ClearSessionsRequest { Confirm = true },
                TestContext.Current.CancellationToken);
            cleared = true;
            Assert.Equal(saved.SessionCount, result.RemovedCount);

            var loaded = await client.SendAsync<LoadSessionsRequest, ArchiveResponse>(
                Operations.LoadSessions,
                new LoadSessionsRequest { Path = archivePath },
                TestContext.Current.CancellationToken);
            cleared = false;
            Assert.Equal(saved.SessionCount, loaded.SessionCount);
        }
        finally
        {
            if (cleared && File.Exists(archivePath))
            {
                await client.SendAsync<LoadSessionsRequest, ArchiveResponse>(
                    Operations.LoadSessions,
                    new LoadSessionsRequest { Path = archivePath },
                    TestContext.Current.CancellationToken);
            }

            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
        }
    }

    /// <summary>
    /// Verifies system proxy attachment changes and always restores the initial state.
    /// </summary>
    [Fact]
    [Trait("Category", "FiddlerProxyIntegration")]
    public async Task ChangesAndRestoresSystemProxyState()
    {
        RequireFlag("FIDDLER_CLASSIC_PROXY_INTEGRATION");
        using var bridgePipe = new InstalledBridgePipeScope();
        var client = new NamedPipeBridgeClient(TimeSpan.FromSeconds(10));
        var initial = await client.SendAsync<EmptyRequest, StatusResponse>(
            Operations.GetStatus,
            new EmptyRequest(),
            TestContext.Current.CancellationToken);

        try
        {
            var changed = await client.SendAsync<SetCaptureRequest, CaptureResponse>(
                Operations.SetCapture,
                new SetCaptureRequest { Enabled = !initial.IsProxyAttached },
                TestContext.Current.CancellationToken);
            Assert.Equal(!initial.IsProxyAttached, changed.IsProxyAttached);
        }
        finally
        {
            await client.SendAsync<SetCaptureRequest, CaptureResponse>(
                Operations.SetCapture,
                new SetCaptureRequest { Enabled = initial.IsProxyAttached },
                TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Verifies AutoResponder configuration, ordered rule mutation, and FARX replacement while restoring the original rule set.
    /// </summary>
    [Fact]
    [Trait("Category", "FiddlerAutomationIntegration")]
    public async Task ManagesAutoResponderRulesAndFarx()
    {
        RequireFlag("FIDDLER_CLASSIC_AUTOMATION_INTEGRATION");
        using var bridgePipe = new InstalledBridgePipeScope();
        var client = new NamedPipeBridgeClient(TimeSpan.FromSeconds(10));
        var cancellationToken = TestContext.Current.CancellationToken;
        var original = await client.SendAsync<EmptyRequest, AutoResponderStatusResponse>(
            Operations.GetAutoResponder,
            new EmptyRequest(),
            cancellationToken);
        var marker = Guid.NewGuid().ToString("N");
        var backupPath = Path.Combine(Path.GetTempPath(), $"fiddler-classic-autoresponder-backup-{marker}.farx");
        var testPath = Path.Combine(Path.GetTempPath(), $"fiddler-classic-autoresponder-test-{marker}.farx");

        await client.SendAsync<SaveAutoResponderRulesRequest, AutoResponderRulesFileResponse>(
            Operations.SaveAutoResponderRules,
            new SaveAutoResponderRulesRequest { Path = backupPath },
            cancellationToken);
        try
        {
            var added = await client.SendAsync<AddAutoResponderRuleRequest, AutoResponderMutationResponse>(
                Operations.AddAutoResponderRule,
                new AddAutoResponderRuleRequest
                {
                    Match = $"EXACT:https://fiddler-classic-cli.invalid/{marker}",
                    Action = "*drop",
                    IsEnabled = false,
                    Comment = marker,
                    LatencyMilliseconds = 15
                },
                cancellationToken);
            var rule = Assert.IsType<AutoResponderRuleDto>(added.Rule);

            var second = await client.SendAsync<AddAutoResponderRuleRequest, AutoResponderMutationResponse>(
                Operations.AddAutoResponderRule,
                new AddAutoResponderRuleRequest
                {
                    Match = $"EXACT:https://fiddler-classic-cli.invalid/{marker}-second",
                    Action = "*drop",
                    IsEnabled = false,
                    Comment = marker + "-second"
                },
                cancellationToken);
            Assert.NotNull(second.Rule);

            var updated = await client.SendAsync<UpdateAutoResponderRuleRequest, AutoResponderMutationResponse>(
                Operations.UpdateAutoResponderRule,
                new UpdateAutoResponderRuleRequest
                {
                    RuleId = rule.RuleId,
                    Comment = marker + "-updated",
                    SetComment = true,
                    DisableOnMatch = true,
                    IsEnabled = true
                },
                cancellationToken);
            Assert.Equal(marker + "-updated", updated.Rule?.Comment);
            Assert.True(updated.Rule?.DisableOnMatch);
            Assert.True(updated.Rule?.IsEnabled);

            var moved = await client.SendAsync<MoveAutoResponderRuleRequest, AutoResponderMutationResponse>(
                Operations.MoveAutoResponderRule,
                new MoveAutoResponderRuleRequest { RuleId = rule.RuleId, Index = 0 },
                cancellationToken);
            Assert.Equal(0, moved.Rule?.Index);
            var reordered = await client.SendAsync<EmptyRequest, ListAutoResponderRulesResponse>(
                Operations.ListAutoResponderRules,
                new EmptyRequest(),
                cancellationToken);
            Assert.Equal(rule.RuleId, reordered.Rules[0].RuleId);

            var saved = await client.SendAsync<SaveAutoResponderRulesRequest, AutoResponderRulesFileResponse>(
                Operations.SaveAutoResponderRules,
                new SaveAutoResponderRulesRequest { Path = testPath },
                cancellationToken);
            Assert.Equal(original.RuleCount + 2, saved.RuleCount);
            Assert.True(File.Exists(testPath));

            var loaded = await client.SendAsync<LoadAutoResponderRulesRequest, AutoResponderRulesFileResponse>(
                Operations.LoadAutoResponderRules,
                new LoadAutoResponderRulesRequest { Path = testPath, Replace = true, ConfirmReplace = true },
                cancellationToken);
            Assert.Equal(saved.RuleCount, loaded.RuleCount);
            var listed = await client.SendAsync<EmptyRequest, ListAutoResponderRulesResponse>(
                Operations.ListAutoResponderRules,
                new EmptyRequest(),
                cancellationToken);
            Assert.Contains(listed.Rules, candidate => candidate.Comment == marker + "-updated");
        }
        finally
        {
            await client.SendAsync<LoadAutoResponderRulesRequest, AutoResponderRulesFileResponse>(
                Operations.LoadAutoResponderRules,
                new LoadAutoResponderRulesRequest { Path = backupPath, Replace = true, ConfirmReplace = true },
                cancellationToken);
            await client.SendAsync<ConfigureAutoResponderRequest, AutoResponderStatusResponse>(
                Operations.ConfigureAutoResponder,
                new ConfigureAutoResponderRequest
                {
                    IsEnabled = original.IsEnabled,
                    PermitFallthrough = original.PermitFallthrough,
                    AcceptAllConnects = original.AcceptAllConnects,
                    UseLatency = original.UseLatency
                },
                cancellationToken);
            if (!original.IsRuleListDirty)
            {
                await client.SendAsync<SaveAutoResponderRulesRequest, AutoResponderRulesFileResponse>(
                    Operations.SaveAutoResponderRules,
                    new SaveAutoResponderRulesRequest
                    {
                        Path = backupPath,
                        Overwrite = true,
                        ConfirmOverwrite = true
                    },
                    cancellationToken);
            }

            File.Delete(backupPath);
            if (File.Exists(testPath))
            {
                File.Delete(testPath);
            }
        }
    }

    /// <summary>
    /// Verifies request and response breakpoint arms, mutation, resume, and automatic timeout against loopback traffic.
    /// </summary>
    [Fact]
    [Trait("Category", "FiddlerAutomationIntegration")]
    public async Task MutatesRequestAndResponseBreakpointsAndAutoResumes()
    {
        RequireFlag("FIDDLER_CLASSIC_AUTOMATION_INTEGRATION");
        using var bridgePipe = new InstalledBridgePipeScope();
        var client = new NamedPipeBridgeClient(TimeSpan.FromSeconds(15));
        var cancellationToken = TestContext.Current.CancellationToken;
        var port = GetFreePort();
        var marker = Guid.NewGuid().ToString("N");
        var root = $"http://127.0.0.1:{port}/";
        var bridgeStatus = await client.SendAsync<EmptyRequest, StatusResponse>(
            Operations.GetStatus,
            new EmptyRequest(),
            cancellationToken);
        Assert.True(bridgeStatus.ListenPort.HasValue);
        using var proxyClient = CreateFiddlerProxyClient(bridgeStatus.ListenPort.Value);
        using var listener = new HttpListener();
        listener.Prefixes.Add(root);
        listener.Start();

        var cursor = (await client.SendAsync<ListPendingBreakpointsRequest, ListPendingBreakpointsResponse>(
            Operations.ListPendingBreakpoints,
            new ListPendingBreakpointsRequest(),
            cancellationToken)).LatestSequence;
        await client.SendAsync<ArmBreakpointRequest, BreakpointArmMutationResponse>(
            Operations.ArmBreakpoint,
            new ArmBreakpointRequest
            {
                Stage = BreakpointStages.Request,
                Filter = new BreakpointFilterDto { UrlContains = marker + "-request" },
                HoldMilliseconds = 15000
            },
            cancellationToken);
        var requestObservedTask = ObserveAndRespondAsync(listener, "origin-request", cancellationToken);
        var requestBaseline = await GetLatestSessionId(client, cancellationToken);
        var requestClientTask = proxyClient.GetAsync(root + marker + "-request", cancellationToken);
        var requestPause = await WaitForBreakpoint(client, cursor, BreakpointStages.Request, cancellationToken);
        var requestStageError = await Assert.ThrowsAsync<BridgeClientException>(() =>
            client.SendAsync<UpdateBreakpointRequest, BreakpointDetailsResponse>(
                Operations.UpdateBreakpoint,
                new UpdateBreakpointRequest { BreakpointId = requestPause.BreakpointId, StatusCode = 299 },
                cancellationToken));
        Assert.Equal(ErrorCodes.InvalidRequest, requestStageError.Code);
        var requestBody = "changed-request"u8.ToArray();
        await client.SendAsync<UpdateBreakpointRequest, BreakpointDetailsResponse>(
            Operations.UpdateBreakpoint,
            new UpdateBreakpointRequest
            {
                BreakpointId = requestPause.BreakpointId,
                Url = root + marker + "-changed",
                SetHeaders = new List<HeaderDto> { new() { Name = "X-Fiddler-Automation", Value = marker } },
                BodyBase64 = Convert.ToBase64String(requestBody)
            },
            cancellationToken);
        await client.SendAsync<BreakpointActionRequest, BreakpointActionResponse>(
            Operations.ResumeBreakpoint,
            new BreakpointActionRequest { BreakpointId = requestPause.BreakpointId },
            cancellationToken);
        var observedRequest = await requestObservedTask;
        using var requestClientResponse = await requestClientTask;
        Assert.Equal(HttpStatusCode.OK, requestClientResponse.StatusCode);
        Assert.Equal('/' + marker + "-changed", observedRequest.Path);
        Assert.Equal(marker, observedRequest.Header);
        Assert.Equal(requestBody, observedRequest.Body);
        var requestSession = await WaitForSession(client, requestBaseline, marker + "-changed", cancellationToken);
        Assert.Equal(requestBody, await ReadCompleteBody(client, requestSession.Id, BodyDirections.Request, cancellationToken));

        cursor = (await client.SendAsync<ListPendingBreakpointsRequest, ListPendingBreakpointsResponse>(
            Operations.ListPendingBreakpoints,
            new ListPendingBreakpointsRequest(),
            cancellationToken)).LatestSequence;
        await client.SendAsync<ArmBreakpointRequest, BreakpointArmMutationResponse>(
            Operations.ArmBreakpoint,
            new ArmBreakpointRequest
            {
                Stage = BreakpointStages.Response,
                Filter = new BreakpointFilterDto { UrlContains = marker + "-response", StatusCode = 200 },
                HoldMilliseconds = 15000
            },
            cancellationToken);
        var responseObservedTask = ObserveAndRespondAsync(listener, "origin-response", cancellationToken);
        var responseBaseline = await GetLatestSessionId(client, cancellationToken);
        var responseClientTask = proxyClient.GetAsync(root + marker + "-response", cancellationToken);
        var responsePause = await WaitForBreakpoint(client, cursor, BreakpointStages.Response, cancellationToken);
        var responseStageError = await Assert.ThrowsAsync<BridgeClientException>(() =>
            client.SendAsync<UpdateBreakpointRequest, BreakpointDetailsResponse>(
                Operations.UpdateBreakpoint,
                new UpdateBreakpointRequest { BreakpointId = responsePause.BreakpointId, Method = "POST" },
                cancellationToken));
        Assert.Equal(ErrorCodes.InvalidRequest, responseStageError.Code);
        var responseBody = "changed-response"u8.ToArray();
        await client.SendAsync<UpdateBreakpointRequest, BreakpointDetailsResponse>(
            Operations.UpdateBreakpoint,
            new UpdateBreakpointRequest
            {
                BreakpointId = responsePause.BreakpointId,
                StatusCode = 299,
                StatusDescription = "Automation",
                SetHeaders = new List<HeaderDto> { new() { Name = "X-Fiddler-Automation", Value = marker } },
                BodyBase64 = Convert.ToBase64String(responseBody)
            },
            cancellationToken);
        await client.SendAsync<BreakpointActionRequest, BreakpointActionResponse>(
            Operations.ResumeBreakpoint,
            new BreakpointActionRequest { BreakpointId = responsePause.BreakpointId },
            cancellationToken);
        await responseObservedTask;
        using var responseClientResponse = await responseClientTask;
        Assert.Equal(299, (int)responseClientResponse.StatusCode);
        Assert.Equal(responseBody, await responseClientResponse.Content.ReadAsByteArrayAsync(cancellationToken));
        var responseSession = await WaitForSession(client, responseBaseline, marker + "-response", cancellationToken);
        Assert.Equal(299, responseSession.StatusCode);
        Assert.Equal(responseBody, await ReadCompleteBody(client, responseSession.Id, BodyDirections.Response, cancellationToken));

        cursor = (await client.SendAsync<ListPendingBreakpointsRequest, ListPendingBreakpointsResponse>(
            Operations.ListPendingBreakpoints,
            new ListPendingBreakpointsRequest(),
            cancellationToken)).LatestSequence;
        await client.SendAsync<ArmBreakpointRequest, BreakpointArmMutationResponse>(
            Operations.ArmBreakpoint,
            new ArmBreakpointRequest
            {
                Stage = BreakpointStages.Request,
                Filter = new BreakpointFilterDto { UrlContains = marker + "-timeout" },
                HoldMilliseconds = 250
            },
            cancellationToken);
        var timeoutObservedTask = ObserveAndRespondAsync(listener, "timeout", cancellationToken);
        var timeoutBaseline = await GetLatestSessionId(client, cancellationToken);
        var timeoutClientTask = proxyClient.GetAsync(root + marker + "-timeout", cancellationToken);
        await WaitForBreakpoint(client, cursor, BreakpointStages.Request, cancellationToken);
        await timeoutObservedTask;
        using var timeoutClientResponse = await timeoutClientTask;
        Assert.Equal(HttpStatusCode.OK, timeoutClientResponse.StatusCode);
        var timeoutSession = await WaitForSession(client, timeoutBaseline, marker + "-timeout", cancellationToken);
        Assert.True(timeoutSession.IsComplete);
        var pending = await client.SendAsync<ListPendingBreakpointsRequest, ListPendingBreakpointsResponse>(
            Operations.ListPendingBreakpoints,
            new ListPendingBreakpointsRequest { SessionId = timeoutSession.Id },
            cancellationToken);
        Assert.Empty(pending.Breakpoints);
    }

    /// <summary>
    /// Serves a fixed binary body for a bounded number of local HTTP requests.
    /// </summary>
    /// <param name="listener">The active loopback HTTP listener.</param>
    /// <param name="body">The response bytes.</param>
    /// <param name="requests">The number of requests to serve.</param>
    /// <param name="cancellationToken">Cancels request acceptance and response writes.</param>
    private static async Task RespondAsync(
        HttpListener listener,
        byte[] body,
        int requests,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < requests; index++)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, cancellationToken);
            context.Response.Close();
        }
    }

    /// <summary>
    /// Reassembles a complete response body from bounded bridge chunks.
    /// </summary>
    /// <param name="client">The live Fiddler bridge client.</param>
    /// <param name="sessionId">The captured session ID.</param>
    /// <param name="cancellationToken">Cancels bridge reads and memory-stream writes.</param>
    private static async Task<byte[]> ReadCompleteBody(
        IBridgeClient client,
        int sessionId,
        string direction,
        CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        long offset = 0;
        while (true)
        {
            var chunk = await client.SendAsync<GetSessionBodyRequest, SessionBodyChunk>(
                Operations.GetSessionBody,
                new GetSessionBodyRequest
                {
                    SessionId = sessionId,
                    Direction = direction,
                    Offset = offset,
                    Length = ProtocolConstants.McpMaxBodyChunkBytes
                },
                cancellationToken);
            var bytes = Convert.FromBase64String(chunk.Base64Data);
            await output.WriteAsync(bytes, cancellationToken);
            offset += bytes.Length;
            if (chunk.EndOfBody)
            {
                return output.ToArray();
            }
        }
    }

    /// <summary>
    /// Creates an HTTP client that always routes loopback requests through Fiddler's explicit proxy endpoint.
    /// </summary>
    /// <param name="proxyPort">Fiddler's active loopback listener port.</param>
    private static HttpClient CreateFiddlerProxyClient(int proxyPort)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}", false),
            UseProxy = true
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Gets the newest current Fiddler session ID for later completion correlation.
    /// </summary>
    /// <param name="client">The live bridge client.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    private static async Task<int> GetLatestSessionId(
        IBridgeClient client,
        CancellationToken cancellationToken)
    {
        var response = await client.SendAsync<ListSessionsRequest, ListSessionsResponse>(
            Operations.ListSessions,
            new ListSessionsRequest { Limit = 1, NewestFirst = true },
            cancellationToken);
        return response.Sessions.Count == 0 ? 0 : response.Sessions[0].Id;
    }

    /// <summary>
    /// Waits for one live pause after a sequence cursor and returns its required breakpoint metadata.
    /// </summary>
    /// <param name="client">The live bridge client.</param>
    /// <param name="cursor">The exclusive breakpoint sequence cursor.</param>
    /// <param name="stage">The request or response stage.</param>
    /// <param name="cancellationToken">Cancels the bridge wait.</param>
    private static async Task<PendingBreakpointDto> WaitForBreakpoint(
        IBridgeClient client,
        long cursor,
        string stage,
        CancellationToken cancellationToken)
    {
        var waited = await client.SendAsync<WaitForBreakpointRequest, WaitForBreakpointResponse>(
            Operations.WaitForBreakpoint,
            new WaitForBreakpointRequest
            {
                AfterSequence = cursor,
                Stage = stage,
                TimeoutMilliseconds = 5000
            },
            cancellationToken);
        Assert.True(waited.Matched);
        return Assert.IsType<PendingBreakpointDto>(waited.Breakpoint);
    }

    /// <summary>
    /// Waits for one completed session whose URL contains the marker.
    /// </summary>
    /// <param name="client">The live bridge client.</param>
    /// <param name="baselineSessionId">The exclusive session ID cursor.</param>
    /// <param name="urlMarker">The expected URL substring.</param>
    /// <param name="cancellationToken">Cancels the bridge wait.</param>
    private static async Task<SessionSummary> WaitForSession(
        IBridgeClient client,
        int baselineSessionId,
        string urlMarker,
        CancellationToken cancellationToken)
    {
        var waited = await client.SendAsync<WaitForSessionRequest, WaitForSessionResponse>(
            Operations.WaitForSession,
            new WaitForSessionRequest
            {
                AfterId = baselineSessionId,
                TimeoutMilliseconds = 5000,
                Filters = new ListSessionsRequest { UrlContains = urlMarker }
            },
            cancellationToken);
        return Assert.IsType<SessionSummary>(waited.Session);
    }

    /// <summary>
    /// Captures one loopback request and sends a fixed UTF-8 response.
    /// </summary>
    /// <param name="listener">The active loopback listener.</param>
    /// <param name="responseText">The response body text.</param>
    /// <param name="cancellationToken">Cancels request acceptance and body I/O.</param>
    private static async Task<ObservedRequest> ObserveAndRespondAsync(
        HttpListener listener,
        string responseText,
        CancellationToken cancellationToken)
    {
        var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
        await using var requestBody = new MemoryStream();
        await context.Request.InputStream.CopyToAsync(requestBody, cancellationToken);
        var response = System.Text.Encoding.UTF8.GetBytes(responseText);
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = response.Length;
        await context.Response.OutputStream.WriteAsync(response, cancellationToken);
        context.Response.Close();
        return new ObservedRequest
        {
            Path = context.Request.Url?.AbsolutePath ?? string.Empty,
            Header = context.Request.Headers["X-Fiddler-Automation"],
            Body = requestBody.ToArray()
        };
    }

    private static void RequireFlag(string variable)
    {
        Assert.SkipUnless(
            string.Equals(Environment.GetEnvironmentVariable(variable), "1", StringComparison.Ordinal),
            $"Set {variable}=1 to run this opt-in integration test.");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class InstalledBridgePipeScope : IDisposable
    {
        private const string VariableName = "FIDDLER_CLASSIC_PIPE_NAME";
        private readonly string? _previousValue;

        public InstalledBridgePipeScope()
        {
            _previousValue = Environment.GetEnvironmentVariable(VariableName);
            Environment.SetEnvironmentVariable(VariableName, null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(VariableName, _previousValue);
        }
    }

    private sealed class ObservedRequest
    {
        public string Path { get; set; } = string.Empty;
        public string? Header { get; set; }
        public byte[] Body { get; set; } = Array.Empty<byte>();
    }
}
