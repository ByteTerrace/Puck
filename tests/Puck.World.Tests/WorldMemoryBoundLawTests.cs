using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: a document's state is bounded by what its arena occupies, not by how many rows or
/// board cells it declares. A document whose rows each fit their own ceilings and together lay out past
/// <see cref="ArenaCapacity.MaxBytes"/> is refused at validation, naming the row that crossed and what to change; the
/// same rows in a number that fits validate.</summary>
public sealed class WorldMemoryBoundLawTests {
    private static WorldDefinition Document(int rings) => Fixtures.BuildDocument() with {
        StateRaw = new(World: [.. Enumerable.Range(
                count: rings,
                start: 0
            ).Select(selector: static index => new WorldStateRow(
                CellName.Parse(candidate: $"history{index}"),
                CellKind.Int,
                Domain: new StateDomain.Ring(StateCapacity.MaxCellsPerRow)
            ))]),
    };

    [Fact]
    public void RowsThatFitTheirOwnCeilingsAndNotTheArenasAreRefusedAtTheRowThatCrossed() {
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: Document(rings: 8),
                reason: out var fits
            ),
            userMessage: fits
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(rings: (StateCapacity.MaxRows / 2)),
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: $"{ArenaCapacity.MaxBytes}-byte ceiling"
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "State row 'history"
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "lower a row's capacity"
        );
    }
}
