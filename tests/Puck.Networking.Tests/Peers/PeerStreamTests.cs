using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

public sealed class PeerStreamTests {
    [Fact]
    public async Task ConcurrentWritesKeepEverySegmentOfEachWriteTogether() {
        using var deadline = Laws.SocketDeadline();
        var (a, b, ab, ba) = await PeerTestSupport.ConnectAsync(deadline.Token);
        await using var ownerA = a; await using var ownerB = b;
        await using var sender = new PeerStream(ab); await using var receiver = new PeerStream(ba);
        var first = new byte[3 * PeerWireProtocol.MaxMessagePayloadBytes];
        var second = new byte[first.Length];
        Array.Fill(first, (byte)17); Array.Fill(second, (byte)83);
        var actual = new byte[first.Length + second.Length];
        var read = receiver.ReadExactlyAsync(actual, deadline.Token).AsTask();
        await Task.WhenAll(sender.WriteAsync(first, deadline.Token).AsTask(), sender.WriteAsync(second, deadline.Token).AsTask());
        await read;
        Assert.True(actual.AsSpan(0, first.Length).SequenceEqual(first));
        Assert.True(actual.AsSpan(first.Length).SequenceEqual(second));
    }

    [Fact]
    public async Task DisposalUnblocksThePendingRead() {
        using var deadline = Laws.SocketDeadline();
        var (a, b, ab, ba) = await PeerTestSupport.ConnectAsync(deadline.Token);
        await using var ownerA = a; await using var ownerB = b;
        await using var sender = new PeerStream(ab); await using var receiver = new PeerStream(ba);
        var read = receiver.ReadAsync(new byte[1], deadline.Token).AsTask();
        Assert.False(read.IsCompleted);
        await receiver.DisposeAsync();
        Assert.Equal(0, await read);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => receiver.ReadAsync(new byte[1], deadline.Token).AsTask());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(PeerWireProtocol.MaxMessagePayloadBytes)]
    [InlineData(PeerWireProtocol.MaxMessagePayloadBytes + 1)]
    [InlineData(8 * 1024 * 1024)]
    public async Task SegmentedStreamPreservesEveryByteAcrossTheBoundedMessageQueue(int size) {
        using var deadline = Laws.SocketDeadline();
        var (a, b, ab, ba) = await PeerTestSupport.ConnectAsync(deadline.Token);
        await using var ownerA = a; await using var ownerB = b;
        await using var sender = new PeerStream(ab); await using var receiver = new PeerStream(ba);
        var expected = new byte[size];
        for (var i = 0; i < size; i++) { expected[i] = (byte)(i * 17 + i / 251); }
        var actual = new byte[size];
        var read = receiver.ReadExactlyAsync(actual, deadline.Token).AsTask();
        await sender.WriteAsync(expected, deadline.Token);
        await read;
        Assert.Equal(expected, actual);
        await sender.CompleteWritesAsync(deadline.Token);
        Assert.Equal(0, await receiver.ReadAsync(new byte[1], deadline.Token));
        await receiver.WriteAsync(new byte[] { 42 }, deadline.Token);
        var reply = new byte[1];
        await sender.ReadExactlyAsync(reply, deadline.Token);
        Assert.Equal(42, reply[0]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.WriteAsync(new byte[] { 1 }, deadline.Token).AsTask());
    }

    [Fact]
    public async Task SmallReadsCrossMessageBoundariesAndCancelledReadLosesNoBytes() {
        using var deadline = Laws.SocketDeadline();
        var (a, b, ab, ba) = await PeerTestSupport.ConnectAsync(deadline.Token);
        await using var ownerA = a; await using var ownerB = b;
        await using var sender = new PeerStream(ab); await using var receiver = new PeerStream(ba);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receiver.ReadAsync(new byte[1], cancelled.Token).AsTask());
        await sender.WriteAsync(new byte[] { 1, 2, 3 }, deadline.Token);
        await sender.WriteAsync(new byte[] { 4, 5 }, deadline.Token);
        var values = new byte[5];
        for (var i = 0; i < values.Length; i++) {
            Assert.Equal(1, await receiver.ReadAsync(values.AsMemory(i, 1), deadline.Token));
        }
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, values);
        await sender.WriteAsync(ReadOnlyMemory<byte>.Empty, deadline.Token);
        await sender.WriteAsync(new byte[] { 6 }, deadline.Token);
        Assert.Equal(1, await receiver.ReadAsync(values.AsMemory(0, 1), deadline.Token));
        Assert.Equal(6, values[0]);
    }
}
