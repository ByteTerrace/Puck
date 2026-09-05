using Puck.Physics.Motion;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins a multi-card deal as one transfer, a rule quantified over a token-keyed row, and an audience a
/// rule widens by writing a token into the readersFrom row.</summary>
public sealed class WorldDealAndRevealLawTests {
    [Fact]
    public void ACountedTransferDealsInOneMutationAndRefusesPastThePile() {
        var definition = Document([
            new(Name("cards"), CellKind.Int, Capacity: 6, Cells: [Cell("c1"), Cell("c2"), Cell("c3"), Cell("c4"), Cell("c5"), Cell("c6")]),
            new(Name("deck"), CellKind.Bool, Capacity: 6, Cells: [Cell("c1"), Cell("c2"), Cell("c3"), Cell("c4"), Cell("c5"), Cell("c6")], Domain: new WorldStateDomain.KeysOf(WorldCellName.Parse("cards"), Ordered: true)),
            new(Name("hand"), CellKind.Bool, Capacity: 6, Domain: new WorldStateDomain.KeysOf(WorldCellName.Parse("cards"), Ordered: true)),
        ], []);

        var dealt = Apply(definition, new WorldStateTransform.Transfer("deck", "hand", WorldZoneSelector.First, Count: 5));
        Assert.Equal(new[] { "c1", "c2", "c3", "c4", "c5" }, Keys(dealt, "hand"));
        Assert.Equal(new[] { "c6" }, Keys(dealt, "deck"));

        Assert.False(WorldStateTransforms.TryApply(dealt, new WorldStateTransform.Transfer("deck", "hand", WorldZoneSelector.First, Count: 2), WorldPrincipal.World, 0, "test", out _, out var shortReason));
        Assert.Contains("fewer than the 2", shortReason);
        Assert.False(WorldStateTransforms.TryApply(definition, new WorldStateTransform.Transfer("deck", "hand", WorldZoneSelector.Key, Key: "c1", Count: 2), WorldPrincipal.World, 0, "test", out _, out var keyReason));
        Assert.Contains("exactly 1 for a key", keyReason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition with { Rules = [new WorldRule(Name("bad"), [new ActionEffect.TransformState(new WorldStateTransform.Transfer("deck", "hand", WorldZoneSelector.First, Count: 0))])] }, out var compileReason));
        Assert.Contains("count of 1..", compileReason);
    }

    [Fact]
    public void ARuleQuantifiedOverTokenKeysBindsEachToTheKey() {
        var definition = Document([
            new(Name("cards"), CellKind.Int, Capacity: 3, Cells: [Cell("c1"), Cell("c2"), Cell("c3")]),
            new(Name("rank"), CellKind.Int, Domain: new WorldStateDomain.KeysOf(WorldCellName.Parse("cards")), Capacity: 3, Cells: [Cell("c1", 5), Cell("c2", 9), Cell("c3", 2)]),
            new(Name("doubled"), CellKind.Int, Domain: new WorldStateDomain.KeysOf(WorldCellName.Parse("cards")), Capacity: 3, Cells: [Cell("c1", 0), Cell("c2", 0), Cell("c3", 0)]),
        ], [
            new WorldRule(Name("double"), Mode: ActionTriggerMode.Edge, ForEach: "rank", Effects: [new ActionEffect.SetState(State: "doubled", Key: "$each", Expression: new WorldValueExpression(Tokens: [
                new WorldValueToken.State(Name: "rank", Key: "$each"), new WorldValueToken.Constant(Value: 2m), new WorldValueToken.Multiply(),
            ]))]),
        ]);

        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();

        var doubled = Find(fixture.Server.Definition, "doubled").Cells!;
        Assert.Equal(10L, WorldDefinitionRows.FindCell(doubled, Name("c1"))!.Value);
        Assert.Equal(18L, WorldDefinitionRows.FindCell(doubled, Name("c2"))!.Value);
        Assert.Equal(4L, WorldDefinitionRows.FindCell(doubled, Name("c3"))!.Value);
    }

    [Fact]
    public void AReadersFromRowRevealsAHandWhenARuleWritesTheToken() {
        var seat = WorldPrincipal.Seat(slot: 1);
        var hand = new WorldStateRow(Name("hand"), CellKind.Int, Capacity: 2, Cells: [Cell("c1", 11), Cell("c2", 12)],
            Visibility: new(Readers: [], ReadersFrom: "audience"));
        var audience = new WorldStateRow(Name("audience"), CellKind.Text, Capacity: 4, Cells: [new WorldStateCell(Name("a1"), 0L, Text: "")]);
        var definition = Document([hand, audience, Slot("showdown", 0)], [
            new WorldRule(Name("reveal"), Mode: ActionTriggerMode.Edge,
                Gate: new ActionPredicate.CompareState(State: "showdown", Comparison: ActionStateComparison.Equal, Value: 1m),
                Effects: [new ActionEffect.SetState(State: "audience", Key: "a1", Text: seat.Describe())]),
        ]);

        Assert.Null(WorldStateDisclosure.Compose(definition, seat)?.FirstOrDefault(r => r.Name == "hand"));

        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();
        Assert.Null(WorldStateDisclosure.Compose(fixture.Server.Definition, seat)?.FirstOrDefault(r => r.Name == "hand"));

        var revealed = fixture.Server.Definition with { StateRaw = fixture.Server.Definition.StateRaw! with { World = [.. fixture.Server.Definition.State.Select(r => r.Name.Value == "showdown" ? r with { Cells = [new WorldStateCell(WorldStateRow.SlotKey, 1L)] } : r)] } };
        using var shown = Fixtures.FreshServer(definition: revealed);
        shown.Step();
        var observed = WorldStateDisclosure.Compose(shown.Server.Definition, seat)?.FirstOrDefault(r => r.Name == "hand");
        Assert.NotNull(observed);
        Assert.Equal(2, observed!.Cells.Count);
        Assert.Null(WorldStateDisclosure.Compose(shown.Server.Definition, WorldPrincipal.Seat(slot: 2))?.FirstOrDefault(r => r.Name == "hand"));

        Assert.False(WorldDefinitionValidator.TryValidateLocally(Document([hand with { Visibility = new(Readers: [], ReadersFrom: "showdown") }, Slot("showdown", 0)], []), out var kindReason));
        Assert.Contains("keyed text row", kindReason);
    }

    private static WorldDefinition Document(WorldStateRow[] rows, WorldRule[] rules) => Fixtures.BuildDocument() with {
        StateRaw = new(World: rows),
        Rules = rules,
    };
    private static WorldDefinition Apply(WorldDefinition definition, WorldStateTransform transform) {
        Assert.True(WorldStateTransforms.TryApply(definition, transform, WorldPrincipal.World, 1, "test", out var candidate, out var reason), reason);
        return candidate!;
    }
    private static string[] Keys(WorldDefinition document, string row) => (Find(document, row).Cells ?? []).Select(c => c.Key.Value).ToArray();
    private static WorldCellName Name(string value) => WorldCellName.Parse(value);
    private static WorldStateCell Cell(string key, long value = 1) => new(Name(key), value);
    private static WorldStateRow Slot(string name, long value) => new(Name(name), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, value)]);
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(document.State, row)!;
}
