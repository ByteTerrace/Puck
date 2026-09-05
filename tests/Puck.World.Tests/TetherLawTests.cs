using System.Numerics;
using Puck.World.Authoring;
using Puck.Hosting;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.SignedDistance;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Law coverage for a kit's tether facet: intent-to-attach determinism, the tether pipeline, checkpoint
/// continuation, and the validator's channel/grip-composition refusals.</summary>
public sealed class TetherLawTests {
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
    // BuildTetherDocument's remarks) within this tolerance of world X 1.8: exactly the half-extent margin plus
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
    // Extends Fixtures.BuildDocument with a tether-ready world: a wall placement centered at (2,0,0) with
    // half-extents (0.2,3,3) and three composition channels (attach/detach/reel) appended after the base document's
    // forward/strafe/turn roles (ordinals 0..2), landing at 3/4/5, and the fixture's one kit given a tether facet.
    // modeState is left unset by default (matching every test written before that facet field existed); a caller
    // wanting the row declares it by name (modeStateRowKind: ActionStateKind.Counter, the only kind the validator
    // admits) or leaves it undeclared (modeStateRowKind: null) to exercise the validator's undeclared-name refusal.
    private static WorldDefinition BuildTetherDocument(bool wallHoldable = true, string? attachChannelName = "attach", bool declareAttachChannel = true, bool includeWall = true, float wallHalfHeight = 3f, string? modeState = null, ActionStateKind? modeStateRowKind = ActionStateKind.Counter) {
        var wall = BuildBoxCreation(
            halfExtents: new Vector3(x: 0.2f, y: wallHalfHeight, z: 3f),
            id: "wall"
        );
        // The tether target, directly ahead of a body's default facing (yaw 0 faces -Z) so the directed aim query
        // finds it without needing a turn.
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
                Grip: (wallHoldable ? new WorldPlacementGrip(Holdable: true) : null)
            ));
        }

        var kit = document.Kits[0] with {
            Tether = new WorldTether(
                AimHalfAngleDegrees: 30f,
                AttachChannel: attachChannelName,
                DetachChannel: "detach",
                LengthRate: 2f,
                MaxAnchorDistance: 20f,
                MinLength: 1f,
                ModeState: modeState,
                ReelChannel: "reel",
                ReleaseVelocityScale: 1f
            ),
        };

        return document with {
            ChannelsRaw = channels,
            CreationsRaw = [wall, post],
            KitRowsRaw = [kit],
            PlacementsRaw = (document.PlacementsRaw! with {
                Rows = rows,
            }),
            StateRaw = (((modeState is { Length: > 0 }) && (modeStateRowKind is { } kind))
                ? new WorldStateSection(Body: [new ActionStateSlot(Name: modeState, Kind: kind)], World: [])
                : document.StateRaw),
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
        var stateDirectory = Directory.CreateTempSubdirectory(prefix: "puck-tether-checkpoint-").FullName;
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
    public void TetherAttach_ConstrainsThroughTheOrdinaryTetherPipeline() {
        var document = BuildTetherDocument(includeWall: false);

        using var fixture = Fixtures.FreshServer(definition: document);
        var body = JoinBody(fixture: fixture);

        body.SubmitIntent(intent: Attach());
        fixture.Step();

        // The attach channel's rising edge runs the directed aim query and anchors the tether to the post ahead.
        Assert.NotNull(@object: body.TetherLength);
    }
    [Fact]
    public void TetherCheckpointRestore_PreservesRopeAnchorAndReelFractionAndContinuesBitIdentically() {
        using var fixture = Fixtures.FreshServer(definition: BuildTetherDocument(includeWall: false));
        var uninterruptedBody = JoinBody(fixture: fixture);

        uninterruptedBody.SubmitIntent(intent: Attach());
        fixture.Step();
        for (var tick = 0; (tick < 7); tick++) {
            uninterruptedBody.SubmitIntent(intent: ReelIn());
            fixture.Step();
        }

        using var restored = Restore(
            fixture: fixture,
            identity: "tether-checkpoint"
        );
        var restoredBody = restored.Server.Body(index: 0)!;

        Assert.NotNull(@object: restoredBody.TetherLength);
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
    public void AimBeyondMaxAnchorDistance_LeavesTheBodyUnattached_WhereAnAnchorInReachAttaches() {
        var farDocument = BuildTetherDocument(includeWall: false);
        // The fixture's post sits at world Z -5; move it past the facet's authored maxAnchorDistance (20) so the
        // directed aim query finds nothing.
        var farPlacements = farDocument.Placements.Select(selector: row => ((row.Id == "post1")
            ? (row with { Position = new Vector3(x: 0f, y: 0f, z: -25f) })
            : row
        )).ToList();
        var far = farDocument with {
            PlacementsRaw = (farDocument.PlacementsRaw! with { Rows = farPlacements }),
        };

        using var farFixture = Fixtures.FreshServer(definition: far);
        var farBody = JoinBody(fixture: farFixture);

        farBody.SubmitIntent(intent: Attach());
        farFixture.Step();

        Assert.Null(@object: farBody.TetherLength);

        using var nearFixture = Fixtures.FreshServer(definition: BuildTetherDocument(includeWall: false));
        var nearBody = JoinBody(fixture: nearFixture);

        nearBody.SubmitIntent(intent: Attach());
        nearFixture.Step();

        Assert.NotNull(@object: nearBody.TetherLength);
    }
    [Fact]
    public void HeldReelIn_ShrinksTheRopeThroughTheTickPathAndClampsAtMinLength() {
        var document = BuildTetherDocument(includeWall: false);

        using var fixture = Fixtures.FreshServer(definition: document);
        var body = JoinBody(fixture: fixture);

        body.SubmitIntent(intent: Attach());
        fixture.Step();

        var attachedLength = body.TetherLength;

        Assert.NotNull(@object: attachedLength);
        Assert.Equal(expected: FixedQ4816.One, actual: body.TetherMinLength);
        Assert.True(condition: (attachedLength!.Value > FixedQ4816.One), userMessage: $"expected an initial rope longer than minLength to give the reel something to shrink; got {attachedLength}");

        // lengthRate is 2 world-units/second at the fixture's 30 Hz tick rate and maxAnchorDistance caps the
        // longest possible initial rope at 20 — comfortably enough ticks (19 / (2/30) ~= 285) to run the rope from
        // its initial length down past minLength however far the post's near face resolved, proving the clamp
        // rather than just an early sample.
        for (var tick = 0; (tick < 400); tick++) {
            body.SubmitIntent(intent: ReelIn());
            fixture.Step();
        }

        Assert.Equal(expected: FixedQ4816.One, actual: body.TetherLength);

        // The control: one further held reel-in tick leaves the rope exactly at the floor instead of continuing
        // past it (a broken clamp would either go negative or leave a fixed-point residue below minLength).
        body.SubmitIntent(intent: ReelIn());
        fixture.Step();

        Assert.Equal(expected: FixedQ4816.One, actual: body.TetherLength);
    }
    [Fact]
    public void TetherModeStateFlipsThroughTheOrdinaryAttachAndDetachChannelEdges() {
        var document = BuildTetherDocument(includeWall: false, modeState: "tethered");

        using var fixture = Fixtures.FreshServer(definition: document);
        var body = JoinBody(fixture: fixture);

        Assert.True(condition: body.TryDescribeActionState(name: "tethered", kind: out var kind, lifetime: out _, playerWritable: out _, value: out var initial, timerTicks: out _));
        Assert.Equal(expected: ActionStateKind.Counter, actual: kind);
        Assert.Equal(expected: FixedQ4816.Zero, actual: initial);

        body.SubmitIntent(intent: Attach());
        fixture.Step();

        Assert.NotNull(@object: body.TetherLength);
        Assert.True(condition: body.TryDescribeActionState(name: "tethered", kind: out _, lifetime: out _, playerWritable: out _, value: out var afterAttach, timerTicks: out _));
        Assert.Equal(expected: FixedQ4816.One, actual: afterAttach);

        // Through the ordinary channel-driven edge (never DetachTether by reflection) — the same edge a client's
        // body.detach press drives.
        body.SubmitIntent(intent: Detach());
        fixture.Step();

        Assert.Null(@object: body.TetherLength);
        Assert.True(condition: body.TryDescribeActionState(name: "tethered", kind: out _, lifetime: out _, playerWritable: out _, value: out var afterDetach, timerTicks: out _));
        Assert.Equal(expected: FixedQ4816.Zero, actual: afterDetach);
    }
    // DetachTether is private (the channel-driven path always runs it ahead of the SAME tick's ordinary motion
    // program, which would otherwise mask the scale under its own shaping/gravity), so this reaches it directly —
    // the same reflection idiom Win32ProbeKernelBenchCleanupTests already uses for a unit assertion the public
    // surface cannot isolate.
    private static void InvokeDetachTether(WorldBody body) => typeof(WorldBody).GetMethod(
        name: "DetachTether",
        bindingAttr: (System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
    )!.Invoke(obj: body, parameters: null);
    [Fact]
    public void Detach_ScalesSurvivingVelocityByReleaseVelocityScale_WhereAUnitScalePreservesIt() {
        var seeded = new FixedVector3(X: FixedQ4816.FromInteger(value: 6L), Y: FixedQ4816.Zero, Z: FixedQ4816.FromInteger(value: 2L));
        var seededVertical = FixedQ4816.FromInteger(value: -4L);

        var scaledDocument = BuildTetherDocument(includeWall: false);
        var scaledKit = scaledDocument.Kits[0] with {
            Tether = (scaledDocument.Kits[0].Tether! with { ReleaseVelocityScale = 0.5f }),
        };
        var scaled = scaledDocument with { KitRowsRaw = [scaledKit] };

        using var scaledFixture = Fixtures.FreshServer(definition: scaled);
        var scaledBody = JoinBody(fixture: scaledFixture);

        scaledBody.SetTetherToWorldPoint(
            anchor: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.FromInteger(value: -5L)),
            length: FixedQ4816.FromInteger(value: 5L),
            minLength: FixedQ4816.One
        );
        scaledBody.ApplyTransferState(state: (scaledBody.CaptureTransferState() with { PlanarVelocity = seeded, VerticalVelocity = seededVertical }));

        InvokeDetachTether(body: scaledBody);

        var scaledSurvivor = scaledBody.CaptureTransferState();

        Assert.Null(@object: scaledBody.TetherLength);
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 3L), actual: scaledSurvivor.PlanarVelocity.X);
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 1L), actual: scaledSurvivor.PlanarVelocity.Z);
        Assert.Equal(expected: FixedQ4816.FromInteger(value: -2L), actual: scaledSurvivor.VerticalVelocity);

        var unitDocument = BuildTetherDocument(includeWall: false);

        using var unitFixture = Fixtures.FreshServer(definition: unitDocument);
        var unitBody = JoinBody(fixture: unitFixture);

        unitBody.SetTetherToWorldPoint(
            anchor: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.FromInteger(value: -5L)),
            length: FixedQ4816.FromInteger(value: 5L),
            minLength: FixedQ4816.One
        );
        unitBody.ApplyTransferState(state: (unitBody.CaptureTransferState() with { PlanarVelocity = seeded, VerticalVelocity = seededVertical }));

        InvokeDetachTether(body: unitBody);

        var unitSurvivor = unitBody.CaptureTransferState();

        Assert.Null(@object: unitBody.TetherLength);
        Assert.Equal(expected: seeded, actual: unitSurvivor.PlanarVelocity);
        Assert.Equal(expected: seededVertical, actual: unitSurvivor.VerticalVelocity);
    }
    [Fact]
    public void AttachChannelNamingAnUndeclaredChannel_RefusesValidation_WhereADeclaredNameIsAdmitted() {
        var denied = BuildTetherDocument(attachChannelName: "no-such-channel", declareAttachChannel: false);

        Assert.False(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var deniedReason),
            userMessage: "an attachChannel naming no declared channel was expected to refuse"
        );
        Assert.Contains(actualString: deniedReason, expectedSubstring: "attachChannel");

        var admitted = BuildTetherDocument();

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: admitted, reason: out var admittedReason), userMessage: admittedReason);
    }
    [Fact]
    public void ModeStateNamingAnUndeclaredSlot_RefusesValidation_WhereADeclaredCounterIsAdmitted() {
        var denied = BuildTetherDocument(modeState: "no-such-row", modeStateRowKind: null);

        Assert.False(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var deniedReason),
            userMessage: "a modeState naming no declared state slot was expected to refuse"
        );
        Assert.Contains(actualString: deniedReason, expectedSubstring: "modeState");

        var admitted = BuildTetherDocument(modeState: "tethered");

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: admitted, reason: out var admittedReason), userMessage: admittedReason);
    }
    [Fact]
    public void ModeStateNamingATimerSlot_RefusesValidation() {
        var denied = BuildTetherDocument(modeState: "tethered", modeStateRowKind: ActionStateKind.Timer);

        Assert.False(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var deniedReason),
            userMessage: "a modeState naming a Timer slot was expected to refuse"
        );
        Assert.Contains(actualString: deniedReason, expectedSubstring: "Counter");
    }
    [Fact]
    public void LiveTetherToTetherKitSwap_ClearsALiveAttach_WhereAKitSwapKeepingTheSameFacetDoesNot() {
        // A genuinely different facet (MaxAnchorDistance halved) dropped through the ordinary kit-retune path — the
        // live FixedTetherConstraint must not keep standing against a reach the new facet no longer authors.
        var changedDocument = BuildTetherDocument(includeWall: false, modeState: "tethered");

        using var changedFixture = Fixtures.FreshServer(definition: changedDocument);
        var changedBody = JoinBody(fixture: changedFixture);

        changedBody.SubmitIntent(intent: Attach());
        changedFixture.Step();
        Assert.NotNull(@object: changedBody.TetherLength);

        var retunedKit = changedFixture.Server.Definition.Kits[0] with {
            Tether = changedFixture.Server.Definition.Kits[0].Tether! with { MaxAnchorDistance = 10f },
        };

        changedFixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertKit(Principal: WorldPrincipal.Console, Kit: retunedKit));
        changedFixture.Step();

        Assert.Null(@object: changedBody.TetherLength);
        Assert.True(condition: changedBody.TryDescribeActionState(name: "tethered", kind: out _, lifetime: out _, playerWritable: out _, value: out var clearedMode, timerTicks: out _));
        Assert.Equal(expected: FixedQ4816.Zero, actual: clearedMode);

        // THE CONTROL: the same kit swap through the same mutation path, but retuning a field OTHER than Tether —
        // RecompileKit still runs (the kit genuinely swaps), yet the facet itself is unchanged, so the live attach
        // survives, proving the reset above keys on facet inequality rather than firing on every kit retune.
        var sameDocument = BuildTetherDocument(includeWall: false);

        using var sameFixture = Fixtures.FreshServer(definition: sameDocument);
        var sameBody = JoinBody(fixture: sameFixture);

        sameBody.SubmitIntent(intent: Attach());
        sameFixture.Step();
        Assert.NotNull(@object: sameBody.TetherLength);

        var retunedSpeedKit = sameFixture.Server.Definition.Kits[0] with {
            Motion = (sameFixture.Server.Definition.Kits[0].Motion! with {
                Speed = (sameFixture.Server.Definition.Kits[0].Motion!.Speed! with { Value = 9f }),
            }),
        };

        sameFixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertKit(Principal: WorldPrincipal.Console, Kit: retunedSpeedKit));
        sameFixture.Step();

        Assert.NotNull(@object: sameBody.TetherLength);
    }
    [Fact]
    public void KitWithNoTetherFacet_RefusesAttachThroughTheTickPath_WhereATetherFacetIsAdmitted() {
        var withoutFacet = Fixtures.BuildDocument();

        using var fixture = Fixtures.FreshServer(definition: withoutFacet);
        var body = JoinBody(fixture: fixture);

        Assert.False(condition: body.HasTetherFacet);

        // The channel-driven path itself, not just the flag: an attach edge against a facet-less kit's body never
        // reaches TryAttachTether (ProcessTetherIntent's own early return on a null m_tetherFacet).
        body.SubmitIntent(intent: Attach());
        fixture.Step();

        Assert.Null(@object: body.TetherLength);

        var withFacet = BuildTetherDocument(includeWall: false);

        using var tetheredFixture = Fixtures.FreshServer(definition: withFacet);
        var tetheredBody = JoinBody(fixture: tetheredFixture);

        Assert.True(condition: tetheredBody.HasTetherFacet);

        tetheredBody.SubmitIntent(intent: Attach());
        tetheredFixture.Step();

        Assert.NotNull(@object: tetheredBody.TetherLength);
    }
    [Fact]
    public void PlacementGripWithoutSolid_RefusesValidation_WhereGripPairedWithSolidIsAdmitted() {
        var wall = BuildBoxCreation(halfExtents: new Vector3(x: 0.2f, y: 3f, z: 3f), id: "wall");
        var document = Fixtures.BuildDocument();
        var ungripped = document with {
            CreationsRaw = [wall],
            PlacementsRaw = (document.PlacementsRaw! with {
                Rows = [
                    new WorldPlacement(Id: "wall1", PrototypeId: "wall", Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: null, Grip: new WorldPlacementGrip(Holdable: true)),
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
                    new WorldPlacement(Id: "wall1", PrototypeId: "wall", Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, Solid: new WorldSolid(Margin: 0f), Grip: new WorldPlacementGrip(Holdable: true)),
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
