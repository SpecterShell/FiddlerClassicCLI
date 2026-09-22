// Discovers the local Fiddler installation, process, version, and bridge files.
using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace FiddlerClassic.Host.Services;

internal sealed class FiddlerEnvironment
{
    public const string BridgeAssemblyName = "FiddlerClassic.Bridge.dll";
    public const string ProtocolAssemblyName = "FiddlerClassic.Protocol.dll";

    public string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FiddlerClassicCLI");

    public string HostLaunchRecordPath => Path.Combine(ConfigDirectory, "bridge-host.json");

    public string ScriptsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Fiddler2",
        "Scripts");

    public string BridgeDestinationPath => Path.Combine(ScriptsDirectory, BridgeAssemblyName);

    public string ProtocolDestinationPath => Path.Combine(ScriptsDirectory, ProtocolAssemblyName);

    /// <summary>
    /// Returns a supported executable when available, or the first existing executable for diagnostics.
    /// </summary>
    public string? FindFiddlerPath()
    {
        var installations = FindInstallations();
        return (installations.FirstOrDefault(installation => installation.Supported)
            ?? installations.FirstOrDefault())?.ExecutablePath;
    }

    /// <summary>
    /// Discovers existing Fiddler executables without starting them or changing registry state.
    /// </summary>
    /// <param name="explicitPath">An absolute Fiddler.exe path, or null to search defaults and App Paths.</param>
    /// <returns>Distinct existing installations in discovery order, including unsupported versions.</returns>
    /// <exception cref="ArgumentException">The explicit path is malformed or does not name Fiddler.exe.</exception>
    public IReadOnlyList<FiddlerInstallation> FindInstallations(string? explicitPath = null)
    {
        return FindInstallationsFromCandidates(GetInstallationCandidates(), File.Exists, GetInstalledVersion, explicitPath);
    }

    /// <summary>
    /// Normalizes discovery candidates and reads metadata through supplied filesystem operations.
    /// </summary>
    /// <param name="candidates">Default executable paths followed by optional App Paths values.</param>
    /// <param name="fileExists">Checks whether a normalized executable path exists.</param>
    /// <param name="readVersion">Reads file version metadata without executing the file.</param>
    /// <param name="explicitPath">An exclusive absolute executable path, or null for candidate discovery.</param>
    /// <returns>Existing installations with case-insensitive absolute-path duplicates removed.</returns>
    /// <exception cref="ArgumentException">The explicit path is malformed or does not name Fiddler.exe.</exception>
    internal static IReadOnlyList<FiddlerInstallation> FindInstallationsFromCandidates(
        IEnumerable<string?> candidates,
        Func<string, bool> fileExists,
        Func<string, string?> readVersion,
        string? explicitPath = null)
    {
        if (explicitPath is not null)
        {
            // Validate before enumeration so an explicit path never consults fallback locations.
            candidates = [NormalizeExecutablePath(explicitPath)];
        }

        var installations = new List<FiddlerInstallation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var path = candidate.Trim();
                if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
                {
                    path = path[1..^1];
                }

                path = NormalizeExecutablePath(path);
                if (!seen.Add(path) || !fileExists(path))
                {
                    continue;
                }

                var version = readVersion(path);
                installations.Add(new FiddlerInstallation(path, version, IsSupportedVersion(version)));
            }
            catch (Exception exception) when (exception is ArgumentException or IOException
                or UnauthorizedAccessException or SecurityException)
            {
                // Stale, malformed, or unreadable discovery entries must not block other installations.
            }
        }

        return installations;
    }

    /// <summary>
    /// Validates an executable file path without resolving relative paths against the working directory.
    /// </summary>
    /// <param name="explicitPath">The absolute path to normalize.</param>
    /// <returns>The normalized absolute Fiddler.exe path.</returns>
    /// <exception cref="ArgumentException">The path is malformed or names a different file.</exception>
    internal static string NormalizeExecutablePath(string explicitPath)
    {
        if (string.IsNullOrWhiteSpace(explicitPath)
            || !Path.IsPathFullyQualified(explicitPath)
            || explicitPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || !string.Equals(Path.GetFileName(explicitPath), "Fiddler.exe", StringComparison.OrdinalIgnoreCase)
            || explicitPath[Path.GetPathRoot(explicitPath)!.Length..]
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new ArgumentException("The path must be an absolute file path named Fiddler.exe.", nameof(explicitPath));
        }

        return Path.GetFullPath(explicitPath);
    }

    /// <summary>
    /// Enumerates known default paths and read-only App Paths entries in both registry views.
    /// </summary>
    /// <returns>Executable candidates; unavailable registry entries are omitted.</returns>
    private static IEnumerable<string?> GetInstallationCandidates()
    {
        var defaults = new[]
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

        foreach (var path in defaults)
        {
            yield return path;
        }

        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                string? path = null;
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var appPath = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\Fiddler.exe", writable: false);
                    path = appPath?.GetValue(null) as string;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
                {
                    // An inaccessible registry view is not evidence that Fiddler is absent elsewhere.
                }

                if (path is not null)
                {
                    yield return path;
                }
            }
        }
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

    internal static bool IsSupportedVersion(string? version)
    {
        return Version.TryParse(version, out var parsed) && parsed.Major is 5 or 6;
    }

    public bool IsBridgeInstalled()
    {
        return File.Exists(BridgeDestinationPath) && File.Exists(ProtocolDestinationPath);
    }
}
