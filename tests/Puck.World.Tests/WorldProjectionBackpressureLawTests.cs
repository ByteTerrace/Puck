using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldProjectionBackpressureLawTests {
    [Fact]
    public void AChangedAuthorityRouteDetachesWithoutWaitingForTheWire() {
        var current = true;
        var sink = new WorldFederationProjectionSink(
            authority: "authority/one",
            disclosure: static () => new WorldSinkDisclosure(ObserverBodyIndex: -1, Policy: new WorldObserverDisclosure(UpdateSeconds: 0f)),
            isCurrent: () => current,
            revision: static () => 1,
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
            revision: static () => 1,
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
            revision: () => 1,
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
            Assert.Equal(expected: ((int)tick), actual: decoded.Revision);
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

/// <summary>A detachable sink test double that counts what it is handed and ends its own subscription after a given
/// number of definitions or snapshots, counting every delivery that still reaches it afterwards.</summary>
internal sealed class EndingSink(int endAfterDefinitions, int endAfterSnapshots) : IWorldDetachableSink {
    public const string Reason = "test.ended";

    public int Definitions { get; private set; }
    public int DeliveriesAfterEnd { get; private set; }
    public string? DetachReason { get; private set; }
    public int Snapshots { get; private set; }

    private void Note() {
        if (DetachReason is not null) {
            DeliveriesAfterEnd++;
        }
    }

    public void DeliverAnswer(in QueryAnswer answer) {
    }
    public void DeliverComposition(WorldComposition composition) {
    }
    public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) {
        Note();

        if (++Definitions == endAfterDefinitions) {
            DetachReason = Reason;
        }
    }
    public void DeliverSessionLever(WorldSessionLever lever) {
    }
    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        Note();

        if (++Snapshots == endAfterSnapshots) {
            DetachReason = Reason;
        }
    }
    public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) => Note();
}
/// <summary>A narration sink test double that runs an action on every line it is handed.</summary>
internal sealed class DisposingNarrationSink(Action dispose) : IWorldNarrationSink {
    public void Narrate(in WorldNarration narration) => dispose();
}
