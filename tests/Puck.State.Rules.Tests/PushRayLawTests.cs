using Puck.Assets.Documents;
using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>Contract laws for the pool-token push along a topology ray.</summary>
public sealed class PushRayLawTests {
    private static readonly CellName CellField = TransformFixture.Name(value: "cell");
    private static readonly CellName KindField = TransformFixture.Name(value: "kind");
    private static readonly CellName PoolName = TransformFixture.Name(value: "pieces");

    [Fact]
    public void ASingleMoverAdvancesIntoItsEmptyNeighbour() {
        var fixture = Arrange((0, 1));

        Assert.True(condition: Apply(fixture, origin: 0, moved: out var moved, refusal: out var refusal), userMessage: refusal.Reason);
        Assert.True(condition: moved);
        Assert.Equal(expected: [1L], actual: Cells(fixture: fixture));
    }
    [Fact]
    public void AFreeRunMovesEveryOccupantOneCellAndKeepsStableSlotOrder() {
        var fixture = Arrange((0, 1), (1, 1), (1, 1));

        Assert.True(condition: Apply(fixture, origin: 0, moved: out var moved, refusal: out var refusal), userMessage: refusal.Reason);
        Assert.True(condition: moved);
        Assert.Equal(expected: [1L, 2L, 2L], actual: Cells(fixture: fixture));
    }
    [Fact]
    public void AFullPoolRunIndexesEveryOccupantAndKeepsStableSlotOrder() {
        var fixture = Arrange((0, 1), (1, 1), (1, 1), (1, 1));

        Assert.True(condition: Apply(fixture, origin: 0, moved: out var moved, refusal: out var refusal), userMessage: refusal.Reason);
        Assert.True(condition: moved);
        Assert.Equal(expected: [1L, 2L, 2L, 2L], actual: Cells(fixture: fixture));
    }
    [Fact]
    public void ARejectedOccupantAtTheSameCellBlocksTheWholePush() {
        var fixture = Arrange((0, 1), (1, 1), (1, 2));
        var before = fixture.Arena.ComputeHash();

        Assert.False(condition: Apply(fixture, origin: 0, moved: out var moved, refusal: out var refusal));
        Assert.False(condition: moved);
        Assert.Equal(expected: TransformRefusal.PushRayBlocked, actual: refusal.Code);
        Assert.Equal(expected: before, actual: fixture.Arena.ComputeHash());
    }
    [Fact]
    public void APassableOccupantTerminatesTheRunAndRemainsUnderTheMover() {
        var fixture = Arrange((0, 1), (1, 3));

        Assert.True(condition: Apply(fixture, origin: 0, moved: out var moved, refusal: out var refusal), userMessage: refusal.Reason);
        Assert.True(condition: moved);
        Assert.Equal(expected: [1L, 1L], actual: Cells(fixture: fixture));
    }
    [Fact]
    public void APassableOccupantAfterAPushableOneStaysInStableSlotOrder() {
        var fixture = Arrange((0, 1), (1, 1), (1, 3));

        Assert.True(condition: Apply(fixture, origin: 0, moved: out var moved, refusal: out var refusal), userMessage: refusal.Reason);
        Assert.True(condition: moved);
        Assert.Equal(expected: [1L, 2L, 1L], actual: Cells(fixture: fixture));
    }
    [Fact]
    public void ACellOutsideTheTopologyDoesNotAliasARayCellAfterIndexing() {
        var fixture = Arrange((0, 1), (4_294_967_297L, 1), (1, 3));

        Assert.True(condition: Apply(fixture, origin: 0, moved: out var moved, refusal: out var refusal), userMessage: refusal.Reason);
        Assert.True(condition: moved);
        Assert.Equal(expected: [1L, 4_294_967_297L, 1L], actual: Cells(fixture: fixture));
    }
    [Fact]
    public void AStopOccupantBlocksEvenWhenThePushSelectorAlsoAcceptsIt() {
        var fixture = Arrange((0, 1), (1, 1));
        var before = fixture.Arena.ComputeHash();

        fixture = fixture with { Transform = fixture.Transform with { StopPattern = fixture.Transform.PushPattern } };

        Assert.False(condition: Apply(fixture, origin: 0, moved: out var moved, refusal: out var refusal));
        Assert.False(condition: moved);
        Assert.Equal(expected: TransformRefusal.PushRayBlocked, actual: refusal.Code);
        Assert.Equal(expected: before, actual: fixture.Arena.ComputeHash());
    }
    [Fact]
    public void AFullRayToTheBoardEdgeRefusesWithoutMoving() {
        var fixture = Arrange((0, 1), (1, 1), (2, 1), (3, 1));
        var before = fixture.Arena.ComputeHash();

        Assert.False(condition: Apply(fixture, origin: 0, moved: out _, refusal: out var refusal));
        Assert.Equal(expected: TransformRefusal.PushRayBlocked, actual: refusal.Code);
        Assert.Equal(expected: before, actual: fixture.Arena.ComputeHash());
    }
    [Fact]
    public void ARayEnteringACycleBeyondItsOriginRefusesWithoutCollectingDuplicateMovers() {
        var fixture = Arrange((0, 1), (1, 1), (2, 1));
        var graph = new LatticeTopology.Graph(Name: "loop", Origin: new DocumentVector3(x: 0, y: 0, z: 0), CellSize: 1,
            Cells: [new GraphCell("a", new DocumentVector3(x: 0, y: 0, z: 0)), new GraphCell("b", new DocumentVector3(x: 1, y: 0, z: 0)), new GraphCell("c", new DocumentVector3(x: 2, y: 0, z: 0)), new GraphCell("d", new DocumentVector3(x: 3, y: 0, z: 0))],
            Directions: [new GraphDirection(Name: "next", Opposite: "next")],
            Edges: [new GraphEdge("a", "b", CellName.Parse(candidate: "next"), OneWay: true), new GraphEdge("b", "c", CellName.Parse(candidate: "next"), OneWay: true), new GraphEdge("c", "b", CellName.Parse(candidate: "next"), OneWay: true)]);
        var topology = TopologyCompilation.Find(lattices: [graph], name: "loop")!;

        fixture = fixture with { Transform = fixture.Transform with { Topology = topology, Direction = 0 } };
        var before = fixture.Arena.ComputeHash();

        Assert.False(condition: Apply(fixture, origin: 0, moved: out _, refusal: out var refusal));
        Assert.Equal(TransformRefusal.PushRayBlocked, refusal.Code);
        Assert.Equal(before, fixture.Arena.ComputeHash());
    }
    [Fact]
    public void AFreePushAllocatesNothingAfterWarmup() {
        var fixture = Arrange((0, 1), (1, 1), (2, 1));

        for (var warm = 0; (warm < 16); warm++) {
            var mark = fixture.Arena.BeginScope();

            _ = Apply(fixture, origin: 0, moved: out _, refusal: out _);
            fixture.Arena.Rewind(mark: mark);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var allApplied = true;

        for (var sample = 0; (sample < 64); sample++) {
            var mark = fixture.Arena.BeginScope();

            allApplied &= Apply(fixture, origin: 0, moved: out _, refusal: out _);
            fixture.Arena.Rewind(mark: mark);
        }
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.True(condition: allApplied);
        Assert.Equal(actual: allocated, expected: 0L);
    }
    [Fact]
    public void AFull256SlotPushAllocatesNothingAfterWarmup() {
        var pieces = new (long Cell, long Kind)[256];

        for (var slot = 0; (slot < (pieces.Length - 1)); slot++) {
            pieces[slot] = (Cell: slot, Kind: 1);
        }
        pieces[^1] = (Cell: (pieces.Length - 1), Kind: 3);
        var fixture = Arrange(capacity: pieces.Length, width: pieces.Length, pieces: pieces);

        for (var warm = 0; (warm < 8); warm++) {
            var mark = fixture.Arena.BeginScope();

            _ = Apply(fixture, origin: 0, moved: out _, refusal: out _);
            fixture.Arena.Rewind(mark: mark);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var allApplied = true;

        for (var sample = 0; (sample < 16); sample++) {
            var mark = fixture.Arena.BeginScope();

            allApplied &= Apply(fixture, origin: 0, moved: out _, refusal: out _);
            fixture.Arena.Rewind(mark: mark);
        }
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.True(condition: allApplied);
        Assert.Equal(actual: allocated, expected: 0L);
    }
    [Fact]
    public void TheCompiledForEachPathCarriesAllThreePatternsAndTheTypedOrigin() {
        var fixture = Arrange((0, 1), (1, 1));
        var context = new RuleCompileContext(section: fixture.Section, catalog: fixture.Arena.Catalog, tables: null, patterns: fixture.Patterns, generators: null, simulationRateHz: 60, vocabulary: RuleVocabulary.Core);
        var compiled = RuleCompiler.Compile(context: context, rule: new Rule(Name: TransformFixture.Name(value: "push"), Effects: [new ActionEffect.ForEachPool(
            Pool: PoolName.Value,
            Binding: TransformFixture.Name(value: "mover"),
            Effects: [new ActionEffect.TransformState(Transform: new StateTransform.PushRay(
                Pool: PoolName,
                Cell: CellField,
                Value: KindField,
                From: StateChannelRef.OfBindingField(binding: "mover", field: "cell"),
                Topology: TransformFixture.Name(value: "map"),
                Direction: CellName.Parse(candidate: "E"),
                Pattern: "pushable",
                PushPattern: "push",
                StopPattern: "stop",
                Empty: 0
            ))]
        )]));
        var each = Assert.IsType<ForEachPoolEffect>(@object: Assert.Single(collection: compiled.Effects));
        var effect = Assert.IsType<TransformStateEffect>(@object: Assert.Single(collection: each.Effects));
        var push = Assert.IsType<ArenaTransform.PushRay>(@object: effect.Arena);

        Assert.Equal(expected: 1, actual: push.PushPattern.LongestAcceptedPrefix(values: [1L]));
        Assert.Equal(expected: 1, actual: push.StopPattern.LongestAcceptedPrefix(values: [2L]));
        Assert.Equal(expected: 2, actual: push.Pattern.LongestAcceptedPrefix(values: [1L, 0L]));
        Assert.True(condition: (effect.Price.Units >= ((12L * fixture.Pool.Capacity) + (3L * push.Topology.CellCount))));
    }

    private static bool Apply(Fixture fixture, long origin, out bool moved, out EffectRefusal refusal) => ArenaTransforms.TryApply(
        context: new ArenaTransformContext(Arena: fixture.Arena, Time: ArenaTime.Origin),
        transform: fixture.Transform,
        binding: new ArenaTransformBinding(bindsInstance: true, instance: fixture.Mover),
        moved: out moved,
        refusal: out refusal
    );
    private static long[] Cells(Fixture fixture) {
        var handles = fixture.Arena.SnapshotPool(poolOrdinal: fixture.Pool.Ordinal);
        var cells = new long[handles.Count];

        for (var index = 0; (index < handles.Count); index++) {
            _ = fixture.Arena.TryRead(handle: handles[index], fieldOrdinal: 0, value: out var value);
            cells[index] = value.AsInt;
        }
        return cells;
    }
    private static Fixture Arrange(params (long Cell, long Kind)[] pieces) => Arrange(capacity: 4, width: 4, pieces: pieces);
    private static Fixture Arrange(int capacity, int width, params (long Cell, long Kind)[] pieces) {
        var seeds = new StatePoolSeed[pieces.Length];

        for (var index = 0; (index < pieces.Length); index++) {
            seeds[index] = new StatePoolSeed(Slot: index, Values: [
                new StatePoolValue(Field: CellField, Value: CellValue.Int(value: pieces[index].Cell)),
                new StatePoolValue(Field: KindField, Value: CellValue.Int(value: pieces[index].Kind)),
            ]);
        }
        var section = new StateSection(
            Lattices: [new LatticeTopology.Grid(Name: TransformFixture.Name(value: "map"), Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: width, Depth: 1)],
            Records: [new StateRecord(Name: TransformFixture.Name(value: "Piece"), Fields: [
                new StatePoolField(Name: CellField, Kind: CellKind.Int, Min: 0, Max: long.MaxValue),
                new StatePoolField(Name: KindField, Kind: CellKind.Int),
            ])],
            Pools: [new StatePool(Name: PoolName, Record: TransformFixture.Name(value: "Piece"), Capacity: capacity, Initial: seeds)]
        );
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(catalog: catalog, options: null, section: section, time: ArenaTime.Origin);

        Assert.True(condition: catalog.TryGetPool(name: PoolName, pool: out var pool));
        var runRow = new PatternRow(
                Name: TransformFixture.Name(value: "pushable"),
                Kind: CellKind.Int,
                Symbols: [new PatternSymbol(Name: TransformFixture.Name(value: "push"), Min: 1, Max: 1), new PatternSymbol(Name: TransformFixture.Name(value: "empty"), Min: 0, Max: 0), new PatternSymbol(Name: TransformFixture.Name(value: "passable"), Min: 3, Max: 3)],
                Pattern: new PatternNode.Sequence(Items: [new PatternNode.Plus(Item: new PatternNode.Symbol(Name: "push")), new PatternNode.Choice(Items: [new PatternNode.Symbol(Name: "empty"), new PatternNode.Plus(Item: new PatternNode.Symbol(Name: "passable"))])])
            );
        var pushRow = new PatternRow(
                Name: TransformFixture.Name(value: "push"),
                Kind: CellKind.Int,
                Symbols: [new PatternSymbol(Name: TransformFixture.Name(value: "push"), Min: 1, Max: 1)],
                Pattern: new PatternNode.Symbol(Name: "push")
            );
        var stopRow = new PatternRow(
                Name: TransformFixture.Name(value: "stop"),
                Kind: CellKind.Int,
                Symbols: [new PatternSymbol(Name: TransformFixture.Name(value: "stop"), Min: 2, Max: 2)],
                Pattern: new PatternNode.Symbol(Name: "stop")
            );
        var patterns = new[] { runRow, pushRow, stopRow };

        Assert.True(condition: CompiledPattern.TryCompile(
            compiled: out var pattern,
            reason: out var reason,
            row: runRow
        ), userMessage: reason);
        Assert.True(condition: CompiledPattern.TryCompile(
            compiled: out var push,
            reason: out reason,
            row: pushRow
        ), userMessage: reason);
        Assert.True(condition: CompiledPattern.TryCompile(
            compiled: out var stop,
            reason: out reason,
            row: stopRow
        ), userMessage: reason);
        var topology = TopologyCompilation.Find(lattices: section.Lattices!, name: "map")!;
        var mover = arena.SnapshotPool(poolOrdinal: pool!.Ordinal).First(predicate: handle => (arena.TryRead(fieldOrdinal: 0, handle: handle, value: out var value) && (value.AsInt == 0)));

        return new Fixture(
            Arena: arena,
            Mover: mover,
            Pool: pool!,
            Patterns: patterns,
            Section: section,
            Transform: new ArenaTransform.PushRay(PoolOrdinal: pool!.Ordinal, CellFieldOrdinal: 0, ValueFieldOrdinal: 1, OriginBindingSlot: 0, Topology: topology, Direction: topology.Direction(token: "E"), Pattern: pattern!, PushPattern: push!, StopPattern: stop!, Empty: 0)
        );
    }

    private sealed record Fixture(StateArena Arena, StateInstanceHandle Mover, StatePoolDescriptor Pool, PatternRow[] Patterns, StateSection Section, ArenaTransform.PushRay Transform);
}
