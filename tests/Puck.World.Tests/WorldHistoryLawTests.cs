using Puck.Physics.Motion;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the history ring: pushes wrap the oldest slot and advance the cursor, ages read newest first with
/// the empty value past what the ring holds, a pattern reads the ring oldest first, the effect resolves its value
/// like a write, and the validator refuses a ring that is not a plain numeric row.</summary>
public sealed class WorldHistoryLawTests {
    [Fact]
    public void PushesWrapTheRingAndAgesReadNewestFirst() {
        var ring = new WorldStateRow(Name("taps"), CellKind.Int, Domain: new WorldStateDomain.Ring(3, Empty: -1));
        var definition = Document([ring, Slot("latest"), Slot("oldest"), Slot("beyond")], [
            new WorldRule(Name("latest"), [new ActionEffect.SetState(State: "latest", FromState: "$history:taps:0")]),
            new WorldRule(Name("oldest"), [new ActionEffect.SetState(State: "oldest", FromState: "$history:taps:2")]),
        ]);

        var pushed = definition;
        foreach (var value in new long[] { 10, 20, 30, 40 }) {
            pushed = Apply(pushed, new WorldStateTransform.Push("taps", value));
        }
        var row = Find(pushed, "taps");
        Assert.Equal(4L, row.HistoryCursor);
        Assert.Equal(40L, WorldDefinitionRows.FindCell(row.Cells, Name("0"))!.Value);
        Assert.Equal(20L, WorldDefinitionRows.FindCell(row.Cells, Name("1"))!.Value);
        Assert.Equal(30L, WorldDefinitionRows.FindCell(row.Cells, Name("2"))!.Value);

        using var fixture = Fixtures.FreshServer(definition: pushed);
        fixture.Step();
        Assert.Equal(40L, Value(fixture, "latest"));
        Assert.Equal(20L, Value(fixture, "oldest"));

        var young = Apply(definition, new WorldStateTransform.Push("taps", 7));
        using var sparse = Fixtures.FreshServer(definition: young);
        sparse.Step();
        Assert.Equal(7L, Value(sparse, "latest"));
        Assert.Equal(-1L, Value(sparse, "oldest"));

        Assert.False(WorldDefinitionValidator.TryValidateLocally(Document([ring, Slot("beyond")], [new WorldRule(Name("beyond"), [new ActionEffect.SetState(State: "beyond", FromState: "$history:taps:3")])]), out var ageReason));
        Assert.Contains("age must be 0..2", ageReason);
    }

    [Fact]
    public void APatternReadsTheRingOldestFirstAndTheEffectPushesLikeAWrite() {
        var ring = new WorldStateRow(Name("taps"), CellKind.Int, Domain: new WorldStateDomain.Ring(4));
        var combo = new WorldPatternRow(Name("combo"), CellKind.Int, Symbols: [new(Name("a"), 1, 1), new(Name("b"), 2, 2)],
            Pattern: new WorldPatternNode.Sequence([new WorldPatternNode.Star(new WorldPatternNode.AnySymbol()), new WorldPatternNode.Symbol("a"), new WorldPatternNode.Symbol("a"), new WorldPatternNode.Symbol("b")]));
        var definition = Document([ring, Slot("hit"), Slot("tick"), Slot("source", 2)], [
            new WorldRule(Name("hit"), [new ActionEffect.SetState(State: "hit", FromState: "$match:combo:taps")]),
        ], [combo]);

        var pushed = definition;
        foreach (var value in new long[] { 9, 1, 1, 2 }) {
            pushed = Apply(pushed, new WorldStateTransform.Push("taps", value));
        }
        using var fixture = Fixtures.FreshServer(definition: pushed);
        fixture.Step();
        Assert.Equal(1L, Value(fixture, "hit"));
        Assert.Contains("accept=1", fixture.Server.DescribeMatch("combo", "taps", null, null, null));

        var wrapped = Apply(pushed, new WorldStateTransform.Push("taps", 5));
        using var stale = Fixtures.FreshServer(definition: wrapped);
        stale.Step();
        Assert.Equal(0L, Value(stale, "hit"));

        var effects = Document([ring, Slot("source", 2), Slot("count")], [
            new WorldRule(Name("push-literal"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.PushState(State: "taps", Value: 1m)]),
            new WorldRule(Name("push-from"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.PushState(State: "taps", FromState: "source")]),
            new WorldRule(Name("push-expression"), Mode: ActionTriggerMode.Edge, Effects: [new ActionEffect.PushState(State: "taps", Expression: new WorldValueExpression(Tokens: [
                new WorldValueToken.State(Name: "source"), new WorldValueToken.Constant(Value: 3m), new WorldValueToken.Multiply(),
            ]))]),
            new WorldRule(Name("count"), [new ActionEffect.SetState(State: "count", FromState: "$history:taps:0")]),
        ]);
        using var fired = Fixtures.FreshServer(definition: effects);
        fired.Step();
        fired.Step();
        var after = Find(fired.Server.Definition, "taps");
        Assert.Equal(3L, after.HistoryCursor);
        Assert.Equal(1L, WorldDefinitionRows.FindCell(after.Cells, Name("0"))!.Value);
        Assert.Equal(2L, WorldDefinitionRows.FindCell(after.Cells, Name("1"))!.Value);
        Assert.Equal(6L, WorldDefinitionRows.FindCell(after.Cells, Name("2"))!.Value);
        Assert.Equal(6L, Value(fired, "count"));
    }

    [Fact]
    public void TheValidatorRefusesARingThatIsNotAPlainNumericRowAndTheShapeRoundTrips() {
        var ring = new WorldStateRow(Name("taps"), CellKind.Int, Domain: new WorldStateDomain.Ring(3), HistoryCursor: 5, Cells: [Cell("0", 4), Cell("1", 5), Cell("2", 3)]);
        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: Document([ring], [])));
        var round = Find(parsed, "taps");
        Assert.Equal(3, ((WorldStateDomain.Ring)round.EffectiveDomain).Capacity);
        Assert.Equal(5L, round.HistoryCursor);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(parsed, out var reason), reason);

        Assert.False(WorldDefinitionValidator.TryValidateLocally(Document([new WorldStateRow(Name("t"), CellKind.Int, HistoryCursor: 1)], []), out var cursorReason));
        Assert.Contains("historyCursor without a ring domain", cursorReason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(Document([new WorldStateRow(Name("t"), CellKind.Int, Domain: new WorldStateDomain.Ring(2), HistoryCursor: 1, Cells: [Cell("5", 1)])], []), out var slotReason));
        Assert.Contains("slots 0..n-1 in order", slotReason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(Document([new WorldStateRow(Name("t"), CellKind.Int, Domain: new WorldStateDomain.Ring(3), HistoryCursor: 2, Cells: [Cell("1", 1), Cell("0", 1)])], []), out var orderReason));
        Assert.Contains("in order", orderReason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(Document([new WorldStateRow(Name("t"), CellKind.Int, Domain: new WorldStateDomain.Ring(3), HistoryCursor: 1, Cells: [Cell("0", 1), Cell("1", 1)])], []), out var cursorCountReason));
        Assert.Contains("says fewer", cursorCountReason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(Document([new WorldStateRow(Name("t"), CellKind.Text, Domain: new WorldStateDomain.Ring(2))], []), out var kindReason));
        Assert.Contains("integer or fixed", kindReason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(Document([new WorldStateRow(Name("t"), CellKind.Int, Domain: new WorldStateDomain.Ring(2), Phase: new(0))], []), out var traitReason));
        Assert.Contains("no other storage", traitReason);
        Assert.False(WorldStateTransforms.TryApply(Document([Slot("plain")], []), new WorldStateTransform.Push("plain", 1), WorldPrincipal.World, 0, "test", out _, out var pushReason));
        Assert.Contains("requires a history row", pushReason);
    }

    private static WorldDefinition Document(WorldStateRow[] rows, WorldRule[] rules, WorldPatternRow[]? patterns = null) => Fixtures.BuildDocument() with {
        StateRaw = new(World: rows, Lattices: [new WorldStateLatticeTopology.Grid("map", new Puck.Assets.Documents.DocumentVector3(0, 0, 0), 1, 4, 4)]),
        PatternsRaw = patterns ?? [],
        Rules = rules,
    };
    private static WorldDefinition Apply(WorldDefinition definition, WorldStateTransform transform) {
        Assert.True(WorldStateTransforms.TryApply(definition, transform, WorldPrincipal.World, 1, "test", out var candidate, out var reason), reason);
        return candidate!;
    }
    private static WorldCellName Name(string value) => WorldCellName.Parse(value);
    private static WorldStateCell Cell(string key, long value = 1) => new(Name(key), value);
    private static WorldStateRow Slot(string name, long value = 0) => new(Name(name), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, value)]);
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(document.State, row)!;
    private static long Value(WorldFixture fixture, string row) =>
        WorldDefinitionRows.FindCell(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells, WorldStateRow.SlotKey)!.Value;
}
