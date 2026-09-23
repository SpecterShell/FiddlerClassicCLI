// Checks atomic listener preferences, bounded address validation, and explicit exposure consent.
using FiddlerClassicCLI.Host.Services;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed class HttpListenerConfigurationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("fiddler-http-settings-").FullName;

    [Fact]
    public void DefaultsAllowLoopbackWithoutAuthenticationAndRememberLastState()
    {
        var configuration = new ConfigStore(_directory).GetOrCreate();
        Assert.False(configuration.HttpServiceEnabled);
        Assert.Equal(HttpBindModes.Loopback, configuration.HttpBindMode);
        Assert.Empty(configuration.HttpBindAddresses);
        Assert.Equal(HttpAuthenticationModes.NonLoopback, configuration.HttpAuthenticationMode);
        Assert.Equal(HttpStartupModes.LastState, configuration.HttpStartupMode);
    }

    [Theory]
    [InlineData(HttpAuthenticationModes.Required)]
    [InlineData(HttpAuthenticationModes.NonLoopback)]
    [InlineData(HttpAuthenticationModes.None)]
    public void AuthenticationModesRoundTripWithoutChangingBindingsOrCredentials(string mode)
    {
        var store = new ConfigStore(_directory);
        var initial = store.GetOrCreate();
        store.ConfigureHttpService(new ConfigureHttpServiceRequest
            { AuthenticationMode = mode, Confirm = mode == HttpAuthenticationModes.None });
        var saved = new ConfigStore(_directory).GetOrCreate();
        Assert.Equal(mode, saved.HttpAuthenticationMode);
        Assert.Equal(initial.HttpPort, saved.HttpPort);
        Assert.Equal(initial.HttpBindMode, saved.HttpBindMode);
        Assert.Equal(initial.HttpBearerToken, saved.HttpBearerToken);
        Assert.False(saved.HttpServiceEnabled);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("NON-LOOPBACK")]
    public void RejectsUnknownAuthenticationModesWithoutSaving(string mode)
    {
        var store = new ConfigStore(_directory);
        store.GetOrCreate();
        var original = File.ReadAllBytes(store.ConfigPath);
        Assert.Equal(ErrorCodes.InvalidRequest, Assert.Throws<HttpAdministrationException>(() =>
            store.ConfigureHttpService(new ConfigureHttpServiceRequest { AuthenticationMode = mode, Confirm = true })).Code);
        Assert.Equal(original, File.ReadAllBytes(store.ConfigPath));
    }

    [Theory]
    [InlineData(false, HttpAuthenticationModes.None)]
    [InlineData(true, HttpAuthenticationModes.Required)]
    public void PreservesExplicitLegacyAuthenticationChoice(bool required, string expected)
    {
        var store = new ConfigStore(_directory);
        var initial = store.GetOrCreate();
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.ConfigPath))!.AsObject();
        document.Remove("HttpAuthenticationMode");
        document["HttpRequireAuthentication"] = required;
        File.WriteAllText(store.ConfigPath, document.ToJsonString());
        var saved = store.GetOrCreate();
        Assert.Equal(expected, saved.HttpAuthenticationMode);
        Assert.Equal(initial.HttpBearerToken, saved.HttpBearerToken);
        Assert.Equal(expected, new ConfigStore(_directory).GetOrCreate().HttpAuthenticationMode);
        Assert.DoesNotContain("HttpRequireAuthentication", File.ReadAllText(store.ConfigPath));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.1")]
    [InlineData("2130706433")]
    [InlineData(" 127.0.0.1")]
    [InlineData("127.000.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData(null)]
    public void RejectsNonCanonicalOrNonUnicastSelections(string? address)
    {
        var store = new ConfigStore(_directory);
        var error = Assert.Throws<HttpAdministrationException>(() => store.ConfigureHttpService(
            new ConfigureHttpServiceRequest { BindMode = HttpBindModes.Selected, BindAddresses = [address!] }));
        Assert.Equal(ErrorCodes.InvalidRequest, error.Code);
        Assert.Equal(HttpBindModes.Loopback, store.GetOrCreate().HttpBindMode);
    }

    [Fact]
    public void BoundsAndDeduplicatesSelectionsAndClearsThemOnModeChange()
    {
        var store = new ConfigStore(_directory);
        foreach (var addresses in new[] { Array.Empty<string>(), ["127.0.0.1", "127.0.0.1"],
                     Enumerable.Range(1, 17).Select(index => $"10.0.0.{index}").ToArray() })
            Assert.Throws<HttpAdministrationException>(() => store.ConfigureHttpService(
                new ConfigureHttpServiceRequest { BindMode = HttpBindModes.Selected, BindAddresses = addresses }));
        Assert.Throws<HttpAdministrationException>(() => store.ConfigureHttpService(
            new ConfigureHttpServiceRequest { BindMode = HttpBindModes.All, BindAddresses = ["10.0.0.1"] }));
        var selected = new[] { "127.0.0.1", "10.0.0.2" };
        store.ConfigureHttpService(new ConfigureHttpServiceRequest { BindMode = HttpBindModes.Selected, BindAddresses = selected });
        selected[0] = "10.0.0.8";
        Assert.Equal(new[] { "127.0.0.1", "10.0.0.2" }, store.GetOrCreate().HttpBindAddresses);
        store.ConfigureHttpService(new ConfigureHttpServiceRequest { BindMode = HttpBindModes.All });
        Assert.Empty(store.GetOrCreate().HttpBindAddresses);
    }

    [Fact]
    public void SecurityConfirmationAndValidationFailuresNeverPartiallySave()
    {
        var store = new ConfigStore(_directory);
        store.AuthorizeClient("Preserved client");
        var before = File.ReadAllBytes(store.ConfigPath);
        var error = Assert.Throws<HttpAdministrationException>(() => store.ConfigureHttpService(
            new ConfigureHttpServiceRequest { Port = 9999, AuthenticationMode = HttpAuthenticationModes.None }));
        Assert.Equal(ErrorCodes.ConfirmationRequired, error.Code);
        Assert.True(before.SequenceEqual(File.ReadAllBytes(store.ConfigPath)));
        Assert.Throws<HttpAdministrationException>(() => store.ConfigureHttpService(
            new ConfigureHttpServiceRequest { Port = 0, AuthenticationMode = HttpAuthenticationModes.None, Confirm = true }));
        Assert.True(before.SequenceEqual(File.ReadAllBytes(store.ConfigPath)));
        var configuration = store.ConfigureHttpService(new ConfigureHttpServiceRequest { AuthenticationMode = HttpAuthenticationModes.None, Confirm = true });
        Assert.Equal(HttpAuthenticationModes.None, configuration.HttpAuthenticationMode);
        Assert.Single(configuration.AuthorizedHttpClients);
        Assert.Equal(ErrorCodes.ConfirmationRequired, Assert.Throws<HttpAdministrationException>(() => store.SetHttpServiceEnabled(true)).Code);
        Assert.True(store.SetHttpServiceEnabled(true, confirm: true).HttpServiceEnabled);
    }

    [Fact]
    public void RequiresConfirmationForRemoteStartupAndLaterBindingChanges()
    {
        var store = new ConfigStore(_directory);
        Assert.Equal(ErrorCodes.ConfirmationRequired, Assert.Throws<HttpAdministrationException>(() => store.ConfigureHttpService(
            new ConfigureHttpServiceRequest { BindMode = HttpBindModes.All, StartupMode = HttpStartupModes.Enabled })).Code);
        store.ConfigureHttpService(new ConfigureHttpServiceRequest { StartupMode = HttpStartupModes.Enabled });
        Assert.Equal(ErrorCodes.ConfirmationRequired, Assert.Throws<HttpAdministrationException>(() => store.ConfigureHttpService(
            new ConfigureHttpServiceRequest { BindMode = HttpBindModes.Selected, BindAddresses = ["10.0.0.1"] })).Code);
        var configuration = store.ConfigureHttpService(new ConfigureHttpServiceRequest
        {
            BindMode = HttpBindModes.Selected, BindAddresses = ["10.0.0.1"], Confirm = true
        });
        Assert.Equal(HttpStartupModes.Enabled, configuration.HttpStartupMode);
        Assert.False(configuration.HttpServiceEnabled);
    }

    [Fact]
    public void RunningServiceAllowsOnlyStartupPreferenceChanges()
    {
        var store = new ConfigStore(_directory);
        store.SetHttpServiceEnabled(true);
        var updated = store.ConfigureHttpService(new ConfigureHttpServiceRequest { StartupMode = HttpStartupModes.Disabled });
        Assert.True(updated.HttpServiceEnabled);
        Assert.Equal(HttpStartupModes.Disabled, updated.HttpStartupMode);
        foreach (var request in new[] { new ConfigureHttpServiceRequest { Port = 9000 },
                     new ConfigureHttpServiceRequest { BindMode = HttpBindModes.All },
                     new ConfigureHttpServiceRequest { AuthenticationMode = HttpAuthenticationModes.None, Confirm = true } })
            Assert.Equal(ErrorCodes.Conflict, Assert.Throws<HttpAdministrationException>(() => store.ConfigureHttpService(request)).Code);
    }

    [Theory]
    [InlineData(HttpStartupModes.LastState, false, false)]
    [InlineData(HttpStartupModes.LastState, true, true)]
    [InlineData(HttpStartupModes.Enabled, false, true)]
    [InlineData(HttpStartupModes.Enabled, true, true)]
    [InlineData(HttpStartupModes.Disabled, false, false)]
    [InlineData(HttpStartupModes.Disabled, true, false)]
    public void StartupPolicyAppliesAtomicallyAndKeepsItsPreference(string mode, bool previous, bool expected)
    {
        var store = new ConfigStore(_directory);
        store.ConfigureHttpService(new ConfigureHttpServiceRequest { StartupMode = mode });
        store.SetHttpServiceEnabled(previous);
        var applied = store.ApplyHttpStartup();
        Assert.Equal(expected, applied.HttpServiceEnabled);
        Assert.Equal(mode, applied.HttpStartupMode);
        Assert.Equal(expected, store.GetOrCreate().HttpServiceEnabled);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("")]
    public void RejectsUnknownStartupPolicies(string mode)
    {
        Assert.Equal(ErrorCodes.InvalidRequest, Assert.Throws<HttpAdministrationException>(() =>
            new ConfigStore(_directory).ConfigureHttpService(new ConfigureHttpServiceRequest { StartupMode = mode })).Code);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
