namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // The changed rows' indices, reused by every selection install.
    private readonly List<int> m_responseRows = [];

    /// <summary>Installs what the response sweep resolved: the live rows take their new
    /// <see cref="WorldPlacement.Holding"/> masks (<see cref="WorldDefinition.WithPlacementHoldings"/>), and the
    /// change reaches every attached sink as a value delivery. A mask is derived state, so the change is neither
    /// journalled nor validated beyond its bits (every creation a row may show was admitted with the row), and it moves
    /// no rule, arena, grant, field, machine, body or input hold. Only a solid row's colliders can follow it, and only
    /// when the creation it now shows compiles to other colliders
    /// (<see cref="WorldPopulation.InstallResponseSolids"/>); a field-quality or line-of-sight world compiles its
    /// solid field again for any solid row.</summary>
    /// <param name="holdings">The rows whose masks moved, as indices into the live placements with their new masks.</param>
    internal void InstallResponseSelection(IReadOnlyList<(int Row, int Holding)> holdings) {
        if (holdings.Count == 0) {
            return;
        }

        var previous = m_definition;
        var definition = previous.WithPlacementHoldings(holdings: holdings);

        m_responseRows.Clear();

        foreach (var (row, _) in holdings) {
            if (definition.Placements[row].Solid is not null) {
                m_responseRows.Add(item: row);
            }
        }

        AdoptDefinition(definition: definition);
        m_pendingStateDelivery = true;

        if (m_responseRows.Count == 0) {
            return;
        }

        if (
            WorldContactSelection.RequiresField(collision: definition.Collision) ||
            WorldTargetSelection.RequiresLineOfSight(definition: definition)
        ) {
            if (!TryBuildSolids(
                definition: definition,
                reason: out var reason,
                solids: out var solids
            )) {
                Host.Output.Narrate(channel: "world.respond", text: $"[world.respond: the solid field keeps the creations shown before; the ones shown now do not compile: {reason}]");

                return;
            }

            AdoptSolidField(solids: solids);
        }

        _ = Host.Population.InstallResponseSolids(
            definition: definition,
            previous: previous,
            rows: m_responseRows,
            solids: m_solids
        );
    }
}
