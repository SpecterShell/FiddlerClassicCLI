// Configures bridge tests and initializes the .NET Framework test assembly resolver.
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal static class TestEnvironment
{
    private static bool _initialized;

    /// <summary>
    /// Assigns a unique bridge pipe name once for the non-parallel .NET Framework test process.
    /// </summary>
    internal static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        Environment.SetEnvironmentVariable(
            "FIDDLER_CLASSIC_PIPE_NAME",
            $"fiddler-classic-cli.bridge-tests.{System.Diagnostics.Process.GetCurrentProcess().Id}.{Guid.NewGuid():N}");
        _initialized = true;
    }
}
