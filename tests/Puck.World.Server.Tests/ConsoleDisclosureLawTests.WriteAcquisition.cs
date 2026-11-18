using Xunit;

namespace Puck.World.Server.Tests;

public sealed partial class ConsoleDisclosureLawTests {
    [Theory]
    [InlineData(CellKind.Int, "not-a-number")]
    [InlineData(CellKind.Text, "text with spaces")]
    [InlineData(CellKind.Vector, "not/base64!")]
    public void WriteParsingCannotDistinguishAWithheldDeclarationFromAnAbsentOne(CellKind kind, string payload) {
        var document = Document();
        var absent = document with {
            StateRaw = document.StateRaw! with {
                World = document.AuthoredState.Where(row => row.Name.Value != "vault").ToArray()
            }
        };
        var hidden = document with {
            StateRaw = document.StateRaw! with {
                World = document.AuthoredState.Select(row => row.Name.Value == "vault"
                    ? row with { Kind = kind, Cells = [], Space = kind == CellKind.Vector ? "secretSpace" : null }
                    : row).ToArray(),
                Spaces = kind == CellKind.Vector ? [new StateSpace(name: CellName.Parse("secretSpace"), model: "law", revision: "one", dimensions: 8)] : []
            }
        };
        using var missingHost = new DisclosureHost(absent);
        using var withheldHost = new DisclosureHost(hidden);
        var line = $"world.state.cell.set vault $value {payload}";
        var missing = missingHost.Seat(1, line);
        var withheld = withheldHost.Seat(1, line);
        Assert.True(missing.IsError);
        Assert.Equal(missing, withheld);
        Assert.Contains("not disclosed", missing.Output, StringComparison.Ordinal);
    }
}
