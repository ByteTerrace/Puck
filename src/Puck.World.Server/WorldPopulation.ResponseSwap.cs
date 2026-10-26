namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    /// <summary>Takes a response change into what the population compiles from the solid rows. Each changed row shows
    /// another creation the document was validated with; when every such creation compiles to the colliders the row
    /// already had (a lit and an unlit floor), the contact field, the navigation runtime and every body stand as they
    /// are. Otherwise the solid consumers compile again (the contact field reusing every row that stands, the
    /// line-of-sight field, the navigation runtime carrying each body's route where its bake stands) and every live
    /// body takes the new field. No kit, look, seat or body changes either way, so <see cref="Revision"/> stands.</summary>
    /// <param name="previous">The definition the population last compiled its solid consumers over.</param>
    /// <param name="definition">The definition holding the changed rows.</param>
    /// <param name="rows">The indices of the rows whose shown creation moved.</param>
    /// <param name="solids">The server's solid field for the field provider or line-of-sight targets, built over
    /// <paramref name="definition"/>, or <see langword="null"/> when neither reads one.</param>
    /// <returns><see langword="true"/> when a collider moved and the solid consumers compiled again.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="previous"/>, <paramref name="definition"/> or
    /// <paramref name="rows"/> is <see langword="null"/>.</exception>
    public bool InstallResponseSolids(WorldDefinition previous, WorldDefinition definition, IReadOnlyList<int> rows, WorldSolidField? solids) {
        ArgumentNullException.ThrowIfNull(argument: previous);
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: rows);

        if (
            (solids is null) &&
            (m_targetField is null) &&
            (m_baseContactField is WorldColliderSet set) &&
            set.TryCarryAcrossHolding(
                definition: definition,
                previous: previous,
                rows: rows
            )
        ) {
            m_contactCensus = null;
            m_censusDefinition = definition;
            m_adjacencyDefinition = definition;

            return false;
        }

        InstallProjectedSolids(definition: definition, solids: solids);
        return true;
    }

    /// <summary>Recompiles solid consumers after a derived placement set changes, retaining bodies and grants.</summary>
    public void InstallProjectedSolids(WorldDefinition definition, WorldSolidField? solids) {
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
