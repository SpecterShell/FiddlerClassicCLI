// Integrates the named-pipe bridge with the Fiddler Classic extension lifecycle.
using Fiddler;
using System.Windows.Forms;

// Keep 5.0 as the minimum so the same extension assembly loads in supported 5.x and 6.x releases.
[assembly: RequiredVersion("5.0.0.0")]

namespace FiddlerClassicCLI.Bridge;

public sealed class BridgeExtension : IFiddlerExtension
{
    private BridgeServer? _server;
    private BridgeDispatcher? _dispatcher;
    private BridgeControlPanel? _controlPanel;
    private TabPage? _tabPage;
    private MenuItem? _toolsMenuItem;
    private BridgeHostLifetime? _hostLifetime;

    /// <summary>
    /// Starts the bridge when Fiddler loads the extension and records startup failures in the Fiddler log.
    /// </summary>
    public void OnLoad()
    {
        if (_hostLifetime is not null)
        {
            return;
        }
        var hostClient = new HostControlClient();
        _hostLifetime = new BridgeHostLifetime(hostClient);
        var bridgeUnavailable = new BridgePipeStatus(string.Empty, BridgeListenerState.Unavailable);
        try
        {
            _dispatcher = new BridgeDispatcher();
            _server = new BridgeServer(_dispatcher);
            _server.Start();
            FiddlerApplication.Log.LogString($"[FiddlerClassicCLI] Bridge starting on {_server.Status.PipeName}.");
        }
        catch (Exception exception)
        {
            bridgeUnavailable = new BridgePipeStatus(string.Empty, BridgeListenerState.Unavailable, exception.Message);
            FiddlerApplication.Log.LogString($"[FiddlerClassicCLI] Bridge failed to start: {exception}");
        }

        try
        {
            _controlPanel = new BridgeControlPanel(hostClient, _hostLifetime,
                FiddlerThread.Invoke(FiddlerApplication.GetVersionString),
                () => _server?.Status ?? bridgeUnavailable);
            _tabPage = new TabPage("Fiddler Classic CLI");
            TerminalTabIcon.ApplyTo(_tabPage, FiddlerApplication.UI.imglSessionIcons);
            _tabPage.Controls.Add(_controlPanel);
            FiddlerApplication.UI.tabsViews.TabPages.Add(_tabPage);

            _toolsMenuItem = new MenuItem("Fiddler Classic CLI", (_, _) =>
            {
                if (_tabPage is not null)
                {
                    FiddlerApplication.UI.tabsViews.SelectedTab = _tabPage;
                }
            });
            FiddlerApplication.UI.mnuTools.MenuItems.Add(_toolsMenuItem);
        }
        catch (Exception exception)
        {
            FiddlerApplication.Log.LogString($"[FiddlerClassicCLI] Management UI failed to load: {exception}");
        }
    }

    public void OnBeforeUnload()
    {
        _hostLifetime?.Dispose();
        _hostLifetime = null;
        if (_toolsMenuItem is not null)
        {
            FiddlerApplication.UI.mnuTools.MenuItems.Remove(_toolsMenuItem);
            _toolsMenuItem.Dispose();
            _toolsMenuItem = null;
        }

        if (_tabPage is not null)
        {
            FiddlerApplication.UI.tabsViews.TabPages.Remove(_tabPage);
            _tabPage.Dispose();
            _tabPage = null;
        }

        _controlPanel = null;
        _server?.Dispose();
        _server = null;
        _dispatcher?.Dispose();
        _dispatcher = null;
    }
}
