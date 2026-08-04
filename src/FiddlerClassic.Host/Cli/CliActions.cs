// Implements CLI operations over the shared bridge, environment, and configuration services.
using System.Text;
using System.Runtime.CompilerServices;
using FiddlerClassic.Host.Bridge;
using FiddlerClassic.Host.Mcp;
using FiddlerClassic.Host.Services;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Host.Cli;

internal sealed class CliActions
{
    private readonly IBridgeClient _bridgeClient;
    private readonly BridgeInstaller _bridgeInstaller;
    private readonly ConfigStore _configStore;
    private readonly FiddlerEnvironment _environment;
    private readonly StatusService _statusService;
    private readonly SessionExportService _sessionExporter;

    /// <summary>
    /// Creates the CLI operation facade from shared bridge, installation, configuration, and status services.
    /// </summary>
    /// <param name="bridgeClient">Sends operational requests to Fiddler.</param>
    /// <param name="bridgeInstaller">Installs and removes bridge artifacts.</param>
    /// <param name="configStore">Loads and rotates local host configuration.</param>
    /// <param name="environment">Discovers local Fiddler state.</param>
    /// <param name="statusService">Combines local and live status information.</param>
    public CliActions(
        IBridgeClient bridgeClient,
        BridgeInstaller bridgeInstaller,
        ConfigStore configStore,
        FiddlerEnvironment environment,
        StatusService statusService)
    {
        _bridgeClient = bridgeClient;
        _bridgeInstaller = bridgeInstaller;
        _configStore = configStore;
        _environment = environment;
        _statusService = statusService;
        _sessionExporter = new SessionExportService(bridgeClient);
    }

    public Task<StatusResponse> Status(CancellationToken cancellationToken)
    {
        return _statusService.GetAsync(cancellationToken);
    }

    /// <summary>
    /// Runs installation, process, bridge, version, and token diagnostics without requiring Fiddler to be online.
    /// </summary>
    /// <param name="cancellationToken">Cancels the live bridge portion of the diagnostics.</param>
    public async Task<DoctorResult> Doctor(CancellationToken cancellationToken)
    {
        _configStore.GetOrCreate();
        var probe = await _statusService.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var status = probe.Status;
        var diagnostics = new List<DoctorCheck>
        {
            new("windows", OperatingSystem.IsWindows(), "Windows is required."),
            new("fiddler-installed", status.FiddlerInstalled, status.FiddlerPath ?? "Fiddler Classic was not found."),
            new("fiddler-5x", status.FiddlerVersion?.StartsWith("5.", StringComparison.Ordinal) == true, status.FiddlerVersion ?? "Version unavailable."),
            new("bridge-installed", status.BridgeInstalled, _environment.BridgeDestinationPath),
            new("fiddler-running", status.FiddlerRunning, status.FiddlerProcessId?.ToString() ?? "Fiddler is not running."),
            new(
                "bridge-connected",
                status.BridgeConnected,
                status.BridgeConnected
                    ? "Named pipe connected."
                    : probe.BridgeErrorMessage ?? "Start or restart Fiddler after installing the bridge."),
            new("token-file", File.Exists(_configStore.ConfigPath), _configStore.ConfigPath)
        };

        return new DoctorResult
        {
            Healthy = diagnostics.Take(6).All(check => check.Passed),
            Checks = diagnostics
        };
    }

    public Task<CaptureResponse> SetCapture(bool enabled, CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<SetCaptureRequest, CaptureResponse>(
            Operations.SetCapture,
            new SetCaptureRequest { Enabled = enabled },
            cancellationToken);
    }

    public Task<ListSessionsResponse> ListSessions(ListSessionsRequest request, CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<ListSessionsRequest, ListSessionsResponse>(
            Operations.ListSessions,
            request,
            cancellationToken);
    }

    /// <summary>
    /// Watches completed sessions after an explicit cursor or the current newest session until an inactivity timeout.
    /// </summary>
    /// <param name="filters">The reusable metadata, header, timing, size, and body filters.</param>
    /// <param name="afterSessionId">The optional exclusive cursor; omission starts after the current newest session.</param>
    /// <param name="timeoutMilliseconds">The inactivity timeout for each event wait.</param>
    /// <param name="cancellationToken">Cancels the watch.</param>
    public async IAsyncEnumerable<SessionSummary> WatchSessions(
        ListSessionsRequest filters,
        int? afterSessionId,
        int timeoutMilliseconds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cursor = afterSessionId ?? await GetLatestSessionId(cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            var response = await _bridgeClient.SendAsync<WaitForSessionRequest, WaitForSessionResponse>(
                Operations.WaitForSession,
                new WaitForSessionRequest
                {
                    AfterId = cursor,
                    TimeoutMilliseconds = timeoutMilliseconds,
                    Filters = filters
                },
                cancellationToken).ConfigureAwait(false);
            if (!response.Matched || response.Session == null)
            {
                yield break;
            }

            cursor = response.Session.Id;
            yield return response.Session;
        }
    }

    public Task<SessionDetails> GetSession(int sessionId, CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<GetSessionDetailsRequest, SessionDetails>(
            Operations.GetSessionDetails,
            new GetSessionDetailsRequest { SessionId = sessionId, IncludeHeaders = true },
            cancellationToken);
    }

    /// <summary>
    /// Streams a complete body from bounded bridge chunks to stdout or an explicit file.
    /// </summary>
    /// <param name="sessionId">The captured session ID.</param>
    /// <param name="direction">Whether to read the request or response body.</param>
    /// <param name="outputPath">An output file path or <c>-</c> for stdout.</param>
    /// <param name="cancellationToken">Cancels bridge reads and output writes.</param>
    public async Task<BodyWriteResult> WriteBody(
        int sessionId,
        string direction,
        string outputPath,
        CancellationToken cancellationToken)
    {
        Stream output;
        string resolvedPath;
        if (outputPath == "-")
        {
            output = Console.OpenStandardOutput();
            resolvedPath = "-";
        }
        else
        {
            resolvedPath = Path.GetFullPath(outputPath);
            output = new FileStream(resolvedPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        }

        await using (output.ConfigureAwait(false))
        {
            long offset = 0;
            while (true)
            {
                var chunk = await _bridgeClient.SendAsync<GetSessionBodyRequest, SessionBodyChunk>(
                    Operations.GetSessionBody,
                    new GetSessionBodyRequest
                    {
                        SessionId = sessionId,
                        Direction = direction,
                        Offset = offset,
                        Length = ProtocolConstants.CliBodyChunkBytes
                    },
                    cancellationToken).ConfigureAwait(false);

                var bytes = Convert.FromBase64String(chunk.Base64Data);
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                offset += bytes.Length;

                if (chunk.EndOfBody)
                {
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return new BodyWriteResult
                    {
                        Path = resolvedPath,
                        BytesWritten = offset,
                        ContentType = chunk.ContentType
                    };
                }

                if (bytes.Length == 0)
                {
                    throw new InvalidDataException("The Fiddler bridge returned an empty body chunk before EOF.");
                }
            }
        }
    }

    public Task<ClearSessionsResponse> ClearSessions(CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<ClearSessionsRequest, ClearSessionsResponse>(
            Operations.ClearSessions,
            new ClearSessionsRequest { Confirm = true },
            cancellationToken);
    }

    public Task<RemoveSessionsResponse> RemoveSessions(int[] sessionIds, CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<RemoveSessionsRequest, RemoveSessionsResponse>(
            Operations.RemoveSessions,
            new RemoveSessionsRequest { SessionIds = sessionIds, Confirm = true },
            cancellationToken);
    }

    /// <summary>
    /// Sends a SAZ export request with optional session selection and explicit overwrite controls.
    /// </summary>
    /// <param name="path">The absolute destination SAZ path.</param>
    /// <param name="sessionIds">The optional captured session IDs to export.</param>
    /// <param name="overwrite">Whether an existing archive may be replaced.</param>
    /// <param name="confirmOverwrite">Whether replacement has been explicitly confirmed.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    public Task<ArchiveResponse> SaveSessions(
        string path,
        int[]? sessionIds,
        bool overwrite,
        bool confirmOverwrite,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<SaveSessionsRequest, ArchiveResponse>(
            Operations.SaveSessions,
            new SaveSessionsRequest
            {
                Path = path,
                SessionIds = sessionIds,
                Overwrite = overwrite,
                ConfirmOverwrite = confirmOverwrite
            },
            cancellationToken);
    }

    public Task<ArchiveResponse> LoadSessions(string path, CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<LoadSessionsRequest, ArchiveResponse>(
            Operations.LoadSessions,
            new LoadSessionsRequest { Path = path },
            cancellationToken);
    }

    public async Task<CliQueuedResult> ReplaySession(
        int sessionId,
        bool unconditional,
        bool wait,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var queued = await _bridgeClient.SendAsync<ReplaySessionRequest, QueuedResponse>(
            Operations.ReplaySession,
            new ReplaySessionRequest { SessionId = sessionId, Unconditional = unconditional },
            cancellationToken).ConfigureAwait(false);
        return await WaitForQueuedRequest(queued, wait, timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates mutually exclusive body inputs, encodes the body, and queues a composed request through Fiddler.
    /// </summary>
    /// <param name="url">The absolute HTTP or HTTPS URL.</param>
    /// <param name="method">The HTTP method token.</param>
    /// <param name="headers">Repeated raw <c>Name: value</c> header arguments.</param>
    /// <param name="body">An optional UTF-8 text body.</param>
    /// <param name="bodyFile">An optional file containing raw body bytes.</param>
    /// <param name="wait">Whether to wait for the resulting completed session.</param>
    /// <param name="timeoutMilliseconds">The bounded completion wait duration.</param>
    /// <param name="cancellationToken">Cancels the bridge request.</param>
    public async Task<CliQueuedResult> SendRequest(
        string url,
        string method,
        string[] headers,
        string? body,
        string? bodyFile,
        bool wait,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        if (body is not null && bodyFile is not null)
        {
            throw new ArgumentException("Specify either --body or --body-file, not both.");
        }

        string? bodyBase64 = null;
        if (body is not null)
        {
            bodyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        }
        else if (bodyFile is not null)
        {
            var fileInfo = new FileInfo(bodyFile);
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException("The request body file was not found.", fileInfo.FullName);
            }

            if (fileInfo.Length > ProtocolConstants.MaxComposeBodyBytes)
            {
                throw new ArgumentException($"The request body cannot exceed {ProtocolConstants.MaxComposeBodyBytes} bytes.");
            }

            bodyBase64 = Convert.ToBase64String(File.ReadAllBytes(fileInfo.FullName));
        }

        RequestInput.ValidateBody(bodyBase64);
        var queued = await _bridgeClient.SendAsync<SendRequestRequest, QueuedResponse>(
            Operations.SendRequest,
            new SendRequestRequest
            {
                Url = url,
                Method = method,
                Headers = RequestInput.ParseHeaders(headers),
                BodyBase64 = bodyBase64
            },
            cancellationToken).ConfigureAwait(false);
        return await WaitForQueuedRequest(queued, wait, timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
    }

    public Task<SessionDiffResponse> DiffSessions(int leftSessionId, int rightSessionId, CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<DiffSessionsRequest, SessionDiffResponse>(
            Operations.DiffSessions,
            new DiffSessionsRequest { LeftSessionId = leftSessionId, RightSessionId = rightSessionId },
            cancellationToken);
    }

    public Task<ListWebSocketMessagesResponse> ListWebSocketMessages(
        int sessionId,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<ListWebSocketMessagesRequest, ListWebSocketMessagesResponse>(
            Operations.ListWebSocketMessages,
            new ListWebSocketMessagesRequest { SessionId = sessionId, Offset = offset, Limit = limit },
            cancellationToken);
    }

    /// <summary>
    /// Streams a complete WebSocket frame payload from a requested offset to stdout or a file.
    /// </summary>
    /// <param name="sessionId">The captured tunnel session ID.</param>
    /// <param name="messageId">The frame ID returned by the list operation.</param>
    /// <param name="offset">The initial payload byte offset.</param>
    /// <param name="outputPath">The destination path or <c>-</c> for stdout.</param>
    /// <param name="cancellationToken">Cancels bridge reads and output writes.</param>
    public async Task<WebSocketWriteResult> WriteWebSocketMessage(
        int sessionId,
        int messageId,
        long offset,
        string outputPath,
        CancellationToken cancellationToken)
    {
        Stream output = outputPath == "-"
            ? Console.OpenStandardOutput()
            : new FileStream(Path.GetFullPath(outputPath), FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
        await using (output.ConfigureAwait(false))
        {
            var currentOffset = offset;
            WebSocketMessageChunk? lastChunk = null;
            while (true)
            {
                lastChunk = await _bridgeClient.SendAsync<GetWebSocketMessageRequest, WebSocketMessageChunk>(
                    Operations.GetWebSocketMessage,
                    new GetWebSocketMessageRequest
                    {
                        SessionId = sessionId,
                        MessageId = messageId,
                        Offset = currentOffset,
                        Length = ProtocolConstants.CliBodyChunkBytes
                    },
                    cancellationToken).ConfigureAwait(false);
                var bytes = Convert.FromBase64String(lastChunk.Base64Data);
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                currentOffset += bytes.Length;
                if (lastChunk.EndOfMessage)
                {
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return new WebSocketWriteResult
                    {
                        Path = outputPath == "-" ? "-" : Path.GetFullPath(outputPath),
                        BytesWritten = currentOffset - offset,
                        Message = lastChunk
                    };
                }

                if (bytes.Length == 0)
                {
                    throw new InvalidDataException("The Fiddler bridge returned an empty WebSocket chunk before EOF.");
                }
            }
        }
    }

    public Task<ExportResult> ExportSession(
        int sessionId,
        string format,
        string outputPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        return _sessionExporter.ExportSingleAsync(sessionId, format, outputPath, overwrite, cancellationToken);
    }

    public Task<ExportResult> ExportHar(
        ListSessionsRequest filters,
        string outputPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        return _sessionExporter.ExportHarAsync(filters, outputPath, overwrite, cancellationToken);
    }

    public Task<AutoResponderStatusResponse> GetAutoResponder(CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<EmptyRequest, AutoResponderStatusResponse>(
            Operations.GetAutoResponder,
            new EmptyRequest(),
            cancellationToken);
    }

    public Task<AutoResponderStatusResponse> ConfigureAutoResponder(
        ConfigureAutoResponderRequest request,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<ConfigureAutoResponderRequest, AutoResponderStatusResponse>(
            Operations.ConfigureAutoResponder,
            request,
            cancellationToken);
    }

    public Task<ListAutoResponderRulesResponse> ListAutoResponderRules(CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<EmptyRequest, ListAutoResponderRulesResponse>(
            Operations.ListAutoResponderRules,
            new EmptyRequest(),
            cancellationToken);
    }

    public Task<AutoResponderMutationResponse> AddAutoResponderRule(
        AddAutoResponderRuleRequest request,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<AddAutoResponderRuleRequest, AutoResponderMutationResponse>(
            Operations.AddAutoResponderRule,
            request,
            cancellationToken);
    }

    public Task<AutoResponderMutationResponse> UpdateAutoResponderRule(
        UpdateAutoResponderRuleRequest request,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<UpdateAutoResponderRuleRequest, AutoResponderMutationResponse>(
            Operations.UpdateAutoResponderRule,
            request,
            cancellationToken);
    }

    public Task<AutoResponderMutationResponse> MoveAutoResponderRule(
        MoveAutoResponderRuleRequest request,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<MoveAutoResponderRuleRequest, AutoResponderMutationResponse>(
            Operations.MoveAutoResponderRule,
            request,
            cancellationToken);
    }

    public Task<AutoResponderMutationResponse> RemoveAutoResponderRule(
        string ruleId,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<RemoveAutoResponderRuleRequest, AutoResponderMutationResponse>(
            Operations.RemoveAutoResponderRule,
            new RemoveAutoResponderRuleRequest { RuleId = ruleId, Confirm = true },
            cancellationToken);
    }

    public Task<AutoResponderMutationResponse> ClearAutoResponderRules(CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<ClearAutoResponderRulesRequest, AutoResponderMutationResponse>(
            Operations.ClearAutoResponderRules,
            new ClearAutoResponderRulesRequest { Confirm = true },
            cancellationToken);
    }

    public Task<AutoResponderRulesFileResponse> SaveAutoResponderRules(
        string path,
        bool overwrite,
        bool confirmOverwrite,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<SaveAutoResponderRulesRequest, AutoResponderRulesFileResponse>(
            Operations.SaveAutoResponderRules,
            new SaveAutoResponderRulesRequest
            {
                Path = path,
                Overwrite = overwrite,
                ConfirmOverwrite = confirmOverwrite
            },
            cancellationToken);
    }

    public Task<AutoResponderRulesFileResponse> LoadAutoResponderRules(
        string path,
        bool replace,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<LoadAutoResponderRulesRequest, AutoResponderRulesFileResponse>(
            Operations.LoadAutoResponderRules,
            new LoadAutoResponderRulesRequest { Path = path, Replace = replace, ConfirmReplace = replace },
            cancellationToken);
    }

    public Task<ListBreakpointArmsResponse> ListBreakpointArms(CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<EmptyRequest, ListBreakpointArmsResponse>(
            Operations.ListBreakpointArms,
            new EmptyRequest(),
            cancellationToken);
    }

    public Task<BreakpointArmMutationResponse> ArmBreakpoint(
        ArmBreakpointRequest request,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<ArmBreakpointRequest, BreakpointArmMutationResponse>(
            Operations.ArmBreakpoint,
            request,
            cancellationToken);
    }

    public Task<BreakpointArmMutationResponse> DisarmBreakpoint(
        string armId,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<DisarmBreakpointRequest, BreakpointArmMutationResponse>(
            Operations.DisarmBreakpoint,
            new DisarmBreakpointRequest { ArmId = armId },
            cancellationToken);
    }

    public Task<ListPendingBreakpointsResponse> ListPendingBreakpoints(
        ListPendingBreakpointsRequest request,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<ListPendingBreakpointsRequest, ListPendingBreakpointsResponse>(
            Operations.ListPendingBreakpoints,
            request,
            cancellationToken);
    }

    public Task<WaitForBreakpointResponse> WaitForBreakpoint(
        WaitForBreakpointRequest request,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<WaitForBreakpointRequest, WaitForBreakpointResponse>(
            Operations.WaitForBreakpoint,
            request,
            cancellationToken);
    }

    public Task<BreakpointDetailsResponse> GetBreakpoint(
        string breakpointId,
        bool includeHeaders,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<GetBreakpointRequest, BreakpointDetailsResponse>(
            Operations.GetBreakpoint,
            new GetBreakpointRequest { BreakpointId = breakpointId, IncludeHeaders = includeHeaders },
            cancellationToken);
    }

    public Task<BreakpointDetailsResponse> UpdateBreakpoint(
        UpdateBreakpointRequest request,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<UpdateBreakpointRequest, BreakpointDetailsResponse>(
            Operations.UpdateBreakpoint,
            request,
            cancellationToken);
    }

    public Task<BreakpointActionResponse> ResumeBreakpoint(
        string breakpointId,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<BreakpointActionRequest, BreakpointActionResponse>(
            Operations.ResumeBreakpoint,
            new BreakpointActionRequest { BreakpointId = breakpointId },
            cancellationToken);
    }

    public Task<BreakpointActionResponse> AbortBreakpoint(
        string breakpointId,
        CancellationToken cancellationToken)
    {
        return _bridgeClient.SendAsync<BreakpointActionRequest, BreakpointActionResponse>(
            Operations.AbortBreakpoint,
            new BreakpointActionRequest { BreakpointId = breakpointId, Confirm = true },
            cancellationToken);
    }

    private async Task<int> GetLatestSessionId(CancellationToken cancellationToken)
    {
        var response = await ListSessions(
            new ListSessionsRequest { Limit = 1, NewestFirst = true },
            cancellationToken).ConfigureAwait(false);
        return response.Sessions.Count == 0 ? 0 : response.Sessions[0].Id;
    }

    private async Task<CliQueuedResult> WaitForQueuedRequest(
        QueuedResponse queued,
        bool wait,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        if (!wait)
        {
            return new CliQueuedResult { Queued = queued };
        }

        var result = await _bridgeClient.SendAsync<WaitForSessionRequest, WaitForSessionResponse>(
            Operations.WaitForSession,
            new WaitForSessionRequest
            {
                AfterId = queued.BaselineSessionId,
                TimeoutMilliseconds = timeoutMilliseconds,
                Filters = new ListSessionsRequest
                {
                    Method = queued.ExpectedMethod,
                    UrlContains = queued.ExpectedUrl
                }
            },
            cancellationToken).ConfigureAwait(false);
        return new CliQueuedResult
        {
            Queued = queued,
            TimedOut = !result.Matched,
            CapturedSession = result.Session
        };
    }

    public IReadOnlyList<string> InstallBridge()
    {
        return _bridgeInstaller.Install();
    }

    public IReadOnlyList<string> UninstallBridge()
    {
        return _bridgeInstaller.Uninstall();
    }

    public HostConfiguration GetConfiguration()
    {
        return _configStore.GetOrCreate();
    }

    public HostConfiguration RotateToken()
    {
        return _configStore.RotateToken();
    }
}

internal sealed record DoctorCheck(string Name, bool Passed, string Details);

internal sealed class DoctorResult
{
    public bool Healthy { get; set; }
    public IReadOnlyList<DoctorCheck> Checks { get; set; } = Array.Empty<DoctorCheck>();
}

internal sealed class BodyWriteResult
{
    public string Path { get; set; } = string.Empty;
    public long BytesWritten { get; set; }
    public string? ContentType { get; set; }
}

internal sealed class CliQueuedResult
{
    public QueuedResponse Queued { get; set; } = new();
    public bool TimedOut { get; set; }
    public SessionSummary? CapturedSession { get; set; }
}

internal sealed class WebSocketWriteResult
{
    public string Path { get; set; } = string.Empty;
    public long BytesWritten { get; set; }
    public WebSocketMessageChunk Message { get; set; } = new();
}
