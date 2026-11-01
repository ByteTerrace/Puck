using Xunit;

namespace Puck.World.Server.Tests;

public sealed partial class ConsoleDisclosureLawTests {
    [Theory]
    [InlineData("")]
    [InlineData(" capture")]
    [InlineData(" pose")]
    [InlineData(" world")]
    [InlineData(" authoritative")]
    public void AuthoritativeHashesAnswerOnlyTheOperator(string arguments) {
        using var host = new DisclosureHost(MetadataDocument(1));
        Assert.True(host.Seat(slot: 1, line: "world.state.hash" + arguments).IsError);
        Assert.DoesNotContain("hash=", host.Seat(slot: 1, line: "world.state.hash" + arguments).Output, StringComparison.Ordinal);
        Assert.Contains("hash=", host.Console("world.state.hash" + arguments).Output, StringComparison.Ordinal);
    }
    private static WorldDefinition MetadataDocument(int hiddenCells) {
        var vector = new sbyte[] { 127, 0, 0, 0, 0, 0, 0, 0 };
        return Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(
                Spaces: [new StateSpace(name: CellName.Parse("space"), dimensions: 8, model: "test", revision: "1")],
                World: [new WorldStateRow(
                    Name: CellName.Parse("vectors"), Kind: CellKind.Vector, Space: "space", Domain: StateDomain.Keys.Instance,
                    Visibility: new StateVisibility(Readers: ["seat1", "seat2"], Hidden: HiddenCells.Omit),
                    Cells: [new StateCell(Key: CellName.Parse("open"), Value: CellValue.Vector(vector)),
                        .. Enumerable.Range(0, hiddenCells).Select(index => new StateCell(
                            Key: CellName.Parse($"secret{index}"), Value: CellValue.Vector(vector),
                            Visibility: new StateVisibility(Readers: ["seat1"])))]
                )])
        };
    }

    [Fact]
    public void OmitReadbacksDoNotRevealHiddenCellCountsOrArenaReservations() {
        using var one = new DisclosureHost(MetadataDocument(1));
        using var two = new DisclosureHost(MetadataDocument(2));
        foreach (var command in new[] { "world.state", "world.state vectors", "world.row state vectors", "world.observe seat2" }) {
            var first = one.Seat(slot: 1, line: command).Output;
            Assert.Equal(first, two.Seat(slot: 1, line: command).Output);
            Assert.DoesNotContain("withheld=", first, StringComparison.Ordinal);
            Assert.DoesNotContain("arena ", first, StringComparison.Ordinal);
        }
        Assert.Contains("arena ", one.Console("world.state").Output, StringComparison.Ordinal);
    }

    [Fact]
    public void SimilarityCannotDistinguishAHiddenQueryFromAMissingQuery() {
        using var hidden = new DisclosureHost(MetadataDocument(1));
        using var missing = new DisclosureHost(MetadataDocument(0));
        var command = "world.state.similar vectors secret0 vectors";
        var hiddenAnswer = hidden.Seat(slot: 1, line: command);
        var missingAnswer = missing.Seat(slot: 1, line: command);
        Assert.Equal(missingAnswer, hiddenAnswer);
        Assert.True(hiddenAnswer.IsError);
        var ranking = hidden.Seat(slot: 1, line: "world.state.similar vectors open vectors");
        Assert.False(ranking.IsError);
        Assert.Contains("1 candidate(s)", ranking.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", ranking.Output, StringComparison.Ordinal);
        Assert.Contains("2 candidate(s)", hidden.Seat(slot: 0, line: command).Output, StringComparison.Ordinal);
    }
}
