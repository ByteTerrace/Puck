using Puck.Commands;
using Xunit;

using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldPopulation.ApplyMappedArrival"/>/<see cref="WorldBody.SetArrivalVelocity"/> —
/// the WRITE half of a mapped portal arrival, applied AFTER the destination's own ordinary
/// <see cref="WorldPopulation.ActivateSeat"/> join already embodied the traveler fresh under its own kit. The
/// isometry itself (which pose, which velocity) is <c>WorldFrameIsometryLawTests</c>'s own contract (`Puck.World.Schema.Tests`); this
/// suite proves the OTHER half: that the write lands exactly where told, that it runs AFTER the hard-teleport commit
/// (so <see cref="WorldBody.FixedPreviousPosition"/> collapses to the new landing spot, never a ghost from the
/// destination's own spawn point), and that it never touches state a mapped arrival deliberately leaves alone
/// (kit/appearance/action-track). <c>Puck.World.WorldInstanceHost</c> (the composition root, home of the scan/
/// coalesce/transfer orchestration that calls this) is out of reach for this project — verified by RUNNING
/// <c>Puck.World</c> instead (AGENTS.md rule 3).
/// </summary>
public sealed class MappedArrivalApplicationLawTests {
    [Fact]
    public void ApplyMappedArrival_ActiveSeat_OverridesPoseAndVelocity_AfterTeleportCommit() {
        using var fixture = Fixtures.FreshServer();
        var actor = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        var population = fixture.Server.Population;
        var body = fixture.Server.Body(index: actor.Index)!;
        var spawnPosition = body.FixedPosition;

        var mappedPosition = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 11),
            Y: FixedQ4816.FromInteger(value: 3),
            Z: FixedQ4816.FromInteger(value: -6)
        );
        var mappedYaw = FixedQ4816.FromDouble(value: ((200.0 * Math.PI) / 180.0));
        var mappedPlanarVelocity = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 2),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.FromInteger(value: -1)
        );
        var mappedVerticalVelocity = FixedQ4816.FromDouble(value: -0.5);

        // Non-triviality: the mapped pose must genuinely differ from the ordinary spawn ActivateSeat already placed
        // this body at, or the assertions below would pass just as well against a no-op.
        Assert.NotEqual(
            actual: spawnPosition,
            expected: mappedPosition
        );

        var applied = population.ApplyMappedArrival(
            slot: actor.Index,
            motionProgramName: "grounded",
            position: mappedPosition,
            yawRadians: mappedYaw,
            planarVelocity: mappedPlanarVelocity,
            verticalVelocity: mappedVerticalVelocity,
            destinationCompletedEngineTick: fixture.Server.CompletedEngineTicks,
            actionContinuity: new WorldTransferActionContinuity(
                Registers: []
            )
        );

        Assert.True(condition: applied);
        Assert.Equal(
            expected: mappedPosition,
            actual: body.FixedPosition
        );
        Assert.Equal(
            expected: mappedYaw,
            actual: body.FixedYaw
        );

        // AFTER the hard-teleport commit (WorldBody.Pose's own CommitTeleport): the swept-crossing segment start
        // collapses to the LANDING spot, never a ghost from the destination's own ordinary spawn point — the SAME
        // invariant PortalSweepOriginLawTests proves for an ordinary Pose() call, now proven for a mapped arrival's
        // own override too.
        Assert.Equal(
            expected: mappedPosition,
            actual: body.FixedPreviousPosition
        );

        var state = body.CaptureTransferState();

        Assert.Equal(
            expected: mappedPlanarVelocity,
            actual: state.PlanarVelocity
        );
        Assert.Equal(
            expected: mappedVerticalVelocity,
            actual: state.VerticalVelocity
        );
        Assert.False(
            condition: state.PreviousChannelBit[0],
            userMessage: "arrival must start without an input edge latch"
        );
        Assert.Equal(
            expected: FixedQ4816.Zero,
            actual: state.HeldChannelImage[0]
        );
    }
    [Fact]
    public void ApplyMappedArrival_CancelsHeldAndQueuedInputWithoutScoringARelease() {
        var definition = Fixtures.BuildDocument();

        definition = definition with {
            ChannelsRaw = [.. definition.Channels, new WorldChannel(
                Name: "jump",
                Shape: ChannelShape.Binary,
                Composition: true
            )],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        var actor = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            actor,
            actor.Index,
            null,
            WorldProtocol.WireProtocolKey
        )).Accepted);
        var body = fixture.Server.Body(index: actor.Index)!;
        var channels = fixture.Server.Population.Channels;

        Assert.True(condition: channels.TryGetOrdinal(
            name: "jump",
            ordinal: out var ordinal
        ));
        fixture.Step();
        var runtime = fixture.Server.Input;
        var held = default(PlayerIntent).WithChannel(ordinal: ordinal, value: FixedQ4816.One);

        Assert.True(condition: runtime.TryPublishSnapshot(actor, actor.Index, "arrival", held, runtime.Interval.Tick,
            out var session, out var press, out var reason), userMessage: reason);
        Assert.True(condition: fixture.Server.ApplyIntentSubmission(body: body,
            submission: new(runtime.Interval.Tick, actor.Index, held, actor, Input: press, InputOwner: session)).IsAccepted);
        Assert.True(condition: runtime.TryGet(input: out var owner, principal: session));
        Assert.True(condition: fixture.Server.Population.InputCatalog.TryGetLogicalId(id: out var logical, name: "jump"));
        var epoch = owner!.Epoch;

        Assert.True(condition: owner.TryHeld(channel: logical, hold: out _));
        body.PressChannel(ordinal, FixedQ4816.One, 2f, FixedQ4816.FromInteger(value: 2));
        body.EnqueueRun(intent: held, seconds: 2f);

        Assert.True(condition: fixture.Server.Population.ApplyMappedArrival(
            slot: actor.Index,
            motionProgramName: "grounded",
            position: body.FixedPosition,
            yawRadians: body.FixedYaw,
            planarVelocity: FixedVector3.Zero,
            verticalVelocity: FixedQ4816.Zero,
            destinationCompletedEngineTick: fixture.Server.CompletedEngineTicks,
            actionContinuity: new WorldTransferActionContinuity(
                Registers: []
            )
        ));

        Assert.Equal((epoch + 1), owner.Epoch);
        Assert.False(condition: owner.TryHeld(channel: logical, hold: out _));
        Assert.True(condition: owner.TryEvent(channel: logical, kind: WorldInputEventKind.Cancel, occurrence: 0, record: out _));
        Assert.False(condition: owner.TryEvent(channel: logical, kind: WorldInputEventKind.Release, occurrence: 0, record: out _));
        fixture.Step();
        Assert.Equal(
            expected: FixedQ4816.Zero,
            actual: body.ChannelReadHeld[ordinal]
        );

        var neutral = Fixtures.Input(
            server: fixture.Server,
            tick: fixture.Server.NextInputTick,
            body: actor.Index,
            image: default,
            actor: actor
        );

        fixture.Server.EnqueueIntent(submission: in neutral);
        fixture.Step();

        Assert.Equal(
            expected: FixedQ4816.Zero,
            actual: body.ChannelReadHeld[ordinal]
        );
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
    [Fact]
    public void ContinuumArrival_CannotAdvanceTwice_AndAbortCaptureRetainsItsCursor() {
        using var fixture = Fixtures.FreshServer();
        var actor = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            actor,
            actor.Index,
            null,
            WorldProtocol.WireProtocolKey
        )).Accepted);
        var contactActor = Principal.Seat(slot: 1);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            contactActor,
            contactActor.Index,
            null,
            WorldProtocol.WireProtocolKey
        )).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        var mappedPosition = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 4),
            Y: FixedQ4816.FromInteger(value: 7),
            Z: FixedQ4816.Zero
        );
        var trajectory = new WorldContinuumTrajectory(
            PreviousPosition: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 3),
                Y: FixedQ4816.FromInteger(value: 7),
                Z: FixedQ4816.Zero
            ),
            SourceTick: 41,
            ContinuumStartEngineTick: 0,
            ContinuumEndEngineTick: 210,
            ConsumedThroughEngineTick: 210,
            BoundaryEvents: 2
        );

        Assert.True(condition: fixture.Server.Population.ApplyMappedArrival(
            slot: actor.Index,
            motionProgramName: "grounded",
            position: mappedPosition,
            yawRadians: FixedQ4816.Zero,
            planarVelocity: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 9),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            verticalVelocity: FixedQ4816.FromInteger(value: -5),
            destinationCompletedEngineTick: fixture.Server.CompletedEngineTicks,
            continuum: trajectory
        ));
        fixture.Server.Body(index: contactActor.Index)!.Pose(
            position: mappedPosition,
            yawRadians: FixedQ4816.Zero,
            pitchRadians: FixedQ4816.Zero,
            rollRadians: FixedQ4816.Zero
        );

        var settledPosition = body.FixedPosition;
        var settledState = body.CaptureTransferState();

        Assert.Equal(
            expected: trajectory,
            actual: body.PendingContinuum
        );
        Assert.Equal(
            expected: trajectory,
            actual: settledState.PendingContinuum
        );

        // A destination-rate step may arrive before the host's topology continuation drain. It advances the world
        // clock, but this body must not evaluate input, gravity, actions, or position a second time.
        fixture.Step();
        Assert.Equal(
            expected: settledPosition,
            actual: body.FixedPosition
        );
        Assert.Equal(
            expected: settledState.PlanarVelocity,
            actual: body.CaptureTransferState().PlanarVelocity
        );
        Assert.Equal(
            expected: settledState.VerticalVelocity,
            actual: body.CaptureTransferState().VerticalVelocity
        );

        body.ClearPendingContinuum();
        fixture.Step();
        Assert.NotEqual(
            expected: settledPosition,
            actual: body.FixedPosition
        );
    }
    [Fact]
    public void ContinuumHopClamp_PreservesTheConsumedThroughTimeFence() {
        using var fixture = Fixtures.FreshServer();
        var actor = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            actor,
            actor.Index,
            null,
            WorldProtocol.WireProtocolKey
        )).Accepted);
        var body = fixture.Server.Body(index: actor.Index)!;
        var consumedThrough = FixedTickConversion.PerRate(ratePerSecond: 60);
        var position = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromInteger(value: 20),
            Z: FixedQ4816.Zero
        );
        var trajectory = new WorldContinuumTrajectory(
            BoundaryEvents: WorldContinuumTrajectory.MaxBoundaryEvents,
            ConsumedThroughEngineTick: consumedThrough,
            ContinuumEndEngineTick: consumedThrough,
            ContinuumStartEngineTick: 0,
            PreviousPosition: position,
            SourceTick: 1
        );

        Assert.True(condition: fixture.Server.Population.ApplyMappedArrival(
            slot: actor.Index,
            motionProgramName: "grounded",
            position: position,
            yawRadians: FixedQ4816.Zero,
            planarVelocity: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 12),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            verticalVelocity: FixedQ4816.Zero,
            destinationCompletedEngineTick: fixture.Server.CompletedEngineTicks,
            continuum: trajectory
        ));
        var frame = new WorldFaceFrame(
            Origin: position,
            Right: new FixedVector3(
                X: FixedQ4816.One,
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            Up: new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.One,
                Z: FixedQ4816.Zero
            ),
            Normal: new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.One
            ),
            HalfWidth: FixedQ4816.One,
            HalfHeight: FixedQ4816.One,
            HalfDepth: FixedQ4816.Zero
        );

        body.ClampContinuum(
            frame: in frame,
            seamU: FixedQ4816.Zero,
            seamV: FixedQ4816.Zero
        );

        Assert.Null(value: body.PendingContinuum);
        Assert.False(condition: body.TryBeginOrdinaryAdvance(stepStartEngineTick: (consumedThrough - 1UL)));
        Assert.True(condition: body.TryBeginOrdinaryAdvance(stepStartEngineTick: consumedThrough));
    }
    [InlineData(120U, 2)]
    [InlineData(60U, 1)]
    [Theory]
    public void ContinuumTimeFence_RejectsEveryDestinationStepThatOverlapsTheSourceStep(uint destinationRateHz, int overlappingSteps) {
        using var fixture = Fixtures.FreshServer();
        var actor = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            actor,
            actor.Index,
            null,
            WorldProtocol.WireProtocolKey
        )).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;
        var sourceWidth = FixedTickConversion.PerRate(ratePerSecond: 60);
        var destinationWidth = FixedTickConversion.PerRate(ratePerSecond: destinationRateHz);
        var position = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromInteger(value: 20),
            Z: FixedQ4816.Zero
        );
        var trajectory = new WorldContinuumTrajectory(
            BoundaryEvents: 1,
            ConsumedThroughEngineTick: sourceWidth,
            ContinuumEndEngineTick: sourceWidth,
            ContinuumStartEngineTick: 0,
            PreviousPosition: position,
            SourceTick: 1
        );

        Assert.True(condition: fixture.Server.Population.ApplyMappedArrival(
            slot: actor.Index,
            motionProgramName: "grounded",
            position: position,
            yawRadians: FixedQ4816.Zero,
            planarVelocity: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 12),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            verticalVelocity: FixedQ4816.Zero,
            destinationCompletedEngineTick: fixture.Server.CompletedEngineTicks,
            continuum: trajectory
        ));
        body.ClearPendingContinuum();

        for (var step = 0; (step < overlappingSteps); step++) {
            fixture.Step(stepTicks: destinationWidth);
            Assert.Equal(
                expected: position,
                actual: body.FixedPosition
            );
        }

        fixture.Step(stepTicks: destinationWidth);
        Assert.NotEqual(
            expected: position,
            actual: body.FixedPosition
        );
    }
    [Fact]
    public void ContinuumTimeFence_UsesDestinationAdmissionTimeWhenTransportArrivesLate() {
        using var fixture = Fixtures.FreshServer();
        var actor = Principal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            actor,
            actor.Index,
            null,
            WorldProtocol.WireProtocolKey
        )).Accepted);
        var width = FixedTickConversion.PerRate(ratePerSecond: 60);

        fixture.Step(stepTicks: width);

        var body = fixture.Server.Body(index: actor.Index)!;
        var position = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromInteger(value: 20),
            Z: FixedQ4816.Zero
        );
        var trajectory = new WorldContinuumTrajectory(
            BoundaryEvents: 1,
            ConsumedThroughEngineTick: (width / 2UL),
            ContinuumEndEngineTick: (width / 2UL),
            ContinuumStartEngineTick: 0,
            PreviousPosition: position,
            SourceTick: 1
        );

        Assert.True(condition: fixture.Server.Population.ApplyMappedArrival(
            slot: actor.Index,
            motionProgramName: "grounded",
            position: position,
            yawRadians: FixedQ4816.Zero,
            planarVelocity: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 12),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            verticalVelocity: FixedQ4816.Zero,
            continuum: trajectory,
            destinationCompletedEngineTick: fixture.Server.CompletedEngineTicks
        ));
        body.ClearPendingContinuum();

        fixture.Step(stepTicks: width);
        Assert.NotEqual(
            expected: position,
            actual: body.FixedPosition
        );
    }
}
