using Xunit;

using Puck.Maths;
using Puck.Physics.Motion;
using Puck.Testing;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a body's named action state is stored in the arena's slot lanes — a <c>state.body</c>
/// declaration on the participant lane, a <c>state.identity</c> one on the identity lane, both addressed by the
/// body's own entity index. The body holds the address, not the values, so the lane is what a checkpoint carries
/// and what a federation arrival loads into.
/// </summary>
public sealed class BodyActionStateLaneLawTests {
    private const string AmmoSlot = "ammo";
    private const string CooldownSlot = "cooldown";
    private const string RangedSlot = "ranged";
    private const string SettedSlot = "setted";

    private static readonly FixedQ4816 AuthoredAmmo = FixedQ4816.FromInteger(value: 3);
    private static readonly FixedQ4816 CarriedAmmo = FixedQ4816.FromDouble(value: 7.5);
    private static readonly ulong AuthoredCooldownTicks = FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.FromInteger(value: 2));

    [Fact]
    public void AJoinedBodyIsBornInBothSlotLanesAndReadsItsNamedStateBackThroughThem() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = fixture.JoinSeat();
        var arena = fixture.Server.Arena;

        Assert.True(
            condition: arena.IsJoined(
                lane: StateLane.Participant,
                ordinal: 0
            ),
            userMessage: "an embodied seat must hold its participant-lane ordinal"
        );
        Assert.True(
            condition: arena.IsJoined(
                lane: StateLane.Identity,
                ordinal: 0
            ),
            userMessage: "an embodied seat must hold its identity-lane ordinal"
        );
        Assert.Equal(
            expected: AuthoredAmmo.Value,
            actual: LaneValue(
                lane: StateLane.Participant,
                name: AmmoSlot,
                ordinal: 0,
                server: fixture.Server
            )
        );
        Assert.Equal(
            expected: unchecked((long)AuthoredCooldownTicks),
            actual: LaneValue(
                lane: StateLane.Identity,
                name: CooldownSlot,
                ordinal: 0,
                server: fixture.Server
            )
        );

        // The lane is the storage, not a copy of it: a write straight into the arena is what the body's own
        // read-back answers, which no private per-body register file could do.
        Assert.True(
            condition: arena.TryWriteSlot(
                operand: CarriedAmmo.Value,
                ordinal: 0,
                reason: out var writeReason,
                rowOrdinal: RowOrdinal(
                    lane: StateLane.Participant,
                    name: AmmoSlot,
                    server: fixture.Server
                ),
                write: StateWriteKind.Set
            ),
            userMessage: writeReason
        );
        Assert.True(condition: body.TryDescribeActionState(
            kind: out _,
            lifetime: out _,
            name: AmmoSlot,
            playerWritable: out _,
            timerTicks: out _,
            value: out var described
        ));
        Assert.Equal(
            actual: described,
            expected: CarriedAmmo
        );

        // Control: an entity index carrying no body holds no lane ordinal, so the roster discriminates occupancy
        // rather than reading joined everywhere.
        var vacant = VacantIndex(server: fixture.Server);

        Assert.False(condition: arena.IsJoined(
            lane: StateLane.Participant,
            ordinal: vacant
        ));
        Assert.False(condition: arena.TryReadSlot(
            ordinal: vacant,
            rowOrdinal: RowOrdinal(
                lane: StateLane.Participant,
                name: AmmoSlot,
                server: fixture.Server
            ),
            value: out _
        ));
    }
    [Fact]
    public void ACheckpointRoundTripRestoresBothSlotLanesExactly() {
        using var fixture = Fixtures.FreshServer(definition: Document());

        _ = fixture.JoinSeat();
        WriteLane(
            lane: StateLane.Participant,
            name: AmmoSlot,
            ordinal: 0,
            raw: CarriedAmmo.Value,
            server: fixture.Server
        );
        WriteLane(
            lane: StateLane.Identity,
            name: CooldownSlot,
            ordinal: 0,
            raw: 41L,
            server: fixture.Server
        );

        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var refusal
            ),
            userMessage: refusal
        );
        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!),
                checkpoint: out var decoded,
                reason: out var decodeReason
            ),
            userMessage: decodeReason
        );

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: decoded!.Server.DefinitionJson);
        using var restoredMachines = new WorldMachineHost(
            engines: [],
            screens: restoredDefinition.Screens
        );
        using var profilesDirectory = new TemporaryDirectory(prefix: "puck-action-state-lane-");

        var (restored, _) = WorldServer.FromCheckpoint(
            checkpoint: decoded,
            instanceIdentity: "boot",
            machines: restoredMachines,
            profiles: new WorldOwnedWorlds(directory: profilesDirectory.RootPath, machineId: Guid.NewGuid(), template: restoredDefinition)
        );

        Assert.Equal(
            expected: CarriedAmmo.Value,
            actual: LaneValue(
                lane: StateLane.Participant,
                name: AmmoSlot,
                ordinal: 0,
                server: restored
            )
        );
        Assert.Equal(
            expected: 41L,
            actual: LaneValue(
                lane: StateLane.Identity,
                name: CooldownSlot,
                ordinal: 0,
                server: restored
            )
        );
        Assert.True(condition: restored.Body(index: 0)!.TryDescribeActionState(
            kind: out _,
            lifetime: out _,
            name: AmmoSlot,
            playerWritable: out _,
            timerTicks: out _,
            value: out var described
        ));
        Assert.Equal(
            actual: described,
            expected: CarriedAmmo
        );

        // Control: the same document booted fresh reads the authored initial, so the restore is carrying the
        // captured lane rather than reproducing a birth.
        using var reference = Fixtures.FreshServer(definition: Document());

        _ = reference.JoinSeat();
        Assert.Equal(
            expected: AuthoredAmmo.Value,
            actual: LaneValue(
                lane: StateLane.Participant,
                name: AmmoSlot,
                ordinal: 0,
                server: reference.Server
            )
        );
    }
    [Fact]
    public void AFederationArrivalLoadsItsCarriedRegistersIntoTheLaneRatherThanRebirthingThem() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = fixture.JoinSeat();

        Assert.True(condition: fixture.Server.Population.ApplyMappedArrival(
            slot: 0,
            motionProgramName: "grounded",
            position: body.FixedPosition,
            yawRadians: body.FixedYaw,
            planarVelocity: default,
            verticalVelocity: FixedQ4816.Zero,
            destinationCompletedEngineTick: fixture.Server.CompletedEngineTicks,
            actionContinuity: new WorldTransferActionContinuity(
                Channels: [],
                Registers: [new WorldTransferActionRegister(
                        Kind: ActionStateKind.Counter,
                        Name: AmmoSlot,
                        TimerTicks: 0UL,
                        Value: CarriedAmmo
                    )]
            )
        ));

        Assert.Equal(
            expected: CarriedAmmo.Value,
            actual: LaneValue(
                lane: StateLane.Participant,
                name: AmmoSlot,
                ordinal: 0,
                server: fixture.Server
            )
        );
        Assert.NotEqual(
            actual: LaneValue(
                lane: StateLane.Participant,
                name: AmmoSlot,
                ordinal: 0,
                server: fixture.Server
            ),
            expected: AuthoredAmmo.Value
        );
        // A register the arrival does not name keeps what this authority's own birth put there — the arrival is a
        // load of what it carries, never a second birth over everything else.
        Assert.Equal(
            expected: unchecked((long)AuthoredCooldownTicks),
            actual: LaneValue(
                lane: StateLane.Identity,
                name: CooldownSlot,
                ordinal: 0,
                server: fixture.Server
            )
        );
    }
    [Fact]
    public void AnArrivalCarryingPastARangeEnvelopeClampsToThatRange() {
        using var fixture = Fixtures.FreshServer(definition: EnvelopeDocument());

        _ = fixture.JoinSeat();
        Arrive(
            carried: FixedQ4816.FromInteger(value: 99),
            fixture: fixture,
            name: RangedSlot
        );

        // The range's own ceiling, not the carried 99 and not the authored initial of 1.
        Assert.Equal(
            expected: FixedQ4816.FromInteger(value: 5).Value,
            actual: LaneValue(
                lane: StateLane.Identity,
                name: RangedSlot,
                ordinal: 0,
                server: fixture.Server
            )
        );
    }
    [Fact]
    public void AnArrivalCarryingOutsideAClosedSetSettlesToTheAuthoredInitial() {
        using var fixture = Fixtures.FreshServer(definition: EnvelopeDocument());

        _ = fixture.JoinSeat();
        // The destination holds a value of its own, admitted by the set, so settling to the authored initial is
        // discriminable from keeping what this authority already held.
        WriteLane(
            lane: StateLane.Identity,
            name: SettedSlot,
            ordinal: 0,
            raw: FixedQ4816.FromInteger(value: 2).Value,
            server: fixture.Server
        );
        Arrive(
            carried: FixedQ4816.FromDouble(value: 1.5),
            fixture: fixture,
            name: SettedSlot
        );

        var settled = LaneValue(
            lane: StateLane.Identity,
            name: SettedSlot,
            ordinal: 0,
            server: fixture.Server
        );

        Assert.Equal(
            expected: FixedQ4816.One.Value,
            actual: settled
        );
        Assert.NotEqual(
            actual: settled,
            expected: FixedQ4816.FromInteger(value: 2).Value
        );
        Assert.NotEqual(
            actual: settled,
            expected: FixedQ4816.FromDouble(value: 1.5).Value
        );
    }
    [Fact]
    public void TheLaneRosterIsTheEntityTablesOwnActiveSetAcrossEveryTransition() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var population = fixture.Server.Population;

        AssertRosterMatchesTheTable(fixture: fixture);

        _ = fixture.JoinSeat();
        AssertRosterMatchesTheTable(fixture: fixture);

        // A second activation of an occupied slot is idempotent: it neither re-joins nor re-births, so a value
        // written after the first one survives it.
        WriteLane(
            lane: StateLane.Participant,
            name: AmmoSlot,
            ordinal: 0,
            raw: CarriedAmmo.Value,
            server: fixture.Server
        );
        population.ActivateSeat(
            profile: null,
            slot: 0
        );
        AssertRosterMatchesTheTable(fixture: fixture);
        Assert.Equal(
            expected: CarriedAmmo.Value,
            actual: LaneValue(
                lane: StateLane.Participant,
                name: AmmoSlot,
                ordinal: 0,
                server: fixture.Server
            )
        );

        // Releasing the slot clears what its ordinal answered, and the next occupant is born rather than
        // inheriting it.
        Assert.True(condition: population.TryDetachSeatForTransfer(
            profile: out _,
            slot: 0
        ));
        AssertRosterMatchesTheTable(fixture: fixture);
        Assert.False(condition: fixture.Server.Arena.IsJoined(
            lane: StateLane.Participant,
            ordinal: 0
        ));

        population.ActivateSeat(
            profile: null,
            slot: 0
        );
        AssertRosterMatchesTheTable(fixture: fixture);
        Assert.Equal(
            expected: AuthoredAmmo.Value,
            actual: LaneValue(
                lane: StateLane.Participant,
                name: AmmoSlot,
                ordinal: 0,
                server: fixture.Server
            )
        );
    }

    private static void AssertRosterMatchesTheTable(WorldFixture fixture) {
        var arena = fixture.Server.Arena;
        var population = fixture.Server.Population;

        for (var index = 0; (index < population.Capacity); index++) {
            var active = population.IsActive(index: index);

            Assert.True(
                condition: (arena.IsJoined(
                lane: StateLane.Participant,
                ordinal: index
            ) == active),
                userMessage: $"participant lane ordinal {index} disagrees with the entity table's active bit ({active})"
            );
            Assert.True(
                condition: (arena.IsJoined(
                lane: StateLane.Identity,
                ordinal: index
            ) == active),
                userMessage: $"identity lane ordinal {index} disagrees with the entity table's active bit ({active})"
            );
        }
    }
    private static void Arrive(WorldFixture fixture, string name, FixedQ4816 carried) => Assert.True(condition: fixture.Server.Population.ApplyMappedArrival(
        slot: 0,
        motionProgramName: "grounded",
        position: fixture.Server.Body(index: 0)!.FixedPosition,
        yawRadians: fixture.Server.Body(index: 0)!.FixedYaw,
        planarVelocity: default,
        verticalVelocity: FixedQ4816.Zero,
        destinationCompletedEngineTick: fixture.Server.CompletedEngineTicks,
        actionContinuity: new WorldTransferActionContinuity(
            Channels: [],
            Registers: [new WorldTransferActionRegister(
                    Kind: ActionStateKind.Counter,
                    Name: name,
                    TimerTicks: 0UL,
                    Value: carried
                )]
        )
    ));
    // An envelope is admitted only on a player-writable slot, and player-writable requires a durable lifetime, so
    // both envelope registers are identity-lane ones.
    private static WorldDefinition EnvelopeDocument() {
        var baseDocument = Fixtures.BuildDocument();

        return (baseDocument with {
            StateRaw = new WorldStateSection(
                Identity: [
                    new ActionStateSlot(
                        Name: RangedSlot,
                        Kind: ActionStateKind.Counter,
                        Initial: 1f,
                        PlayerWritable: true,
                        Envelope: new ActionStateEnvelope.Range(
                            Maximum: 5f,
                            Minimum: 0f
                        )
                    ),
                    new ActionStateSlot(
                        Name: SettedSlot,
                        Kind: ActionStateKind.Counter,
                        Initial: 1f,
                        PlayerWritable: true,
                        Envelope: new ActionStateEnvelope.Set(Values: [
                                0f,
                                1f,
                                2f,
                            ])
                    ),
                ],
                World: baseDocument.State
            ),
        });
    }
    private static WorldDefinition Document() {
        var baseDocument = Fixtures.BuildDocument();

        return (baseDocument with {
            StateRaw = new WorldStateSection(
                Body: [new ActionStateSlot(
                        Name: AmmoSlot,
                        Kind: ActionStateKind.Counter,
                        Initial: 3f
                    )],
                Identity: [new ActionStateSlot(
                        Name: CooldownSlot,
                        Kind: ActionStateKind.Timer,
                        Initial: 2f
                    )],
                World: baseDocument.State
            ),
        });
    }
    private static long LaneValue(WorldServer server, StateLane lane, string name, int ordinal) {
        Assert.True(condition: server.Arena.TryReadSlot(
            ordinal: ordinal,
            rowOrdinal: RowOrdinal(
                lane: lane,
                name: name,
                server: server
            ),
            value: out var value
        ));

        return ((value.Kind == CellKind.Fixed)
            ? value.AsFixed
            : value.AsInt
        );
    }
    private static int RowOrdinal(WorldServer server, StateLane lane, string name) {
        Assert.True(
            condition: server.Arena.Catalog.TryResolve(
                handle: out var handle,
                lane: lane,
                name: name
            ),
            userMessage: $"the catalog declares no '{name}' on the {lane} lane"
        );

        return handle.Ordinal;
    }
    private static int VacantIndex(WorldServer server) {
        for (var index = 0; (index < server.Population.Capacity); index++) {
            if (server.Body(index: index) is null) {
                return index;
            }
        }

        Assert.Fail(message: "the fixture world embodies every entity index, so the roster control cannot discriminate");

        return -1;
    }
    private static void WriteLane(WorldServer server, StateLane lane, string name, int ordinal, long raw) => Assert.True(condition: server.Arena.TryWriteSlot(
        operand: raw,
        ordinal: ordinal,
        reason: out _,
        rowOrdinal: RowOrdinal(
            lane: lane,
            name: name,
            server: server
        ),
        write: StateWriteKind.Set
    ));
}
