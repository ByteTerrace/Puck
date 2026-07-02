namespace Puck.Maths;

/// <summary>
/// A hierarchical world position: an integer cell index plus a signed <see cref="FixedVector3"/> offset from the cell's
/// centre. The cell index supplies unbounded RANGE while the local offset keeps all hot-path math in native 64-bit
/// <see cref="FixedQ4816"/> — together they span astronomical down to microscopic scale, which a single flat 64-bit
/// fixed-point coordinate cannot (it must trade range for resolution). One cell spans <c>2^<see cref="CellSizeLog2"/></c>
/// world units; the local offset is kept in <c>[-CellSize/2, CellSize/2)</c> by <see cref="Normalize"/>, so a position
/// near a cell's centre (e.g. everything at cell <c>0</c>) carries the same component values a flat fixed-point vector
/// would. Differences and the render rebase are exact integer arithmetic and bit-identical across machines.
/// </summary>
/// <param name="CellX">The cell index along X.</param>
/// <param name="CellY">The cell index along Y.</param>
/// <param name="CellZ">The cell index along Z.</param>
/// <param name="Local">The signed offset from the cell's centre, in world units.</param>
public readonly record struct WorldCoord3(long CellX, long CellY, long CellZ, FixedVector3 Local) {
    /// <summary>The base-2 logarithm of a cell's edge length in world units (a cell spans <c>2^20 = 1,048,576</c> units).</summary>
    public const int CellSizeLog2 = 20;

    // A cell edge and half-edge in RAW Q48.16 units: CellSizeLog2 world units scaled by the fraction bit count.
    private const int CellRawLog2 = (CellSizeLog2 + FixedQ4816.FractionBitCount);
    private const long HalfCellRaw = (1L << (CellRawLog2 - 1));

    /// <summary>Gets the origin (cell <c>0</c>, zero offset).</summary>
    public static WorldCoord3 Zero => default;

    /// <summary>Translates a position by a local delta, re-anchoring across cell boundaries so the offset stays centred.</summary>
    /// <param name="coord">The position to translate.</param>
    /// <param name="delta">The local-space displacement to add.</param>
    /// <returns>The normalized translated position.</returns>
    public static WorldCoord3 operator +(WorldCoord3 coord, FixedVector3 delta) =>
        new WorldCoord3(CellX: coord.CellX, CellY: coord.CellY, CellZ: coord.CellZ, Local: (coord.Local + delta)).Normalize();
    /// <summary>Returns the exact local-space vector FROM <paramref name="origin"/> TO <paramref name="coord"/> — the same as <see cref="Delta"/>.</summary>
    /// <param name="coord">The target position.</param>
    /// <param name="origin">The position to measure from.</param>
    /// <returns>The displacement, valid (within the fixed-point range) whenever the two cells are near.</returns>
    public static FixedVector3 operator -(WorldCoord3 coord, WorldCoord3 origin) =>
        coord.Delta(origin: origin);

    /// <summary>Constructs a position from a local offset at cell <c>0</c>, re-anchoring if the offset exceeds half a cell.</summary>
    /// <param name="local">The local-space position relative to the origin cell's centre.</param>
    /// <returns>The normalized position.</returns>
    public static WorldCoord3 FromLocal(FixedVector3 local) =>
        new WorldCoord3(CellX: 0L, CellY: 0L, CellZ: 0L, Local: local).Normalize();

    /// <summary>Re-anchors the position so each local component lies in <c>[-CellSize/2, CellSize/2)</c>, carrying whole
    /// cells into the cell index. Pure integer arithmetic (an arithmetic shift floors toward negative infinity), so it
    /// is deterministic and O(1) regardless of how far the offset has drifted.</summary>
    /// <returns>The equivalent position with a centred local offset.</returns>
    public WorldCoord3 Normalize() {
        var (cellX, localX) = NormalizeComponent(cell: CellX, localRaw: Local.X.Value);
        var (cellY, localY) = NormalizeComponent(cell: CellY, localRaw: Local.Y.Value);
        var (cellZ, localZ) = NormalizeComponent(cell: CellZ, localRaw: Local.Z.Value);

        return new WorldCoord3(
            CellX: cellX,
            CellY: cellY,
            CellZ: cellZ,
            Local: new FixedVector3(
                X: FixedQ4816.FromRawBits(value: localX),
                Y: FixedQ4816.FromRawBits(value: localY),
                Z: FixedQ4816.FromRawBits(value: localZ)
            )
        );
    }

    /// <summary>Returns the exact local-space displacement from <paramref name="origin"/> to this position.</summary>
    /// <param name="origin">The position to measure from (typically the per-frame render anchor or a collision frame).</param>
    /// <returns>The displacement <c>(this - origin)</c> as a fixed-point vector — exact when the cells are near, which holds for the camera/collision rebase.</returns>
    public FixedVector3 Delta(WorldCoord3 origin) {
        // World = Cell·CellSize + Local; the absolute product would overflow far from the origin, so difference the cells
        // FIRST (small when near) and fold in the local difference — all in raw Q48.16.
        return new FixedVector3(
            X: FixedQ4816.FromRawBits(value: unchecked((((CellX - origin.CellX) << CellRawLog2) + (Local.X.Value - origin.Local.X.Value)))),
            Y: FixedQ4816.FromRawBits(value: unchecked((((CellY - origin.CellY) << CellRawLog2) + (Local.Y.Value - origin.Local.Y.Value)))),
            Z: FixedQ4816.FromRawBits(value: unchecked((((CellZ - origin.CellZ) << CellRawLog2) + (Local.Z.Value - origin.Local.Z.Value))))
        );
    }
    /// <summary>Converts this position to a single-precision vector RELATIVE to <paramref name="origin"/>, for the renderer.</summary>
    /// <param name="origin">The per-frame render anchor the GPU coordinate space is centred on.</param>
    /// <returns>The camera-relative position; small (precise) whenever the camera is near, no matter how far both are from the world origin.</returns>
    public System.Numerics.Vector3 ToRenderRelative(WorldCoord3 origin) =>
        Delta(origin: origin).ToVector3();

    private static (long Cell, long LocalRaw) NormalizeComponent(long cell, long localRaw) {
        // Carry whole cells so localRaw lands in [-HalfCellRaw, HalfCellRaw). The arithmetic right shift floors the
        // signed quotient toward negative infinity, and the cell edge is a power of two, so this is exact and branchless.
        var carry = ((localRaw + HalfCellRaw) >> CellRawLog2);

        return ((cell + carry), unchecked((localRaw - (carry << CellRawLog2))));
    }
}
