using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the pattern-language operand over its three word sources, complement and intersection, the sort that
/// canonicalizes a hand, the state budget's refusal, and the strict wire shape.</summary>
public sealed class WorldPatternLawTests {
    [Fact]
    public void ABoardRayIsAWordAndAFlankIsARegularPattern() {
        var board = new WorldStateRow(Name("board"), CellKind.Int, Cells: [Cell("0", 1), Cell("1", 2), Cell("2", 2), Cell("3", 1)], Board: new("map"));
        var definition = Document([board, Slot("flank"), Slot("south"), Slot("narrow")],
            [Bracket("bracket"), Bracket("tight", true)],
            [
                Mirror("flank", "$match:bracket:board:E", "0"),
                Mirror("south", "$match:bracket:board:S", "0"),
                Mirror("narrow", "$match:tight:board:E", "0"),
            ]);

        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();

        Assert.Equal(1L, Value(fixture, "flank"));
        Assert.Equal(0L, Value(fixture, "south"));
        Assert.Equal(0L, Value(fixture, "narrow"));
        Assert.Contains("bracket kind=Int letters=3 states=", fixture.Server.DescribePatterns());
        var narrated = fixture.Server.DescribeMatch("bracket", "board", null, "0", "E");
        Assert.Contains("accept=1 prefix=3", narrated);
        Assert.Contains("them", narrated);
        Assert.Contains("direction", fixture.Server.DescribeMatch("bracket", "board", null, "0", "any"));
        Assert.Contains("names no pattern", fixture.Server.DescribeMatch("missing", "board", null, "0", "E"));
    }

    [Fact]
    public void PrefixAndEveryDirectionFacetsAnswerFlipCountsAndFlankMasks() {
        // Row 0 of the 4x4 grid reads 1 2 2 1 eastward from cell 0, so the flank pattern accepts the 3-cell
        // prefix and no other ray from cell 0 has a them+ run closed by me.
        var board = new WorldStateRow(Name("board"), CellKind.Int, Cells: [Cell("0", 1), Cell("1", 2), Cell("2", 2), Cell("3", 1)], Board: new("map"));
        var definition = Document([board, Slot("flips"), Slot("mask"), Slot("count"), Slot("handPrefix"),
                new(Name("hand"), CellKind.Int, KeysFrom: "cards", Cells: [Cell("c1", 2), Cell("c2", 2), Cell("c3", 1), Cell("c4", 2)]),
                new(Name("cards"), CellKind.Int, Capacity: 4, Cells: [Cell("c1"), Cell("c2"), Cell("c3"), Cell("c4")], Tokens: new())],
            [Bracket("bracket")],
            [
                Mirror("flips", "$match:bracket:board:E:prefix", "0"),
                Mirror("mask", "$match:bracket:board:any:mask", "0"),
                Mirror("count", "$match:bracket:board:any:count", "0"),
                Mirror("handPrefix", "$match:bracket:hand:prefix"),
            ]);

        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();

        Assert.Equal(3L, Value(fixture, "flips"));
        Assert.Equal(1L, Value(fixture, "count"));
        var mask = Value(fixture, "mask");
        Assert.True(mask != 0L && (mask & (mask - 1L)) == 0L, $"one direction accepts, mask={mask}");
        Assert.Equal(3L, Value(fixture, "handPrefix"));

        Assert.False(WorldDefinitionValidator.TryValidateLocally(Document([board, Slot("x")], [Bracket("bracket")], [Mirror("x", "$match:bracket:board:E:mask", "0")]), out var facetReason));
        Assert.Contains("not a facet", facetReason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(Document([board, Slot("x")], [Bracket("bracket")], [Mirror("x", "$match:bracket:board:any:prefix", "0")]), out var anyReason));
        Assert.Contains("not a facet", anyReason);
    }

    [Fact]
    public void AValueExpressionReadsATupleOfAttributesPerTokenAndTokenBindsOnlyThere() {
        // suit * 16 + rank: hearts are suit 1, so a heart of any rank lies in 16..31 and a heart flush is h{5}.
        WorldValueExpression Tuple() => new(Tokens: [
            new WorldValueToken.State(Name: "suit", Key: "$token"), new WorldValueToken.Constant(Value: 16m), new WorldValueToken.Multiply(),
            new WorldValueToken.State(Name: "rank", Key: "$token"), new WorldValueToken.Add(),
        ]);
        var flush = new WorldPatternRow(Name("hearts"), CellKind.Int, Symbols: [new(Name("h"), 16, 31)], Pattern: new WorldPatternNode.Repeat(new WorldPatternNode.Symbol("h"), 5, 5), Value: Tuple());
        var straightFlush = new WorldPatternRow(Name("royal"), CellKind.Int, Symbols: [.. Enumerable.Range(5, 5).Select(r => new WorldPatternSymbol(Name($"h{r}"), 16 + r, 16 + r))],
            Pattern: new WorldPatternNode.Sequence([.. Enumerable.Range(5, 5).Select(r => (WorldPatternNode)new WorldPatternNode.Symbol($"h{r}"))]), Value: Tuple());
        var suited = Hand() with { StateRaw = Hand().StateRaw! with { World = [.. Hand().State.Where(r => r.Name.Value != "straight").Select(r => r.Name.Value == "suit"
            ? r with { Cells = [Cell("c1", 1), Cell("c2", 1), Cell("c3", 1), Cell("c4", 1), Cell("c5", 1)] }
            : r),
            Slot("flush"), Slot("royal")] } };
        var definition = suited with { PatternsRaw = [flush, straightFlush], Rules = [Mirror("flush", "$match:hearts:hand"), Mirror("royal", "$match:royal:hand")] };

        using var unsorted = Fixtures.FreshServer(definition: definition);
        unsorted.Step();
        Assert.Equal(1L, Value(unsorted, "flush"));
        Assert.Equal(0L, Value(unsorted, "royal"));
        Assert.Contains("accept=1", unsorted.Server.DescribeMatch("hearts", "hand", null, null, null));

        var sorted = Apply(definition, new WorldStateTransform.SortZone("hand", By: [new("rank")]));
        using var ordered = Fixtures.FreshServer(definition: sorted);
        ordered.Step();
        Assert.Equal(1L, Value(ordered, "royal"));

        var mixed = definition with { StateRaw = definition.StateRaw! with { World = [.. definition.State.Select(r => r.Name.Value == "suit" ? r with { Cells = [Cell("c1", 1), Cell("c2", 2), Cell("c3", 1), Cell("c4", 1), Cell("c5", 1)] } : r)] } };
        using var broken = Fixtures.FreshServer(definition: mixed);
        broken.Step();
        Assert.Equal(0L, Value(broken, "flush"));

        var stray = definition with { Rules = [new WorldRule(Name("stray"), [new ActionEffect.SetState(State: "flush", FromState: "rank", FromKey: "$token")])] };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(stray, out var strayReason));
        Assert.Contains("not bound here", strayReason);
        var foreign = definition with { PatternsRaw = [flush with { Value = new(Tokens: [new WorldValueToken.State(Name: "flush", Key: "$token")]) }], Rules = [Mirror("flush", "$match:hearts:hand")] };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(foreign, out var foreignReason));
        Assert.Contains("$token", foreignReason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition with { PatternsRaw = [flush with { Attribute = "rank" }] }, out var bothReason));
        Assert.Contains("both attribute and value", bothReason);
    }

    [Fact]
    public void TheEmptyLanguageIsTheZeroOfChoiceAndTheAnnihilatorOfSequence() {
        var dice = new WorldStateRow(Name("dice"), CellKind.Int, Capacity: 2, Cells: [Cell("d1", 1), Cell("d2", 1)]);
        WorldPatternRow Row(string name, WorldPatternNode pattern) => new(Name(name), CellKind.Int, Symbols: [new(Name("a"), 1, 1)], Pattern: pattern);
        var definition = Document([dice, Slot("zero"), Slot("choice"), Slot("annihilated"), Slot("everything")], [
            Row("zero", new WorldPatternNode.None()),
            Row("choice", new WorldPatternNode.Choice([new WorldPatternNode.None(), new WorldPatternNode.Repeat(new WorldPatternNode.Symbol("a"), 2, 2)])),
            Row("annihilated", new WorldPatternNode.Sequence([new WorldPatternNode.Star(new WorldPatternNode.AnySymbol()), new WorldPatternNode.None()])),
            Row("everything", new WorldPatternNode.Complement(new WorldPatternNode.None())),
        ], [Mirror("zero", "$match:zero:dice"), Mirror("choice", "$match:choice:dice"), Mirror("annihilated", "$match:annihilated:dice"), Mirror("everything", "$match:everything:dice")]);

        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();

        Assert.Equal(0L, Value(fixture, "zero"));
        Assert.Equal(1L, Value(fixture, "choice"));
        Assert.Equal(0L, Value(fixture, "annihilated"));
        Assert.Equal(1L, Value(fixture, "everything"));
    }

    [Fact]
    public void ASortedHandReadsItsAttributeWordAndAStraightMatchesOnlyAfterSorting() {
        var unsorted = Hand();
        var sorted = Apply(unsorted, new WorldStateTransform.SortZone("hand", By: [new("rank")]));
        Assert.Equal(new[] { "c2", "c4", "c3", "c5", "c1" }, Find(sorted, "hand").Cells!.Select(c => c.Key.Value));
        var descending = Apply(unsorted, new WorldStateTransform.SortZone("hand", By: [new("rank", Descending: true)]));
        Assert.Equal("c1", Find(descending, "hand").Cells![0].Key.Value);
        // Suit first, rank descending inside a suit, stable across cards with equal keys.
        var suited = Apply(unsorted, new WorldStateTransform.SortZone("hand", By: [new("suit"), new("rank", Descending: true)]));
        Assert.Equal(new[] { "c1", "c5", "c3", "c4", "c2" }, Find(suited, "hand").Cells!.Select(c => c.Key.Value));
        // Control: no attribute keys at all is not "sort by nothing" — it refuses.
        Assert.False(WorldStateTransforms.TryApply(unsorted, new WorldStateTransform.SortZone("hand", By: []), WorldPrincipal.World, 0, "test", out _, out var flagReason));
        Assert.Contains("attribute keys", flagReason);
        // An attribute keyed over another token domain never sorts silently as zeroes.
        var foreign = unsorted with { StateRaw = unsorted.StateRaw! with { World = [.. unsorted.State,
            new(Name("seats"), CellKind.Int, Capacity: 2, Cells: [Cell("s1"), Cell("s2")], Tokens: new()),
            new(Name("score"), CellKind.Int, KeysFrom: "seats", Cells: [Cell("s1", 3), Cell("s2", 1)])] } };
        Assert.False(WorldStateTransforms.TryApply(foreign, new WorldStateTransform.SortZone("hand", By: [new("score")]), WorldPrincipal.World, 0, "test", out _, out var domainReason));
        Assert.Contains("token domain 'cards'", domainReason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(foreign with { Rules = [new WorldRule(Name("bad"), [new ActionEffect.TransformState(new WorldStateTransform.SortZone("hand", By: [new("score")]))])] }, out var compileReason));
        Assert.Contains("token domain", compileReason);
        var foreignPattern = new WorldPatternRow(Name("far"), CellKind.Int, Attribute: "score", Symbols: [new(Name("one"), 1, 1)], Pattern: new WorldPatternNode.Symbol("one"));
        Assert.False(WorldDefinitionValidator.TryValidateLocally(foreign with { PatternsRaw = [.. foreign.Patterns, foreignPattern], Rules = [Mirror("straight", "$match:far:hand")] }, out var attributeReason));
        Assert.Contains("token domain", attributeReason);

        using var before = Fixtures.FreshServer(definition: unsorted);
        before.Step();
        Assert.Equal(0L, Value(before, "straight"));

        using var after = Fixtures.FreshServer(definition: sorted);
        after.Step();
        Assert.Equal(1L, Value(after, "straight"));
    }

    [Fact]
    public void ASortedTrayMatchesAChoiceOfSequencesAndAKeyedRowSortsByItsOwnValues() {
        var dice = new WorldStateRow(Name("dice"), CellKind.Int, Capacity: 5, Cells: [Cell("d1", 4), Cell("d2", 2), Cell("d3", 6), Cell("d4", 3), Cell("d5", 5)]);
        var large = new WorldPatternRow(Name("large"), CellKind.Int, Symbols: Enumerable.Range(1, 6).Select(i => new WorldPatternSymbol(Name($"p{i}"), i, i)).ToArray(),
            Pattern: new WorldPatternNode.Choice([
                new WorldPatternNode.Sequence([.. Enumerable.Range(1, 5).Select(i => (WorldPatternNode)new WorldPatternNode.Symbol($"p{i}"))]),
                new WorldPatternNode.Sequence([.. Enumerable.Range(2, 5).Select(i => (WorldPatternNode)new WorldPatternNode.Symbol($"p{i}"))]),
            ]));
        var definition = Document([dice, Slot("hit")], [large], [Mirror("hit", "$match:large:dice")]);

        using var unsorted = Fixtures.FreshServer(definition: definition);
        unsorted.Step();
        Assert.Equal(0L, Value(unsorted, "hit"));

        var sorted = Apply(definition, new WorldStateTransform.SortKeyed("dice"));
        Assert.Equal(new[] { 2L, 3L, 4L, 5L, 6L }, Find(sorted, "dice").Cells!.Select(c => c.Value));
        using var fixture = Fixtures.FreshServer(definition: sorted);
        fixture.Step();
        Assert.Equal(1L, Value(fixture, "hit"));

        // Control: a plain slot row carries no keyed cells to order.
        Assert.False(WorldStateTransforms.TryApply(definition, new WorldStateTransform.SortKeyed("hit"), WorldPrincipal.World, 0, "test", out _, out var reason));
        Assert.Contains("keyed numeric row", reason);
    }

    [Fact]
    public void TheStateBudgetRefusesAtValidationAndMismatchedKindsRefuseAtCompilation() {
        // any* a any{n} needs 2^(n+1) states: the classical witness that a budget is a real refusal, not a formality.
        WorldPatternRow Tail(int n, int maxStates) => new(Name("tail"), CellKind.Int, Symbols: [new(Name("a"), 1, 1)], MaxStates: maxStates,
            Pattern: new WorldPatternNode.Sequence([new WorldPatternNode.Star(new WorldPatternNode.AnySymbol()), new WorldPatternNode.Symbol("a"), new WorldPatternNode.Repeat(new WorldPatternNode.AnySymbol(), n, n)]));
        var dice = new WorldStateRow(Name("dice"), CellKind.Int, Capacity: 8, Cells: [Cell("d1", 1)]);

        Assert.True(WorldDefinitionValidator.TryValidateLocally(Document([dice], [Tail(2, 16)], []), out var narrowReason), narrowReason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(Document([dice], [Tail(6, 16)], []), out var wideReason));
        Assert.Contains("more than 16 states", wideReason);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(Document([dice], [Tail(6, 256)], []), out var roomyReason), roomyReason);

        var fixedPattern = new WorldPatternRow(Name("fx"), CellKind.Fixed, Symbols: [new(Name("half"), 0.5m, 0.5m)], Pattern: new WorldPatternNode.Symbol("half"));
        Assert.False(WorldDefinitionValidator.TryValidateLocally(Document([dice, Slot("hit")], [fixedPattern], [Mirror("hit", "$match:fx:dice")]), out var kindReason));
        Assert.Contains("kind=Fixed", kindReason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(Document([dice, Slot("hit")], [Tail(1, 16)], [Mirror("hit", "$match:missing:dice")]), out var missingReason));
        Assert.Contains("names no pattern", missingReason);
    }

    [Fact]
    public void AComplementAndAnIntersectionAreSinglePatterns() {
        WorldPatternNode Contains(string symbol) => new WorldPatternNode.Sequence([new WorldPatternNode.Star(new WorldPatternNode.AnySymbol()), new WorldPatternNode.Symbol(symbol), new WorldPatternNode.Star(new WorldPatternNode.AnySymbol())]);
        WorldPatternNode Adjacent(string symbol) => new WorldPatternNode.Sequence([new WorldPatternNode.Star(new WorldPatternNode.AnySymbol()), new WorldPatternNode.Symbol(symbol), new WorldPatternNode.Symbol(symbol), new WorldPatternNode.Star(new WorldPatternNode.AnySymbol())]);
        var symbols = Enumerable.Range(1, 6).Select(i => new WorldPatternSymbol(Name($"p{i}"), i, i)).ToArray();
        var dice = new WorldStateRow(Name("dice"), CellKind.Int, Capacity: 5, Cells: [Cell("d1", 4), Cell("d2", 2), Cell("d3", 6), Cell("d4", 6), Cell("d5", 5)]);
        var definition = Document([dice, Slot("pair"), Slot("noSix"), Slot("twoAndFive")], [
            new(Name("pair"), CellKind.Int, Symbols: symbols, Pattern: new WorldPatternNode.Choice([.. Enumerable.Range(1, 6).Select(i => Adjacent($"p{i}"))])),
            new(Name("noSix"), CellKind.Int, Symbols: symbols, Pattern: new WorldPatternNode.Complement(Contains("p6"))),
            new(Name("twoAndFive"), CellKind.Int, Symbols: symbols, Pattern: new WorldPatternNode.Both([Contains("p2"), Contains("p5")])),
        ], [Mirror("pair", "$match:pair:dice"), Mirror("noSix", "$match:noSix:dice"), Mirror("twoAndFive", "$match:twoAndFive:dice")]);

        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();

        Assert.Equal(1L, Value(fixture, "pair"));
        Assert.Equal(0L, Value(fixture, "noSix"));
        Assert.Equal(1L, Value(fixture, "twoAndFive"));
    }

    [Fact]
    public void PatternsAndSortRoundTripThroughTheStrictWireShape() {
        var every = new WorldPatternRow(Name("every"), CellKind.Int, Symbols: [new(Name("a"), 1, 2), new(Name("b"), 2, 3)],
            Pattern: new WorldPatternNode.Sequence([
                new WorldPatternNode.Symbol("a"), new WorldPatternNode.AnySymbol(), new WorldPatternNode.Except("b"), new WorldPatternNode.Nothing(),
                new WorldPatternNode.Choice([new WorldPatternNode.Symbol("a"), new WorldPatternNode.Symbol("b")]),
                new WorldPatternNode.Optional(new WorldPatternNode.Symbol("a")),
                new WorldPatternNode.Repeat(new WorldPatternNode.Symbol("b"), 0, 1),
                new WorldPatternNode.Complement(new WorldPatternNode.Both([new WorldPatternNode.Symbol("a"), new WorldPatternNode.AnySymbol()])),
                new WorldPatternNode.Optional(new WorldPatternNode.None()),
            ]), MaxStates: 12);
        var definition = Hand() with { PatternsRaw = [.. Hand().Patterns, every], Rules = [.. Hand().Rules!, new WorldRule(Name("order"), [new ActionEffect.TransformState(new WorldStateTransform.SortZone("hand", By: [new("suit"), new("rank", Descending: true)]))])] };

        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));
        Assert.Equal(2, parsed.Patterns.Count);
        Assert.Equal(12, parsed.Patterns[1].MaxStates);
        Assert.IsType<WorldPatternNode.Sequence>(parsed.Patterns[1].Pattern);
        var sort = Assert.IsType<WorldStateTransform.SortZone>(Assert.IsType<ActionEffect.TransformState>(parsed.Rules![1].Effects[0]).Transform);
        Assert.Equal(2, sort.By!.Count);
        Assert.True(sort.By[1].Descending);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(parsed, out var reason), reason);
    }

    private static WorldPatternRow Bracket(string name, bool single = false) => new(Name(name), CellKind.Int,
        Symbols: [new(Name("me"), 1, 1), new(Name("them"), 2, 2)],
        Pattern: new WorldPatternNode.Sequence([single ? new WorldPatternNode.Symbol("them") : new WorldPatternNode.Plus(new WorldPatternNode.Symbol("them")), new WorldPatternNode.Symbol("me")]));
    private static WorldDefinition Hand() {
        var straight = new WorldPatternRow(Name("straight"), CellKind.Int, Attribute: "rank",
            Symbols: Enumerable.Range(5, 5).Select(i => new WorldPatternSymbol(Name($"r{i}"), i, i)).ToArray(),
            Pattern: new WorldPatternNode.Sequence([.. Enumerable.Range(5, 5).Select(i => (WorldPatternNode)new WorldPatternNode.Symbol($"r{i}"))]));
        return Document([
            new(Name("cards"), CellKind.Int, Capacity: 5, Cells: [Cell("c1"), Cell("c2"), Cell("c3"), Cell("c4"), Cell("c5")], Tokens: new()),
            new(Name("rank"), CellKind.Int, KeysFrom: "cards", Cells: [Cell("c1", 9), Cell("c2", 5), Cell("c3", 7), Cell("c4", 6), Cell("c5", 8)]),
            new(Name("suit"), CellKind.Int, KeysFrom: "cards", Cells: [Cell("c1", 1), Cell("c2", 2), Cell("c3", 1), Cell("c4", 2), Cell("c5", 1)]),
            new(Name("hand"), CellKind.Bool, Capacity: 5, Cells: [Cell("c1"), Cell("c2"), Cell("c3"), Cell("c4"), Cell("c5")], Zone: new("cards", Ordered: true)),
            Slot("straight"),
        ], [straight], [Mirror("straight", "$match:straight:hand")]);
    }
    private static WorldRule Mirror(string target, string operand, string? key = null) => new(Name(target + "-mirror"), [new ActionEffect.SetState(State: target, FromState: operand, FromKey: key)]);
    private static WorldDefinition Document(WorldStateRow[] rows, WorldPatternRow[] patterns, WorldRule[] rules) => Fixtures.BuildDocument() with {
        StateRaw = new(World: rows, Lattices: [new WorldStateLatticeTopology("map", new DocumentVector3(0, 0, 0), 1, 4, 4, Kind: WorldTopologyKind.Grid)]),
        PatternsRaw = patterns,
        Rules = rules,
    };
    private static WorldDefinition Apply(WorldDefinition definition, WorldStateTransform transform) {
        Assert.True(WorldStateTransforms.TryApply(definition, transform, WorldPrincipal.World, 1, "test", out var candidate, out var reason), reason);
        return candidate!;
    }
    private static WorldCellName Name(string value) => WorldCellName.Parse(value);
    private static WorldStateCell Cell(string key, long value = 1) => new(Name(key), value);
    private static WorldStateRow Slot(string name) => new(Name(name), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 0L)]);
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(document.State, row)!;
    private static long Value(WorldFixture fixture, string row) =>
        WorldDefinitionRows.FindCell(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells, WorldStateRow.SlotKey)!.Value;
}
