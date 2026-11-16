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
        using var lease = buffer.Read();

        Assert.True(condition: lease.HasValue);
        Assert.Equal(actual: lease.Value, expected: [7L, 7L]);
        Assert.Equal(actual: lease.Version, expected: 0L);
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
        var slots = ((Array)typeof(PublishBuffer<Wide>).GetField(bindingAttr: System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, name: "m_slots")!.GetValue(obj: buffer)!);

        foreach (var slot in slots) {
            Assert.Equal(expected: 0L, actual: ((long)slot!.GetType().GetField(name: "Pending")!.GetValue(obj: slot)!));
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
            var image = await Task.Run(function: () => {
                using var lease = buffer.Read();

                return lease.Value.ToArray();
            }, cancellationToken: ct).WaitAsync(cancellationToken: ct, timeout: TimeSpan.FromSeconds(seconds: 30));

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
        using var first = buffer.Read();

        buffer.Publish(value: Of(value: 2));
        using var second = buffer.Read();

        // Every slot is now published or held, so a writer that waited on a reader would never return from these.
        for (var value = 3L; (value <= 64L); value++) {
            buffer.Publish(value: Of(value: value));
        }

        Assert.Equal(actual: first.Value, expected: Of(value: 1));
        Assert.Equal(actual: first.Version, expected: 1L);
        Assert.Equal(actual: second.Value, expected: Of(value: 2));
        Assert.Equal(actual: second.Version, expected: 2L);
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
    public void DisposingALeaseTwiceCannotReleaseAnotherReaderIncludingThroughAnAlias() {
        var buffer = new PublishBuffer<Wide>();

        buffer.Publish(value: Of(value: 1));
        var first = buffer.Read();
        var alias = first;
        using var other = buffer.Read();

        first.Dispose();
        alias.Dispose();
        first.Dispose();
        Assert.False(condition: first.HasValue);
        for (var value = 2L; (value < 32L); value++) { buffer.Publish(value: Of(value: value)); }
        Assert.Equal(expected: Of(value: 1), actual: other.Value);
        other.Dispose();
        // A spurious leave would keep one retired slot at -1 after the real reader left.
        var slots = ((Array)typeof(PublishBuffer<Wide>).GetField(bindingAttr: System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, name: "m_slots")!.GetValue(obj: buffer)!);

        foreach (var slot in slots) {
            Assert.Equal(expected: 0L, actual: ((long)slot!.GetType().GetField(name: "Pending")!.GetValue(obj: slot)!));
        }
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
    public void ReaderAdmissionRefusesBeforeTheCounterCanCarryIntoTheSlotIndex() {
        var buffer = new PublishBuffer<Wide>();

        buffer.Publish(value: Of(value: 1));
        const long Mask = ((1L << 48) - 1);
        const long Limit = (Mask - (1L << 32));
        var state = typeof(PublishBuffer<Wide>).GetField(bindingAttr: System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, name: "m_state")!;
        var slots = ((Array)typeof(PublishBuffer<Wide>).GetField(bindingAttr: System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, name: "m_slots")!.GetValue(obj: buffer)!);
        // Seed balanced earlier reads of this publication, including their already completed leaves.
        slots.GetValue(index: 0)!.GetType().GetField(name: "Pending")!.SetValue(obj: slots.GetValue(index: 0), value: -(Limit - 1));
        state.SetValue(obj: buffer, value: (1L << 48) | (Limit - 1));
        Assert.True(condition: buffer.TrySnapshot(value: out var value));
        Assert.Equal(expected: Of(value: 1), actual: value);
        Assert.Throws<InvalidOperationException>(testCode: () => buffer.TrySnapshot(value: out _));
        Assert.Throws<InvalidOperationException>(testCode: () => buffer.Read());
        Assert.Equal(expected: (1L << 48) | Limit, actual: ((long)state.GetValue(obj: buffer)!));
        buffer.Publish(value: Of(value: 2));
        Assert.True(condition: buffer.TrySnapshot(value: out value));
        Assert.Equal(expected: Of(value: 2), actual: value);
        Assert.Equal(expected: 0L, actual: ((long)slots.GetValue(index: 0)!.GetType().GetField(name: "Pending")!.GetValue(obj: slots.GetValue(index: 0))!));
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
