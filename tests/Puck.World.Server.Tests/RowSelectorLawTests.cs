using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Laws for an assignment's per-body selector (<see cref="WorldRowAssignment.Select"/>): each body's own cell of
/// a keyed Int row picks its kit and look, the choice follows the cell through either door a value reaches the
/// document by, a value no case names falls back to the sequence, a checkpoint restores the choice with the cell, a
/// <c>world.assign</c> keeps the authored selector, and the validator refuses a selector naming what the document does
/// not declare, by name.</summary>
public sealed class RowSelectorLawTests {
    private const string SlowKit = "slow";
    private const string SuitedLook = "suited";
    private const string PilotLook = "pilot";

    private static WorldRowSelector Selector(string first, string second) => new(
        State: "suit",
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
    // The fixture document with a second, slower kit, two catalog looks, a suit cell of 1 for body 0, and both
    // assignments selecting by it: 1 is the seat kit and the suited look, 0 the slow kit and the pilot look.
    private static WorldDefinition Document(WorldRule[]? rules = null, WorldRowSelector? kitSelect = null, WorldRowSelector? lookSelect = null) {
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
                World = [
                    .. (source.StateRaw?.World ?? []),
                    new WorldStateRow(
                        CellName.Parse(candidate: "suit"),
                        CellKind.Int,
                        Capacity: 8,
                        Cells: [new StateCell(
                            CellName.Parse(candidate: "0"),
                            CellValue.Int(value: 1L)
                        )]
                    ),
                ],
            }),
        };
    }
    private static WorldMutation WriteSuit(long value) => new WorldMutation.UpsertStateCell(
        Key: "0",
        Kind: WorldDocumentWriteKind.Set,
        Principal: Principal.Console,
        Row: "suit",
        Value: value
    );
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
    public void TheBootCellSelectsTheBodysKitAndLook() {
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
    // its program on moves so the client re-latches the look.
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AWrittenCellMovesKitAndLookWhicheverDoorWroteIt(bool byRule) {
        using var fixture = Fixtures.FreshServer(definition: Document(rules: (byRule
            ? [new WorldRule(
                CellName.Parse(candidate: "unsuit"),
                [new ActionEffect.SetState(
                    Key: "0",
                    State: "suit",
                    Value: 0m
                )]
            )]
            : null)));
        var body = fixture.JoinSeat();

        fixture.Step();

        var revision = fixture.Server.Population.Revision;

        if (!byRule) {
            fixture.Server.EnqueueMutation(mutation: WriteSuit(value: 0L));
            fixture.Step();
        }

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

        if (!byRule) {
            Assert.NotEqual(
                actual: fixture.Server.Population.Revision,
                expected: revision
            );
        }

        // Writing it back restores both.
        fixture.Server.EnqueueMutation(mutation: WriteSuit(value: 1L));
        fixture.Step();

        if (!byRule) {
            Assert.Equal(
                actual: LookName(server: fixture.Server),
                expected: SuitedLook
            );
            Assert.Equal(
                actual: fixture.Server.Population.KitMotion(index: 0).Speed.Value,
                expected: 4f
            );
        }
    }
    [Fact]
    public void AValueNoCaseNamesFallsBackToTheSequence() {
        using var unselected = Fixtures.FreshServer(definition: (Document() with {
            LooksRaw = (Document().LooksRaw! with { Assignment = Document().LookAssignment with { Select = null } }),
            KitsRaw = (Document().KitsRaw! with { Assignment = Document().Assignment with { Select = null } }),
        }));
        using var fixture = Fixtures.FreshServer(definition: Document());

        unselected.JoinSeat();
        fixture.JoinSeat();
        fixture.Server.EnqueueMutation(mutation: WriteSuit(value: 7L));
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
    // A checkpoint carries the cell, not the selection, so the restored body takes its kit and look from the cell.
    [Fact]
    public void ACheckpointRestoresTheSelectionWithTheCell() {
        using var fixture = Fixtures.FreshServer(definition: Document());

        fixture.JoinSeat();
        fixture.Server.EnqueueMutation(mutation: WriteSuit(value: 0L));
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
        using var fixture = Fixtures.FreshServer(definition: Document());

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
        fixture.Server.EnqueueMutation(mutation: WriteSuit(value: 0L));
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
    [Fact]
    public void TheValidatorRefusesAnUndeclaredRowByName() {
        Assert.Contains(
            expectedSubstring: "looks.assignment.select.cases[1].row",
            actualString: Refusal(definition: Document(lookSelect: Selector(first: SuitedLook, second: "moth-pilot")))
        );
        Assert.Contains(
            expectedSubstring: "'moth-pilot'",
            actualString: Refusal(definition: Document(lookSelect: Selector(first: SuitedLook, second: "moth-pilot")))
        );
        Assert.Contains(
            expectedSubstring: "kits.assignment.select.cases[1].row",
            actualString: Refusal(definition: Document(kitSelect: Selector(first: Fixtures.SeatKitName, second: "pilot")))
        );
        Assert.Contains(
            expectedSubstring: "names state row 'cape', which the document does not declare",
            actualString: Refusal(definition: Document(lookSelect: (Selector(first: SuitedLook, second: PilotLook) with { State = "cape" })))
        );
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(),
            reason: out var reason
        ), userMessage: reason);
    }
    [Fact]
    public void TheValidatorRefusesAMisshapenSelector() {
        var repeated = new WorldRowSelector(
            State: "suit",
            Cases: [
                new WorldRowSelectorCase(Value: 1L, Row: SuitedLook),
                new WorldRowSelectorCase(Value: 1L, Row: PilotLook),
            ]
        );
        static WorldDefinition WithSuitRow(CellKind kind, string key, CellValue value) {
            var document = Document();

            return (document with {
                StateRaw = (document.StateRaw! with {
                    World = [.. document.StateRaw!.World!.Where(predicate: static row => (row.Name.Value != "suit")), new WorldStateRow(
                        CellName.Parse(candidate: "suit"),
                        kind,
                        Capacity: 8,
                        Cells: [new StateCell(
                            CellName.Parse(candidate: key),
                            value
                        )]
                    )],
                }),
            });
        }

        Assert.Contains(
            expectedSubstring: "looks.assignment.select.cases[1] repeats value 1",
            actualString: Refusal(definition: Document(lookSelect: repeated))
        );
        Assert.Contains(
            expectedSubstring: "must be a keyed Int or Bool row",
            actualString: Refusal(definition: WithSuitRow(
                key: "0",
                kind: CellKind.Fixed,
                value: CellValue.Fixed(rawBits: 0L)
            ))
        );
        Assert.Contains(
            expectedSubstring: "whose cell key '99' is not a 0-based body index",
            actualString: Refusal(definition: WithSuitRow(
                key: "99",
                kind: CellKind.Int,
                value: CellValue.Int(value: 0L)
            ))
        );
        Assert.Contains(
            expectedSubstring: "looks.assignment.select.state is required",
            actualString: Refusal(definition: Document(lookSelect: (Selector(first: SuitedLook, second: PilotLook) with { State = "" })))
        );
        Assert.Contains(
            expectedSubstring: "looks.assignment.select.cases[0] is required",
            actualString: Refusal(definition: Document(lookSelect: new WorldRowSelector(State: "suit", Cases: [null!])))
        );
        Assert.Contains(
            expectedSubstring: "looks.assignment.select.cases must name at least one value",
            actualString: Refusal(definition: Document(lookSelect: new WorldRowSelector(State: "suit", Cases: [])))
        );
    }
}
