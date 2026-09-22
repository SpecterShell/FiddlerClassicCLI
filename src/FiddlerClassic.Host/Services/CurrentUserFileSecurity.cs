// Writes application files atomically and restricts them to the current Windows user.
using System.Security.AccessControl;
using System.Security.Principal;

namespace FiddlerClassic.Host.Services;

internal static class CurrentUserFileSecurity
{
    public static void WriteAllTextAtomically(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The destination path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        ApplyCurrentUserAcl(directory, isDirectory: true);

        var temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, contents);
            ApplyCurrentUserAcl(temporaryPath, isDirectory: false);
            File.Move(temporaryPath, path, overwrite: true);
            ApplyCurrentUserAcl(path, isDirectory: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

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
