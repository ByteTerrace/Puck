using System.Numerics;

using Xunit;

using Puck.Maths;
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
