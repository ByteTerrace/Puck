using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="ArenaLayout"/> gives every row sharing a lane, a shape, and a cell kind
/// one contiguous column; gives every declared key of a stored row a cell offset; sizes ring, ordered, vector, and
/// draw rows from their own declarations; records which derived boards each token and code row feeds; and gives a
/// host-owned row a descriptor with no columns.</summary>
public sealed class ArenaLayoutLawTests {
    [Fact]
    public void EveryColumnIsOneContiguousRunOfOneLaneShapeAndKind() {
        var (catalog, arena) = ArenaFixture.Build();
        var layout = arena.Layout;
        var seen = new HashSet<(StateLane, RowShape, CellKind)>();
        var next = 0;

        foreach (var column in layout.Columns) {
            Assert.True(condition: seen.Add(item: (column.Lane, column.Shape, column.Kind)));
            Assert.Equal(
                expected: next,
                actual: column.FirstCell
            );

            var cells = 0;

            for (var index = column.FirstRow; (index < (column.FirstRow + column.RowCount)); index++) {
                var ordinal = layout.ColumnRows[index];
                var descriptor = catalog.Descriptors[ordinal];

                Assert.Equal(
                    expected: column.Kind,
                    actual: descriptor.Kind
                );
                Assert.Equal(
                    expected: column.Lane,
                    actual: descriptor.Lane
                );
                Assert.Equal(
                    expected: column.Shape,
                    actual: descriptor.Shape
                );

                cells += layout[ordinal].CellCapacity;
            }

            Assert.Equal(
                expected: cells,
                actual: column.CellCount
            );

            next += column.CellCount;
        }

        Assert.Equal(
            expected: layout.CellSlotCount,
            actual: next
        );
    }
    [Fact]
    public void EveryRowIsSizedFromItsOwnDeclaration() {
        var (_, arena) = ArenaFixture.Build();
        var layout = arena.Layout;

        Assert.Equal(
            expected: 1,
            actual: layout[ArenaFixture.Score].CellCapacity
        );
        Assert.Equal(
            expected: 4,
            actual: layout[ArenaFixture.Tokens].CellCapacity
        );
        Assert.Equal(
            expected: 4,
            actual: layout[ArenaFixture.Board].CellCapacity
        );
        Assert.Equal(
            expected: 3,
            actual: layout[ArenaFixture.History].CellCapacity
        );
        Assert.Equal(
            expected: 4,
            actual: layout[ArenaFixture.Deck].CellCapacity
        );
        Assert.Equal(
            expected: 8,
            actual: layout[ArenaFixture.Embed].Dimensions
        );
        Assert.Equal(
            expected: 8,
            actual: layout.VectorByteCount
        );
        Assert.Equal(
            expected: ArenaFixture.Tokens,
            actual: layout[ArenaFixture.Deck].DomainOrdinal
        );
        Assert.True(condition: (layout[ArenaFixture.Deal].MaskWordStart >= 0));
    }
    // A site's mask run is a function of its declaration: an inline source says how many masks its draws persist,
    // and a named source, which lives in a section the arena does not read, reserves the ceiling.
    [Fact]
    public void ADrawSiteReservesTheMasksItsDeclaredSourcePersists() {
        var (_, arena) = ArenaFixture.Build();
        var layout = arena.Layout;

        Assert.Equal(
            actual: layout[ArenaFixture.Deal].MaskCount,
            expected: 2
        );
        Assert.Equal(
            actual: layout.MaskWordCount,
            expected: 8
        );
        Assert.True(condition: arena.TryWriteDrawnMask(
            index: 1,
            mask: new ClosedBitset256(
                Word0: 3UL,
                Word1: 0UL,
                Word2: 0UL,
                Word3: 8UL
            ),
            rowOrdinal: ArenaFixture.Deal
        ));
        Assert.False(condition: arena.TryWriteDrawnMask(
            index: 2,
            mask: new ClosedBitset256(
                Word0: 1UL,
                Word1: 0UL,
                Word2: 0UL,
                Word3: 0UL
            ),
            rowOrdinal: ArenaFixture.Deal
        ));
        Assert.Equal(
            actual: arena.DrawnMask(
                index: 2,
                rowOrdinal: ArenaFixture.Deal
            ),
            expected: default
        );
    }
    [Fact]
    public void ANamedSourceReservesTheCeilingAndANonExhaustingSourceReservesNothing() {
        var section = new StateSection(Rows: [
            new StateRow(
                Name: ArenaFixture.Name(value: "named"),
                Kind: CellKind.Int,
                Draw: new Draw(Source: ArenaFixture.Name(value: "pool"))
            ),
            new StateRow(
                Name: ArenaFixture.Name(value: "dice"),
                Kind: CellKind.Int,
                Draw: new Draw(Generator: new StateGenerator(
                    RangeMax: 6L,
                    RangeMin: 1L,
                    Source: GeneratorSource.UniformRange
                ))
            ),
            new StateRow(
                Name: ArenaFixture.Name(value: "bag"),
                Kind: CellKind.Int,
                Draw: new Draw(Generator: (ArenaFixture.Walk() with { Mode = GeneratorMode.WithReplacement }))
            ),
        ]);
        var layout = ArenaLayout.Build(
            catalog: StateCatalog.Compile(section: section),
            options: null,
            section: section
        );

        Assert.Equal(
            actual: layout[0].MaskCount,
            expected: ArenaCapacity.MaxDrawnMasks
        );
        Assert.Equal(
            actual: layout[1].MaskCount,
            expected: 0
        );
        Assert.Equal(
            actual: layout[2].MaskCount,
            expected: 0
        );
        Assert.Equal(
            actual: layout.MaskWordCount,
            expected: (ArenaCapacity.MaxDrawnMasks * 4)
        );
    }
    [Fact]
    public void AuthoredMasksBeyondWhatTheDeclaredSourcePersistsRefuseByName() {
        var section = new StateSection(Rows: [
            new StateRow(
                Name: ArenaFixture.Name(value: "deal"),
                Kind: CellKind.Int,
                Draw: new Draw(Generator: ArenaFixture.Walk()),
                DrawnMasks: [
                    default,
                    default,
                    default,
                ]
            ),
        ]);
        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => ArenaLayout.Build(
            catalog: StateCatalog.Compile(section: section),
            options: null,
            section: section
        ));

        Assert.Contains(
            actualString: refusal.Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "'deal' carries 3 drawn masks, more than the 2"
        );
    }
    [Fact]
    public void EveryDeclaredKeyOfAStoredRowHasACellOffset() {
        var (catalog, arena) = ArenaFixture.Build();
        var start = arena.Layout[ArenaFixture.Tokens].CellStart;

        Assert.True(condition: arena.TryCellSlot(
            key: ArenaFixture.Key(
                catalog: catalog,
                value: "a"
            ),
            rowOrdinal: ArenaFixture.Tokens,
            slot: out var first
        ));
        Assert.True(condition: arena.TryCellSlot(
            key: ArenaFixture.Key(
                catalog: catalog,
                value: "b"
            ),
            rowOrdinal: ArenaFixture.Tokens,
            slot: out var second
        ));
        Assert.Equal(
            actual: first,
            expected: start
        );
        Assert.Equal(
            actual: second,
            expected: (start + 1)
        );
        Assert.True(condition: arena.TryCellSlot(
            key: ArenaFixture.SlotKey(catalog: catalog),
            rowOrdinal: ArenaFixture.Score,
            slot: out _
        ));
        Assert.True(condition: arena.TryCellSlot(
            key: ArenaFixture.Key(
                catalog: catalog,
                value: "2"
            ),
            rowOrdinal: ArenaFixture.Board,
            slot: out var cell
        ));
        Assert.Equal(
            expected: (arena.Layout[ArenaFixture.Board].CellStart + 2),
            actual: cell
        );
    }
    [Fact]
    public void AHostOwnedRowHasADescriptorAndNoColumns() {
        var (catalog, arena) = ArenaFixture.Build();

        Assert.True(condition: catalog.Descriptors[ArenaFixture.Field].HostOwned);
        Assert.True(condition: arena.Layout[ArenaFixture.Field].HostOwned);
        Assert.False(condition: arena.Layout[ArenaFixture.Field].IsStored);
        Assert.Equal(
            expected: 0,
            actual: arena.Layout[ArenaFixture.Field].CellCapacity
        );
        Assert.False(condition: arena.TryWriteBoardCell(
            cell: 0,
            reason: out var reason,
            rowOrdinal: ArenaFixture.Field,
            value: 1L,
            write: StateWriteKind.Set
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "field"
        );
    }
    [Fact]
    public void ADerivedBoardRecordsTheRowsItIsFedBy() {
        var (_, arena) = ArenaFixture.Build();
        var layout = arena.Layout;

        Assert.True(condition: layout[ArenaFixture.Board].IsDerivedBoard);
        Assert.Equal(
            expected: ArenaFixture.Tokens,
            actual: layout[ArenaFixture.Board].InverseTokensOrdinal
        );
        Assert.Equal(
            expected: ArenaFixture.Codes,
            actual: layout[ArenaFixture.Board].InverseCodesOrdinal
        );
        Assert.Equal(
            expected: ArenaFixture.Board,
            actual: Assert.Single(collection: layout.DependentBoardsOfTokens(tokensOrdinal: ArenaFixture.Tokens)!)
        );
        Assert.Equal(
            expected: ArenaFixture.Board,
            actual: Assert.Single(collection: layout.DependentBoardsOfCodes(codesOrdinal: ArenaFixture.Codes)!)
        );
    }
    [Fact]
    public void EveryColumnClaimsADisjointRangeOfTheChangeSlotSpace() {
        var (_, arena) = ArenaFixture.Build();
        var layout = arena.Layout;
        var next = 0;

        foreach (var column in ArenaColumns.All) {
            Assert.Equal(
                expected: next,
                actual: layout.ChangeBase(column: column)
            );

            next += layout.Size(column: column);
        }

        Assert.Equal(
            expected: layout.ChangeSlotCount,
            actual: next
        );
    }
    [Fact]
    public void TheSectionsAuthoredValuesSeedTheColumnsWithEveryCounterAtZero() {
        var (catalog, arena) = ArenaFixture.Build();

        Assert.Equal(
            expected: CellValue.Int(value: 5L),
            actual: arena.Read(
                key: ArenaFixture.SlotKey(catalog: catalog),
                rowOrdinal: ArenaFixture.Score
            )
        );
        Assert.Equal(
            expected: CellValue.Text(value: "hi"),
            actual: arena.Read(
                key: ArenaFixture.SlotKey(catalog: catalog),
                rowOrdinal: ArenaFixture.Label
            )
        );
        Assert.Equal(
            expected: 2,
            actual: arena.CellCount(rowOrdinal: ArenaFixture.Deck)
        );

        for (var ordinal = 0; (ordinal < arena.Layout.RowCount); ordinal++) {
            Assert.Equal(
                expected: 0UL,
                actual: arena.RowGeneration(rowOrdinal: ordinal)
            );
            Assert.Equal(
                expected: 0UL,
                actual: arena.RowVersion(rowOrdinal: ordinal)
            );
        }
    }
}
