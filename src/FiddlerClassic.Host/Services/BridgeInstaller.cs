// Installs bridge artifacts and records the exact host executable used by the Fiddler UI.
using System.Reflection;
using System.Text.Json;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Host.Services;

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
        var sourceDirectory = FindSourceDirectory();
        Directory.CreateDirectory(_environment.ScriptsDirectory);

        var installed = new List<string>();
        foreach (var fileName in RequiredFiles())
        {
            var source = Path.Combine(sourceDirectory, fileName);
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
                    "FiddlerClassic.Bridge",
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
            "Bridge artifacts were not found. Use the published distribution or build FiddlerClassic.Bridge first.");
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
    /// Copies one artifact, using atomic replacement when Fiddler has the destination assembly loaded.
    /// </summary>
    /// <param name="source">The built or published artifact.</param>
    /// <param name="destination">The target path in the Fiddler Scripts directory.</param>
    private static void InstallFile(string source, string destination)
    {
        if (!File.Exists(destination))
        {
            File.Copy(source, destination);
            return;
        }

        try
        {
            File.Copy(source, destination, overwrite: true);
            return;
        }
        catch (IOException)
        {
            // A loaded .NET Framework add-on may permit atomic replacement but deny in-place writes.
        }

        var stagedPath = destination + ".new." + Guid.NewGuid().ToString("N");
        File.Copy(source, stagedPath);
        try
        {
            File.Replace(stagedPath, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
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
