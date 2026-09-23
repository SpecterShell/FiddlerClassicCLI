// Opens embedded bridge assemblies as streams for current-user deployment.
namespace FiddlerClassicCLI.Host.Services;

internal static class DistributionAssets
{
    private const string ResourcePrefix = "FiddlerClassicCLI.Distribution.";

    /// <summary>Opens an embedded bridge artifact as a read-only stream owned by the caller.</summary>
    /// <param name="fileName">One of the two exact bridge assembly filenames.</param>
    /// <returns>The artifact stream, or null for an unknown filename or an absent resource.</returns>
    public static Stream? TryOpenBridgeFile(string fileName)
    {
        if (fileName is not (FiddlerEnvironment.BridgeAssemblyName or FiddlerEnvironment.ProtocolAssemblyName))
        {
            return null;
        }

        return typeof(DistributionAssets).Assembly.GetManifestResourceStream(ResourcePrefix + fileName);
    }
}
