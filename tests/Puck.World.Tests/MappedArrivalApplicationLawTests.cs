using Xunit;

using Puck.Hosting;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldPopulation.ApplyMappedArrival"/>/<see cref="WorldBody.SetArrivalVelocity"/> —
/// the WRITE half of a mapped portal arrival, applied AFTER the destination's own ordinary
/// <see cref="WorldPopulation.ActivateSeat"/> join already embodied the traveler fresh under its own kit. The
/// isometry itself (which pose, which velocity) is <see cref="WorldFrameIsometryLawTests"/>'s own contract; this
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
            destinationCompletedEngineTick: fixture.Server.CompletedEngineTicks,
            actionContinuity: new WorldTransferActionContinuity(
                Channels: [new WorldTransferChannelEdge(Name: "forward", PreviousBit: true, HeldValue: FixedQ4816.One)],
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
        Assert.Equal(expected: FixedQ4816.One, actual: state.HeldChannelImage[0]);
    }

    [Fact]
    public void ApplyMappedArrival_HeldCompositionBridgesUntilFirstDestinationInput() {
        var definition = Fixtures.BuildDocument();
        definition = definition with {
            Channels = [.. definition.Channels, new WorldChannel(Name: "jump", Shape: ChannelShape.Binary, Composition: true)],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(fixture.Server.ApplySession(new SessionRequest.Join(actor, actor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        var body = fixture.Server.Body(index: actor.Index)!;
        var channels = fixture.Server.Population.Channels;
        Assert.True(channels.TryGetOrdinal(name: "jump", ordinal: out var ordinal));

        Assert.True(fixture.Server.Population.ApplyMappedArrival(
            slot: actor.Index,
            motionProgramName: "grounded",
            position: body.FixedPosition,
            yawRadians: body.FixedYaw,
            planarVelocity: FixedVector3.Zero,
            verticalVelocity: FixedQ4816.Zero,
            destinationCompletedEngineTick: fixture.Server.CompletedEngineTicks,
            actionContinuity: new WorldTransferActionContinuity(
                Channels: [new WorldTransferChannelEdge(Name: "jump", PreviousBit: true, HeldValue: FixedQ4816.One)],
                Registers: [])));

        fixture.Step();
        Assert.Equal(expected: FixedQ4816.One, actual: body.ChannelReadHeld[ordinal]);

        var neutral = new IntentSubmission(
            Tick: fixture.Server.NextInputTick,
            EntityIndex: actor.Index,
            Intent: default,
            Principal: actor,
            HeldChannels: default);
        fixture.Server.EnqueueIntent(submission: in neutral);
        fixture.Step();

        Assert.Equal(expected: FixedQ4816.Zero, actual: body.ChannelReadHeld[ordinal]);
    }

    [Fact]
    public void ContinuumArrival_CannotAdvanceTwice_AndAbortCaptureRetainsItsCursor() {
        using var fixture = Fixtures.FreshServer();
        var actor = WorldPrincipal.Seat(slot: 0);
        Assert.True(fixture.Server.ApplySession(new SessionRequest.Join(actor, actor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        var contactActor = WorldPrincipal.Seat(slot: 1);
        Assert.True(fixture.Server.ApplySession(new SessionRequest.Join(contactActor, contactActor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        var mappedPosition = new FixedVector3(FixedQ4816.FromInteger(value: 4), FixedQ4816.FromInteger(value: 7), FixedQ4816.Zero);
        var trajectory = new WorldContinuumTrajectory(
            PreviousPosition: new FixedVector3(FixedQ4816.FromInteger(value: 3), FixedQ4816.FromInteger(value: 7), FixedQ4816.Zero),
            SourceTick: 41,
            ContinuumStartEngineTick: 0,
            ContinuumEndEngineTick: 210,
            ConsumedThroughEngineTick: 210,
            BoundaryEvents: 2);

        Assert.True(fixture.Server.Population.ApplyMappedArrival(
            slot: actor.Index,
            motionProgramName: "grounded",
            position: mappedPosition,
            yawRadians: FixedQ4816.Zero,
            planarVelocity: new FixedVector3(FixedQ4816.FromInteger(value: 9), FixedQ4816.Zero, FixedQ4816.Zero),
            verticalVelocity: FixedQ4816.FromInteger(value: -5),
            destinationCompletedEngineTick: fixture.Server.CompletedEngineTicks,
            continuum: trajectory));
        fixture.Server.Body(index: contactActor.Index)!.Pose(
            position: mappedPosition,
            yawRadians: FixedQ4816.Zero,
            pitchRadians: FixedQ4816.Zero,
            rollRadians: FixedQ4816.Zero);

        var settledPosition = body.FixedPosition;
        var settledState = body.CaptureTransferState();
        Assert.Equal(expected: trajectory, actual: body.PendingContinuum);
        Assert.Equal(expected: trajectory, actual: settledState.PendingContinuum);

        // A destination-rate step may arrive before the host's topology continuation drain. It advances the world
        // clock, but this body must not evaluate input, gravity, actions, or position a second time.
        fixture.Step();
        Assert.Equal(expected: settledPosition, actual: body.FixedPosition);
        Assert.Equal(expected: settledState.PlanarVelocity, actual: body.CaptureTransferState().PlanarVelocity);
        Assert.Equal(expected: settledState.VerticalVelocity, actual: body.CaptureTransferState().VerticalVelocity);

        body.ClearPendingContinuum();
        fixture.Step();
        Assert.NotEqual(expected: settledPosition, actual: body.FixedPosition);
    }

    [Theory]
    [InlineData(120U, 2)]
    [InlineData(60U, 1)]
    public void ContinuumTimeFence_RejectsEveryDestinationStepThatOverlapsTheSourceStep(uint destinationRateHz, int overlappingSteps) {
        using var fixture = Fixtures.FreshServer();
        var actor = WorldPrincipal.Seat(slot: 0);
        Assert.True(fixture.Server.ApplySession(new SessionRequest.Join(actor, actor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        var sourceWidth = EngineTicks.PerRate(ratePerSecond: 60);
        var destinationWidth = EngineTicks.PerRate(ratePerSecond: destinationRateHz);
        var position = new FixedVector3(FixedQ4816.Zero, FixedQ4816.FromInteger(value: 20), FixedQ4816.Zero);
        var trajectory = new WorldContinuumTrajectory(
            PreviousPosition: position,
            SourceTick: 1,
            ContinuumStartEngineTick: 0,
            ContinuumEndEngineTick: sourceWidth,
            ConsumedThroughEngineTick: sourceWidth,
            BoundaryEvents: 1);

        Assert.True(fixture.Server.Population.ApplyMappedArrival(
            slot: actor.Index,
            motionProgramName: "grounded",
            position: position,
            yawRadians: FixedQ4816.Zero,
            planarVelocity: new FixedVector3(FixedQ4816.FromInteger(value: 12), FixedQ4816.Zero, FixedQ4816.Zero),
            verticalVelocity: FixedQ4816.Zero,
            destinationCompletedEngineTick: fixture.Server.CompletedEngineTicks,
            continuum: trajectory));
        body.ClearPendingContinuum();

        for (var step = 0; step < overlappingSteps; step++) {
            fixture.Step(stepTicks: destinationWidth);
            Assert.Equal(expected: position, actual: body.FixedPosition);
        }

        fixture.Step(stepTicks: destinationWidth);
        Assert.NotEqual(expected: position, actual: body.FixedPosition);
    }

    [Fact]
    public void ContinuumTimeFence_UsesDestinationAdmissionTimeWhenTransportArrivesLate() {
        using var fixture = Fixtures.FreshServer();
        var actor = WorldPrincipal.Seat(slot: 0);
        Assert.True(fixture.Server.ApplySession(new SessionRequest.Join(actor, actor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        var width = EngineTicks.PerRate(ratePerSecond: 60);
        fixture.Step(stepTicks: width);

        var body = fixture.Server.Body(index: actor.Index)!;
        var position = new FixedVector3(FixedQ4816.Zero, FixedQ4816.FromInteger(value: 20), FixedQ4816.Zero);
        var trajectory = new WorldContinuumTrajectory(
            PreviousPosition: position,
            SourceTick: 1,
            ContinuumStartEngineTick: 0,
            ContinuumEndEngineTick: (width / 2UL),
            ConsumedThroughEngineTick: (width / 2UL),
            BoundaryEvents: 1);

        Assert.True(fixture.Server.Population.ApplyMappedArrival(
            slot: actor.Index,
            motionProgramName: "grounded",
            position: position,
            yawRadians: FixedQ4816.Zero,
            planarVelocity: new FixedVector3(FixedQ4816.FromInteger(value: 12), FixedQ4816.Zero, FixedQ4816.Zero),
            verticalVelocity: FixedQ4816.Zero,
            continuum: trajectory,
            destinationCompletedEngineTick: fixture.Server.CompletedEngineTicks));
        body.ClearPendingContinuum();

        fixture.Step(stepTicks: width);
        Assert.NotEqual(expected: position, actual: body.FixedPosition);
    }

    [Fact]
    public void ContinuumHopClamp_PreservesTheConsumedThroughTimeFence() {
        using var fixture = Fixtures.FreshServer();
        var actor = WorldPrincipal.Seat(slot: 0);
        Assert.True(fixture.Server.ApplySession(new SessionRequest.Join(actor, actor.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        var body = fixture.Server.Body(index: actor.Index)!;
        var consumedThrough = EngineTicks.PerRate(ratePerSecond: 60);
        var position = new FixedVector3(FixedQ4816.Zero, FixedQ4816.FromInteger(value: 20), FixedQ4816.Zero);
        var trajectory = new WorldContinuumTrajectory(
            PreviousPosition: position,
            SourceTick: 1,
            ContinuumStartEngineTick: 0,
            ContinuumEndEngineTick: consumedThrough,
            ConsumedThroughEngineTick: consumedThrough,
            BoundaryEvents: WorldContinuumTrajectory.MaxBoundaryEvents);
        Assert.True(fixture.Server.Population.ApplyMappedArrival(
            slot: actor.Index,
            motionProgramName: "grounded",
            position: position,
            yawRadians: FixedQ4816.Zero,
            planarVelocity: new FixedVector3(FixedQ4816.FromInteger(value: 12), FixedQ4816.Zero, FixedQ4816.Zero),
            verticalVelocity: FixedQ4816.Zero,
            destinationCompletedEngineTick: fixture.Server.CompletedEngineTicks,
            continuum: trajectory));
        var frame = new WorldFaceFrame(
            Origin: position,
            Right: new FixedVector3(FixedQ4816.One, FixedQ4816.Zero, FixedQ4816.Zero),
            Up: new FixedVector3(FixedQ4816.Zero, FixedQ4816.One, FixedQ4816.Zero),
            Normal: new FixedVector3(FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.One),
            HalfWidth: FixedQ4816.One,
            HalfHeight: FixedQ4816.One,
            HalfDepth: FixedQ4816.Zero);

        body.ClampContinuum(frame: in frame, seamU: FixedQ4816.Zero, seamV: FixedQ4816.Zero);

        Assert.Null(body.PendingContinuum);
        Assert.False(body.TryBeginOrdinaryAdvance(stepStartEngineTick: consumedThrough - 1UL));
        Assert.True(body.TryBeginOrdinaryAdvance(stepStartEngineTick: consumedThrough));
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
            verticalVelocity: FixedQ4816.Zero,
            destinationCompletedEngineTick: fixture.Server.CompletedEngineTicks
        );

        Assert.False(condition: applied);
    }
}
