namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    /// <summary>Installs a response swap's solid rows (each row showing another creation the document was validated
    /// with): compiles again only what the solid rows make (the contact field, reusing every row that stands; the
    /// line-of-sight field; the navigation runtime over them, carrying each body's route where its bake stands) and hands
    /// the field to every live body. No kit, look, seat or body changes, so <see cref="Revision"/> stands and a client
    /// rebuilds its program once, on the definition the swap delivers.</summary>
    /// <param name="definition">The definition holding the swapped rows.</param>
    /// <param name="solids">The server's pre-built solid field for the field provider or line-of-sight targets, or
    /// <see langword="null"/> when neither reads one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public void InstallResponseSolids(WorldDefinition definition, WorldSolidField? solids) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var previousNavigation = m_navigation;

        CompileSolidConsumers(
            definition: definition,
            previousNavigation: previousNavigation,
            solids: solids
        );

        for (var index = 0; (index < m_entries.Length); index++) {
            RebindNavigation(
                bodyIndex: index,
                previousNavigation: previousNavigation
            );

            if (m_entries[index] is { Active: true, Body: { } body }) {
                body.SetContactConfiguration(
                    field: m_contactField,
                    upPolicy: m_bodyUpPolicy,
                    walkableThreshold: m_walkableThreshold
                );
            }
        }
    }
}
