using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="StateRow.TryAdmitKind"/> refuses a <see cref="CellValue"/> whose own
/// case differs from the row's declared <see cref="CellKind"/>, and refuses a carrier holding no case at all —
/// admitting only a value whose <see cref="CellValue.Kind"/> is exactly the row's own. The arena's cell import door
/// (<c>ArenaImport.TryAdmitImport</c>) decides every imported cell through this same method.</summary>
public sealed class StateRowKindAdmissionLawTests {
    private static StateRow Row(CellKind kind) => new(
        Name: CellName.Parse(candidate: "gauge"),
        Kind: kind
    );

    [InlineData(CellKind.Int, CellKind.Fixed)]
    [InlineData(CellKind.Int, CellKind.Bool)]
    [InlineData(CellKind.Int, CellKind.Text)]
    [InlineData(CellKind.Int, CellKind.Vector)]
    [InlineData(CellKind.Text, CellKind.Int)]
    [InlineData(CellKind.Vector, CellKind.Text)]
    [InlineData(CellKind.Bool, CellKind.Fixed)]
    [Theory]
    public void AValueOfAnotherCaseIsRefusedByName(CellKind rowKind, CellKind valueKind) {
        var row = Row(kind: rowKind);
        var value = (valueKind switch {
            CellKind.Int => CellValue.Int(value: 1L),
            CellKind.Fixed => CellValue.Fixed(rawBits: 1L),
            CellKind.Bool => CellValue.Bool(value: true),
            CellKind.Text => CellValue.Text(value: "x"),
            _ => CellValue.Vector(components: new sbyte[8]),
        });

        Assert.False(condition: row.TryAdmitKind(
            reason: out var reason,
            value: value
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: rowKind.ToString()
        );
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: valueKind.ToString()
        );
    }
    [InlineData(CellKind.Int)]
    [InlineData(CellKind.Fixed)]
    [InlineData(CellKind.Bool)]
    [InlineData(CellKind.Text)]
    [InlineData(CellKind.Vector)]
    [Theory]
    public void ACarrierHoldingNoCaseIsRefusedByName(CellKind rowKind) {
        var row = Row(kind: rowKind);

        Assert.False(condition: row.TryAdmitKind(
            reason: out var reason,
            value: default
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "no case"
        );
    }
    [InlineData(CellKind.Int)]
    [InlineData(CellKind.Fixed)]
    [InlineData(CellKind.Bool)]
    [InlineData(CellKind.Text)]
    [InlineData(CellKind.Vector)]
    [Theory]
    public void AValueOfTheRowsOwnKindIsAdmitted(CellKind rowKind) {
        var row = Row(kind: rowKind);
        var value = (rowKind switch {
            CellKind.Int => CellValue.Int(value: 1L),
            CellKind.Fixed => CellValue.Fixed(rawBits: 1L),
            CellKind.Bool => CellValue.Bool(value: true),
            CellKind.Text => CellValue.Text(value: "x"),
            _ => CellValue.Vector(components: new sbyte[8]),
        });

        Assert.True(condition: row.TryAdmitKind(
            reason: out var reason,
            value: value
        ));
        Assert.Equal(
            actual: reason,
            expected: string.Empty
        );
    }
    // The arena's own import door decides every imported cell through this method (ArenaImport.TryAdmitImport):
    // this is a control proving the wiring still admits a well-formed vector row, and still refuses a vector cell
    // carrying no StateVector, exactly as it did before this door started asking TryAdmitKind too.
    [Fact]
    public void TheArenaImportDoorStillAdmitsAWellFormedVectorCellAndRefusesOneCarryingNone() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        Assert.True(condition: arena.TryLoad(
            reason: out var admitReason,
            rows: [new StateRow(
                    Name: ArenaFixture.Name(value: "embed"),
                    Kind: CellKind.Vector,
                    Cells: [new StateCell(
                            Key: StateRow.SlotKey,
                            Value: CellValue.Vector(components: ArenaFixture.Unit(axis: 0).Memory)
                        )]
                )],
            time: ArenaTime.Origin
        ), userMessage: admitReason);

        Assert.False(condition: arena.TryLoad(
            reason: out var refuseReason,
            rows: [new StateRow(
                    Name: ArenaFixture.Name(value: "embed"),
                    Kind: CellKind.Vector,
                    Cells: [new StateCell(
                            Key: StateRow.SlotKey,
                            Value: default
                        )]
                )],
            time: ArenaTime.Origin
        ));
        Assert.Contains(
            actualString: refuseReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "embed"
        );
    }
}
