using Puck.World.Protocol;
using Puck.Commands;
using System.Buffers.Binary;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class WorldProjectionBackpressureLawTests {
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
            var sink = new WorldFederationProjectionSink(tier, fixture.Server.AuthorityIdentity,
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
            sink.DeliverDefinition(fixture.Server.Definition, fixture.Server.DocumentVersion);
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
                if (frame.Kind is (byte)WorldFederationResponse.Definition or (byte)WorldFederationResponse.ProjectionDelta) {
                    var version = new WorldDocumentVersion(new Guid(body.AsSpan(1, 16)), BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(17)));
                    transcript.Nonce(version).Activation.TryWriteBytes(body.AsSpan(1, 16));
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
            authority: "authority/one",
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
            authority: "authority/one",
            disclosure: static () => new WorldSinkDisclosure(ObserverBodyIndex: -1, Policy: new WorldObserverDisclosure(UpdateSeconds: 0f)),
            tier: WorldDisclosureTier.Replica
        );
        var hub = new WorldOutputHub();
        var narration = new RecordingNarrationSink();

        using var narrated = hub.AttachNarrationSink(sink: narration);
        using var lease = hub.Subscribe(sink: sink);

        for (var tick = 1UL; (tick <= ((ulong)(WorldFederationProjectionSink.PendingDeliveryLimit + 1))); tick++) {
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
            authority: "authority/one",
            disclosure: () => disclosure,
            tier: WorldDisclosureTier.Replica
        );
        var hub = new WorldOutputHub();

        using var lease = hub.Subscribe(sink: sink);

        for (var tick = 1UL; (tick <= ((ulong)(WorldFederationProjectionSink.PendingDeliveryLimit + 1))); tick++) {
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

        sink.DeliverSnapshot(snapshot: in laterEpoch);
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
                body: frame.Body.Span,
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

    private sealed class ProjectionWire : MemoryStream {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            await Task.Delay(cancellationToken: cancellationToken, delay: Timeout.InfiniteTimeSpan);

            return 0;
        }
    }
}

/// <summary>A narration sink test double that runs an action on every line it is handed.</summary>
internal sealed class DisposingNarrationSink(Action dispose) : IWorldNarrationSink {
    public void Narrate(in WorldNarration narration) => dispose();
}
