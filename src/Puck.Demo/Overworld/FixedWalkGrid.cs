using Puck.Demo.World;
using Puck.Maths;

namespace Puck.Demo.Overworld;

/// <summary>
/// The sim-side, PURE FIXED-POINT reader over a save-time-baked <see cref="WalkGridDocument"/>: a rectangular grid of
/// 1-bit-per-cell blocked flags, decoded once at load and queried by <see cref="PlatformerBody.Step"/> every tick. No
/// float anywhere in this file — the origin, cell size, and row stride arrive as raw Q48.16 (see
/// <see cref="WalkGridDocument"/>), and a query is integer cell math only, so the same grid produces the same clamp on
/// every machine. Two tessellations: <c>square</c> (the default; floor-divide point location) and <c>hex</c>
/// (pointy-top hexagonal cells as the Voronoi regions of staggered row centers; exact nearest-center point location
/// on raw long deltas — the √3 in the row spacing was rounded ONCE at bake into the stored stride, never here).
/// <para>
/// A query outside the grid's bounds returns NOT blocked: the grid only ever narrows what the perimeter walls already
/// allow, so out-of-grid space defers to the wall clamps (the outer authority) rather than silently opening or closing
/// off unauthored space.
/// </para>
/// </summary>
public sealed class FixedWalkGrid {
    private readonly ulong[] m_cells;
    private readonly int m_width;
    private readonly int m_height;
    private readonly FixedQ4816 m_originX;
    private readonly FixedQ4816 m_originZ;
    private readonly FixedQ4816 m_cellSize;
    // The hex tessellation state: rows RowStride apart in Z, odd rows offset +CellSize/2 in X (see IsBlockedHex).
    // Square grids keep m_isHex false and m_rowStride == m_cellSize, and never read either on the query path.
    private readonly bool m_isHex;
    private readonly FixedQ4816 m_rowStride;

    private FixedWalkGrid(ulong[] cells, int width, int height, FixedQ4816 originX, FixedQ4816 originZ, FixedQ4816 cellSize, bool isHex, FixedQ4816 rowStride) {
        m_cells = cells;
        m_width = width;
        m_height = height;
        m_originX = originX;
        m_originZ = originZ;
        m_cellSize = cellSize;
        m_isHex = isHex;
        m_rowStride = rowStride;
    }

    /// <summary>The cell count along X.</summary>
    public int Width => m_width;
    /// <summary>The cell count along Z.</summary>
    public int Height => m_height;

    /// <summary>Decodes a baked <see cref="WalkGridDocument"/> into a queryable grid. Pure data reshaping — no
    /// re-derivation, no floats: the raw origin/cell-size/bitmap are trusted as baked.</summary>
    /// <param name="document">The document to decode.</param>
    /// <returns>The decoded grid, or <see langword="null"/> when the document describes an empty (zero-area) grid.</returns>
    public static FixedWalkGrid? FromDocument(WalkGridDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);
        ArgumentOutOfRangeException.ThrowIfNegative(document.Width);
        ArgumentOutOfRangeException.ThrowIfNegative(document.Height);

        if ((document.Width == 0) || (document.Height == 0)) {
            return null;
        }

        // A walk grid is small authored data (the town's is 72x64); a Width*Height that overflows int, or a corrupt
        // base64 cell blob, is malformed/hostile input — bound the size and swallow a decode fault, deferring to the
        // wall clamps (a null grid) rather than allocating an absurd buffer or tearing the tick-boundary reload down.
        var cellCount = ((long)document.Width * document.Height);

        if (cellCount > (1L << 24)) {
            return null;
        }

        var wordCount = (int)((cellCount + 63) / 64);

        byte[] bytes;

        try {
            bytes = (string.IsNullOrEmpty(value: document.Cells) ? [] : Convert.FromBase64String(s: document.Cells));
        }
        catch (FormatException) {
            return null;
        }
        var cells = new ulong[wordCount];
        var copyWords = Math.Min(val1: wordCount, val2: (bytes.Length / 8));

        for (var word = 0; (word < copyWords); word++) {
            cells[word] = BitConverter.ToUInt64(value: bytes, startIndex: (word * 8));
        }

        return new FixedWalkGrid(
            cells: cells,
            cellSize: FixedQ4816.FromRawBits(value: document.CellSizeRaw),
            height: document.Height,
            isHex: string.Equals(a: document.Kind, b: "hex", comparisonType: StringComparison.OrdinalIgnoreCase),
            originX: FixedQ4816.FromRawBits(value: document.OriginXRaw),
            originZ: FixedQ4816.FromRawBits(value: document.OriginZRaw),
            rowStride: FixedQ4816.FromRawBits(value: (document.RowStrideRaw ?? document.CellSizeRaw)),
            width: document.Width
        );
    }

    /// <summary>Whether the cell containing world point <paramref name="x"/>,<paramref name="z"/> is blocked. A point
    /// outside the grid's extent is NOT blocked (defers to the wall clamps — see the type remarks). Integer cell math
    /// only, for BOTH tessellations: square floor-divides into a cell index; hex nearest-center point-locates on raw
    /// long deltas. Never a float division.</summary>
    /// <param name="x">The world-space X coordinate (the room-local frame the grid was baked in).</param>
    /// <param name="z">The world-space Z coordinate.</param>
    /// <returns><see langword="true"/> when the containing cell is blocked; otherwise <see langword="false"/>.</returns>
    public bool IsBlocked(FixedQ4816 x, FixedQ4816 z) =>
        (m_isHex ? IsBlockedHex(x: x, z: z) : IsBlockedSquare(x: x, z: z));

    // The square tessellation's point location: floor-divide each axis into its cell index.
    private bool IsBlockedSquare(FixedQ4816 x, FixedQ4816 z) {
        if (!TryCellIndex(offset: (x - m_originX), cellIndex: out var cellX) || !TryCellIndex(offset: (z - m_originZ), cellIndex: out var cellZ)) {
            return false;
        }

        if ((cellX < 0) || (cellX >= m_width) || (cellZ < 0) || (cellZ >= m_height)) {
            return false;
        }

        var cellIndex = ((cellZ * m_width) + cellX);
        var word = (cellIndex >> 6);
        var bit = (cellIndex & 63);

        return (0UL != (m_cells[word] & (1UL << bit)));
    }

    // The hex tessellation's point location: the cells are the VORONOI REGIONS of staggered row centers — row r's
    // centers sit at Z = origin + r·rowStride and X = origin + (r odd ? cellSize/2 : 0) + c·cellSize — so the
    // containing cell is the NEAREST center, found exactly: candidate rows r0-1..r0+1 around the point's Z strip,
    // candidate columns c0..c0+1 around the point's (parity-shifted) X strip, minimum squared distance on RAW LONG
    // deltas (the deltas are sub-cell-scale, so the squares fit a long with room to spare; no FixedQ4816 multiply —
    // its rounding would blur an exact compare). Ties break to the LOWER row then LOWER column: the loops ascend and
    // the compare is strict, so the first-seen candidate wins. A winner outside the grid = not blocked (defers to
    // the wall clamps, the same rule as the square path).
    private bool IsBlockedHex(FixedQ4816 x, FixedQ4816 z) {
        if ((m_cellSize.Value <= 0L) || (m_rowStride.Value <= 0L)) {
            return false; // a malformed document defers to "not blocked" rather than dividing by zero/negative.
        }

        var pointX = (x.Value - m_originX.Value);
        var pointZ = (z.Value - m_originZ.Value);
        var rowFloor = FloorDiv(dividend: pointZ, divisor: m_rowStride.Value);
        var halfCell = (m_cellSize.Value / 2L);
        var bestDistanceSq = long.MaxValue;
        var bestRow = long.MinValue;
        var bestColumn = 0L;

        for (var row = (rowFloor - 1L); (row <= (rowFloor + 1L)); row++) {
            var parityOffset = (((row & 1L) != 0L) ? halfCell : 0L); // two's-complement & keeps negative-row parity right
            var columnFloor = FloorDiv(dividend: (pointX - parityOffset), divisor: m_cellSize.Value);

            for (var column = columnFloor; (column <= (columnFloor + 1L)); column++) {
                var deltaX = (pointX - (parityOffset + (column * m_cellSize.Value)));
                var deltaZ = (pointZ - (row * m_rowStride.Value));
                var distanceSq = ((deltaX * deltaX) + (deltaZ * deltaZ));

                if (distanceSq < bestDistanceSq) {
                    bestDistanceSq = distanceSq;
                    bestRow = row;
                    bestColumn = column;
                }
            }
        }

        if ((bestRow < 0L) || (bestRow >= m_height) || (bestColumn < 0L) || (bestColumn >= m_width)) {
            return false;
        }

        var cellIndex = ((((int)bestRow) * m_width) + ((int)bestColumn));

        return (0UL != (m_cells[cellIndex >> 6] & (1UL << (cellIndex & 63))));
    }

    /// <summary>Resolves ONE axis of the per-axis grid clamp <see cref="PlatformerBody.Step"/> applies after the wall
    /// and obstacle clamps: given the body's CURRENT coordinate on this axis and the CANDIDATE (post-move) coordinate,
    /// with the other axis held at its (already-resolved) value, returns the candidate unchanged when the destination
    /// cell is walkable, or <paramref name="current"/> ITSELF — held in place — when the move would enter a blocked
    /// cell. Mirrors the wall clamp's per-axis shape (attempt the move, clamp back on rejection, zero the inward
    /// velocity — see <see cref="PlatformerBody.Step"/>), so a body pressing into a blocked edge behaves the same way
    /// it already does against a wall or console: it settles at the boundary and stays there.
    /// <para>
    /// Holding at <paramref name="current"/> (rather than re-deriving a boundary from either coordinate's cell index
    /// every tick) is the stable choice: <paramref name="current"/> is this tick's PRE-move position, which passed
    /// last tick's clamp and is therefore never itself inside a blocked cell. A boundary point sits exactly on a cell
    /// edge, so floor-division of it is directionally ambiguous — it always resolves to the cell that starts there,
    /// which (approaching from below) is the very cell just clamped out of; re-deriving the boundary from that
    /// coordinate's cell would let the body creep one cell deeper into the blocked region every tick it keeps
    /// pressing. Holding at the already-known-safe <paramref name="current"/> sidesteps the ambiguity entirely —
    /// and is TESSELLATION-AGNOSTIC: a hex grid's Voronoi boundaries have the same knife-edge ambiguity, and the
    /// same hold-at-current resolution, so both shapes clamp through this one path via <see cref="IsBlocked"/>.
    /// </para>
    /// </summary>
    /// <param name="current">The body's coordinate on this axis before the move (guaranteed not itself blocked, by
    /// induction from the same guarantee last tick).</param>
    /// <param name="candidate">The body's coordinate on this axis after the move (pre-grid-clamp).</param>
    /// <param name="otherAxis">The other axis's coordinate (already clamped by any earlier step in this tick).</param>
    /// <returns>The candidate, or <paramref name="current"/> when the move would enter a blocked cell.</returns>
    public FixedQ4816 ClampAxisX(FixedQ4816 current, FixedQ4816 candidate, FixedQ4816 otherAxis) =>
        (IsBlocked(x: candidate, z: otherAxis) ? current : candidate);
    /// <inheritdoc cref="ClampAxisX"/>
    public FixedQ4816 ClampAxisZ(FixedQ4816 current, FixedQ4816 candidate, FixedQ4816 otherAxis) =>
        (IsBlocked(x: otherAxis, z: candidate) ? current : candidate);

    // Floor-divides a local (origin-relative) raw offset by the raw cell size — integer-only, floors toward negative
    // infinity so a point exactly on the origin lands in cell 0 and a point one cell short of the origin lands in
    // cell -1 (caught by the out-of-range check above, not wrapped). Returns false when the baked cell size is
    // non-positive (a malformed document defers to "not blocked" rather than dividing by zero/negative).
    private bool TryCellIndex(FixedQ4816 offset, out int cellIndex) {
        if (m_cellSize.Value <= 0L) {
            cellIndex = 0;

            return false;
        }

        var quotient = FloorDiv(dividend: offset.Value, divisor: m_cellSize.Value);

        if ((quotient < int.MinValue) || (quotient > int.MaxValue)) {
            cellIndex = 0;

            return false;
        }

        cellIndex = (int)quotient;

        return true;
    }

    // Integer floor division (floors toward negative infinity, unlike C#'s truncating /), matching the raw fixed-point
    // domain: both operands are raw Q48.16 longs, so this is exact integer arithmetic — no float.
    private static long FloorDiv(long dividend, long divisor) {
        var quotient = (dividend / divisor);
        var remainder = (dividend % divisor);

        return (((remainder != 0L) && ((remainder < 0L) != (divisor < 0L))) ? (quotient - 1L) : quotient);
    }
}
