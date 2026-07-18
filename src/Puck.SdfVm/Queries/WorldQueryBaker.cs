using Puck.Maths;

namespace Puck.SdfVm.Queries;

/// <summary>
/// Bakes float-authored terrain/blocker rectangles into a deterministic <see cref="WorldQueryArtifact"/> — the
/// query-namespace sibling of <c>Puck.Demo.World.WalkGridBaker</c>, following the SAME quantize-once-per-edge
/// discipline (every rectangle edge is snapped to raw Q48.16 via <see cref="FixedQ4816.FromDouble"/> exactly once;
/// every per-cell loop after that is pure integer arithmetic — float never touches the inner loop). This type
/// cannot literally reuse <c>WalkGridBaker</c> (it lives in <c>Puck.Demo</c>, downstream of <c>Puck.SdfVm</c>) —
/// it is an independent, self-contained generalization living entirely inside the engine-side query namespace.
/// </summary>
public static class WorldQueryBaker {
    /// <summary>The default cell edge length (world units) — matches the walk grid's own default, a reasonable
    /// resolution for both foot-traffic blocking and RTS ground-height sampling. Exactly <c>16384</c> raw Q48.16
    /// (no rounding), like the walk grid's cell size.</summary>
    public const float CellSize = 0.25f;

    private const long CellSizeRaw = 16384L;

    /// <summary>Bakes an artifact covering <c>[minX,maxX] x [minZ,maxZ]</c>.</summary>
    /// <param name="minX">The grid's minimum X bound (world units).</param>
    /// <param name="minZ">The grid's minimum Z bound.</param>
    /// <param name="maxX">The grid's maximum X bound.</param>
    /// <param name="maxZ">The grid's maximum Z bound.</param>
    /// <param name="terrain">Terrain rectangles, applied in order (a later rectangle overwrites an earlier one's
    /// height where they overlap — "last authored wins," matching the walk grid's override-application order).</param>
    /// <param name="blockers">Blocker rectangles — any covered cell is marked blocked (OR, not overwrite).</param>
    /// <returns>The baked artifact.</returns>
    public static WorldQueryArtifact Bake(float minX, float minZ, float maxX, float maxZ, IEnumerable<WorldQueryTerrainInput> terrain, IEnumerable<WorldQueryBlockerInput> blockers) {
        ArgumentNullException.ThrowIfNull(argument: terrain);
        ArgumentNullException.ThrowIfNull(argument: blockers);

        var originXRaw = FixedQ4816.FromDouble(value: minX).Value;
        var originZRaw = FixedQ4816.FromDouble(value: minZ).Value;
        var maxXRaw = FixedQ4816.FromDouble(value: maxX).Value;
        var maxZRaw = FixedQ4816.FromDouble(value: maxZ).Value;
        var width = (int)Math.Max(val1: 0L, val2: FloorDiv(dividend: (maxXRaw - originXRaw), divisor: CellSizeRaw));
        var height = (int)Math.Max(val1: 0L, val2: FloorDiv(dividend: (maxZRaw - originZRaw), divisor: CellSizeRaw));
        var cellCount = (width * height);
        var heightRaw = new long[cellCount];
        var blocked = new ulong[Math.Max(val1: 1, val2: ((cellCount + 63) / 64))];

        Array.Fill(array: heightRaw, value: WorldQueryArtifact.NoHeightSentinel);

        foreach (var patch in terrain) {
            MarkTerrain(heightRaw: heightRaw, width: width, height: height, originXRaw: originXRaw, originZRaw: originZRaw, patch: patch);
        }

        foreach (var blocker in blockers) {
            MarkBlocked(blocked: blocked, width: width, height: height, originXRaw: originXRaw, originZRaw: originZRaw, blocker: blocker);
        }

        return new WorldQueryArtifact(
            Blocked: blocked,
            CellSizeRaw: CellSizeRaw,
            Height: height,
            HeightRaw: heightRaw,
            OriginXRaw: originXRaw,
            OriginZRaw: originZRaw,
            Width: width
        );
    }

    private static void MarkTerrain(long[] heightRaw, int width, int height, long originXRaw, long originZRaw, WorldQueryTerrainInput patch) {
        if (!TryCellSpan(originRaw: originXRaw, minValue: patch.MinX, maxValue: patch.MaxX, axisCells: width, minCell: out var minColumn, maxCellExclusive: out var maxColumn)) {
            return;
        }

        if (!TryCellSpan(originRaw: originZRaw, minValue: patch.MinZ, maxValue: patch.MaxZ, axisCells: height, minCell: out var minRow, maxCellExclusive: out var maxRow)) {
            return;
        }

        var topYRaw = FixedQ4816.FromDouble(value: patch.TopY).Value;

        for (var row = minRow; (row < maxRow); row++) {
            var rowBase = (row * width);

            for (var column = minColumn; (column < maxColumn); column++) {
                heightRaw[(rowBase + column)] = topYRaw;
            }
        }
    }
    private static void MarkBlocked(ulong[] blocked, int width, int height, long originXRaw, long originZRaw, WorldQueryBlockerInput blocker) {
        if (!TryCellSpan(originRaw: originXRaw, minValue: blocker.MinX, maxValue: blocker.MaxX, axisCells: width, minCell: out var minColumn, maxCellExclusive: out var maxColumn)) {
            return;
        }

        if (!TryCellSpan(originRaw: originZRaw, minValue: blocker.MinZ, maxValue: blocker.MaxZ, axisCells: height, minCell: out var minRow, maxCellExclusive: out var maxRow)) {
            return;
        }

        for (var row = minRow; (row < maxRow); row++) {
            var rowBase = (row * width);

            for (var column = minColumn; (column < maxColumn); column++) {
                var cellIndex = (rowBase + column);

                blocked[(cellIndex >> 6)] |= (1UL << (cellIndex & 63));
            }
        }
    }

    // Quantizes a rectangle's [min,max] edge on one axis to a clamped [minCell, maxCellExclusive) cell span. Each
    // edge is snapped to raw Q48.16 exactly once (the quantize-once-per-edge discipline); the loop the caller then
    // runs is pure integer arithmetic. Returns false when the span is empty or entirely out of grid bounds.
    private static bool TryCellSpan(long originRaw, float minValue, float maxValue, int axisCells, out int minCell, out int maxCellExclusive) {
        var minRaw = FixedQ4816.FromDouble(value: minValue).Value;
        var maxRaw = FixedQ4816.FromDouble(value: maxValue).Value;
        var minIndex = (int)Math.Clamp(value: FloorDiv(dividend: (minRaw - originRaw), divisor: CellSizeRaw), min: 0L, max: axisCells);
        var maxIndex = (int)Math.Clamp(value: CeilDiv(dividend: (maxRaw - originRaw), divisor: CellSizeRaw), min: 0L, max: axisCells);

        minCell = minIndex;
        maxCellExclusive = maxIndex;

        return (maxIndex > minIndex);
    }
    private static long FloorDiv(long dividend, long divisor) {
        var quotient = (dividend / divisor);
        var remainder = (dividend % divisor);

        return (((remainder != 0L) && ((remainder < 0L) != (divisor < 0L))) ? (quotient - 1L) : quotient);
    }
    private static long CeilDiv(long dividend, long divisor) {
        var quotient = (dividend / divisor);
        var remainder = (dividend % divisor);

        return (((remainder != 0L) && ((remainder < 0L) == (divisor < 0L))) ? (quotient + 1L) : quotient);
    }
}
