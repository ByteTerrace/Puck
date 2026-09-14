using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

public sealed class PeerStreamTests {
    [Fact]
    public async Task ConcurrentWritesKeepEverySegmentOfEachWriteTogether() {
        using var deadline = Laws.SocketDeadline();

        var (a, b, ab, ba) = await PeerTestSupport.ConnectAsync(ct: deadline.Token);
        await using var ownerA = a; await using var ownerB = b;
        await using var sender = new PeerStream(link: ab); await using var receiver = new PeerStream(link: ba);
        var first = new byte[(3 * PeerWireProtocol.MaxMessagePayloadBytes)];
        var second = new byte[first.Length];

        Array.Fill(
            array: first,
            value: ((byte)17)
        ); Array.Fill(
            array: second,
            value: ((byte)83)
        );
        var actual = new byte[(first.Length + second.Length)];
        var read = receiver.ReadExactlyAsync(
            buffer: actual,
            cancellationToken: deadline.Token
        ).AsTask();

        await Task.WhenAll(
            sender.WriteAsync(
                buffer: first,
                cancellationToken: deadline.Token
            ).AsTask(),
            sender.WriteAsync(
                buffer: second,
                cancellationToken: deadline.Token
            ).AsTask()
        );
        await read;
        Assert.True(condition: actual.AsSpan(
            0,
            first.Length
        ).SequenceEqual(other: first));
        Assert.True(condition: actual.AsSpan(start: first.Length).SequenceEqual(other: second));
    }
    [Fact]
    public async Task DisposalUnblocksThePendingRead() {
        using var deadline = Laws.SocketDeadline();

        var (a, b, ab, ba) = await PeerTestSupport.ConnectAsync(ct: deadline.Token);
        await using var ownerA = a; await using var ownerB = b;
        await using var sender = new PeerStream(link: ab); await using var receiver = new PeerStream(link: ba);
        var read = receiver.ReadAsync(
            buffer: new byte[1],
            cancellationToken: deadline.Token
        ).AsTask();

        Assert.False(condition: read.IsCompleted);
        await receiver.DisposeAsync();
        Assert.Equal(
            0,
            await read
        );
        await Assert.ThrowsAsync<ObjectDisposedException>(testCode: () => receiver.ReadAsync(
            buffer: new byte[1],
            cancellationToken: deadline.Token
        ).AsTask());
    }
    [InlineData(1)]
    [InlineData(PeerWireProtocol.MaxMessagePayloadBytes)]
    [InlineData((PeerWireProtocol.MaxMessagePayloadBytes + 1))]
    [InlineData(((8 * 1024) * 1024))]
    [Theory]
    public async Task SegmentedStreamPreservesEveryByteAcrossTheBoundedMessageQueue(int size) {
        using var deadline = Laws.SocketDeadline();

        var (a, b, ab, ba) = await PeerTestSupport.ConnectAsync(ct: deadline.Token);
        await using var ownerA = a; await using var ownerB = b;
        await using var sender = new PeerStream(link: ab); await using var receiver = new PeerStream(link: ba);
        var expected = new byte[size];

        for (var i = 0; (i < size); i++) { expected[i] = ((byte)((i * 17) + (i / 251))); }
        var actual = new byte[size];
        var read = receiver.ReadExactlyAsync(
            buffer: actual,
            cancellationToken: deadline.Token
        ).AsTask();

        await sender.WriteAsync(
            buffer: expected,
            cancellationToken: deadline.Token
        );
        await read;
        Assert.Equal(
            actual: actual,
            expected: expected
        );
        await sender.CompleteWritesAsync(ct: deadline.Token);
        Assert.Equal(
            0,
            await receiver.ReadAsync(
                buffer: new byte[1],
                cancellationToken: deadline.Token
            )
        );
        await receiver.WriteAsync(
            buffer: new byte[] { 42 },
            cancellationToken: deadline.Token
        );
        var reply = new byte[1];

        await sender.ReadExactlyAsync(
            buffer: reply,
            cancellationToken: deadline.Token
        );
        Assert.Equal(
            42,
            reply[0]
        );
        await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => sender.WriteAsync(
            buffer: new byte[] { 1 },
            cancellationToken: deadline.Token
        ).AsTask());
    }
    [Fact]
    public async Task SmallReadsCrossMessageBoundariesAndCancelledReadLosesNoBytes() {
        using var deadline = Laws.SocketDeadline();

        var (a, b, ab, ba) = await PeerTestSupport.ConnectAsync(ct: deadline.Token);
        await using var ownerA = a; await using var ownerB = b;
        await using var sender = new PeerStream(link: ab); await using var receiver = new PeerStream(link: ba);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => receiver.ReadAsync(
            buffer: new byte[1],
            cancellationToken: cancelled.Token
        ).AsTask());
        await sender.WriteAsync(
            buffer: new byte[] { 1, 2, 3 },
            cancellationToken: deadline.Token
        );
        await sender.WriteAsync(
            buffer: new byte[] { 4, 5 },
            cancellationToken: deadline.Token
        );
        var values = new byte[5];

        for (var i = 0; (i < values.Length); i++) {
            Assert.Equal(
                1,
                await receiver.ReadAsync(
                    buffer: values.AsMemory(
                        length: 1,
                        start: i
                    ),
                    cancellationToken: deadline.Token
                )
            );
        }
        Assert.Equal(
            actual: values,
            expected: new byte[] { 1, 2, 3, 4, 5 }
        );
        await sender.WriteAsync(
            buffer: ReadOnlyMemory<byte>.Empty,
            cancellationToken: deadline.Token
        );
        await sender.WriteAsync(
            buffer: new byte[] { 6 },
            cancellationToken: deadline.Token
        );
        Assert.Equal(
            1,
            await receiver.ReadAsync(
                buffer: values.AsMemory(
                    length: 1,
                    start: 0
                ),
                cancellationToken: deadline.Token
            )
        );
        Assert.Equal(
            6,
            values[0]
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task TerminalDrainWaitsPastHalfCloseForTheFinalReply(bool cancelDrain) {
        using var deadline = Laws.SocketDeadline();

        var (a, b, ab, ba) = await PeerTestSupport.ConnectAsync(ct: deadline.Token);
        await using var ownerA = a; await using var ownerB = b;
        await using var client = new PeerStream(link: ab); await using var server = new PeerStream(link: ba);

        await client.CompleteWritesAsync(ct: deadline.Token);
        Assert.Equal(
            0,
            await server.ReadAsync(
                buffer: new byte[1],
                cancellationToken: deadline.Token
            )
        );

        await server.WriteAsync(
            buffer: new byte[] { 42 },
            cancellationToken: deadline.Token
        );
        using var drainLifetime = CancellationTokenSource.CreateLinkedTokenSource(token: deadline.Token);
        var draining = StreamDrain.UntilClosedAsync(
            server,
            drainLifetime.Token
        );

        Assert.False(
            condition: draining.IsCompleted,
            userMessage: "A sending-direction EOF must not close the reply's delivery window."
        );
        var reply = new byte[1];

        await client.ReadExactlyAsync(
            buffer: reply,
            cancellationToken: deadline.Token
        );
        Assert.Equal(
            42,
            reply[0]
        );

        if (cancelDrain) { drainLifetime.Cancel(); } else { await client.DisposeAsync(); }
        await draining.WaitAsync(cancellationToken: deadline.Token);
    }
}
