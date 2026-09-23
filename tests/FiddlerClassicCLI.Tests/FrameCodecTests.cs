// Verifies frame round trips, limits, truncation handling, and clean EOF behavior.
using System.Text;
using FiddlerClassicCLI.Protocol;

namespace FiddlerClassicCLI.Tests;

public sealed class FrameCodecTests
{
    /// <summary>
    /// Verifies framed JSON preserves non-ASCII UTF-8 content through a complete round trip.
    /// </summary>
    [Fact]
    public async Task RoundTripsUtf8Json()
    {
        const string payload = "{\"message\":\"hello \u4e16\u754c\"}";
        await using var stream = new MemoryStream();

        await FrameCodec.WriteAsync(stream, payload, CancellationToken.None);
        stream.Position = 0;

        Assert.Equal(payload, await FrameCodec.ReadAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ProtocolConstants.MaxFrameBytes + 1)]
    public async Task RejectsInvalidFrameLengths(int length)
    {
        var prefix = BitConverter.GetBytes(length);
        await using var stream = new MemoryStream(prefix);

        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCodec.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsTruncatedFrames()
    {
        await using var stream = new MemoryStream();
        await stream.WriteAsync(BitConverter.GetBytes(10), TestContext.Current.CancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("short"), TestContext.Current.CancellationToken);
        stream.Position = 0;

        await Assert.ThrowsAsync<EndOfStreamException>(() => FrameCodec.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task CleanEofReturnsNull()
    {
        await using var stream = new MemoryStream();
        Assert.Null(await FrameCodec.ReadAsync(stream, CancellationToken.None));
    }
}
