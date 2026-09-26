namespace Puck.World;

/// <summary>What a whole state row bound to a pass's array presents: a keyed document row read as numbers, cell <c>i</c>
/// at element <c>i</c>, over as many elements as the row's shape holds. A lattice row holds one element per cell of its
/// topology, a ring row its capacity, and any other keyed row its cell ceiling; an element whose cell is absent reads
/// zero. The state mirror reads a bound row through this shape, and the load gate holds it to the array it fills.</summary>
public static class WorldBoundRow {
    /// <summary>Resolves a bound row: a keyed document-lane row, and the element count it presents.</summary>
    /// <param name="definition">The document.</param>
    /// <param name="rowName">The row's name.</param>
    /// <param name="row">The row, when this returns <see langword="true"/>.</param>
    /// <param name="length">The element count the row presents, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the document declares a keyed row of that name whose shape states its
    /// length.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="rowName"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryResolve(WorldDefinition definition, string rowName, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out StateRow? row, out int length) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: rowName);

        row = null;
        length = 0;

        if (
            !definition.StateCatalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: rowName
        ) ||
            (handle.Ordinal >= definition.State.Count)
        ) {
            return false;
        }

        var resolved = definition.State[handle.Ordinal];

        if (!resolved.IsKeyed) {
            return false;
        }

        length = (resolved.EffectiveDomain switch {
            StateDomain.CellsOf lattice => (WorldTopologyCompilation.Find(
                definition: definition,
                name: lattice.Topology
            )?.CellCount ?? 0),
            _ => resolved.CellCeiling,
        });
        row = resolved;

        return (length > 0);
    }
}
