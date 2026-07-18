using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// A hierarchical world position: an integer cell index plus a signed <see cref="FixedVector3"/> offset from the cell's
/// centre. The signed 64-bit cell index supplies vast range while the local offset keeps hot-path math in native 64-bit
/// <see cref="FixedQ4816"/> — together they span astronomical down to microscopic scale, which a single flat 64-bit
/// fixed-point coordinate cannot (it must trade range for resolution). One cell spans <c>2^<see cref="CellSizeLog2"/></c>
/// world units; construction keeps the local offset in <c>[-CellSize/2, CellSize/2)</c>, so a position
/// near a cell's centre (e.g. everything at cell <c>0</c>) carries the same component values a flat fixed-point vector
/// would. Representable differences and render rebases are exact integer arithmetic and bit-identical across machines.
/// </summary>
public readonly record struct WorldCoord3
    : IAdditionOperators<WorldCoord3, FixedVector3, WorldCoord3>,
      ISubtractionOperators<WorldCoord3, WorldCoord3, FixedVector3> {
    /// <summary>The base-2 logarithm of a cell's edge length in world units (a cell spans <c>2^20 = 1,048,576</c> units).</summary>
    public const int CellSizeLog2 = 20;

    // A cell edge and half-edge in RAW Q48.16 units: CellSizeLog2 world units scaled by the fraction bit count.
    private const int CellRawLog2 = (CellSizeLog2 + FixedQ4816.FractionBitCount);
    private const long CellRaw = (1L << CellRawLog2);
    private const long HalfCellRaw = (1L << (CellRawLog2 - 1));

    /// <summary>Gets the cell index along X.</summary>
    public long CellX { get; }
    /// <summary>Gets the cell index along Y.</summary>
    public long CellY { get; }
    /// <summary>Gets the cell index along Z.</summary>
    public long CellZ { get; }
    /// <summary>Gets the centred offset from the cell, in world units.</summary>
    public FixedVector3 Local { get; }

    /// <summary>Constructs a canonical world position, carrying any out-of-cell local offset into the cell indices.</summary>
    /// <param name="CellX">The initial cell index along X.</param>
    /// <param name="CellY">The initial cell index along Y.</param>
    /// <param name="CellZ">The initial cell index along Z.</param>
    /// <param name="Local">The offset from the initial cell's centre.</param>
    /// <exception cref="OverflowException">Canonicalization would move a cell index outside the signed 64-bit range.</exception>
    public WorldCoord3(long CellX, long CellY, long CellZ, FixedVector3 Local) {
        if (!TryCreate(cellX: CellX, cellY: CellY, cellZ: CellZ, local: Local, result: out var result)) {
            throw new OverflowException(message: "The canonical world position exceeds the signed 64-bit cell range.");
        }

        this = result;
    }

    private WorldCoord3(long cellX, long cellY, long cellZ, FixedVector3 local, CanonicalTag _) {
        CellX = cellX;
        CellY = cellY;
        CellZ = cellZ;
        Local = local;
    }

    /// <summary>Gets the origin (cell <c>0</c>, zero offset).</summary>
    public static WorldCoord3 Zero => default;

    /// <summary>Translates a position by a local delta, re-anchoring across cell boundaries so the offset stays centred.</summary>
    /// <param name="coord">The position to translate.</param>
    /// <param name="delta">The local-space displacement to add.</param>
    /// <returns>The canonical translated position.</returns>
    /// <exception cref="OverflowException">The translated position exceeds the signed 64-bit cell range.</exception>
    public static WorldCoord3 operator +(WorldCoord3 coord, FixedVector3 delta) {
        if (!coord.TryTranslate(delta: delta, result: out var result)) {
            throw new OverflowException(message: "The translated world position exceeds the signed 64-bit cell range.");
        }

        return result;
    }
    /// <summary>Returns the exact local-space vector FROM <paramref name="origin"/> TO <paramref name="coord"/> — the same as <see cref="Delta"/>.</summary>
    /// <param name="coord">The target position.</param>
    /// <param name="origin">The position to measure from.</param>
    /// <returns>The displacement when it fits in <see cref="FixedVector3"/>.</returns>
    /// <exception cref="OverflowException">The displacement is outside the signed Q48.16 range.</exception>
    public static FixedVector3 operator -(WorldCoord3 coord, WorldCoord3 origin) =>
        coord.Delta(origin: origin);

    /// <summary>Constructs a position from a local offset at cell <c>0</c>, re-anchoring if the offset exceeds half a cell.</summary>
    /// <param name="local">The local-space position relative to the origin cell's centre.</param>
    /// <returns>The normalized position.</returns>
    public static WorldCoord3 FromLocal(FixedVector3 local) =>
        new(
        CellX: 0L,
        CellY: 0L,
        CellZ: 0L,
        Local: local
    );

    /// <summary>Attempts to construct a canonical position without overflowing a cell index.</summary>
    /// <param name="cellX">The initial cell index along X.</param>
    /// <param name="cellY">The initial cell index along Y.</param>
    /// <param name="cellZ">The initial cell index along Z.</param>
    /// <param name="local">The offset from the initial cell's centre.</param>
    /// <param name="result">The canonical position on success; otherwise <see cref="Zero"/>.</param>
    /// <returns><see langword="true"/> when the position has a canonical representation.</returns>
    public static bool TryCreate(long cellX, long cellY, long cellZ, FixedVector3 local, out WorldCoord3 result) =>
        TryCreateFromRaw(
            cellX: cellX,
            cellY: cellY,
            cellZ: cellZ,
            localX: local.X.Value,
            localY: local.Y.Value,
            localZ: local.Z.Value,
            result: out result
        );

    /// <summary>Returns this position with a replacement local offset, carrying it into the cell indices.</summary>
    /// <param name="local">The new offset relative to the current cell's centre.</param>
    /// <returns>The canonical position.</returns>
    /// <exception cref="OverflowException">Canonicalization would move a cell index outside the signed 64-bit range.</exception>
    public WorldCoord3 WithLocal(FixedVector3 local) =>
        new(CellX: CellX, CellY: CellY, CellZ: CellZ, Local: local);

    /// <summary>Attempts to translate this position without overflowing a cell index.</summary>
    /// <param name="delta">The local-space displacement.</param>
    /// <param name="result">The canonical translated position on success; otherwise <see cref="Zero"/>.</param>
    /// <returns><see langword="true"/> when the translated position has a canonical representation.</returns>
    public bool TryTranslate(FixedVector3 delta, out WorldCoord3 result) {
        if (
            TryAdd(left: Local.X.Value, right: delta.X.Value, result: out var localX) &&
            TryAdd(left: Local.Y.Value, right: delta.Y.Value, result: out var localY) &&
            TryAdd(left: Local.Z.Value, right: delta.Z.Value, result: out var localZ)
        ) {
            return TryCreateFromRaw(
                cellX: CellX,
                cellY: CellY,
                cellZ: CellZ,
                localX: localX,
                localY: localY,
                localZ: localZ,
                result: out result
            );
        }

        return TryCreateFromRaw(
            cellX: CellX,
            cellY: CellY,
            cellZ: CellZ,
            localX: ((Int128)Local.X.Value + delta.X.Value),
            localY: ((Int128)Local.Y.Value + delta.Y.Value),
            localZ: ((Int128)Local.Z.Value + delta.Z.Value),
            result: out result
        );
    }

    /// <summary>Re-anchors the position so each local component lies in <c>[-CellSize/2, CellSize/2)</c>, carrying whole
    /// cells into the cell index. Public construction already establishes this invariant, so normalization is
    /// idempotent and O(1).</summary>
    /// <returns>This canonical position.</returns>
    public WorldCoord3 Normalize() => this;

    /// <summary>Returns the exact local-space displacement from <paramref name="origin"/> to this position.</summary>
    /// <param name="origin">The position to measure from (typically the per-frame render anchor or a collision frame).</param>
    /// <returns>The exact displacement <c>(this - origin)</c> as a fixed-point vector.</returns>
    /// <exception cref="OverflowException">The displacement is outside the signed Q48.16 range.</exception>
    public FixedVector3 Delta(WorldCoord3 origin) {
        if (!TryDelta(origin: origin, delta: out var delta)) {
            throw new OverflowException(message: "The world-position displacement is outside the signed Q48.16 range.");
        }

        return delta;
    }

    /// <summary>Attempts to compute the exact local-space displacement from <paramref name="origin"/>.</summary>
    /// <param name="origin">The position to measure from.</param>
    /// <param name="delta">The exact displacement on success; otherwise <see cref="FixedVector3.Zero"/>.</param>
    /// <returns><see langword="true"/> when the displacement fits in signed Q48.16.</returns>
    public bool TryDelta(WorldCoord3 origin, out FixedVector3 delta) {
        if (
            !TryDeltaComponent(cell: CellX, localRaw: Local.X.Value, originCell: origin.CellX, originLocalRaw: origin.Local.X.Value, deltaRaw: out var x) ||
            !TryDeltaComponent(cell: CellY, localRaw: Local.Y.Value, originCell: origin.CellY, originLocalRaw: origin.Local.Y.Value, deltaRaw: out var y) ||
            !TryDeltaComponent(cell: CellZ, localRaw: Local.Z.Value, originCell: origin.CellZ, originLocalRaw: origin.Local.Z.Value, deltaRaw: out var z)
        ) {
            delta = FixedVector3.Zero;

            return false;
        }

        delta = new(
            X: FixedQ4816.FromRawBits(value: x),
            Y: FixedQ4816.FromRawBits(value: y),
            Z: FixedQ4816.FromRawBits(value: z)
        );

        return true;
    }
    /// <summary>Converts this position to a single-precision vector RELATIVE to <paramref name="origin"/>, for the renderer.</summary>
    /// <param name="origin">The per-frame render anchor the GPU coordinate space is centred on.</param>
    /// <returns>The camera-relative position; small (precise) whenever the camera is near, no matter how far both are from the world origin.</returns>
    /// <exception cref="OverflowException">The displacement is outside the signed Q48.16 range.</exception>
    public Vector3 ToRenderRelative(WorldCoord3 origin) =>
        Delta(origin: origin).ToVector3();

    /// <summary>Deconstructs the position into its canonical cell indices and local offset.</summary>
    /// <param name="cellX">The canonical cell index along X.</param>
    /// <param name="cellY">The canonical cell index along Y.</param>
    /// <param name="cellZ">The canonical cell index along Z.</param>
    /// <param name="local">The centred local offset.</param>
    public void Deconstruct(out long cellX, out long cellY, out long cellZ, out FixedVector3 local) {
        cellX = CellX;
        cellY = CellY;
        cellZ = CellZ;
        local = Local;
    }

    private static bool TryCreateFromRaw(long cellX, long cellY, long cellZ, long localX, long localY, long localZ, out WorldCoord3 result) {
        if (
            !TryNormalizeComponent(cell: cellX, localRaw: localX, normalizedCell: out var normalizedCellX, normalizedLocalRaw: out var normalizedLocalX) ||
            !TryNormalizeComponent(cell: cellY, localRaw: localY, normalizedCell: out var normalizedCellY, normalizedLocalRaw: out var normalizedLocalY) ||
            !TryNormalizeComponent(cell: cellZ, localRaw: localZ, normalizedCell: out var normalizedCellZ, normalizedLocalRaw: out var normalizedLocalZ)
        ) {
            result = Zero;

            return false;
        }

        result = CreateCanonical(
            cellX: normalizedCellX,
            cellY: normalizedCellY,
            cellZ: normalizedCellZ,
            localX: normalizedLocalX,
            localY: normalizedLocalY,
            localZ: normalizedLocalZ
        );

        return true;
    }

    private static bool TryCreateFromRaw(long cellX, long cellY, long cellZ, Int128 localX, Int128 localY, Int128 localZ, out WorldCoord3 result) {
        if (
            !TryNormalizeComponent(cell: cellX, localRaw: localX, normalizedCell: out var normalizedCellX, normalizedLocalRaw: out var normalizedLocalX) ||
            !TryNormalizeComponent(cell: cellY, localRaw: localY, normalizedCell: out var normalizedCellY, normalizedLocalRaw: out var normalizedLocalY) ||
            !TryNormalizeComponent(cell: cellZ, localRaw: localZ, normalizedCell: out var normalizedCellZ, normalizedLocalRaw: out var normalizedLocalZ)
        ) {
            result = Zero;

            return false;
        }

        result = CreateCanonical(
            cellX: normalizedCellX,
            cellY: normalizedCellY,
            cellZ: normalizedCellZ,
            localX: normalizedLocalX,
            localY: normalizedLocalY,
            localZ: normalizedLocalZ
        );

        return true;
    }

    private static WorldCoord3 CreateCanonical(long cellX, long cellY, long cellZ, long localX, long localY, long localZ) =>
        new(
            cellX: cellX,
            cellY: cellY,
            cellZ: cellZ,
            local: new(
                X: FixedQ4816.FromRawBits(value: localX),
                Y: FixedQ4816.FromRawBits(value: localY),
                Z: FixedQ4816.FromRawBits(value: localZ)
            ),
            _: CanonicalTag.Value
        );

    private static bool TryNormalizeComponent(long cell, long localRaw, out long normalizedCell, out long normalizedLocalRaw) {
        var quotient = (localRaw >> CellRawLog2);
        var remainder = localRaw & (CellRaw - 1L);
        var carry = (quotient + ((remainder + HalfCellRaw) >> CellRawLog2));

        if (
            ((carry > 0L) && (cell > (long.MaxValue - carry))) ||
            ((carry < 0L) && (cell < (long.MinValue - carry)))
        ) {
            normalizedCell = default;
            normalizedLocalRaw = default;

            return false;
        }

        normalizedCell = (cell + carry);
        normalizedLocalRaw = unchecked((localRaw - (carry << CellRawLog2)));

        return true;
    }

    private static bool TryNormalizeComponent(long cell, Int128 localRaw, out long normalizedCell, out long normalizedLocalRaw) {
        // Arithmetic right shift is floor division by the power-of-two cell size. The non-negative remainder then
        // selects the centred representative without an overflowing half-cell bias.
        var quotient = (localRaw >> CellRawLog2);
        var remainder = localRaw & (CellRaw - 1L);
        var carry = (quotient + ((remainder + HalfCellRaw) >> CellRawLog2));
        var wideCell = ((Int128)cell + carry);

        if ((wideCell < long.MinValue) || (wideCell > long.MaxValue)) {
            normalizedCell = default;
            normalizedLocalRaw = default;

            return false;
        }

        normalizedCell = (long)wideCell;
        normalizedLocalRaw = (long)(localRaw - (carry << CellRawLog2));

        return true;
    }

    private static bool TryDeltaComponent(long cell, long localRaw, long originCell, long originLocalRaw, out long deltaRaw) {
        var cellDelta = unchecked((cell - originCell));

        if (
            (((cell ^ originCell) & (cell ^ cellDelta)) >= 0L) &&
            (cellDelta >= -(1L << 26)) &&
            (cellDelta <= (1L << 26))
        ) {
            // Canonical locals differ by less than one cell, so this conservative cell range cannot overflow long.
            deltaRaw = unchecked(((cellDelta << CellRawLog2) + (localRaw - originLocalRaw)));

            return true;
        }

        var wideDelta = ((((Int128)cell - originCell) << CellRawLog2) + ((Int128)localRaw - originLocalRaw));

        if ((wideDelta < long.MinValue) || (wideDelta > long.MaxValue)) {
            deltaRaw = default;

            return false;
        }

        deltaRaw = (long)wideDelta;

        return true;
    }

    private static bool TryAdd(long left, long right, out long result) {
        result = unchecked((left + right));

        return (((left ^ result) & (right ^ result)) >= 0L);
    }

    private enum CanonicalTag { Value }
}
