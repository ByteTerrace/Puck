using Puck.Assets.Documents;
using Puck.Physics.Motion;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class DiscreteStateLawTests {
    [Fact]
    public void HexAndRingAddressingAreBoundedAndReciprocal() {
        var hex = new WorldStateLatticeTopology.Hex("hex", new DocumentVector3(0,0,0), 1, Radius: 2);
        var ring = new WorldStateLatticeTopology.Ring("ring", new DocumentVector3(0,0,0), 1, Width: 5);
        var state = new WorldStateSection(Lattices: [hex, ring]);
        var topology = WorldTopologyCompilation.Find(state, "hex")!;
        Assert.Equal(19, topology.CellCount);
        for (var cell = 0; cell < topology.CellCount; cell++) {
            for (var direction = 0; direction < 6; direction++) {
                var neighbour = topology.Neighbour(cell, direction);
                if (neighbour >= 0) { Assert.Equal(cell, topology.Neighbour(neighbour, (direction + 3) % 6)); }
            }
        }
        var cycle = WorldTopologyCompilation.Find(state, "ring")!;
        Assert.Equal(4, cycle.Neighbour(0, cycle.Direction("backward")));
        Assert.Equal(0, cycle.Neighbour(4, cycle.Direction("forward")));
        Assert.False(WorldTopologyCompilation.TryValidate(hex with { Radius = WorldTopologyCompilation.MaxHexRadius + 1 }, out var radiusReason));
        Assert.Contains($"0..{WorldTopologyCompilation.MaxHexRadius}", radiusReason);
        Assert.True(WorldTopologyCompilation.TryValidate(hex with { Radius = WorldTopologyCompilation.MaxHexRadius }, out _));
    }

    [Fact]
    public void WarmBoardReadsAndPathQueriesAllocateNothing() {
        var state = new WorldStateSection(Lattices: [Grid()]);
        var topology = WorldTopologyCompilation.Find(state, "map")!;
        var row = new WorldStateRow(Name("terrain"), CellKind.Int, Cells: [Cell("1",2)], Board: new("map",1));
        var query = new BoardPathCostQuery(topology, target: 15, maxCost: 100, maxVisits: 16);
        Span<long> values = stackalloc long[16];
        WorldBoardQueries.Read(row, topology, values);
        _ = WorldBoardQueries.Evaluate(query, values, 1, 0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var repeat = 0; repeat < 100; repeat++) {
            _ = WorldTopologyCompilation.FindPhysical(state);
            WorldBoardQueries.Read(row, topology, values);
            _ = WorldBoardQueries.Evaluate(query, values, 1, 0);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void APhaseOfTaggedRowRefusesAnUnguardedTransformAndAMatchingGuardAdvancesTheGenerationOnSuccess() {
        var definition = Document(
            new(Name("turn"), CellKind.Int, Phase: new()),
            new(Name("cards"), CellKind.Int, Cells: [Cell("a")], Tokens: new()),
            new(Name("deck"), CellKind.Bool, Cells: [Cell("a")], Zone: new("cards"), PhaseOf: "turn"),
            new(Name("hand"), CellKind.Bool, Zone: new("cards"), PhaseOf: "turn"));
        Assert.True(WorldStateTransforms.CanAct(definition, new("turn", 0), WorldPrincipal.Console));
        Assert.False(WorldStateTransforms.CanAct(definition, new("turn", 1), WorldPrincipal.Console));
        using var fixture = Fixtures.FreshServer(definition: definition);
        var operation = new WorldStateTransform.Transfer("deck", "hand", WorldZoneSelector.First);
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

    private static WorldCellName Name(string value) => WorldCellName.Parse(value);
    private static WorldStateCell Cell(string key, long value = 1) => new(Name(key), value);
    private static WorldStateRow Row(string name, params WorldStateCell[] cells) => new(Name(name), CellKind.Int, Cells: cells);
    private static WorldStateLatticeTopology.Grid Grid(int width = 4, int depth = 4, WorldTopologyWrap wrap = WorldTopologyWrap.None) => new("map", new DocumentVector3(0, 0, 0), 1, width, depth, Wrap: wrap);
    private static WorldDefinition Document(params WorldStateRow[] rows) => Fixtures.BuildDocument() with { StateRaw = new(World: rows, Lattices: [Grid()]), Rules = [] };
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(document.State, row)!;

    [Fact]
    public void DiscreteTopologiesDoNotAllocatePhysicalFieldsAndWrappedRaysTerminate() {
        var definition = Document(new WorldStateRow(Name("board"), CellKind.Int, Board: new("map")));
        Assert.Null(definition.Fields);
        var topology = WorldTopologyCompilation.Find(definition.StateRaw, "map")!;
        Assert.Equal(-1, topology.Neighbour(0, topology.Direction("N")));
        Assert.Equal(5, topology.Neighbour(0, topology.Direction("SE")));
        var wrapped = WorldTopologyCompilation.Find(new(Lattices: [Grid(wrap: WorldTopologyWrap.Both)]), "map")!;
        Assert.Equal(12, wrapped.Neighbour(0, wrapped.Direction("N")));

        // A ray over a fully wrapped board whose every cell matches the pattern reads the pattern-engine's own
        // ReadRay, which breaks the moment it returns to its own origin: the walk still terminates (rather than
        // looping forever) with no blocker found, since the pattern accepts the whole (CellCount - 1)-cell word —
        // the wrap-termination guarantee a $match-over-a-ray read now leans on where a dedicated ray query once did.
        var runOfOnes = new WorldPatternRow(Name("runOfOnes"), CellKind.Int, Symbols: [new(Name("one"), 1, 1)], Pattern: new WorldPatternNode.Star(new WorldPatternNode.Symbol("one")));
        var wrappedDefinition = Fixtures.BuildDocument() with {
            StateRaw = new(World: [new WorldStateRow(Name("board"), CellKind.Int, Board: new("map", Empty: 1)), new WorldStateRow(Name("blocker"), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 0L)])], Lattices: [Grid(wrap: WorldTopologyWrap.Both)]),
            PatternsRaw = [runOfOnes],
            Rules = [new WorldRule(Name("read"), [new ActionEffect.SetState(State: "blocker", FromState: "$match:runOfOnes:board:E:cell", FromKey: "0")])],
        };
        using var fixture = Fixtures.FreshServer(definition: wrappedDefinition);
        fixture.Step();
        Assert.Equal(-1L, WorldDefinitionRows.FindCell(Find(fixture.Server.Definition, "blocker").Cells, WorldStateRow.SlotKey)!.Value);
    }

    private static WorldPatternRow CapturePattern() => new(Name("capture"), CellKind.Int,
        [new(Name("through"), 2, 2), new(Name("until"), 1, 1)],
        new WorldPatternNode.Sequence([new WorldPatternNode.Plus(new WorldPatternNode.Symbol("through")), new WorldPatternNode.Symbol("until")]));

    [Fact]
    public void BracketedRayCommitsTheAcceptedPrefixOrRefusesOnAnEmptyOne() {
        var definition = Document(new WorldStateRow(Name("board"), CellKind.Int, Cells: [Cell("0",1),Cell("1",2),Cell("2",2),Cell("3",1)], Board: new("map"))) with {
            PatternsRaw = [CapturePattern()],
        };
        Assert.True(CompiledWorldPatterns.TryCompileAll(definition, out var patterns, []));
        var operation = new WorldStateTransform.SetRay("board", "0", "E", "capture", 1);
        Assert.True(WorldStateTransforms.TryApply(definition, operation, WorldPrincipal.World, 1, "test", out var changed, out var reason, patterns), reason);
        Assert.All(Find(changed, "board").Cells!, c => Assert.Equal(1, c.Value));
        Assert.Equal(2, Find(definition, "board").Cells![1].Value);
        // Control: a board holding only "through" values never reaches the required "until" terminator, so the
        // longest accepted prefix is empty and the whole write is refused.
        var open = Document(new WorldStateRow(Name("board"), CellKind.Int, Cells: [Cell("0",1),Cell("1",2),Cell("2",2),Cell("3",2)], Board: new("map"))) with {
            PatternsRaw = [CapturePattern()],
        };
        Assert.True(CompiledWorldPatterns.TryCompileAll(open, out var openPatterns, []));
        Assert.False(WorldStateTransforms.TryApply(open, operation, WorldPrincipal.World, 1, "test", out var refused, out _, openPatterns));
        Assert.Same(open, refused);
    }

    [Fact]
    public void TransfersPreserveDuplicateValuedTokenIdentitiesAndPileOrder() {
        var definition = Document(
            new(Name("cards"), CellKind.Int, Cells: [Cell("a",7),Cell("b",7)], Tokens: new()),
            new(Name("deck"), CellKind.Bool, Cells: [Cell("a"),Cell("b")], Zone: new("cards")),
            new(Name("hand"), CellKind.Bool, Capacity: 1, Cells: [], Zone: new("cards")));
        var operation = new WorldStateTransform.Transfer("deck", "hand", WorldZoneSelector.First);
        Assert.True(WorldStateTransforms.TryApply(definition, operation, WorldPrincipal.Console, 0, "test", out var changed, out var reason), reason);
        Assert.Equal("a", Assert.Single(Find(changed, "hand").Cells!).Key.Value);
        Assert.Equal("b", Assert.Single(Find(changed, "deck").Cells!).Key.Value);
        Assert.False(WorldStateTransforms.TryApply(changed, operation, WorldPrincipal.Console, 0, "test", out var refused, out _));
        Assert.Same(changed, refused);
        Assert.True(WorldStateTransforms.TryApply(definition, new WorldStateTransform.Transfer("deck", "deck", WorldZoneSelector.First), WorldPrincipal.Console, 0, "test", out var reordered, out reason), reason);
        Assert.Equal(new[] { "b", "a" }, Find(reordered, "deck").Cells!.Select(c => c.Key.Value));
    }

    // moveToken (pathfind + allowance debit + baked occupancy, one opaque WorldStateTransform) is retired: the same
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
            new(Name("terrain"), CellKind.Int, Board: new("map", Empty: 1)),
            new(Name("occupancy"), CellKind.Int, Board: new("map", Empty: 0), Cells: [Cell("0", 1)])
        ) with {
            Rules = [new(Name("move"), Effects: [new ActionEffect.Transaction([
                new WorldTransactionStep.AddCell("allowance", Key: "0", Expression: new([
                    new WorldValueToken.State("$board:pathCost:terrain:cell:destination:0:100:16", Key: "$cell:position:0"),
                    new WorldValueToken.Negate(),
                ])),
                new WorldTransactionStep.SetCell("occupancy", Key: "$cell:position:0", Value: 0),
                new WorldTransactionStep.SetCell("terrain", Key: "$cell:position:0", Value: 1),
                new WorldTransactionStep.SetCell("position", Key: "0", FromState: "destination", FromKey: "0"),
                new WorldTransactionStep.SetCell("occupancy", Key: "$cell:position:0", Value: 1),
                new WorldTransactionStep.SetCell("terrain", Key: "$cell:position:0", Value: -1),
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
            new(Name("cards"), CellKind.Int, Cells: [Cell("a",7)], Tokens: new()),
            new(Name("deck"), CellKind.Bool, Cells: [Cell("a")], Zone: new("cards")),
            new(Name("hand"), CellKind.Bool, Cells: [], Zone: new("cards")),
            Row("failed", new WorldStateCell(WorldStateRow.SlotKey, 0))) with {
            Rules = [new(Name("atomic"), Effects: [new ActionEffect.Transaction([
                new WorldTransactionStep.TransformStateStep(new WorldStateTransform.Transfer("deck", "hand", WorldZoneSelector.First)),
                new WorldTransactionStep.RemoveCell("hand", "missing")
            ], OnFailure: [new WorldTransactionStep.SetCell("failed", Value: 1)])])],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();
        Assert.Single(Find(fixture.Server.Definition, "deck").Cells!);
        Assert.Empty(Find(fixture.Server.Definition, "hand").Cells!);
        Assert.Equal(1, Find(fixture.Server.Definition, "failed").Cells![0].Value);
    }

    [Fact]
    public void AttacksQueryStopsAtTheFirstBlockerAndOnlyMatchesItsOwnValue() {
        var definition = Document(new WorldStateRow(Name("board"), CellKind.Int, Board: new("map")));
        var topology = WorldTopologyCompilation.Find(definition.StateRaw, "map")!;
        var east = topology.Direction("E");
        var south = topology.Direction("S");
        const int origin = 4; // (x=0, z=1) on the 4-wide grid
        const int rookCell = 6; // two steps east of the origin
        var values = new long[topology.CellCount];
        values[rookCell] = 4;
        var attacksEast = new BoardAttacksQuery(topology, lower: 4, upper: 4, directions: [east]);
        Assert.Equal(1, WorldBoardQueries.Evaluate(attacksEast, values, 0, origin));
        // Control: the same ray with no qualifying piece at all must read a miss, not a stale hit.
        Assert.Equal(0, WorldBoardQueries.Evaluate(attacksEast, new long[topology.CellCount], 0, origin));
        // Control: the rook's cell holds a code outside the authored range -- geometry alone must not be enough.
        var attacksWrongValue = new BoardAttacksQuery(topology, lower: 5, upper: 5, directions: [east]);
        Assert.Equal(0, WorldBoardQueries.Evaluate(attacksWrongValue, values, 0, origin));
        // Control: the piece sits east, not south -- an authored direction that never reaches it must read a miss.
        var attacksSouthOnly = new BoardAttacksQuery(topology, lower: 4, upper: 4, directions: [south]);
        Assert.Equal(0, WorldBoardQueries.Evaluate(attacksSouthOnly, values, 0, origin));
        // Several authored directions OR together: south alone misses, but south-or-east finds the rook via east.
        var attacksEitherWay = new BoardAttacksQuery(topology, lower: 4, upper: 4, directions: [south, east]);
        Assert.Equal(1, WorldBoardQueries.Evaluate(attacksEitherWay, values, 0, origin));
        // Control: a non-qualifying piece one step closer blocks the ray -- if the walk did not stop at the first
        // occupied cell, this would wrongly still see the rook past it.
        var blocked = (long[])values.Clone();
        blocked[origin + 1] = 9;
        Assert.Equal(0, WorldBoardQueries.Evaluate(attacksEast, blocked, 0, origin));
    }
}
