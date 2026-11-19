using System.Numerics;

namespace Puck.SignedDistance.Baking;

/// <summary>What the bake's refinement did with one coarse cell of a final lattice (<see cref="SdfRefinementMap.Flag"/>).</summary>
public enum SdfCellFlag {
    /// <summary>The point lies outside the lattice.</summary>
    Outside,
    /// <summary>The bake never ran the refinement scan on this lattice, so no cell of it was ever examined for a feature
    /// thinner than the cell (<see cref="SdfRefinementMap.Asked"/> is zero).</summary>
    NotScanned,
    /// <summary>The refinement scan ran on this lattice and flagged no cell, or the split it chose would have measured more
    /// than the refinement's budget, so every cell contours at the lattice's own spacing.</summary>
    LatticeUnrefined,
    /// <summary>The lattice was refined elsewhere and this cell was not flagged, nor did it touch a flagged cell: it
    /// contours at the lattice's own spacing.</summary>
    Unflagged,
    /// <summary>The cell touches a flagged cell and contours at the fine spacing from interpolated coarse values, with no
    /// fine samples of its own.</summary>
    Ring,
    /// <summary>The cell was flagged and its fine samples measured: the refinement took it for a feature its coarse samples
    /// could not resolve (<see cref="SdfRefinementMap.Thin"/> says whether a sheet or gap thinner than the cell was the reason).</summary>
    Measured,
}

/// <summary>
/// Which coarse cells of a final lattice <see cref="SdfSampledLattice.Refine"/> flagged for finer sampling, which cells
/// contour fine from them, and which it left at the lattice's own spacing, copied at the moment the lattice's contour
/// starts so a diagnostic can ask, for a point a defect lies at, whether the bake ever noticed the cell. It is a read-only
/// view: it carries no field values and never feeds the bake.
/// </summary>
public sealed class SdfRefinementMap {
    private readonly bool[] m_measured;
    private readonly bool[] m_refined;
    private readonly bool[] m_thin;
    private readonly Vector3 m_origin;
    private readonly Vector3 m_cell;
    private readonly int m_cellsX;
    private readonly int m_cellsY;
    private readonly int m_cellsZ;

    internal SdfRefinementMap(Vector3 origin, Vector3 cell, int cellsX, int cellsY, int cellsZ, int asked, int factor, bool[] measured, bool[] refined, bool[] thin) {
        Asked = asked;
        Factor = factor;
        m_cell = cell;
        m_cellsX = cellsX;
        m_cellsY = cellsY;
        m_cellsZ = cellsZ;
        m_measured = measured;
        m_origin = origin;
        m_refined = refined;
        m_thin = thin;
    }

    /// <summary>Gets the lattice's coarse cell, in world units along each axis.</summary>
    public Vector3 Cell => m_cell;
    /// <summary>Gets the fine cells along each axis of a coarse cell the refinement scan was asked for; zero when the bake
    /// never ran it on this lattice (its aim was no finer than the lattice), so a lattice with a factor of one and an
    /// asked factor above zero was scanned and nothing in it needed refining.</summary>
    public int Asked { get; }
    /// <summary>Gets the fine cells along each axis of a coarse cell: one when the lattice was never refined.</summary>
    public int Factor { get; }
    /// <summary>Gets the coarse cells the refinement measured at the fine spacing.</summary>
    public int Measured => Count(flags: m_measured);
    /// <summary>Gets the coarse cells that contour at the fine spacing, the measured cells and every cell touching one.</summary>
    public int Refined => Count(flags: m_refined);
    /// <summary>Gets the coarse cells a sheet or gap thinner than a cell crosses an edge of.</summary>
    public int Thin => Count(flags: m_thin);

    /// <summary>Reads what the refinement did with the coarse cell that holds a point.</summary>
    /// <param name="point">The point, in the lattice's frame.</param>
    /// <returns>The cell's flag; <see cref="SdfCellFlag.Outside"/> past the lattice's last cell.</returns>
    public SdfCellFlag Flag(Vector3 point) {
        var at = ((point - m_origin) / m_cell);
        var x = ((int)MathF.Floor(x: at.X));
        var y = ((int)MathF.Floor(x: at.Y));
        var z = ((int)MathF.Floor(x: at.Z));

        // A point on the lattice's far face belongs to the last cell, which the dual contour's last vertex can lie on.
        x = ((x == m_cellsX) && (at.X <= m_cellsX) ? (m_cellsX - 1) : x);
        y = ((y == m_cellsY) && (at.Y <= m_cellsY) ? (m_cellsY - 1) : y);
        z = ((z == m_cellsZ) && (at.Z <= m_cellsZ) ? (m_cellsZ - 1) : z);
        if ((x < 0) || (y < 0) || (z < 0) || (x >= m_cellsX) || (y >= m_cellsY) || (z >= m_cellsZ)) { return SdfCellFlag.Outside; }
        if (Factor <= 1) { return ((Asked == 0) ? SdfCellFlag.NotScanned : SdfCellFlag.LatticeUnrefined); }

        var cell = (x + (m_cellsX * (y + (m_cellsY * z))));

        return (m_measured[cell] ? SdfCellFlag.Measured : (m_refined[cell] ? SdfCellFlag.Ring : SdfCellFlag.Unflagged));
    }
    /// <summary>Reads whether a sheet or gap thinner than a cell crosses an edge of the coarse cell that holds a point.</summary>
    /// <param name="point">The point, in the lattice's frame.</param>
    /// <returns><see langword="true"/> when the contour closes sheets in the cell.</returns>
    public bool IsThin(Vector3 point) {
        var at = ((point - m_origin) / m_cell);
        var x = ((int)MathF.Floor(x: at.X));
        var y = ((int)MathF.Floor(x: at.Y));
        var z = ((int)MathF.Floor(x: at.Z));

        return ((x >= 0) && (y >= 0) && (z >= 0) && (x < m_cellsX) && (y < m_cellsY) && (z < m_cellsZ) && (Factor > 1) && m_thin[(x + (m_cellsX * (y + (m_cellsY * z))))]);
    }

    private static int Count(bool[] flags) {
        var count = 0;

        foreach (var flag in flags) { count += (flag ? 1 : 0); }

        return count;
    }
}
