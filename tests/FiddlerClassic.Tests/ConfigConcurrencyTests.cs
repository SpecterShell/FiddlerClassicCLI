// Exercises configuration transactions between independent stores, processes, and abandoned owners.
using System.Diagnostics;
using FiddlerClassic.Host.Services;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Tests;

public sealed class ConfigConcurrencyTests : IDisposable
{
    private const string WorkerDirectory = "FIDDLER_CLASSIC_CONFIG_TEST_DIRECTORY";
    private const string WorkerRole = "FIDDLER_CLASSIC_CONFIG_TEST_ROLE";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FiddlerClassicTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PreservesConcurrentProcessUpdates()
    {
        var store = new ConfigStore(_directory);
        store.GetOrCreate();
        var processes = new List<Process>();
        var outputs = new List<Task<string>>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            foreach (var role in new[] { "clients-a", "clients-b", "rotate", "configure" })
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add(typeof(ConfigConcurrencyTests).Assembly.Location);
                start.ArgumentList.Add("-method");
                start.ArgumentList.Add(typeof(ConfigConcurrencyTests).FullName + ".ConfigurationWorker");
                start.Environment[WorkerDirectory] = _directory;
                start.Environment[WorkerRole] = role;
                var process = Process.Start(start)!;
                processes.Add(process);
                outputs.Add(process.StandardOutput.ReadToEndAsync(deadline.Token));
                outputs.Add(process.StandardError.ReadToEndAsync(deadline.Token));
            }

            while (Directory.GetFiles(_directory, "ready-*").Length != processes.Count)
            {
                Assert.DoesNotContain(processes, process => process.HasExited);
                await Task.Delay(20, deadline.Token);
            }

            File.WriteAllText(Path.Combine(_directory, "start"), string.Empty);
            await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(deadline.Token)));
            await Task.WhenAll(outputs);
            Assert.All(processes, process => Assert.Equal(0, process.ExitCode));

            var configuration = store.GetOrCreate();
            Assert.Equal(24, configuration.AuthorizedHttpClients.Count);
            Assert.Equal(24, configuration.AuthorizedHttpClients.Select(client => client.Name).Distinct().Count());
            Assert.Equal(9023, configuration.HttpPort);
            Assert.Equal(HttpBindModes.All, configuration.HttpBindMode);
            Assert.Equal(File.ReadAllText(Path.Combine(_directory, "rotated-hash")),
                ConfigStore.HashToken(configuration.HttpBearerToken));
            Assert.Empty(Directory.GetFiles(_directory, "*.tmp.*"));
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                process.Dispose();
            }
        }
    }

    // Invoked in a separate test process with an explicit method filter; inert in the normal suite.
    [Fact]
    public void ConfigurationWorker()
    {
        var directory = Environment.GetEnvironmentVariable(WorkerDirectory);
        if (directory is null)
        {
            return;
        }

        var role = Environment.GetEnvironmentVariable(WorkerRole)!;
        var store = new ConfigStore(Path.Combine(directory, "."));
        File.WriteAllText(Path.Combine(directory, "ready-" + role), string.Empty);
        Assert.True(SpinWait.SpinUntil(() => File.Exists(Path.Combine(directory, "start")), TimeSpan.FromSeconds(20)));
        for (var index = 0; index < 24; index++)
        {
            if (role == "rotate")
            {
                var configuration = store.RotateToken();
                File.WriteAllText(Path.Combine(directory, "rotated-hash"), ConfigStore.HashToken(configuration.HttpBearerToken));
            }
            else if (role == "configure")
            {
                store.ConfigureHttpService(HttpBindModes.All, 9000 + index);
            }
            else if (index < 12)
            {
                store.AuthorizeClient(role + "-" + index);
            }
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void BoundsLockContentionAndReleasesAfterFailure()
    {
        var store = new ConfigStore(_directory);
        using (ConfigFileLock.Acquire(store.ConfigPath))
        {
            Exception? failure = null;
            var contender = new Thread(() =>
            {
                try
                {
                    using var held = ConfigFileLock.Acquire(store.ConfigPath, TimeSpan.FromMilliseconds(50));
                }
                catch (Exception exception) { failure = exception; }
            });
            contender.Start();
            Assert.True(contender.Join(TimeSpan.FromSeconds(3)));
            Assert.Equal(ErrorCodes.Timeout, Assert.IsType<HttpAdministrationException>(failure).Code);
        }

        Assert.Throws<HttpAdministrationException>(() => store.ConfigureHttpService("invalid", null));
        Assert.NotNull(new ConfigStore(_directory).GetOrCreate());
    }

    [Fact]
    public void RecoversAnAbandonedOwnerWithoutDiscardingConfiguration()
    {
        var store = new ConfigStore(_directory);
        store.AuthorizeClient("Existing client");
        ConfigFileLock? abandoned = null;
        Exception? failure = null;
        var owner = new Thread(() =>
        {
            try { abandoned = ConfigFileLock.Acquire(store.ConfigPath); }
            catch (Exception exception) { failure = exception; }
        });
        owner.Start();
        Assert.True(owner.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
        Assert.NotNull(abandoned);
        Assert.Single(new ConfigStore(_directory).RotateToken().AuthorizedHttpClients);
        GC.KeepAlive(abandoned);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
