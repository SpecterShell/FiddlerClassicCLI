// Hosts the current-user named pipe that exposes bridge operations from Fiddler Classic.
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Web.Script.Serialization;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed class BridgeServer : IDisposable
{
    private readonly Func<BridgeRequest, BridgeResponse> _dispatch;
    private readonly Action<string> _log;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private readonly ConcurrentDictionary<int, NamedPipeServerStream> _connections =
        new ConcurrentDictionary<int, NamedPipeServerStream>();
    private readonly object _listenerLock = new object();
    private NamedPipeServerStream? _listener;
    private Task? _acceptLoop;
    private int _nextConnectionId;
    private bool _disposed;
    private BridgePipeStatus _status;

    /// <summary>
    /// Creates a server that dispatches operations through the production bridge dispatcher.
    /// </summary>
    /// <param name="dispatcher">The operation dispatcher bound to Fiddler state.</param>
    public BridgeServer(BridgeDispatcher dispatcher)
        : this(
            (dispatcher ?? throw new ArgumentNullException(nameof(dispatcher))).Dispatch,
            message => Fiddler.FiddlerApplication.Log.LogString(message),
            PipeNames.ForCurrentUser())
    {
    }

    /// <summary>
    /// Creates a server with injected dispatch and logging delegates for isolated execution.
    /// </summary>
    /// <param name="dispatch">Handles deserialized bridge requests.</param>
    /// <param name="log">Receives listener and connection failures.</param>
    /// <param name="pipeName">The current-user pipe name to bind.</param>
    internal BridgeServer(Func<BridgeRequest, BridgeResponse> dispatch, Action<string> log, string pipeName)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? throw new ArgumentException("A pipe name is required.", nameof(pipeName)) : pipeName;
        _status = new BridgePipeStatus(_pipeName, BridgeListenerState.Stopped);
    }

    internal BridgePipeStatus Status
    {
        get { lock (_listenerLock) { return _status; } }
    }

    /// <summary>
    /// Starts the accept loop once and returns immediately.
    /// </summary>
    public void Start()
    {
        ThrowIfDisposed();
        if (_acceptLoop != null)
        {
            return;
        }

        SetStatus(BridgeListenerState.Starting);
        var cancellationToken = _shutdown.Token;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(cancellationToken));
    }

    /// <summary>
    /// Stops acceptance and closes the listener and all active client connections.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();

        lock (_listenerLock)
        {
            _status = new BridgePipeStatus(_pipeName, BridgeListenerState.Stopped);
            _listener?.Dispose();
            _listener = null;
        }

        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }

        _connections.Clear();
        _shutdown.Dispose();
    }

    /// <summary>
    /// Creates secured pipe instances and hands connected clients to independent handlers.
    /// </summary>
    /// <param name="cancellationToken">Stops listener creation and retry delays.</param>
    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? listener = null;
            try
            {
                listener = CreatePipe();
                lock (_listenerLock)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _listener = listener;
                    _status = new BridgePipeStatus(_pipeName, BridgeListenerState.Listening);
                }

                await listener.WaitForConnectionAsync().ConfigureAwait(false);

                lock (_listenerLock)
                {
                    if (ReferenceEquals(_listener, listener))
                    {
                        _listener = null;
                    }
                }

                var connectionId = Interlocked.Increment(ref _nextConnectionId);
                var connection = listener;
                _connections[connectionId] = connection;
                _ = Task.Run(() => HandleConnectionAsync(connectionId, connection, cancellationToken));
                listener = null;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                SetStatus(BridgeListenerState.Retrying, exception.Message);
                _log($"[FiddlerClassicCLI] Pipe listener error: {exception}");
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_listenerLock)
                {
                    if (ReferenceEquals(_listener, listener))
                    {
                        _listener = null;
                    }
                }
                listener?.Dispose();
            }
        }
    }

    /// <summary>
    /// Processes framed requests sequentially for one connection until EOF, cancellation, or transport failure.
    /// </summary>
    /// <param name="connectionId">The server-local ID used to track the active connection.</param>
    /// <param name="connection">The connected named-pipe stream.</param>
    /// <param name="cancellationToken">Stops reads and writes during server shutdown.</param>
    private async Task HandleConnectionAsync(
        int connectionId,
        NamedPipeServerStream connection,
        CancellationToken cancellationToken)
    {
        var serializer = CreateSerializer();
        try
        {
            while (connection.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                var requestJson = await FrameCodec.ReadAsync(connection, cancellationToken).ConfigureAwait(false);
                if (requestJson == null)
                {
                    return;
                }

                BridgeResponse response;
                try
                {
                    var request = serializer.Deserialize<BridgeRequest>(requestJson)
                        ?? throw new InvalidDataException("The request envelope was empty.");
                    response = _dispatch(request);
                }
                catch (Exception exception)
                {
                    response = new BridgeResponse
                    {
                        ProtocolVersion = ProtocolConstants.Version,
                        Success = false,
                        Error = new BridgeError
                        {
                            Code = ErrorCodes.InvalidRequest,
                            Message = exception.Message
                        }
                    };
                }

                await FrameCodec.WriteAsync(connection, serializer.Serialize(response), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            _log($"[FiddlerClassicCLI] Pipe connection error: {exception}");
        }
        finally
        {
            _connections.TryRemove(connectionId, out _);
            connection.Dispose();
        }
    }

    /// <summary>
    /// Creates an asynchronous pipe instance whose ACL grants access only to the current user.
    /// </summary>
    private NamedPipeServerStream CreatePipe()
    {
        var identity = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows identity has no security identifier.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            identity,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            64 * 1024,
            64 * 1024,
            security);
    }

    private static JavaScriptSerializer CreateSerializer()
    {
        return new JavaScriptSerializer
        {
            MaxJsonLength = ProtocolConstants.MaxFrameBytes,
            RecursionLimit = 64
        };
    }

    private void SetStatus(BridgeListenerState state, string? error = null)
    {
        lock (_listenerLock)
        {
            if (!_disposed)
            {
                _status = new BridgePipeStatus(_pipeName, state, error);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(BridgeServer));
        }
    }
}
