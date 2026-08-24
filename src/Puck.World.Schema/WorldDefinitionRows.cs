namespace Puck.World;

/// <summary>Resolves stable row names in world-definition collections.</summary>
public static class WorldDefinitionRows {
    // The one linear scan every Find* below composes: allocation-free (selector is a static, capture-free lambda, so
    // the compiler caches ONE delegate instance rather than allocating one per call) and null-tolerant, so a
    // section's own nullability is never a second thing a caller must guard before resolving a name against it.
    private static T? Find<T>(IReadOnlyList<T>? rows, string name, Func<T, string> selector) {
        foreach (var row in (rows ?? [])) {
            if (
                ((object?)row is not null) &&
                string.Equals(
                a: selector(row),
                b: name,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return row;
            }
        }

        return default;
    }

    /// <summary>Finds an adjacency row by stable name.</summary>
    public static WorldAdjacency? FindAdjacency(IReadOnlyList<WorldAdjacency>? adjacencies, string name) => Find(
        rows: adjacencies,
        name: name,
        selector: static adjacency => adjacency.Name.Value
    );
    /// <summary>Finds a state cell by key in a (possibly absent) cell list.</summary>
    /// <param name="cells">The row's cells, or <see langword="null"/> for a row declaring none.</param>
    /// <param name="key">The cell key to find.</param>
    /// <returns>The cell, or <see langword="null"/> when none carries that key.</returns>
    public static WorldStateCell? FindCell(IReadOnlyList<WorldStateCell>? cells, WorldCellName key) {
        foreach (var cell in (cells ?? [])) {
            if (cell.Key == key) {
                return cell;
            }
        }

        return null;
    }
    /// <summary>Finds a creation by stable id.</summary>
    /// <param name="creations">The section's creations.</param>
    /// <param name="id">The creation id to find.</param>
    /// <returns>The creation, or <see langword="null"/> when the section declares none by that id.</returns>
    public static WorldPrototype? FindCreation(IReadOnlyList<WorldPrototype>? creations, string id) => Find(
        rows: creations,
        name: id,
        selector: static creation => creation.Id
    );
    /// <summary>Finds a destinations row by stable name — the primitive a portal facet's <c>destination</c> resolves
    /// against (see <see cref="WorldPlacementPortal.Destination"/>).</summary>
    /// <param name="destinations">The section's destinations, or <see langword="null"/> for a document declaring none.</param>
    /// <param name="name">The destination name to find.</param>
    /// <returns>The destination, or <see langword="null"/> when the section declares none by that name.</returns>
    public static WorldDestination? FindDestination(IReadOnlyList<WorldDestination>? destinations, string name) => Find(
        rows: destinations,
        name: name,
        selector: static destination => destination.Name.Value
    );
    /// <summary>Finds a dynamics row by stable name.</summary>
    /// <param name="dynamics">The section's dynamics rows.</param>
    /// <param name="name">The dynamics row name to find.</param>
    /// <returns>The row, or <see langword="null"/> when the section declares none by that name.</returns>
    public static WorldDynamicsRow? FindDynamics(IReadOnlyList<WorldDynamicsRow>? dynamics, string name) => Find(
        rows: dynamics,
        name: name,
        selector: static row => row.Name
    );
    /// <summary>Resolves an entity's look row: <paramref name="rows"/> indexed at <paramref name="index"/>, or the
    /// implicit single catalog look (<see cref="WorldLook.Implicit"/>) when the world authors no <c>looks</c>
    /// section, or for an index no declared row covers.</summary>
    /// <param name="rows">The world's declared look rows (see <see cref="WorldDefinition.Looks"/>).</param>
    /// <param name="index">The entity's resolved look-row index.</param>
    public static WorldLook ResolveLook(IReadOnlyList<WorldLook> rows, int index) => (((index >= 0) && (index < rows.Count))
        ? rows[index]
        : WorldLook.Implicit
    );
    /// <summary>Resolves the world's whole look table: the declared rows, or a single-row table holding just the
    /// implicit catalog look when the world declares none — so a consumer that materializes a fixed table up front
    /// (rather than resolving per entity through <see cref="ResolveLook"/>) never holds an empty one.</summary>
    /// <param name="looks">The world's declared look rows (see <see cref="WorldDefinition.Looks"/>).</param>
    public static IReadOnlyList<WorldLook> ResolveLookRows(IReadOnlyList<WorldLook> looks) => ((looks.Count > 0)
        ? looks
        : [WorldLook.Implicit]
    );
    /// <summary>Finds a kit by stable name.</summary>
    /// <param name="kits">The section's kits.</param>
    /// <param name="name">The kit name to find.</param>
    /// <returns>The kit, or <see langword="null"/> when the section declares none by that name.</returns>
    public static WorldKit? FindKit(IReadOnlyList<WorldKit>? kits, string name) => Find(
        rows: kits,
        name: name,
        selector: static kit => kit.Name
    );
    /// <summary>Finds a placement by stable id.</summary>
    /// <param name="placements">The section's placements.</param>
    /// <param name="id">The placement id to find.</param>
    /// <returns>The placement, or <see langword="null"/> when the section declares none by that id.</returns>
    public static WorldPlacement? FindPlacement(IReadOnlyList<WorldPlacement>? placements, string id) => Find(
        rows: placements,
        name: id,
        selector: static placement => placement.Id
    );
    /// <summary>Finds a placement's declared face by name — the primitive a
    /// <see cref="WorldPlacementPortal.Counterpart"/> resolves its face half against (see
    /// <see cref="WorldPortalCounterpart"/>), and every other placement/face reader (<c>world.portals</c>,
    /// <c>world.faces</c>) already walks by hand.</summary>
    /// <param name="placement">The placement to search.</param>
    /// <param name="face">The face name to find.</param>
    /// <returns>The face row, or <see langword="null"/> when the placement declares no <c>faceSources</c> row by
    /// that name.</returns>
    public static WorldPlacementFace? FindPlacementFace(WorldPlacement placement, string face) => Find(
        rows: placement.FaceSources,
        name: face,
        selector: static row => row.Face
    );
    /// <summary>Finds a references row by stable name — the primitive a <see cref="WorldDestination.Reference"/>
    /// resolves against.</summary>
    /// <param name="references">The section's references, or <see langword="null"/> for a document declaring none.</param>
    /// <param name="name">The reference name to find.</param>
    /// <returns>The reference, or <see langword="null"/> when the section declares none by that name.</returns>
    public static WorldReference? FindReference(IReadOnlyList<WorldReference>? references, string name) => Find(
        rows: references,
        name: name,
        selector: static reference => reference.Name.Value
    );
    /// <summary>Finds a spawn point by stable id.</summary>
    /// <param name="spawnPoints">The section's spawn points.</param>
    /// <param name="id">The spawn point id to find.</param>
    /// <returns>The spawn point, or <see langword="null"/> when the section declares none by that id.</returns>
    public static WorldSpawnPoint? FindSpawnPoint(IReadOnlyList<WorldSpawnPoint>? spawnPoints, string id) => Find(
        rows: spawnPoints,
        name: id,
        selector: static point => point.Id
    );
    /// <summary>Finds a state row by stable name — the ONE row-find every reader of the <c>state</c> section shares
    /// (the rule compiler's operand walk, the mutation compose arms, the console read-backs, the HUD binding
    /// resolver, and an owned identity document's own durable slots).</summary>
    /// <param name="rows">The section's rows.</param>
    /// <param name="name">The row name to find.</param>
    /// <returns>The row, or <see langword="null"/> when the section declares none by that name.</returns>
    /// <remarks>Allocation-free and ordinal, like its siblings: the HUD path runs this per frame. The whole-document
    /// validator deliberately does NOT route here — it builds a name-keyed map once per walk and asks it O(1), which
    /// a linear scan per lookup would turn quadratic.</remarks>
    public static WorldStateRow? FindStateRow(IReadOnlyList<WorldStateRow>? rows, string name) => Find(
        rows: rows,
        name: name,
        selector: static row => row.Name
    );
}
