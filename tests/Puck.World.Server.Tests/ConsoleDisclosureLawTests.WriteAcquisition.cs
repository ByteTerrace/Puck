using Xunit;

namespace Puck.World.Server.Tests;

public sealed partial class ConsoleDisclosureLawTests {
    [Theory]
    [InlineData("int", "42", "42")]
    [InlineData("int", "42 add", "42")]
    [InlineData("text", "text with spaces", "text with spaces")]
    [InlineData("text", "text with add", "text with add")]
    public void ASeatCellWriteComposesAfterTheConsolesQueuedDeclaration(string kind, string payload, string expected) {
        using var host = new DisclosureHost(Document());
        var declaration = host.Console($"world.row.set state {{\"name\":\"newRow\",\"kind\":\"{kind}\"}}");
        Assert.False(declaration.IsError, declaration.Output);
        var write = host.Seat(1, $"world.state.cell.set newRow $value {payload}", settle: true);
        Assert.False(write.IsError, write.Output);
        Assert.False(host.LastEcho!.Value.Rejected);
        Assert.Contains(expected, host.Seat(1, "world.state newRow").Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CellKind.Text, "int", "7 add", "7")]
    [InlineData(CellKind.Int, "text", "words with add", "words with add")]
    public void CellWriteUsesTheReplacementKindWhenTheRowWasAlreadyLive(CellKind before, string after, string payload, string expected) {
        var definition = Document();
        using var host = new DisclosureHost(definition.WithWorldState([.. definition.AuthoredState, new WorldStateRow(CellName.Parse("changing"), before)]));
        Assert.False(host.Console($"world.row.set state {{\"name\":\"changing\",\"kind\":\"{after}\"}}").IsError);
        var result = host.Seat(1, $"world.state.cell.set changing $value {payload}", settle: true);
        Assert.False(result.IsError, result.Output);
        Assert.False(host.LastEcho!.Value.Rejected);
        Assert.Contains(expected, host.Seat(1, "world.state changing").Output, StringComparison.Ordinal);
    }

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
        var missing = missingHost.Seat(1, line, settle: true);
        var withheld = withheldHost.Seat(1, line, settle: true);
        Assert.True(missing.IsError);
        Assert.Equal(missing, withheld);
        Assert.Contains("not disclosed", missing.Output, StringComparison.Ordinal);
        Assert.True(missingHost.LastEcho!.Value.Denied);
        Assert.True(withheldHost.LastEcho!.Value.Denied);
        Assert.Equal(payload, Assert.IsType<Puck.World.Protocol.WorldMutation.UpsertStateCell>(
            missingHost.LastEcho.Value.Mutation).RawToken);
    }
}
