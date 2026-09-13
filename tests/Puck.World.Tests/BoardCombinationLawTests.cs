using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class BoardCombinationLawTests {
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
    [Theory]
    public void FramesAndLiveMutationsAgreeWithDistinctEmptyValuesAndAliasedTargets(BoardCombineOp operation) {
        var map = new LatticeTopology.Grid(
            "map",
            new DocumentVector3(
                x: 0,
                y: 0,
                z: 0
            ),
            1,
            2,
            2
        );
        var source = new WorldStateRow(
            CellName.Parse(candidate: "source"),
            CellKind.Int,
            Domain: new StateDomain.CellsOf(
                "map",
                Empty: 7
            ),
            Cells: [new(
                    CellName.Parse(candidate: "0"),
                    3
                )]
        );
        var target = new WorldStateRow(
            CellName.Parse(candidate: "target"),
            CellKind.Int,
            Domain: new StateDomain.CellsOf("map"),
            Cells: [new(
                    CellName.Parse(candidate: "1"),
                    2
                )]
        );
        var definition = Fixtures.BuildDocument() with { StateRaw = new(
            World: [source, target],
            Lattices: [map]
        ) };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition,
                reason: out var invalid
            ),
            userMessage: invalid
        );
        var topology = WorldTopologyCompilation.Find(
            definition: definition,
            name: "map"
        )!;

        foreach (var destination in new[] { "target", "source" }) {
            var transform = new StateTransform.BoardCombine(
                destination,
                operation,
                Left: ((operation is BoardCombineOp.Fill or BoardCombineOp.Clear)
                ? null
                : "source"),
                Right: ((operation is BoardCombineOp.And or BoardCombineOp.Or or BoardCombineOp.Xor or BoardCombineOp.AndNot)
                ? "target"
                : null),
                Direction: ((operation == BoardCombineOp.Shift)
                ? "E"
                : null),
                Element: ((operation == BoardCombineOp.Image)
                ? "-z+x"
                : null),
                Value: 9
            );

            _ = RuleCompiler.CompileEffect(
                new ActionEffect.TransformState(Transform: transform),
                "combine",
                new WorldRuleCompileContext(definition: definition)
            );
            var frame = new StateFrame(
                layout: new FrameLayout(
                    rows: definition.State,
                    topology: _ => topology
                ),
                rows: definition.State
            );

            frame.Load(source: new RowStore(rows: definition.State));
            Assert.True(
                condition: frame.TryBoardCombine(
                    combine: transform,
                    reason: out var reason
                ),
                userMessage: reason
            );
            Assert.True(
                condition: WorldStateTransforms.TryApply(
                    definition,
                    transform,
                    WorldPrincipal.World,
                    0,
                    "law",
                    out var candidate,
                    out reason
                ),
                userMessage: reason
            );
            Assert.True(
                condition: WorldDefinitionValidator.TryValidateLocally(
                    definition: candidate,
                    reason: out invalid
                ),
                userMessage: invalid
            );
            var dense = new long[4];
            var live = new long[4];

            frame.ReadBoard(
                row: definition.State.Single(predicate: row => (row.Name == destination)),
                topology: topology,
                values: dense
            );
            BoardQueries.Read(
                row: candidate.State.Single(predicate: row => (row.Name == destination)),
                topology: topology,
                values: live
            );
            Assert.Equal(
                actual: dense,
                expected: live
            );
            if (operation == BoardCombineOp.Copy) {
                Assert.Equal(
                    actualArray: dense,
                    expectedSpan: [3L, 7L, 7L, 7L]
                );
            }
        }
    }
}
