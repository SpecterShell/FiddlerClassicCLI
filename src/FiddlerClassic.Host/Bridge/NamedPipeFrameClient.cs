// Exchanges one framed JSON request and response over a Windows named pipe.
using System.IO.Pipes;
using FiddlerClassic.Protocol;

namespace FiddlerClassic.Host.Bridge;

internal static class NamedPipeFrameClient
{
    /// <summary>
    /// Connects to a local pipe, writes one framed request, and reads one required framed response.
    /// </summary>
    /// <param name="pipeName">The local Windows pipe name.</param>
    /// <param name="requestJson">The complete JSON request payload.</param>
    /// <param name="cancellationToken">Cancels connection, write, or read work.</param>
    public static async Task<string> ExchangeAsync(
        string pipeName,
        string requestJson,
        CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await FrameCodec.WriteAsync(pipe, requestJson, cancellationToken).ConfigureAwait(false);
        return await FrameCodec.ReadAsync(pipe, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The named-pipe peer closed the connection without a response.");
    }
}
