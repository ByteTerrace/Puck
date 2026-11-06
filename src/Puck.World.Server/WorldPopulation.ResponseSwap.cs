using Puck.Maths;
using Puck.Physics.Navigation;

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

        // An analytic set whose sleepers owe no earlier change can bound what the swap moves: the changed rows' colliders
        // before it and after. Bodies outside that box keep resting; any other swap reaches every body.
        var box = default(WorldColliderBox);
        var local = (
            (solids is null) &&
            (m_targetField is null) &&
            m_sleepFieldTermsKnown &&
            (m_sleepFieldVersion == ContactFieldVersion) &&
            (m_baseContactField is WorldColliderSet)
        );

        if (local) { ((WorldColliderSet)m_baseContactField!).FoldRows(box: ref box, definition: previous, rows: rows); }

        var previousNavigation = m_navigation;

        CompileSolidConsumers(
            definition: definition,
            previousNavigation: previousNavigation,
            solids: solids
        );
        if (local && (m_baseContactField is WorldColliderSet rebuilt)) { rebuilt.FoldRows(box: ref box, definition: definition, rows: rows); }
        RebindSolidConsumers(
            previousNavigation: previousNavigation,
            reach: ((local && !box.Unbounded && (m_baseContactField is WorldColliderSet)) ? (box, FieldWakeMargin()) : null)
        );
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
        RebindSolidConsumers(previousNavigation: previousNavigation, reach: null);
    }

    // Hands every live body the navigation and contact field the solid consumers compiled. With no reach every rigid
    // body wakes. With one, every collider the change moved lies inside its box: only the rigid bodies within its margin
    // of that box wake (their rest latch cleared), and the sleepers settle on the new field now, so the change owes the
    // next PrepareBodyStep nothing and a checkpoint between the two finds no sleeper behind the field it rests in.
    private void RebindSolidConsumers(NavigationRuntime previousNavigation, (WorldColliderBox Box, FixedQ4816 Margin)? reach) {
        var sleepersBefore = m_sleepingBodies.Count;

        for (var index = 0; (index < m_entries.Length); index++) {
            RebindNavigation(
                bodyIndex: index,
                previousNavigation: previousNavigation
            );

            if (m_entries[index] is { Active: true, Body: { } body }) {
                body.SetContactConfiguration(
                    field: m_contactField,
                    upPolicy: m_bodyUpPolicy,
                    walkableThreshold: m_walkableThreshold,
                    wake: ((reach is not { } changed) || FieldChangeReaches(box: in changed.Box, body: body, margin: changed.Margin))
                );
            }
        }
        if (reach is null) { return; }
        var version = ContactFieldVersion;

        WorldBodyWork.Process.Add(amount: (sleepersBefore - m_sleepingBodies.Count), kind: WorldBodyWork.FieldWakes);
        WorldBodyWork.Process.Add(amount: m_sleepingBodies.Count, kind: WorldBodyWork.FieldObserved);
        m_sleepFieldVersion = version;
        RecordSleepFieldObservation(known: true);
    }
}
