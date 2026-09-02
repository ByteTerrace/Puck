using System.Numerics;
using Puck.World.Authoring;
using Puck.Hosting;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Law coverage for the climb/grapple attachment surface: intent→attach determinism, suspended grounding
/// while gripping, detach momentum preservation, and the validator's grip-composition refusals.</summary>
public sealed class AttachmentLawTests {
    private const int AttachOrdinal = 3;
    private const int DetachOrdinal = 4;
    private const int ForwardOrdinal = 0;
    private const int ReelOrdinal = 5;

    private sealed class RestoredCheckpoint(WorldServer server, WorldMachineHost machines, string stateDirectory) : IDisposable {
        public WorldServer Server { get; } = server;

        public void Dispose() {
            machines.Dispose();

            if (Directory.Exists(path: stateDirectory)) {
                Directory.Delete(
                    path: stateDirectory,
                    recursive: true
                );
            }
        }
    }

    // A body standing at the origin resolves the wall's near face (a clean X-only clamp — see
    // BuildAttachmentDocument's remarks) within this tolerance of world X 1.8: exactly the half-extent margin plus
    // whatever the creation-stamp/collider compile chain's own float-to-fixed rounding carries. Y and Z stay exact
    // zero (the probe's own unclamped coordinates on those axes).
    private static void AssertOnWallFace(FixedVector3? anchor) {
        Assert.NotNull(@object: anchor);
        Assert.True(
            condition: (Math.Abs(value: (((double)anchor!.Value.X) - 1.8)) < 0.02),
            userMessage: $"expected the wall's near face (world X ~1.8), got {anchor.Value}"
        );
        Assert.Equal(expected: FixedQ4816.Zero, actual: anchor.Value.Y);
        Assert.Equal(expected: FixedQ4816.Zero, actual: anchor.Value.Z);
    }
    private static WorldPrototype BuildBoxCreation(string id, Vector3 halfExtents) {
        var shape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: SdfSolidPrimitive.Box,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: halfExtents,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: id,
            Palette: null,
            Shapes: [shape],
            Frames: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: id);

        return new WorldPrototype(Id: id, Document: canonical.Document, HashRaw: canonical.Hash);
    }
    // Extends Fixtures.BuildDocument with a climb/grapple-ready world: a wall placement centered at (2,0,0) with
    // half-extents (0.2,3,3) — its near face sits at world X=1.8, well clear of every Y/Z boundary from a body
    // standing at the origin, so an attach from there always resolves the clean face normal (-1,0,0) rather than an
    // edge/corner — and three composition channels (attach/detach/reel) appended after the base document's
    // forward/strafe/turn roles (ordinals 0..2), landing at 3/4/5.
    private static WorldDefinition BuildAttachmentDocument(bool wallClimbable = true, string? attachChannelName = "attach", bool declareAttachChannel = true, bool includeWall = true) {
        var wall = BuildBoxCreation(
            halfExtents: new Vector3(x: 0.2f, y: 3f, z: 3f),
            id: "wall"
        );
        // The grapple target: never climbable (no Grip facet, so it inherits the world's DefaultGrip: false),
        // directly ahead of a body's default facing (yaw 0 faces -Z) so the directed aim query finds it without
        // needing a turn — the wall alone cannot serve both roles, since it sits to the SIDE at +X, which an
        // undirected climb reaches but a directed aim facing -Z never does.
        var post = BuildBoxCreation(
            halfExtents: new Vector3(x: 1f, y: 1f, z: 1f),
            id: "post"
        );
        var document = Fixtures.BuildDocument();
        var channels = document.Channels.ToList();

        if (declareAttachChannel) {
            channels.Add(item: new WorldChannel(Name: "attach", Shape: ChannelShape.Binary, Composition: true));
        }
        channels.Add(item: new WorldChannel(Name: "detach", Shape: ChannelShape.Binary, Composition: true));
        channels.Add(item: new WorldChannel(Name: "reel", Shape: ChannelShape.Bipolar, Composition: true));

        var rows = new List<WorldPlacement> {
            new(
                Id: "post1",
                PrototypeId: "post",
                Position: new Vector3(x: 0f, y: 0f, z: -5f),
                YawDegrees: 0f,
                Scale: 1f,
                Solid: new WorldSolid(Margin: 0f)
            ),
        };

        if (includeWall) {
            rows.Add(item: new WorldPlacement(
                Id: "wall1",
                PrototypeId: "wall",
                Position: new Vector3(x: 2f, y: 0f, z: 0f),
                YawDegrees: 0f,
                Scale: 1f,
                Solid: new WorldSolid(Margin: 0f),
                Grip: (wallClimbable ? new WorldPlacementGrip(Climbable: true) : null)
            ));
        }

        return document with {
            ChannelsRaw = channels,
            CreationsRaw = [wall, post],
            PlacementsRaw = (document.PlacementsRaw! with {
                Rows = rows,
            }),
            AttachmentRaw = new WorldAttachmentSection(
                AttachChannel: attachChannelName,
                ClimbReach: 5f,
                ClimbSpeed: 2f,
                DefaultGrip: false,
                DetachChannel: "detach",
                Enabled: true,
                GrappleAssistHalfAngleDegrees: 30f,
                GrappleMaxDistance: 20f,
                GripCost: 0f,
                ReelChannel: "reel",
                ReelInFloor: 1f,
                ReelRate: 2f,
                ReleaseMomentumScale: 1f
            ),
        };
    }
    private static WorldBody JoinBody(WorldFixture fixture, int slot = 0) {
        var actor = WorldPrincipal.Seat(slot: slot);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        return fixture.Server.Body(index: actor.Index)!;
    }
    private static PlayerIntent Attach() => default(PlayerIntent).WithChannel(ordinal: AttachOrdinal, value: FixedQ4816.One);
    private static PlayerIntent Detach() => default(PlayerIntent).WithChannel(ordinal: DetachOrdinal, value: FixedQ4816.One);
    private static PlayerIntent Forward() => default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One);
    private static PlayerIntent ReelIn() => default(PlayerIntent).WithChannel(ordinal: ReelOrdinal, value: -FixedQ4816.One);
    private static WorldAuthorityHostRowCheckpoint EmptyHostRow() => new(
        AnnouncedCrossingHolds: [],
        AppliedTransferHighWater: null,
        AppliedTransferIds: [],
        ElapsedEngineTicks: 0,
        ForwardedBodies: [],
        FreshCounter: 0,
        InDoubtTransfers: [],
        IsPaused: false,
        NextTransferId: 1,
        PortalOccupancy: [],
        Retained: false,
        ScheduleAccumulatorTicks: 0,
        SeededArrivals: []
    );
    private static RestoredCheckpoint Restore(WorldFixture fixture, string identity) {
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: EmptyHostRow(),
            reason: out var refusal
        ), userMessage: refusal);
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: encoded,
            checkpoint: out var decoded,
            reason: out var decodeRefusal
        ), userMessage: decodeRefusal);

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: decoded!.Server.DefinitionJson);
        var machines = new WorldMachineHost(engines: [], screens: definition.Screens);
        var stateDirectory = Directory.CreateTempSubdirectory(prefix: "puck-attachment-checkpoint-").FullName;
        var profiles = new WorldOwnedWorlds(
            directory: stateDirectory,
            machineId: Guid.NewGuid(),
            template: definition
        );

        var (server, _) = WorldServer.FromCheckpoint(
            checkpoint: decoded,
            instanceIdentity: identity,
            machines: machines,
            profiles: profiles
        );

        return new RestoredCheckpoint(
            machines: machines,
            server: server,
            stateDirectory: stateDirectory
        );
    }

    [Fact]
    public void ClimbAttach_SnapsToTheGrippedSurfaceAndSuspendsGrounding() {
        using var fixture = Fixtures.FreshServer(definition: BuildAttachmentDocument());
        var body = JoinBody(fixture: fixture);

        Assert.Equal(expected: WorldBodyAttachmentMode.None, actual: body.AttachmentMode);

        body.SubmitIntent(intent: Attach());
        fixture.Step();
        body.SubmitIntent(intent: default);
        fixture.Step();

        Assert.Equal(expected: WorldBodyAttachmentMode.Climb, actual: body.AttachmentMode);
        AssertOnWallFace(anchor: body.AttachmentAnchor);
        Assert.True(condition: body.AttachmentGrantedByOverride, userMessage: "the wall's own grip override, not the world default, should have granted this climb");

        var yBeforeIdleTicks = body.FixedPosition.Y;

        // 60 idle ticks (0.25s) with no move intent: a grounded, non-climbing body under this fixture's gravity
        // (RiseGravity/FallGravity nonzero) would have measurably fallen; a gripping body must not move at all.
        for (var tick = 0; (tick < 60); tick++) {
            body.SubmitIntent(intent: default);
            fixture.Step();
        }

        Assert.Equal(expected: yBeforeIdleTicks, actual: body.FixedPosition.Y);
        Assert.Equal(expected: WorldBodyAttachmentMode.Climb, actual: body.AttachmentMode);
    }
    [Fact]
    public void ClimbSuspendsGrounding_WhereAnUnattachedControlBodyFallsUnderTheSameGravity() {
        using var fixture = Fixtures.FreshServer(definition: BuildAttachmentDocument());
        var climber = JoinBody(fixture: fixture, slot: 0);
        var control = JoinBody(fixture: fixture, slot: 1);

        climber.SubmitIntent(intent: Attach());
        control.SubmitIntent(intent: default);
        fixture.Step();

        Assert.Equal(expected: WorldBodyAttachmentMode.Climb, actual: climber.AttachmentMode);

        var climberStartY = climber.FixedPosition.Y;
        var controlStartY = control.FixedPosition.Y;

        for (var tick = 0; (tick < 60); tick++) {
            climber.SubmitIntent(intent: default);
            control.SubmitIntent(intent: default);
            fixture.Step();
        }

        Assert.Equal(expected: climberStartY, actual: climber.FixedPosition.Y);
        Assert.True(condition: (control.FixedPosition.Y < controlStartY), userMessage: $"the unattached control body should have fallen under gravity; started at {controlStartY}, now at {control.FixedPosition.Y}");
    }
    [Fact]
    public void DetachRestoresLocomotion_CarryingTheClimbVelocityScaledByReleaseMomentum() {
        var scale = 0.5f;
        var document = (BuildAttachmentDocument() with {
            AttachmentRaw = (BuildAttachmentDocument().AttachmentRaw! with { ReleaseMomentumScale = scale }),
        });

        using var fixture = Fixtures.FreshServer(definition: document);
        var body = JoinBody(fixture: fixture);

        body.SubmitIntent(intent: Attach());
        fixture.Step();

        Assert.Equal(expected: WorldBodyAttachmentMode.Climb, actual: body.AttachmentMode);

        // Climb upward (the tangent basis at this wall's clean face normal is world +Y) for one full tick, so
        // m_climbVelocity carries a known, nonzero value the instant before detach.
        body.SubmitIntent(intent: Forward());
        fixture.Step();

        var yAtDetach = body.FixedPosition.Y;

        body.SubmitIntent(intent: Detach());
        fixture.Step();

        Assert.Equal(expected: WorldBodyAttachmentMode.None, actual: body.AttachmentMode);

        var stepSeconds = (1.0 / 240.0);
        var climbSpeed = 2.0;
        // The climb tick immediately before detach moved the body by climbSpeed * stepSeconds along +Y (the
        // accumulator's own remainder aside) — Detach carries that same rate, scaled, into the vertical channel, so
        // the FIRST post-detach tick's own vertical displacement should match it within one accumulator step's
        // rounding.
        var expectedDelta = ((climbSpeed * scale) * stepSeconds);
        var actualDelta = ((double)((float)((double)(body.FixedPosition.Y - yAtDetach))));

        Assert.True(
            condition: (Math.Abs(value: (actualDelta - expectedDelta)) < (expectedDelta * 0.5)),
            userMessage: $"post-detach vertical delta {actualDelta} should track the scaled release velocity ~{expectedDelta}"
        );
        Assert.True(condition: (actualDelta > 0), userMessage: "detach must carry POSITIVE (upward) momentum forward, never drop it to zero");
    }
    [Fact]
    public void IdenticalAttachClimbDetachReplays_ProduceIdenticalHashTraces_WhileOmittingAttachDiverges() {
        static ulong[] DriveHashTrace(bool attach) => Fixtures.DriveHashTrace(
            document: BuildAttachmentDocument(),
            ticks: 240,
            join: static fixture => JoinBody(fixture: fixture),
            perTick: (body, tick) => {
                var intent = default(PlayerIntent);

                if (attach && (tick == 0)) {
                    intent = Attach();
                } else if (attach && (tick is > 5 and < 60)) {
                    intent = Forward();
                } else if (attach && (tick == 120)) {
                    intent = Detach();
                }

                body.SubmitIntent(intent: intent);
            }
        );

        var first = DriveHashTrace(attach: true);
        var second = DriveHashTrace(attach: true);

        Assert.Equal(actual: second, expected: first);

        var withoutAttach = DriveHashTrace(attach: false);

        Assert.NotEqual(actual: withoutAttach, expected: first);
    }
    [Fact]
    public void ClimbCheckpointRestore_PreservesTheWholeTangentIntegratorAndContinuesBitIdentically() {
        using var fixture = Fixtures.FreshServer(definition: BuildAttachmentDocument());
        var uninterruptedBody = JoinBody(fixture: fixture);

        uninterruptedBody.SubmitIntent(intent: Attach());
        fixture.Step();
        for (var tick = 0; (tick < 7); tick++) {
            uninterruptedBody.SubmitIntent(intent: Forward());
            fixture.Step();
        }

        using var restored = Restore(
            fixture: fixture,
            identity: "climb-checkpoint"
        );
        var restoredBody = restored.Server.Body(index: 0)!;

        Assert.Equal(expected: WorldBodyAttachmentMode.Climb, actual: restoredBody.AttachmentMode);
        Assert.Equal(expected: uninterruptedBody.CaptureIntegrationResidue(), actual: restoredBody.CaptureIntegrationResidue());

        var elapsed = 0UL;
        var nextTick = fixture.Server.NextInputTick;

        for (var step = 0; (step < 24); step++) {
            uninterruptedBody.SubmitIntent(intent: Forward());
            restoredBody.SubmitIntent(intent: Forward());
            elapsed = checked((elapsed + Fixtures.StepTicks));
            var context = new FixedStepContext(ElapsedTicks: elapsed, StepTicks: Fixtures.StepTicks, Tick: nextTick++);

            fixture.Server.Step(context: in context);
            restored.Server.Step(context: in context);

            Assert.Equal(expected: WorldReplaySnapshot.HashState(population: fixture.Server.Population), actual: WorldReplaySnapshot.HashState(population: restored.Server.Population));
            Assert.Equal(expected: uninterruptedBody.CaptureIntegrationResidue(), actual: restoredBody.CaptureIntegrationResidue());
        }
    }
    [Fact]
    public void GrappleAttach_ConstrainsThroughTheOrdinaryTetherPipeline() {
        var document = BuildAttachmentDocument(includeWall: false);

        using var fixture = Fixtures.FreshServer(definition: document);
        var body = JoinBody(fixture: fixture);

        body.SubmitIntent(intent: Attach());
        fixture.Step();

        // No climbable surface within reach (the wall is excluded), so the SAME attach channel falls through to the
        // directed grapple query and anchors the tether to the post ahead instead.
        Assert.Equal(expected: WorldBodyAttachmentMode.Grapple, actual: body.AttachmentMode);
        Assert.NotNull(@object: body.AttachmentRopeLength);
        Assert.NotNull(@object: body.TetherLength);
        Assert.False(condition: body.AttachmentGrantedByOverride);
    }
    [Fact]
    public void GrappleCheckpointRestore_PreservesRopeAnchorAndReelFractionAndContinuesBitIdentically() {
        using var fixture = Fixtures.FreshServer(definition: BuildAttachmentDocument(includeWall: false));
        var uninterruptedBody = JoinBody(fixture: fixture);

        uninterruptedBody.SubmitIntent(intent: Attach());
        fixture.Step();
        for (var tick = 0; (tick < 7); tick++) {
            uninterruptedBody.SubmitIntent(intent: ReelIn());
            fixture.Step();
        }

        using var restored = Restore(
            fixture: fixture,
            identity: "grapple-checkpoint"
        );
        var restoredBody = restored.Server.Body(index: 0)!;

        Assert.Equal(expected: WorldBodyAttachmentMode.Grapple, actual: restoredBody.AttachmentMode);
        Assert.Equal(expected: uninterruptedBody.CaptureIntegrationResidue(), actual: restoredBody.CaptureIntegrationResidue());

        var elapsed = 0UL;
        var nextTick = fixture.Server.NextInputTick;

        for (var step = 0; (step < 24); step++) {
            uninterruptedBody.SubmitIntent(intent: ReelIn());
            restoredBody.SubmitIntent(intent: ReelIn());
            elapsed = checked((elapsed + Fixtures.StepTicks));
            var context = new FixedStepContext(ElapsedTicks: elapsed, StepTicks: Fixtures.StepTicks, Tick: nextTick++);

            fixture.Server.Step(context: in context);
            restored.Server.Step(context: in context);

            Assert.Equal(expected: uninterruptedBody.TetherLength, actual: restoredBody.TetherLength);
            Assert.Equal(expected: WorldReplaySnapshot.HashState(population: fixture.Server.Population), actual: WorldReplaySnapshot.HashState(population: restored.Server.Population));
            Assert.Equal(expected: uninterruptedBody.CaptureIntegrationResidue(), actual: restoredBody.CaptureIntegrationResidue());
        }
    }
    [Fact]
    public void AttachChannelNamingAnUndeclaredChannel_RefusesValidation_WhereADeclaredNameIsAdmitted() {
        var denied = BuildAttachmentDocument(attachChannelName: "no-such-channel", declareAttachChannel: false);

        Assert.False(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var deniedReason),
            userMessage: "an attachChannel naming no declared channel was expected to refuse"
        );
        Assert.Contains(actualString: deniedReason, expectedSubstring: "attachChannel");

        var admitted = BuildAttachmentDocument();

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: admitted, reason: out var admittedReason), userMessage: admittedReason);
    }
    [Fact]
    public void PlacementGripWithoutSolid_RefusesValidation_WhereGripPairedWithSolidIsAdmitted() {
        var wall = BuildBoxCreation(halfExtents: new Vector3(x: 0.2f, y: 3f, z: 3f), id: "wall");
        var document = Fixtures.BuildDocument();
        var ungripped = document with {
            CreationsRaw = [wall],
            PlacementsRaw = (document.PlacementsRaw! with {
                Rows = [
                    new WorldPlacement(Id: "wall1", PrototypeId: "wall", Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: null, Grip: new WorldPlacementGrip(Climbable: true)),
                ],
            }),
        };

        Assert.False(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: ungripped, reason: out var deniedReason),
            userMessage: "a grip facet with no solid facet was expected to refuse"
        );
        Assert.Contains(actualString: deniedReason, expectedSubstring: "grip");

        var gripped = document with {
            CreationsRaw = [wall],
            PlacementsRaw = (document.PlacementsRaw! with {
                Rows = [
                    new WorldPlacement(Id: "wall1", PrototypeId: "wall", Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f), Grip: new WorldPlacementGrip(Climbable: true)),
                ],
            }),
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: gripped, reason: out var admittedReason), userMessage: admittedReason);
    }
    [Fact]
    public void SetTetherToBody_DragsTheTetheredBodyThroughTheRealPopulationPipeline() {
        var document = Fixtures.BuildDocument();

        using var fixture = Fixtures.FreshServer(definition: document);
        var anchorBody = JoinBody(fixture: fixture, slot: 0);
        var tetheredBody = JoinBody(fixture: fixture, slot: 1);
        var length = FixedQ4816.FromInteger(value: 4L);

        anchorBody.Pose(pitchRadians: 0f, rollRadians: 0f, x: 0f, y: 0f, yawRadians: 0f, z: 0f);
        tetheredBody.Pose(pitchRadians: 0f, rollRadians: 0f, x: -4f, y: 0f, yawRadians: 0f, z: 0f);
        tetheredBody.SetTetherToBody(
            bodyIndex: 0,
            length: length,
            localOffset: FixedVector3.Zero,
            minLength: FixedQ4816.One
        );

        var maxAllowedDistanceSquared = (FixedQ4816.FromDouble(value: 4.2) * FixedQ4816.FromDouble(value: 4.2));

        for (var tick = 0; (tick < 240); tick++) {
            // Drive the anchor away at a steady rate; the tethered body submits no intent of its own — every inch
            // it moves comes from the tether constraint alone (ResolveTethers, reached through the ordinary
            // WorldServer.Step pipeline, never called directly by this test).
            anchorBody.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One));
            tetheredBody.SubmitIntent(intent: default);
            fixture.Step();

            var distanceSquared = (anchorBody.FixedPosition - tetheredBody.FixedPosition).LengthSquared;

            Assert.True(
                condition: (distanceSquared <= maxAllowedDistanceSquared),
                userMessage: $"tick {tick}: tethered body drifted to {(anchorBody.FixedPosition - tetheredBody.FixedPosition).Length} from its anchor, past the {length} rope"
            );
        }

        // The anchor never reads the tether at all — one-way by construction (FixedTetherConstraint's own remarks).
        // Confirms the REAL population pipeline preserved that, not just the raw kernel slice-1 already pins.
        Assert.Null(@object: anchorBody.TetherLength);
        Assert.NotNull(@object: tetheredBody.TetherLength);
        Assert.True(
            condition: (anchorBody.FixedPosition.LengthSquared > FixedQ4816.One),
            userMessage: $"the anchor should have moved measurably under its own intent, undisturbed by the tether it drags; ended at {anchorBody.FixedPosition}"
        );
    }
}
