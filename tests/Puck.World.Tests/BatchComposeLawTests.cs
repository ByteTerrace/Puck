using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A <see cref="WorldMutation.Batch"/> installs exactly the document its members would have reached
/// applied one by one: a member that reads a row an earlier member wrote reads the written value, a document value
/// bound to a written row resolves, and one refused member refuses the whole batch.</summary>
public sealed class BatchComposeLawTests {
    private const string GroupReference = "state.bindingGroups.actionGroup";

    // The reference arm of a document identifier is the JSON converter's, so the fixture round-trips its bytes to
    // hold the same bound value a hand-authored world does.
    private static WorldDefinition Document() {
        var authored = (Fixtures.BuildDocument() with {
            BindingOverlaysRaw = [
                new WorldBindingOverlay(
                    Id: "batch-compose-law",
                    Document: new BindingProfileDocument(
                        Version: BindingProfileDocument.CurrentVersion,
                        Modifiers: [],
                        Chords: [new BindingChordDefinition(Group: GroupReference, Page: new BindingPageDefinition(Id: "base", Entries: []))]
                    )
                ),
            ],
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(Name: Name("bindingGroups"), Kind: CellKind.Text, Cells: [new StateCell(Key: Name("actionGroup"), Text: "alpha")]),
                new WorldStateRow(Name("counter"), CellKind.Int, Min: 0, Max: 1_000, Cells: [new StateCell(WorldStateRow.SlotKey, 0)]),
                new WorldStateRow(Name("bag"), CellKind.Int, Capacity: 4, Cells: [Cell("a", 1), Cell("b", 2), Cell("c", 3)]),
            ]),
        });

        return WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: authored));
    }
    // A cell write that reads the value an earlier member wrote, a text write to the row the chord group is bound
    // to, and a write after it that opens a fresh workspace over the rehydrated document.
    private static WorldMutation[] Members() => [
        new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "counter", Key: WorldStateRow.SlotKey.Value, Value: 5, Kind: WorldDocumentWriteKind.Set),
        new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "bindingGroups", Key: "actionGroup", Value: 0, Kind: WorldDocumentWriteKind.Set, Text: "beta"),
        new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "counter", Key: WorldStateRow.SlotKey.Value, Value: 3, Kind: WorldDocumentWriteKind.Add),
        new WorldMutation.RemoveStateCell(Principal: WorldPrincipal.Console, Row: "bag", Key: "b"),
        new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "bag", Key: "d", Value: 4, Kind: WorldDocumentWriteKind.Set),
    ];
    private static string ComposedGroup(WorldDefinition definition) => WorldBindingComposer.Compose(definition.BindingOverlays[0].Document).Chords[0].Group.Value;

    [Fact]
    public void ABatchInstallsTheDocumentItsMembersReachOneByOne() {
        using var batched = Fixtures.FreshServer(definition: Document());
        using var sequential = Fixtures.FreshServer(definition: Document());
        var journal = 0;
        batched.Server.MutationJournalTap = (_, _) => journal++;

        batched.Server.EnqueueMutation(mutation: new WorldMutation.Batch(Principal: WorldPrincipal.Console, Mutations: Members()));
        batched.Step();

        foreach (var member in Members()) {
            sequential.Server.EnqueueMutation(mutation: member);
            sequential.Step();
        }

        Assert.Equal(expected: 1, actual: journal);
        Assert.Equal(expected: 8, actual: Slot(definition: batched.Server.Definition, row: "counter"));
        Assert.Equal(expected: ["a", "c", "d"], actual: Keys(definition: batched.Server.Definition, row: "bag"));
        Assert.Equal(expected: "beta", actual: ComposedGroup(definition: batched.Server.Definition));
        Assert.Equal(expected: "beta", actual: ComposedGroup(definition: sequential.Server.Definition));
        Assert.Equal(expected: sequential.DefinitionBytes(), actual: batched.DefinitionBytes());
    }
    [Fact]
    public void OneRefusedMemberRefusesTheWholeBatch() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var before = fixture.DefinitionBytes();
        var refusals = new List<string>();
        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.Batch(Principal: WorldPrincipal.Console, Mutations: [
            .. Members(),
            new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "absent", Key: WorldStateRow.SlotKey.Value, Value: 1, Kind: WorldDocumentWriteKind.Set),
        ]));
        fixture.Step();

        Assert.Contains(collection: refusals, filter: reason => reason.Contains(value: "no state row named 'absent'", comparisonType: StringComparison.Ordinal));
        Assert.Equal(expected: before, actual: fixture.DefinitionBytes());
    }

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateCell Cell(string key, long value) => new(Key: Name(value: key), Value: value);
    private static WorldStateRow Row(WorldDefinition definition, string row) => definition.State.Single(predicate: candidate => (candidate.Name.Value == row));
    private static long Slot(WorldDefinition definition, string row) => Row(definition: definition, row: row).Cells!.Single(predicate: cell => (cell.Key == WorldStateRow.SlotKey)).Value;
    private static string[] Keys(WorldDefinition definition, string row) => [.. Row(definition: definition, row: row).Cells!.Select(selector: cell => cell.Key.Value)];
}
