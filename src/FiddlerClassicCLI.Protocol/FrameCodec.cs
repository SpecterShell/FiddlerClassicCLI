// Reads and writes bounded length-prefixed UTF-8 JSON frames.
using System.Text;

namespace FiddlerClassicCLI.Protocol;

public static class FrameCodec
{
    /// <summary>
    /// Reads one length-prefixed JSON frame, returning <see langword="null"/> for a clean EOF before a frame begins.
    /// </summary>
    /// <param name="stream">The stream containing the little-endian length prefix and UTF-8 payload.</param>
    /// <param name="cancellationToken">Cancels pending stream reads.</param>
    public static async Task<string?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[sizeof(int)];
        var lengthRead = await ReadExactlyAsync(stream, lengthBuffer, allowCleanEof: true, cancellationToken)
            .ConfigureAwait(false);

        if (lengthRead == 0)
        {
            return null;
        }

        var length = lengthBuffer[0]
            | (lengthBuffer[1] << 8)
            | (lengthBuffer[2] << 16)
            | (lengthBuffer[3] << 24);
        if (length <= 0 || length > ProtocolConstants.MaxFrameBytes)
        {
            throw new InvalidDataException($"Frame length {length} is outside the allowed range.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, allowCleanEof: false, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    /// <summary>
    /// Writes one bounded UTF-8 JSON frame and flushes it to the destination stream.
    /// </summary>
    /// <param name="stream">The stream that receives the framed payload.</param>
    /// <param name="json">The non-empty JSON document to frame.</param>
    /// <param name="cancellationToken">Cancels pending writes or the final flush.</param>
    public static async Task WriteAsync(Stream stream, string json, CancellationToken cancellationToken)
    {
        if (stream == null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (json == null)
        {
            throw new ArgumentNullException(nameof(json));
        }

        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length == 0 || payload.Length > ProtocolConstants.MaxFrameBytes)
        {
            throw new InvalidDataException($"Frame length {payload.Length} is outside the allowed range.");
        }

        var lengthBuffer = new byte[sizeof(int)];
        lengthBuffer[0] = (byte)payload.Length;
        lengthBuffer[1] = (byte)(payload.Length >> 8);
        lengthBuffer[2] = (byte)(payload.Length >> 16);
        lengthBuffer[3] = (byte)(payload.Length >> 24);
        await stream.WriteAsync(lengthBuffer, 0, lengthBuffer.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fills a buffer and distinguishes a clean pre-frame EOF from a truncated frame.
    /// </summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="buffer">The buffer that must be filled.</param>
    /// <param name="allowCleanEof">Whether zero bytes at the initial read should return normally.</param>
    /// <param name="cancellationToken">Cancels pending stream reads.</param>
    private static async Task<int> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        bool allowCleanEof,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                if (allowCleanEof && offset == 0)
                {
                    return 0;
                }

                throw new EndOfStreamException("The stream ended before the frame was complete.");
            }

            offset += read;
        }

        return offset;
    }
}
