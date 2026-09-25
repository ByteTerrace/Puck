using System.Text;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for <see cref="WorldDefinitionLoader.TryReadPublishable"/>, the read every release publication
/// crosses: the definition comes back undrawn, because draws are instance state, and it is refused unless a copy drawn
/// for the boot instance admits.</summary>
public sealed class PublishableDefinitionLawTests {
    private const string CensusRow = "census";

    // A world whose body census reads a state row, so a boot must fill that row before it can admit the document.
    internal static WorldDefinition CensusDefinition(Draw? draw) {
        var document = Fixtures.BuildDocument();

        return (document with {
            PopulationRaw = (document.Population with {
                CapacityRaw = null,
                CapacityRow = CensusRow,
            }),
        }).WithWorldState(rows: [
            .. document.AuthoredState,
            new WorldStateRow(
                Name: CellName.Parse(candidate: CensusRow),
                Kind: CellKind.Int,
                Draw: draw
            ),
        ]);
    }

    private static string CensusWorld(Draw? draw) => Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: CensusDefinition(draw: draw)));

    [Fact]
    public void APublishedDefinitionComesBackUndrawn() {
        var json = CensusWorld(draw: new Draw(
            Generator: new StateGenerator(
                Source: GeneratorSource.WeightedNumeric,
                Weighted: [new GeneratorWeightedNumeric(Value: 8L, Weight: 1UL)]
            ),
            Timing: DrawTiming.Boot
        ));

        Assert.True(
            condition: WorldDefinitionLoader.TryReadPublishable(
                definition: out var published,
                json: json,
                reason: out var reason,
                sourceName: "census-world"
            ),
            userMessage: reason
        );

        var census = Assert.Single(collection: published.AuthoredState, predicate: static row => (row.Name.Value == CensusRow));

        Assert.Empty(collection: (census.Cells ?? []));
        Assert.Equal(
            expected: json,
            actual: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: published))
        );
    }
    [Fact]
    public void ADefinitionNoBootCanDrawIsNotPublishable() {
        var json = CensusWorld(draw: null);

        Assert.False(condition: WorldDefinitionLoader.TryReadPublishable(
            definition: out var published,
            json: json,
            reason: out var reason,
            sourceName: "census-world"
        ));
        Assert.Null(@object: published);
        Assert.Contains(actualString: reason, expectedSubstring: $"bodies.capacityRow '{CensusRow}'");

        // Control: the same bytes parse and pass the undrawn document's own validation, which is all a read that does
        // not draw can check.
        Assert.True(condition: WorldDefinitionFileSource.TryParseDocument(
            definition: out var parsed,
            json: json,
            reason: out var parseReason,
            sourceName: "census-world"
        ), userMessage: parseReason);
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: parsed!, reason: out var validationReason),
            userMessage: validationReason
        );
    }
}
