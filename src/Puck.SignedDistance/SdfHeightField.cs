using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>
/// A lattice of columns whose heights move without a new mesh: each column a box from one cell below the lattice's origin
/// to its height, drawn by displacing one baked reference. The type owns two sampled fields. <see cref="Reference"/> is the
/// columns' distance lattice with every raised column's top at the reference height, <see cref="ReferenceCells"/> cells
/// above the origin, which the mesh bake samples and keys; it is published again only when a column is raised or lowered
/// to nothing, so a moving height keys no new mesh. <see cref="Heights"/> is a one-voxel-tall lattice of the columns'
/// current heights above the origin, which the bake never reads: the renderer maps each vertex of the reference mesh
/// vertically by it as the vertex is drawn (<c>sdfMeshDisplaced</c>, <c>frame/sdf-mesh.hlsli</c>), the top band (the
/// reference top's last cell, which holds the top face and its edge rounding) translated by the bilinear height over the
/// column centres less the reference, the band below scaled from the column base.
/// <para>Invariant: a height field's drawn geometry is a function of its mesh key (the reference: origin, cell, lattice
/// dimensions and which columns are raised) and its heights snapshot. This type is the only producer of a displacement
/// (<see cref="SdfProgramBuilder.HeightField"/> takes one) and the only writer of a reference, so the reference height the
/// renderer assumes is the one every reference was baked at. Presentation only: no simulation query reads either
/// field.</para>
/// </summary>
public sealed class SdfHeightField {
    /// <summary>The reference top's height above the origin, in cells: every raised column of <see cref="Reference"/>
    /// stands this many cells tall above the origin, and the renderer's map assumes it (generated into the kernels as
    /// <c>SDF_HEIGHT_FIELD_REFERENCE_CELLS</c>).</summary>
    public const int ReferenceCells = 1;
    /// <summary>The voxel rows of <see cref="Reference"/> along +Y: the row below the origin, the row the reference top
    /// lies over and the row above it, centred half a cell into each cell from one cell below the origin.</summary>
    public const int ReferenceRows = (ReferenceCells + 2);

    // The cells about a voxel whose columns it measures exactly; farther it holds the lower bound Reach cells.
    private const int Reach = 2;

    private readonly float[] m_heights;
    private readonly bool[] m_raised;
    private readonly float[] m_voxels;

    /// <summary>Initializes a new instance of the <see cref="SdfHeightField"/> class with its first heights.</summary>
    /// <param name="origin">The lattice's minimum corner: column (x, z) spans x to x + 1 and z to z + 1 cells from it, and
    /// stands from one cell below it.</param>
    /// <param name="cell">The column edge, in world units; finite and positive.</param>
    /// <param name="width">Columns along +X, in [1, <see cref="SdfProgramBuilder.MaxSampledRegionDim"/> - 2].</param>
    /// <param name="depth">Columns along +Z, in [1, <see cref="SdfProgramBuilder.MaxSampledRegionDim"/> - 2].</param>
    /// <param name="top">The greatest height a column takes above the origin, in world units: a height past it draws at
    /// it, and every drawn box holds it. Finite and non-negative.</param>
    /// <param name="heights">The first heights above the origin, <paramref name="width"/> fastest; a height at or below
    /// zero raises no column.</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <exception cref="ArgumentException"><paramref name="heights"/> holds another count, or a value that is not
    /// finite.</exception>
    public SdfHeightField(Vector3 origin, float cell, int width, int depth, float top, ReadOnlySpan<float> heights) {
        if (!float.IsFinite(f: origin.X) || !float.IsFinite(f: origin.Y) || !float.IsFinite(f: origin.Z)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(origin), message: "A height field's origin must be finite.");
        }
        if (!float.IsFinite(f: cell) || (cell <= 0f)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(cell), message: "A height field's cell must be finite and positive.");
        }
        if (!float.IsFinite(f: top) || (top < 0f)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(top), message: "A height field's top must be finite and non-negative.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(value: width, other: 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: depth, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: width, other: (SdfProgramBuilder.MaxSampledRegionDim - 2));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: depth, other: (SdfProgramBuilder.MaxSampledRegionDim - 2));

        Origin = origin;
        Cell = cell;
        Width = width;
        Depth = depth;
        Top = top;
        m_heights = new float[checked(width * depth)];
        m_raised = new bool[m_heights.Length];
        m_voxels = new float[checked(((width + 2) * ReferenceRows) * (depth + 2))];
        Take(heights: heights);
        for (var column = 0; (column < m_heights.Length); column++) { m_raised[column] = (m_heights[column] > 0f); }
        BakeReference();
        Heights = new SdfSampledField(dimX: width, dimY: 1, dimZ: depth, values: m_heights);
        Reference = new SdfSampledField(dimX: (width + 2), dimY: ReferenceRows, dimZ: (depth + 2), values: m_voxels);
    }

    /// <summary>Gets the lattice's minimum corner.</summary>
    public Vector3 Origin { get; }
    /// <summary>Gets the column edge, in world units.</summary>
    public float Cell { get; }
    /// <summary>Gets the columns along +X.</summary>
    public int Width { get; }
    /// <summary>Gets the columns along +Z.</summary>
    public int Depth { get; }
    /// <summary>Gets the greatest height a column takes above the origin.</summary>
    public float Top { get; }
    /// <summary>Gets the reference's distance lattice, which the mesh bake samples: every raised column's top at
    /// <see cref="ReferenceCells"/> cells above the origin.</summary>
    public SdfSampledField Reference { get; }
    /// <summary>Gets the columns' current heights above the origin, one voxel a column, <see cref="Width"/> fastest, which
    /// the renderer displaces the reference by.</summary>
    public SdfSampledField Heights { get; }
    /// <summary>Gets the minimum corner of <see cref="Reference"/>'s box: one cell below and outside the lattice's.</summary>
    public Vector3 ReferenceMin => (Origin - new Vector3(value: Cell));
    /// <summary>Gets the extent of <see cref="Reference"/>'s box.</summary>
    public Vector3 ReferenceExtent => (new Vector3(x: (Width + 2), y: ReferenceRows, z: (Depth + 2)) * Cell);
    /// <summary>Gets the lowest height, in world units, any drawn point of the field takes: the columns' base.</summary>
    public float DrawnLow => (Origin.Y - Cell);
    /// <summary>Gets the highest height, in world units, a displaced point of the field takes: <see cref="Top"/> above the
    /// origin, which no height passes. The reference mesh's own top bounds what lies higher.</summary>
    public float DrawnHigh => (Origin.Y + Top);

    /// <summary>Publishes new heights: a new <see cref="Heights"/> snapshot when any height moved, and a new
    /// <see cref="Reference"/> snapshot only when a column was raised from nothing or lowered to nothing.</summary>
    /// <param name="heights">The heights above the origin, <see cref="Width"/> fastest.</param>
    /// <returns>Whether the reference moved, which keys a new mesh.</returns>
    /// <exception cref="ArgumentException"><paramref name="heights"/> holds another count, or a value that is not
    /// finite.</exception>
    public bool Publish(ReadOnlySpan<float> heights) {
        Take(heights: heights);
        if (!m_heights.AsSpan().SequenceEqual(other: Heights.Current.Values)) { _ = Heights.Publish(values: m_heights); }

        var raised = false;

        for (var column = 0; (column < m_heights.Length); column++) {
            var now = (m_heights[column] > 0f);

            raised |= (now != m_raised[column]);
            m_raised[column] = now;
        }
        if (!raised) { return false; }
        BakeReference();
        if (m_voxels.AsSpan().SequenceEqual(other: Reference.Current.Values)) { return false; }
        _ = Reference.Publish(values: m_voxels);
        return true;
    }

    // Copies heights in, each clamped to [0, Top], so every drawn box holds every height.
    private void Take(ReadOnlySpan<float> heights) {
        if (heights.Length != m_heights.Length) {
            throw new ArgumentException(message: $"A {Width}x{Depth} height field takes {m_heights.Length} heights, not {heights.Length}.", paramName: nameof(heights));
        }
        for (var column = 0; (column < heights.Length); column++) {
            var height = heights[column];

            if (!float.IsFinite(f: height)) { throw new ArgumentException(message: "A height field's heights must be finite.", paramName: nameof(heights)); }
            m_heights[column] = Math.Clamp(value: height, min: 0f, max: Top);
        }
    }

    // Each voxel holds the distance to the union of the nearby raised columns (boxes from one cell below the origin to the
    // reference top), exact within Reach cells and a conservative lower bound beyond, scaled by 1/√3 as the sampled field
    // stores it. Voxel (0, 0, 0) is centred half a cell into the reference box on every axis. The reference top lies on a
    // whole cell, where the rows either side of it read the top's distance unclamped, so the interpolated top face lies
    // exactly at it.
    private void BakeReference() {
        var cell = Cell;
        var dimX = (Width + 2);
        var dimZ = (Depth + 2);
        var top = (ReferenceCells * cell);
        var far = (Reach * cell);

        for (var vz = 0; (vz < dimZ); vz++) {
            var pz = (((vz + 0.5f) * cell) - cell);

            for (var vy = 0; (vy < ReferenceRows); vy++) {
                var py = (((vy + 0.5f) * cell) - cell);

                for (var vx = 0; (vx < dimX); vx++) {
                    var px = (((vx + 0.5f) * cell) - cell);
                    var best = far;

                    for (var z = Math.Max(val1: 0, val2: ((vz - 1) - Reach)); (z <= Math.Min(val1: (Depth - 1), val2: ((vz - 1) + Reach))); z++) {
                        for (var x = Math.Max(val1: 0, val2: ((vx - 1) - Reach)); (x <= Math.Min(val1: (Width - 1), val2: ((vx - 1) + Reach))); x++) {
                            if (!m_raised[((z * Width) + x)]) { continue; }

                            var dx = MathF.Max(x: ((x * cell) - px), y: (px - ((x + 1) * cell)));
                            var dy = MathF.Max(x: (-cell - py), y: (py - top));
                            var dz = MathF.Max(x: ((z * cell) - pz), y: (pz - ((z + 1) * cell)));
                            var ox = MathF.Max(x: dx, y: 0f);
                            var oy = MathF.Max(x: dy, y: 0f);
                            var oz = MathF.Max(x: dz, y: 0f);
                            var distance = (MathF.Sqrt(x: (((ox * ox) + (oy * oy)) + (oz * oz))) + MathF.Min(x: MathF.Max(x: dx, y: MathF.Max(x: dy, y: dz)), y: 0f));

                            if (distance < best) { best = distance; }
                        }
                    }

                    m_voxels[((((vz * ReferenceRows) + vy) * dimX) + vx)] = (best * SdfSampledField.DistanceScale);
                }
            }
        }
    }
}
