using Puck.Assets.Documents;
using Puck.Maths;
using Xunit;

namespace Puck.State.Topology.Tests;

/// <summary>The word kernels a board's planes run on answer what a walk of every cell answers: a shift of a cell set is
/// each member's neighbour, an image each member's carried cell, a reach the flood a queue walk finds, and a
/// combination's members the set algebra cell by cell, over boards of one word and of several, wrapped and not.</summary>
public sealed class BoardPlaneKernelLawTests {
    public static TheoryData<string> Boards => ["grid19", "grid8", "wrapped13", "hex4", "grid1"];

    private static CompiledTopology Compile(string name) => name switch {
        "grid19" => TopologyCompilation.Compile(topology: new LatticeTopology.Grid(Name: name, Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: 19, Depth: 19)),
        "grid8" => TopologyCompilation.Compile(topology: new LatticeTopology.Grid(Name: name, Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: 8, Depth: 8)),
        "wrapped13" => TopologyCompilation.Compile(topology: new LatticeTopology.Grid(Name: name, Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: 13, Depth: 11, Wrap: TopologyWrap.Both)),
        "hex4" => TopologyCompilation.Compile(topology: new LatticeTopology.Hex(Name: name, Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Radius: 4)),
        _ => TopologyCompilation.Compile(topology: new LatticeTopology.Grid(Name: name, Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: 1, Depth: 1)),
    };
    private static ulong[] Random(CompiledTopology topology, Random random, int percent) {
        var cells = new ulong[topology.CellWords];

        for (var cell = 0; (cell < topology.CellCount); cell++) {
            if (random.Next(maxValue: 100) < percent) {
                BitWords.Add(index: cell, words: cells);
            }
        }
        return cells;
    }

    [Theory]
    [MemberData(nameof(Boards))]
    public void TheWordKernelsAnswerWhatACellWalkAnswers(string board) {
        var topology = Compile(name: board);
        var random = new Random(Seed: board.Length * 7919);
        var scratch = new ArenaScratch();
        var words = topology.CellWords;

        for (var trial = 0; (trial < 60); trial++) {
            var left = Random(topology: topology, percent: (trial % 9) * 6, random: random);
            var right = Random(topology: topology, percent: 30 + ((trial % 7) * 10), random: random);

            for (var direction = 0; (direction < topology.DirectionCount); direction++) {
                var shifted = new ulong[words];
                var expected = new ulong[words];

                topology.ShiftCells(destination: shifted, direction: direction, source: left);
                foreach (var cell in BitWords.Members(words: left)) {
                    if (topology.Neighbour(cell: cell, direction: direction) is >= 0 and var neighbour) {
                        BitWords.Add(index: neighbour, words: expected);
                    }
                }
                Assert.Equal(expected, shifted);
            }
            for (var element = 0; (element < topology.ElementCount); element++) {
                var image = new ulong[words];
                var expected = new ulong[words];

                topology.ImageCells(destination: image, element: element, source: left);
                foreach (var cell in BitWords.Members(words: left)) {
                    if (topology.Image(cell: cell, element: element) is >= 0 and var carried) {
                        BitWords.Add(index: carried, words: expected);
                    }
                }
                Assert.Equal(expected, image);
            }

            var reach = new ulong[words];
            var flooded = (ulong[])left.Clone();
            var queue = new Queue<int>();

            _ = topology.Reach(passable: right, reach: reach, scratch: scratch, seeds: left);
            foreach (var cell in BitWords.Members(words: left)) {
                queue.Enqueue(item: cell);
            }
            while (queue.TryDequeue(result: out var cell)) {
                for (var direction = 0; (direction < topology.DirectionCount); direction++) {
                    if ((topology.Neighbour(cell: cell, direction: direction) is >= 0 and var neighbour) && BitWords.Contains(index: neighbour, words: right) && !BitWords.Contains(index: neighbour, words: flooded)) {
                        BitWords.Add(index: neighbour, words: flooded);
                        queue.Enqueue(item: neighbour);
                    }
                }
            }
            Assert.Equal(flooded, reach);

            foreach (var operation in Enum.GetValues<BoardCombineOp>()) {
                if (operation is BoardCombineOp.Shift or BoardCombineOp.Image or BoardCombineOp.Reach) {
                    continue;
                }

                var members = new ulong[words];
                var expected = new ulong[words];

                BoardCombination.Members(direction: -1, element: -1, left: left, members: members, operation: operation, right: right, scratch: scratch, topology: topology);
                for (var cell = 0; (cell < topology.CellCount); cell++) {
                    var inLeft = BitWords.Contains(index: cell, words: left);
                    var inRight = BitWords.Contains(index: cell, words: right);
                    var member = operation switch {
                        BoardCombineOp.Copy => inLeft,
                        BoardCombineOp.Fill => true,
                        BoardCombineOp.And => (inLeft && inRight),
                        BoardCombineOp.Or => (inLeft || inRight),
                        BoardCombineOp.Xor => (inLeft ^ inRight),
                        BoardCombineOp.AndNot => (inLeft && !inRight),
                        BoardCombineOp.Not => !inLeft,
                        _ => false,
                    };

                    if (member) {
                        BitWords.Add(index: cell, words: expected);
                    }
                }
                Assert.Equal(expected, members);
            }
        }
    }
}
