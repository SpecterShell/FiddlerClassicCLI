// Discovers the local Fiddler installation, process, version, and bridge files.
using System.Diagnostics;

namespace FiddlerClassic.Host.Services;

internal sealed class FiddlerEnvironment
{
    public const string BridgeAssemblyName = "FiddlerClassic.Bridge.dll";
    public const string ProtocolAssemblyName = "FiddlerClassic.Protocol.dll";

    public string ScriptsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Fiddler2",
        "Scripts");

    public string BridgeDestinationPath => Path.Combine(ScriptsDirectory, BridgeAssemblyName);

    public string ProtocolDestinationPath => Path.Combine(ScriptsDirectory, ProtocolAssemblyName);

    /// <summary>
    /// Returns the first supported Fiddler Classic executable found in known per-user and machine locations.
    /// </summary>
    public string? FindFiddlerPath()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Fiddler",
                "Fiddler.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Fiddler2",
                "Fiddler.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Fiddler2",
                "Fiddler.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public Process? FindRunningProcess()
    {
        return Process.GetProcessesByName("Fiddler").OrderBy(process => process.Id).FirstOrDefault();
    }

    /// <summary>
    /// Reads the executable file version when the supplied Fiddler path exists.
    /// </summary>
    /// <param name="fiddlerPath">The candidate Fiddler executable path.</param>
    public string? GetInstalledVersion(string? fiddlerPath)
    {
        if (string.IsNullOrEmpty(fiddlerPath) || !File.Exists(fiddlerPath))
        {
            return null;
        }

        return FileVersionInfo.GetVersionInfo(fiddlerPath).FileVersion;
    }

    public bool IsBridgeInstalled()
    {
        return File.Exists(BridgeDestinationPath) && File.Exists(ProtocolDestinationPath);
    }
}
