namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="PublishBuffer{T}"/>, the latest-value handoff writers publish frames into and readers read from:
/// an empty buffer reports no frame, a buffer of images starts with a complete one, a read sees the latest published
/// frame, readers racing writers never see a frame half written or going back, a reader never waits on a writer
/// stalled mid-publish, a held read keeps its frame while writers publish on, and a steady publish and read allocate
/// nothing.
/// </summary>
public sealed class PublishBufferLawTests {
    // A frame wide enough that a copy of it is several machine words, so a torn read would show as mismatched halves.
    private readonly record struct Wide(long A, long B, long C, long D);

    private static Wide Of(long value) => new(A: value, B: value, C: value, D: value);

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
    public void ABufferOfImagesStartsWithACompleteImage() {
        var buffer = new PublishBuffer<long[]>(create: static () => [7L, 7L]);

        buffer.Read(state: 0, read: static (in long[] image, long version, int state) => {
            Assert.Equal(actual: image, expected: [7L, 7L]);
            Assert.Equal(actual: version, expected: 0L);
            return 0;
        });
    }
    [Fact]
    public async Task ReadersRacingTwoWritersNeverSeeAFrameHalfWritten() {
        const long Publishes = 200_000;
        const int Readers = 8;

        var buffer = new PublishBuffer<Wide>();
        var torn = 0L;
        var snapshots = 0L;
        var writing = 2;
        var ct = TestContext.Current.CancellationToken;

        // The race is arranged by events, never by scheduling: every thread starts together, and each writer holds its
        // second half of publishes until both readers have read, so the readers read while both writers still publish
        // however the machine schedules them, and each reads at least once.
        using var start = new Barrier(participantCount: (2 + Readers));
        using var readersRead = new CountdownEvent(initialCount: Readers);

        buffer.Publish(value: default);

        var writers = Enumerable.Range(count: 2, start: 1).Select(selector: writer => Task.Factory.StartNew(action: () => {
            start.SignalAndWait(cancellationToken: ct);

            for (var value = 1L; (value <= Publishes); value++) {
                if (value == (Publishes / 2)) {
                    readersRead.Wait(cancellationToken: ct);
                }

                buffer.Publish(value: Of(value: ((value * 2) + writer)));
            }

            _ = Interlocked.Decrement(location: ref writing);
        }, cancellationToken: ct, creationOptions: TaskCreationOptions.LongRunning, scheduler: TaskScheduler.Default)).ToArray();
        var readers = Enumerable.Range(count: Readers, start: 0).Select(selector: reader => Task.Factory.StartNew(action: () => {
            var counted = false;

            start.SignalAndWait(cancellationToken: ct);

            do {
                if (buffer.TrySnapshot(value: out var frame)) {
                    _ = Interlocked.Increment(location: ref snapshots);

                    if (!counted) {
                        counted = true;
                        _ = readersRead.Signal();
                    }
                    if (
                        (frame.B != frame.A) ||
                        (frame.C != frame.A) ||
                        (frame.D != frame.A)
                    ) {
                        _ = Interlocked.Increment(location: ref torn);
                    }
                }
            } while (Volatile.Read(location: ref writing) != 0);
        }, cancellationToken: ct, creationOptions: TaskCreationOptions.LongRunning, scheduler: TaskScheduler.Default)).ToArray();

        await Task.WhenAll(tasks: [.. writers, .. readers]);

        Assert.True(condition: (snapshots >= Readers));
        Assert.Equal(
            actual: torn,
            expected: 0L
        );
        // Retire the final publication too: every balanced read must leave every retired slot reusable.
        buffer.Publish(value: default);
        var slots = Columns(arena: Arena(buffer: buffer));

        foreach (Array column in slots) {
            Assert.Equal(expected: 0L, actual: Pending(column: column));
        }
    }
    [Fact]
    public async Task AReaderNeverWaitsOnAWriterStalledMidPublish() {
        var ct = TestContext.Current.CancellationToken;
        var buffer = new PublishBuffer<long[]>(create: static () => new long[2]);
        using var stalled = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        buffer.Publish(state: 1L, fill: static (ref long[] image, long value) => image.AsSpan().Fill(value: value));
        // The writer stops between the two halves of its image and holds there until released.
        var writer = Task.Run(action: () => buffer.Publish(state: (stalled, release, ct), fill: static (ref long[] image, (ManualResetEventSlim Stalled, ManualResetEventSlim Release, CancellationToken Token) gates) => {
            image[0] = 2L;
            gates.Stalled.Set();
            gates.Release.Wait(cancellationToken: gates.Token);
            image[1] = 2L;
        }), cancellationToken: ct);

        try {
            Assert.True(condition: stalled.Wait(cancellationToken: ct, timeout: TimeSpan.FromSeconds(seconds: 30)));
            // A read that does not finish within the bound waited on the writer.
            var image = await Task.Run(function: () => buffer.Read(state: 0,
                read: static (in long[] value, long version, int state) => value.ToArray()), cancellationToken: ct)
                .WaitAsync(cancellationToken: ct, timeout: TimeSpan.FromSeconds(seconds: 30));

            Assert.Equal(actual: image, expected: [1L, 1L]);
        } finally {
            release.Set();
        }
        await writer;
        Assert.True(condition: buffer.TrySnapshot(value: out var published));
        Assert.Equal(actual: published, expected: [2L, 2L]);
    }
    [Fact]
    public void AHeldReadKeepsItsFrameWhileWritersPublishOn() {
        var buffer = new PublishBuffer<Wide>();

        buffer.Publish(value: Of(value: 1));
        buffer.Read(state: buffer, read: static (in Wide first, long firstVersion, PublishBuffer<Wide> owner) => {
            owner.Publish(value: Of(value: 2));
            owner.Read(state: owner, read: (in Wide second, long secondVersion, PublishBuffer<Wide> writer) => {
                for (var value = 3L; (value <= 64L); value++) { writer.Publish(value: Of(value: value)); }
                Assert.Equal(actual: second, expected: Of(value: 2));
                Assert.Equal(actual: secondVersion, expected: 2L);
                return 0;
            });
            Assert.Equal(actual: first, expected: Of(value: 1));
            Assert.Equal(actual: firstVersion, expected: 1L);
            return 0;
        });
        Assert.True(condition: buffer.TrySnapshot(value: out var latest));
        Assert.Equal(actual: latest, expected: Of(value: 64));
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
    [Fact]
    public void BothPublicationOverloadsRefuseInsideAFill() {
        var buffer = new PublishBuffer<Wide>();

        buffer.Publish(value: Of(value: 1));
        buffer.Publish(state: buffer, fill: static (ref Wide value, PublishBuffer<Wide> owner) => {
            Assert.Throws<InvalidOperationException>(testCode: () => owner.Publish(value: Of(value: 99)));
            Assert.Throws<InvalidOperationException>(testCode: () => owner.Publish(state: 99L, fill: static (ref Wide nested, long number) => nested = Of(value: number)));
            value = Of(value: 2);
        });
        Assert.Equal(expected: 2L, actual: buffer.Version);
        Assert.True(condition: buffer.TrySnapshot(value: out var value));
        Assert.Equal(expected: Of(value: 2), actual: value);
    }
    [Fact]
    public void ReaderAdmissionsFoldWithoutRefusalIncludingBeforeFirstPublication() {
        var buffer = new PublishBuffer<Wide>();
        var arena = Arena(buffer: buffer);
        var states = ((long[])arena.GetType().GetField(bindingAttr: Private, name: "m_states")!.GetValue(obj: arena)!);
        const long FoldAt = (1L << 32);

        states[0] = (FoldAt - 1);
        Assert.False(condition: buffer.TrySnapshot(value: out _));
        Assert.Equal(0L, states[0]);
        buffer.Publish(value: Of(value: 1));
        var column = ((Array)Columns(arena: arena).GetValue(index: 0)!);
        var slot = column.GetValue(index: 0)!;

        slot.GetType().GetField(name: "Pending")!.SetValue(obj: slot, value: -(FoldAt - 1));
        column.SetValue(index: 0, value: slot);
        states[0] = (1L << 48) | (FoldAt - 1);
        Assert.True(condition: buffer.TrySnapshot(value: out var value));
        Assert.Equal(Of(value: 1), value);
        Assert.Equal((1L << 48), states[0]);
        Assert.Equal(0L, Pending(column: column));
        buffer.Publish(value: Of(value: 2));
        Assert.Equal(0L, Pending(column: column));
        // Model every fold losing its CAS until the carry guard is reached: refusal cannot change the index.
        const long Limit = (((1L << 48) - 1) - (1L << 32));
        var before = states[0] = (2L << 48) | Limit;

        Assert.Throws<InvalidOperationException>(testCode: () => buffer.TrySnapshot(value: out _));
        Assert.Equal(expected: before, actual: states[0]);
    }
    [Fact]
    public void RetiredValueSlotsClearButHeldValuesAndFactoryImagesSurvive() {
        var arena = new PublishArena<object>(capacity: 2);
        var first = new object();
        var latest = new object();

        arena.Publish(entry: 0, value: first);
        arena.Read(entry: 0, state: 0, read: (in object held, long version, int state) => {
            arena.Publish(entry: 0, value: latest);
            var retained = new HashSet<object>();

            arena.VisitRetained(state: retained, visit: static (in object value, long number, HashSet<object> found) => {
                if (value is not null) { found.Add(item: value); }
                return 0;
            });
            Assert.Contains(expected: first, set: retained);
            Assert.Same(actual: held, expected: first);
            return 0;
        });
        var after = new HashSet<object>();

        arena.VisitRetained(state: after, visit: static (in object value, long version, HashSet<object> found) => {
            if (value is not null) { found.Add(item: value); }
            return 0;
        });
        Assert.DoesNotContain(expected: first, set: after);
        Assert.Contains(expected: latest, set: after);
        arena.Publish(entry: 0, value: new object());
        after.Clear();
        arena.VisitRetained(state: after, visit: static (in object value, long version, HashSet<object> found) => {
            if (value is not null) { found.Add(item: value); }
            return 0;
        });
        Assert.DoesNotContain(expected: latest, set: after);
        var images = new PublishBuffer<long[]>(create: static () => new long[2]);

        for (var index = 0; (index < 8); index++) {
            images.Publish(state: 7L, fill: static (ref long[] image, long value) => image.AsSpan().Fill(value: value));
        }
        Assert.True(condition: images.TrySnapshot(value: out var image));
        Assert.Equal(actualArray: image, expectedSpan: [7L, 7L]);
    }

    private const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    private static object Arena<T>(PublishBuffer<T> buffer) => typeof(PublishBuffer<T>).GetField(bindingAttr: Private, name: "m_arena")!.GetValue(obj: buffer)!;
    private static Array Columns(object arena) => ((Array)arena.GetType().GetField(bindingAttr: Private, name: "m_slots")!.GetValue(obj: arena)!);
    private static long Pending(Array column) {
        var slot = column.GetValue(index: 0)!;

        return ((long)slot.GetType().GetField(name: "Pending")!.GetValue(obj: slot)!);
    }

    [Fact]
    public void AScopedReadReleasesOnThrowAndAllocatesNothingInSteadyUse() {
        var buffer = new PublishBuffer<Wide>();

        buffer.Publish(value: Of(value: 1));
        Assert.Throws<InvalidOperationException>(testCode: () => buffer.Read<int, int>(state: 0, read: static (in Wide value, long version, int state) => throw new InvalidOperationException()));
        PublishBuffer<Wide>.ReadValue<int, long> read = static (in Wide value, long version, int state) => value.A;

        _ = buffer.Read(read: read, state: 0);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; (index < 64); index++) {
            _ = buffer.Read(read: read, state: 0);
            buffer.Publish(value: Of(value: index));
        }
        Assert.Equal(expected: 0L, actual: (GC.GetAllocatedBytesForCurrentThread() - before));
    }
}
