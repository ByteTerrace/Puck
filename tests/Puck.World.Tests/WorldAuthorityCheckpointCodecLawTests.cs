using System.Numerics;

using Xunit;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.Storage;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Round-trip and refusal laws for <see cref="WorldAuthorityCheckpointCodec"/>, then the hermetic wiring
/// through <see cref="WorldAuthorityBlobStore"/> over <see cref="FakeObjectBlobStore"/>.</summary>
public sealed class WorldAuthorityCheckpointCodecLawTests {
    private static readonly ObjectStorageTarget Target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: "UseDevelopmentStorage=true");

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
    // Carries one populated WorldInDoubtTransferCheckpoint (commit members AND landed members both non-empty) so the
    // round-trip laws below actually exercise every leaf the in-doubt shape added, not just its zero-length case.
    private static WorldAuthorityHostRowCheckpoint SampleHostRow(WorldBody.TransferState dynamicState) {
        var commitMember = new WorldTransferCommitMember(
            ActionContinuity: new WorldTransferActionContinuity(
                Channels: [new WorldTransferChannelEdge(Name: "move", PreviousBit: true, HeldValue: FixedQ4816.FromInteger(value: 1))],
                Registers: []
            ),
            BodyMotionProgramName: "walk",
            Continuum: null,
            HasMappedArrival: false,
            PlanarVelocity: new FixedVector3(X: FixedQ4816.FromInteger(value: 1), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            Position: new FixedVector3(X: FixedQ4816.FromInteger(value: 2), Y: FixedQ4816.Zero, Z: FixedQ4816.FromInteger(value: 3)),
            Profile: null,
            VerticalVelocity: FixedQ4816.Zero,
            YawRadians: FixedQ4816.Zero
        );
        var landed = new WorldLandedMemberCheckpoint(
            AdmissionGrants: [new WorldAdmissionGrant(Capability: WorldCapability.Drive, Subject: GrantSubject.Body(index: 4))],
            BodyColor: new Vector3(x: 1, y: 0, z: 0),
            Designations: [
                WorldTargetDesignation.Body(index: 1),
                WorldTargetDesignation.AtPoint(point: new FixedVector3(
                    X: FixedQ4816.FromInteger(value: 7),
                    Y: FixedQ4816.Zero,
                    Z: -FixedQ4816.FromInteger(value: 3)
                ))
            ],
            DynamicState: dynamicState,
            Mobility: new WorldMobilityIdentity(
                Incarnation: new WorldEntityAddress(Authority: "row-a", Generation: 1, Index: 0),
                Epoch: 1
            ),
            Peer: new WorldPeerEventEntry(
                AuthorityTransferred: false,
                BodyIndex: 4,
                CatalogRig: 0,
                Generation: 1,
                Identity: WorldPrincipal.Peer(generation: 1, index: 4),
                IdentityDomain: "example.test",
                IdentitySubject: "traveler",
                PlacementId: null,
                Source: IntentSource.Live
            ),
            Position: new FixedVector3(X: FixedQ4816.FromInteger(value: 5), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            SourceGrants: [new WorldGrant(
                Principal: WorldPrincipal.Peer(generation: 1, index: 4),
                Capability: WorldCapability.Drive,
                Subject: GrantSubject.Body(index: 4),
                Exclusive: false
            )],
            SourceSlot: 0,
            TargetSlot: 4,
            Yaw: FixedQ4816.Zero
        );
        var inDoubt = new WorldInDoubtTransferCheckpoint(
            CommitMembers: [commitMember],
            Landed: [landed],
            MemberCount: 1,
            SourceDeadlineTick: 12345,
            SourceInstance: "row-a",
            Spawned: false,
            TargetAuthority: "row-b",
            TargetEndpoint: null,
            TargetName: "row-b",
            TransferId: 7
        );

        return new WorldAuthorityHostRowCheckpoint(
            AnnouncedCrossingHolds: [],
            AppliedTransferHighWater: null,
            AppliedTransferIds: [],
            ElapsedEngineTicks: 0,
            ForwardedBodies: [],
            FreshCounter: 0,
            InDoubtTransfers: [inDoubt],
            IsPaused: false,
            NextTransferId: 1,
            PortalOccupancy: [],
            Retained: false,
            ScheduleAccumulatorTicks: 0,
            SeededArrivals: []
        );
    }
    private static WorldAuthorityCheckpoint CapturedCheckpoint() {
        using var fixture = Fixtures.FreshServer();

        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: WorldPrincipal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        ));
        _ = fixture.Server.Population.SetSimulatedCount(count: 3);

        for (var tick = 0; (tick < 500); tick++) {
            fixture.Step();
        }

        var dynamicState = fixture.Server.Body(index: 0)!.CaptureTransferState();

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: SampleHostRow(dynamicState: dynamicState),
            reason: out var reason
        ), userMessage: reason);

        return checkpoint!;
    }

    [Fact]
    public void Decode_of_Encode_is_structurally_equal() {
        var checkpoint = CapturedCheckpoint();
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: encoded,
            checkpoint: out var decoded,
            reason: out var reason
        ), userMessage: reason);
        var equal = DeepEqual.Compare(
            a: checkpoint,
            b: decoded
        );

        Assert.True(condition: equal, userMessage: DeepEqual.LastMismatchPath);
    }
    [Fact]
    public void Field_section_round_trips_structurally() {
        var checkpoint = CapturedCheckpoint() with {
            Fields = new WorldFieldLattice.WorldFieldCheckpoint(Raw: [
                [FixedQ4816.Zero.Value, FixedQ4816.FromInteger(value: 1).Value],
                [FixedQ4816.FromInteger(value: -2).Value, FixedQ4816.FromInteger(value: 3).Value],
            ]),
        };
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: encoded,
            checkpoint: out var decoded,
            reason: out var reason
        ), userMessage: reason);
        Assert.True(
            condition: DeepEqual.Compare(a: checkpoint.Fields, b: decoded!.Fields),
            userMessage: DeepEqual.LastMismatchPath
        );
    }
    [Fact]
    public void Encode_of_Decode_is_byte_identical() {
        var checkpoint = CapturedCheckpoint();
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: encoded,
            checkpoint: out var decoded,
            reason: out var reason
        ), userMessage: reason);

        var reEncoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: decoded!);

        Assert.Equal(
            actual: reEncoded,
            expected: encoded
        );
    }
    [Fact]
    public void Truncated_blob_refuses_by_name() {
        var checkpoint = CapturedCheckpoint();
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);
        var truncated = encoded[..(encoded.Length / 2)];

        Assert.False(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: truncated,
            checkpoint: out _,
            reason: out var reason
        ));
        Assert.NotEqual(
            actual: reason,
            expected: string.Empty
        );
    }
    [Fact]
    public void Bit_flipped_blob_refuses_by_name() {
        var checkpoint = CapturedCheckpoint();
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);
        var flipped = ((byte[])encoded.Clone());

        flipped[(flipped.Length / 2)] ^= 0xFF;

        Assert.False(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: flipped,
            checkpoint: out _,
            reason: out var reason
        ));
        Assert.NotEqual(
            actual: reason,
            expected: string.Empty
        );
    }
    [Fact]
    public void Version_mismatch_refuses_by_name() {
        var checkpoint = CapturedCheckpoint();
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);
        var corrupted = ((byte[])encoded.Clone());

        // The version u16 sits immediately after the 4-byte "PCKP" magic (WorldAuthorityCheckpointCodec's own wire
        // layout) — bump it past the one supported value.
        corrupted[4] = 0xFF;
        corrupted[5] = 0xFF;

        Assert.False(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: corrupted,
            checkpoint: out _,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "version"
        );
    }
    [Fact]
    public void Dynamics_kit_body_with_nonzero_follower_state_round_trips_structurally() {
        var document = Fixtures.BuildDocument();
        var kit = document.Kits[0];
        var motion = kit.Motion;

        document = document with {
            DynamicsRaw = [.. Fixtures.StandardDynamics, new WorldDynamicsRow(Damping: 1f, Frequency: 2f, Name: "settle", Response: 0f)],
            KitRowsRaw = [kit with { Motion = motion with { Shaping = [motion.Shaping![0] with { Along = null, Dynamics = "settle" }] } }],
        };

        using var fixture = Fixtures.FreshServer(definition: document);

        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: WorldPrincipal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        ));

        var body = fixture.Server.Body(index: 0)!;

        for (var tick = 0; (tick < 24); tick++) {
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: 0, value: FixedQ4816.One));
            fixture.Step();
        }

        var dynamicState = body.CaptureTransferState();

        Assert.True(condition: ((dynamicState.PlanarFollowerPositionRawX | dynamicState.PlanarFollowerPositionRawY | dynamicState.PlanarFollowerPositionRawZ) != 0L), userMessage: "the driven follower must be off rest before this law proves anything");

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: SampleHostRow(dynamicState: dynamicState),
            reason: out var reason
        ), userMessage: reason);
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: encoded,
            checkpoint: out var decoded,
            reason: out var decodeReason
        ), userMessage: decodeReason);
        Assert.True(
            condition: DeepEqual.Compare(a: checkpoint, b: decoded),
            userMessage: DeepEqual.LastMismatchPath
        );
    }
    [Fact]
    public void Curve_follow_producer_with_nonzero_arc_state_round_trips_structurally() {
        var curveRow = new WorldCurveRow(
            Name: "path",
            Knots: [
                new WorldCurveKnot(Position: new DocumentVector3(x: 0f, y: 0f, z: 0f), TangentYaw: 0f, Curvature: 0f),
                new WorldCurveKnot(Position: new DocumentVector3(x: 20f, y: 0f, z: 0f), TangentYaw: 0f, Curvature: 0f),
            ],
            Closed: false
        );
        var document = (Fixtures.BuildDocument() with { CurvesRaw = [curveRow] });
        var kit = document.Kits[0];
        var followProgram = new BodyMotionProgram(
            Name: "follow",
            Version: "puck.body-motion.v1",
            Kind: BodyProgramKind.Producer,
            Operations: [BodyMotionOp.SenseNearestInCone, BodyMotionOp.FaceSensorTarget, BodyMotionOp.ProduceSteeringIntent],
            Target: new BodyTargetSource.CurveFollow(Curve: "path", Rate: 2f)
        );

        document = (document with {
            BodyMotionProgramsRaw = [.. document.BodyMotionPrograms, followProgram],
            KitRowsRaw = [kit with {
                ProducersRaw = new Dictionary<string, BodyProgramParameters>(collection: kit.Producers) {
                    ["follow"] = new BodyProgramParameters(
                        Scalars: new Dictionary<string, float> {
                            ["standoffRadius"] = 0.1f,
                            ["approach"] = 1f,
                            ["orbit"] = 0f,
                            ["altitudeGain"] = 0f,
                            ["inwardGain"] = 3f,
                            ["turnScale"] = 3f,
                            ["forward"] = 0f,
                            ["softRadius"] = 1f,
                            ["weaveAmplitude"] = 0f,
                            ["weaveFrequencyBase"] = 0f,
                            ["weaveFrequencyRange"] = 0f,
                            ["activityRateBase"] = 0f,
                            ["activityRateRange"] = 0f,
                            ["strafeWave"] = 0f,
                            ["turnWave"] = 0f,
                            ["upWave"] = 0f,
                            ["pitchWave"] = 0f,
                            ["rollTurn"] = 0f,
                            ["pressThreshold"] = 0f,
                            ["altitudeBase"] = 0f,
                            ["altitudeRange"] = 0f,
                        },
                        Channels: new Dictionary<string, string>()
                    ),
                },
            }],
        });

        using var fixture = Fixtures.FreshServer(definition: document);

        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: WorldPrincipal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        ));

        fixture.Server.Body(index: 0)!.SetIntentSource(source: IntentSource.Producer(name: "follow"));

        for (var tick = 0; (tick < 24); tick++) {
            fixture.Step();
        }

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: EmptyHostRow(),
            reason: out var reason
        ), userMessage: reason);

        var entry = checkpoint!.Population.Entries.Single(predicate: static row => (row.Index == 0));

        Assert.True(condition: (entry.ProducerCurveArcRaw != 0L), userMessage: "the driven curve-follow arc position must be off rest before this law proves anything");

        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: encoded,
            checkpoint: out var decoded,
            reason: out var decodeReason
        ), userMessage: decodeReason);
        Assert.True(
            condition: DeepEqual.Compare(a: checkpoint, b: decoded),
            userMessage: DeepEqual.LastMismatchPath
        );
    }
    [Fact]
    public void Version_two_envelope_refuses_by_name() {
        var checkpoint = CapturedCheckpoint();
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);
        var downgraded = ((byte[])encoded.Clone());

        // The version u16 sits immediately after the 4-byte "PCKP" magic — pin the PRE-follower-state version
        // literally (2), not an arbitrary corrupt value, to prove the specific old wire shape is refused rather than
        // silently tolerated by a reader that skips the new fields.
        downgraded[4] = 2;
        downgraded[5] = 0;

        Assert.False(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: downgraded,
            checkpoint: out _,
            reason: out var reason
        ));
        Assert.Contains(actualString: reason, expectedSubstring: "version 2");
    }
    [Fact]
    public void Version_four_envelope_refuses_by_name() {
        var checkpoint = CapturedCheckpoint();
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);
        var downgraded = ((byte[])encoded.Clone());

        // Version 4 predates checkpoint-only arbitrary-up, follower-seed, and attachment continuation state. The
        // current decoder must reject that exact prior layout instead of reading its shorter population residue.
        downgraded[4] = 4;
        downgraded[5] = 0;

        Assert.False(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: downgraded,
            checkpoint: out _,
            reason: out var reason
        ));
        Assert.Contains(actualString: reason, expectedSubstring: "version 4");
    }
    [Fact]
    public void Version_five_envelope_refuses_by_name() {
        var checkpoint = CapturedCheckpoint();
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);
        var downgraded = ((byte[])encoded.Clone());

        // Version 5 predates the curve-follow producer's arc-position state on the population entry. The current
        // decoder must reject that exact prior layout instead of reading its shorter population residue.
        downgraded[4] = 5;
        downgraded[5] = 0;

        Assert.False(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: downgraded,
            checkpoint: out _,
            reason: out var reason
        ));
        Assert.Contains(actualString: reason, expectedSubstring: "version 5");
    }
    [Fact]
    public void Version_six_envelope_refuses_by_name() {
        var checkpoint = CapturedCheckpoint();
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);
        var downgraded = ((byte[])encoded.Clone());

        // Version 6 predates cached navigation routes. Refuse that shorter population entry rather than allowing a
        // resumed producer to silently choose a different continuation after restore.
        downgraded[4] = 6;
        downgraded[5] = 0;

        Assert.False(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: downgraded,
            checkpoint: out _,
            reason: out var reason
        ));
        Assert.Contains(actualString: reason, expectedSubstring: "version 6");
    }
    [Fact]
    public async Task Capture_encode_write_load_decode_restore_reaches_an_identical_second_checkpoint() {
        var checkpoint = CapturedCheckpoint();
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);
        var store = new WorldAuthorityBlobStore(
            store: new FakeObjectBlobStore(),
            target: Target
        );
        var identity = new WorldAuthorityIdentity(
            Owner: Guid.NewGuid(),
            World: WorldSafeName.Parse(candidate: "amber")
        );
        var cancellationToken = TestContext.Current.CancellationToken;

        var written = await store.WriteCheckpointAsync(
            cancellationToken: cancellationToken,
            encoded: encoded,
            identity: identity,
            tick: checkpoint.Server.LastCompletedTick
        );

        Assert.True(condition: written.Ok, userMessage: written.Detail);

        var loaded = await store.LoadLatestAsync(
            cancellationToken: cancellationToken,
            identity: identity
        );

        Assert.True(condition: loaded.HasValue);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: loaded!.Value.Encoded.Span,
            checkpoint: out var decoded,
            reason: out var reason
        ), userMessage: reason);

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: decoded!.Server.DefinitionJson);
        using var restoredMachines = new WorldMachineHost(
            engines: [],
            screens: definition.Screens
        );

        var (restoredServer, _) = WorldServer.FromCheckpoint(
            checkpoint: decoded,
            instanceIdentity: "boot",
            machines: restoredMachines,
            profiles: new WorldOwnedWorlds(
                directory: Directory.CreateTempSubdirectory(prefix: "puck-checkpoint-codec-tests-").FullName,
                machineId: Guid.NewGuid(),
                template: definition
            )
        );

        // The restored body's dynamic state must be bit-identical to the one CapturedCheckpoint() fed SampleHostRow
        // above for the second checkpoint to reach the SAME encoded bytes — re-reading it here (rather than reusing
        // the first call's local) is itself part of what this law proves.
        var restoredDynamicState = restoredServer.Body(index: 0)!.CaptureTransferState();

        Assert.True(condition: restoredServer.TryCaptureCheckpoint(
            checkpoint: out var secondCheckpoint,
            hostRow: SampleHostRow(dynamicState: restoredDynamicState),
            reason: out var secondReason
        ), userMessage: secondReason);

        var secondEncoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: secondCheckpoint!);

        Assert.Equal(
            actual: secondEncoded,
            expected: encoded
        );
    }
}
