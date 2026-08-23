using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>The client's mirror of the authority's field lattice — raw Q48.16 cells patched from snapshot deltas.
/// Presentation reads it; nothing here feeds the tick.</summary>
public sealed class WorldClientFieldLattice {
    private readonly long[][] m_raw;

    public WorldClientFieldLattice(WorldFieldsSection document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        Document = document;
        Width = document.Lattice.Width;
        Depth = document.Lattice.Depth;
        Layers = document.Lattice.Layers;
        CellCount = ((Width * Layers) * Depth);
        m_raw = new long[document.Fields.Count][];

        for (var field = 0; (field < m_raw.Length); field++) {
            m_raw[field] = new long[CellCount];
        }
    }

    /// <summary>Gets the cell count.</summary>
    public int CellCount { get; }
    /// <summary>Gets the lattice depth in cells.</summary>
    public int Depth { get; }
    /// <summary>Gets the authored section this mirror was shaped from.</summary>
    public WorldFieldsSection Document { get; }
    /// <summary>Gets the field count.</summary>
    public int FieldCount => m_raw.Length;
    /// <summary>Gets the layer count.</summary>
    public int Layers { get; }
    /// <summary>Gets a counter that moves on every applied delta set.</summary>
    public int Revision { get; private set; }
    /// <summary>Gets the lattice width in cells.</summary>
    public int Width { get; }

    /// <summary>Applies one snapshot's cell deltas.</summary>
    /// <param name="deltas">The deltas.</param>
    /// <param name="full">Whether the deltas cover every cell.</param>
    public void Apply(ReadOnlySpan<FieldCellDelta> deltas, bool full) {
        if (
            (deltas.Length == 0) &&
            !full
        ) {
            return;
        }

        foreach (var delta in deltas) {
            if (
                (delta.Field >= m_raw.Length) ||
                (((uint)delta.Cell) >= ((uint)CellCount))
            ) {
                continue;
            }

            m_raw[delta.Field][delta.Cell] = delta.Raw;
        }

        Revision++;
    }
    /// <summary>Gets the cell index of a column's layer.</summary>
    /// <param name="x">The X cell index.</param>
    /// <param name="y">The layer.</param>
    /// <param name="z">The Z cell index.</param>
    /// <returns>The cell index.</returns>
    public int CellIndex(int x, int y, int z) => (((z * Layers) + y) * Width + x);
    /// <summary>Gets a cell's value as a presentation float.</summary>
    /// <param name="field">The field index.</param>
    /// <param name="cell">The cell index.</param>
    /// <returns>The value.</returns>
    public float Value(int field, int cell) => (m_raw[field][cell] / 65536f);
}
