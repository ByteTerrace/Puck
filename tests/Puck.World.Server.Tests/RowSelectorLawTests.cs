using Puck.Commands;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.Testing;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Laws for an assignment's per-body selector (<see cref="WorldRowAssignment.Select"/>): each body's own
/// <c>state.body</c> register picks its kit and look. A rule's <c>setBodyState</c> moves the choice, the kit recompile it
/// causes keeps every register, a value no case names falls back to the sequence, a checkpoint restores the choice with
/// the register, a <c>world.assign</c> keeps the authored selector, a crossing carries the register so the destination
/// selects by it before the body first advances there, and the validator and rule compiler refuse what the document does
/// not declare, by name.</summary>
public sealed class RowSelectorLawTests {
    private const string SlowKit = "slow";
    private const string SuitedLook = "suited";
    private const string PilotLook = "pilot";
    private const string Suit = "suit";
    private const string Fuel = "fuel";

    private static WorldRowSelector Selector(string first, string second) => new(
        BodyState: Suit,
        Cases: [
            new WorldRowSelectorCase(
                Value: 1L,
                Row: first
            ),
            new WorldRowSelectorCase(
                Value: 0L,
                Row: second
            ),
        ]
    );
    // A rule that fires once, on the first tick a body is seated, and writes body 0's registers.
    private static WorldRule Once(string name, params ActionEffect[] effects) => new(
        Name: CellName.Parse(candidate: name),
        Effects: effects,
        Gate: new ActionPredicate.CompareState(
            State: WorldRuleFacts.Population,
            Comparison: ExpressionOp.GreaterOrEqual,
            Value: 1m
        ),
        Mode: ActionTriggerMode.Edge
    );
    private static WorldEffect.SetBodyState Write(string state, decimal value, string key = "0") => new(
        Key: key,
        State: state,
        Value: value
    );
    // The fixture document with a second, slower kit, two catalog looks, a suit register born at 1 and a fuel register
    // born at 0, and both assignments selecting by the suit: 1 is the seat kit and the suited look, 0 the slow kit and
    // the pilot look.
    private static WorldDefinition Document(WorldRule[]? rules = null, WorldRowSelector? kitSelect = null, WorldRowSelector? lookSelect = null, ActionStateKind suitKind = ActionStateKind.Counter) {
        var source = Fixtures.BuildDocument();
        var seatKit = source.Kits[0];

        return source with {
            KitsRaw = new WorldKitsSection(
                Assignment: (source.Assignment with { Select = (kitSelect ?? Selector(first: seatKit.Name, second: SlowKit)) }),
                Rows: [
                    seatKit,
                    (seatKit with {
                        Name = SlowKit,
                        Motion = (seatKit.Motion with { Speed = new WorldSpeed(Value: 1f) }),
                    }),
                ]
            ),
            LooksRaw = new WorldLooksSection(
                Assignment: (source.LookAssignment with { Select = (lookSelect ?? Selector(first: SuitedLook, second: PilotLook)) }),
                Rows: [
                    new WorldLook(
                        Name: SuitedLook,
                        Source: new WorldLookSource.Catalog(Index: 1),
                        Scale: 1f,
                        Motion: WorldLookMotion.Default
                    ),
                    new WorldLook(
                        Name: PilotLook,
                        Source: new WorldLookSource.Catalog(Index: 2),
                        Scale: 1f,
                        Motion: WorldLookMotion.Default
                    ),
                ]
            ),
            Rules = (rules ?? []),
            StateRaw = ((source.StateRaw ?? new WorldStateSection()) with {
                Body = [
                    new ActionStateSlot(
                        Name: Suit,
                        Kind: suitKind,
                        Initial: 1f
                    ),
                    new ActionStateSlot(
                        Name: Fuel,
                        Kind: ActionStateKind.Counter,
                        Initial: 0f
                    ),
                ],
            }),
        };
    }
    private static FixedQ4816 Register(WorldServer server, string name, int body = 0) {
        Assert.True(condition: WorldBodyStateReads.TryResolveSlot(
            definition: server.Definition,
            kind: out _,
            name: name,
            slot: out var slot
        ));

        return server.Population.ActionStateLane.Counter(
            ordinal: body,
            slot: slot
        );
    }
    private static string LookName(WorldServer server) => server.Population.LookRows[server.Population.LookIndex(index: 0)].Name;
    private static string KitName(WorldServer server) => server.Definition.Kits[server.Population.KitIndex(index: 0)].Name;
    private static string Refusal(WorldDefinition definition) {
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        ));

        return reason;
    }

    [Fact]
    public void TheBornRegisterSelectsTheBodysKitAndLook() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = fixture.JoinSeat();

        fixture.Step();

        Assert.Equal(
            actual: LookName(server: fixture.Server),
            expected: SuitedLook
        );
        Assert.Equal(
            actual: fixture.Server.Population.KitMotion(index: 0).Speed.Value,
            expected: 4f
        );
        Assert.Same(
            actual: fixture.Server.Body(index: 0),
            expected: body
        );
    }
    // The body keeps its identity across the swap: only its compiled kit changes, and the revision the client rebuilds
    // its program on moves so the client re-latches the look. The rule fires once, so a recompile that re-birthed the
    // registers would read the suit back at 1 and the fuel at 0 on the ticks after it.
    [Fact]
    public void ARuleWriteMovesKitAndLookAndTheRecompileKeepsEveryRegister() {
        using var fixture = Fixtures.FreshServer(definition: Document(rules: [Once(
            effects: [Write(state: Suit, value: 0m), Write(state: Fuel, value: 5m)],
            name: "unsuit"
        )]));
        var body = fixture.JoinSeat();
        var revision = fixture.Server.Population.Revision;

        fixture.Step();
        fixture.Step();
        fixture.Step();

        Assert.Equal(
            actual: Register(server: fixture.Server, name: Suit),
            expected: FixedQ4816.Zero
        );
        Assert.Equal(
            actual: Register(server: fixture.Server, name: Fuel),
            expected: FixedQ4816.FromInteger(value: 5L)
        );
        Assert.Equal(
            actual: LookName(server: fixture.Server),
            expected: PilotLook
        );
        Assert.Equal(
            actual: KitName(server: fixture.Server),
            expected: SlowKit
        );
        Assert.Equal(
            actual: fixture.Server.Population.KitMotion(index: 0).Speed.Value,
            expected: 1f
        );
        Assert.Same(
            actual: fixture.Server.Body(index: 0),
            expected: body
        );
        Assert.NotEqual(
            actual: fixture.Server.Population.Revision,
            expected: revision
        );
    }
    [Fact]
    public void AValueNoCaseNamesFallsBackToTheSequence() {
        var unsuit = Once(
            effects: [Write(state: Suit, value: 7m)],
            name: "seven"
        );
        var selected = Document(rules: [unsuit]);
        using var unselected = Fixtures.FreshServer(definition: (selected with {
            LooksRaw = (selected.LooksRaw! with { Assignment = selected.LookAssignment with { Select = null } }),
            KitsRaw = (selected.KitsRaw! with { Assignment = selected.Assignment with { Select = null } }),
        }));
        using var fixture = Fixtures.FreshServer(definition: selected);

        unselected.JoinSeat();
        fixture.JoinSeat();
        fixture.Step();
        unselected.Step();

        Assert.Equal(
            actual: fixture.Server.Population.LookIndex(index: 0),
            expected: unselected.Server.Population.LookIndex(index: 0)
        );
        Assert.Equal(
            actual: fixture.Server.Population.KitMotion(index: 0).Speed.Value,
            expected: 4f
        );
    }
    // A checkpoint carries the register, not the selection, so the restored body takes its kit and look from it.
    [Fact]
    public void ACheckpointRestoresTheSelectionWithTheRegister() {
        using var fixture = Fixtures.FreshServer(definition: Document(rules: [Once(
            effects: [Write(state: Suit, value: 0m)],
            name: "unsuit"
        )]));

        fixture.JoinSeat();
        fixture.Step();

        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var refusal
            ),
            userMessage: refusal
        );

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson);
        using var restoredMachines = new WorldMachineHost(
            engines: [],
            screens: restoredDefinition.Screens
        );
        using var profilesDirectory = new TemporaryDirectory(prefix: "puck-row-selector-tests-");

        var (restoredServer, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "boot",
            machines: restoredMachines,
            profiles: new WorldOwnedWorlds(directory: profilesDirectory.RootPath, machineId: Guid.NewGuid(), template: restoredDefinition)
        );

        Assert.Equal(
            actual: LookName(server: restoredServer),
            expected: PilotLook
        );
        Assert.Equal(
            actual: restoredServer.Population.KitMotion(index: 0).Speed.Value,
            expected: 1f
        );
    }
    [Fact]
    public void WorldAssignKeepsTheAuthoredSelector() {
        using var fixture = Fixtures.FreshServer(definition: Document(rules: [Once(
            effects: [Write(state: Suit, value: 0m)],
            name: "unsuit"
        )]));

        fixture.JoinSeat();
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetLookAssignment(
            Assignment: new WorldRowAssignment(
                Sequence: WorldSequence.IndexDefault,
                Rows: [SuitedLook]
            ),
            Principal: Principal.Console
        ));
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetKitAssignment(
            Assignment: new WorldRowAssignment(
                Sequence: WorldSequence.IndexDefault,
                Rows: [Fixtures.SeatKitName]
            ),
            Principal: Principal.Console
        ));
        fixture.Step();

        Assert.NotNull(@object: fixture.Server.Definition.LookAssignment.Select);
        Assert.NotNull(@object: fixture.Server.Definition.Assignment.Select);
        Assert.Equal(
            actual: KitName(server: fixture.Server),
            expected: SlowKit
        );
        Assert.Equal(
            actual: LookName(server: fixture.Server),
            expected: PilotLook
        );
    }
    // THE LAW: the form travels with the body. An anonymous traveller lands with the suit register it carried, and the
    // destination, whose own register would be born at 1, selects its kit and look by the carried value before the
    // body first advances there; the next tick keeps it. The control carries 1 and lands suited.
    [InlineData(0L, PilotLook, 1f)]
    [InlineData(1L, SuitedLook, 4f)]
    [Theory]
    public void ACrossingCarriesTheRegisterAndTheDestinationSelectsByIt(long carried, string look, float speed) {
        using var fixture = Fixtures.FreshServer(definition: Document());
        const string SourceAuthority = "origin/world";
        var address = new WorldEntityAddress(
            Authority: SourceAuthority,
            Generation: 7,
            Index: 0
        );
        var request = new WorldTransferReservationRequest(
            TransferId: 1,
            SourceAuthority: SourceAuthority,
            SourceRateHz: 30,
            SourceTick: 0,
            DeadlineSourceTick: 60,
            Border: "painting",
            BorderCapacity: null,
            PartyAllOrNothing: true,
            PeerAdmission: false,
            Members: [new WorldTransferReservationMember(
                Principal: Principal.Console,
                PreferredSlot: 0,
                Identity: null,
                Source: default,
                BodyColor: default,
                CatalogRig: 0,
                Mobility: new WorldMobilityIdentity(
                    DepartedFrom: address,
                    Epoch: 0,
                    Incarnation: address
                )
            )]
        );
        var reservation = fixture.Server.ReserveTransfer(request: request);

        Assert.True(
            condition: reservation.Accepted,
            userMessage: reservation.Reason
        );
        Assert.True(
            condition: (fixture.Server.CommitTransfer(
                members: [new WorldTransferCommitMember(
                    Profile: null,
                    HasMappedArrival: true,
                    BodyMotionProgramName: "grounded",
                    Position: default,
                    YawRadians: default,
                    PlanarVelocity: default,
                    VerticalVelocity: default,
                    ActionContinuity: new WorldTransferActionContinuity(
                        Channels: [],
                        Registers: [new WorldTransferActionRegister(
                            Kind: ActionStateKind.Counter,
                            Name: Suit,
                            TimerTicks: 0UL,
                            Value: FixedQ4816.FromInteger(value: carried)
                        )]
                    )
                )],
                reason: out var reason,
                sourceAuthority: SourceAuthority,
                transferId: request.TransferId
            ) == WorldTransferStatus.Committed),
            userMessage: reason
        );

        var slot = Assert.Single(collection: reservation.BodyIndices);

        Assert.Null(@object: fixture.Server.Body(index: slot)!.Profile);

        for (var step = 0; (step < 2); step++) {
            Assert.Equal(
                actual: Register(body: slot, name: Suit, server: fixture.Server),
                expected: FixedQ4816.FromInteger(value: carried)
            );
            Assert.Equal(
                actual: fixture.Server.Population.LookRows[fixture.Server.Population.LookIndex(index: slot)].Name,
                expected: look
            );
            Assert.Equal(
                actual: fixture.Server.Population.KitMotion(index: slot).Speed.Value,
                expected: speed
            );

            fixture.Step();
        }
    }
    [Fact]
    public void ARuleWriteToAnInactiveBodyOrAFaultedValueIsRefusedByName() {
        using var inactive = Fixtures.FreshServer(definition: Document(rules: [Once(
            effects: [Write(key: "3", state: Suit, value: 0m)],
            name: "absent"
        )]));
        using var faulted = Fixtures.FreshServer(definition: Document(rules: [Once(
            effects: [new WorldEffect.SetBodyState(
                Expression: ExpressionProgram.Parse(text: "1 / 0"),
                Key: "0",
                State: Suit
            )],
            name: "faulted"
        )]));

        inactive.JoinSeat();
        faulted.JoinSeat();
        inactive.Step();
        faulted.Step();

        Assert.Equal(
            actual: Assert.Single(collection: inactive.Server.RuleRuntimeDiagnostics()).Refusal,
            expected: WorldRuleEffectRefusal.BodyInactive
        );
        Assert.Equal(
            actual: Assert.Single(collection: faulted.Server.RuleRuntimeDiagnostics()).Refusal,
            expected: WorldRuleEffectRefusal.BodyStateFaulted
        );
        Assert.Equal(
            actual: Register(server: faulted.Server, name: Suit),
            expected: FixedQ4816.One
        );
    }
    [Fact]
    public void TheValidatorRefusesAnUndeclaredRowOrRegisterByName() {
        Assert.Contains(
            expectedSubstring: "looks.assignment.select.cases[1].row",
            actualString: Refusal(definition: Document(lookSelect: Selector(first: SuitedLook, second: "moth-pilot")))
        );
        Assert.Contains(
            expectedSubstring: "kits.assignment.select.cases[1].row",
            actualString: Refusal(definition: Document(kitSelect: Selector(first: Fixtures.SeatKitName, second: "pilot")))
        );
        Assert.Contains(
            expectedSubstring: "names register 'cape', which state.body does not declare",
            actualString: Refusal(definition: Document(lookSelect: (Selector(first: SuitedLook, second: PilotLook) with { BodyState = "cape" })))
        );
        Assert.Contains(
            expectedSubstring: "names register 'suit', a Timer; a selector reads a Counter",
            actualString: Refusal(definition: Document(suitKind: ActionStateKind.Timer))
        );
        Assert.Contains(
            expectedSubstring: "'setBodyState' state 'cape' names no Counter state.body declares",
            actualString: Refusal(definition: Document(rules: [Once(
                effects: [Write(state: "cape", value: 0m)],
                name: "cape"
            )]))
        );
        Assert.Contains(
            expectedSubstring: "'setBodyState' must name exactly one of 'value' or 'expression'",
            actualString: Refusal(definition: Document(rules: [Once(
                effects: [new WorldEffect.SetBodyState(Key: "0", State: Suit)],
                name: "neither"
            )]))
        );
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(rules: [Once(
                effects: [new WorldEffect.SetBodyState(
                    Expression: ExpressionProgram.Parse(text: $"1 - {WorldRuleFacts.BodyStatePrefix}body:0:{Suit}"),
                    Key: "0",
                    State: Suit
                )],
                name: "toggle"
            )]),
            reason: out var reason
        ), userMessage: reason);
    }
    [Fact]
    public void AKitActionRefusesSetBodyStateByName() {
        var document = Document();
        var seatKit = document.Kits[0];
        var kit = seatKit with {
            ActionsRaw = new Dictionary<string, ActionSpec>(comparer: StringComparer.Ordinal) {
                [Suit] = new ActionSpec(OnPress: new ActionTrigger(Effects: [Write(state: Suit, value: 0m)])),
            },
        };

        Assert.Contains(
            expectedSubstring: "'setBodyState' writes a body the world names; a kit's action writes its own register with 'setState'",
            actualString: Refusal(definition: (document with {
                ChannelsRaw = [.. document.Channels, new WorldChannel(
                    Name: Suit,
                    Shape: ChannelShape.Binary,
                    Composition: true
                )],
                KitsRaw = (document.KitsRaw! with { Rows = [kit, .. document.Kits.Skip(count: 1)] }),
            }))
        );
    }
    [Fact]
    public void TheValidatorRefusesAMisshapenSelector() {
        var repeated = new WorldRowSelector(
            BodyState: Suit,
            Cases: [
                new WorldRowSelectorCase(Value: 1L, Row: SuitedLook),
                new WorldRowSelectorCase(Value: 1L, Row: PilotLook),
            ]
        );

        Assert.Contains(
            expectedSubstring: "looks.assignment.select.cases[1] repeats value 1",
            actualString: Refusal(definition: Document(lookSelect: repeated))
        );
        Assert.Contains(
            expectedSubstring: "looks.assignment.select.bodyState is required",
            actualString: Refusal(definition: Document(lookSelect: (Selector(first: SuitedLook, second: PilotLook) with { BodyState = "" })))
        );
        Assert.Contains(
            expectedSubstring: "looks.assignment.select.cases[0] is required",
            actualString: Refusal(definition: Document(lookSelect: new WorldRowSelector(BodyState: Suit, Cases: [null!])))
        );
        Assert.Contains(
            expectedSubstring: "looks.assignment.select.cases must name at least one value",
            actualString: Refusal(definition: Document(lookSelect: new WorldRowSelector(BodyState: Suit, Cases: [])))
        );
    }
}
