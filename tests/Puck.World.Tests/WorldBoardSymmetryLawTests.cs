using Puck.Assets.Documents;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins a topology's derived point group: it closes under composition, every element permutes the cells,
/// and the canonical fingerprint — the one form a board's symmetry orbit folds to — is invariant under every
/// element.</summary>
public sealed class WorldBoardSymmetryLawTests {
    private static StateCell Cell(string key, long value = 1) => new(
        Name(value: key),
        CellValue.Int(value: value)
    );
    private static WorldDefinition Document(WorldStateRow[] rows, WorldRule[] rules) => Fixtures.BuildDocument() with {
        StateRaw = new(
        World: rows,
        Lattices: [new LatticeTopology.Grid(
                "map",
                new DocumentVector3(
                    x: 0,
                    y: 0,
                    z: 0
                ),
                1,
                4,
                4
            )]
    ),
        Rules = rules,
    };
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static LatticeTopology Topology(TopologyKind kind, int width, int depth) => ((kind == TopologyKind.Hex)
        ? new LatticeTopology.Hex(
            "t",
            new DocumentVector3(
                x: 0,
                y: 0,
                z: 0
            ),
            1,
            Radius: 2
        )
        : new LatticeTopology.Grid(
            "t",
            new DocumentVector3(
                x: 0,
                y: 0,
                z: 0
            ),
            1,
            width,
            depth
        )
    );

    [Fact]
    public void AnAuthoredAliasResolvesAlongsideTheCanonicalSpellingButElementNameAlwaysAnswersTheCanonicalOne() {
        var square = new LatticeTopology.Grid(
            "t",
            new DocumentVector3(
                x: 0,
                y: 0,
                z: 0
            ),
            1,
            4,
            4,
            ElementAliases: [new(
                    Element: "-z+x",
                    Name: "rot90"
                )]
        );
        var topology = TopologyCompilation.Find(
            new WorldStateSection(Lattices: [square]),
            "t"
        )!;
        var aliased = topology.Element(name: "rot90");

        Assert.True(condition: (aliased >= 0));
        Assert.Equal(
            topology.Element(name: "-z+x"),
            aliased
        );
        Assert.Equal(
            "-z+x",
            topology.ElementName(element: aliased)
        );

        Assert.False(condition: TopologyCompilation.TryValidate(
            square with {
                ElementAliases = [new(
                    Element: "not-an-element",
                    Name: "rot90"
                )],
            },
            out var missingReason
        ));
        Assert.Contains(
            actualString: missingReason,
            expectedSubstring: "names no element"
        );
        Assert.False(condition: TopologyCompilation.TryValidate(
            square with {
                ElementAliases = [new(
                    Element: "-z+x",
                    Name: "identity"
                )],
            },
            out var shadowReason
        ));
        Assert.Contains(
            actualString: shadowReason,
            expectedSubstring: "already a canonical element name"
        );
        Assert.False(condition: TopologyCompilation.TryValidate(
            square with {
                ElementAliases = [new(
                    Element: "-z+x",
                    Name: "rot90"
                ), new(
                    Element: "+x-z",
                    Name: "rot90"
                )],
            },
            out var duplicateReason
        ));
        Assert.Contains(
            actualString: duplicateReason,
            expectedSubstring: "distinct name"
        );

        var definition = Fixtures.BuildDocument() with { StateRaw = new(Lattices: [square]) };
        using var fixture = Fixtures.FreshServer(definition: definition);

        Assert.Contains(
            "aliases=rot90=-z+x",
            fixture.Server.DescribeSymmetry(
                cellKey: null,
                topologyName: "t"
            )
        );
    }
    [Fact]
    public void TheCanonicalFingerprintIsInvariantUnderEveryElementAndTheImageOpAgreesWithItsMask() {
        var board = new WorldStateRow(
            Name(value: "board"),
            CellKind.Int,
            Cells: [Cell(
                    key: "0",
                    value: 1
                ), Cell(
                    key: "1",
                    value: 2
                )],
            Domain: new StateDomain.CellsOf("map")
        );
        var definition = Document(
            rows: [board, StateFixtures.IntSlot(name: "print"), StateFixtures.IntSlot(name: "imageMask")],
            rules: [
            new WorldRule(
                    Name(value: "print"),
                    [new ActionEffect.SetState(
                            State: "print",
                            FromState: "$board:canonical:board"
                        )]
                ),
            // The image op composes with the one board-set read ($board:mask) instead of a dedicated read+image
            // query: one spelling for "carry a mask through an element" everywhere it is needed.
            new WorldRule(
                    Name(value: "imageMask"),
                    [new ActionEffect.SetState(
                            State: "imageMask",
                            Expression: new ExpressionProgram(Instructions: [
                Instruction.Operand(name: "$board:mask:board:1:2"), Instruction.Board(index: "-z+x",
                                    operation: ExpressionOp.BoardImage,
                                    topology: "map"
                                ),
            ])
                        )]
                ),
        ]
        );
        var topology = TopologyCompilation.Find(
            definition.StateRaw,
            "map"
        )!;

        using var baseline = Fixtures.FreshServer(definition: definition);

        baseline.Step();
        Assert.Equal(
            (1L << topology.Image(
                topology.Element(name: "-z+x"),
                0
            )) | (1L << topology.Image(
                topology.Element(name: "-z+x"),
                1
            )),
            baseline.SlotValue(row: "imageMask"
            )
        );

        foreach (var element in Enumerable.Range(
            0,
            topology.ElementCount
        ).Select(selector: topology.ElementName)) {
            var rot = topology.Element(name: element);
            // The mirror board is built directly from the topology's own image map — the law under test is that
            // Canonical folds every element to the same fingerprint, not any one way of constructing a mirror.
            var mirror = new WorldStateRow(
                Name(value: "board"),
                CellKind.Int,
                Cells: [Cell(
                        key: topology.Key(cell: topology.Image(
                            cell: 0,
                            element: rot
                        )),
                        value: 1
                    ), Cell(
                        key: topology.Key(cell: topology.Image(
                            cell: 1,
                            element: rot
                        )),
                        value: 2
                    )],
                Domain: new StateDomain.CellsOf("map")
            );
            var mappedDefinition = definition with {
                StateRaw = definition.StateRaw! with {
                    World = [.. definition.State.Select(selector: r => ((r.Name.Value == "board")
                ? mirror
                : r))],
                },
            };
            using var fixture = Fixtures.FreshServer(definition: mappedDefinition);

            fixture.Step();
            Assert.Equal(
                baseline.SlotValue(row: "print"
                ),
                fixture.SlotValue(row: "print"
                )
            );
        }

        var different = definition with {
            StateRaw = definition.StateRaw! with {
                World = [.. definition.State.Select(selector: r => ((r.Name.Value == "board")
            ? r with { Cells = [Cell(
                        key: "0",
                        value: 1
                    ), Cell(
                        key: "5",
                        value: 2
                    )] }
            : r))],
            },
        };
        using var other = Fixtures.FreshServer(definition: different);

        other.Step();
        Assert.NotEqual(
            baseline.SlotValue(row: "print"
            ),
            other.SlotValue(row: "print"
            )
        );

        Assert.Contains(
            "elements=8",
            baseline.Server.DescribeSymmetry(
                cellKey: null,
                topologyName: "map"
            )
        );
        Assert.Contains(
            "-x-z→3",
            baseline.Server.DescribeSymmetry(
                cellKey: "12",
                topologyName: "map"
            )
        );
        var badElement = Document(
            rows: [board, StateFixtures.IntSlot(name: "bad")],
            rules: [
            new WorldRule(
                    Name(value: "bad"),
                    [new ActionEffect.SetState(
                            State: "bad",
                            Expression: new ExpressionProgram(Instructions: [
                Instruction.Operand(name: "$board:mask:board:1:2"), Instruction.Board(index: "rot45",
                                    operation: ExpressionOp.BoardImage,
                                    topology: "map"
                                ),
            ])
                        )]
                ),
        ]
        );

        Assert.Throws<RuleException>(testCode: () => WorldFactsCompiler.CompileAll(definition: badElement));
    }
    // The fingerprint is the canonical fold's identity term: every image of a board under a non-trivial element reads
    // the board's canonical form and, where the image moves a stone, a different fingerprint; the canonical form is
    // the least fingerprint over the board's images; and an equal board reads an equal fingerprint.
    [Fact]
    public void TheFingerprintTellsAMirrorImageApartWhereTheCanonicalFormCannot() {
        var topology = TopologyCompilation.Find(
            new WorldStateSection(Lattices: [Topology(
                    depth: 4,
                    kind: TopologyKind.Grid,
                    width: 4
                )]),
            "t"
        )!;
        var fingerprint = new BoardFingerprintQuery(topology: topology);
        var canonical = new BoardCanonicalQuery(topology: topology);
        var board = new long[topology.CellCount];

        board[0] = 1;
        board[1] = 2;
        board[6] = 1;

        var own = BoardQueries.Evaluate(
            empty: 0L,
            query: fingerprint,
            source: 0,
            values: board
        );
        var form = BoardQueries.Evaluate(
            empty: 0L,
            query: canonical,
            source: 0,
            values: board
        );
        var least = long.MaxValue;

        for (var element = 0; (element < topology.ElementCount); element++) {
            var image = new long[topology.CellCount];

            for (var cell = 0; (cell < topology.CellCount); cell++) {
                image[topology.Image(
                    cell: cell,
                    element: element
                )] = board[cell];
            }

            var print = BoardQueries.Evaluate(
                empty: 0L,
                query: fingerprint,
                source: 0,
                values: image
            );

            least = ((((ulong)print) < ((ulong)least)) ? print : least);
            Assert.Equal(
                expected: form,
                actual: BoardQueries.Evaluate(
                    empty: 0L,
                    query: canonical,
                    source: 0,
                    values: image
                )
            );

            if (element == 0) {
                Assert.Equal(
                    actual: print,
                    expected: own
                );
            } else {
                Assert.NotEqual(
                    actual: print,
                    expected: own
                );
            }
        }

        Assert.Equal(
            actual: form,
            expected: least
        );
        Assert.Equal(
            expected: own,
            actual: BoardQueries.Evaluate(
                empty: 0L,
                query: fingerprint,
                source: 0,
                values: board.ToArray()
            )
        );
    }
    [InlineData(TopologyKind.Grid, 4, 4, 8)]
    [InlineData(TopologyKind.Grid, 4, 2, 4)]
    [InlineData(TopologyKind.Hex, 0, 0, 12)]
    [Theory]
    public void ThePointGroupClosesAndEveryElementPermutesTheCells(TopologyKind kind, int width, int depth, int elements) {
        var topology = TopologyCompilation.Find(
            new WorldStateSection(Lattices: [Topology(
                    depth: depth,
                    kind: kind,
                    width: width
                )]),
            "t"
        )!;

        Assert.Equal(
            elements,
            topology.ElementCount
        );
        Assert.Equal(
            "identity",
            topology.ElementName(element: 0)
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
        Assert.Equal(
            -1,
            topology.Element(name: "rot45")
        );
    }
}
