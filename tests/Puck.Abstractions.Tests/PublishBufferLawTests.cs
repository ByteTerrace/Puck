namespace Puck.Abstractions.Tests;

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

        Assert.False(condition: buffer.TrySnapshot(value: out var frame));
        Assert.Equal(
            actual: frame,
            expected: default
        );
    }
    [Fact]
    public void ASnapshotReadsTheLatestPublishedFrame() {
        var buffer = new PublishBuffer<Wide>();

        buffer.Publish(value: new Wide(A: 1, B: 1, C: 1, D: 1));
        buffer.Publish(value: new Wide(A: 2, B: 2, C: 2, D: 2));

        Assert.True(condition: buffer.TrySnapshot(value: out var frame));
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
        var ct = TestContext.Current.CancellationToken;

        // The race is arranged by events, never by scheduling: the three threads start together, and each writer holds
        // its second half of publishes until the reader has taken a snapshot, so the reader reads while both writers
        // still publish however the machine schedules them, and it reads at least once.
        using var start = new Barrier(participantCount: 3);
        using var readerSnapshotted = new ManualResetEventSlim(initialState: false);

        buffer.Publish(value: default);

        var writers = Enumerable.Range(count: 2, start: 1).Select(selector: writer => Task.Factory.StartNew(action: () => {
            start.SignalAndWait(cancellationToken: ct);

            for (var value = 1L; (value <= Publishes); value++) {
                if (value == (Publishes / 2)) {
                    readerSnapshotted.Wait(cancellationToken: ct);
                }

                var word = ((value * 2) + writer);

                buffer.Publish(value: new Wide(A: word, B: word, C: word, D: word));
            }

            _ = Interlocked.Decrement(location: ref writing);
        }, cancellationToken: ct, creationOptions: TaskCreationOptions.LongRunning, scheduler: TaskScheduler.Default)).ToArray();
        var reader = Task.Factory.StartNew(action: () => {
            start.SignalAndWait(cancellationToken: ct);

            do {
                if (buffer.TrySnapshot(value: out var frame)) {
                    snapshots++;
                    readerSnapshotted.Set();

                    if (
                        (frame.B != frame.A) ||
                        (frame.C != frame.A) ||
                        (frame.D != frame.A)
                    ) {
                        torn++;
                    }
                }
            } while (Volatile.Read(location: ref writing) != 0);
        }, cancellationToken: ct, creationOptions: TaskCreationOptions.LongRunning, scheduler: TaskScheduler.Default);

        await Task.WhenAll(tasks: [.. writers, reader]);

        Assert.True(condition: (snapshots > 0));
        Assert.Equal(
            actual: torn,
            expected: 0L
        );
    }
    [Fact]
    public void TheVersionCountsCompletedPublications() {
        var buffer = new PublishBuffer<Wide>();

        Assert.Equal(
            actual: buffer.Version,
            expected: 0L
        );

        buffer.Publish(value: new Wide(A: 1, B: 1, C: 1, D: 1));
        Assert.Equal(
            actual: buffer.Version,
            expected: 1L
        );

        buffer.Publish(value: new Wide(A: 2, B: 2, C: 2, D: 2));
        Assert.Equal(
            actual: buffer.Version,
            expected: 2L
        );
    }
    [Fact]
    public async Task AReaderRacingOneWriterNeverSeesAValueRegress() {
        const long Publishes = 200_000;

        var buffer = new PublishBuffer<Wide>();
        var stop = 0;
        var ct = TestContext.Current.CancellationToken;
        using var firstRead = new ManualResetEventSlim(initialState: false);

        buffer.Publish(value: default);

        // The writer holds its remaining publications until the reader has read once, so the reads overlap writes.
        var writer = Task.Factory.StartNew(action: () => {
            try {
                firstRead.Wait(cancellationToken: ct);

                for (var value = 1L; (value < Publishes); value++) {
                    buffer.Publish(value: new Wide(A: value, B: value, C: value, D: value));
                }
            } finally {
                Volatile.Write(location: ref stop, value: 1);
            }
        }, cancellationToken: ct, creationOptions: TaskCreationOptions.LongRunning, scheduler: TaskScheduler.Default);
        var reader = Task.Factory.StartNew(action: () => {
            var last = -1L;

            try {
                do {
                    Assert.True(condition: buffer.TrySnapshot(value: out var value));
                    firstRead.Set();
                    Assert.True(condition: (value.A >= last), userMessage: $"{value.A} after {last}");
                    Assert.True(condition: ((value.B == value.A) && (value.C == value.A) && (value.D == value.A)), userMessage: $"torn {value}");
                    last = value.A;
                } while (Volatile.Read(location: ref stop) == 0);
            } finally {
                firstRead.Set();
            }

            Assert.True(condition: buffer.TrySnapshot(value: out var final));
            Assert.Equal(
                actual: final.A,
                expected: (Publishes - 1)
            );
        }, cancellationToken: ct, creationOptions: TaskCreationOptions.LongRunning, scheduler: TaskScheduler.Default);

        await Task.WhenAll(tasks: [writer, reader]);
    }
    [Fact]
    public void ASteadyPublishAndSnapshotAllocateNothing() {
        var buffer = new PublishBuffer<Wide>();
        var frame = new Wide(A: 3, B: 3, C: 3, D: 3);

        buffer.Publish(value: frame);
        _ = buffer.TrySnapshot(value: out _);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; (index < 64); index++) {
            buffer.Publish(value: frame);
            _ = buffer.TrySnapshot(value: out _);
        }

        Assert.Equal(
            actual: (GC.GetAllocatedBytesForCurrentThread() - before),
            expected: 0L
        );
    }
}
