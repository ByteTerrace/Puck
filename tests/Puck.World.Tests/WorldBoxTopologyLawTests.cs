using Puck.Assets.Documents;
using Puck.Maths;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the box topology: 26 named space directions, a layer resolved from Y, the octahedral group of a
/// cube against the smaller groups of a prism and a box, and a space-diagonal line of four read by the same line
/// query a flat board uses.</summary>
public sealed class WorldBoxTopologyLawTests {
    private static LatticeTopology.Box Box(int width, int depth, int layers) =>
        new(
            "box",
            new DocumentVector3(
                x: 0,
                y: 0,
                z: 0
            ),
            0.5f,
            width,
            depth,
            layers,
            LayerHeight: 0.5f
        );

    [InlineData(4, 4, 4, 48)]
    [InlineData(4, 4, 2, 16)]
    [InlineData(4, 3, 2, 8)]
    [Theory]
    public void ABoxDerivesItsSignedAxisPermutationsAndTwentySixDirections(int width, int depth, int layers, int elements) {
        var topology = TopologyCompilation.Find(
            new WorldStateSection(Lattices: [Box(
                    depth: depth,
                    layers: layers,
                    width: width
                )]),
            "box"
        )!;

        Assert.Equal(
            ((width * depth) * layers),
            topology.CellCount
        );
        Assert.Equal(
            26,
            topology.DirectionCount
        );
        Assert.Equal(
            elements,
            topology.ElementCount
        );
        Assert.Equal(
            "identity",
            topology.ElementName(element: 0)
        );
        Assert.Equal(
            topology.Direction(token: "UNE"),
            Array.IndexOf(
                array: CompiledTopology.BoxDirectionNames,
                value: "UNE"
            )
        );
        Assert.Equal(
            -1,
            topology.Direction(token: "X")
        );

        var tables = Enumerable.Range(
            count: elements,
            start: 0
        ).Select(selector: e => Enumerable.Range(
            0,
            topology.CellCount
        ).Select(selector: c => topology.Image(
            cell: c,
            element: e
        )).ToArray()).ToList();

        foreach (var table in tables) {
            Assert.Equal(
                topology.CellCount,
                table.Distinct().Count()
            );
        }
        for (var a = 0; (a < elements); a++) {
            for (var b = 0; (b < elements); b++) {
                var composed = Enumerable.Range(
                    0,
                    topology.CellCount
                ).Select(selector: c => tables[a][tables[b][c]]).ToArray();

                Assert.Contains(
                    collection: tables,
                    filter: table => table.SequenceEqual(other: composed)
                );
            }
        }

        // U from the bottom layer's origin cell is one layer up; D from it is off the box; the ordinal law holds.
        Assert.Equal(
            (width * depth),
            topology.Neighbour(
                cell: 0,
                direction: topology.Direction(token: "U")
            )
        );
        Assert.Equal(
            -1,
            topology.Neighbour(
                cell: 0,
                direction: topology.Direction(token: "D")
            )
        );
    }
    [Fact]
    public void ASpaceDiagonalRunIsReadThroughMatchOverARayInsteadOfABoardLineQuery() {
        // "USE" (up, south, east) is the box's signed step (+1,+1,+1) — the space diagonal from the cube's own
        // origin corner. A pattern of "one or more of the marked value" read with the prefix facet answers how far
        // the run continues past the origin cell exactly as a dedicated line query would, generalized to any
        // authored run shape rather than only exact-length equality.
        static StateCell Mark(int x, int y, int z) => new(
            CellName.Parse(candidate: ((((z * 4) + y) * 4) + x).ToString()),
            CellValue.Int(value: 1L)
        );
        var board = new WorldStateRow(
            CellName.Parse(candidate: "cube"),
            CellKind.Int,
            Cells: [Mark(
                    x: 0,
                    y: 0,
                    z: 0
                ), Mark(
                    x: 1,
                    y: 1,
                    z: 1
                ), Mark(
                    x: 2,
                    y: 2,
                    z: 2
                ), Mark(
                    x: 3,
                    y: 3,
                    z: 3
                )],
            Domain: new StateDomain.CellsOf("box")
        );
        var runOfOnes = new PatternRow(
            CellName.Parse(candidate: "runOfOnes"),
            CellKind.Int,
            Symbols: [new(
                    CellName.Parse(candidate: "one"),
                    1,
                    1
                )],
            Pattern: new PatternNode.Star(Item: new PatternNode.Symbol(Name: "one"))
        );
        var run = new WorldStateRow(
            CellName.Parse(candidate: "run"),
            CellKind.Int,
            Cells: [new StateCell(
                    WorldStateRow.SlotKey,
                    CellValue.Int(value: 0L)
                )]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new(
            World: [board, run],
            Lattices: [Box(
                    depth: 4,
                    layers: 4,
                    width: 4
                )]
        ),
            PatternsRaw = [runOfOnes],
            Rules = [new WorldRule(
                CellName.Parse(candidate: "read"),
                [new ActionEffect.SetState(
                        State: "run",
                        FromState: "$match:runOfOnes:cube:USE:prefix",
                        FromKey: "0"
                    )]
            )],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();
        Assert.Equal(
            3L,
            fixture.SlotValue(row: "run"
            )
        );

        var broken = definition with {
            StateRaw = definition.StateRaw! with {
                World = [board with { Cells = [Mark(
                    x: 0,
                    y: 0,
                    z: 0
                ), Mark(
                    x: 1,
                    y: 1,
                    z: 1
                ), Mark(
                    x: 2,
                    y: 2,
                    z: 2
                )] }, run],
            },
        };
        using var control = Fixtures.FreshServer(definition: broken);

        control.Step();
        Assert.Equal(
            2L,
            control.SlotValue(row: "run"
            )
        );
    }
    [Fact]
    public void AnExactRunOfFourIsAcceptedAndAContinuingFifthCellIsNot() {
        // Two boards over the same 5-wide, 2-layer box: "isolated" marks only x0..x3 along E (a genuine run of
        // exactly four, flanked by a real but unmarked cell), and "extended" marks x0..x4 (a run of five, so the
        // same window is followed by a fifth marked cell). Reading east from x0's own cell with a pattern of
        // "exactly three more of the marked value, then never another" tells the two apart on one $match read: it
        // needs the ray's WHOLE remainder, not just a prefix length, which is why the facet here is the plain
        // accept (no suffix) rather than prefix/cell/distance. Layer 1 exists only so a wrapped-opposite direction
        // resolves to a real, unmarked cell rather than an out-of-range one.
        static WorldStateRow Row(string name, params int[] indices) => new(
            CellName.Parse(candidate: name),
            CellKind.Int,
            Cells: [.. indices.Select(selector: i => new StateCell(
                    CellName.Parse(candidate: i.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)),
                    CellValue.Int(value: 7L)
                ))],
            Domain: new StateDomain.CellsOf("box")
        );
        var runTerminated = new PatternRow(
            CellName.Parse(candidate: "runTerminated"),
            CellKind.Int,
            Symbols: [new(
                    CellName.Parse(candidate: "seven"),
                    7,
                    7
                )],
            Pattern: new PatternNode.Sequence(Items: [
                new PatternNode.Repeat(
                    new PatternNode.Symbol(Name: "seven"),
                    3,
                    3
                ),
                new PatternNode.Star(Item: new PatternNode.Except(Name: "seven")),
            ])
        );

        var isolated = Row(
            "isolated",
            0,
            1,
            2,
            3
        );
        var extended = Row(
            "extended",
            0,
            1,
            2,
            3,
            4
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new(
            World: [isolated, extended, StateFixtures.IntSlot(name: "winnerIsolated"), StateFixtures.IntSlot(name: "winnerExtended")],
            Lattices: [Box(
                    depth: 1,
                    layers: 2,
                    width: 5
                )]
        ),
            PatternsRaw = [runTerminated],
            Rules = [
                new WorldRule(
                CellName.Parse(candidate: "markIsolated"),
                [new ActionEffect.SetState(
                        State: "winnerIsolated",
                        FromState: "$match:runTerminated:isolated:E",
                        FromKey: "0"
                    )]
            ),
                new WorldRule(
                CellName.Parse(candidate: "markExtended"),
                [new ActionEffect.SetState(
                        State: "winnerExtended",
                        FromState: "$match:runTerminated:extended:E",
                        FromKey: "0"
                    )]
            ),
            ],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();
        Assert.Equal(
            1L,
            fixture.SlotValue(row: "winnerIsolated"
            )
        );
        Assert.Equal(
            0L,
            fixture.SlotValue(row: "winnerExtended"
            )
        );
    }
    [Fact]
    public void OppositeIsDerivedFromEachDirectionsOwnVectorRatherThanHalvingTheOrdinal() {
        var topology = TopologyCompilation.Find(
            new WorldStateSection(Lattices: [Box(
                    depth: 4,
                    layers: 4,
                    width: 4
                )]),
            "box"
        )!;
        // The old (direction + DirectionCount/2) % DirectionCount trick puts N's (0) "opposite" at ordinal 13
        // ("US"), not S (4) — the 26 box directions are not paired by half-count offset the way Grid/Hex/Ring are.
        Assert.Equal(
            topology.Direction(token: "S"),
            topology.Opposite(direction: topology.Direction(token: "N"))
        );
        Assert.Equal(
            topology.Direction(token: "N"),
            topology.Opposite(direction: topology.Direction(token: "S"))
        );
        for (var direction = 0; (direction < topology.DirectionCount); direction++) {
            Assert.Equal(
                direction,
                topology.Opposite(direction: topology.Opposite(direction: direction))
            );
        }
    }
    [Fact]
    public void YResolvesToALayer() {
        var topology = TopologyCompilation.Find(
            new WorldStateSection(Lattices: [Box(
                    depth: 4,
                    layers: 4,
                    width: 4
                )]),
            "box"
        )!;

        Assert.True(condition: topology.TryCellOf(
            new FixedVector3(
                X: FixedQ4816.FromDouble(value: 0.6),
                Y: FixedQ4816.FromDouble(value: 0.8),
                Z: FixedQ4816.FromDouble(value: 0.1)
            ),
            out var cell
        ));
        Assert.Equal(
            actual: cell,
            expected: ((((1 * 4) + 0) * 4) + 1)
        );
        Assert.False(condition: topology.TryCellOf(
            new FixedVector3(
                X: FixedQ4816.FromDouble(value: 0.6),
                Y: FixedQ4816.FromDouble(value: -0.1),
                Z: FixedQ4816.FromDouble(value: 0.1)
            ),
            out _
        ));
        Assert.False(condition: topology.TryCellOf(
            new FixedVector3(
                X: FixedQ4816.FromDouble(value: 0.6),
                Y: FixedQ4816.FromDouble(value: 2.5),
                Z: FixedQ4816.FromDouble(value: 0.1)
            ),
            out _
        ));

        Assert.False(condition: TopologyCompilation.TryValidate(
            Box(
                depth: 4,
                layers: 4,
                width: 4
            ) with { LayerHeight = 0f },
            out var heightReason
        ));
        Assert.Contains(
            actualString: heightReason,
            expectedSubstring: "layerHeight"
        );

        // A grid's case type carries no 'layerHeight' property to author in the first place; the invariant a
        // runtime check once named ("layerHeight belongs to a box") is now enforced by the document's own
        // strict-parsed JSON shape instead.
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new(Lattices: [new LatticeTopology.Grid(
                "g",
                new DocumentVector3(
                    x: 0,
                    y: 0,
                    z: 0
                ),
                1,
                4,
                4
            )]),
        };
        var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: definition)))!.AsObject();

        node["state"]!["lattices"]!.AsArray()[0]!["layerHeight"] = 1f;
        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: node.ToJsonString())));

        Assert.IsType<System.Text.Json.JsonException>(@object: exception.InnerException);
    }
}
