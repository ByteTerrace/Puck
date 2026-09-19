using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="CellKeyTable"/> interns one ordinal per distinct name in mint order,
/// hands the same key back for a repeated name, renders a key back to its name, refuses a key another table
/// minted, and refuses past <see cref="StateCapacity.MaxCellKeys"/> by name.</summary>
public sealed class CellKeyLawTests {
    private static CellName Name(string candidate) => CellName.Parse(candidate: candidate);

    [Fact]
    public void InterningAssignsOrdinalsInMintOrderAndIsIdempotent() {
        var table = new CellKeyTable();
        var first = table.Intern(name: Name(candidate: "alpha"));
        var second = table.Intern(name: Name(candidate: "beta"));

        Assert.Equal(
            expected: 0,
            actual: first.Ordinal
        );
        Assert.Equal(
            expected: 1,
            actual: second.Ordinal
        );
        Assert.Equal(
            expected: first,
            actual: table.Intern(name: Name(candidate: "alpha"))
        );
        Assert.Equal(
            expected: 2,
            actual: table.Count
        );
    }
    [Fact]
    public void AKeyRendersBackToTheNameThatMintedIt() {
        var table = new CellKeyTable();
        var key = table.Intern(name: Name(candidate: "gamma"));

        Assert.True(condition: table.TryGetName(
            key: key,
            name: out var name
        ));
        Assert.Equal(
            expected: "gamma",
            actual: name.Value
        );
        Assert.Equal(
            expected: "gamma",
            actual: table[key].Value
        );
        Assert.Equal(
            expected: "gamma",
            actual: table.Names[key.Ordinal].Value
        );
    }
    [Fact]
    public void TheDefaultKeyIsInvalidAndNoTableResolvesIt() {
        var table = new CellKeyTable();

        Assert.False(condition: default(CellKey).IsValid);
        Assert.Equal(
            expected: -1,
            actual: default(CellKey).Ordinal
        );
        Assert.False(condition: table.TryGetName(
            key: default,
            name: out _
        ));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => table[default]);
    }
    [Fact]
    public void AKeyFromAnotherTableIsRefusedEvenWhenItsOrdinalFits() {
        var left = new CellKeyTable();
        var right = new CellKeyTable();
        var key = left.Intern(name: Name(candidate: "shared"));

        right.Intern(name: Name(candidate: "shared"));

        Assert.False(condition: right.TryGetName(
            key: key,
            name: out _
        ));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => right[key]);
    }
    [Fact]
    public void ResolvingNeverMints() {
        var table = new CellKeyTable();

        Assert.False(condition: table.TryResolve(
            key: out var missing,
            name: Name(candidate: "absent")
        ));
        Assert.False(condition: missing.IsValid);
        Assert.Equal(
            expected: 0,
            actual: table.Count
        );
    }
    [Fact]
    public void MintingPastTheCeilingRefusesByName() {
        var table = new CellKeyTable();

        for (var index = 0; (index < StateCapacity.MaxCellKeys); index++) {
            Assert.True(condition: table.TryIntern(
                key: out _,
                name: Name(candidate: $"k{index}"),
                reason: out _
            ));
        }

        Assert.False(condition: table.TryIntern(
            key: out var refused,
            name: Name(candidate: "overflow"),
            reason: out var reason
        ));
        Assert.False(condition: refused.IsValid);
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "overflow"
        );
        Assert.Throws<InvalidOperationException>(testCode: () => table.Intern(name: Name(candidate: "overflow")));
    }
    [Fact]
    public void ACatalogIndexesEveryAuthoredCellKeyInDocumentOrder() {
        var catalog = StateCatalog.Compile(section: new StateSection(Rows: [
            new StateRow(
                Name: Name(candidate: "hands"),
                Kind: CellKind.Int,
                Capacity: 4,
                Cells: [
                    new StateCell(Key: Name(candidate: "north"), Value: CellValue.Int(value: 0L)),
                    new StateCell(Key: Name(candidate: "south"), Value: CellValue.Int(value: 0L)),
                ]
            ),
            new StateRow(
                Name: Name(candidate: "scores"),
                Kind: CellKind.Int,
                Capacity: 4,
                Cells: [
                    new StateCell(Key: Name(candidate: "south"), Value: CellValue.Int(value: 0L)),
                    new StateCell(Key: Name(candidate: "east"), Value: CellValue.Int(value: 0L)),
                ]
            ),
        ]));

        Assert.Equal(
            expected: 3,
            actual: catalog.Keys.Count
        );
        Assert.Equal(
            expected: "north",
            actual: catalog.Keys.Names[0].Value
        );
        Assert.Equal(
            expected: "south",
            actual: catalog.Keys.Names[1].Value
        );
        Assert.Equal(
            expected: "east",
            actual: catalog.Keys.Names[2].Value
        );
    }
}
