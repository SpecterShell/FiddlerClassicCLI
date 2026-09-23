// Authenticates default and named HTTP clients without retaining supplied bearer tokens.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using FiddlerClassicCLI.Host.Services;

namespace FiddlerClassicCLI.Host.Mcp;

internal interface IHttpCredentialProvider
{
    HttpClientIdentity? Authenticate(string? authorization);
}

internal sealed record HttpClientIdentity(string ClientId, string Name);

internal sealed class HttpCredentialManager : IHttpCredentialProvider
{
    private const string BearerPrefix = "Bearer ";
    private readonly ConfigStore _configStore;
    private readonly ConcurrentDictionary<string, DateTime> _lastSeen = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private DateTime _loadedWriteTimeUtc;
    private CredentialSnapshot? _snapshot;

    public HttpCredentialManager(ConfigStore configStore)
    {
        _configStore = configStore;
    }

    public HttpClientIdentity? Authenticate(string? authorization)
    {
        if (authorization is null || !authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorization[BearerPrefix.Length..];
        if (token.Length == 0)
        {
            return null;
        }

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        foreach (var credential in GetSnapshot().Credentials)
        {
            if (credential.TokenHash.Length == suppliedHash.Length
                && CryptographicOperations.FixedTimeEquals(credential.TokenHash, suppliedHash))
            {
                _lastSeen[credential.Identity.ClientId] = DateTime.UtcNow;
                return credential.Identity;
            }
        }

        return null;
    }

    public DateTime? GetLastSeen(string clientId)
    {
        return _lastSeen.TryGetValue(clientId, out var value) ? value : null;
    }

    public void Reload()
    {
        lock (_sync)
        {
            _snapshot = null;
            _loadedWriteTimeUtc = default;
        }
    }

    private CredentialSnapshot GetSnapshot()
    {
        var writeTime = File.Exists(_configStore.ConfigPath)
            ? File.GetLastWriteTimeUtc(_configStore.ConfigPath)
            : default;
        lock (_sync)
        {
            if (_snapshot is not null && writeTime == _loadedWriteTimeUtc)
            {
                return _snapshot;
            }

            _snapshot = _configStore.ReadConsistently(configuration =>
            {
                var credentials = new List<Credential>
            {
                new(
                    new HttpClientIdentity(HttpClientIds.Default, "Default CLI token"),
                    SHA256.HashData(Encoding.UTF8.GetBytes(configuration.HttpBearerToken)))
            };
                foreach (var client in configuration.AuthorizedHttpClients)
                {
                    try
                    {
                        credentials.Add(new Credential(
                            new HttpClientIdentity(client.ClientId, client.Name),
                            Convert.FromBase64String(client.TokenSha256)));
                    }
                    catch (FormatException)
                    {
                        // Configuration validation reports malformed hashes to administrative callers.
                    }
                }

                // The file stamp belongs to these credentials, even if rotation is waiting for this read.
                _loadedWriteTimeUtc = File.GetLastWriteTimeUtc(_configStore.ConfigPath);
                return new CredentialSnapshot(credentials);
            });
            return _snapshot;
        }
    }

    private sealed record Credential(HttpClientIdentity Identity, byte[] TokenHash);

    private sealed record CredentialSnapshot(IReadOnlyList<Credential> Credentials);
}

internal sealed class SingleTokenCredentialProvider : IHttpCredentialProvider
{
    private readonly byte[] _expectedHash;

    public SingleTokenCredentialProvider(string token)
    {
        _expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    public HttpClientIdentity? Authenticate(string? authorization)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(authorization[prefix.Length..]));
        return CryptographicOperations.FixedTimeEquals(_expectedHash, suppliedHash)
            ? new HttpClientIdentity(HttpClientIds.Default, "Default CLI token")
            : null;
    }
}
