using Xunit;

namespace Puck.World.Browser.Tests;

/// <summary>The read-back wire carries one cell value as the tag-and-payload pair <c>CellValue</c> is, so two cells
/// holding the same 64-bit word under different kinds never read back the same.</summary>
public sealed class BrowserCellValueWireTests {
    private static WorldDefinition Document(params WorldStateRow[] rows) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: rows)
    );
    private static WorldStateRow Row(string name, CellKind kind, StateCell cell) => new(
        Capacity: 4,
        Cells: [cell],
        Kind: kind,
        Name: CellName.Parse(candidate: name)
    );

    [Fact]
    public void ReadRow_NamesTheKindItCarries() {
        var session = new BrowserSession(definition: Document(
            Row(
                cell: new StateCell(
                    Key: CellName.Parse(candidate: "k"),
                    Value: CellValue.Int(value: 1)
                ),
                kind: CellKind.Int,
                name: "counter"
            ),
            Row(
                cell: new StateCell(
                    Key: CellName.Parse(candidate: "k"),
                    Value: CellValue.Bool(value: true)
                ),
                kind: CellKind.Bool,
                name: "flag"
            ),
            Row(
                cell: new StateCell(
                    Key: CellName.Parse(candidate: "k"),
                    Value: CellValue.Fixed(rawBits: 98304)
                ),
                kind: CellKind.Fixed,
                name: "rate"
            ),
            Row(
                cell: new StateCell(
                    Key: CellName.Parse(candidate: "k"),
                    Value: CellValue.Text(value: "ready")
                ),
                kind: CellKind.Text,
                name: "label"
            )
        ));

        var counter = session.ReadRow(
            key: "k",
            row: "counter"
        );
        var flag = session.ReadRow(
            key: "k",
            row: "flag"
        );
        var rate = session.ReadRow(
            key: "k",
            row: "rate"
        );
        var label = session.ReadRow(
            key: "k",
            row: "label"
        );

        Assert.True(condition: (counter.Found && flag.Found && rate.Found && label.Found));
        Assert.Equal(
            expected: (nameof(CellKind.Int), "1"),
            actual: (counter.Kind, counter.Value)
        );
        // The same stored word as the counter above: only the kind tells the two apart, which is the whole reason
        // the payload carries one.
        Assert.Equal(
            expected: (nameof(CellKind.Bool), "true"),
            actual: (flag.Kind, flag.Value)
        );
        // Raw FixedQ4816 bits, never a decimal reading of them — the same channel WriteRow takes.
        Assert.Equal(
            expected: (nameof(CellKind.Fixed), "98304"),
            actual: (rate.Kind, rate.Value)
        );
        Assert.Equal(
            expected: (nameof(CellKind.Text), "ready"),
            actual: (label.Kind, label.Value)
        );
    }
    [Fact]
    public void ReadRow_AnAddressNoRowAnswers_CarriesNothing() {
        var session = new BrowserSession(definition: Document(Row(
            cell: new StateCell(
                Key: CellName.Parse(candidate: "k"),
                Value: CellValue.Int(value: 1)
            ),
            kind: CellKind.Int,
            name: "counter"
        )));

        var missing = session.ReadRow(
            key: "k",
            row: "absent"
        );

        Assert.False(condition: missing.Found);
        Assert.Null(@object: missing.Kind);
        Assert.Null(@object: missing.Value);
    }
    [Fact]
    public void Rows_SpellsEachCellUnderItsOwnRowKind() {
        var session = new BrowserSession(definition: Document(
            Row(
                cell: new StateCell(
                    Key: CellName.Parse(candidate: "k"),
                    Value: CellValue.Bool(value: true)
                ),
                kind: CellKind.Bool,
                name: "flag"
            ),
            Row(
                cell: new StateCell(
                    Key: CellName.Parse(candidate: "k"),
                    Value: CellValue.Text(value: "ready")
                ),
                kind: CellKind.Text,
                name: "label"
            )
        ));

        var rows = session.Rows();

        Assert.Equal(
            expected: (nameof(CellKind.Bool), "true"),
            actual: (rows[0].Kind, rows[0].Cells[0].Value)
        );
        Assert.Equal(
            expected: (nameof(CellKind.Text), "ready"),
            actual: (rows[1].Kind, rows[1].Cells[0].Value)
        );
    }
}
