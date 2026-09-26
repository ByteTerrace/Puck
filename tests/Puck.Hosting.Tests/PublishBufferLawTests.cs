namespace Puck.Hosting.Tests;

/// <summary>
/// Laws for <see cref="PublishBuffer{T}"/>, the one slot a writer publishes frames into and a reader snapshots from: an
/// empty buffer reports no frame, a snapshot reads the latest published frame, a reader racing writers never sees a
/// frame half written, and a steady publish and snapshot allocate nothing.
/// </summary>
public sealed class PublishBufferLawTests {
    // A frame wide enough that a copy of it is several machine words, so a torn read would show as mismatched halves.
    private readonly record struct Wide(long A, long B, long C, long D);

    [Fact]
    public void AnEmptyBufferReportsNoFrame() {
        var buffer = new PublishBuffer<Wide>();

        Assert.False(condition: buffer.TrySnapshot(frame: out var frame));
        Assert.Equal(
            actual: frame,
            expected: default
        );
    }
    [Fact]
    public void ASnapshotReadsTheLatestPublishedFrame() {
        var buffer = new PublishBuffer<Wide>();

        buffer.Publish(frame: new Wide(A: 1, B: 1, C: 1, D: 1));
        buffer.Publish(frame: new Wide(A: 2, B: 2, C: 2, D: 2));

        Assert.True(condition: buffer.TrySnapshot(frame: out var frame));
        Assert.Equal(
            actual: frame,
            expected: new Wide(A: 2, B: 2, C: 2, D: 2)
        );
    }
    [Fact]
    public async Task AReaderRacingTwoWritersNeverSeesAFrameHalfWritten() {
        const long Publishes = 200_000;

        var buffer = new PublishBuffer<Wide>();
        var torn = 0L;
        var snapshots = 0L;
        var writing = 2;

        buffer.Publish(frame: default);

        var writers = Enumerable.Range(count: 2, start: 1).Select(selector: writer => Task.Run(action: () => {
            for (var value = 1L; (value <= Publishes); value++) {
                var word = ((value * 2) + writer);

                buffer.Publish(frame: new Wide(A: word, B: word, C: word, D: word));
            }

            _ = Interlocked.Decrement(location: ref writing);
        }, cancellationToken: TestContext.Current.CancellationToken)).ToArray();
        var reader = Task.Run(action: () => {
            while (Volatile.Read(location: ref writing) != 0) {
                if (buffer.TrySnapshot(frame: out var frame)) {
                    snapshots++;

                    if (
                        (frame.B != frame.A) ||
                        (frame.C != frame.A) ||
                        (frame.D != frame.A)
                    ) {
                        torn++;
                    }
                }
            }
        }, cancellationToken: TestContext.Current.CancellationToken);

        await Task.WhenAll(tasks: [.. writers, reader]);

        Assert.True(condition: (snapshots > 0));
        Assert.Equal(
            actual: torn,
            expected: 0L
        );
    }
    [Fact]
    public void ASteadyPublishAndSnapshotAllocateNothing() {
        var buffer = new PublishBuffer<Wide>();
        var frame = new Wide(A: 3, B: 3, C: 3, D: 3);

        buffer.Publish(frame: frame);
        _ = buffer.TrySnapshot(frame: out _);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; (index < 64); index++) {
            buffer.Publish(frame: frame);
            _ = buffer.TrySnapshot(frame: out _);
        }

        Assert.Equal(
            actual: (GC.GetAllocatedBytesForCurrentThread() - before),
            expected: 0L
        );
    }
}
