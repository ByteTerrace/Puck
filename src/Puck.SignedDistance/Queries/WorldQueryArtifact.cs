using Puck.Maths;

namespace Puck.SignedDistance.Queries;

/// <summary>
/// Stores a resolution-quantized 2.5D heightfield and an optional blocked-cell bitmap. Coordinates and heights are
/// raw Q48.16 values, and occupancy is bit-packed, so the artifact round-trips deterministically regardless of the
/// floating-point geometry used to bake it. The artifact is an in-memory query input; persistence is the caller's
/// responsibility, and a schema token belongs to whatever serializer a caller adds.
/// <para>
/// Construction is total: a layer whose length contradicts <see cref="Width"/> and <see cref="Height"/>, a
/// non-positive cell size, a negative dimension, and a blocked layer carrying a bit past the last cell are each
/// refused by parameter name, so no query can index past a layer and no bit outside the grid can be observed. Both
/// layers are copied in, which is what lets the capability flags be computed once and stay true.
/// </para>
/// <para>
/// The capability flags describe content, not allocation: <see cref="HasHeightfield"/> is false for an all-sentinel
/// height layer and <see cref="HasBlocked"/> is false for an all-zero bitmap, however those arrays were sized. A
/// layer reported present answers at least one query, which is what <see cref="QueryCapabilities"/> promises a
/// caller that checks once at bind time.
/// </para>
/// </summary>
public sealed class WorldQueryArtifact {
    /// <summary>The sentinel <see cref="HeightRaw"/> value marking "no authored ground at this cell" — the most
    /// negative representable Q48.16 tick, unreachable by any authored terrain height.</summary>
    public const long NoHeightSentinel = long.MinValue;

    private readonly ulong[] m_blocked;
    private readonly int m_cellCount;
    private readonly long[] m_heightRaw;

    /// <summary>Gets the blocked-cell bitmap, 1 bit/cell, row-major, packed little-endian into <see cref="ulong"/>
    /// words (<c>word = cellIndex &gt;&gt; 6; bit = cellIndex &amp; 63</c>) — identical packing to the walk grid's
    /// <c>Cells</c>. The final word's bits at or past <see cref="CellCount"/> are padding and are required to be
    /// zero. Empty when the layer is omitted; <see cref="IsBlockedCell"/> is the guarded reader.</summary>
    public ReadOnlySpan<ulong> Blocked => m_blocked;
    /// <summary>Gets the grid's cell count, <c><see cref="Width"/> * <see cref="Height"/></c> — the exclusive upper
    /// bound on every row-major cell index.</summary>
    public int CellCount => m_cellCount;
    /// <summary>Gets the cell edge length, raw Q48.16 (uniform on X/Z). Always positive.</summary>
    public long CellSizeRaw { get; }
    /// <summary>Gets a value indicating whether the blocked-cell layer carries at least one blocked cell.</summary>
    public bool HasBlocked { get; }
    /// <summary>Gets a value indicating whether the height layer carries at least one non-sentinel height.</summary>
    public bool HasHeightfield { get; }
    /// <summary>Gets the grid height in cells (Z axis).</summary>
    public int Height { get; }
    /// <summary>Gets the per-cell ground height, raw Q48.16, row-major (<c>index = (row * Width) + column</c>). A
    /// cell with no authored ground carries <see cref="NoHeightSentinel"/>. Empty when the layer is omitted;
    /// <see cref="TryHeightRaw"/> is the guarded reader.</summary>
    public ReadOnlySpan<long> HeightRaw => m_heightRaw;
    /// <summary>Gets the grid's minimum-X corner, raw Q48.16.</summary>
    public long OriginXRaw { get; }
    /// <summary>Gets the grid's minimum-Z corner, raw Q48.16.</summary>
    public long OriginZRaw { get; }
    /// <summary>Gets the grid width in cells (X axis).</summary>
    public int Width { get; }

    /// <summary>Wraps two baked layers over a grid, validating that they describe it.</summary>
    /// <param name="originXRaw">The grid's minimum-X corner, raw Q48.16.</param>
    /// <param name="originZRaw">The grid's minimum-Z corner, raw Q48.16.</param>
    /// <param name="cellSizeRaw">The cell edge length, raw Q48.16 (uniform on X/Z); must be positive.</param>
    /// <param name="width">The grid width in cells (X axis); must not be negative.</param>
    /// <param name="height">The grid height in cells (Z axis); must not be negative.</param>
    /// <param name="heightRaw">The height layer — either empty (layer omitted) or exactly
    /// <c><paramref name="width"/> * <paramref name="height"/></c> entries.</param>
    /// <param name="blocked">The blocked bitmap — either empty (layer omitted) or exactly
    /// <c>ceil(width * height / 64)</c> words.</param>
    /// <exception cref="ArgumentNullException"><paramref name="heightRaw"/> or <paramref name="blocked"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is
    /// negative, or <paramref name="cellSizeRaw"/> is not positive.</exception>
    /// <exception cref="ArgumentException">A layer's length contradicts the grid dimensions, the blocked layer sets a
    /// padding bit at or past <see cref="CellCount"/>, or the cell count overflows <see cref="int"/>.</exception>
    public WorldQueryArtifact(long originXRaw, long originZRaw, long cellSizeRaw, int width, int height, long[] heightRaw, ulong[] blocked) {
        ArgumentNullException.ThrowIfNull(argument: blocked);
        ArgumentNullException.ThrowIfNull(argument: heightRaw);
        ArgumentOutOfRangeException.ThrowIfNegative(
            paramName: nameof(width),
            value: width
        );
        ArgumentOutOfRangeException.ThrowIfNegative(
            paramName: nameof(height),
            value: height
        );
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            paramName: nameof(cellSizeRaw),
            value: cellSizeRaw
        );

        var cellCountLong = (((long)width) * height);

        if (cellCountLong > int.MaxValue) {
            throw new ArgumentException(
                message: $"A {width}x{height} grid holds {cellCountLong} cells, which overflows a 32-bit cell index.",
                paramName: nameof(width)
            );
        }

        var cellCount = ((int)cellCountLong);

        if (
            (heightRaw.Length != 0) &&
            (heightRaw.Length != cellCount)
        ) {
            throw new ArgumentException(
                message: $"The height layer holds {heightRaw.Length} entries, but a {width}x{height} grid needs {cellCount} (or 0 to omit the layer).",
                paramName: nameof(heightRaw)
            );
        }

        var blockedWordCount = BlockedWordCount(cellCount: cellCount);

        if (
            (blocked.Length != 0) &&
            (blocked.Length != blockedWordCount)
        ) {
            throw new ArgumentException(
                message: $"The blocked layer holds {blocked.Length} words, but a {width}x{height} grid needs {blockedWordCount} (or 0 to omit the layer).",
                paramName: nameof(blocked)
            );
        }

        var paddingBits = (cellCount & 63);

        // The bits above the last cell in the final word address no cell. Left unconstrained they are observable
        // through IsBlockedCell and HasBlocked, so an artifact could report a blocker outside its own grid.
        if (
            (blocked.Length != 0) &&
            (paddingBits != 0) &&
            ((blocked[^1] & ~((1UL << paddingBits) - 1UL)) != 0UL)
        ) {
            throw new ArgumentException(
                message: $"The blocked layer's final word sets a bit at or past cell {cellCount}, which lies outside a {width}x{height} grid.",
                paramName: nameof(blocked)
            );
        }

        m_blocked = blocked.ToArray();
        m_cellCount = cellCount;
        m_heightRaw = heightRaw.ToArray();

        CellSizeRaw = cellSizeRaw;
        HasBlocked = AnyBlockedBit(blocked: m_blocked);
        HasHeightfield = AnyAuthoredHeight(heightRaw: m_heightRaw);
        Height = height;
        OriginXRaw = originXRaw;
        OriginZRaw = originZRaw;
        Width = width;
    }

    private static bool AnyAuthoredHeight(long[] heightRaw) {
        for (var index = 0; (index < heightRaw.Length); index++) {
            if (heightRaw[index] != NoHeightSentinel) {
                return true;
            }
        }

        return false;
    }
    private static bool AnyBlockedBit(ulong[] blocked) {
        for (var index = 0; (index < blocked.Length); index++) {
            if (blocked[index] != 0UL) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns how many <see cref="ulong"/> words a blocked bitmap covering <paramref name="cellCount"/>
    /// cells occupies — the length this type requires of a present blocked layer.</summary>
    /// <param name="cellCount">The grid's cell count; must not be negative.</param>
    /// <returns>The word count, 0 for an empty grid.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cellCount"/> is negative.</exception>
    public static int BlockedWordCount(int cellCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(
            paramName: nameof(cellCount),
            value: cellCount
        );

        // Shift-then-carry rather than (cellCount + 63) / 64: the rounding addend overflows int for the last 63 cell
        // counts and turns the largest grids into negative word counts.
        return ((cellCount >> 6) + (((cellCount & 63) == 0) ? 0 : 1));
    }
    /// <summary>Returns a value indicating whether the cell at <paramref name="cellIndex"/> is blocked. An index
    /// outside the grid reads as not blocked — the "out of bounds reads as clear" contract this artifact inherits
    /// from the walk grid — and the bitmap's trailing padding bits are not addressable.</summary>
    /// <param name="cellIndex">The row-major cell index (<c>(row * Width) + column</c>).</param>
    /// <returns><see langword="true"/> when the cell carries a blocker.</returns>
    public bool IsBlockedCell(int cellIndex) {
        var word = (cellIndex >> 6);

        return (
            (cellIndex >= 0) &&
            (cellIndex < m_cellCount) &&
            (word < m_blocked.Length) &&
            ((m_blocked[word] & (1UL << (cellIndex & 63))) != 0UL)
        );
    }
    /// <summary>Reads the authored ground height at <paramref name="cellIndex"/>. An index outside the layer, an
    /// absent layer, and a cell carrying <see cref="NoHeightSentinel"/> all answer <see langword="false"/> — the same
    /// bounds discipline <see cref="IsBlockedCell"/> applies, so neither layer can be indexed unguarded.</summary>
    /// <param name="cellIndex">The row-major cell index (<c>(row * Width) + column</c>).</param>
    /// <param name="heightRaw">The authored ground height, raw Q48.16, when the method returns
    /// <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the cell carries an authored ground height.</returns>
    public bool TryHeightRaw(int cellIndex, out long heightRaw) {
        if (
            (cellIndex < 0) ||
            (cellIndex >= m_heightRaw.Length)
        ) {
            heightRaw = NoHeightSentinel;

            return false;
        }

        heightRaw = m_heightRaw[cellIndex];

        return (heightRaw != NoHeightSentinel);
    }
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
/// should carry, mirroring the walk grid's obstacle marking. A blocker is an infinite vertical column: nothing in
/// this input carries a height, so every query treats a blocked cell as blocking at every Y.</summary>
/// <param name="MinX">The rectangle's minimum X (world units).</param>
/// <param name="MinZ">The rectangle's minimum Z.</param>
/// <param name="MaxX">The rectangle's maximum X.</param>
/// <param name="MaxZ">The rectangle's maximum Z.</param>
public readonly record struct WorldQueryBlockerInput(float MinX, float MinZ, float MaxX, float MaxZ);
