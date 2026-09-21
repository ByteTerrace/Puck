using Xunit;

namespace Puck.World.Tests;

/// <summary>Search jobs may name authored rows, but never the rows synthesized to store pools.</summary>
public sealed class SearchAuthoringBoundaryLawTests {
    [Fact]
    public void DocumentValidationRefusesGeneratedPoolBackingRowsAsSearchInputs() {
        var actor = CellName.Parse(candidate: "actor");
        var actors = CellName.Parse(candidate: "actors");
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(
                Pools: [new StatePool(Name: actors, Record: actor, Capacity: 2)],
                Records: [new StateRecord(
                    Name: actor,
                    Fields: [new StatePoolField(
                        Name: CellName.Parse(candidate: "score"),
                        Default: CellValue.Int(value: 0L)
                    )]
                )]
            ),
            SearchRaw = new WorldSearchSection(Jobs: [new WorldSearchRow(
                Name: "search",
                Tokens: "$pool_actors_field_score",
                Board: "missing"
            )]),
        };

        Assert.Contains(
            collection: definition.State,
            filter: row => (row.Generated && string.Equals(
                a: row.Name.Value,
                b: "$pool_actors_field_score",
                comparisonType: StringComparison.Ordinal
            ))
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "search 'search' tokens '$pool_actors_field_score' must be a keyed integer row"
        );
    }
}
