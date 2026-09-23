// Verifies persistent HTTP settings, default-token compatibility, and named-client authentication.
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FiddlerClassicCLI.Host.Mcp;
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed class ConfigAndAuthTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FiddlerClassicCLITests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Verifies token creation is persistent, URL-safe, and replaced during explicit rotation.
    /// </summary>
    [Fact]
    public void CreatesAndRotatesCryptographicToken()
    {
        var store = new ConfigStore(_directory);
        var first = store.GetOrCreate();
        var loaded = store.GetOrCreate();
        var rotated = store.RotateToken();

        Assert.Equal(first.HttpBearerToken, loaded.HttpBearerToken);
        Assert.NotEqual(first.HttpBearerToken, rotated.HttpBearerToken);
        Assert.True(first.HttpBearerToken.Length >= 40);
        Assert.DoesNotContain("=", first.HttpBearerToken);
        Assert.True(File.Exists(store.ConfigPath));
    }

    [Fact]
    public void MigratesExistingConfigurationToSafeManagedHttpDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "config.json"),
            """
            {
              "HttpPort": 9001,
              "HttpBearerToken": "existing-secret"
            }
            """);

        var configuration = new ConfigStore(_directory).GetOrCreate();

        Assert.Equal(9001, configuration.HttpPort);
        Assert.False(configuration.HttpServiceEnabled);
        Assert.Equal(FiddlerClassicCLI.Protocol.HttpBindModes.Loopback, configuration.HttpBindMode);
        Assert.Empty(configuration.AuthorizedHttpClients);
        Assert.Equal(HttpAuthenticationModes.NonLoopback, configuration.HttpAuthenticationMode);
        Assert.Equal(HttpStartupModes.LastState, configuration.HttpStartupMode);
        Assert.Empty(configuration.HttpBindAddresses);
        Assert.False(string.IsNullOrWhiteSpace(configuration.HttpDefaultTokenCreatedAtUtc));
    }

    [Fact]
    public void NamedClientTokenIsShownOnceAndOnlyItsHashIsStored()
    {
        var store = new ConfigStore(_directory);
        var authorized = store.AuthorizeClient("Build agent");
        var rawConfiguration = File.ReadAllText(store.ConfigPath);
        var credentials = new HttpCredentialManager(store);

        Assert.DoesNotContain(authorized.Token, rawConfiguration, StringComparison.Ordinal);
        Assert.Contains(
            store.GetOrCreate().AuthorizedHttpClients,
            client => client.TokenSha256 == authorized.Client.TokenSha256);
        var identity = credentials.Authenticate("Bearer " + authorized.Token);
        Assert.NotNull(identity);
        Assert.Equal(authorized.Client.ClientId, identity.ClientId);

        store.DeauthorizeClient(authorized.Client.ClientId);
        credentials.Reload();
        Assert.Null(credentials.Authenticate("Bearer " + authorized.Token));
    }

    [Fact]
    public void RejectsDuplicateClientNamesAndRotatesTheDefaultClient()
    {
        var store = new ConfigStore(_directory);
        store.AuthorizeClient("Agent");
        var duplicate = Assert.Throws<HttpAdministrationException>(() => store.AuthorizeClient("agent"));
        var previousDefault = store.GetOrCreate().HttpBearerToken;

        var result = store.DeauthorizeClient(HttpClientIds.Default);

        Assert.Equal(FiddlerClassicCLI.Protocol.ErrorCodes.Conflict, duplicate.Code);
        Assert.True(result.DefaultTokenRotated);
        Assert.NotEqual(previousDefault, store.GetOrCreate().HttpBearerToken);
    }

    [Fact]
    public void EnforcesClientNameAndCountLimits()
    {
        var store = new ConfigStore(_directory);
        Assert.Equal(ErrorCodes.InvalidRequest, Assert.Throws<HttpAdministrationException>(
            () => store.AuthorizeClient(" ")).Code);
        Assert.Equal(ErrorCodes.InvalidRequest, Assert.Throws<HttpAdministrationException>(
            () => store.AuthorizeClient(new string('x', ConfigStore.MaximumClientNameLength + 1))).Code);

        for (var index = 0; index < ConfigStore.MaximumNamedClients; index++)
        {
            store.AuthorizeClient($"client-{index}");
        }

        Assert.Equal(ErrorCodes.Conflict, Assert.Throws<HttpAdministrationException>(
            () => store.AuthorizeClient("one-too-many")).Code);
    }

    [Fact]
    public void RejectsMalformedStoredHashes()
    {
        var store = new ConfigStore(_directory);
        var configuration = store.GetOrCreate();
        configuration.AuthorizedHttpClients.Add(new AuthorizedHttpClientConfiguration
        {
            ClientId = "client-id",
            Name = "client",
            TokenSha256 = "not-a-sha256-hash",
            CreatedAtUtc = DateTime.UtcNow.ToString("O")
        });

        Assert.Throws<InvalidDataException>(() => store.Save(configuration));

        File.WriteAllText(
            store.ConfigPath,
            System.Text.Json.JsonSerializer.Serialize(configuration));
        Assert.Throws<InvalidDataException>(() => new ConfigStore(_directory).GetOrCreate());
    }

    [Fact]
    public void WritesConfigurationAtomicallyWithCurrentUserAcl()
    {
        var store = new ConfigStore(_directory);
        store.GetOrCreate();
        store.RotateToken();

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp.*"));
        if (OperatingSystem.IsWindows())
        {
            VerifyCurrentUserAcl(store.ConfigPath);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyCurrentUserAcl(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        var currentSid = WindowsIdentity.GetCurrent().User;
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        Assert.True(security.AreAccessRulesProtected);
        Assert.NotNull(currentSid);
        Assert.All(rules, rule => Assert.Equal(currentSid, rule.IdentityReference));
        Assert.Contains(rules, rule =>
            rule.AccessControlType == AccessControlType.Allow
            && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Basic secret", false)]
    [InlineData("Bearer wrong", false)]
    [InlineData("Bearer secret", true)]
    [InlineData("bearer secret", true)]
    public void ValidatesBearerHeader(string? header, bool expected)
    {
        Assert.Equal(expected, McpHost.IsValidBearerValue(header, "secret"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
