// Serializes configuration transactions across processes and Windows sessions for the current user.
using System.Security.Cryptography;
using System.Text;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Host.Services;

internal sealed class ConfigFileLock : IDisposable
{
    private readonly Mutex _mutex;

    private ConfigFileLock(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Acquires the configuration lock; the caller must dispose it on this thread without awaiting.
    /// Atomic replacement makes it safe to reload the file after a previous owner exits unexpectedly.
    /// </summary>
    /// <param name="configPath">The configuration file whose complete transaction is protected.</param>
    /// <param name="timeout">An optional test override for the five-second acquisition limit.</param>
    public static ConfigFileLock Acquire(string configPath, TimeSpan? timeout = null)
    {
        var path = Path.GetFullPath(configPath);
        if (OperatingSystem.IsWindows())
        {
            path = path.ToUpperInvariant();
        }

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        var mutex = new Mutex(false, "FiddlerClassicCLI.Config." + key, new NamedWaitHandleOptions
        {
            CurrentUserOnly = true,
            CurrentSessionOnly = false
        });
        try
        {
            try
            {
                if (!mutex.WaitOne(timeout ?? TimeSpan.FromSeconds(5)))
                {
                    throw new HttpAdministrationException(
                        ErrorCodes.Timeout,
                        "Timed out waiting for another CLI or daemon configuration update. Retry the operation.");
                }
            }
            catch (AbandonedMutexException)
            {
                // WaitOne transferred ownership; read the last complete atomic file replacement.
            }

            return new ConfigFileLock(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
