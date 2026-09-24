using Puck.Abstractions.Counting;
using Xunit;

namespace Puck.State.Tests;

/// <summary>Shape comparison is bounded by declarations, while cached expansion retains each section's values.</summary>
public sealed class CatalogPoolShapeLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateSection Section(int capacity) => new(
        Records: [new StateRecord(Name: Name(value: "Piece"), Fields: [new StatePoolField(Name: Name(value: "hp"))])],
        Pools: [new StatePool(Name: Name(value: "pieces"), Record: Name(value: "Piece"), Capacity: capacity)]
    );

    [Fact]
    public void ValueOnlyPoolReplacementMatchesWithoutPopulationExpansionOrAllocation() {
        var section = Section(capacity: 4096);
        var catalog = StateCatalog.Compile(section: section);
        var replacement = section with {
            Pools = [section.Pools![0] with {
                Initial = [new StatePoolSeed(Slot: 2048, Values: [new StatePoolValue(Field: Name(value: "hp"), Value: CellValue.Int(value: 17))])],
            }],
        };

        for (var warm = 0; (warm < 100); warm++) {
            Assert.True(condition: catalog.MatchesShape(section: replacement));
        }
        var matches = true;

        var before = AllocationWindow.Least(window: () => {
            for (var iteration = 0; (iteration < 1000); iteration++) {
                matches &= catalog.MatchesShape(section: replacement);
            }
        });

        var allocated = before;

        Assert.True(condition: matches);
        Assert.Equal(actual: allocated, expected: 0L);
        var originalRows = StateCatalog.ExpandRows(section: section);

        Assert.Same(expected: originalRows, actual: StateCatalog.ExpandRows(section: section));
        var replacementRows = StateCatalog.ExpandRows(section: replacement);

        Assert.NotSame(actual: replacementRows, expected: originalRows);
        Assert.Empty(collection: originalRows[0].Cells!);
        Assert.Single(collection: replacementRows[0].Cells!);
        Assert.Equal(expected: 17L, actual: replacementRows[2].Cells![0].Value.AsInt);
    }
    [Fact]
    public void FieldAndPoolShapeChangesCannotReuseCatalogHandles() {
        var section = Section(capacity: 8);
        var catalog = StateCatalog.Compile(section: section);

        Assert.False(condition: catalog.MatchesShape(section: section with { Pools = [section.Pools![0] with { Capacity = 16 }] }));
        Assert.False(condition: catalog.MatchesShape(section: section with {
            Records = [new StateRecord(Name: Name(value: "Piece"), Fields: [new StatePoolField(Name: Name(value: "hp"), Min: 0L)])],
        }));
        Assert.False(condition: catalog.MatchesShape(section: section with { Rows = [new StateRow(Name: Name(value: "extra"), Kind: CellKind.Int)] }));
        Assert.False(condition: catalog.MatchesShape(section: section with { Records = [section.Records![0], section.Records[0]] }));
    }
    [Fact]
    public void PairPoolsCompareTheirDeclarationWithoutExpandingTheirIdentityUniverse() {
        var section = Section(capacity: 64) with {
            PairPools = [new StatePairPool(Name: Name(value: "links"), Record: Name(value: "Piece"), LeftPool: Name(value: "pieces"), RightPool: Name(value: "pieces"), MaxLive: 8)],
        };
        var catalog = StateCatalog.Compile(section: section);

        Assert.True(condition: catalog.MatchesShape(section: section with { }));
        Assert.False(condition: catalog.MatchesShape(section: section with { PairPools = [section.PairPools![0] with { MaxLive = 9 }] }));
        Assert.False(condition: catalog.MatchesShape(section: section with { PairPools = [section.PairPools![0] with { Directed = false }] }));
    }
}
