// Checks the shipped extension inside a disposable native Fiddler process. This probe is never shipped with the CLI.
using System.Collections;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Fiddler;
using FiddlerClassicCLI.Protocol;

[assembly: RequiredVersion("5.0.0.0")]

namespace FiddlerClassicCLI.CompatibilityProbe;

public sealed class CompatibilityProbe : IFiddlerExtension
{
    private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 250 };
    private readonly List<string> _passed = new List<string>();
    private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
    private string _stage = "native extension loading";

    /// <summary>Schedules assertions after native extension loading returns to the UI message loop.</summary>
    public void OnLoad()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Environment.GetEnvironmentVariable("FIDDLER_CLASSIC_COMPATIBILITY_CI") != "1")
            return;
        _timer.Tick += async (_, _) =>
        {
            _timer.Stop();
            try
            {
                await RunAsync();
                SaveResult(true, null);
            }
            catch (Exception exception)
            {
                // Do not export Fiddler logs, traffic, configuration, tokens, or native stack traces.
                SaveResult(false, _stage + ": " + exception.GetType().Name);
            }
        };
        _timer.Start();
    }

    public void OnBeforeUnload() => _timer.Dispose();

    /// <summary>Exercises real UI registration, host startup, evidence access, and the shipped unload callback.</summary>
    private async Task RunAsync()
    {
        var extension = FindBridge();
        var bridgeAssembly = extension.GetType().Assembly;
        using (var digest = SHA256.Create())
        using (var stream = File.OpenRead(bridgeAssembly.Location))
            Check(BitConverter.ToString(digest.ComputeHash(stream)).Replace("-", string.Empty).Equals(
                Environment.GetEnvironmentVariable("FIDDLER_CLASSIC_EXPECTED_BRIDGE_SHA256"),
                StringComparison.OrdinalIgnoreCase), "loaded bridge SHA-256 matches the release");

        var tabs = FiddlerApplication.UI.tabsViews;
        var tab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Fiddler Classic CLI");
        var panel = tab.Controls.Cast<Control>().Single();
        Check(tabs.SelectedTab != tab, "management tab registered without selection");
        Check(FiddlerApplication.UI.mnuTools.MenuItems.Cast<MenuItem>().Count(
            item => item.Text == "Fiddler Classic CLI") == 1, "Tools menu registered once");
        var selectedDuringStartup = false;
        EventHandler selected = (_, _) => selectedDuringStartup |= tabs.SelectedTab == tab;
        tabs.SelectedIndexChanged += selected;
        try
        {
            _stage = "host startup without selecting the management tab";
            var lifetime = Field(extension, "_hostLifetime") ?? throw new InvalidOperationException("Missing host lifetime.");
            var completion = (Task)lifetime.GetType().GetProperty("Completion")!.GetValue(lifetime)!;
            if (await Task.WhenAny(completion, Task.Delay(15000)) != completion)
                throw new TimeoutException("Hidden-tab host startup timed out.");
            await completion;
            Check(lifetime.GetType().GetProperty("Error")!.GetValue(lifetime) is null,
                "host startup completed without an error");
            var daemon = await DaemonStatusAsync();
            Check(daemon.Running && daemon.ProcessId != Process.GetCurrentProcess().Id &&
                daemon.Capabilities.Contains(DaemonProtocol.ManagedHttpCapability), "released host daemon started");
            Check(daemon.HttpService is not null && !daemon.HttpService.Enabled && !daemon.HttpService.Running,
                "managed HTTP remains disabled");
            Check(!selectedDuringStartup && tabs.SelectedTab != tab, "host startup did not require tab selection");
        }
        finally { tabs.SelectedIndexChanged -= selected; }

        // A duplicate native OnLoad callback must leave a single panel and listener.
        extension.OnLoad();
        Check(tabs.TabPages.Cast<TabPage>().Count(page => page.Text == tab.Text) == 1, "duplicate load is idempotent");
        _stage = "synthetic session inspection";
        var status = await BridgeAsync<StatusResponse>(Operations.GetStatus, new EmptyRequest());
        Check(!status.IsProxyAttached && !status.IsHttpsDecryptionEnabled, "proxy and HTTPS decryption remain disabled");

        var body = new byte[] { 0, 1, 127, 128, 255, 13, 10, 65 };
        var url = "http://127.0.0.1:1/compatibility-" + Guid.NewGuid().ToString("N");
        var request = Encoding.ASCII.GetBytes("GET " + url + " HTTP/1.1\r\nHost: 127.0.0.1:1\r\n" +
            "X-Ordered: first\r\nX-Ordered: second\r\n\r\n");
        var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n" +
            "X-Ordered: alpha\r\nX-Ordered: beta\r\nContent-Length: 8\r\n\r\n").Concat(body).ToArray();
        // Import in-memory loopback evidence. No request is sent, replayed, or intercepted.
        var session = new Session(request, response);
        FiddlerApplication.UI.AddImportedSessions(new[] { session });
        var list = await BridgeAsync<ListSessionsResponse>(Operations.ListSessions, new ListSessionsRequest
        {
            MinId = session.id,
            MaxId = session.id,
            UrlContains = url,
            Limit = 1
        });
        Check(list.Sessions.Count == 1 && list.Sessions[0].Id == session.id &&
            list.Sessions[0].StatusCode == 200, "bounded session list matches native session");
        var details = await BridgeAsync<SessionDetails>(Operations.GetSessionDetails,
            new GetSessionDetailsRequest { SessionId = session.id, IncludeHeaders = true });
        Check(details.RequestHeaders!.Where(header => header.Name == "X-Ordered").Select(header => header.Value)
            .SequenceEqual(new[] { "first", "second" }), "duplicate request header order preserved");
        Check(details.ResponseHeaders!.Where(header => header.Name == "X-Ordered").Select(header => header.Value)
            .SequenceEqual(new[] { "alpha", "beta" }), "duplicate response header order preserved");
        var chunk = await BridgeAsync<SessionBodyChunk>(Operations.GetSessionBody,
            new GetSessionBodyRequest { SessionId = session.id, Offset = 2, Length = 3 });
        Check(chunk.Offset == 2 && chunk.BytesReturned == 3 && chunk.TotalBytes == 8 && !chunk.EndOfBody &&
            Convert.FromBase64String(chunk.Base64Data).SequenceEqual(body.Skip(2).Take(3)), "exact bounded binary body bytes");

        _stage = "extension unload cleanup";
        using (var pending = new NamedPipeClientStream(".", PipeNames.ForCurrentUser(), PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await pending.ConnectAsync(3000);
            var pendingRead = pending.ReadAsync(new byte[1], 0, 1);
            extension.OnBeforeUnload();
            Check(tab.IsDisposed && panel.IsDisposed && !tabs.TabPages.Contains(tab), "unload disposes panel and removes tab");
            Check(!FiddlerApplication.UI.mnuTools.MenuItems.Cast<MenuItem>().Any(
                item => item.Text == "Fiddler Classic CLI"), "unload removes Tools menu item");
            Check(Field(extension, "_server") is null && Field(extension, "_dispatcher") is null &&
                Field(extension, "_hostLifetime") is null, "unload releases server, dispatcher, and lifetime");
            Check(await Task.WhenAny(pendingRead, Task.Delay(3000)) == pendingRead, "unload terminates active pipe read");
            try { Check(await pendingRead == 0, "active pipe closes at unload"); }
            catch (IOException) { _passed.Add("active pipe closes at unload"); }
        }
        using (var closed = new NamedPipeClientStream(".", PipeNames.ForCurrentUser(), PipeDirection.InOut))
        {
            var connected = false;
            try { closed.Connect(500); connected = true; }
            catch (TimeoutException) { }
            Check(!connected, "unload closes the listener");
        }
        extension.OnBeforeUnload();
        _passed.Add("repeated unload is safe");
    }

    /// <summary>Finds the native loader's actual extension instance without depending on obfuscated field names.</summary>
    private static IFiddlerExtension FindBridge()
    {
        // Test-only reflection: 5.x/6.x do not expose the extension-instance collection publicly.
        var loaded = typeof(FiddlerExtensions).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => field.GetValue(FiddlerApplication.oExtensions)).OfType<IDictionary>()
            .SelectMany(dictionary => dictionary.Values.Cast<object>()).OfType<IFiddlerExtension>()
            .Where(extension => extension.GetType().FullName == "FiddlerClassicCLI.Bridge.BridgeExtension").Distinct().ToArray();
        if (loaded.Length != 1) throw new InvalidOperationException("Native loader did not expose one shipped bridge instance.");
        return loaded[0];
    }

    private static object? Field(object instance, string name) => instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);

    /// <summary>Sends a bounded request using the released wire codec, keeping all native reads on the UI thread.</summary>
    /// <param name="operation">The read-only bridge operation.</param>
    /// <param name="payload">Synthetic, bounded operation parameters.</param>
    private async Task<T> BridgeAsync<T>(string operation, object payload)
    {
        var request = new BridgeRequest
        {
            ProtocolVersion = ProtocolConstants.Version,
            RequestId = Guid.NewGuid().ToString("N"),
            Operation = operation,
            PayloadJson = _json.Serialize(payload)
        };
        var response = _json.Deserialize<BridgeResponse>(await ExchangeAsync(PipeNames.ForCurrentUser(), _json.Serialize(request)));
        if (!response.Success || response.RequestId != request.RequestId || response.ProtocolVersion != ProtocolConstants.Version)
            throw new InvalidOperationException("Bridge request failed: " + operation + " (" + response.Error?.Code + ").");
        return _json.Deserialize<T>(response.PayloadJson);
    }

    private async Task<DaemonStatus> DaemonStatusAsync()
    {
        var request = new DaemonRequest { RequestId = Guid.NewGuid().ToString("N"), Method = DaemonProtocol.Status };
        var response = _json.Deserialize<DaemonResponse>(await ExchangeAsync(DaemonPipeNames.ForCurrentUser(), _json.Serialize(request)));
        if (!response.Success || response.RequestId != request.RequestId || response.ProtocolVersion != DaemonProtocol.Version)
            throw new InvalidOperationException("Released daemon status request failed.");
        return _json.Deserialize<DaemonStatus>(response.PayloadJson);
    }

    /// <summary>Bounds each pipe exchange. Disposal interrupts Framework reads that ignore cancellation.</summary>
    /// <param name="pipeName">The unique CI pipe name.</param>
    /// <param name="json">The serialized request, containing no credentials.</param>
    private static async Task<string> ExchangeAsync(string pipeName, string json)
    {
        using var timeout = new CancellationTokenSource(5000);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var abort = timeout.Token.Register(pipe.Dispose);
        await pipe.ConnectAsync(3000, timeout.Token);
        await FrameCodec.WriteAsync(pipe, json, timeout.Token);
        return await FrameCodec.ReadAsync(pipe, timeout.Token) ?? throw new IOException("Pipe closed without a response.");
    }

    private void Check(bool success, string description)
    {
        if (!success) throw new InvalidOperationException(description);
        _passed.Add(description);
    }

    /// <summary>Writes assertion names only into the runner-owned result file.</summary>
    /// <param name="success">Whether every assertion passed.</param>
    /// <param name="failure">The failing stage and bounded diagnostic, when present.</param>
    private void SaveResult(bool success, string? failure)
    {
        var path = Environment.GetEnvironmentVariable("FIDDLER_CLASSIC_COMPATIBILITY_RESULT");
        if (string.IsNullOrEmpty(path) || !Path.GetFullPath(path).StartsWith(
                Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Compatibility result must be inside the disposable runner's temp directory.");
        File.WriteAllText(path + ".pending", _json.Serialize(new { Success = success, Passed = _passed, Failure = failure }));
        File.Move(path + ".pending", path);
    }
}
