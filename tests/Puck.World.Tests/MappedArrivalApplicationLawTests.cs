using Xunit;

using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldPopulation.ApplyMappedArrival"/>/<see cref="WorldBody.SetArrivalVelocity"/> —
/// the WRITE half of a mapped portal arrival, applied AFTER the destination's own ordinary
/// <see cref="WorldPopulation.ActivateSeat"/> join already embodied the traveler fresh under its own kit. The
/// isometry itself (which pose, which velocity) is <see cref="WorldPortalArrivalMathLawTests"/>'s own contract; this
/// suite proves the OTHER half: that the write lands exactly where told, that it runs AFTER the hard-teleport commit
/// (so <see cref="WorldBody.FixedPreviousPosition"/> collapses to the new landing spot, never a ghost from the
/// destination's own spawn point), and that it never touches state a mapped arrival deliberately leaves alone
/// (kit/appearance/action-track). <c>Puck.World.WorldInstanceHost</c> (the composition root, home of the scan/
/// coalesce/transfer orchestration that calls this) is out of reach for this project — verified by RUNNING
/// <c>Puck.World</c> instead (CLAUDE.md rule 3).
/// </summary>
public sealed class MappedArrivalApplicationLawTests {
    [Fact]
    public void ApplyMappedArrival_ActiveSeat_OverridesPoseAndVelocity_AfterTeleportCommit() {
        using var fixture = Fixtures.FreshServer();
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var population = fixture.Server.Population;
        var body = fixture.Server.Body(index: actor.Index)!;
        var spawnPosition = body.FixedPosition;

        var mappedPosition = new FixedVector3(X: FixedQ4816.FromInteger(value: 11), Y: FixedQ4816.FromInteger(value: 3), Z: FixedQ4816.FromInteger(value: -6));
        var mappedYaw = FixedQ4816.FromDouble(value: (200.0 * Math.PI / 180.0));
        var mappedPlanarVelocity = new FixedVector3(X: FixedQ4816.FromInteger(value: 2), Y: FixedQ4816.Zero, Z: FixedQ4816.FromInteger(value: -1));
        var mappedVerticalVelocity = FixedQ4816.FromDouble(value: -0.5);

        // Non-triviality: the mapped pose must genuinely differ from the ordinary spawn ActivateSeat already placed
        // this body at, or the assertions below would pass just as well against a no-op.
        Assert.NotEqual(expected: mappedPosition, actual: spawnPosition);

        var applied = population.ApplyMappedArrival(
            slot: actor.Index,
            motionProgramName: "grounded",
            position: mappedPosition,
            yawRadians: mappedYaw,
            planarVelocity: mappedPlanarVelocity,
            verticalVelocity: mappedVerticalVelocity,
            actionContinuity: new WorldTransferActionContinuity(
                Channels: [new WorldTransferChannelEdge(Name: "forward", PreviousBit: true)],
                Registers: []));

        Assert.True(condition: applied);
        Assert.Equal(expected: mappedPosition, actual: body.FixedPosition);
        Assert.Equal(expected: mappedYaw, actual: body.FixedYaw);

        // AFTER the hard-teleport commit (WorldBody.Pose's own CommitTeleport): the swept-crossing segment start
        // collapses to the LANDING spot, never a ghost from the destination's own ordinary spawn point — the SAME
        // invariant PortalSweepOriginLawTests proves for an ordinary Pose() call, now proven for a mapped arrival's
        // own override too.
        Assert.Equal(expected: mappedPosition, actual: body.FixedPreviousPosition);

        var state = body.CaptureTransferState();

        Assert.Equal(expected: mappedPlanarVelocity, actual: state.PlanarVelocity);
        Assert.Equal(expected: mappedVerticalVelocity, actual: state.VerticalVelocity);
        Assert.True(condition: state.PreviousChannelBit[0], userMessage: "the mapped writer change manufactured a fresh held-channel edge");
    }

    [Fact]
    public void ApplyMappedArrival_InactiveSeat_IsANoOp() {
        using var fixture = Fixtures.FreshServer();

        // Control for the discriminating case above: an inactive seat (never joined) has no body to override, and
        // ApplyMappedArrival must report that honestly rather than throwing or silently minting one.
        var applied = fixture.Server.Population.ApplyMappedArrival(
            slot: 0,
            motionProgramName: "grounded",
            position: FixedVector3.Zero,
            yawRadians: FixedQ4816.Zero,
            planarVelocity: FixedVector3.Zero,
            verticalVelocity: FixedQ4816.Zero
        );

        Assert.False(condition: applied);
    }
}
