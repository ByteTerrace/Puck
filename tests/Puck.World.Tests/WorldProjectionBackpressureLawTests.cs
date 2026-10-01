using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldProjectionBackpressureLawTests {
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

        for (var tick = 1UL; (tick <= ((ulong)(WorldFederationProjectionSink.PendingDeliveryLimit + 1))); tick++) {
            var snapshot = new WorldSnapshot(
                Authority: "authority/one",
                Entries: ReadOnlyMemory<EntitySnapshot>.Empty,
                Revision: ((int)tick),
                StepTicks: 1UL,
                Tick: tick
            );

            sink.DeliverSnapshot(snapshot: in snapshot);
            Assert.True(condition: (sink.PendingDeliveries <= WorldFederationProjectionSink.PendingDeliveryLimit));
        }

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
