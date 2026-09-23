// Inspects the running single-file host's embedded streams before its harmless version command.
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

internal static class StartupHook
{
    /// <summary>Verifies bundle metadata and exact asset bytes without invoking installation or host services.</summary>
    public static void Initialize()
    {
        if (!NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), "DotNetRuntimeInfo", out _))
        {
            throw new InvalidOperationException("The executable does not contain the statically linked .NET runtime.");
        }
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(
            Environment.GetEnvironmentVariable("FIDDLER_CLASSIC_DISTRIBUTION_HASHES")
                ?? "{}")!;
        var output = Environment.GetEnvironmentVariable("FIDDLER_CLASSIC_DISTRIBUTION_OUTPUT");
        var assembly = Assembly.GetEntryAssembly()!;
        var assets = assembly.GetType("FiddlerClassicCLI.Host.Services.DistributionAssets", throwOnError: true)!;
        if (!string.Equals(assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "FiddlerClassicCLI.SingleFile").Value,
                "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The standalone host does not declare single-file publishing.");
        }
        if (assembly.GetReferencedAssemblies().Any(reference => reference.Name is "Fiddler" or "FiddlerClassicCLI.Bridge"))
        {
            throw new InvalidOperationException("The host must not reference native Fiddler assemblies.");
        }

        using var license = assembly.GetManifestResourceStream("FiddlerClassicCLI.Distribution.LICENSE")
            ?? throw new InvalidOperationException("The single-file host is missing its license.");
        VerifyResource(license, "LICENSE", expected, output);
        foreach (var name in new[] { "FiddlerClassicCLI.Bridge.dll", "FiddlerClassicCLI.Protocol.dll" })
        {
            using var resource = (Stream?)assets.GetMethod("TryOpenBridgeFile")!.Invoke(null, [name])
                ?? throw new InvalidOperationException("The single-file host is missing a bridge resource.");
            var hash = VerifyResource(resource, name, expected, output);
            var bridgeInstaller = assembly.GetType("FiddlerClassicCLI.Host.Services.BridgeInstaller", throwOnError: true)!;
            using var installSource = (Stream)bridgeInstaller.GetMethod("OpenBridgeFile", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [name])!;
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(installSource)), hash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The bridge installer does not use the embedded bridge.");
            }
        }
        Console.WriteLine("DISTRIBUTION_ASSETS_OK");
    }

    /// <summary>Checks a nonempty asset and optionally extracts it for tests of the shipped bridge.</summary>
    /// <param name="stream">The caller-owned embedded resource stream.</param>
    /// <param name="name">A fixed resource name selected by this probe.</param>
    /// <param name="expected">Optional digests of the original build inputs.</param>
    /// <param name="output">An empty test-owned directory, or null for inspection only.</param>
    /// <returns>The embedded asset's SHA-256 digest.</returns>
    private static string VerifyResource(Stream stream, string name, Dictionary<string, string> expected, string? output)
    {
        if (stream.Length == 0) throw new InvalidOperationException("An embedded asset is empty.");
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (expected.TryGetValue(name, out var expectedHash) &&
            !string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Embedded asset bytes differ from the build input.");
        }
        if (!string.IsNullOrEmpty(output))
        {
            var directory = name.EndsWith(".dll", StringComparison.Ordinal) ? Path.Combine(output, "bridge") : output;
            Directory.CreateDirectory(directory);
            stream.Position = 0;
            using var destination = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write);
            stream.CopyTo(destination);
        }
        return hash;
    }
}
