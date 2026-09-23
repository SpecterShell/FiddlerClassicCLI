// Selects the built test host or an explicitly supplied release executable for fake-peer process tests.
using FiddlerClassicCLI.Host.Mcp;

namespace FiddlerClassicCLI.Tests;

internal static class HostExecutable
{
    public static string Resolve()
    {
        var published = Environment.GetEnvironmentVariable("FIDDLER_CLASSIC_TEST_HOST_EXE");
        if (published is null)
        {
            return Path.Combine(Path.GetDirectoryName(typeof(FiddlerTools).Assembly.Location)!, "fiddler-classic-cli.exe");
        }
        if (!Path.IsPathFullyQualified(published) || !File.Exists(published)
            || !Path.GetFileName(published).Equals("fiddler-classic-cli.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("FIDDLER_CLASSIC_TEST_HOST_EXE must name an existing absolute fiddler-classic-cli.exe path.");
        }
        return published;
    }
}
