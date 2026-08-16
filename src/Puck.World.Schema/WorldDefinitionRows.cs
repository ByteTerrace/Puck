namespace Puck.World;

/// <summary>Resolves stable row names in world-definition collections.</summary>
public static class WorldDefinitionRows {
    /// <summary>Finds an adjacency row by stable name.</summary>
    public static WorldAdjacency? FindAdjacency(IReadOnlyList<WorldAdjacency>? adjacencies, string name) {
        foreach (var adjacency in (adjacencies ?? [])) {
            if (
                (adjacency is not null) &&
                string.Equals(
                a: adjacency.Name.Value,
                b: name,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return adjacency;
            }
        }

        return null;
    }
    /// <summary>Finds a creation by stable id.</summary>
    /// <param name="creations">The section's creations.</param>
    /// <param name="id">The creation id to find.</param>
    /// <returns>The creation, or <see langword="null"/> when the section declares none by that id.</returns>
    public static WorldCreation? FindCreation(IReadOnlyList<WorldCreation> creations, string id) {
        foreach (var creation in creations) {
            if (
                (creation is not null) &&
                string.Equals(
                a: creation.Id,
                b: id,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return creation;
            }
        }

        return null;
    }
    /// <summary>Finds a destinations row by stable name — the primitive a portal facet's <c>destination</c> resolves
    /// against (see <see cref="WorldPlacementPortal.Destination"/>).</summary>
    /// <param name="destinations">The section's destinations, or <see langword="null"/> for a document declaring none.</param>
    /// <param name="name">The destination name to find.</param>
    /// <returns>The destination, or <see langword="null"/> when the section declares none by that name.</returns>
    public static WorldDestination? FindDestination(IReadOnlyList<WorldDestination>? destinations, string name) {
        foreach (var destination in (destinations ?? [])) {
            if (
                (destination is not null) &&
                string.Equals(
                a: destination.Name.Value,
                b: name,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return destination;
            }
        }

        return null;
    }
    /// <summary>Finds a kit by stable name.</summary>
    /// <param name="kits">The section's kits.</param>
    /// <param name="name">The kit name to find.</param>
    /// <returns>The kit, or <see langword="null"/> when the section declares none by that name.</returns>
    public static WorldKit? FindKit(IReadOnlyList<WorldKit> kits, string name) {
        foreach (var kit in kits) {
            if (
                (kit is not null) &&
                string.Equals(
                a: kit.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return kit;
            }
        }

        return null;
    }
    /// <summary>Finds a placement by stable id.</summary>
    /// <param name="placements">The section's placements.</param>
    /// <param name="id">The placement id to find.</param>
    /// <returns>The placement, or <see langword="null"/> when the section declares none by that id.</returns>
    public static WorldPlacement? FindPlacement(IReadOnlyList<WorldPlacement> placements, string id) {
        foreach (var placement in placements) {
            if (
                (placement is not null) &&
                string.Equals(
                a: placement.Id,
                b: id,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return placement;
            }
        }

        return null;
    }
    /// <summary>Finds a placement's declared face by name — the primitive a
    /// <see cref="WorldPlacementPortal.Counterpart"/> resolves its face half against (see
    /// <see cref="WorldPortalCounterpart"/>), and every other placement/face reader (<c>world.portals</c>,
    /// <c>world.faces</c>) already walks by hand.</summary>
    /// <param name="placement">The placement to search.</param>
    /// <param name="face">The face name to find.</param>
    /// <returns>The face row, or <see langword="null"/> when the placement declares no <c>faceSources</c> row by
    /// that name.</returns>
    public static WorldPlacementFace? FindPlacementFace(WorldPlacement placement, string face) {
        foreach (var row in (placement.FaceSources ?? [])) {
            if (
                (row is not null) &&
                string.Equals(
                a: row.Face,
                b: face,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return row;
            }
        }

        return null;
    }
    /// <summary>Finds a references row by stable name — the primitive a <see cref="WorldDestination.Reference"/>
    /// resolves against.</summary>
    /// <param name="references">The section's references, or <see langword="null"/> for a document declaring none.</param>
    /// <param name="name">The reference name to find.</param>
    /// <returns>The reference, or <see langword="null"/> when the section declares none by that name.</returns>
    public static WorldReference? FindReference(IReadOnlyList<WorldReference>? references, string name) {
        foreach (var reference in (references ?? [])) {
            if (
                (reference is not null) &&
                string.Equals(
                a: reference.Name.Value,
                b: name,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return reference;
            }
        }

        return null;
    }
    /// <summary>Finds a spawn point by stable id.</summary>
    /// <param name="spawnPoints">The section's spawn points.</param>
    /// <param name="id">The spawn point id to find.</param>
    /// <returns>The spawn point, or <see langword="null"/> when the section declares none by that id.</returns>
    public static WorldSpawnPoint? FindSpawnPoint(IReadOnlyList<WorldSpawnPoint> spawnPoints, string id) {
        foreach (var point in spawnPoints) {
            if (string.Equals(
                a: point.Id,
                b: id,
                comparisonType: StringComparison.Ordinal
            )) {
                return point;
            }
        }

        return null;
    }
    /// <summary>Finds a state row by stable name — the ONE row-find every reader of the <c>state</c> section shares
    /// (the rule compiler's operand walk, the mutation compose arms, the console read-backs, the HUD binding
    /// resolver, and an owned identity document's own durable slots).</summary>
    /// <param name="rows">The section's rows.</param>
    /// <param name="name">The row name to find.</param>
    /// <returns>The row, or <see langword="null"/> when the section declares none by that name.</returns>
    /// <remarks>Allocation-free and ordinal, like its siblings: the HUD path runs this per frame. The whole-document
    /// validator deliberately does NOT route here — it builds a name-keyed map once per walk and asks it O(1), which
    /// a linear scan per lookup would turn quadratic.</remarks>
    public static WorldStateRow? FindStateRow(IReadOnlyList<WorldStateRow> rows, string name) {
        foreach (var row in rows) {
            if (string.Equals(
                a: row.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                return row;
            }
        }

        return null;
    }
}
