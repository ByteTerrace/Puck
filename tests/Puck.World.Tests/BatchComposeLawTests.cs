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
                new WorldStateRow(Name("cards"), CellKind.Int, Capacity: 8, Cells: [.. Enumerable.Range(0, 8).Select(index => Cell($"c{index}", index))]),
                new WorldStateRow(Name("deck"), CellKind.Bool, Domain: new StateDomain.KeysOf(Name("cards"), Ordered: true), Capacity: 4, Cells: [Cell("c0", 1), Cell("c1", 1), Cell("c2", 1), Cell("c3", 1)]),
                new WorldStateRow(Name("randomPile"), CellKind.Bool, Domain: new StateDomain.KeysOf(Name("cards"), Ordered: true), Capacity: 4),
                new WorldStateRow(Name("run"), CellKind.Bool, Domain: new StateDomain.KeysOf(Name("cards"), Ordered: true), Capacity: 4, Cells: [Cell("c4", 1), Cell("c5", 1), Cell("c6", 1), Cell("c7", 1)]),
                new WorldStateRow(Name("slicePile"), CellKind.Bool, Domain: new StateDomain.KeysOf(Name("cards"), Ordered: true), Capacity: 4),
                new WorldStateRow(Name("dice"), CellKind.Int, Draw: new Draw(Generator: new StateGenerator(Source: GeneratorSource.StreamDraw), Timing: DrawTiming.Event)),
            ]),
        });

        return WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: authored));
    }
    // A cell write that reads the value an earlier member wrote, a text write to the row the chord group is bound
    // to, and a write after it that opens a fresh workspace over the rehydrated document — the deal's own cell
    // writes and its one text write. A shuffle and a random transfer (over "deck"/"randomPile") and a slice transfer
    // (over "run"/"slicePile") close out the members TryComposeBatch cannot answer from its workspace alone (the
    // "default" arm below): each still must compose exactly as it would applied on its own.
    private static WorldMutation[] Members() => [
        new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "counter", Key: WorldStateRow.SlotKey.Value, Value: 5, Kind: WorldDocumentWriteKind.Set),
        new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "bindingGroups", Key: "actionGroup", Value: 0, Kind: WorldDocumentWriteKind.Set, Text: "beta"),
        new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "counter", Key: WorldStateRow.SlotKey.Value, Value: 3, Kind: WorldDocumentWriteKind.Add),
        new WorldMutation.RemoveStateCell(Principal: WorldPrincipal.Console, Row: "bag", Key: "b"),
        new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: "bag", Key: "d", Value: 4, Kind: WorldDocumentWriteKind.Set),
        new WorldMutation.TransformState(Principal: WorldPrincipal.Console, Transform: new StateTransform.Shuffle(Row: "deck", Draw: "dice")),
        new WorldMutation.TransformState(Principal: WorldPrincipal.Console, Transform: new StateTransform.Transfer(From: "deck", To: "randomPile", Selector: ZoneSelector.Random, Draw: "dice")),
        new WorldMutation.TransformState(Principal: WorldPrincipal.Console, Transform: new StateTransform.Transfer(From: "run", To: "slicePile", Selector: ZoneSelector.Slice, Key: "c5")),
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
        Assert.Equal(expected: 3, actual: Row(definition: batched.Server.Definition, row: "deck").Cells!.Count);
        Assert.Single(collection: Row(definition: batched.Server.Definition, row: "randomPile").Cells!);
        Assert.Equal(expected: ["c4"], actual: Keys(definition: batched.Server.Definition, row: "run"));
        Assert.Equal(expected: ["c5", "c6", "c7"], actual: Keys(definition: batched.Server.Definition, row: "slicePile"));
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
