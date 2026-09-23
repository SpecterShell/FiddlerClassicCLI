// Displays read-only named-pipe endpoints and lifecycle metadata independently of MCP HTTP state.
using System.Drawing;
using System.Windows.Forms;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Bridge;

internal sealed partial class BridgeControlPanel
{
    private readonly Func<BridgePipeStatus> _bridgeStatus;
    private readonly Label _bridgePipePath = PipeLabel("Bridge pipe path");
    private readonly Label _bridgePipeState = PipeLabel("Bridge listener status");
    private readonly Label _bridgePipeError = PipeLabel("Bridge listener error");
    private readonly Label _daemonPipePath = PipeLabel("Daemon pipe path");
    private readonly Label _daemonPipeState = PipeLabel("Daemon pipe status", "Not checked");
    private readonly Label _daemonPipeError = PipeLabel("Daemon pipe error");
    private readonly Label _daemonProcessId = PipeLabel("Daemon process ID", "Unknown");
    private readonly Label _daemonStartedAt = PipeLabel("Daemon start time UTC", "Unknown");
    private readonly Button _copyBridgePipe = new Button
    {
        Text = "Copy bridge p&ipe", AutoSize = true, AccessibleName = "Copy bridge pipe path"
    };
    private readonly Button _copyDaemonPipe = new Button
    {
        Text = "Copy &daemon pipe", AutoSize = true, AccessibleName = "Copy daemon pipe path"
    };
    private readonly Button _refreshPipes = new Button
    {
        Text = "Refresh pipe&s", AutoSize = true, AccessibleName = "Refresh named pipes"
    };

    /// <summary>Builds a width-constrained diagnostics section with wrapping text and copy actions.</summary>
    private GroupBox CreatePipesGroup()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(layout, "Bridge", _bridgePipeState);
        AddFullRow(layout, _bridgePipePath);
        _bridgePipeError.ForeColor = Color.DarkRed;
        AddFullRow(layout, _bridgePipeError);
        AddRow(layout, "Daemon", _daemonPipeState);
        AddFullRow(layout, _daemonPipePath);
        _daemonPipeError.ForeColor = Color.DarkRed;
        AddFullRow(layout, _daemonPipeError);
        AddRow(layout, "PID", _daemonProcessId);
        AddFullRow(layout, PipeLabel("Daemon start time caption", "Started (UTC)"));
        AddFullRow(layout, _daemonStartedAt);
        AddRow(layout, "Protocols", PipeLabel("Named pipe protocol versions",
            $"Bridge v{ProtocolConstants.Version}, daemon v{DaemonProtocol.Version}"));
        AddRow(layout, "Access", PipeLabel("Named pipe access restrictions", "Current Windows user only"));
        AddFullRow(layout, CreateActions(_copyBridgePipe, _copyDaemonPipe, _refreshPipes));
        return new ContentGroupBox("Named pipes", layout) { AccessibleName = "Named pipes", TabStop = false };
    }

    private void InitializePipeDiagnostics()
    {
        SetPipePath(_daemonPipePath, _copyDaemonPipe, _client.PipeName);
        UpdateBridgePipeStatus();
        foreach (var button in new[] { _copyBridgePipe, _copyDaemonPipe })
        {
            button.Click += (_, _) =>
            {
                if (!string.IsNullOrEmpty(button.AccessibleDescription))
                    Clipboard.SetText(button.AccessibleDescription);
            };
        }
        _refreshPipes.Click += async (_, _) => await RunOperationAsync(async () =>
        {
            await RefreshPipeStatusAsync();
        }, showErrors: false);
    }

    /// <summary>Refreshes local state and checks the daemon without starting it or changing configuration.</summary>
    /// <returns>The daemon response, or null when its state could not be established.</returns>
    private async Task<DaemonStatus?> RefreshPipeStatusAsync()
    {
        UpdateBridgePipeStatus();
        try
        {
            var daemon = await _client.GetDaemonStatusAsync(_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            _daemonPipeState.Text = daemon.Running ? "Responding" : "Stopped (reported)";
            _daemonPipeError.Text = string.Empty;
            _daemonProcessId.Text = daemon.Running && daemon.ProcessId > 0 ? daemon.ProcessId.ToString() : "Unknown";
            _daemonStartedAt.Text = daemon.Running && !string.IsNullOrWhiteSpace(daemon.StartedAtUtc)
                ? daemon.StartedAtUtc : "Unknown";
            return daemon;
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _daemonPipeState.Text = exception is TimeoutException
                ? "Timed out (state unknown)" : "Unavailable (state unknown)";
            _daemonPipeError.Text = exception.Message;
            _daemonProcessId.Text = "Unknown";
            _daemonStartedAt.Text = "Unknown";
            return null;
        }
    }

    private void UpdateBridgePipeStatus()
    {
        var status = _bridgeStatus();
        SetPipePath(_bridgePipePath, _copyBridgePipe, status.PipeName);
        _bridgePipeState.Text = status.State.ToString();
        _bridgePipeError.Text = status.Error ?? string.Empty;
    }

    private static void SetPipePath(Label label, Button copy, string name)
    {
        var path = string.IsNullOrWhiteSpace(name) ? string.Empty : @"\\.\pipe\" + name;
        label.Text = path.Length == 0 ? "Unavailable" : path;
        copy.AccessibleDescription = path;
        copy.Enabled = path.Length != 0;
    }

    private static Label PipeLabel(string name, string text = "") => new Label
    {
        AutoSize = true, AccessibleName = name, UseMnemonic = false, Text = text
    };
}
