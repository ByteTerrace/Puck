using Puck.Maths;
using Puck.SignedDistance.Queries;

namespace Puck.SignedDistance.Baking;

/// <summary>
/// The sample lattice a mesh is extracted from: a cube of <see cref="Cells"/> cells a side around a prototype's reach,
/// with <see cref="SdfBakeTier.PaddingCells"/> empty cells between the reach and each face. Corner values are read
/// lazily: a block of cells whose center proves, through the program's Lipschitz bound, that no surface lies within
/// the block's half-diagonal takes that center's sign at every corner without evaluating any of them, and a corner is
/// evaluated only when a sign-changing edge needs its value. Every coordinate is fixed point, so the lattice and every
/// value read on it are the same on every machine.
/// </summary>
internal sealed class SdfBakeGrid {
    // The cells along each side of a culling block. A larger block proves less often; a smaller one pays more centers.
    private const int BlockCells = 4;

    private readonly SdfBakeField m_field;
    private readonly long m_originX;
    private readonly long m_originY;
    private readonly long m_originZ;
    private readonly long m_cell;
    private readonly long[] m_values;
    private readonly byte[] m_state;

    private SdfBakeGrid(SdfBakeField field, int cells, long originX, long originY, long originZ, long cell) {
        m_field = field;
        Cells = cells;
        m_originX = originX;
        m_originY = originY;
        m_originZ = originZ;
        m_cell = cell;

        var corners = (cells + 1);

        m_values = new long[((corners * corners) * corners)];
        m_state = new byte[m_values.Length];
    }

    /// <summary>Gets the cells along each side.</summary>
    public int Cells { get; }
    /// <summary>Gets the cell size, in world units.</summary>
    public double CellSize => ((double)FixedQ4816.FromRawBits(value: m_cell));
    /// <summary>Gets the cell size in fixed point.</summary>
    public FixedQ4816 CellSizeFixed => FixedQ4816.FromRawBits(value: m_cell);

    /// <summary>Returns the lattice around a sphere of <paramref name="reach"/> about <paramref name="center"/>, with
    /// every corner's sign resolved.</summary>
    /// <param name="field">The field.</param>
    /// <param name="center">The sphere's world-space center.</param>
    /// <param name="reach">The sphere's radius, in world units; positive.</param>
    /// <param name="cells">The cells along each side, padding included; more than twice the padding.</param>
    /// <returns>The lattice.</returns>
    public static SdfBakeGrid Create(SdfBakeField field, FixedVector3 center, FixedQ4816 reach, int cells) {
        var inner = (cells - (2 * SdfBakeTier.PaddingCells));
        var cell = Math.Max(
            val1: 1L,
            val2: (((2L * reach.Value) + (inner - 1)) / inner)
        );
        var half = ((cells * cell) / 2L);
        var grid = new SdfBakeGrid(
            cell: cell,
            cells: cells,
            field: field,
            originX: (center.X.Value - half),
            originY: (center.Y.Value - half),
            originZ: (center.Z.Value - half)
        );

        grid.ResolveSigns();

        return grid;
    }
    /// <summary>Returns the flat index of corner <c>(x, y, z)</c>.</summary>
    public int Corner(int x, int y, int z) {
        var corners = (Cells + 1);

        return (x + (corners * (y + (corners * z))));
    }
    /// <summary>Returns whether a corner lies inside the surface, where the field is negative.</summary>
    public bool Inside(int corner) =>
        ((m_state[corner] & StateInside) != 0);
    /// <summary>Returns a corner's field value, evaluating it on first use.</summary>
    public long Value(int corner, int x, int y, int z) {
        if ((m_state[corner] & StateEvaluated) == 0) {
            Evaluate(corner: corner, x: x, y: y, z: z);
        }

        return m_values[corner];
    }
    /// <summary>Returns the world-space position of a point in lattice coordinates.</summary>
    public (double X, double Y, double Z) World(double x, double y, double z) {
        var cell = CellSize;

        return (
            (((double)FixedQ4816.FromRawBits(value: m_originX)) + (x * cell)),
            (((double)FixedQ4816.FromRawBits(value: m_originY)) + (y * cell)),
            (((double)FixedQ4816.FromRawBits(value: m_originZ)) + (z * cell))
        );
    }

    private const byte StateEvaluated = 1;
    private const byte StateInside = 2;
    private const byte StateKnown = 4;

    private FixedVector3 CornerPoint(long x, long y, long z) =>
        new(
            X: FixedQ4816.FromRawBits(value: (m_originX + (x * m_cell))),
            Y: FixedQ4816.FromRawBits(value: (m_originY + (y * m_cell))),
            Z: FixedQ4816.FromRawBits(value: (m_originZ + (z * m_cell)))
        );
    private void Evaluate(int corner, int x, int y, int z) {
        _ = m_field.TryDistance(
            distance: out var distance,
            material: out _,
            point: CornerPoint(x: x, y: y, z: z)
        );
        m_values[corner] = distance.Value;
        m_state[corner] = ((byte)(StateEvaluated | StateKnown | ((distance.Value < 0L) ? StateInside : 0)));
    }
    // A block whose center's scaled field value exceeds its half-diagonal holds no surface point, so every corner in
    // it (its faces included) shares the center's sign. The half-diagonal is rounded up, so the proof stays a proof.
    private void ResolveSigns() {
        var blocks = ((Cells + (BlockCells - 1)) / BlockCells);
        var halfDiagonal = (((long)Math.Ceiling(a: ((Math.Sqrt(d: 3.0) * (BlockCells * 0.5)) * m_cell))) + 1L);

        for (var bz = 0; (bz < blocks); bz++) {
            for (var by = 0; (by < blocks); by++) {
                for (var bx = 0; (bx < blocks); bx++) {
                    var x0 = (bx * BlockCells);
                    var y0 = (by * BlockCells);
                    var z0 = (bz * BlockCells);
                    var x1 = Math.Min(val1: Cells, val2: (x0 + BlockCells));
                    var y1 = Math.Min(val1: Cells, val2: (y0 + BlockCells));
                    var z1 = Math.Min(val1: Cells, val2: (z0 + BlockCells));
                    var center = new FixedVector3(
                        X: FixedQ4816.FromRawBits(value: (m_originX + (((x0 + x1) * m_cell) / 2L))),
                        Y: FixedQ4816.FromRawBits(value: (m_originY + (((y0 + y1) * m_cell) / 2L))),
                        Z: FixedQ4816.FromRawBits(value: (m_originZ + (((z0 + z1) * m_cell) / 2L)))
                    );

                    _ = m_field.TryDistance(
                        distance: out var distance,
                        material: out _,
                        point: center
                    );

                    var clearance = SdfFieldMarch.ScaleDistanceDown(
                        distance: FixedQ4816.Abs(value: distance),
                        scale: m_field.StepScale
                    );
                    var proven = (clearance.Value > halfDiagonal);
                    var inside = (distance.Value < 0L);

                    for (var z = z0; (z <= z1); z++) {
                        for (var y = y0; (y <= y1); y++) {
                            for (var x = x0; (x <= x1); x++) {
                                var corner = Corner(x: x, y: y, z: z);

                                if ((m_state[corner] & StateKnown) != 0) {
                                    continue;
                                }

                                if (proven) {
                                    m_state[corner] = ((byte)(StateKnown | (inside ? StateInside : 0)));
                                } else {
                                    Evaluate(corner: corner, x: x, y: y, z: z);
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
