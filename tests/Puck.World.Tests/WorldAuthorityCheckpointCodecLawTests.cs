using Puck.Commands;
using System.Numerics;

using Xunit;

using Puck.Assets.Documents;
using Puck.Physics.Fields;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.Storage;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Round-trip and refusal laws for <see cref="WorldAuthorityCheckpointCodec"/>, then the hermetic wiring
/// through <see cref="WorldAuthorityBlobStore"/> over <see cref="FakeObjectBlobStore"/>.</summary>
public sealed class WorldAuthorityCheckpointCodecLawTests {
    private static readonly ObjectStorageTarget Target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: "UseDevelopmentStorage=true");
    // Capturing boots a server and steps it; the checkpoint is an immutable record, so one capture serves every law.
    private static readonly Lazy<WorldAuthorityCheckpoint> Captured = new(valueFactory: Capture);

    private static WorldAuthorityCheckpoint CapturedCheckpoint() => Captured.Value;
    private static WorldAuthorityCheckpoint Capture() {
        using var fixture = Fixtures.FreshServer();

        Assert.True(
            condition: fixture.Server.Arena.Keys.TryIntern(
                key: out _,
                name: CellName.Parse(candidate: "orphan-checkpoint-key"),
                reason: out var keyReason
            ),
            userMessage: keyReason
        );

        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: Principal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        ));
        _ = fixture.Server.Population.SetSimulatedCount(count: 3);

        for (var tick = 0; (tick < 62); tick++) {
            fixture.Step();
        }

        var dynamicState = fixture.Server.Body(index: 0)!.CaptureTransferState();

        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: SampleHostRow(dynamicState: dynamicState),
                reason: out var reason
            ),
            userMessage: reason
        );

        return checkpoint!;
    }
    // Carries one populated WorldInDoubtTransferCheckpoint (commit members AND landed members both non-empty) so the
    // round-trip laws below actually exercise every leaf the in-doubt shape added, not just its zero-length case.
    private static WorldAuthorityHostRowCheckpoint SampleHostRow(WorldBodyTransferState dynamicState) {
        var commitMember = new WorldTransferCommitMember(
            ActionContinuity: new WorldTransferActionContinuity(
                Channels: [new WorldTransferChannelEdge(
                        Name: "move",
                        PreviousBit: true,
                        HeldValue: FixedQ4816.FromInteger(value: 1)
                    )],
                Registers: []
            ),
            BodyMotionProgramName: "walk",
            Continuum: null,
            HasMappedArrival: false,
            PlanarVelocity: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 1),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            Position: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 2),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.FromInteger(value: 3)
            ),
            Profile: null,
            VerticalVelocity: FixedQ4816.Zero,
            YawRadians: FixedQ4816.Zero
        );
        var landed = new WorldLandedMemberCheckpoint(
            AdmissionGrants: [new WorldAdmissionGrant(
                    Capability: WorldCapability.Drive,
                    Subject: GrantSubject.Body(index: 4)
                )],
            BodyColor: new Vector3(
                x: 1,
                y: 0,
                z: 0
            ),
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
                DepartedFrom: new WorldEntityAddress(
                    Authority: "row-a",
                    Generation: 1,
                    Index: 0
                ),
                Epoch: 1,
                Incarnation: new WorldEntityAddress(
                    Authority: "row-a",
                    Generation: 1,
                    Index: 0
                )
            ),
            Peer: new WorldPeerEventEntry(
                AuthorityTransferred: false,
                BodyIndex: 4,
                CatalogRig: 0,
                Generation: 1,
                Identity: Principal.Peer(
                    generation: 1,
                    index: 4
                ),
                IdentityDomain: "example.test",
                IdentitySubject: "traveler",
                PlacementId: null,
                Source: IntentSource.Live
            ),
            Position: new FixedVector3(
                X: FixedQ4816.FromInteger(value: 5),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            SourceGrants: [new WorldGrant(
                    Grantee: Principal.Peer(
                        generation: 1,
                        index: 4
                    ),
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

    [Fact]
    public void Capture_refuses_an_open_arena_transaction() {
        using var fixture = Fixtures.FreshServer();
        var mark = fixture.Server.Arena.BeginScope();

        try {
            Assert.False(condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out _,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var reason
            ));
            Assert.Contains(
                actualString: reason,
                expectedSubstring: "arena transaction is open"
            );
        } finally {
            fixture.Server.Arena.Rewind(mark: mark);
        }
    }
    [Fact]
    public void RestoreRetainsAnOrphanArenaKeyLedgerAfterRuleRelayout() {
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: CellName.Parse(candidate: "ledger"),
                Kind: CellKind.Int,
                Capacity: 2,
                Domain: StateDomain.Keys.Instance
            )]),
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        Assert.True(condition: fixture.Server.Arena.Catalog.TryResolve(
            handle: out var row,
            lane: StateLane.Document,
            name: CellName.Parse(candidate: "ledger")
        ));
        var mark = fixture.Server.Arena.BeginScope();

        Assert.True(condition: fixture.Server.Arena.TryMint(
            key: out var orphan,
            name: CellName.Parse(candidate: "orphan"),
            reason: out var reason,
            rowOrdinal: row.Ordinal,
            value: CellValue.Int(value: 1L)
        ), reason);
        Assert.True(condition: fixture.Server.Arena.TryRemove(
            key: orphan,
            reason: out reason,
            rowOrdinal: row.Ordinal
        ), reason);
        fixture.Server.Arena.Commit(mark: mark);

        var expectedBytes = fixture.Server.Arena.Bytes;
        var expectedHash = fixture.Server.Arena.ComputeHash();
        var expectedKeyCount = fixture.Server.Arena.Keys.Count;

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: WorldAuthorityHostRowCheckpoint.Empty,
            reason: out reason
        ), reason);

        using var restoredMachines = new WorldMachineHost(
            engines: [],
            screens: definition.Screens
        );
        using var profilesDirectory = new TemporaryDirectory(prefix: "puck-key-ledger-tests-");

        var (restored, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint!,
            instanceIdentity: "restored-ledger",
            machines: restoredMachines,
            profiles: new WorldOwnedWorlds(
                directory: profilesDirectory.RootPath,
                machineId: Guid.NewGuid(),
                template: definition
            )
        );

        Assert.True(condition: restored.Arena.Keys.TryResolve(
            key: out _,
            name: CellName.Parse(candidate: "orphan")
        ));
        Assert.Equal(expectedKeyCount, restored.Arena.Keys.Count);
        Assert.Equal(expectedBytes, restored.Arena.Bytes);
        Assert.Equal(expectedHash, restored.Arena.ComputeHash());

        var next = CellName.Parse(candidate: "next-key");

        Assert.Equal(
            actual: restored.Arena.Keys.TryIntern(key: out _, name: next, reason: out var restoredReason),
            expected: fixture.Server.Arena.Keys.TryIntern(key: out _, name: next, reason: out var originalReason)
        );
        Assert.Equal(actual: restoredReason, expected: originalReason);
        Assert.Equal(fixture.Server.Arena.Bytes, restored.Arena.Bytes);
        Assert.Equal(fixture.Server.Arena.ComputeHash(), restored.Arena.ComputeHash());
    }
    [Fact]
    public void Duplicate_arena_key_ledger_refuses_by_name() {
        var duplicate = CellName.Parse(candidate: "duplicate");
        var captured = CapturedCheckpoint();
        var checkpoint = captured with {
            Server = captured.Server with { ArenaKeys = [duplicate, duplicate] },
        };
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);

        Assert.False(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: encoded,
            checkpoint: out _,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "arena key 'duplicate' is duplicated"
        );
    }
    [Fact]
    public void ArenaKeyLedgerRoundTripsNamesPastTheGeneralWireStringLimit() {
        var captured = CapturedCheckpoint();
        var longName = CellName.Parse(candidate: ("key-" + new string(c: 'x', count: 20_000)));
        var checkpoint = captured with {
            Server = captured.Server with { ArenaKeys = [longName] },
        };
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: encoded,
            checkpoint: out var decoded,
            reason: out var reason
        ), reason);
        Assert.Equal(
            actual: Assert.Single(collection: decoded!.Server.ArenaKeys),
            expected: longName
        );
    }
    [Fact]
    public void ArenaKeyLedgerRoundTripsUnicodeCodeUnitsLosslessly() {
        var captured = CapturedCheckpoint();
        var unicode = CellName.Parse(candidate: "key-漢-\uD800-tail");
        var checkpoint = captured with {
            Server = captured.Server with { ArenaKeys = [unicode] },
        };
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: encoded,
            checkpoint: out var decoded,
            reason: out var reason
        ), reason);
        Assert.Equal(
            actual: Assert.Single(collection: decoded!.Server.ArenaKeys).Value.AsSpan().ToArray(),
            expected: unicode.Value.AsSpan().ToArray()
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
    public async Task Capture_encode_write_load_decode_restore_reaches_an_identical_second_checkpoint() {
        var checkpoint = CapturedCheckpoint();
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);
        var store = new WorldAuthorityBlobStore(
            store: new FakeObjectBlobStore(),
            target: Target
        );
        var identity = new WorldAuthorityIdentity(
            Owner: Guid.NewGuid(),
            World: SafeName.Parse(candidate: "amber")
        );
        var cancellationToken = TestContext.Current.CancellationToken;

        var written = await store.WriteCheckpointAsync(
            cancellationToken: cancellationToken,
            encoded: encoded,
            identity: identity,
            tick: checkpoint.Server.LastCompletedTick
        );

        Assert.True(
            condition: written.Ok,
            userMessage: written.Detail
        );

        var loaded = await store.LoadLatestAsync(
            cancellationToken: cancellationToken,
            identity: identity
        );

        Assert.True(condition: loaded.HasValue);
        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: loaded!.Value.Encoded.Span,
                checkpoint: out var decoded,
                reason: out var reason
            ),
            userMessage: reason
        );

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: decoded!.Server.DefinitionJson);
        using var restoredMachines = new WorldMachineHost(
            engines: [],
            screens: definition.Screens
        );
        using var profilesDirectory = new TemporaryDirectory(prefix: "puck-checkpoint-codec-tests-");

        var (restoredServer, _) = WorldServer.FromCheckpoint(
            checkpoint: decoded,
            instanceIdentity: "boot",
            machines: restoredMachines,
            profiles: new WorldOwnedWorlds(
                directory: profilesDirectory.RootPath,
                machineId: Guid.NewGuid(),
                template: definition
            )
        );

        // The restored body's dynamic state must be bit-identical to the one CapturedCheckpoint() fed SampleHostRow
        // above for the second checkpoint to reach the SAME encoded bytes — re-reading it here (rather than reusing
        // the first call's local) is itself part of what this law proves.
        var restoredDynamicState = restoredServer.Body(index: 0)!.CaptureTransferState();

        Assert.True(
            condition: restoredServer.TryCaptureCheckpoint(
                checkpoint: out var secondCheckpoint,
                hostRow: SampleHostRow(dynamicState: restoredDynamicState),
                reason: out var secondReason
            ),
            userMessage: secondReason
        );

        var secondEncoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: secondCheckpoint!);

        Assert.Equal(
            decoded!.Grants.Revision,
            secondCheckpoint!.Grants.Revision
        );

        Assert.Equal(
            actual: secondEncoded,
            expected: encoded
        );
    }
    [Fact]
    public void Curve_follow_producer_with_nonzero_arc_state_round_trips_structurally() {
        var curveRow = new WorldCurveRow(
            Name: "path",
            Knots: [
                new WorldCurveKnot(
                    Position: new DocumentVector3(
                        x: 0f,
                        y: 0f,
                        z: 0f
                    ),
                    TangentYaw: 0f,
                    Curvature: 0f
                ),
                new WorldCurveKnot(
                    Position: new DocumentVector3(
                        x: 20f,
                        y: 0f,
                        z: 0f
                    ),
                    TangentYaw: 0f,
                    Curvature: 0f
                ),
            ],
            Closed: false
        );
        var document = (Fixtures.BuildDocument() with { CurvesRaw = [curveRow] });
        var kit = document.Kits[0];
        var followProgram = new BodyMotionProgram(
            Name: "follow",
            Version: "puck.body.program.v1",
            Kind: BodyProgramKind.Producer,
            Operations: [BodyMotionOp.SenseNearestInCone, BodyMotionOp.FaceSensorTarget, BodyMotionOp.ProduceSteeringIntent],
            Target: new BodyTargetSource.CurveFollow(
                Curve: "path",
                Rate: 2f
            )
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
                            ["approachAltitudeGain"] = 0f,
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
            Principal: Principal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        ));

        fixture.Server.Body(index: 0)!.SetIntentSource(source: IntentSource.Producer(name: "follow"));

        for (var tick = 0; (tick < 3); tick++) {
            fixture.Step();
        }

        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var reason
            ),
            userMessage: reason
        );

        var entry = checkpoint!.Population.Entries.Single(predicate: static row => (row.Index == 0));

        Assert.True(
            condition: (entry.ProducerCurveArcRaw != 0L),
            userMessage: "the driven curve-follow arc position must be off rest before this law proves anything"
        );

        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);

        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: encoded,
                checkpoint: out var decoded,
                reason: out var decodeReason
            ),
            userMessage: decodeReason
        );
        Assert.True(
            condition: DeepEqual.Compare(
                a: checkpoint,
                b: decoded
            ),
            userMessage: DeepEqual.LastMismatchPath
        );
    }
    // No shipped document declares a rule group yet, so a captured checkpoint's group list is empty and only an
    // authored entry proves the wire form carries a group's cursor, its open flag and its ceiling breach.
    [Fact]
    public void A_rule_groups_progress_round_trips_on_the_wire() {
        var captured = CapturedCheckpoint();
        var checkpoint = captured with {
            Server = captured.Server with {
                RuleGroups = [new WorldRuleGroupEntry(
                        Breached: true,
                        Group: "settle",
                        Running: true,
                        Step: 3
                    ), new WorldRuleGroupEntry(
                        Breached: false,
                        Group: "deal",
                        Running: false,
                        Step: 0
                    )],
            },
        };
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);

        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: encoded,
                checkpoint: out var decoded,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            actual: decoded!.Server.RuleGroups,
            expected: checkpoint.Server.RuleGroups
        );
    }
    [Fact]
    public void Decode_of_Encode_is_structurally_equal() {
        var checkpoint = CapturedCheckpoint();
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);

        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: encoded,
                checkpoint: out var decoded,
                reason: out var reason
            ),
            userMessage: reason
        );
        var equal = DeepEqual.Compare(
            a: checkpoint,
            b: decoded
        );

        Assert.True(
            condition: equal,
            userMessage: DeepEqual.LastMismatchPath
        );
    }
    [Fact]
    public void Dynamics_kit_body_with_nonzero_follower_state_round_trips_structurally() {
        var document = Fixtures.BuildDocument();
        var kit = document.Kits[0];
        var motion = kit.Motion;

        document = document with {
            DynamicsRaw = [.. Fixtures.StandardDynamics, new DynamicsRow(
                Damping: 1f,
                Frequency: 2f,
                Name: "settle",
                Response: 0f
            )],
            KitRowsRaw = [kit with { Motion = motion with { Shaping = [motion.Shaping![0] with { Along = null, Dynamics = "settle" }] } }],
        };

        using var fixture = Fixtures.FreshServer(definition: document);

        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: Principal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        ));

        var body = fixture.Server.Body(index: 0)!;

        for (var tick = 0; (tick < 3); tick++) {
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(
                ordinal: 0,
                value: FixedQ4816.One
            ));
            fixture.Step();
        }

        var dynamicState = body.CaptureTransferState();

        Assert.True(
            condition: ((dynamicState.PlanarFollowerPositionRawX | dynamicState.PlanarFollowerPositionRawY | dynamicState.PlanarFollowerPositionRawZ) != 0L),
            userMessage: "the driven follower must be off rest before this law proves anything"
        );

        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: SampleHostRow(dynamicState: dynamicState),
                reason: out var reason
            ),
            userMessage: reason
        );
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!);

        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: encoded,
                checkpoint: out var decoded,
                reason: out var decodeReason
            ),
            userMessage: decodeReason
        );
        Assert.True(
            condition: DeepEqual.Compare(
                a: checkpoint,
                b: decoded
            ),
            userMessage: DeepEqual.LastMismatchPath
        );
    }
    [Fact]
    public void Encode_of_Decode_is_byte_identical() {
        var checkpoint = CapturedCheckpoint();
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);

        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: encoded,
                checkpoint: out var decoded,
                reason: out var reason
            ),
            userMessage: reason
        );

        var reEncoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: decoded!);

        Assert.Equal(
            actual: reEncoded,
            expected: encoded
        );
    }
    [Fact]
    public void Field_section_round_trips_structurally() {
        var checkpoint = CapturedCheckpoint() with {
            Fields = new FieldLattice.Checkpoint(Raw: [
                [FixedQ4816.Zero.Value, FixedQ4816.FromInteger(value: 1).Value],
                [FixedQ4816.FromInteger(value: -2).Value, FixedQ4816.FromInteger(value: 3).Value],
            ]),
        };
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);

        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: encoded,
                checkpoint: out var decoded,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.True(
            condition: DeepEqual.Compare(
                a: checkpoint.Fields,
                b: decoded!.Fields
            ),
            userMessage: DeepEqual.LastMismatchPath
        );
    }
    [Fact]
    public void MachineControlGrantSurvivesCheckpointEncodeDecodeAndRestore() {
        using var source = Fixtures.FreshServer();
        var tool = Principal.Addon(name: "cabinet-tool");

        source.Server.Grant(
            grant: new WorldGrant(
                tool,
                WorldCapability.Control,
                GrantSubject.Machine(name: "cabinet"),
                Exclusive: false
            ),
            actor: Principal.Console
        );
        Assert.True(condition: source.Server.Grants.Allows(
            tool,
            WorldCapability.Control,
            GrantSubject.Machine(name: "cabinet")
        ).IsAllowed);

        Assert.True(
            condition: source.Server.TryCaptureCheckpoint(
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                checkpoint: out var captured,
                reason: out var captureReason
            ),
            userMessage: captureReason
        );
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: captured!);

        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: encoded,
                checkpoint: out var decoded,
                reason: out var decodeReason
            ),
            userMessage: decodeReason
        );

        using var restored = Fixtures.FreshServer();

        restored.Server.RestoreCheckpoint(checkpoint: decoded!);

        Assert.True(condition: restored.Server.Grants.Allows(
            tool,
            WorldCapability.Control,
            GrantSubject.Machine(name: "cabinet")
        ).IsAllowed);
        Assert.False(condition: restored.Server.Grants.Allows(
            tool,
            WorldCapability.Control,
            GrantSubject.Machine(name: "other")
        ).IsAllowed);
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
    // Every older layout lacks a section the current reader requires, and a newer one is unknown to it; the envelope
    // refuses both by version before any payload is read with the wrong layout.
    public static TheoryData<ushort> EveryUnsupportedVersion() => new(values: [
        .. Enumerable.Range(
            count: WorldAuthorityCheckpointCodec.SupportedVersion,
            start: 0
        ).Select(selector: static version => ((ushort)version)),
        ((ushort)(WorldAuthorityCheckpointCodec.SupportedVersion + 1)),
        ushort.MaxValue,
    ]);
    [MemberData(memberName: nameof(EveryUnsupportedVersion))]
    [Theory]
    public void An_unsupported_version_envelope_refuses_by_name(ushort version) {
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: CapturedCheckpoint());

        // The version u16 sits immediately after the 4-byte "PCKP" magic.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            destination: encoded.AsSpan(start: 4),
            value: version
        );

        Assert.False(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: encoded,
            checkpoint: out _,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: $"checkpoint version {version} is not the supported version"
        );
    }
}
