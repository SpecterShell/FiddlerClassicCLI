// Configures host tests and initializes the test assembly resolver.
using System.Runtime.CompilerServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable(
            "FIDDLER_CLASSIC_PIPE_NAME",
            $"fiddler-classic-cli.tests.{Environment.ProcessId}.{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(
            "FIDDLER_CLASSIC_DAEMON_PIPE_NAME",
            $"fiddler-classic-cli.daemon-tests.{Environment.ProcessId}.{Guid.NewGuid():N}");
    }
}
