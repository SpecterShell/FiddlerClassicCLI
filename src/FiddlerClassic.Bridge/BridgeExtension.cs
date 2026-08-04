// Integrates the named-pipe bridge with the Fiddler Classic extension lifecycle.
using Fiddler;
using FiddlerClassic.Protocol;

[assembly: RequiredVersion("5.0.0.0")]

namespace FiddlerClassic.Bridge;

public sealed class BridgeExtension : IFiddlerExtension
{
    private BridgeServer? _server;
    private BridgeDispatcher? _dispatcher;

    /// <summary>
    /// Starts the bridge when Fiddler loads the extension and records startup failures in the Fiddler log.
    /// </summary>
    public void OnLoad()
    {
        try
        {
            _dispatcher = new BridgeDispatcher();
            _server = new BridgeServer(_dispatcher);
            _server.Start();
            FiddlerApplication.Log.LogString($"[FiddlerClassicCLI] Bridge listening on {PipeNames.ForCurrentUser()}.");
        }
        catch (Exception exception)
        {
            FiddlerApplication.Log.LogString($"[FiddlerClassicCLI] Bridge failed to start: {exception}");
        }
    }

    public void OnBeforeUnload()
    {
        _server?.Dispose();
        _server = null;
        _dispatcher?.Dispose();
        _dispatcher = null;
    }
}
