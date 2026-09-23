// Tracks bounded operational metadata for active Kestrel connections and can abort selected transports.
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Connections;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Mcp;

internal sealed class HttpConnectionRegistry
{
    private readonly ConcurrentDictionary<string, TrackedConnection> _connections = new(StringComparer.Ordinal);

    public int Count => _connections.Count;

    public async Task TrackAsync(ConnectionContext context, ConnectionDelegate next)
    {
        var connection = new TrackedConnection(context);
        _connections[context.ConnectionId] = connection;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            _connections.TryRemove(context.ConnectionId, out _);
        }
    }

    public void BeginRequest(string connectionId, HttpClientIdentity? identity, bool anonymous = false)
    {
        if (_connections.TryGetValue(connectionId, out var connection))
        {
            connection.BeginRequest(identity, anonymous);
        }
    }

    public void EndRequest(string connectionId)
    {
        if (_connections.TryGetValue(connectionId, out var connection))
        {
            connection.EndRequest();
        }
    }

    public ListHttpConnectionsResponse List()
    {
        return new ListHttpConnectionsResponse
        {
            Connections = _connections.Values
                .Select(connection => connection.Snapshot())
                .OrderBy(connection => connection.ConnectedAtUtc, StringComparer.Ordinal)
                .ToList()
        };
    }

    public int CountForClient(string clientId)
    {
        return _connections.Values.Count(connection => connection.HasClient(clientId));
    }

    public bool Disconnect(string connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
        {
            return false;
        }

        connection.Abort("Disconnected by the Fiddler Classic CLI administrator.");
        return true;
    }

    public int DisconnectClient(string clientId)
    {
        var matched = _connections.Values.Where(connection => connection.HasClient(clientId)).ToArray();
        foreach (var connection in matched)
        {
            connection.Abort("The HTTP client credential was deauthorized.");
        }

        return matched.Length;
    }

    public void DisconnectAll()
    {
        foreach (var connection in _connections.Values)
        {
            connection.Abort("The managed MCP HTTP service is stopping.");
        }
    }

    private sealed class TrackedConnection
    {
        private readonly object _sync = new();
        private readonly ConnectionContext _context;
        private readonly Dictionary<string, string> _clients = new(StringComparer.Ordinal);
        private readonly DateTime _connectedAtUtc = DateTime.UtcNow;
        private DateTime _lastActivityAtUtc = DateTime.UtcNow;
        private long _totalRequestCount;
        private int _activeRequestCount;
        private bool _rejected;
        private bool _anonymous;

        public TrackedConnection(ConnectionContext context)
        {
            _context = context;
        }

        public void BeginRequest(HttpClientIdentity? identity, bool anonymous)
        {
            lock (_sync)
            {
                _lastActivityAtUtc = DateTime.UtcNow;
                _totalRequestCount++;
                _activeRequestCount++;
                if (anonymous)
                {
                    _anonymous = true;
                }
                else if (identity is null)
                {
                    _rejected = true;
                }
                else
                {
                    _clients[identity.ClientId] = identity.Name;
                }
            }
        }

        public void EndRequest()
        {
            lock (_sync)
            {
                _lastActivityAtUtc = DateTime.UtcNow;
                if (_activeRequestCount > 0)
                {
                    _activeRequestCount--;
                }
            }
        }

        public bool HasClient(string clientId)
        {
            lock (_sync)
            {
                return _clients.ContainsKey(clientId);
            }
        }

        public HttpConnectionDto Snapshot()
        {
            lock (_sync)
            {
                return new HttpConnectionDto
                {
                    ConnectionId = _context.ConnectionId,
                    RemoteEndpoint = _context.RemoteEndPoint?.ToString() ?? "unknown",
                    AuthorizationState = AuthorizationState(),
                    ClientIds = _clients.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                    ClientNames = _clients.Values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                    ConnectedAtUtc = _connectedAtUtc.ToString("O"),
                    LastActivityAtUtc = _lastActivityAtUtc.ToString("O"),
                    TotalRequestCount = _totalRequestCount,
                    ActiveRequestCount = _activeRequestCount
                };
            }
        }

        public void Abort(string reason)
        {
            _context.Abort(new ConnectionAbortedException(reason));
        }

        private string AuthorizationState()
        {
            if (_clients.Count > 0 && _rejected)
            {
                return "mixed";
            }

            if (_clients.Count > 0)
            {
                return "authorized";
            }

            return _rejected ? "rejected" : _anonymous ? "anonymous" : "pending";
        }
    }
}
