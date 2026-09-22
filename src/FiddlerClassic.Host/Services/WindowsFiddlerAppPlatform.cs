// Uses current-user Windows process handles and normal window-close requests for explicit Fiddler lifecycle commands.
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using FiddlerClassic.Host.Bridge;
using FiddlerClassic.Protocol;
using Microsoft.Win32.SafeHandles;

namespace FiddlerClassic.Host.Services;

[SupportedOSPlatform("windows")]
internal sealed class WindowsFiddlerAppPlatform : IFiddlerAppPlatform
{
    private readonly FiddlerEnvironment _environment = new();

    public IReadOnlyList<FiddlerInstallation> FindInstallations(string? executablePath) =>
        _environment.FindInstallations(executablePath);

    /// <summary>Retains handles only to verified Fiddler processes owned by this user in this Windows session.</summary>
    /// <returns>Process leases that the caller must dispose, including when selection fails.</returns>
    public IReadOnlyList<IFiddlerAppProcess> GetProcesses()
    {
        var result = new List<IFiddlerAppProcess>();
        using var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName("Fiddler");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.SessionId != current.SessionId || !IsOwnedByCurrentUser(process))
                    {
                        continue;
                    }

                    // SafeHandle stays open in the lease. Windows cannot recycle the process identity
                    // while it is held, so selection and a later close do not target a reused PID.
                    var path = process.MainModule?.FileName;
                    if (path is null || !Path.GetFileName(path).Equals("Fiddler.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new Win32Exception("The Fiddler executable path could not be verified.");
                    }

                    var version = _environment.GetInstalledVersion(path);
                    result.Add(new WindowsAppProcess(process,
                        new(process.Id, path, version, FiddlerEnvironment.IsSupportedVersion(version))));
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // A process that exited during discovery is not a target.
                }
            }

            return result;
        }
        catch (Exception exception) when (exception is Win32Exception or UnauthorizedAccessException)
        {
            foreach (var lease in result)
            {
                lease.Dispose();
            }
            result.Clear();
            throw new BridgeClientException(ErrorCodes.Unavailable,
                "Cannot verify access to a Fiddler process. Use the same Windows user and permission level as Fiddler.", exception);
        }
        catch
        {
            foreach (var lease in result)
            {
                lease.Dispose();
            }
            result.Clear();
            throw;
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!result.OfType<WindowsAppProcess>().Any(lease => ReferenceEquals(lease.Process, process)))
                {
                    process.Dispose();
                }
            }
        }
    }

    /// <summary>Serializes app mutations across CLI processes without a thread-affine mutex or daemon startup.</summary>
    /// <returns>A user-only pipe lease held through close and restart waits.</returns>
    public IDisposable AcquireOperation()
    {
        try
        {
            return new NamedPipeServerStream(PipeNames.ForCurrentUser() + ".app-lifecycle",
                PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.FirstPipeInstance | PipeOptions.CurrentUserOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BridgeClientException(ErrorCodes.Conflict,
                "Another Fiddler app command is in progress, or its ownership pipe is inaccessible. Retry after it finishes.", exception);
        }
    }

    public int Start(string executablePath)
    {
        using var process = Process.Start(CreateStartInfo(executablePath))
            ?? throw new BridgeClientException(ErrorCodes.Unavailable, "Windows did not return a Fiddler process handle.");
        return process.Id;
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath)
    {
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            WindowStyle = ProcessWindowStyle.Normal
        };
        start.ArgumentList.Add("-noattach");
        return start;
    }

    /// <summary>Verifies the process token before exposing a target; the Process retains its native handle.</summary>
    /// <param name="process">A local process whose handle remains owned by the caller.</param>
    internal static bool IsOwnedByCurrentUser(Process process)
    {
        if (!OpenProcessToken(process.SafeHandle, 0x0008 /* TOKEN_QUERY */, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        using (token)
        using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
        {
            var currentSid = current.User ?? throw new Win32Exception("The current Windows user SID is unavailable.");
            var ownerSid = owner.User ?? throw new Win32Exception("The process owner SID is unavailable.");
            return ownerSid.Equals(currentSid);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    private sealed class WindowsAppProcess(Process process, FiddlerAppProcessInfo info) : IFiddlerAppProcess
    {
        internal Process Process => process;
        public FiddlerAppProcessInfo Info => info;
        public bool HasExited => process.HasExited;
        public bool CloseMainWindow() => process.CloseMainWindow();
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Dispose() => process.Dispose();
    }
}
