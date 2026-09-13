using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

[Collection(AllocationCollection.Name)]
public sealed class DiscreteStateLawTests {
    [Fact]
    public void HexAndRingAddressingAreBoundedAndReciprocal() {
        var hex = new LatticeTopology.Hex(
            "hex",
            new DocumentVector3(
                x: 0,
                y: 0,
                z: 0
            ),
            1,
            Radius: 2
        );
        var ring = new LatticeTopology.Ring(
            "ring",
            new DocumentVector3(
                x: 0,
                y: 0,
                z: 0
            ),
            1,
            Width: 5
        );
        var state = new WorldStateSection(Lattices: [hex, ring]);
        var topology = TopologyCompilation.Find(
            name: "hex",
            section: state
        )!;

        Assert.Equal(
            19,
            topology.CellCount
        );
        for (var cell = 0; (cell < topology.CellCount); cell++) {
            for (var direction = 0; (direction < 6); direction++) {
                var neighbour = topology.Neighbour(
                    cell: cell,
                    direction: direction
                );

                if (neighbour >= 0) { Assert.Equal(
                    cell,
                    topology.Neighbour(
                        cell: neighbour,
                        direction: ((direction + 3) % 6)
                    )
                ); }
            }
        }
        var cycle = TopologyCompilation.Find(
            name: "ring",
            section: state
        )!;

        Assert.Equal(
            4,
            cycle.Neighbour(
                cell: 0,
                direction: cycle.Direction(token: "backward")
            )
        );
        Assert.Equal(
            0,
            cycle.Neighbour(
                cell: 4,
                direction: cycle.Direction(token: "forward")
            )
        );
        Assert.False(condition: TopologyCompilation.TryValidate(
            hex with { Radius = (TopologyCompilation.MaxHexRadius + 1) },
            out var radiusReason
        ));
        Assert.Contains(
            actualString: radiusReason,
            expectedSubstring: $"0..{TopologyCompilation.MaxHexRadius}"
        );
        Assert.True(condition: TopologyCompilation.TryValidate(
            hex with { Radius = TopologyCompilation.MaxHexRadius },
            out _
        ));
    }
    [Fact]
    public void WarmBoardReadsAndPathQueriesAllocateNothing() {
        var state = new WorldStateSection(Lattices: [Grid()]);
        var topology = TopologyCompilation.Find(
            name: "map",
            section: state
        )!;
        var row = new WorldStateRow(
            Name(value: "terrain"),
            CellKind.Int,
            Cells: [Cell(
                    key: "1",
                    value: 2
                )],
            Domain: new StateDomain.CellsOf(
                Empty: 1,
                Topology: "map"
            )
        );
        var query = new BoardPathCostQuery(
            topology,
            target: 15,
            maxCost: 100,
            maxVisits: 16
        );
        Span<long> values = stackalloc long[16];

        BoardQueries.Read(
            row: row,
            topology: topology,
            values: values
        );
        _ = BoardQueries.Evaluate(
            query,
            values,
            1,
            0
        );
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var repeat = 0; (repeat < 100); repeat++) {
            _ = WorldTopologyCompilation.FindPhysical(state: state);
            BoardQueries.Read(
                row: row,
                topology: topology,
                values: values
            );
            _ = BoardQueries.Evaluate(
                query,
                values,
                1,
                0
            );
        }
        Assert.Equal(
            0,
            (GC.GetAllocatedBytesForCurrentThread() - before)
        );
    }
    [Fact]
    public void APhaseOfTaggedRowRefusesAnUnguardedTransformAndAMatchingGuardAdvancesTheGenerationOnSuccess() {
        var definition = Document(
            new(
                Name(value: "turn"),
                CellKind.Int,
                Phase: new()
            ),
            new(
                Name(value: "cards"),
                CellKind.Int,
                Cells: [Cell("a")]
            ),
            new(
                Name(value: "deck"),
                CellKind.Bool,
                Cells: [Cell("a")],
                Domain: new StateDomain.KeysOf(
                    CellName.Parse(candidate: "cards"),
                    Ordered: true
                ),
                PhaseOf: "turn"
            ),
            new(
                Name(value: "hand"),
                CellKind.Bool,
                Domain: new StateDomain.KeysOf(
                    CellName.Parse(candidate: "cards"),
                    Ordered: true
                ),
                PhaseOf: "turn"
            )
        );

        Assert.True(condition: WorldStateTransforms.CanAct(
            definition,
            new(
                "turn",
                0
            ),
            WorldPrincipal.Console
        ));
        Assert.False(condition: WorldStateTransforms.CanAct(
            definition,
            new(
                "turn",
                1
            ),
            WorldPrincipal.Console
        ));
        using var fixture = Fixtures.FreshServer(definition: definition);
        var operation = new StateTransform.Transfer(
            "deck",
            "hand",
            ZoneSelector.First
        );

        fixture.Server.Submit(
            new(
                SubmissionEnvelope.LocalConnectionId,
                0,
                1,
                1,
                WorldPrincipal.Console,
                new WorldSubmissionPayload.Mutation(Value: new WorldMutation.TransformState(
                    WorldPrincipal.Console,
                    operation
                ))
            ),
            _ => { }
        );
        fixture.Step();
        Assert.Empty(collection: (Find(
            document: fixture.Server.Definition,
            row: "hand"
        ).Cells ?? []));
        Assert.Equal(
            0L,
            Find(
                document: fixture.Server.Definition,
                row: "turn"
            ).Phase!.Sequence
        );
        fixture.Server.Submit(
            new(
                SubmissionEnvelope.LocalConnectionId,
                0,
                2,
                2,
                WorldPrincipal.Console,
                new WorldSubmissionPayload.Mutation(Value: new WorldMutation.TransformState(
                    WorldPrincipal.Console,
                    operation,
                    new(
                        "turn",
                        0
                    )
                ))
            ),
            _ => { }
        );
        fixture.Step();
        Assert.Single(collection: Find(
            document: fixture.Server.Definition,
            row: "hand"
        ).Cells!);
        Assert.Equal(
            1L,
            Find(
                document: fixture.Server.Definition,
                row: "turn"
            ).Phase!.Sequence
        );
    }

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateCell Cell(string key, long value = 1) => new(
        Name(value: key),
        value
    );
    private static WorldStateRow Row(string name, params StateCell[] cells) => new(
        Name(value: name),
        CellKind.Int,
        Cells: cells
    );
    private static LatticeTopology.Grid Grid(int width = 4, int depth = 4, TopologyWrap wrap = TopologyWrap.None) => new(
        "map",
        new DocumentVector3(
            x: 0,
            y: 0,
            z: 0
        ),
        1,
        width,
        depth,
        Wrap: wrap
    );
    private static WorldDefinition Document(params WorldStateRow[] rows) => Fixtures.BuildDocument() with { StateRaw = new(
        World: rows,
        Lattices: [Grid()]
    ), Rules = [] };
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(
        document.State,
        row
    )!;

    [Fact]
    public void DiscreteTopologiesDoNotAllocatePhysicalFieldsAndWrappedRaysTerminate() {
        var definition = Document(new WorldStateRow(
            Name(value: "board"),
            CellKind.Int,
            Domain: new StateDomain.CellsOf("map")
        ));

        Assert.Null(@object: definition.Fields);
        var topology = TopologyCompilation.Find(
            definition.StateRaw,
            "map"
        )!;

        Assert.Equal(
            -1,
            topology.Neighbour(
                cell: 0,
                direction: topology.Direction(token: "N")
            )
        );
        Assert.Equal(
            5,
            topology.Neighbour(
                cell: 0,
                direction: topology.Direction(token: "SE")
            )
        );
        var wrapped = TopologyCompilation.Find(
            new WorldStateSection(Lattices: [Grid(wrap: TopologyWrap.Both)]),
            "map"
        )!;

        Assert.Equal(
            12,
            wrapped.Neighbour(
                cell: 0,
                direction: wrapped.Direction(token: "N")
            )
        );

        // A ray over a fully wrapped board whose every cell matches the pattern reads the pattern-engine's own
        // ReadRay, which breaks the moment it returns to its own origin: the walk still terminates (rather than
        // looping forever) with no blocker found, since the pattern accepts the whole (CellCount - 1)-cell word —
        // the wrap-termination guarantee a $match-over-a-ray read now leans on where a dedicated ray query once did.
        var runOfOnes = new PatternRow(
            Name(value: "runOfOnes"),
            CellKind.Int,
            Symbols: [new(
                    Name(value: "one"),
                    1,
                    1
                )],
            Pattern: new PatternNode.Star(Item: new PatternNode.Symbol(Name: "one"))
        );
        var wrappedDefinition = Fixtures.BuildDocument() with {
            StateRaw = new(
            World: [new WorldStateRow(
                    Name(value: "board"),
                    CellKind.Int,
                    Domain: new StateDomain.CellsOf(
                        "map",
                        Empty: 1
                    )
                ), new WorldStateRow(
                    Name(value: "blocker"),
                    CellKind.Int,
                    Cells: [new StateCell(
                            WorldStateRow.SlotKey,
                            0L
                        )]
                )],
            Lattices: [Grid(wrap: TopologyWrap.Both)]
        ),
            PatternsRaw = [runOfOnes],
            Rules = [new WorldRule(
                Name(value: "read"),
                [new ActionEffect.SetState(
                        State: "blocker",
                        FromState: "$match:runOfOnes:board:E:cell",
                        FromKey: "0"
                    )]
            )],
        };
        using var fixture = Fixtures.FreshServer(definition: wrappedDefinition);

        fixture.Step();
        Assert.Equal(
            -1L,
            StateRows.FindCell(
                cells: Find(
                    document: fixture.Server.Definition,
                    row: "blocker"
                ).Cells,
                key: WorldStateRow.SlotKey
            )!.Value
        );
    }

    private static PatternRow CapturePattern() => new(
        Name(value: "capture"),
        CellKind.Int,
        [new(
                Name(value: "through"),
                2,
                2
            ), new(
                Name(value: "until"),
                1,
                1
            )],
        new PatternNode.Sequence(Items: [new PatternNode.Plus(Item: new PatternNode.Symbol(Name: "through")), new PatternNode.Symbol(Name: "until")])
    );

    [Fact]
    public void BracketedRayCommitsTheAcceptedPrefixOrRefusesOnAnEmptyOne() {
        var definition = Document(new WorldStateRow(
            Name(value: "board"),
            CellKind.Int,
            Cells: [Cell(
                    key: "0",
                    value: 1
                ), Cell(
                    key: "1",
                    value: 2
                ), Cell(
                    key: "2",
                    value: 2
                ), Cell(
                    key: "3",
                    value: 1
                )],
            Domain: new StateDomain.CellsOf("map")
        )) with {
            PatternsRaw = [CapturePattern()],
        };

        Assert.True(condition: CompiledPatterns.TryCompileAll(
            definition.Patterns,
            out var patterns,
            []
        ));
        var operation = new StateTransform.SetRay(
            Direction: "E",
            From: "0",
            Pattern: "capture",
            Row: "board",
            Value: 1
        );

        Assert.True(
            condition: WorldStateTransforms.TryApply(
                definition,
                operation,
                WorldPrincipal.World,
                1,
                "test",
                out var changed,
                out var reason,
                patterns
            ),
            userMessage: reason
        );
        Assert.All(
            Find(
                document: changed,
                row: "board"
            ).Cells!,
            c => Assert.Equal(
                1,
                c.Value
            )
        );
        Assert.Equal(
            2,
            Find(
                document: definition,
                row: "board"
            ).Cells![1].Value
        );
        // Control: a board holding only "through" values never reaches the required "until" terminator, so the
        // longest accepted prefix is empty and the whole write is refused.
        var open = Document(new WorldStateRow(
            Name(value: "board"),
            CellKind.Int,
            Cells: [Cell(
                    key: "0",
                    value: 1
                ), Cell(
                    key: "1",
                    value: 2
                ), Cell(
                    key: "2",
                    value: 2
                ), Cell(
                    key: "3",
                    value: 2
                )],
            Domain: new StateDomain.CellsOf("map")
        )) with {
            PatternsRaw = [CapturePattern()],
        };

        Assert.True(condition: CompiledPatterns.TryCompileAll(
            open.Patterns,
            out var openPatterns,
            []
        ));
        Assert.False(condition: WorldStateTransforms.TryApply(
            open,
            operation,
            WorldPrincipal.World,
            1,
            "test",
            out var refused,
            out _,
            openPatterns
        ));
        Assert.Same(
            actual: refused,
            expected: open
        );
    }
    [Fact]
    public void ASliceMovesTheKeyedTokenAndEverythingAfterItInOrder() {
        var definition = Document(
            new(
                Name(value: "cards"),
                CellKind.Int,
                Cells: [Cell(
                        key: "a",
                        value: 1
                    ), Cell(
                        key: "b",
                        value: 2
                    ), Cell(
                        key: "c",
                        value: 3
                    ), Cell(
                        key: "d",
                        value: 4
                    ), Cell(
                        key: "e",
                        value: 5
                    )]
            ),
            new(
                Name(value: "column"),
                CellKind.Bool,
                Cells: [Cell("a"), Cell("b"), Cell("c"), Cell("d")],
                Domain: new StateDomain.KeysOf(
                    CellName.Parse(candidate: "cards"),
                    Ordered: true
                )
            ),
            new(
                Name(value: "other"),
                CellKind.Bool,
                Cells: [Cell("e")],
                Domain: new StateDomain.KeysOf(
                    CellName.Parse(candidate: "cards"),
                    Ordered: true
                )
            ),
            new(
                Name(value: "small"),
                CellKind.Bool,
                Capacity: 2,
                Cells: [],
                Domain: new StateDomain.KeysOf(
                    CellName.Parse(candidate: "cards"),
                    Ordered: true
                )
            )
        );
        var slice = new StateTransform.Transfer(
            "column",
            "other",
            ZoneSelector.Slice,
            Key: "b"
        );

        Assert.True(
            condition: WorldStateTransforms.TryApply(
                definition,
                slice,
                WorldPrincipal.Console,
                0,
                "test",
                out var changed,
                out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            new[] { "a" },
            Find(
                document: changed,
                row: "column"
            ).Cells!.Select(selector: c => c.Key.Value)
        );
        Assert.Equal(
            new[] { "e", "b", "c", "d" },
            Find(
                document: changed,
                row: "other"
            ).Cells!.Select(selector: c => c.Key.Value)
        );
        Assert.True(
            condition: WorldStateTransforms.TryApply(
                definition,
                slice with { InsertFirst = true },
                WorldPrincipal.Console,
                0,
                "test",
                out var first,
                out reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            new[] { "b", "c", "d", "e" },
            Find(
                document: first,
                row: "other"
            ).Cells!.Select(selector: c => c.Key.Value)
        );
        Assert.False(condition: WorldStateTransforms.TryApply(
            definition,
            slice with { To = "small" },
            WorldPrincipal.Console,
            0,
            "test",
            out _,
            out var full
        ));
        Assert.Contains(
            actualString: full,
            expectedSubstring: "full"
        );
        Assert.False(condition: WorldStateTransforms.TryApply(
            definition,
            slice with { Key = "zz" },
            WorldPrincipal.Console,
            0,
            "test",
            out _,
            out var missing
        ));
        Assert.Contains(
            actualString: missing,
            expectedSubstring: "does not contain"
        );
        Assert.False(condition: WorldStateTransforms.TryApply(
            definition,
            slice with { Count = 2 },
            WorldPrincipal.Console,
            0,
            "test",
            out _,
            out _
        ));
        Assert.True(
            condition: WorldStateTransforms.TryApply(
                definition,
                new StateTransform.Transfer(
                    "column",
                    "column",
                    ZoneSelector.Slice,
                    Key: "c",
                    InsertFirst: true
                ),
                WorldPrincipal.Console,
                0,
                "test",
                out var rotated,
                out reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            new[] { "c", "d", "a", "b" },
            Find(
                document: rotated,
                row: "column"
            ).Cells!.Select(selector: c => c.Key.Value)
        );
    }
    [Fact]
    public void BoardCombineRunsTheSetAlgebraOverABoardWiderThanAWord() {
        var wide = new LatticeTopology.Grid(
            "wide",
            new DocumentVector3(
                x: 0,
                y: 0,
                z: 0
            ),
            1,
            Width: 10,
            Depth: 10
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new(
            World: [
                new(
                    Name(value: "white"),
                    CellKind.Int,
                    Cells: [Cell(
                            key: "0",
                            value: 1
                        ),Cell(
                            key: "1",
                            value: 1
                        ),Cell(
                            key: "11",
                            value: 1
                        ),Cell(
                            key: "99",
                            value: 1
                        )],
                    Domain: new StateDomain.CellsOf("wide")
                ),
                new(
                    Name(value: "black"),
                    CellKind.Int,
                    Cells: [Cell(
                            key: "1",
                            value: 1
                        ),Cell(
                            key: "2",
                            value: 1
                        )],
                    Domain: new StateDomain.CellsOf("wide")
                ),
                new(
                    Name(value: "out"),
                    CellKind.Int,
                    Domain: new StateDomain.CellsOf("wide")
                ),
            ],
            Lattices: [wide]
        ),
            Rules = [],
        };

        static long[] Members(WorldDefinition document, string row) => [.. Find(
                document: document,
                row: row
            ).Cells!.Where(predicate: c => (c.Value != 0)).Select(selector: c => long.Parse(s: c.Key.Value)).Order()];

        Assert.True(
            condition: WorldStateTransforms.TryApply(
                definition,
                new StateTransform.BoardCombine(
                    "out",
                    BoardCombineOp.Shift,
                    Left: "white",
                    Direction: "E"
                ),
                WorldPrincipal.Console,
                0,
                "test",
                out var shifted,
                out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            new long[] { 1, 2, 12 },
            Members(
                document: shifted,
                row: "out"
            )
        );
        Assert.True(
            condition: WorldStateTransforms.TryApply(
                definition,
                new StateTransform.BoardCombine(
                    "out",
                    BoardCombineOp.And,
                    Left: "white",
                    Right: "black"
                ),
                WorldPrincipal.Console,
                0,
                "test",
                out var both,
                out reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            new long[] { 1 },
            Members(
                document: both,
                row: "out"
            )
        );
        Assert.True(
            condition: WorldStateTransforms.TryApply(
                definition,
                new StateTransform.BoardCombine(
                    "out",
                    BoardCombineOp.AndNot,
                    Left: "white",
                    Right: "black",
                    Value: 7
                ),
                WorldPrincipal.Console,
                0,
                "test",
                out var only,
                out reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            new long[] { 0, 11, 99 },
            Members(
                document: only,
                row: "out"
            )
        );
        Assert.All(
            Find(
                document: only,
                row: "out"
            ).Cells!,
            c => Assert.Equal(
                7,
                c.Value
            )
        );
        Assert.True(
            condition: WorldStateTransforms.TryApply(
                definition,
                new StateTransform.BoardCombine(
                    "out",
                    BoardCombineOp.Not,
                    Left: "black"
                ),
                WorldPrincipal.Console,
                0,
                "test",
                out var complement,
                out reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            98,
            Members(
                document: complement,
                row: "out"
            ).Length
        );
        Assert.True(
            condition: WorldStateTransforms.TryApply(
                definition,
                new StateTransform.BoardCombine(
                    "out",
                    BoardCombineOp.Image,
                    Left: "white",
                    Element: "identity"
                ),
                WorldPrincipal.Console,
                0,
                "test",
                out var image,
                out reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            new long[] { 0, 1, 11, 99 },
            Members(
                document: image,
                row: "out"
            )
        );
        Assert.True(
            condition: WorldStateTransforms.TryApply(
                definition,
                new StateTransform.BoardCombine(
                    "out",
                    BoardCombineOp.Fill,
                    Value: 3
                ),
                WorldPrincipal.Console,
                0,
                "test",
                out var filled,
                out reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            100,
            Members(
                document: filled,
                row: "out"
            ).Length
        );
        Assert.True(
            condition: WorldStateTransforms.TryApply(
                filled,
                new StateTransform.BoardCombine(
                    "out",
                    BoardCombineOp.Clear
                ),
                WorldPrincipal.Console,
                0,
                "test",
                out var cleared,
                out reason
            ),
            userMessage: reason
        );
        Assert.Empty(collection: Members(
            document: cleared,
            row: "out"
        ));
        Assert.False(condition: WorldStateTransforms.TryApply(
            definition,
            new StateTransform.BoardCombine(
                "out",
                BoardCombineOp.Shift,
                Left: "white",
                Direction: "UP"
            ),
            WorldPrincipal.Console,
            0,
            "test",
            out _,
            out var badDirection
        ));
        Assert.Contains(
            actualString: badDirection,
            expectedSubstring: "does not declare"
        );
        Assert.False(condition: WorldStateTransforms.TryApply(
            definition,
            new StateTransform.BoardCombine(
                "out",
                BoardCombineOp.Or,
                Left: "white"
            ),
            WorldPrincipal.Console,
            0,
            "test",
            out _,
            out _
        ));
        Assert.False(condition: WorldStateTransforms.TryApply(
            definition,
            new StateTransform.BoardCombine(
                "out",
                BoardCombineOp.Copy,
                Left: "white",
                Value: 0
            ),
            WorldPrincipal.Console,
            0,
            "test",
            out _,
            out var emptyValue
        ));
        Assert.Contains(
            actualString: emptyValue,
            expectedSubstring: "empty"
        );
    }
    [Fact]
    public void AnOrderedZoneHasAnArrangementRankAndArrangePutsItBack() {
        var definition = Document(
            new(
                Name(value: "cards"),
                CellKind.Int,
                Cells: [Cell(
                        key: "a",
                        value: 1
                    ), Cell(
                        key: "b",
                        value: 2
                    ), Cell(
                        key: "c",
                        value: 3
                    )]
            ),
            new(
                Name(value: "pile"),
                CellKind.Bool,
                Cells: [Cell("c"), Cell("a"), Cell("b")],
                Domain: new StateDomain.KeysOf(
                    CellName.Parse(candidate: "cards"),
                    Ordered: true
                )
            ),
            new(
                Name(value: "rank"),
                CellKind.Int,
                Cells: [Cell(
                        key: WorldStateRow.SlotKey,
                        value: 0
                    )]
            )
        );
        var pile = Find(
            document: definition,
            row: "pile"
        );
        // (c, a, b) is domain ordinals (2, 0, 1): Lehmer rank 2·2! + 0·1! + 0 = 4 of the six orders.
        Assert.Equal(
            4L,
            StateReader.ArrangementRank(
                rows: definition.State,
                zone: pile
            )
        );
        Assert.Equal(
            -1L,
            StateReader.ArrangementRank(
                rows: definition.State,
                zone: Find(
                    document: definition,
                    row: "cards"
                )
            )
        );

        var arrange = new StateTransform.Arrange(
            "pile",
            "rank"
        );

        Assert.True(
            condition: WorldStateTransforms.TryApply(
                definition,
                arrange,
                WorldPrincipal.Console,
                0,
                "test",
                out var sorted,
                out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            new[] { "a", "b", "c" },
            Find(
                document: sorted,
                row: "pile"
            ).Cells!.Select(selector: c => c.Key.Value)
        );
        Assert.Equal(
            0L,
            StateReader.ArrangementRank(
                rows: sorted.State,
                zone: Find(
                    document: sorted,
                    row: "pile"
                )
            )
        );
        var atFour = definition with { StateRaw = new(
            World: [.. definition.State.Select(selector: r => ((r.Name.Value == "rank")
            ? r with { Cells = [Cell(
                            key: WorldStateRow.SlotKey,
                            value: 4
                        )] }
            : r))],
            Lattices: definition.StateRaw!.Lattices
        ) };

        Assert.True(
            condition: WorldStateTransforms.TryApply(
                sorted with { StateRaw = atFour.StateRaw },
                arrange,
                WorldPrincipal.Console,
                0,
                "test",
                out var back,
                out reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            new[] { "c", "a", "b" },
            Find(
                document: back,
                row: "pile"
            ).Cells!.Select(selector: c => c.Key.Value)
        );
        var atSix = definition with { StateRaw = new(
            World: [.. definition.State.Select(selector: r => ((r.Name.Value == "rank")
            ? r with { Cells = [Cell(
                            key: WorldStateRow.SlotKey,
                            value: 6
                        )] }
            : r))],
            Lattices: definition.StateRaw!.Lattices
        ) };

        Assert.False(condition: WorldStateTransforms.TryApply(
            atSix,
            arrange,
            WorldPrincipal.Console,
            0,
            "test",
            out _,
            out var outside
        ));
        Assert.Contains(
            actualString: outside,
            expectedSubstring: "outside"
        );
    }
    [Fact]
    public void TransfersPreserveDuplicateValuedTokenIdentitiesAndPileOrder() {
        var definition = Document(
            new(
                Name(value: "cards"),
                CellKind.Int,
                Cells: [Cell(
                        key: "a",
                        value: 7
                    ), Cell(
                        key: "b",
                        value: 7
                    )]
            ),
            new(
                Name(value: "deck"),
                CellKind.Bool,
                Cells: [Cell("a"), Cell("b")],
                Domain: new StateDomain.KeysOf(
                    CellName.Parse(candidate: "cards"),
                    Ordered: true
                )
            ),
            new(
                Name(value: "hand"),
                CellKind.Bool,
                Capacity: 1,
                Cells: [],
                Domain: new StateDomain.KeysOf(
                    CellName.Parse(candidate: "cards"),
                    Ordered: true
                )
            )
        );
        var operation = new StateTransform.Transfer(
            "deck",
            "hand",
            ZoneSelector.First
        );

        Assert.True(
            condition: WorldStateTransforms.TryApply(
                definition,
                operation,
                WorldPrincipal.Console,
                0,
                "test",
                out var changed,
                out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            "a",
            Assert.Single(collection: Find(
                document: changed,
                row: "hand"
            ).Cells!).Key.Value
        );
        Assert.Equal(
            "b",
            Assert.Single(collection: Find(
                document: changed,
                row: "deck"
            ).Cells!).Key.Value
        );
        Assert.False(condition: WorldStateTransforms.TryApply(
            changed,
            operation,
            WorldPrincipal.Console,
            0,
            "test",
            out var refused,
            out _
        ));
        Assert.Same(
            actual: refused,
            expected: changed
        );
        Assert.True(
            condition: WorldStateTransforms.TryApply(
                definition,
                new StateTransform.Transfer(
                    "deck",
                    "deck",
                    ZoneSelector.First
                ),
                WorldPrincipal.Console,
                0,
                "test",
                out var reordered,
                out reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            new[] { "b", "a" },
            Find(
                document: reordered,
                row: "deck"
            ).Cells!.Select(selector: c => c.Key.Value)
        );
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
            new(
                Name(value: "position"),
                CellKind.Int,
                Capacity: 1,
                Cells: [Cell(
                        key: "0",
                        value: 0
                    )]
            ),
            new(
                Name(value: "destination"),
                CellKind.Int,
                Capacity: 1,
                Cells: [Cell(
                        key: "0",
                        value: 2
                    )]
            ),
            new(
                Name(value: "allowance"),
                CellKind.Int,
                Capacity: 1,
                Cells: [Cell(
                        key: "0",
                        value: allowance
                    )]
            ),
            new(
                Name(value: "terrain"),
                CellKind.Int,
                Domain: new StateDomain.CellsOf(
                    "map",
                    Empty: 1
                )
            ),
            new(
                Name(value: "occupancy"),
                CellKind.Int,
                Domain: new StateDomain.CellsOf(
                    "map",
                    Empty: 0
                ),
                Cells: [Cell(
                        key: "0",
                        value: 1
                    )]
            )
        ) with {
            Rules = [new(
                Name(value: "move"),
                Effects: [new ActionEffect.Transaction([
                new ActionEffect.AddState(
                            "allowance",
                            Key: "0",
                            Expression: new(Tokens: [
                    new ValueToken.State(
                                    "$board:pathCost:terrain:cell:destination:0:100:16",
                                    Key: "$cell:position:0"
                                ),
                    new ValueToken.Negate(),
                ])
                        ),
                new ActionEffect.SetState(
                            "occupancy",
                            Key: "$cell:position:0",
                            Value: 0
                        ),
                new ActionEffect.SetState(
                            "terrain",
                            Key: "$cell:position:0",
                            Value: 1
                        ),
                new ActionEffect.SetState(
                            "position",
                            Key: "0",
                            FromState: "destination",
                            FromKey: "0"
                        ),
                new ActionEffect.SetState(
                            "occupancy",
                            Key: "$cell:position:0",
                            Value: 1
                        ),
                new ActionEffect.SetState(
                            "terrain",
                            Key: "$cell:position:0",
                            Value: -1
                        ),
            ])],
                Mode: ActionTriggerMode.Edge,
                Gate: new ActionPredicate.All(Predicates: [
                new ActionPredicate.CompareState(
                        "position",
                        ActionStateComparison.NotEqual,
                        Key: "0",
                        ComparandState: "destination",
                        ComparandKey: "0"
                    ),
                new ActionPredicate.CompareState(
                        "$board:pathCost:terrain:cell:destination:0:100:16",
                        ActionStateComparison.LessOrEqual,
                        Key: "$cell:position:0",
                        ComparandState: "allowance",
                        ComparandKey: "0"
                    ),
            ])
            )],
        };

        // Cell 0 to cell 2 on the 4-wide grid is two due-east steps at the uniform cost-1 terrain: affordable at
        // exactly 2, not at 1.
        using (var fixture = Fixtures.FreshServer(definition: Scenario(allowance: 1))) {
            fixture.Step();
            Assert.Equal(
                0,
                Find(
                    document: fixture.Server.Definition,
                    row: "position"
                ).Cells![0].Value
            );
            Assert.Equal(
                1,
                Find(
                    document: fixture.Server.Definition,
                    row: "allowance"
                ).Cells![0].Value
            );
            Assert.Equal(
                1,
                Find(
                    document: fixture.Server.Definition,
                    row: "occupancy"
                ).Cells!.Single(predicate: c => (c.Key.Value == "0")).Value
            );
        }

        // Control: the identical request succeeds once the allowance covers the live cost — the gate tracks the
        // cost, not a value frozen at compile time.
        using (var fixture = Fixtures.FreshServer(definition: Scenario(allowance: 2))) {
            fixture.Step();
            Assert.Equal(
                2,
                Find(
                    document: fixture.Server.Definition,
                    row: "position"
                ).Cells![0].Value
            );
            Assert.Equal(
                0,
                Find(
                    document: fixture.Server.Definition,
                    row: "allowance"
                ).Cells![0].Value
            );
            Assert.Equal(
                0,
                Find(
                    document: fixture.Server.Definition,
                    row: "occupancy"
                ).Cells!.Single(predicate: c => (c.Key.Value == "0")).Value
            );
            Assert.Equal(
                1,
                Find(
                    document: fixture.Server.Definition,
                    row: "occupancy"
                ).Cells!.Single(predicate: c => (c.Key.Value == "2")).Value
            );
        }
    }
    [Fact]
    public void RuleTransactionRollsBackTransferAndPhaseWhenLaterEffectRefuses() {
        var definition = Document(
            new(
                Name(value: "cards"),
                CellKind.Int,
                Cells: [Cell(
                        key: "a",
                        value: 7
                    )]
            ),
            new(
                Name(value: "deck"),
                CellKind.Bool,
                Cells: [Cell("a")],
                Domain: new StateDomain.KeysOf(
                    CellName.Parse(candidate: "cards"),
                    Ordered: true
                )
            ),
            new(
                Name(value: "hand"),
                CellKind.Bool,
                Cells: [],
                Domain: new StateDomain.KeysOf(
                    CellName.Parse(candidate: "cards"),
                    Ordered: true
                )
            ),
            Row(
                "failed",
                new StateCell(
                    WorldStateRow.SlotKey,
                    0
                )
            )
        ) with {
            Rules = [new(
                Name(value: "atomic"),
                Effects: [new ActionEffect.Transaction(
                        [
                new ActionEffect.TransformState(Transform: new StateTransform.Transfer(
                                "deck",
                                "hand",
                                ZoneSelector.First
                            )),
                new ActionEffect.RemoveStateCell(
                                Key: "missing",
                                State: "hand"
                            )
            ],
                        OnFailure: [new ActionEffect.SetState(
                                "failed",
                                Value: 1
                            )]
                    )]
            )],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();
        Assert.Single(collection: Find(
            document: fixture.Server.Definition,
            row: "deck"
        ).Cells!);
        Assert.Empty(collection: Find(
            document: fixture.Server.Definition,
            row: "hand"
        ).Cells!);
        Assert.Equal(
            1,
            Find(
                document: fixture.Server.Definition,
                row: "failed"
            ).Cells![0].Value
        );
    }
    [Fact]
    public void AttacksQueryStopsAtTheFirstBlockerAndOnlyMatchesItsOwnValue() {
        var definition = Document(new WorldStateRow(
            Name(value: "board"),
            CellKind.Int,
            Domain: new StateDomain.CellsOf("map")
        ));
        var topology = TopologyCompilation.Find(
            definition.StateRaw,
            "map"
        )!;
        var east = topology.Direction(token: "E");
        var south = topology.Direction(token: "S");
        const int Origin = 4; // (x=0, z=1) on the 4-wide grid
        const int RookCell = 6; // two steps east of the origin
        var values = new long[topology.CellCount];

        values[RookCell] = 4;
        var attacksEast = new BoardAttacksQuery(
            topology,
            lower: 4,
            upper: 4,
            directions: [east]
        );

        Assert.Equal(
            1,
            BoardQueries.Evaluate(
                attacksEast,
                values,
                0,
                Origin
            )
        );
        // Control: the same ray with no qualifying piece at all must read a miss, not a stale hit.
        Assert.Equal(
            0,
            BoardQueries.Evaluate(
                attacksEast,
                new long[topology.CellCount],
                0,
                Origin
            )
        );
        // Control: the rook's cell holds a code outside the authored range -- geometry alone must not be enough.
        var attacksWrongValue = new BoardAttacksQuery(
            topology,
            lower: 5,
            upper: 5,
            directions: [east]
        );

        Assert.Equal(
            0,
            BoardQueries.Evaluate(
                attacksWrongValue,
                values,
                0,
                Origin
            )
        );
        // Control: the piece sits east, not south -- an authored direction that never reaches it must read a miss.
        var attacksSouthOnly = new BoardAttacksQuery(
            topology,
            lower: 4,
            upper: 4,
            directions: [south]
        );

        Assert.Equal(
            0,
            BoardQueries.Evaluate(
                attacksSouthOnly,
                values,
                0,
                Origin
            )
        );
        // Several authored directions OR together: south alone misses, but south-or-east finds the rook via east.
        var attacksEitherWay = new BoardAttacksQuery(
            topology,
            lower: 4,
            upper: 4,
            directions: [south, east]
        );

        Assert.Equal(
            1,
            BoardQueries.Evaluate(
                attacksEitherWay,
                values,
                0,
                Origin
            )
        );
        // Control: a non-qualifying piece one step closer blocks the ray -- if the walk did not stop at the first
        // occupied cell, this would wrongly still see the rook past it.
        var blocked = ((long[])values.Clone());

        blocked[(Origin + 1)] = 9;
        Assert.Equal(
            0,
            BoardQueries.Evaluate(
                attacksEast,
                blocked,
                0,
                Origin
            )
        );
    }
    // No chess-specific code: a search job's per-token legal mask (an int row keyed by token index) paints a plan
    // board through the general writeSet transform, addressed by a $cell: indirection naming whichever token index
    // is held. A boardCombine clear ahead of it keeps the paint current rather than additive.
    [Fact]
    public void WriteSetPaintsFromACellIndirectionAndRepaintsWhenTheIndirectionChanges() {
        var definition = Document(
            new(
                Name(value: "board"),
                CellKind.Int,
                Domain: new StateDomain.CellsOf("map")
            ),
            new(
                Name(value: "held"),
                CellKind.Int,
                Cells: [Cell(
                        key: "token",
                        value: 0
                    )]
            ),
            new(
                Name(value: "legal"),
                CellKind.Int,
                Cells: [Cell(
                        key: "0",
                        value: 0b0011L
                    ), Cell(
                        key: "1",
                        value: 0b1100L
                    )]
            )
        ) with {
            Rules = [new WorldRule(
                Name(value: "paint"),
                [
                new ActionEffect.TransformState(Transform: new StateTransform.BoardCombine(
                        "board",
                        BoardCombineOp.Clear
                    )),
                new ActionEffect.TransformState(Transform: new StateTransform.WriteSet(
                        "board",
                        "legal",
                        SetKey: "$cell:held:token",
                        Value: 9
                    )),
            ]
            )],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();
        var painted = Find(
            document: fixture.Server.Definition,
            row: "board"
        ).Cells!;

        Assert.Equal(
            9L,
            StateRows.FindCell(
                cells: painted,
                key: Name(value: "0")
            )!.Value
        );
        Assert.Equal(
            9L,
            StateRows.FindCell(
                cells: painted,
                key: Name(value: "1")
            )!.Value
        );
        Assert.Null(@object: StateRows.FindCell(
            cells: painted,
            key: Name(value: "2")
        ));
        Assert.Null(@object: StateRows.FindCell(
            cells: painted,
            key: Name(value: "3")
        ));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console,
            Row: "held",
            Key: "token",
            Value: 1,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();
        var repainted = Find(
            document: fixture.Server.Definition,
            row: "board"
        ).Cells!;

        Assert.Equal(
            9L,
            StateRows.FindCell(
                cells: repainted,
                key: Name(value: "2")
            )!.Value
        );
        Assert.Equal(
            9L,
            StateRows.FindCell(
                cells: repainted,
                key: Name(value: "3")
            )!.Value
        );
        Assert.Null(@object: StateRows.FindCell(
            cells: repainted,
            key: Name(value: "0")
        ));
        Assert.Null(@object: StateRows.FindCell(
            cells: repainted,
            key: Name(value: "1")
        ));
    }
}
