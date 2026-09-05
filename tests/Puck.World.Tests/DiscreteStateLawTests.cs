using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class DiscreteStateLawTests {
    [Fact]
    public void HexAndRingAddressingAreBoundedAndReciprocal() {
        var hex = new LatticeTopology.Hex("hex", new DocumentVector3(0,0,0), 1, Radius: 2);
        var ring = new LatticeTopology.Ring("ring", new DocumentVector3(0,0,0), 1, Width: 5);
        var state = new WorldStateSection(Lattices: [hex, ring]);
        var topology = TopologyCompilation.Find(state, "hex")!;
        Assert.Equal(19, topology.CellCount);
        for (var cell = 0; cell < topology.CellCount; cell++) {
            for (var direction = 0; direction < 6; direction++) {
                var neighbour = topology.Neighbour(cell, direction);
                if (neighbour >= 0) { Assert.Equal(cell, topology.Neighbour(neighbour, (direction + 3) % 6)); }
            }
        }
        var cycle = TopologyCompilation.Find(state, "ring")!;
        Assert.Equal(4, cycle.Neighbour(0, cycle.Direction("backward")));
        Assert.Equal(0, cycle.Neighbour(4, cycle.Direction("forward")));
        Assert.False(TopologyCompilation.TryValidate(hex with { Radius = TopologyCompilation.MaxHexRadius + 1 }, out var radiusReason));
        Assert.Contains($"0..{TopologyCompilation.MaxHexRadius}", radiusReason);
        Assert.True(TopologyCompilation.TryValidate(hex with { Radius = TopologyCompilation.MaxHexRadius }, out _));
    }

    [Fact]
    public void WarmBoardReadsAndPathQueriesAllocateNothing() {
        var state = new WorldStateSection(Lattices: [Grid()]);
        var topology = TopologyCompilation.Find(state, "map")!;
        var row = new WorldStateRow(Name("terrain"), CellKind.Int, Cells: [Cell("1",2)], Domain: new StateDomain.CellsOf("map",1));
        var query = new BoardPathCostQuery(topology, target: 15, maxCost: 100, maxVisits: 16);
        Span<long> values = stackalloc long[16];
        BoardQueries.Read(row, topology, values);
        _ = BoardQueries.Evaluate(query, values, 1, 0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var repeat = 0; repeat < 100; repeat++) {
            _ = WorldTopologyCompilation.FindPhysical(state);
            BoardQueries.Read(row, topology, values);
            _ = BoardQueries.Evaluate(query, values, 1, 0);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void APhaseOfTaggedRowRefusesAnUnguardedTransformAndAMatchingGuardAdvancesTheGenerationOnSuccess() {
        var definition = Document(
            new(Name("turn"), CellKind.Int, Phase: new()),
            new(Name("cards"), CellKind.Int, Cells: [Cell("a")]),
            new(Name("deck"), CellKind.Bool, Cells: [Cell("a")], Domain: new StateDomain.KeysOf(CellName.Parse("cards"), Ordered: true), PhaseOf: "turn"),
            new(Name("hand"), CellKind.Bool, Domain: new StateDomain.KeysOf(CellName.Parse("cards"), Ordered: true), PhaseOf: "turn"));
        Assert.True(WorldStateTransforms.CanAct(definition, new("turn", 0), WorldPrincipal.Console));
        Assert.False(WorldStateTransforms.CanAct(definition, new("turn", 1), WorldPrincipal.Console));
        using var fixture = Fixtures.FreshServer(definition: definition);
        var operation = new StateTransform.Transfer("deck", "hand", ZoneSelector.First);
        fixture.Server.Submit(new(SubmissionEnvelope.LocalConnectionId, 0, 1, 1, WorldPrincipal.Console,
            new WorldSubmissionPayload.Mutation(new WorldMutation.TransformState(WorldPrincipal.Console, operation))), _ => { });
        fixture.Step();
        Assert.Empty(Find(fixture.Server.Definition, "hand").Cells ?? []);
        Assert.Equal(0L, Find(fixture.Server.Definition, "turn").Phase!.Sequence);
        fixture.Server.Submit(new(SubmissionEnvelope.LocalConnectionId, 0, 2, 2, WorldPrincipal.Console,
            new WorldSubmissionPayload.Mutation(new WorldMutation.TransformState(WorldPrincipal.Console, operation, new("turn",0)))), _ => { });
        fixture.Step();
        Assert.Single(Find(fixture.Server.Definition, "hand").Cells!);
        Assert.Equal(1L, Find(fixture.Server.Definition, "turn").Phase!.Sequence);
    }

    private static CellName Name(string value) => CellName.Parse(value);
    private static StateCell Cell(string key, long value = 1) => new(Name(key), value);
    private static WorldStateRow Row(string name, params StateCell[] cells) => new(Name(name), CellKind.Int, Cells: cells);
    private static LatticeTopology.Grid Grid(int width = 4, int depth = 4, TopologyWrap wrap = TopologyWrap.None) => new("map", new DocumentVector3(0, 0, 0), 1, width, depth, Wrap: wrap);
    private static WorldDefinition Document(params WorldStateRow[] rows) => Fixtures.BuildDocument() with { StateRaw = new(World: rows, Lattices: [Grid()]), Rules = [] };
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(document.State, row)!;

    [Fact]
    public void DiscreteTopologiesDoNotAllocatePhysicalFieldsAndWrappedRaysTerminate() {
        var definition = Document(new WorldStateRow(Name("board"), CellKind.Int, Domain: new StateDomain.CellsOf("map")));
        Assert.Null(definition.Fields);
        var topology = TopologyCompilation.Find(definition.StateRaw, "map")!;
        Assert.Equal(-1, topology.Neighbour(0, topology.Direction("N")));
        Assert.Equal(5, topology.Neighbour(0, topology.Direction("SE")));
        var wrapped = TopologyCompilation.Find(new WorldStateSection(Lattices: [Grid(wrap: TopologyWrap.Both)]), "map")!;
        Assert.Equal(12, wrapped.Neighbour(0, wrapped.Direction("N")));

        // A ray over a fully wrapped board whose every cell matches the pattern reads the pattern-engine's own
        // ReadRay, which breaks the moment it returns to its own origin: the walk still terminates (rather than
        // looping forever) with no blocker found, since the pattern accepts the whole (CellCount - 1)-cell word —
        // the wrap-termination guarantee a $match-over-a-ray read now leans on where a dedicated ray query once did.
        var runOfOnes = new PatternRow(Name("runOfOnes"), CellKind.Int, Symbols: [new(Name("one"), 1, 1)], Pattern: new PatternNode.Star(new PatternNode.Symbol("one")));
        var wrappedDefinition = Fixtures.BuildDocument() with {
            StateRaw = new(World: [new WorldStateRow(Name("board"), CellKind.Int, Domain: new StateDomain.CellsOf("map", Empty: 1)), new WorldStateRow(Name("blocker"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)])], Lattices: [Grid(wrap: TopologyWrap.Both)]),
            PatternsRaw = [runOfOnes],
            Rules = [new WorldRule(Name("read"), [new ActionEffect.SetState(State: "blocker", FromState: "$match:runOfOnes:board:E:cell", FromKey: "0")])],
        };
        using var fixture = Fixtures.FreshServer(definition: wrappedDefinition);
        fixture.Step();
        Assert.Equal(-1L, StateRows.FindCell(Find(fixture.Server.Definition, "blocker").Cells, WorldStateRow.SlotKey)!.Value);
    }

    private static PatternRow CapturePattern() => new(Name("capture"), CellKind.Int,
        [new(Name("through"), 2, 2), new(Name("until"), 1, 1)],
        new PatternNode.Sequence([new PatternNode.Plus(new PatternNode.Symbol("through")), new PatternNode.Symbol("until")]));

    [Fact]
    public void BracketedRayCommitsTheAcceptedPrefixOrRefusesOnAnEmptyOne() {
        var definition = Document(new WorldStateRow(Name("board"), CellKind.Int, Cells: [Cell("0",1),Cell("1",2),Cell("2",2),Cell("3",1)], Domain: new StateDomain.CellsOf("map"))) with {
            PatternsRaw = [CapturePattern()],
        };
        Assert.True(CompiledPatterns.TryCompileAll(definition.Patterns, out var patterns, []));
        var operation = new StateTransform.SetRay("board", "0", "E", "capture", 1);
        Assert.True(WorldStateTransforms.TryApply(definition, operation, WorldPrincipal.World, 1, "test", out var changed, out var reason, patterns), reason);
        Assert.All(Find(changed, "board").Cells!, c => Assert.Equal(1, c.Value));
        Assert.Equal(2, Find(definition, "board").Cells![1].Value);
        // Control: a board holding only "through" values never reaches the required "until" terminator, so the
        // longest accepted prefix is empty and the whole write is refused.
        var open = Document(new WorldStateRow(Name("board"), CellKind.Int, Cells: [Cell("0",1),Cell("1",2),Cell("2",2),Cell("3",2)], Domain: new StateDomain.CellsOf("map"))) with {
            PatternsRaw = [CapturePattern()],
        };
        Assert.True(CompiledPatterns.TryCompileAll(open.Patterns, out var openPatterns, []));
        Assert.False(WorldStateTransforms.TryApply(open, operation, WorldPrincipal.World, 1, "test", out var refused, out _, openPatterns));
        Assert.Same(open, refused);
    }

    [Fact]
    public void ASliceMovesTheKeyedTokenAndEverythingAfterItInOrder() {
        var definition = Document(
            new(Name("cards"), CellKind.Int, Cells: [Cell("a",1),Cell("b",2),Cell("c",3),Cell("d",4),Cell("e",5)]),
            new(Name("column"), CellKind.Bool, Cells: [Cell("a"),Cell("b"),Cell("c"),Cell("d")], Domain: new StateDomain.KeysOf(CellName.Parse("cards"), Ordered: true)),
            new(Name("other"), CellKind.Bool, Cells: [Cell("e")], Domain: new StateDomain.KeysOf(CellName.Parse("cards"), Ordered: true)),
            new(Name("small"), CellKind.Bool, Capacity: 2, Cells: [], Domain: new StateDomain.KeysOf(CellName.Parse("cards"), Ordered: true)));
        var slice = new StateTransform.Transfer("column", "other", ZoneSelector.Slice, Key: "b");
        Assert.True(WorldStateTransforms.TryApply(definition, slice, WorldPrincipal.Console, 0, "test", out var changed, out var reason), reason);
        Assert.Equal(new[] { "a" }, Find(changed, "column").Cells!.Select(c => c.Key.Value));
        Assert.Equal(new[] { "e", "b", "c", "d" }, Find(changed, "other").Cells!.Select(c => c.Key.Value));
        Assert.True(WorldStateTransforms.TryApply(definition, slice with { InsertFirst = true }, WorldPrincipal.Console, 0, "test", out var first, out reason), reason);
        Assert.Equal(new[] { "b", "c", "d", "e" }, Find(first, "other").Cells!.Select(c => c.Key.Value));
        Assert.False(WorldStateTransforms.TryApply(definition, slice with { To = "small" }, WorldPrincipal.Console, 0, "test", out _, out var full));
        Assert.Contains("full", full);
        Assert.False(WorldStateTransforms.TryApply(definition, slice with { Key = "zz" }, WorldPrincipal.Console, 0, "test", out _, out var missing));
        Assert.Contains("does not contain", missing);
        Assert.False(WorldStateTransforms.TryApply(definition, slice with { Count = 2 }, WorldPrincipal.Console, 0, "test", out _, out _));
        Assert.True(WorldStateTransforms.TryApply(definition, new StateTransform.Transfer("column", "column", ZoneSelector.Slice, Key: "c", InsertFirst: true), WorldPrincipal.Console, 0, "test", out var rotated, out reason), reason);
        Assert.Equal(new[] { "c", "d", "a", "b" }, Find(rotated, "column").Cells!.Select(c => c.Key.Value));
    }

    [Fact]
    public void BoardCombineRunsTheSetAlgebraOverABoardWiderThanAWord() {
        var wide = new LatticeTopology.Grid("wide", new DocumentVector3(0,0,0), 1, Width: 10, Depth: 10);
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new(World: [
                new(Name("white"), CellKind.Int, Cells: [Cell("0",1),Cell("1",1),Cell("11",1),Cell("99",1)], Domain: new StateDomain.CellsOf("wide")),
                new(Name("black"), CellKind.Int, Cells: [Cell("1",1),Cell("2",1)], Domain: new StateDomain.CellsOf("wide")),
                new(Name("out"), CellKind.Int, Domain: new StateDomain.CellsOf("wide")),
            ], Lattices: [wide]),
            Rules = [],
        };
        static long[] Members(WorldDefinition document, string row) => [.. Find(document, row).Cells!.Where(c => c.Value != 0).Select(c => long.Parse(c.Key.Value)).Order()];

        Assert.True(WorldStateTransforms.TryApply(definition, new StateTransform.BoardCombine("out", BoardCombineOp.Shift, Left: "white", Direction: "E"), WorldPrincipal.Console, 0, "test", out var shifted, out var reason), reason);
        Assert.Equal(new long[] { 1, 2, 12 }, Members(shifted, "out"));
        Assert.True(WorldStateTransforms.TryApply(definition, new StateTransform.BoardCombine("out", BoardCombineOp.And, Left: "white", Right: "black"), WorldPrincipal.Console, 0, "test", out var both, out reason), reason);
        Assert.Equal(new long[] { 1 }, Members(both, "out"));
        Assert.True(WorldStateTransforms.TryApply(definition, new StateTransform.BoardCombine("out", BoardCombineOp.AndNot, Left: "white", Right: "black", Value: 7), WorldPrincipal.Console, 0, "test", out var only, out reason), reason);
        Assert.Equal(new long[] { 0, 11, 99 }, Members(only, "out"));
        Assert.All(Find(only, "out").Cells!, c => Assert.Equal(7, c.Value));
        Assert.True(WorldStateTransforms.TryApply(definition, new StateTransform.BoardCombine("out", BoardCombineOp.Not, Left: "black"), WorldPrincipal.Console, 0, "test", out var complement, out reason), reason);
        Assert.Equal(98, Members(complement, "out").Length);
        Assert.True(WorldStateTransforms.TryApply(definition, new StateTransform.BoardCombine("out", BoardCombineOp.Image, Left: "white", Element: "identity"), WorldPrincipal.Console, 0, "test", out var image, out reason), reason);
        Assert.Equal(new long[] { 0, 1, 11, 99 }, Members(image, "out"));
        Assert.True(WorldStateTransforms.TryApply(definition, new StateTransform.BoardCombine("out", BoardCombineOp.Fill, Value: 3), WorldPrincipal.Console, 0, "test", out var filled, out reason), reason);
        Assert.Equal(100, Members(filled, "out").Length);
        Assert.True(WorldStateTransforms.TryApply(filled, new StateTransform.BoardCombine("out", BoardCombineOp.Clear), WorldPrincipal.Console, 0, "test", out var cleared, out reason), reason);
        Assert.Empty(Members(cleared, "out"));
        Assert.False(WorldStateTransforms.TryApply(definition, new StateTransform.BoardCombine("out", BoardCombineOp.Shift, Left: "white", Direction: "UP"), WorldPrincipal.Console, 0, "test", out _, out var badDirection));
        Assert.Contains("does not declare", badDirection);
        Assert.False(WorldStateTransforms.TryApply(definition, new StateTransform.BoardCombine("out", BoardCombineOp.Or, Left: "white"), WorldPrincipal.Console, 0, "test", out _, out _));
        Assert.False(WorldStateTransforms.TryApply(definition, new StateTransform.BoardCombine("out", BoardCombineOp.Copy, Left: "white", Value: 0), WorldPrincipal.Console, 0, "test", out _, out var emptyValue));
        Assert.Contains("empty", emptyValue);
    }

    [Fact]
    public void TransfersPreserveDuplicateValuedTokenIdentitiesAndPileOrder() {
        var definition = Document(
            new(Name("cards"), CellKind.Int, Cells: [Cell("a",7),Cell("b",7)]),
            new(Name("deck"), CellKind.Bool, Cells: [Cell("a"),Cell("b")], Domain: new StateDomain.KeysOf(CellName.Parse("cards"), Ordered: true)),
            new(Name("hand"), CellKind.Bool, Capacity: 1, Cells: [], Domain: new StateDomain.KeysOf(CellName.Parse("cards"), Ordered: true)));
        var operation = new StateTransform.Transfer("deck", "hand", ZoneSelector.First);
        Assert.True(WorldStateTransforms.TryApply(definition, operation, WorldPrincipal.Console, 0, "test", out var changed, out var reason), reason);
        Assert.Equal("a", Assert.Single(Find(changed, "hand").Cells!).Key.Value);
        Assert.Equal("b", Assert.Single(Find(changed, "deck").Cells!).Key.Value);
        Assert.False(WorldStateTransforms.TryApply(changed, operation, WorldPrincipal.Console, 0, "test", out var refused, out _));
        Assert.Same(changed, refused);
        Assert.True(WorldStateTransforms.TryApply(definition, new StateTransform.Transfer("deck", "deck", ZoneSelector.First), WorldPrincipal.Console, 0, "test", out var reordered, out reason), reason);
        Assert.Equal(new[] { "b", "a" }, Find(reordered, "deck").Cells!.Select(c => c.Key.Value));
    }

    // moveToken (pathfind + allowance debit + baked occupancy, one opaque StateTransform) is retired: the same
    // shape is now ordinary authoring over three already-general primitives — $board:pathCost's own live target (a
    // '$cell:<row>:<key>' indirection, not a compile-time literal), an authored occupancy board a rule maintains
    // itself, and a Transaction bundling the affordability gate's own cost expression, the position write, and the
    // occupancy/terrain updates atomically. THE LAW: the transaction fires (position advances, allowance debits by
    // the exact path cost, occupancy and terrain both move with the token) only while the live pathCost stays within
    // the live allowance; an unaffordable request leaves every row exactly as it was — the control that raising the
    // allowance is the only thing that flips the outcome is what proves the gate reads the cost live rather than
    // baking a stale one at compile time.
    [Fact]
    public void APathCostTransactionMovesATokenUnderAnAllowanceAndRefusesWhenCostExceedsIt() {
        WorldDefinition Scenario(long allowance) => Document(
            new(Name("position"), CellKind.Int, Capacity: 1, Cells: [Cell("0", 0)]),
            new(Name("destination"), CellKind.Int, Capacity: 1, Cells: [Cell("0", 2)]),
            new(Name("allowance"), CellKind.Int, Capacity: 1, Cells: [Cell("0", allowance)]),
            new(Name("terrain"), CellKind.Int, Domain: new StateDomain.CellsOf("map", Empty: 1)),
            new(Name("occupancy"), CellKind.Int, Domain: new StateDomain.CellsOf("map", Empty: 0), Cells: [Cell("0", 1)])
        ) with {
            Rules = [new(Name("move"), Effects: [new ActionEffect.Transaction([
                new TransactionStep.AddCell("allowance", Key: "0", Expression: new([
                    new ValueToken.State("$board:pathCost:terrain:cell:destination:0:100:16", Key: "$cell:position:0"),
                    new ValueToken.Negate(),
                ])),
                new TransactionStep.SetCell("occupancy", Key: "$cell:position:0", Value: 0),
                new TransactionStep.SetCell("terrain", Key: "$cell:position:0", Value: 1),
                new TransactionStep.SetCell("position", Key: "0", FromState: "destination", FromKey: "0"),
                new TransactionStep.SetCell("occupancy", Key: "$cell:position:0", Value: 1),
                new TransactionStep.SetCell("terrain", Key: "$cell:position:0", Value: -1),
            ])], Mode: ActionTriggerMode.Edge, Gate: new ActionPredicate.All([
                new ActionPredicate.CompareState("position", ActionStateComparison.NotEqual, Key: "0", ComparandState: "destination", ComparandKey: "0"),
                new ActionPredicate.CompareState("$board:pathCost:terrain:cell:destination:0:100:16", ActionStateComparison.LessOrEqual, Key: "$cell:position:0", ComparandState: "allowance", ComparandKey: "0"),
            ]))],
        };

        // Cell 0 to cell 2 on the 4-wide grid is two due-east steps at the uniform cost-1 terrain: affordable at
        // exactly 2, not at 1.
        using (var fixture = Fixtures.FreshServer(definition: Scenario(allowance: 1))) {
            fixture.Step();
            Assert.Equal(0, Find(fixture.Server.Definition, "position").Cells![0].Value);
            Assert.Equal(1, Find(fixture.Server.Definition, "allowance").Cells![0].Value);
            Assert.Equal(1, Find(fixture.Server.Definition, "occupancy").Cells!.Single(c => c.Key.Value == "0").Value);
        }

        // Control: the identical request succeeds once the allowance covers the live cost — the gate tracks the
        // cost, not a value frozen at compile time.
        using (var fixture = Fixtures.FreshServer(definition: Scenario(allowance: 2))) {
            fixture.Step();
            Assert.Equal(2, Find(fixture.Server.Definition, "position").Cells![0].Value);
            Assert.Equal(0, Find(fixture.Server.Definition, "allowance").Cells![0].Value);
            Assert.Equal(0, Find(fixture.Server.Definition, "occupancy").Cells!.Single(c => c.Key.Value == "0").Value);
            Assert.Equal(1, Find(fixture.Server.Definition, "occupancy").Cells!.Single(c => c.Key.Value == "2").Value);
        }
    }

    [Fact]
    public void RuleTransactionRollsBackTransferAndPhaseWhenLaterEffectRefuses() {
        var definition = Document(
            new(Name("cards"), CellKind.Int, Cells: [Cell("a",7)]),
            new(Name("deck"), CellKind.Bool, Cells: [Cell("a")], Domain: new StateDomain.KeysOf(CellName.Parse("cards"), Ordered: true)),
            new(Name("hand"), CellKind.Bool, Cells: [], Domain: new StateDomain.KeysOf(CellName.Parse("cards"), Ordered: true)),
            Row("failed", new StateCell(WorldStateRow.SlotKey, 0))) with {
            Rules = [new(Name("atomic"), Effects: [new ActionEffect.Transaction([
                new TransactionStep.TransformStateStep(new StateTransform.Transfer("deck", "hand", ZoneSelector.First)),
                new TransactionStep.RemoveCell("hand", "missing")
            ], OnFailure: [new TransactionStep.SetCell("failed", Value: 1)])])],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();
        Assert.Single(Find(fixture.Server.Definition, "deck").Cells!);
        Assert.Empty(Find(fixture.Server.Definition, "hand").Cells!);
        Assert.Equal(1, Find(fixture.Server.Definition, "failed").Cells![0].Value);
    }

    [Fact]
    public void AttacksQueryStopsAtTheFirstBlockerAndOnlyMatchesItsOwnValue() {
        var definition = Document(new WorldStateRow(Name("board"), CellKind.Int, Domain: new StateDomain.CellsOf("map")));
        var topology = TopologyCompilation.Find(definition.StateRaw, "map")!;
        var east = topology.Direction("E");
        var south = topology.Direction("S");
        const int origin = 4; // (x=0, z=1) on the 4-wide grid
        const int rookCell = 6; // two steps east of the origin
        var values = new long[topology.CellCount];
        values[rookCell] = 4;
        var attacksEast = new BoardAttacksQuery(topology, lower: 4, upper: 4, directions: [east]);
        Assert.Equal(1, BoardQueries.Evaluate(attacksEast, values, 0, origin));
        // Control: the same ray with no qualifying piece at all must read a miss, not a stale hit.
        Assert.Equal(0, BoardQueries.Evaluate(attacksEast, new long[topology.CellCount], 0, origin));
        // Control: the rook's cell holds a code outside the authored range -- geometry alone must not be enough.
        var attacksWrongValue = new BoardAttacksQuery(topology, lower: 5, upper: 5, directions: [east]);
        Assert.Equal(0, BoardQueries.Evaluate(attacksWrongValue, values, 0, origin));
        // Control: the piece sits east, not south -- an authored direction that never reaches it must read a miss.
        var attacksSouthOnly = new BoardAttacksQuery(topology, lower: 4, upper: 4, directions: [south]);
        Assert.Equal(0, BoardQueries.Evaluate(attacksSouthOnly, values, 0, origin));
        // Several authored directions OR together: south alone misses, but south-or-east finds the rook via east.
        var attacksEitherWay = new BoardAttacksQuery(topology, lower: 4, upper: 4, directions: [south, east]);
        Assert.Equal(1, BoardQueries.Evaluate(attacksEitherWay, values, 0, origin));
        // Control: a non-qualifying piece one step closer blocks the ray -- if the walk did not stop at the first
        // occupied cell, this would wrongly still see the rook past it.
        var blocked = (long[])values.Clone();
        blocked[origin + 1] = 9;
        Assert.Equal(0, BoardQueries.Evaluate(attacksEast, blocked, 0, origin));
    }
}
