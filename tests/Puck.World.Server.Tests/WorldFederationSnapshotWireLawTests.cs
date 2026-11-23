using System.Numerics;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>
/// Laws for what one snapshot entity and one snapshot record carry across the federation wire: a continuity hint
/// round-trips whatever kind holds a carried motion, a carry kind with no motion is refused, and a record the receiver
/// would refuse for its size is refused by the encoder or the sink instead.
/// </summary>
public sealed class WorldFederationSnapshotWireLawTests(ITestOutputHelper output) {
    private static readonly WorldPoseCarry Motion = new(
        Rotation: Quaternion.Normalize(value: new Quaternion(x: 0.1f, y: 0.2f, z: 0.3f, w: 0.9f)),
        Scale: 2f,
        Translation: new Vector3(x: 4f, y: -5f, z: 6f)
    );

    private static EntitySnapshot Entity(int index, EntityContinuity continuity, ReadOnlyMemory<long> state = default) => new(
        Active: true,
        BodyColor: Vector3.One,
        CatalogRig: 0,
        Continuity: continuity,
        Generation: 7,
        Heading: 0.25f,
        Index: index,
        Kit: 1,
        Look: 2,
        Orientation: Quaternion.Identity,
        PlacementId: "placed/one",
        Position: new Vector3(x: index, y: 1f, z: 2f),
        State: state
    );
    private static WorldSnapshot Snapshot(EntitySnapshot[] entries) => new(
        Authority: "wire",
        Entries: entries,
        Revision: 1,
        StepTicks: 1UL,
        Tick: 1UL
    );

    /// <summary>Every continuity kind that may hold a carried motion decodes to the entity that was encoded, and the
    /// fields after the continuity head (generation, placement id, state) decode to theirs, so no kind shifts the
    /// record.</summary>
    [Theory]
    [InlineData(EntityContinuityKind.Continuous, false)]
    [InlineData(EntityContinuityKind.Continuous, true)]
    [InlineData(EntityContinuityKind.Teleport, false)]
    [InlineData(EntityContinuityKind.Teleport, true)]
    [InlineData(EntityContinuityKind.Correction, false)]
    [InlineData(EntityContinuityKind.Correction, true)]
    [InlineData(EntityContinuityKind.Carry, true)]
    public void AContinuityRoundTripsWhetherOrNotItHoldsACarry(EntityContinuityKind kind, bool carried) {
        var continuity = new EntityContinuity(
            Carried: (carried ? Motion : null),
            Kind: kind,
            Seconds: ((kind == EntityContinuityKind.Correction) ? 0.5f : 0f)
        );
        long[] state = [3L, -4L];
        var snapshot = Snapshot(entries: [Entity(index: 0, continuity: continuity), Entity(index: 1, continuity: continuity, state: state)]);

        Assert.True(
            condition: WorldFederationCodec.TryDecodeSnapshot(body: WorldFederationCodec.EncodeSnapshot(snapshot: in snapshot), failure: out var failure, snapshot: out var decoded),
            userMessage: failure.ToString()
        );
        Assert.Equal(expected: 2, actual: decoded.Entries.Length);

        for (var index = 0; (index < 2); index++) {
            var expected = snapshot.Entries.Span[index];
            var actual = decoded.Entries.Span[index];

            Assert.Equal(expected: expected.Continuity, actual: actual.Continuity);
            Assert.Equal(expected: (expected with { State = default }), actual: (actual with { State = default }));
            Assert.Equal(expected: expected.State.ToArray(), actual: actual.State.ToArray());
        }
    }
    /// <summary>A carry kind that arrives without its motion is refused as malformed rather than decoded into an entity
    /// a consumer cannot apply.</summary>
    [Fact]
    public void ACarryKindWithNoCarriedMotionIsRefused() {
        var snapshot = Snapshot(entries: [Entity(index: 0, continuity: new EntityContinuity(Kind: EntityContinuityKind.Carry))]);

        Assert.False(condition: WorldFederationCodec.TryDecodeSnapshot(body: WorldFederationCodec.EncodeSnapshot(snapshot: in snapshot), failure: out var failure, snapshot: out _));
        Assert.Equal(expected: Puck.Networking.WireRefusal.PayloadMalformed, actual: failure.Refusal);
    }
    /// <summary>A dense 4,096-body table, every body carrying the most register slots a definition may
    /// declare, encodes larger than the 4 MiB the receiver's cap once was, and is within the cap now.</summary>
    [Fact]
    public void ADenseTableOfFullRegisterFilesIsWithinTheReceiversCap() {
        var state = new long[StateCapacity.MaxBodySlots];
        var entries = new EntitySnapshot[4096];

        for (var index = 0; (index < entries.Length); index++) {
            entries[index] = Entity(index: index, continuity: EntityContinuity.Continuous, state: state);
        }

        var snapshot = Snapshot(entries: entries);
        var encoded = WorldFederationCodec.EncodeSnapshot(snapshot: in snapshot);
        var stamped = (WorldFederationCodec.StreamStampBytes + encoded.Length);

        output.WriteLine(message: $"ceiling table with {state.Length} register slots per body: {encoded.Length:N0} bytes");
        Assert.True(condition: (encoded.Length > ((4 * 1024) * 1024)));
        Assert.True(condition: WorldFederationCodec.WithinResponseCap(kind: WorldFederationResponse.Snapshot, length: stamped));
        Assert.True(
            condition: WorldFederationCodec.TryDecodeSnapshot(body: encoded, failure: out var failure, snapshot: out var decoded),
            userMessage: failure.ToString()
        );
        Assert.Equal(expected: entries.Length, actual: decoded.Entries.Length);
    }
    /// <summary>An encoder asked for a snapshot the receiver's cap would refuse refuses it first, as an unencodable
    /// snapshot (<see cref="ArgumentException"/>) every caller already handles.</summary>
    [Fact]
    public void AnOverCapSnapshotIsRefusedByTheEncoder() {
        var snapshot = Snapshot(entries: OverCap());

        Assert.Throws<ArgumentException>(testCode: () => WorldFederationCodec.EncodeSnapshot(snapshot: in snapshot));
    }
    /// <summary>A projection sink whose composed snapshot record is over the receiver's cap detaches by name and queues
    /// nothing, so the peer is never sent a record it must drop the stream to refuse.</summary>
    [Fact]
    public void AnOverCapSnapshotDetachesTheSinkByName() {
        var sink = new WorldFederationProjectionSink(
            authority: "wire",
            disclosure: static () => new WorldSinkDisclosure(ObserverBodyIndex: -1, Policy: new WorldObserverDisclosure(UpdateSeconds: 0f)),
            tier: WorldDisclosureTier.Replica
        );
        var hub = new WorldOutputHub();

        using var lease = hub.Subscribe(sink: sink);

        var snapshot = Snapshot(entries: OverCap());

        hub.DeliverSnapshot(snapshot: in snapshot);
        Assert.False(condition: hub.HasTypedSubscribers);
        Assert.Equal(expected: WorldFederationProjectionSink.OversizeDetachReason, actual: sink.DetachReason);
        Assert.Equal(expected: 0, actual: sink.PendingDeliveries);
    }

    // Enough full-register bodies that one record cannot fit the frame ceiling.
    private static EntitySnapshot[] OverCap() {
        var state = new long[StateCapacity.MaxBodySlots];
        var entries = new EntitySnapshot[31_000];

        for (var index = 0; (index < entries.Length); index++) {
            entries[index] = Entity(index: index, continuity: EntityContinuity.Continuous, state: state);
        }

        return entries;
    }
}
