using Xunit;

namespace Puck.Networking.Tests;

public sealed class StreamDrainTests {
    [Fact]
    public async Task DrainsThroughEofWithoutTakingStreamOwnership() {
        using var stream = new MemoryStream(new byte[1025]);
        await StreamDrain.UntilClosedAsync(stream, CancellationToken.None);
        Assert.Equal(stream.Length, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task CancellationStopsAnAlwaysBufferedReaderEvenIfItIgnoresTheToken() {
        using var cancelled = new CancellationTokenSource();
        using var stream = new BufferedSource(cancelled);
        await StreamDrain.UntilClosedAsync(stream, cancelled.Token);
        Assert.Equal(3, stream.Reads);
        Assert.True(stream.CanRead);
    }

    private sealed class BufferedSource(CancellationTokenSource cancellation) : MemoryStream {
        public int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            if (++Reads > 3) { throw new InvalidOperationException("Drain ignored its deadline while data remained buffered."); }
            if (Reads == 3) { cancellation.Cancel(); }
            buffer.Span[0] = 1;
            return ValueTask.FromResult(1);
        }
    }
}
