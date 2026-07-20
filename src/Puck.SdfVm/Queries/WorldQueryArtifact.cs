namespace Puck.SdfVm.Queries;

/// <summary>
/// Stores a resolution-quantized 2.5D heightfield and an optional blocked-cell bitmap. Coordinates and heights are
/// raw Q48.16 values, and occupancy is bit-packed, so the artifact round-trips deterministically regardless of the
/// floating-point geometry used to bake it. The artifact is an in-memory query input; persistence is the caller's
/// responsibility, and a schema token belongs to whatever serializer a caller adds.
/// </summary>
/// <param name="OriginXRaw">The grid's minimum-X corner, raw Q48.16.</param>
/// <param name="OriginZRaw">The grid's minimum-Z corner, raw Q48.16.</param>
/// <param name="CellSizeRaw">The cell edge length, raw Q48.16 (uniform on X/Z).</param>
/// <param name="Width">The grid width in cells (X axis).</param>
/// <param name="Height">The grid height in cells (Z axis).</param>
/// <param name="HeightRaw">Per-cell ground height, raw Q48.16, row-major (<c>index = (row * Width) + column</c>).
/// A cell with no authored ground carries <see cref="NoHeightSentinel"/>.</param>
/// <param name="Blocked">The blocked-cell bitmap, 1 bit/cell, row-major, packed little-endian into
/// <see cref="ulong"/> words (<c>word = cellIndex &gt;&gt; 6; bit = cellIndex &amp; 63</c>) — identical packing to
/// the walk grid's <c>Cells</c>. May be empty (<see cref="QueryCapabilities.HasBlocked"/> then false).</param>
public sealed record WorldQueryArtifact(
    long OriginXRaw,
    long OriginZRaw,
    long CellSizeRaw,
    int Width,
    int Height,
    long[] HeightRaw,
    ulong[] Blocked
) {
    /// <summary>The sentinel <see cref="HeightRaw"/> value marking "no authored ground at this cell" — the most
    /// negative representable Q48.16 tick, unreachable by any authored terrain height.</summary>
    public const long NoHeightSentinel = long.MinValue;

    /// <summary>Whether the height layer carries any real (non-sentinel) data.</summary>
    public bool HasHeightfield => (HeightRaw.Length > 0);

    /// <summary>Whether the blocked-cell layer is present.</summary>
    public bool HasBlocked => (Blocked.Length > 0);
}

/// <summary>Describes a flat terrain rectangle for <see cref="WorldQueryBaker"/> over the inclusive XZ span
/// <c>[MinX, MaxX] × [MinZ, MaxZ]</c>.</summary>
/// <param name="MinX">The rectangle's minimum X (world units).</param>
/// <param name="MinZ">The rectangle's minimum Z.</param>
/// <param name="MaxX">The rectangle's maximum X.</param>
/// <param name="MaxZ">The rectangle's maximum Z.</param>
/// <param name="TopY">The flat ground height across the whole rectangle.</param>
public readonly record struct WorldQueryTerrainInput(float MinX, float MinZ, float MaxX, float MaxZ, float TopY);

/// <summary>One authored blocker rectangle (an XZ footprint, no height) — marks cells the blocked-bitmap layer
/// should carry, mirroring the walk grid's obstacle marking.</summary>
/// <param name="MinX">The rectangle's minimum X (world units).</param>
/// <param name="MinZ">The rectangle's minimum Z.</param>
/// <param name="MaxX">The rectangle's maximum X.</param>
/// <param name="MaxZ">The rectangle's maximum Z.</param>
public readonly record struct WorldQueryBlockerInput(float MinX, float MinZ, float MaxX, float MaxZ);
