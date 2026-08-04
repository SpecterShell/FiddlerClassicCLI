// Persists the HTTP bearer token with current-user filesystem permissions.
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace FiddlerClassic.Host.Services;

internal sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _configDirectory;

    public ConfigStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FiddlerClassicCLI"))
    {
    }

    internal ConfigStore(string configDirectory)
    {
        _configDirectory = configDirectory;
    }

    public string ConfigDirectory => _configDirectory;

    public string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    /// <summary>
    /// Loads a usable configuration or creates a new token-protected configuration when none exists.
    /// </summary>
    public HostConfiguration GetOrCreate()
    {
        if (File.Exists(ConfigPath))
        {
            var existing = JsonSerializer.Deserialize<HostConfiguration>(File.ReadAllText(ConfigPath), JsonOptions);
            if (existing is not null && !string.IsNullOrWhiteSpace(existing.HttpBearerToken))
            {
                return existing;
            }
        }

        return Save(new HostConfiguration { HttpBearerToken = GenerateToken() });
    }

    public HostConfiguration RotateToken()
    {
        var configuration = GetOrCreate();
        configuration.HttpBearerToken = GenerateToken();
        return Save(configuration);
    }

    /// <summary>
    /// Atomically replaces the configuration and reapplies current-user permissions to the directory and file.
    /// </summary>
    /// <param name="configuration">The configuration to persist.</param>
    internal HostConfiguration Save(HostConfiguration configuration)
    {
        Directory.CreateDirectory(ConfigDirectory);
        ApplyCurrentUserAcl(ConfigDirectory, isDirectory: true);

        var temporaryPath = ConfigPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(configuration, JsonOptions));
        ApplyCurrentUserAcl(temporaryPath, isDirectory: false);
        File.Move(temporaryPath, ConfigPath, overwrite: true);
        ApplyCurrentUserAcl(ConfigPath, isDirectory: false);
        return configuration;
    }

    private static string GenerateToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Replaces inherited Windows permissions with full control for the current user.
    /// </summary>
    /// <param name="path">The file or directory whose ACL should be replaced.</param>
    /// <param name="isDirectory">Whether directory inheritance flags should be applied.</param>
    private static void ApplyCurrentUserAcl(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var identity = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");

        if (isDirectory)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
            return;
        }

        var fileSecurity = new FileSecurity();
        fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        fileSecurity.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(fileSecurity);
    }
}

internal sealed class HostConfiguration
{
    public int HttpPort { get; set; } = 8877;
    public string HttpBearerToken { get; set; } = string.Empty;
}
