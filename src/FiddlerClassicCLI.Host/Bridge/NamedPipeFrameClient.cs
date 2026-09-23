// Exchanges one framed JSON request and response over a Windows named pipe.
using System.IO.Pipes;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Host.Bridge;

internal static class NamedPipeFrameClient
{
    /// <summary>
    /// Connects to a local pipe, writes one framed request, and reads one required framed response.
    /// </summary>
    /// <param name="pipeName">The local Windows pipe name.</param>
    /// <param name="requestJson">The complete JSON request payload.</param>
    /// <param name="cancellationToken">Cancels connection, write, or read work.</param>
    /// <param name="onConnected">Optionally records that the peer was reached before exchanging frames.</param>
    public static async Task<string> ExchangeAsync(
        string pipeName,
        string requestJson,
        CancellationToken cancellationToken,
        Action? onConnected = null)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
        onConnected?.Invoke();
        await FrameCodec.WriteAsync(pipe, requestJson, cancellationToken).ConfigureAwait(false);
        return await FrameCodec.ReadAsync(pipe, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The named-pipe peer closed the connection without a response.");
    }
}
