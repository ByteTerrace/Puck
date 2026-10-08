

namespace Puck.World.Testing;

/// <summary>The census world the publication laws draw from, shared by the suites that publish it.</summary>
internal static class CensusFixtures {
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

    internal const string CensusRow = "census";
}
