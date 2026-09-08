using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class BoardCombinationLawTests {
    [Theory]
    [InlineData(BoardCombineOp.Copy)]
    [InlineData(BoardCombineOp.Clear)]
    [InlineData(BoardCombineOp.Fill)]
    [InlineData(BoardCombineOp.And)]
    [InlineData(BoardCombineOp.Or)]
    [InlineData(BoardCombineOp.Xor)]
    [InlineData(BoardCombineOp.AndNot)]
    [InlineData(BoardCombineOp.Not)]
    [InlineData(BoardCombineOp.Shift)]
    [InlineData(BoardCombineOp.Image)]
    public void FramesAndLiveMutationsAgreeWithDistinctEmptyValuesAndAliasedTargets(BoardCombineOp operation) {
        var map = new LatticeTopology.Grid("map", new DocumentVector3(0, 0, 0), 1, 2, 2);
        var source = new WorldStateRow(CellName.Parse("source"), CellKind.Int, Domain: new StateDomain.CellsOf("map", Empty: 7), Cells: [new(CellName.Parse("0"), 3)]);
        var target = new WorldStateRow(CellName.Parse("target"), CellKind.Int, Domain: new StateDomain.CellsOf("map"), Cells: [new(CellName.Parse("1"), 2)]);
        var definition = Fixtures.BuildDocument() with { StateRaw = new(World: [source, target], Lattices: [map]) };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var invalid), invalid);
        var topology = WorldTopologyCompilation.Find(definition, "map")!;
        foreach (var destination in new[] { "target", "source" }) {
            var transform = new StateTransform.BoardCombine(destination, operation,
                Left: operation is BoardCombineOp.Fill or BoardCombineOp.Clear ? null : "source",
                Right: operation is BoardCombineOp.And or BoardCombineOp.Or or BoardCombineOp.Xor or BoardCombineOp.AndNot ? "target" : null,
                Direction: operation == BoardCombineOp.Shift ? "E" : null,
                Element: operation == BoardCombineOp.Image ? "-z+x" : null, Value: 9);
            _ = RuleCompiler.CompileEffect(new ActionEffect.TransformState(transform), "combine", new WorldRuleCompileContext(definition));
            var frame = new StateFrame(new FrameLayout(definition.State, _ => topology), definition.State);
            frame.Load(new RowStore(definition.State));
            Assert.True(frame.TryBoardCombine(transform, out var reason), reason);
            Assert.True(WorldStateTransforms.TryApply(definition, transform, WorldPrincipal.World, 0, "law", out var candidate, out reason), reason);
            Assert.True(WorldDefinitionValidator.TryValidateLocally(candidate, out invalid), invalid);
            var dense = new long[4];
            var live = new long[4];
            frame.ReadBoard(definition.State.Single(row => row.Name == destination), topology, dense);
            BoardQueries.Read(candidate.State.Single(row => row.Name == destination), topology, live);
            Assert.Equal(live, dense);
            if (operation == BoardCombineOp.Copy) {
                Assert.Equal([3L, 7L, 7L, 7L], dense);
            }
        }
    }
}
