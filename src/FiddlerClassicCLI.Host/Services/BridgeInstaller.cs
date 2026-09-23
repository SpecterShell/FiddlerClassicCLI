// Installs bridge artifacts and records the exact host executable used by the Fiddler UI.
using System.Reflection;
using System.Text.Json;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Services;

internal sealed class BridgeInstaller
{
    private readonly FiddlerEnvironment _environment;

    public BridgeInstaller(FiddlerEnvironment environment)
    {
        _environment = environment;
    }

    /// <summary>
    /// Installs the bridge and shared protocol assemblies into the current user's Scripts directory.
    /// </summary>
    public IReadOnlyList<string> Install()
    {
        using var bridge = OpenBridgeFile(FiddlerEnvironment.BridgeAssemblyName);
        using var protocol = OpenBridgeFile(FiddlerEnvironment.ProtocolAssemblyName);
        Directory.CreateDirectory(_environment.ScriptsDirectory);

        var installed = new List<string>();
        foreach (var (fileName, source) in new[]
        {
            (FiddlerEnvironment.BridgeAssemblyName, bridge),
            (FiddlerEnvironment.ProtocolAssemblyName, protocol)
        })
        {
            var destination = Path.Combine(_environment.ScriptsDirectory, fileName);
            InstallFile(source, destination);
            installed.Add(destination);
        }

        var launchRecordPath = WriteHostLaunchRecord();
        installed.Add(launchRecordPath);

        return installed;
    }

    /// <summary>
    /// Removes installed bridge artifacts and Fiddler-generated shadow copies for those artifacts.
    /// </summary>
    public IReadOnlyList<string> Uninstall()
    {
        var removed = new List<string>();
        var installedPaths = new[]
        {
            _environment.BridgeDestinationPath,
            _environment.ProtocolDestinationPath,
            _environment.HostLaunchRecordPath
        };
        var paths = installedPaths.Concat(installedPaths.SelectMany(path =>
            Directory.Exists(_environment.ScriptsDirectory)
                ? Directory.GetFiles(_environment.ScriptsDirectory, Path.GetFileName(path) + "~RF*.TMP")
                : Array.Empty<string>()));
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            File.Delete(path);
            removed.Add(path);
        }

        return removed;
    }

    /// <summary>
    /// Opens embedded bridge bytes first, falling back to published files or a local build tree.
    /// </summary>
    /// <param name="fileName">The exact required assembly filename.</param>
    /// <returns>A source stream owned by the caller.</returns>
    internal static Stream OpenBridgeFile(string fileName)
    {
        if (!RequiredFiles().Contains(fileName, StringComparer.Ordinal))
        {
            throw new ArgumentException("Unknown bridge artifact.", nameof(fileName));
        }
        return DistributionAssets.TryOpenBridgeFile(fileName)
            ?? File.OpenRead(Path.Combine(FindSourceDirectory(), fileName));
    }

    /// <summary>
    /// Locates bridge artifacts in a published distribution or a local build tree.
    /// </summary>
    private static string FindSourceDirectory()
    {
        var publishedDirectory = Path.Combine(AppContext.BaseDirectory, "bridge");
        if (ContainsRequiredFiles(publishedDirectory))
        {
            return publishedDirectory;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "FiddlerClassicCLI.slnx")))
            {
                continue;
            }

            foreach (var configuration in new[] { "Release", "Debug" })
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "src",
                    "FiddlerClassicCLI.Bridge",
                    "bin",
                    configuration,
                    "net462");
                if (ContainsRequiredFiles(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException(
            "Bridge artifacts were not found. Use the published distribution or build FiddlerClassicCLI.Bridge first.");
    }

    private static bool ContainsRequiredFiles(string directory)
    {
        return RequiredFiles().All(fileName => File.Exists(Path.Combine(directory, fileName)));
    }

    private static string[] RequiredFiles()
    {
        return new[] { FiddlerEnvironment.BridgeAssemblyName, FiddlerEnvironment.ProtocolAssemblyName };
    }

    private string WriteHostLaunchRecord()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new FileNotFoundException("The current Fiddler Classic CLI executable path is unavailable.");
        }

        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        var record = new HostLaunchRecord
        {
            HostExecutablePath = Path.GetFullPath(executablePath),
            HostVersion = version
        };
        CurrentUserFileSecurity.WriteAllTextAtomically(
            _environment.HostLaunchRecordPath,
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        return _environment.HostLaunchRecordPath;
    }

    /// <summary>
    /// Stages one complete artifact and atomically replaces an existing assembly, including a loaded add-on.
    /// </summary>
    /// <param name="source">The artifact stream, read from its current position and left open for the caller.</param>
    /// <param name="destination">The target path in the Fiddler Scripts directory.</param>
    internal static void InstallFile(Stream source, string destination)
    {
        var stagedPath = destination + ".new." + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(output);
            }

            if (File.Exists(destination))
            {
                File.Replace(stagedPath, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(stagedPath, destination);
            }
        }
        finally
        {
            if (File.Exists(stagedPath))
            {
                File.Delete(stagedPath);
            }
        }
    }
}
