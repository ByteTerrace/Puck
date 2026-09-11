using Puck.Maths;

namespace Puck.SignedDistance.Queries;

/// <summary>
/// A lattice of the exact field values an <see cref="SdfFieldEvaluator"/> answers at the corners of world-space cells
/// of one authored size, and the sound lower bound on the field those corners prove between them.
/// </summary>
/// <remarks>
/// <para>Every corner holds the program's own distance and material at that point, evaluated once through the exact
/// evaluator and kept, so the values are a pure function of the program, the origin, and the cell size — identical on
/// every machine whatever order the corners are first read in. Corners are stored in 8×8×8 blocks that come into
/// being on first touch, so a grid may span a world whose queries only ever visit a corner of it.</para>
/// <para>The bound: the field is <see cref="LipschitzBound"/>-Lipschitz, so its value anywhere in a cell is at least
/// the nearest corner's value minus <see cref="Slack"/>, which covers the half-diagonal reach of a cell at that bound
/// plus the fixed-point rounding an evaluation carries. A consumer that only needs to know the field is at least some
/// threshold reads the bound; one that needs the value itself reads the exact evaluator.</para>
/// </remarks>
public sealed class SdfDistanceGrid {
    private const int BlockCornerCount = ((BlockEdge * BlockEdge) * BlockEdge);
    private const int BlockEdge = (1 << BlockShift);
    private const int BlockMask = (BlockEdge - 1);
    private const int BlockShift = 3;
    // Two sentinels below any distance the evaluator can answer: an unbaked corner, and a corner the evaluator could
    // not answer at all (a point outside the program's signed frame).
    private const long UnbakedCorner = long.MinValue;
    private const long UnansweredCorner = (long.MinValue + 1L);
    // The largest corner count a grid may address, keeping block indices and corner indices inside int.
    private const long MaxCornerCount = (1L << 30);

    private static readonly FixedQ4816 SqrtThreeHalf = FixedQ4816.FromDouble(value: 0.8660254037844386);

    private readonly int m_blockCountX;
    private readonly int m_blockCountY;
    private readonly long[]?[] m_distances;
    private readonly SdfFieldEvaluator m_exact;
    private readonly int[]?[] m_materials;

    private long m_bakedCornerCount;

    private SdfDistanceGrid(SdfFieldEvaluator exact, FixedVector3 origin, FixedQ4816 cellSize, int cornerCountX, int cornerCountY, int cornerCountZ) {
        m_exact = exact;
        Origin = origin;
        CellSize = cellSize;
        CornerCountX = cornerCountX;
        CornerCountY = cornerCountY;
        CornerCountZ = cornerCountZ;
        LipschitzBound = exact.LipschitzBound;
        Slack = ((((cellSize * LipschitzBound) * SqrtThreeHalf) + SdfFieldMarch.HitEpsilon) + FixedQ4816.Epsilon);
        m_blockCountX = ((cornerCountX + BlockMask) >> BlockShift);
        m_blockCountY = ((cornerCountY + BlockMask) >> BlockShift);

        var blockCount = ((m_blockCountX * m_blockCountY) * ((cornerCountZ + BlockMask) >> BlockShift));

        m_distances = new long[blockCount][];
        m_materials = new int[blockCount][];
    }

    /// <summary>Gets the number of corners whose exact value has been evaluated and kept.</summary>
    public long BakedCornerCount => m_bakedCornerCount;
    /// <summary>Gets the world-space edge length of one cell.</summary>
    public FixedQ4816 CellSize { get; }
    /// <summary>Gets the corner count along X.</summary>
    public int CornerCountX { get; }
    /// <summary>Gets the corner count along Y.</summary>
    public int CornerCountY { get; }
    /// <summary>Gets the corner count along Z.</summary>
    public int CornerCountZ { get; }
    /// <summary>Gets the total corner count.</summary>
    public long CornerCount => ((((long)CornerCountX) * CornerCountY) * CornerCountZ);
    /// <summary>Gets the program's Lipschitz bound the slack is derived from.</summary>
    public FixedQ4816 LipschitzBound { get; }
    /// <summary>Gets the world-space position of corner (0, 0, 0).</summary>
    public FixedVector3 Origin { get; }
    /// <summary>Gets how far below its nearest corner's value the field may fall anywhere in the grid — the amount a
    /// corner value is lowered by to become a bound.</summary>
    public FixedQ4816 Slack { get; }

    /// <summary>Creates a grid covering every finite instance bound <paramref name="program"/> declares, padded by
    /// <paramref name="padding"/> on every side.</summary>
    /// <param name="exact">The evaluator over <paramref name="program"/> whose values the corners hold.</param>
    /// <param name="program">The program whose static instance bounds size the grid.</param>
    /// <param name="cellSize">The cell edge length in world units.</param>
    /// <param name="padding">The distance the grid extends past the outermost instance bound, in world units.</param>
    /// <returns>The grid, or <see langword="null"/> when there is nothing to cover: the program declares no shape or
    /// no finitely bounded static instance, its step scale floors to zero so no bound can be proven, or the covered
    /// box would exceed the addressable corner count.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exact"/> or <paramref name="program"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cellSize"/> is not positive, or <paramref name="padding"/> is negative.</exception>
    public static SdfDistanceGrid? TryCover(SdfFieldEvaluator exact, SdfProgram program, FixedQ4816 cellSize, FixedQ4816 padding) {
        ArgumentNullException.ThrowIfNull(argument: exact);
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(other: FixedQ4816.Zero, value: cellSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(other: FixedQ4816.Zero, value: padding);

        if (
            !exact.HasShape ||
            (exact.StepScale.Value <= 0L)
        ) {
            return null;
        }

        var covered = false;
        var minX = 0.0;
        var minY = 0.0;
        var minZ = 0.0;
        var maxX = 0.0;
        var maxY = 0.0;
        var maxZ = 0.0;

        foreach (var instance in program.Instances) {
            if (
                instance.IsDynamic ||
                program.HasUnmaskableInfluence(
                    first: instance.First,
                    end: instance.End
                )
            ) {
                continue;
            }

            var radius = ((double)instance.Radius);

            if (!covered) {
                minX = (instance.Center.X - radius);
                minY = (instance.Center.Y - radius);
                minZ = (instance.Center.Z - radius);
                maxX = (instance.Center.X + radius);
                maxY = (instance.Center.Y + radius);
                maxZ = (instance.Center.Z + radius);
                covered = true;

                continue;
            }

            minX = Math.Min(val1: minX, val2: (instance.Center.X - radius));
            minY = Math.Min(val1: minY, val2: (instance.Center.Y - radius));
            minZ = Math.Min(val1: minZ, val2: (instance.Center.Z - radius));
            maxX = Math.Max(val1: maxX, val2: (instance.Center.X + radius));
            maxY = Math.Max(val1: maxY, val2: (instance.Center.Y + radius));
            maxZ = Math.Max(val1: maxZ, val2: (instance.Center.Z + radius));
        }

        if (!covered) {
            return null;
        }

        var pad = ((double)padding);

        return TryCoverBox(
            cellSize: cellSize,
            exact: exact,
            max: new FixedVector3(
                X: FixedQ4816.FromDouble(value: (maxX + pad)),
                Y: FixedQ4816.FromDouble(value: (maxY + pad)),
                Z: FixedQ4816.FromDouble(value: (maxZ + pad))
            ),
            min: new FixedVector3(
                X: FixedQ4816.FromDouble(value: (minX - pad)),
                Y: FixedQ4816.FromDouble(value: (minY - pad)),
                Z: FixedQ4816.FromDouble(value: (minZ - pad))
            )
        );
    }
    /// <summary>Creates a grid whose corners cover the box from <paramref name="min"/> to <paramref name="max"/>: the
    /// origin snaps down to a whole number of cells from the world origin, and the far side rounds up to the next
    /// corner past <paramref name="max"/>.</summary>
    /// <param name="exact">The evaluator whose values the corners hold.</param>
    /// <param name="min">The box's minimum corner.</param>
    /// <param name="max">The box's maximum corner.</param>
    /// <param name="cellSize">The cell edge length in world units.</param>
    /// <returns>The grid, or <see langword="null"/> when the evaluator declares no shape, its step scale floors to
    /// zero, the box is empty on any axis, or the covered box would exceed the addressable corner count.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exact"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cellSize"/> is not positive.</exception>
    public static SdfDistanceGrid? TryCoverBox(SdfFieldEvaluator exact, FixedVector3 min, FixedVector3 max, FixedQ4816 cellSize) {
        ArgumentNullException.ThrowIfNull(argument: exact);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(other: FixedQ4816.Zero, value: cellSize);

        if (
            !exact.HasShape ||
            (exact.StepScale.Value <= 0L) ||
            (min.X > max.X) ||
            (min.Y > max.Y) ||
            (min.Z > max.Z)
        ) {
            return null;
        }

        var edge = cellSize.Value;
        var firstX = min.X.Value.FloorDivide(divisor: edge);
        var firstY = min.Y.Value.FloorDivide(divisor: edge);
        var firstZ = min.Z.Value.FloorDivide(divisor: edge);
        var countX = ((((max.X.Value + edge) - 1L).FloorDivide(divisor: edge) - firstX) + 1L);
        var countY = ((((max.Y.Value + edge) - 1L).FloorDivide(divisor: edge) - firstY) + 1L);
        var countZ = ((((max.Z.Value + edge) - 1L).FloorDivide(divisor: edge) - firstZ) + 1L);

        if (
            (countX <= 1L) ||
            (countY <= 1L) ||
            (countZ <= 1L) ||
            (countX > MaxCornerCount) ||
            (countY > MaxCornerCount) ||
            (countZ > MaxCornerCount) ||
            ((countX * countY) > MaxCornerCount) ||
            (((countX * countY) * countZ) > MaxCornerCount)
        ) {
            return null;
        }

        return new SdfDistanceGrid(
            cellSize: cellSize,
            cornerCountX: ((int)countX),
            cornerCountY: ((int)countY),
            cornerCountZ: ((int)countZ),
            exact: exact,
            origin: new FixedVector3(
                X: FixedQ4816.FromRawBits(value: (firstX * edge)),
                Y: FixedQ4816.FromRawBits(value: (firstY * edge)),
                Z: FixedQ4816.FromRawBits(value: (firstZ * edge))
            )
        );
    }
    /// <summary>Returns the world-space position of a corner.</summary>
    /// <param name="x">The corner's X index.</param>
    /// <param name="y">The corner's Y index.</param>
    /// <param name="z">The corner's Z index.</param>
    /// <returns>The corner position.</returns>
    public FixedVector3 CornerPosition(int x, int y, int z) =>
        new(
            X: FixedQ4816.FromRawBits(value: (Origin.X.Value + (x * CellSize.Value))),
            Y: FixedQ4816.FromRawBits(value: (Origin.Y.Value + (y * CellSize.Value))),
            Z: FixedQ4816.FromRawBits(value: (Origin.Z.Value + (z * CellSize.Value)))
        );
    /// <summary>Reads a corner's exact field value, evaluating it on first read.</summary>
    /// <param name="x">The corner's X index.</param>
    /// <param name="y">The corner's Y index.</param>
    /// <param name="z">The corner's Z index.</param>
    /// <param name="distance">The exact signed distance at the corner, when the method returns <see langword="true"/>.</param>
    /// <param name="material">The nearest surface's material at the corner, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the corner lies in the grid and the exact evaluator answered there.</returns>
    public bool TryCornerDistance(int x, int y, int z, out FixedQ4816 distance, out int material) {
        distance = FixedQ4816.Zero;
        material = 0;

        if (
            (((uint)x) >= ((uint)CornerCountX)) ||
            (((uint)y) >= ((uint)CornerCountY)) ||
            (((uint)z) >= ((uint)CornerCountZ))
        ) {
            return false;
        }

        var raw = Corner(
            material: out material,
            x: x,
            y: y,
            z: z
        );

        if (raw == UnansweredCorner) {
            return false;
        }

        distance = FixedQ4816.FromRawBits(value: raw);

        return true;
    }
    /// <summary>Reads the bound the grid proves at a world-space point: the nearest corner's exact value less
    /// <see cref="Slack"/>, which the field at the point is never below.</summary>
    /// <param name="world">The point, as a displacement from the world origin.</param>
    /// <param name="lowerBound">The bound, when the method returns <see langword="true"/>.</param>
    /// <param name="material">The nearest corner's material, when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the point's nearest corner lies in the grid and the exact evaluator
    /// answered there.</returns>
    public bool TryLowerBound(in FixedVector3 world, out FixedQ4816 lowerBound, out int material) {
        lowerBound = FixedQ4816.Zero;
        material = 0;

        var edge = CellSize.Value;
        var half = (edge >> 1);
        var x = ((world.X.Value - Origin.X.Value) + half).FloorDivide(divisor: edge);
        var y = ((world.Y.Value - Origin.Y.Value) + half).FloorDivide(divisor: edge);
        var z = ((world.Z.Value - Origin.Z.Value) + half).FloorDivide(divisor: edge);

        if (
            (((ulong)x) >= ((ulong)CornerCountX)) ||
            (((ulong)y) >= ((ulong)CornerCountY)) ||
            (((ulong)z) >= ((ulong)CornerCountZ))
        ) {
            return false;
        }

        var raw = Corner(
            material: out material,
            x: ((int)x),
            y: ((int)y),
            z: ((int)z)
        );

        if (raw == UnansweredCorner) {
            return false;
        }

        lowerBound = (FixedQ4816.FromRawBits(value: raw) - Slack);

        return true;
    }

    private long Corner(int x, int y, int z, out int material) {
        var block = (((((z >> BlockShift) * m_blockCountY) + (y >> BlockShift)) * m_blockCountX) + (x >> BlockShift));
        var inner = (((((z & BlockMask) << BlockShift) + (y & BlockMask)) << BlockShift) + (x & BlockMask));
        var distances = m_distances[block];
        var materials = m_materials[block];

        if (distances is null) {
            distances = new long[BlockCornerCount];
            materials = new int[BlockCornerCount];
            Array.Fill(array: distances, value: UnbakedCorner);
            m_distances[block] = distances;
            m_materials[block] = materials;
        }

        var raw = distances[inner];

        if (raw == UnbakedCorner) {
            raw = (m_exact.TryDistance(
                distance: out var distance,
                material: out var baked,
                position: FixedPosition.FromLocal(local: CornerPosition(
                    x: x,
                    y: y,
                    z: z
                ))
            )
                ? distance.Value
                : UnansweredCorner
            );
            distances[inner] = raw;
            materials![inner] = baked;
            m_bakedCornerCount++;
        }

        material = materials![inner];

        return raw;
    }
}
