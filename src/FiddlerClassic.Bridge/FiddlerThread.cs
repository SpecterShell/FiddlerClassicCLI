// Provides one synchronous gateway to Fiddler Classic's UI-owned APIs.
using Fiddler;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Bridge;

internal static class FiddlerThread
{
    /// <summary>
    /// Executes an operation synchronously on Fiddler's UI thread.
    /// </summary>
    /// <typeparam name="T">The operation result type.</typeparam>
    /// <param name="action">The Fiddler API operation to execute.</param>
    public static T Invoke<T>(Func<T> action)
    {
        var ui = FiddlerApplication.UI
            ?? throw new BridgeOperationException(ErrorCodes.Unavailable, "Fiddler's user interface is not available.");

        if (ui.InvokeRequired)
        {
            return (T)ui.Invoke(action);
        }

        return action();
    }

    /// <summary>
    /// Executes an operation synchronously on Fiddler's UI thread.
    /// </summary>
    /// <param name="action">The Fiddler API operation to execute.</param>
    public static void Invoke(Action action)
    {
        Invoke(() =>
        {
            action();
            return true;
        });
    }
}
