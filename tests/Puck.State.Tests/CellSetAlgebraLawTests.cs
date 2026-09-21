using Puck.Assets.Documents;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a <see cref="CellSetExpression"/> lowers to one topology-width
/// <see cref="CellSet"/> over
/// the width its sources agree on, and the operators obey the boolean algebra they spell — De Morgan over
/// intersection, union, and complement; a set meeting its own complement is empty and joining it is everything;
/// complement stops at the carrier's width. The three sources read the arena's own columns, so a write moves the
/// set, and an expression mixing carriers of different widths refuses by name.</summary>
public sealed class CellSetAlgebraLawTests {
    private static CellSetExpression Board(long low, long high) => new CellSetExpression.Board(
        High: high,
        Low: low,
        Row: TopologyArenaFixture.Name(value: "board")
    );
    private static CellSetExpression Family(long low, long high) => new CellSetExpression.Family(
        High: high,
        Low: low,
        Name: TopologyArenaFixture.Name(value: "slot")
    );
    private static CellSetExpression Zone(long low, long high) => new CellSetExpression.Zone(
        High: high,
        Low: low,
        Row: TopologyArenaFixture.Name(value: "pile")
    );
    private static CellSet Lower(StateArena arena, CellSetExpression expression) {
        Assert.True(
            condition: CellSetLowering.TryLower(
                arena: arena,
                expression: expression,
                reason: out var reason,
                set: out var set
            ),
            userMessage: reason
        );

        return set;
    }
    private static StateArena Seeded() {
        var (catalog, arena) = TopologyArenaFixture.Build();

        for (var cell = 0; (cell < TopologyArenaFixture.BoardCells); cell++) {
            Assert.True(
                condition: arena.TryWriteBoardCell(
                    cell: cell,
                    reason: out var reason,
                    rowOrdinal: TopologyArenaFixture.Board,
                    value: (cell % 5),
                    write: StateWriteKind.Set
                ),
                userMessage: reason
            );
        }
        for (var member = 0; (member < 6); member++) {
            Assert.True(
                condition: arena.TryMint(
                    key: out _,
                    name: TopologyArenaFixture.Name(value: $"t{member}"),
                    reason: out var reason,
                    rowOrdinal: TopologyArenaFixture.Pile,
                    value: CellValue.Int(value: (member % 3))
                ),
                userMessage: reason
            );
        }

        Assert.Equal(
            actual: catalog.Count,
            expected: 10
        );

        return arena;
    }
    private static StateArena BoardArena(int width, int depth) {
        var section = new StateSection(
            Rows: [new StateRow(
                    Name: TopologyArenaFixture.Name(value: "board"),
                    Kind: CellKind.Int,
                    Domain: new StateDomain.CellsOf(
                        Empty: 0L,
                        Topology: "map"
                    )
                )],
            Lattices: [new LatticeTopology.Grid(
                    Name: "map",
                    Origin: new DocumentVector3(
                        x: 0f,
                        y: 0f,
                        z: 0f
                    ),
                    CellSize: 1f,
                    Width: width,
                    Depth: depth
                )]
        );
        var arena = new StateArena(
            catalog: StateCatalog.Compile(section: section),
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        for (var cell = 0; (cell < (width * depth)); cell++) {
            Assert.True(condition: arena.TryWriteBoardCell(
                cell: cell,
                reason: out var reason,
                rowOrdinal: 0,
                value: ((cell % 7) + 1L),
                write: StateWriteKind.Set
            ), userMessage: reason);
        }
        return arena;
    }

    [Fact]
    public void DeMorganHoldsOverEveryCarrier() {
        var arena = Seeded();
        CellSetExpression[][] pairs = [
            [Board(
                high: 2L,
                low: 0L
            ), Board(
                high: 4L,
                low: 2L
            )],
            [Family(
                high: 1L,
                low: 0L
            ), Family(
                high: 3L,
                low: 1L
            )],
            [Zone(
                high: 0L,
                low: 0L
            ), Zone(
                high: 2L,
                low: 1L
            )],
        ];

        foreach (var pair in pairs) {
            var complementOfUnion = Lower(
                arena: arena,
                expression: new CellSetExpression.Complement(Item: new CellSetExpression.Any(Items: pair))
            );
            var intersectionOfComplements = Lower(
                arena: arena,
                expression: new CellSetExpression.Both(Items: [
                    new CellSetExpression.Complement(Item: pair[0]),
                    new CellSetExpression.Complement(Item: pair[1]),
                ])
            );

            Assert.Equal(
                actual: intersectionOfComplements,
                expected: complementOfUnion
            );

            var complementOfIntersection = Lower(
                arena: arena,
                expression: new CellSetExpression.Complement(Item: new CellSetExpression.Both(Items: pair))
            );
            var unionOfComplements = Lower(
                arena: arena,
                expression: new CellSetExpression.Any(Items: [
                    new CellSetExpression.Complement(Item: pair[0]),
                    new CellSetExpression.Complement(Item: pair[1]),
                ])
            );

            Assert.Equal(
                actual: unionOfComplements,
                expected: complementOfIntersection
            );
        }
    }
    [Fact]
    public void ASetMeetsItsComplementInNothingAndJoinsItIntoEverything() {
        var arena = Seeded();
        var source = Board(
            high: 2L,
            low: 0L
        );
        var meet = Lower(
            arena: arena,
            expression: new CellSetExpression.Both(Items: [source, new CellSetExpression.Complement(Item: source)])
        );

        Assert.True(condition: meet.IsEmpty);

        var join = Lower(
            arena: arena,
            expression: new CellSetExpression.Any(Items: [source, new CellSetExpression.Complement(Item: source)])
        );

        Assert.Equal(
            actual: join.Count,
            expected: TopologyArenaFixture.BoardCells
        );
        Assert.Equal(
            actual: join,
            expected: Lower(
                arena: arena,
                expression: new CellSetExpression.Any(Items: [source, new CellSetExpression.Everything()])
            )
        );
    }
    [Fact]
    public void AComplementStopsAtTheCarriersWidth() {
        var arena = Seeded();
        var everything = Lower(
            arena: arena,
            expression: new CellSetExpression.Complement(Item: new CellSetExpression.Any(Items: [
                Family(
                    high: 3L,
                    low: 0L
                ),
                new CellSetExpression.Nothing(),
            ]))
        );

        Assert.True(condition: everything.IsEmpty);

        var nothingComplemented = Lower(
            arena: arena,
            expression: new CellSetExpression.Complement(Item: new CellSetExpression.Any(Items: [
                Family(
                    high: -1L,
                    low: -2L
                ),
                new CellSetExpression.Nothing(),
            ]))
        );

        Assert.Equal(
            actual: nothingComplemented.Count,
            expected: TopologyArenaFixture.FamilySize
        );
        Assert.True(condition: nothingComplemented.Fits(count: TopologyArenaFixture.FamilySize));
    }
    [Fact]
    public void EverySourceReadsTheArenasOwnColumns() {
        var arena = Seeded();
        var board = Board(
            high: 0L,
            low: 0L
        );
        var before = Lower(
            arena: arena,
            expression: board
        );

        Assert.True(condition: arena.TryWriteBoardCell(
            cell: 1,
            reason: out _,
            rowOrdinal: TopologyArenaFixture.Board,
            value: 0L,
            write: StateWriteKind.Set
        ));
        Assert.Equal(
            actual: Lower(
                arena: arena,
                expression: board
            ),
            expected: before.Add(index: 1)
        );

        var zone = Zone(
            high: 0L,
            low: 0L
        );
        var zoneBefore = Lower(
            arena: arena,
            expression: zone
        );

        Assert.True(condition: arena.TryMint(
            key: out _,
            name: TopologyArenaFixture.Name(value: "t9"),
            reason: out _,
            rowOrdinal: TopologyArenaFixture.Pile,
            value: CellValue.Int(value: 0L)
        ));
        Assert.Equal(
            actual: Lower(
                arena: arena,
                expression: zone
            ),
            expected: zoneBefore.Add(index: 6)
        );

        var family = Family(
            high: 9L,
            low: 9L
        );

        Assert.True(condition: Lower(
            arena: arena,
            expression: family
        ).IsEmpty);
        Assert.True(condition: arena.TryWrite(
            key: arena.Catalog.Keys.Intern(name: StateRow.SlotKey),
            operand: 9L,
            reason: out _,
            rowOrdinal: (TopologyArenaFixture.Slots + 2),
            write: StateWriteKind.Set
        ));
        Assert.Equal(
            actual: Lower(
                arena: arena,
                expression: family
            ).Count,
            expected: 1
        );
    }
    [Fact]
    public void MixingCarriersOfDifferentWidthsRefusesByName() {
        var arena = Seeded();

        Assert.False(condition: CellSetLowering.TryLower(
            arena: arena,
            expression: new CellSetExpression.Both(Items: [
                Board(
                    high: 2L,
                    low: 0L
                ),
                Family(
                    high: 3L,
                    low: 0L
                ),
            ]),
            reason: out var reason,
            set: out _
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "slot"
        );
        Assert.False(condition: CellSetLowering.TryElementCount(
            arena: arena,
            elements: out _,
            expression: new CellSetExpression.Complement(Item: new CellSetExpression.Nothing()),
            reason: out var widthless
        ));
        Assert.Contains(
            actualString: widthless,
            expectedSubstring: "width"
        );
    }
    [Fact]
    public void ASourceNamingNoCarrierRefusesByName() {
        var arena = Seeded();

        Assert.False(condition: CellSetLowering.TryLower(
            arena: arena,
            expression: new CellSetExpression.Board(
                High: 1L,
                Low: 0L,
                Row: TopologyArenaFixture.Name(value: "pile")
            ),
            reason: out var notALattice,
            set: out _
        ));
        Assert.Contains(
            actualString: notALattice,
            expectedSubstring: "pile"
        );
        Assert.False(condition: CellSetLowering.TryLower(
            arena: arena,
            expression: new CellSetExpression.Zone(
                High: 1L,
                Low: 0L,
                Row: TopologyArenaFixture.Name(value: "absent")
            ),
            reason: out var absent,
            set: out _
        ));
        Assert.Contains(
            actualString: absent,
            expectedSubstring: "absent"
        );
    }
    [InlineData(63, 1)]
    [InlineData(64, 1)]
    [InlineData(65, 1)]
    [InlineData(255, 1)]
    [InlineData(256, 1)]
    [InlineData(257, 1)]
    [InlineData(19, 19)]
    [InlineData(33, 18)]
    [Theory]
    public void AlgebraAgreesWithAScalarOracleAcrossWordAndInlineBoundaries(int width, int depth) {
        var arena = BoardArena(
            depth: depth,
            width: width
        );
        var cells = (width * depth);
        var expression = new CellSetExpression.Both(Items: [
            new CellSetExpression.Any(Items: [
                Board(high: 2L, low: 1L),
                Board(high: 6L, low: 5L),
            ]),
            new CellSetExpression.Complement(Item: Board(high: 5L, low: 2L)),
        ]);
        var actual = Lower(
            arena: arena,
            expression: expression
        );
        var expectedCount = 0;

        Assert.Equal(
            actual: actual.Length,
            expected: cells
        );
        for (var cell = 0; (cell < cells); cell++) {
            var value = ((cell % 7) + 1L);
            var expected = (((value is >= 1L and <= 2L) || (value is >= 5L and <= 6L)) && (value is not (>= 2L and <= 5L)));

            Assert.Equal(
                actual: actual.Contains(index: cell),
                expected: expected
            );
            if (expected) {
                expectedCount++;
            }
        }
        Assert.Equal(
            actual: actual.Count,
            expected: expectedCount
        );
        Assert.True(condition: actual.Fits(count: cells));
        Assert.False(condition: actual.Contains(index: cells));
        Assert.False(condition: actual.Contains(index: (cells + 63)));
    }
    [Fact]
    public void InlineAlgebraAllocatesNothingAfterWarmup() {
        var arena = BoardArena(
            depth: 1,
            width: 256
        );
        var expression = new CellSetExpression.Complement(Item: Board(
            high: 4L,
            low: 2L
        ));

        _ = Lower(
            arena: arena,
            expression: expression
        );
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var repeat = 0; (repeat < 100); repeat++) {
            _ = Lower(
                arena: arena,
                expression: expression
            );
        }
        Assert.Equal(
            actual: (GC.GetAllocatedBytesForCurrentThread() - before),
            expected: 0L
        );
    }
    [Fact]
    public void AnExplicitWidthPastTheTopologyCeilingIsRefusedBeforeStorageSizing() {
        var arena = Seeded();

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => CellSetLowering.TryLower(
            arena: arena,
            expression: new CellSetExpression.Nothing(),
            elements: int.MaxValue,
            set: out _,
            reason: out _
        ));
    }
}
