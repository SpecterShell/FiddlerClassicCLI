// Persists managed HTTP settings and credentials with current-user filesystem permissions.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Services;

internal sealed partial class ConfigStore
{
    public const int MaximumNamedClients = 64;
    public const int MaximumClientNameLength = 64;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _configDirectory;

    public ConfigStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FiddlerClassicCLI"))
    {
    }

    internal ConfigStore(string configDirectory)
    {
        _configDirectory = configDirectory;
    }

    public string ConfigDirectory => _configDirectory;

    public string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public string HostLaunchRecordPath => Path.Combine(ConfigDirectory, "bridge-host.json");

    /// <summary>
    /// Loads a usable configuration and fills additive defaults for older configuration files.
    /// </summary>
    public HostConfiguration GetOrCreate()
    {
        return ReadConsistently(static configuration => configuration);
    }

    /// <summary>Reads and projects configuration while holding the cross-process transaction lock.</summary>
    /// <typeparam name="T">The snapshot or metadata returned by the reader.</typeparam>
    /// <param name="read">A synchronous projection that must not await or mutate the configuration.</param>
    internal T ReadConsistently<T>(Func<HostConfiguration, T> read)
    {
        using (ConfigFileLock.Acquire(ConfigPath))
        {
            return read(GetOrCreateUnsafe());
        }
    }

    public HostConfiguration RotateToken()
    {
        using (ConfigFileLock.Acquire(ConfigPath))
        {
            var configuration = GetOrCreateUnsafe();
            configuration.HttpBearerToken = GenerateToken();
            configuration.HttpDefaultTokenCreatedAtUtc = DateTime.UtcNow.ToString("O");
            return SaveUnsafe(configuration);
        }
    }

    public AuthorizedClientSecret AuthorizeClient(string name)
    {
        using (ConfigFileLock.Acquire(ConfigPath))
        {
            var normalizedName = (name ?? string.Empty).Trim();
            if (normalizedName.Length is < 1 or > MaximumClientNameLength)
            {
                throw new HttpAdministrationException(
                    ErrorCodes.InvalidRequest,
                    $"Client name must contain between 1 and {MaximumClientNameLength} characters.");
            }

            var configuration = GetOrCreateUnsafe();
            if (configuration.AuthorizedHttpClients.Count >= MaximumNamedClients)
            {
                throw new HttpAdministrationException(
                    ErrorCodes.Conflict,
                    $"At most {MaximumNamedClients} named HTTP clients may be authorized.");
            }

            if (string.Equals(normalizedName, "Default CLI token", StringComparison.OrdinalIgnoreCase)
                || configuration.AuthorizedHttpClients.Any(client =>
                    string.Equals(client.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new HttpAdministrationException(
                    ErrorCodes.Conflict,
                    $"An HTTP client named '{normalizedName}' already exists.");
            }

            var token = GenerateToken();
            var client = new AuthorizedHttpClientConfiguration
            {
                ClientId = Guid.NewGuid().ToString("N"),
                Name = normalizedName,
                TokenSha256 = HashToken(token),
                CreatedAtUtc = DateTime.UtcNow.ToString("O")
            };
            configuration.AuthorizedHttpClients.Add(client);
            SaveUnsafe(configuration);
            return new AuthorizedClientSecret(client, token);
        }
    }

    public DeauthorizedClient DeauthorizeClient(string clientId)
    {
        using (ConfigFileLock.Acquire(ConfigPath))
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw new HttpAdministrationException(ErrorCodes.InvalidRequest, "Client ID is required.");
            }

            var configuration = GetOrCreateUnsafe();
            if (string.Equals(clientId, HttpClientIds.Default, StringComparison.Ordinal))
            {
                configuration.HttpBearerToken = GenerateToken();
                configuration.HttpDefaultTokenCreatedAtUtc = DateTime.UtcNow.ToString("O");
                SaveUnsafe(configuration);
                return new DeauthorizedClient(HttpClientIds.Default, true);
            }

            var client = configuration.AuthorizedHttpClients.FirstOrDefault(candidate =>
                string.Equals(candidate.ClientId, clientId, StringComparison.Ordinal));
            if (client is null)
            {
                throw new HttpAdministrationException(
                    ErrorCodes.NotFound,
                    $"Authorized HTTP client '{clientId}' was not found.");
            }

            configuration.AuthorizedHttpClients.Remove(client);
            SaveUnsafe(configuration);
            return new DeauthorizedClient(client.ClientId, false);
        }
    }

    internal HostConfiguration Save(HostConfiguration configuration)
    {
        using (ConfigFileLock.Acquire(ConfigPath))
        {
            return SaveUnsafe(configuration);
        }
    }

    internal static string GenerateToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static string HashToken(string token)
    {
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    internal static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new HttpAdministrationException(ErrorCodes.InvalidRequest, "Port must be between 1 and 65535.");
        }
    }

    private HostConfiguration GetOrCreateUnsafe()
    {
        if (!File.Exists(ConfigPath))
        {
            return SaveUnsafe(CreateDefault());
        }

        using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var existing = document.RootElement.Deserialize<HostConfiguration>(JsonOptions)
            ?? throw new InvalidDataException("The Fiddler Classic CLI configuration is empty.");
        var changed = Normalize(existing);
        // Preserve an explicit earlier preference without retaining the superseded field in new writes.
        if (!document.RootElement.TryGetProperty(nameof(HostConfiguration.HttpAuthenticationMode), out _)
            && document.RootElement.TryGetProperty("HttpRequireAuthentication", out var legacyAuthentication))
        {
            existing.HttpAuthenticationMode = legacyAuthentication.GetBoolean()
                ? HttpAuthenticationModes.Required : HttpAuthenticationModes.None;
            changed = true;
        }
        if (changed)
        {
            return SaveUnsafe(existing);
        }

        Validate(existing);
        return existing;
    }

    private HostConfiguration SaveUnsafe(HostConfiguration configuration)
    {
        Validate(configuration);
        CurrentUserFileSecurity.WriteAllTextAtomically(
            ConfigPath,
            JsonSerializer.Serialize(configuration, JsonOptions));
        return configuration;
    }

    private static HostConfiguration CreateDefault()
    {
        return new HostConfiguration
        {
            HttpBearerToken = GenerateToken(),
            HttpDefaultTokenCreatedAtUtc = DateTime.UtcNow.ToString("O")
        };
    }

    private static bool Normalize(HostConfiguration configuration)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(configuration.HttpBearerToken))
        {
            configuration.HttpBearerToken = GenerateToken();
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(configuration.HttpDefaultTokenCreatedAtUtc))
        {
            configuration.HttpDefaultTokenCreatedAtUtc = DateTime.UtcNow.ToString("O");
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(configuration.HttpBindMode))
        {
            configuration.HttpBindMode = HttpBindModes.Loopback;
            changed = true;
        }

        if (configuration.AuthorizedHttpClients is null)
        {
            configuration.AuthorizedHttpClients = new List<AuthorizedHttpClientConfiguration>();
            changed = true;
        }

        return changed;
    }

    private static void Validate(HostConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.HttpBearerToken))
        {
            throw new InvalidDataException("The default HTTP bearer token is missing.");
        }

        ValidatePort(configuration.HttpPort);
        HttpListenerSettings.Validate(configuration);
        if (configuration.AuthorizedHttpClients.Count > MaximumNamedClients)
        {
            throw new InvalidDataException($"The HTTP client list exceeds {MaximumNamedClients} entries.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Default CLI token" };
        foreach (var client in configuration.AuthorizedHttpClients)
        {
            if (string.IsNullOrWhiteSpace(client.ClientId)
                || !ids.Add(client.ClientId)
                || string.IsNullOrWhiteSpace(client.Name)
                || client.Name.Length > MaximumClientNameLength
                || !names.Add(client.Name)
                || !IsSha256Hash(client.TokenSha256))
            {
                throw new InvalidDataException("The authorized HTTP client configuration is invalid.");
            }
        }
    }

    private static bool IsSha256Hash(string value)
    {
        try
        {
            return Convert.FromBase64String(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal static class HttpClientIds
{
    public const string Default = "default";
}

internal sealed class HostConfiguration
{
    public int HttpPort { get; set; } = 8877;
    public string HttpBearerToken { get; set; } = string.Empty;
    public string HttpDefaultTokenCreatedAtUtc { get; set; } = string.Empty;
    public bool HttpServiceEnabled { get; set; }
    public string HttpBindMode { get; set; } = HttpBindModes.Loopback;
    public string[] HttpBindAddresses { get; set; } = Array.Empty<string>();
    public string HttpStartupMode { get; set; } = HttpStartupModes.LastState;
    public string HttpAuthenticationMode { get; set; } = HttpAuthenticationModes.NonLoopback;
    public List<AuthorizedHttpClientConfiguration> AuthorizedHttpClients { get; set; } = new();
}

internal sealed class AuthorizedHttpClientConfiguration
{
    public string ClientId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TokenSha256 { get; set; } = string.Empty;
    public string CreatedAtUtc { get; set; } = string.Empty;
}

internal sealed record AuthorizedClientSecret(AuthorizedHttpClientConfiguration Client, string Token);

internal sealed record DeauthorizedClient(string ClientId, bool DefaultTokenRotated);
