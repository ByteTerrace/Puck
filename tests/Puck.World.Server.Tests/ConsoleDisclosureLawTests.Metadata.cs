using Xunit;

namespace Puck.World.Server.Tests;

public sealed partial class ConsoleDisclosureLawTests {
    [Fact]
    public void SummariesDoNotExposePrivateGeneratedPoolStorage() {
        var document = MetadataDocument(0);
        document = document with { StateRaw = document.StateRaw! with {
            Records = [new StateRecord(Name: CellName.Parse("memory"), Fields: [new StatePoolField(
                Name: CellName.Parse("vector"), Kind: CellKind.Vector, Space: CellName.Parse("space"), Dimensions: 8,
                Default: CellValue.Vector(new sbyte[] { 127, 0, 0, 0, 0, 0, 0, 0 }))])],
            Pools = [new StatePool(Name: CellName.Parse("memories"), Record: CellName.Parse("memory"), Capacity: 2, Initial: [new StatePoolSeed(Slot: 0)])]
        } };
        var generated = document.State.Single(row => row.Generated && row.Kind == CellKind.Vector).Name.Value;
        using var host = new DisclosureHost(document);
        Assert.DoesNotContain(generated, host.Seat(slot: 1, line: "world.state").Output, StringComparison.Ordinal);
        Assert.True(host.Seat(slot: 1, line: $"world.row state {generated}").IsError);
        Assert.True(host.Seat(slot: 1, line: $"world.state.similar vectors open {generated}").IsError);
        Assert.True(host.Seat(slot: 1, line: $"world.state.similar {generated} 0 vectors").IsError);
        Assert.DoesNotContain(generated, host.Console("world.state").Output, StringComparison.Ordinal);
        Assert.True(host.Console($"world.state.similar vectors open {generated}").IsError);
    }
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
        using var zero = new DisclosureHost(MetadataDocument(0));
        using var one = new DisclosureHost(MetadataDocument(1));
        using var two = new DisclosureHost(MetadataDocument(2));
        foreach (var command in new[] { "world.state", "world.state vectors", "world.row state vectors", "world.observe seat2" }) {
            var first = one.Seat(slot: 1, line: command).Output;
            Assert.Equal(zero.Seat(slot: 1, line: command).Output, first);
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
