using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class DiscreteStateLawTests {
    [Fact]
    public void HexAndRingAddressingAreBoundedAndReciprocal() {
        var hex = new WorldStateLatticeTopology("hex", new DocumentVector3(0,0,0), 1, 1, 1, Kind: WorldTopologyKind.Hex, Radius: 2);
        var ring = new WorldStateLatticeTopology("ring", new DocumentVector3(0,0,0), 1, 5, 1, Kind: WorldTopologyKind.Ring);
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
        Assert.False(WorldTopologyCompilation.TryValidate(hex with { Radius = 37 }, out _));
    }

    [Fact]
    public void WarmBoardReadsAndPathQueriesAllocateNothing() {
        var state = new WorldStateSection(Lattices: [Grid()]);
        var topology = WorldTopologyCompilation.Find(state, "map")!;
        var row = new WorldStateRow(Name("terrain"), CellKind.Int, Cells: [Cell("1",2)], Board: new("map",1));
        var query = new CompiledWorldBoardQuery(topology, WorldBoardQueryKind.PathCost, Target: 15, MaxCost: 100, MaxVisits: 16);
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
    public void PhaseDeadlineWinsAtTheExactTickAndGuardCannotBeOmitted() {
        var phase = new WorldStatePhase(["console"], [new("act", WorldPhaseMode.Sequential, "act", 1)]);
        var definition = Document(
            new(Name("turn"), CellKind.Int, Phase: phase),
            new(Name("cards"), CellKind.Int, Cells: [Cell("a")], Tokens: new()),
            new(Name("deck"), CellKind.Bool, Cells: [Cell("a")], Zone: new("cards"), PhaseOf: "turn"),
            new(Name("hand"), CellKind.Bool, Zone: new("cards"), PhaseOf: "turn"));
        var deadline = (ulong)definition.SimulationRateHz;
        Assert.True(WorldStateTransforms.CanAct(definition, new("turn",0), WorldPrincipal.Console, deadline - 1));
        Assert.False(WorldStateTransforms.CanAct(definition, new("turn",0), WorldPrincipal.Console, deadline));
        Assert.False(WorldStateTransforms.TryApply(definition, new WorldStateTransform.CompletePhase("turn",0), WorldPrincipal.Console, deadline, "test", out _, out _));
        Assert.True(WorldStateTransforms.TryApply(definition, new WorldStateTransform.CompletePhase("turn", Timeout: true), WorldPrincipal.World, deadline, "test", out _, out _));
        using var fixture = Fixtures.FreshServer(definition: definition);
        var operation = new WorldStateTransform.Transfer("deck", "hand", WorldZoneSelector.First);
        fixture.Server.Submit(new(SubmissionEnvelope.LocalConnectionId, 0, 1, 1, WorldPrincipal.Console,
            new WorldSubmissionPayload.Mutation(new WorldMutation.TransformState(WorldPrincipal.Console, operation))), _ => { });
        fixture.Step();
        Assert.Empty(Find(fixture.Server.Definition, "hand").Cells ?? []);
        fixture.Server.Submit(new(SubmissionEnvelope.LocalConnectionId, 0, 2, 2, WorldPrincipal.Console,
            new WorldSubmissionPayload.Mutation(new WorldMutation.TransformState(WorldPrincipal.Console, operation, new("turn",0)))), _ => { });
        fixture.Step();
        Assert.Single(Find(fixture.Server.Definition, "hand").Cells!);
    }

    private static WorldCellName Name(string value) => WorldCellName.Parse(value);
    private static WorldStateCell Cell(string key, long value = 1) => new(Name(key), value);
    private static WorldStateRow Row(string name, params WorldStateCell[] cells) => new(Name(name), CellKind.Int, Cells: cells);
    private static WorldStateLatticeTopology Grid(int width = 4, int depth = 4, WorldTopologyWrap wrap = WorldTopologyWrap.None) => new("map", new DocumentVector3(0, 0, 0), 1, width, depth, Kind: WorldTopologyKind.Grid, Wrap: wrap);
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
        var query = new CompiledWorldBoardQuery(wrapped, WorldBoardQueryKind.RayCell, Direction: wrapped.Direction("E"));
        Assert.Equal(-1, WorldBoardQueries.Evaluate(query, new long[16], 0, 0));
        var line = new CompiledWorldBoardQuery(wrapped, WorldBoardQueryKind.Line, Length: 4, Value: 1, Exact: true);
        Assert.Equal(1, WorldBoardQueries.Evaluate(line, [1,1,1,1,0,0,0,0,0,0,0,0,0,0,0,0], 0, -1));
        Assert.Equal(0, WorldBoardQueries.Evaluate(line with { Length = 3 }, [1,1,1,1,0,0,0,0,0,0,0,0,0,0,0,0], 0, -1));
    }

    [Fact]
    public void BracketedRayCommitsAllInterveningCellsOrNone() {
        var definition = Document(new WorldStateRow(Name("board"), CellKind.Int, Cells: [Cell("0",1),Cell("1",2),Cell("2",2),Cell("3",1)], Board: new("map")));
        var operation = new WorldStateTransform.SetRay("board", "0", "E", 2, 1, 1);
        Assert.True(WorldStateTransforms.TryApply(definition, operation, WorldPrincipal.World, 1, "test", out var changed, out var reason), reason);
        Assert.All(Find(changed, "board").Cells!, c => Assert.Equal(1, c.Value));
        Assert.Equal(2, Find(definition, "board").Cells![1].Value);
        Assert.False(WorldStateTransforms.TryApply(definition, operation with { Until = 3 }, WorldPrincipal.World, 1, "test", out var refused, out _));
        Assert.Same(definition, refused);
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

    [Fact]
    public void SimultaneousReadinessKeepsOtherParticipantsGenerationValid() {
        var phase = new WorldStatePhase(["seat1", "seat2"], [new("plan", WorldPhaseMode.Together, "resolve"),new("resolve", WorldPhaseMode.Resolution, "plan")]);
        var definition = Document(new WorldStateRow(Name("turn"), CellKind.Int, Phase: phase));
        Assert.True(WorldStateTransforms.TryApply(definition, new WorldStateTransform.CompletePhase("turn", 0), WorldPrincipal.Seat(0), 1, "test", out var ready, out var reason), reason);
        Assert.Equal(0, Find(ready, "turn").Phase!.Sequence);
        Assert.False(WorldStateTransforms.CanAct(ready, new("turn", 0), WorldPrincipal.Seat(0), 1));
        Assert.True(WorldStateTransforms.CanAct(ready, new("turn", 0), WorldPrincipal.Seat(1), 1));
        Assert.True(WorldStateTransforms.TryApply(ready, new WorldStateTransform.CompletePhase("turn", 0), WorldPrincipal.Seat(1), 1, "test", out var resolving, out reason), reason);
        Assert.Equal(1, Find(resolving, "turn").Phase!.Sequence);
        Assert.Equal(1, Find(resolving, "turn").Phase!.Current);
        Assert.False(WorldStateTransforms.CanAct(resolving, new("turn", 0), WorldPrincipal.Seat(0), 1));
        Assert.True(WorldStateTransforms.TryApply(resolving, new WorldStateTransform.CompletePhase("turn"), WorldPrincipal.World, 1, "test", out var nextRound, out reason), reason);
        Assert.Equal(1, Find(nextRound, "turn").Phase!.Round);
    }

    [Fact]
    public void MovementSpendsPointsAtomicallyAndOccupancyBlocksEntry() {
        var definition = Document(
            new(Name("units"), CellKind.Int, Cells: [Cell("a"),Cell("b")], Tokens: new()),
            new(Name("terrain"), CellKind.Int, Board: new("map", Empty: 1)),
            new(Name("positions"), CellKind.Int, Cells: [Cell("a",0),Cell("b",1)], KeysFrom: "units", ValuesFrom: "map"),
            new(Name("points"), CellKind.Int, Cells: [Cell("a",2),Cell("b",2)], KeysFrom: "units"));
        var move = new WorldStateTransform.MoveToken("positions", "a", 4, "terrain", "points", 16);
        Assert.True(WorldStateTransforms.TryApply(definition, move, WorldPrincipal.Console, 1, "test", out var changed, out var reason), reason);
        Assert.Equal(4, Find(changed, "positions").Cells![0].Value);
        Assert.Equal(1, Find(changed, "points").Cells![0].Value);
        Assert.False(WorldStateTransforms.TryApply(definition, move with { Destination = 1 }, WorldPrincipal.Console, 1, "test", out var refused, out _));
        Assert.Same(definition, refused);
        Assert.False(WorldStateTransforms.TryApply(definition, move with { MaxVisits = 1 }, WorldPrincipal.Console, 1, "test", out _, out reason));
        Assert.Contains("budget exhausted", reason);
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
}
