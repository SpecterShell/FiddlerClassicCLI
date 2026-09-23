// Forces rotation to overlap a configuration snapshot and checks that its timestamp remains consistent.
using FiddlerClassicCLI.Host.Mcp;
using FiddlerClassicCLI.Host.Services;

namespace FiddlerClassicCLI.Tests;

public sealed class CredentialSnapshotTests
{
    [Fact]
    public async Task RotationWaitsUntilSnapshotDataAndTimestampHaveBeenRead()
    {
        var directory = Path.Combine(Path.GetTempPath(), "FiddlerClassicCLITests", Guid.NewGuid().ToString("N"));
        var store = new ConfigStore(directory);
        var previous = store.GetOrCreate();
        var stamp = File.GetLastWriteTimeUtc(store.ConfigPath);
        var credentials = new HttpCredentialManager(store);
        Assert.NotNull(credentials.Authenticate("Bearer " + previous.HttpBearerToken));
        using var readerEntered = new ManualResetEventSlim();
        using var writerEntered = new ManualResetEventSlim();
        using var finishRead = new ManualResetEventSlim();
        var reader = Task.Run(() => store.ReadConsistently(configuration =>
        {
            readerEntered.Set();
            Assert.True(finishRead.Wait(TimeSpan.FromSeconds(5)));
            return (Hash: ConfigStore.HashToken(configuration.HttpBearerToken), Stamp: File.GetLastWriteTimeUtc(store.ConfigPath));
        }), TestContext.Current.CancellationToken);
        Task<HostConfiguration>? writer = null;
        try
        {
            Assert.True(await Task.Run(() => readerEntered.Wait(TimeSpan.FromSeconds(3)), TestContext.Current.CancellationToken));
            writer = Task.Run(() =>
            {
                writerEntered.Set();
                return new ConfigStore(directory).RotateToken();
            }, TestContext.Current.CancellationToken);
            Assert.True(await Task.Run(() => writerEntered.Wait(TimeSpan.FromSeconds(3)), TestContext.Current.CancellationToken));
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(writer.IsCompleted, "Rotation must wait until the reader has captured the matching file timestamp.");
            finishRead.Set();
            var snapshot = await reader;
            var rotated = await writer;
            Assert.Equal(ConfigStore.HashToken(previous.HttpBearerToken), snapshot.Hash);
            Assert.Equal(stamp, snapshot.Stamp);
            Assert.Null(credentials.Authenticate("Bearer " + previous.HttpBearerToken));
            Assert.NotNull(credentials.Authenticate("Bearer " + rotated.HttpBearerToken));
        }
        finally
        {
            finishRead.Set();
            try { await reader; if (writer is not null) await writer; }
            finally { Directory.Delete(directory, recursive: true); }
        }
    }
}
