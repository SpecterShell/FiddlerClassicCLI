// Verifies token persistence, rotation, and bearer-token validation.
using FiddlerClassic.Host.Mcp;
using FiddlerClassic.Host.Services;

namespace FiddlerClassic.Tests;

public sealed class ConfigAndAuthTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FiddlerClassicTests", Guid.NewGuid().ToString("N"));

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
