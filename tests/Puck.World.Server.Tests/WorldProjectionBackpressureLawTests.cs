using Puck.World.Protocol;
using Puck.Commands;
using System.Buffers.Binary;
using Xunit;
using System.Numerics;
using System.Threading.Channels;
using Puck.Networking;

namespace Puck.World.Server.Tests;

public sealed class WorldProjectionBackpressureLawTests(ITestOutputHelper output) {
    [Theory]
    [InlineData(WorldDisclosureTier.Replica)]
    [InlineData(WorldDisclosureTier.Presentation)]
    public async Task AHiddenInstallDoesNotChangeThePeersWire(WorldDisclosureTier tier) {
        async Task<byte[]> Received(bool install) {
            var row = new WorldStateRow(CellName.Parse("hand"), CellKind.Int,
                Visibility: new StateVisibility(Hidden: HiddenCells.Omit), Cells: [
                    new StateCell(CellName.Parse("pub"), CellValue.Int(3)),
                    new StateCell(CellName.Parse("secret"), CellValue.Int(7), Visibility: new StateVisibility(Readers: ["seat1"]))]);
            using var fixture = Fixtures.FreshServer(Fixtures.BuildDocument() with { StateRaw = new WorldStateSection(World: [row]) });
            var current = true;
            var sink = new WorldFederationProjectionSink(tier,
                () => new WorldSinkDisclosure(new WorldObserverDisclosure(UpdateSeconds: 0f), -1),
                server: fixture.Server, recipient: Principal.Peer(4, 1), isCurrent: () => current);
            using var attached = fixture.Server.AttachSink(sink);
            fixture.Step();
            if (install) {
                fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateRow(Principal.Console, row with {
                    Cells = [row.Cells![0], row.Cells[1] with { Value = CellValue.Int(19) }]
                }));
            }
            fixture.Step();
            fixture.Step();
            Assert.Null(sink.DetachReason);
            current = false;
            sink.DeliverDefinition(fixture.Server.Definition, fixture.Server.DocumentVersion, stream: default);
            using var wire = new ProjectionWire();
            await sink.StreamAsync(wire, TestContext.Current.CancellationToken);
            // Apply the harness's sole nonce normalization to the actual wire leaves. Preserve all payload bytes,
            // frame kinds, lengths, delivery order and recipient sequence numbers.
            using var read = new MemoryStream(wire.ToArray());
            using var normalized = new MemoryStream();
            var transcript = new WorldDisclosureTranscript(() => 0);
            while (read.Position < read.Length) {
                var frame = await WorldFederationCodec.ReadResponseAsync(read, TestContext.Current.CancellationToken);
                Assert.True(frame.Ok);
                var body = frame.Body.ToArray();
                // A stream's identity is random per stream; its generation and ordinals stay.
                if (frame.Kind is (byte)WorldFederationResponse.Definition or (byte)WorldFederationResponse.ProjectionDelta or (byte)WorldFederationResponse.Snapshot) {
                    body.AsSpan(0, 8).Clear();
                }
                if (frame.Kind is (byte)WorldFederationResponse.Definition or (byte)WorldFederationResponse.ProjectionDelta) {
                    var leaf = body.AsSpan(WorldFederationCodec.StreamStampBytes);
                    var version = new WorldDocumentVersion(new Guid(leaf.Slice(1, 16)), BinaryPrimitives.ReadInt64LittleEndian(leaf[17..]));
                    transcript.Nonce(version).Activation.TryWriteBytes(leaf.Slice(1, 16));
                }
                await WorldFederationCodec.WriteResponseAsync(normalized, (WorldFederationResponse)frame.Kind, body, TestContext.Current.CancellationToken);
            }
            return normalized.ToArray();
        }
        Assert.Equal(await Received(false), await Received(true));
    }
    [Fact]
    public void AChangedAuthorityRouteDetachesWithoutWaitingForTheWire() {
        var current = true;
        var sink = new WorldFederationProjectionSink(
            disclosure: static () => new WorldSinkDisclosure(ObserverBodyIndex: -1, Policy: new WorldObserverDisclosure(UpdateSeconds: 0f)),
            isCurrent: () => current,
            tier: WorldDisclosureTier.Replica
        );
        var hub = new WorldOutputHub();

        using var lease = hub.Subscribe(sink: sink);
        var snapshot = new WorldSnapshot(Authority: "authority/one", Entries: ReadOnlyMemory<EntitySnapshot>.Empty, Revision: 1, StepTicks: 1UL, Tick: 1UL);

        hub.DeliverSnapshot(snapshot: in snapshot);
        current = false;
        snapshot = snapshot with { Authority = "authority/two", Tick = 2UL };
        hub.DeliverSnapshot(snapshot: in snapshot);

        Assert.False(condition: hub.HasTypedSubscribers);
        Assert.Equal(expected: 1, actual: sink.PendingDeliveries);
        Assert.Equal(expected: WorldFederationProjectionSink.InvalidatedDetachReason, actual: sink.DetachReason);
    }
    [Fact]
    public void ABackpressureDetachIsNarratedAsADetachNotAFault() {
        var sink = new WorldFederationProjectionSink(
            disclosure: static () => new WorldSinkDisclosure(ObserverBodyIndex: -1, Policy: new WorldObserverDisclosure(UpdateSeconds: 0f)),
            tier: WorldDisclosureTier.Replica
        );
        var hub = new WorldOutputHub();
        var narration = new RecordingNarrationSink();

        using var narrated = hub.AttachNarrationSink(sink: narration);
        using var lease = hub.Subscribe(sink: sink);

        for (var tick = 1UL; (tick <= ((ulong)(WorldFederationProjectionSink.PendingDeliveryLimit + WorldFederationProjectionSink.StalledDeliveryLimit + 2))); tick++) {
            var snapshot = new WorldSnapshot(Authority: "authority/one", Entries: ReadOnlyMemory<EntitySnapshot>.Empty, Revision: 1, StepTicks: 1UL, Tick: tick);

            hub.DeliverSnapshot(snapshot: in snapshot);
        }

        Assert.False(condition: hub.HasTypedSubscribers);
        var line = Assert.Single(collection: narration.Narrations).Text;

        Assert.Contains(actualString: line, expectedSubstring: WorldFederationProjectionSink.BackpressureDetachReason);
        Assert.DoesNotContain(actualString: line, expectedSubstring: "threw");
        Assert.DoesNotContain(actualString: line, expectedSubstring: "Exception");
        Assert.DoesNotContain(actualString: line, expectedSubstring: " at ");
    }
    [Fact]
    public void ANarrationSinkThatDisposesAnEndingLeaseLeavesTheHealthySubscriberDelivered() {
        var hub = new WorldOutputHub();
        var ending = new EndingSink(endAfterDefinitions: int.MaxValue, endAfterSnapshots: 1);
        var healthy = new EndingSink(endAfterDefinitions: int.MaxValue, endAfterSnapshots: int.MaxValue);
        IDisposable? endingLease = null;

        // The operator's narration reaction to the detach line releases the same lease the hub is detaching.
        using var narrated = hub.AttachNarrationSink(sink: new DisposingNarrationSink(dispose: () => endingLease!.Dispose()));

        endingLease = hub.Subscribe(sink: ending);
        using var healthyLease = hub.Subscribe(sink: healthy);

        for (var tick = 1UL; (tick <= 2UL); tick++) {
            var snapshot = new WorldSnapshot(Authority: "authority/one", Entries: ReadOnlyMemory<EntitySnapshot>.Empty, Revision: 1, StepTicks: 1UL, Tick: tick);

            hub.DeliverSnapshot(snapshot: in snapshot);
            Assert.True(condition: hub.HasTypedSubscribers);
        }

        Assert.Equal(expected: 2, actual: healthy.Snapshots);
        Assert.Equal(expected: 1, actual: ending.Snapshots);
    }
    [Fact]
    public void ASinkThatEndsOnItsAttachDefinitionIsHandedNoPrimerSnapshot() {
        using var fixture = Fixtures.FreshServer();
        var sink = new EndingSink(endAfterDefinitions: 1, endAfterSnapshots: int.MaxValue);

        using var lease = fixture.Server.AttachSink(sink: sink);

        fixture.Step();
        fixture.Step();

        Assert.Equal(expected: 1, actual: sink.Definitions);
        Assert.Equal(expected: 0, actual: sink.Snapshots);
        Assert.Equal(expected: 0, actual: sink.DeliveriesAfterEnd);
    }
    [Fact]
    public async Task AStalledProjectionKeepsOnlyItsBoundedPrimerAndDetachesBeforeLaterEpochs() {
        var disclosure = new WorldSinkDisclosure(
            ObserverBodyIndex: -1,
            Policy: new WorldObserverDisclosure(UpdateSeconds: 0f)
        );
        var sink = new WorldFederationProjectionSink(
            disclosure: () => disclosure,
            tier: WorldDisclosureTier.Replica
        );
        var hub = new WorldOutputHub();

        using var lease = hub.Subscribe(sink: sink);

        for (var tick = 1UL; (tick <= ((ulong)(WorldFederationProjectionSink.PendingDeliveryLimit + WorldFederationProjectionSink.StalledDeliveryLimit + 2))); tick++) {
            var snapshot = new WorldSnapshot(
                Authority: "authority/one",
                Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
                Revision: ((int)tick),
                StepTicks: 1UL,
                Tick: tick
            );

            hub.DeliverSnapshot(snapshot: in snapshot);
            Assert.True(condition: (sink.PendingDeliveries <= WorldFederationProjectionSink.PendingDeliveryLimit));
        }

        Assert.False(condition: hub.HasTypedSubscribers);
        Assert.Equal(
            expected: WorldFederationProjectionSink.BackpressureDetachReason,
            actual: sink.DetachReason
        );
        Assert.Equal(
            expected: WorldFederationProjectionSink.PendingDeliveryLimit,
            actual: sink.PendingDeliveries
        );

        var laterEpoch = new WorldSnapshot(
            Authority: "authority/two",
            Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
            Revision: 1,
            StepTicks: 1UL,
            Tick: 1_000UL
        );

        sink.DeliverSnapshot(snapshot: in laterEpoch, stream: default);
        Assert.Equal(
            expected: WorldFederationProjectionSink.PendingDeliveryLimit,
            actual: sink.PendingDeliveries
        );

        using var wire = new ProjectionWire();

        await sink.StreamAsync(
            ct: TestContext.Current.CancellationToken,
            output: wire
        );

        using var decodedWire = new MemoryStream(buffer: wire.ToArray());

        for (var tick = 1UL; (tick <= ((ulong)WorldFederationProjectionSink.PendingDeliveryLimit)); tick++) {
            var frame = await WorldFederationCodec.ReadResponseAsync(
                ct: TestContext.Current.CancellationToken,
                stream: decodedWire
            );

            Assert.True(condition: frame.Ok);
            Assert.Equal(expected: ((byte)WorldFederationResponse.Snapshot), actual: frame.Kind);
            Assert.True(condition: WorldFederationCodec.TryDecodeSnapshot(
                body: frame.Body.Span[WorldFederationCodec.StreamStampBytes..],
                failure: out var failure,
                snapshot: out var decoded
            ), userMessage: failure.ToString());
            Assert.Equal(expected: tick, actual: decoded.Tick);
            Assert.Equal(expected: 0, actual: decoded.Revision);
            Assert.Equal(expected: "authority/one", actual: decoded.Authority);
        }

        var terminal = await WorldFederationCodec.ReadResponseAsync(
            ct: TestContext.Current.CancellationToken,
            stream: decodedWire
        );

        Assert.True(condition: terminal.Ok);
        Assert.Equal(expected: ((byte)WorldFederationResponse.ProjectionInvalidated), actual: terminal.Kind);
        Assert.Equal(
            expected: WorldFederationProjectionSink.BackpressureDetachReason,
            actual: System.Text.Encoding.UTF8.GetString(bytes: terminal.Body.Span)
        );
    }
    // An observer whose wire fell a whole queue behind is not detached. It is sent nothing while its wire drains, and
    // the first delivery after that cuts its stream to a new generation and sends the current snapshot behind the cut,
    // so it catches up on the stream it holds.
    [Fact]
    public async Task ASlowObserverCatchesUpOnItsOpenStreamBehindACut() {
        var ct = TestContext.Current.CancellationToken;
        var sink = new WorldFederationProjectionSink(
            disclosure: static () => new WorldSinkDisclosure(ObserverBodyIndex: -1, Policy: new WorldObserverDisclosure(UpdateSeconds: 0f)),
            tier: WorldDisclosureTier.Replica
        );
        var hub = new WorldOutputHub();
        const ulong Missed = 16UL;
        var last = (((ulong)WorldFederationProjectionSink.PendingDeliveryLimit) + Missed);

        using var lease = hub.Subscribe(sink: sink);

        void Deliver(ulong tick) {
            var snapshot = new WorldSnapshot(Authority: "authority/one", Entries: ReadOnlyMemory<EntitySnapshot>.Empty, Revision: 1, StepTicks: 1UL, Tick: tick);

            hub.DeliverSnapshot(snapshot: in snapshot);
        }
        for (var tick = 1UL; (tick <= last); tick++) {
            Deliver(tick: tick);
        }
        Assert.Null(@object: sink.DetachReason);
        Assert.True(condition: hub.HasTypedSubscribers);
        Assert.Equal(expected: WorldFederationProjectionSink.PendingDeliveryLimit, actual: sink.PendingDeliveries);

        using var wire = new ProjectionWire();
        using var streaming = CancellationTokenSource.CreateLinkedTokenSource(token: ct);
        var stream = sink.StreamAsync(ct: streaming.Token, output: wire);

        Puck.Testing.TestLiveness.Until(reason: static () => "the observer's wire did not drain what was queued", step: () => (wire.Flushes == WorldFederationProjectionSink.PendingDeliveryLimit));
        Deliver(tick: (last + 1UL));
        Assert.Null(@object: sink.DetachReason);
        Puck.Testing.TestLiveness.Until(reason: static () => "the observer's wire did not drain its catch-up", step: () => (wire.Flushes == (WorldFederationProjectionSink.PendingDeliveryLimit + 2)));
        await streaming.CancelAsync();
        try { await stream; } catch (OperationCanceledException) { }

        using var decodedWire = new MemoryStream(buffer: wire.ToArray());
        var generation = 0L;

        for (var tick = 1UL; (tick <= ((ulong)WorldFederationProjectionSink.PendingDeliveryLimit)); tick++) {
            var frame = await WorldFederationCodec.ReadResponseAsync(ct: ct, stream: decodedWire);

            Assert.Equal(expected: ((byte)WorldFederationResponse.Snapshot), actual: frame.Kind);
            Assert.True(condition: WorldFederationCodec.TryReadStamped(frame.Body.Span, out var stamp, out _, out _));
            Assert.True(condition: WorldFederationCodec.TryDecodeSnapshot(body: frame.Body.Span[WorldFederationCodec.StreamStampBytes..], failure: out var failure, snapshot: out var decoded), userMessage: failure.ToString());
            Assert.Equal(expected: tick, actual: decoded.Tick);
            generation = stamp.Generation;
        }
        var cutFrame = await WorldFederationCodec.ReadResponseAsync(ct: ct, stream: decodedWire);

        Assert.Equal(expected: ((byte)WorldFederationResponse.Cut), actual: cutFrame.Kind);
        Assert.True(condition: WorldFederationCodec.TryDecodeCut(body: cutFrame.Body.Span, stream: out var cut, terminal: out var terminal));
        Assert.False(condition: terminal);
        Assert.True(condition: (cut.Generation > generation));
        var caughtUp = await WorldFederationCodec.ReadResponseAsync(ct: ct, stream: decodedWire);

        Assert.Equal(expected: ((byte)WorldFederationResponse.Snapshot), actual: caughtUp.Kind);
        Assert.True(condition: WorldFederationCodec.TryReadStamped(caughtUp.Body.Span, out var resumed, out _, out _));
        Assert.Equal(expected: cut.Generation, actual: resumed.Generation);
        Assert.True(condition: WorldFederationCodec.TryDecodeSnapshot(body: caughtUp.Body.Span[WorldFederationCodec.StreamStampBytes..], failure: out var resumedFailure, snapshot: out var current), userMessage: resumedFailure.ToString());
        Assert.Equal(expected: (last + 1UL), actual: current.Tick);
    }
    // A catch-up rides a delivery, and a paused authority makes none. An observer that fell a whole queue behind a real
    // authority, and whose wire then drained while the host held that authority paused, is sent nothing however many
    // host steps pass over the paused world: it keeps what it held. The first tick after the authority resumes cuts the
    // stream to a new generation and sends the whole document and a primer snapshot of that tick behind the cut.
    [Fact]
    public async Task ASlowObserverOfAPausedAuthorityCatchesUpWhenItResumes() {
        const string Row = "row-a";
        var ct = TestContext.Current.CancellationToken;
        using var stateRoot = new Puck.Testing.TemporaryDirectory(prefix: "puck-paused-observer-tests-");
        using var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            admitsSpawn: true,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: Guid.NewGuid(),
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: stateRoot.RootPath)
        );
        using var row = HostRow.Build(name: Row);
        var sink = new WorldFederationProjectionSink(
            disclosure: static () => new WorldSinkDisclosure(ObserverBodyIndex: -1, Policy: new WorldObserverDisclosure(UpdateSeconds: 0f)),
            server: row.Server,
            tier: WorldDisclosureTier.Replica
        );

        host.Admit(row: row.Instance);
        using var attached = row.Server.AttachSink(sink: sink);

        // The observer's wire is not read, so it falls a whole queue behind while the world steps.
        for (var tick = 0; (tick < (WorldFederationProjectionSink.PendingDeliveryLimit + 16)); tick++) {
            host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        }
        Assert.Null(@object: sink.DetachReason);
        Assert.Equal(expected: WorldFederationProjectionSink.PendingDeliveryLimit, actual: sink.PendingDeliveries);

        // The authority pauses, and then the wire drains everything that was queued.
        Assert.True(condition: host.TryPause(name: Row, reason: out var pauseReason), userMessage: pauseReason);
        var pausedAt = row.Instance.CompletedTicks;
        using var wire = new ProjectionWire();
        using var streaming = CancellationTokenSource.CreateLinkedTokenSource(token: ct);
        var stream = sink.StreamAsync(ct: streaming.Token, output: wire);

        Puck.Testing.TestLiveness.Until(reason: static () => "the observer's wire did not drain what was queued", step: () => (wire.Flushes == WorldFederationProjectionSink.PendingDeliveryLimit));
        for (var step = 0; (step < 32); step++) {
            host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        }
        Assert.Equal(expected: pausedAt, actual: row.Instance.CompletedTicks);
        Assert.Equal(expected: 0, actual: sink.PendingDeliveries);
        Assert.Equal(expected: WorldFederationProjectionSink.PendingDeliveryLimit, actual: wire.Flushes);
        Assert.Null(@object: sink.DetachReason);

        // The authority resumes. Its next tick is the delivery the catch-up rides.
        Assert.True(condition: host.TryResume(name: Row, reason: out var resumeReason, wasPaused: out var wasPaused), userMessage: resumeReason);
        Assert.True(condition: wasPaused);
        host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        Assert.Equal(expected: (pausedAt + 1UL), actual: row.Instance.CompletedTicks);
        Puck.Testing.TestLiveness.Until(
            reason: static () => "the observer's wire did not drain its catch-up",
            step: () => ((wire.Flushes > WorldFederationProjectionSink.PendingDeliveryLimit) && (sink.PendingDeliveries == 0))
        );
        var records = wire.Flushes;

        await streaming.CancelAsync();
        try { await stream; } catch (OperationCanceledException) { }

        using var decodedWire = new MemoryStream(buffer: wire.ToArray());
        var generation = 0L;

        for (var record = 0; (record < WorldFederationProjectionSink.PendingDeliveryLimit); record++) {
            var frame = await WorldFederationCodec.ReadResponseAsync(ct: ct, stream: decodedWire);

            Assert.True(condition: WorldFederationCodec.TryReadStamped(frame.Body.Span, out var stamp, out _, out _));
            generation = Math.Max(val1: generation, val2: stamp.Generation);
        }
        var cutFrame = await WorldFederationCodec.ReadResponseAsync(ct: ct, stream: decodedWire);

        Assert.Equal(expected: ((byte)WorldFederationResponse.Cut), actual: cutFrame.Kind);
        Assert.True(condition: WorldFederationCodec.TryDecodeCut(body: cutFrame.Body.Span, stream: out var cut, terminal: out var terminal));
        Assert.False(condition: terminal);
        Assert.True(condition: (cut.Generation > generation));
        var documents = 0;
        var primers = new List<ulong>();

        for (var record = (WorldFederationProjectionSink.PendingDeliveryLimit + 1); (record < records); record++) {
            var frame = await WorldFederationCodec.ReadResponseAsync(ct: ct, stream: decodedWire);

            Assert.True(condition: WorldFederationCodec.TryReadStamped(frame.Body.Span, out var stamp, out _, out _));
            Assert.Equal(expected: cut.Generation, actual: stamp.Generation);
            if (frame.Kind == ((byte)WorldFederationResponse.Definition)) {
                documents++;
            } else if (frame.Kind == ((byte)WorldFederationResponse.Snapshot)) {
                Assert.True(condition: WorldFederationCodec.TryDecodeSnapshot(body: frame.Body.Span[WorldFederationCodec.StreamStampBytes..], failure: out var failure, snapshot: out var primer), userMessage: failure.ToString());
                primers.Add(item: primer.Tick);
            }
        }
        Assert.Equal(expected: 1, actual: documents);
        Assert.Equal(expected: [(pausedAt + 1UL)], actual: primers);
    }

    [Fact]
    public async Task SharedRadiusDeltasUseTheImageEachPeerAcknowledged() {
        const int Sessions = 8;
        var ct = TestContext.Current.CancellationToken;
        var hub = new WorldOutputHub();
        var policy = new WorldSinkDisclosure(new(WorldObserverDisclosureMode.Radius, Radius: 15.5f, UpdateSeconds: 0), 0);
        var wires = new InterestWire[Sessions];
        var pumps = new Task[Sessions];
        var leases = new IDisposable[Sessions];
        var receivers = new WorldSnapshotReceiver[Sessions];
        var rows = Enumerable.Range(0, 64).Select(i => new EntitySnapshot(i, new Vector3(i, 0, 0),
            Quaternion.Identity, Vector3.One, true, 0, 0, 0, EntityContinuity.Continuous)).ToArray();
        var state = new long[] { 1 };
        rows[1] = rows[1] with { State = state };

        for (var i = 0; i < Sessions; i++) {
            var sink = new WorldFederationProjectionSink(WorldDisclosureTier.Replica, () => policy);
            leases[i] = hub.Subscribe(sink);
            receivers[i] = new();
            wires[i] = new();
            pumps[i] = sink.StreamAsync(wires[i], ct);
        }
        try {
            for (ulong tick = 1; tick <= 3; tick++) {
                if (tick == 2) {
                    state[0]++;
                    rows[15] = rows[15] with { Position = new Vector3(50, 0, 0) };
                    rows[16] = rows[16] with { Position = new Vector3(15, 0, 0) };
                }
                var snapshot = new WorldSnapshot(tick, 0, 1, rows, "interest");
                hub.DeliverSnapshot(snapshot);
                var expected = rows.Where(row => policy.Policy.Discloses(row, 0, Vector3.Zero)).ToArray();
                var before = WorldFederationCodec.EncodeSnapshot(snapshot with { Entries = expected }).Length;
                var overhead = WorldFederationCodec.EncodeSnapshot(snapshot with { Entries = default }).Length;
                var wireBytes = 0;
                for (var i = 0; i < Sessions; i++) {
                    var bytes = await wires[i].Frames.Reader.ReadAsync(ct);
                    wireBytes += bytes.Length;
                    using var frameStream = new MemoryStream(bytes);
                    var record = await WorldFederationCodec.ReadResponseAsync(frameStream, ct);
                    Assert.True(record.Ok);
                    var delta = record.Kind == (byte)WorldFederationResponse.SnapshotDelta;
                    Assert.Equal(tick > 1, delta);
                    Assert.True(WorldFederationCodec.TryReadStamped(record.Body.Span, out var stamp, out var body, out _));
                    Assert.True(receivers[i].TryReceive(stamp, delta, body, out var received));
                    Assert.Equal(expected.Select(e => e.Index), received.Entries.ToArray().Select(e => e.Index));
                    Assert.Equal(state[0], received.Entries.Span[1].State.Span[0]);
                    // Half the peers stay on the first baseline for the third tick.
                    if (tick == 1 || (tick == 2 && i % 2 == 0)) { await wires[i].Ack(stamp, ct); }
                }
                output.WriteLine($"tick {tick}, {Sessions} sessions: body encodes {Sessions * expected.Length} -> {hub.EncodedBodies.Bodies}, body bytes {Sessions * (before - overhead)} -> {hub.EncodedBodies.Bytes}, wire bytes {Sessions * (before + WorldFederationCodec.StreamStampBytes + FrameCodec.PrefixBytes)} -> {wireBytes}");
                Assert.Equal(tick == 1 ? 16 : 2, hub.EncodedBodies.Bodies);
            }
        } finally {
            foreach (var wire in wires) { wire.CloseInput(); }
            await Task.WhenAll(pumps);
            foreach (var lease in leases) { lease.Dispose(); }
            foreach (var wire in wires) { wire.Dispose(); }
        }
    }

    private sealed class InterestWire : MemoryStream {
        private readonly Channel<(byte[] Bytes, TaskCompletionSource? Done)> m_input =
            Channel.CreateUnbounded<(byte[], TaskCompletionSource?)>();
        private TaskCompletionSource? m_processed;
        public Channel<byte[]> Frames { get; } = Channel.CreateUnbounded<byte[]>();

        public async Task Ack(WorldStreamStamp stamp, CancellationToken ct) {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await m_input.Writer.WriteAsync((WorldFederationCodec.Stamped(stamp, []), done), ct);
            await done.Task.WaitAsync(ct);
        }
        public void CloseInput() => m_input.Writer.TryWrite(([], null));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            m_processed?.TrySetResult();
            var (bytes, done) = await m_input.Reader.ReadAsync(cancellationToken);
            bytes.CopyTo(buffer);
            m_processed = done;
            return bytes.Length;
        }
        public override Task FlushAsync(CancellationToken cancellationToken) {
            var bytes = ToArray();
            SetLength(0);
            Frames.Writer.TryWrite(bytes);
            return Task.CompletedTask;
        }
    }
}

/// <summary>A projection stream that keeps every record written to it and never acknowledges one, so a sink's
/// records stay whole snapshots rather than deltas against an acknowledged baseline.</summary>
internal sealed class ProjectionWire : MemoryStream {
    private int m_flushes;

    /// <summary>Gets the records written so far: a sink flushes once behind each record.</summary>
    public int Flushes => Volatile.Read(location: ref m_flushes);

    public override Task FlushAsync(CancellationToken cancellationToken) {
        _ = Interlocked.Increment(location: ref m_flushes);
        return base.FlushAsync(cancellationToken: cancellationToken);
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
        await Task.Delay(cancellationToken: cancellationToken, delay: Timeout.InfiniteTimeSpan);

        return 0;
    }
}

/// <summary>A narration sink test double that runs an action on every line it is handed.</summary>
internal sealed class DisposingNarrationSink(Action dispose) : IWorldNarrationSink {
    public void Narrate(in WorldNarration narration) => dispose();
}
