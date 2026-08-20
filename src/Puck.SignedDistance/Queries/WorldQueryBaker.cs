using Puck.Maths;

namespace Puck.SignedDistance.Queries;

/// <summary>
/// Bakes float-authored terrain/blocker rectangles into a deterministic <see cref="WorldQueryArtifact"/>, following
/// a quantize-once-per-edge discipline: every rectangle edge is snapped to raw Q48.16 via
/// <see cref="FixedQ4816.FromDouble"/> exactly once, and every per-cell loop after that is pure integer arithmetic —
/// float never touches the inner loop.
/// </summary>
public static class WorldQueryBaker {
    private const long CellSizeRaw = 16384L;

    /// <summary>The default cell edge length (world units) — matches the walk grid's own default, a reasonable
    /// resolution for both foot-traffic blocking and RTS ground-height sampling. Exactly <c>16384</c> raw Q48.16
    /// (no rounding), like the walk grid's cell size.</summary>
    public const float CellSize = 0.25f;

    // A rectangle edge that is not finite has no cell span: NaN compares false against every bound and quantizes to
    // 0, and an infinity quantizes to the Q48.16 carrier's extreme. Either one bakes as authored geometry
    // indistinguishable from a real edge, so both are refused here rather than at the cell loop.
    private static void CheckRectangle(string kind, int index, float minX, float minZ, float maxX, float maxZ) {
        CheckFinite(
            index: index,
            kind: kind,
            name: "MinX",
            value: minX
        );
        CheckFinite(
            index: index,
            kind: kind,
            name: "MinZ",
            value: minZ
        );
        CheckFinite(
            index: index,
            kind: kind,
            name: "MaxX",
            value: maxX
        );
        CheckFinite(
            index: index,
            kind: kind,
            name: "MaxZ",
            value: maxZ
        );

        if (maxX < minX) {
            throw new ArgumentException(message: $"{kind} rectangle {index} has MaxX {maxX} below MinX {minX}.");
        }

        if (maxZ < minZ) {
            throw new ArgumentException(message: $"{kind} rectangle {index} has MaxZ {maxZ} below MinZ {minZ}.");
        }
    }
    private static void CheckFinite(string kind, int index, string name, float value) {
        if (!float.IsFinite(f: value)) {
            throw new ArgumentException(message: $"{kind} rectangle {index} has a non-finite {name} ({value}).");
        }
    }
    // The grid's own corner and far edge are the only quantized coordinates the artifact stores verbatim, so a value
    // the Q48.16 carrier can only saturate to would place the grid somewhere the caller never authored.
    private static long QuantizeBound(string paramName, float value) {
        if (!float.IsFinite(f: value)) {
            throw new ArgumentException(
                message: $"The grid bound is not finite ({value}).",
                paramName: paramName
            );
        }

        var raw = FixedQ4816.FromDouble(value: value).Value;

        if (
            (raw == long.MinValue) ||
            (raw == long.MaxValue)
        ) {
            throw new ArgumentException(
                message: $"The grid bound ({value}) is outside the Q48.16 coordinate range and would saturate to {raw}.",
                paramName: paramName
            );
        }

        return raw;
    }
    // A terrain height quantizing to NoHeightSentinel would erase the very cells the caller authored, and one
    // quantizing to the opposite extreme would store a height nowhere near the authored one.
    private static long QuantizeTopY(int index, float value) {
        CheckFinite(
            index: index,
            kind: "Terrain",
            name: "TopY",
            value: value
        );

        var raw = FixedQ4816.FromDouble(value: value).Value;

        if (
            (raw == WorldQueryArtifact.NoHeightSentinel) ||
            (raw == long.MaxValue)
        ) {
            throw new ArgumentException(message: $"Terrain rectangle {index} has a TopY ({value}) outside the Q48.16 height range, which would saturate to {raw}.");
        }

        return raw;
    }
    // The number of cells covering [originRaw, maxRaw], refusing a span no 32-bit cell index can address rather than
    // narrowing it: the unchecked narrowing turns a grid wider than 2^31 cells into a silently empty artifact.
    private static int AxisCells(string paramName, long originRaw, long maxRaw) {
        var cells = CeilDiv(
            dividend: ((((Int128)maxRaw)) - originRaw),
            divisor: CellSizeRaw
        );

        if (cells > int.MaxValue) {
            throw new ArgumentException(
                message: $"The grid spans {cells} cells of {CellSize} along one axis, which overflows a 32-bit cell index.",
                paramName: paramName
            );
        }

        return ((cells < Int128.Zero)
            ? 0
            : ((int)cells)
        );
    }
    private static Int128 CeilDiv(Int128 dividend, Int128 divisor) {
        var quotient = (dividend / divisor);
        var remainder = (dividend % divisor);

        return (((remainder != Int128.Zero) && ((remainder < Int128.Zero) == (divisor < Int128.Zero)))
            ? (quotient + Int128.One)
            : quotient
        );
    }
    private static void MarkBlocked(ulong[] blocked, int width, int height, long originXRaw, long originZRaw, WorldQueryBlockerInput blocker) {
        if (!TryCellSpan(
            originRaw: originXRaw,
            minValue: blocker.MinX,
            maxValue: blocker.MaxX,
            axisCells: width,
            minCell: out var minColumn,
            maxCellExclusive: out var maxColumn
        )) {
            return;
        }

        if (!TryCellSpan(
            originRaw: originZRaw,
            minValue: blocker.MinZ,
            maxValue: blocker.MaxZ,
            axisCells: height,
            minCell: out var minRow,
            maxCellExclusive: out var maxRow
        )) {
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
    private static void MarkTerrain(long[] heightRaw, int width, int height, long originXRaw, long originZRaw, WorldQueryTerrainInput patch, long topYRaw) {
        if (!TryCellSpan(
            originRaw: originXRaw,
            minValue: patch.MinX,
            maxValue: patch.MaxX,
            axisCells: width,
            minCell: out var minColumn,
            maxCellExclusive: out var maxColumn
        )) {
            return;
        }

        if (!TryCellSpan(
            originRaw: originZRaw,
            minValue: patch.MinZ,
            maxValue: patch.MaxZ,
            axisCells: height,
            minCell: out var minRow,
            maxCellExclusive: out var maxRow
        )) {
            return;
        }

        for (var row = minRow; (row < maxRow); row++) {
            var rowBase = (row * width);

            for (var column = minColumn; (column < maxColumn); column++) {
                heightRaw[(rowBase + column)] = topYRaw;
            }
        }
    }
    // Quantizes a rectangle's [min,max] edge on one axis to a clamped [minCell, maxCellExclusive) cell span. Each
    // edge is snapped to raw Q48.16 exactly once (the quantize-once-per-edge discipline); the loop the caller then
    // runs is pure integer arithmetic. Returns false when the span is empty or entirely out of grid bounds.
    private static bool TryCellSpan(long originRaw, float minValue, float maxValue, int axisCells, out int minCell, out int maxCellExclusive) {
        var minRaw = FixedQ4816.FromDouble(value: minValue).Value;
        var maxRaw = FixedQ4816.FromDouble(value: maxValue).Value;
        // Widened before the subtraction: an edge saturated at the carrier and an origin of the opposite sign differ
        // by more than a long holds, and the clamp that follows only makes sense on the true difference.
        var minIndex = ClampIndex(
            axisCells: axisCells,
            value: ((((Int128)minRaw)) - originRaw).FloorDivide(divisor: ((Int128)CellSizeRaw))
        );
        var maxIndex = ClampIndex(
            axisCells: axisCells,
            value: CeilDiv(
                dividend: ((((Int128)maxRaw)) - originRaw),
                divisor: CellSizeRaw
            )
        );

        minCell = minIndex;
        maxCellExclusive = maxIndex;

        return (maxIndex > minIndex);
    }
    private static int ClampIndex(Int128 value, int axisCells) =>
        ((value < Int128.Zero)
            ? 0
            : ((value > axisCells)
                ? axisCells
                : ((int)value)
            )
        );

    /// <summary>Bakes an artifact covering <c>[minX,maxX] x [minZ,maxZ]</c>. A maximum edge that is not aligned to
    /// <see cref="CellSize"/> rounds outward so the final partial cell remains inside the artifact.</summary>
    /// <param name="minX">The grid's minimum X bound (world units).</param>
    /// <param name="minZ">The grid's minimum Z bound.</param>
    /// <param name="maxX">The grid's maximum X bound.</param>
    /// <param name="maxZ">The grid's maximum Z bound.</param>
    /// <param name="terrain">Terrain rectangles, applied in order (a later rectangle overwrites an earlier one's
    /// height where they overlap — "last authored wins," matching the walk grid's override-application order).</param>
    /// <param name="blockers">Blocker rectangles — any covered cell is marked blocked (OR, not overwrite).</param>
    /// <returns>The baked artifact.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="terrain"/> or <paramref name="blockers"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A grid bound, a rectangle edge, or a terrain height is not finite; a grid
    /// bound or a terrain height lies outside the Q48.16 range the artifact stores; a maximum edge lies below its
    /// minimum; or the grid spans more cells than a 32-bit cell index addresses.</exception>
    public static WorldQueryArtifact Bake(float minX, float minZ, float maxX, float maxZ, IEnumerable<WorldQueryTerrainInput> terrain, IEnumerable<WorldQueryBlockerInput> blockers) {
        ArgumentNullException.ThrowIfNull(argument: terrain);
        ArgumentNullException.ThrowIfNull(argument: blockers);

        var originXRaw = QuantizeBound(
            paramName: nameof(minX),
            value: minX
        );
        var originZRaw = QuantizeBound(
            paramName: nameof(minZ),
            value: minZ
        );
        var maxXRaw = QuantizeBound(
            paramName: nameof(maxX),
            value: maxX
        );
        var maxZRaw = QuantizeBound(
            paramName: nameof(maxZ),
            value: maxZ
        );

        if (maxX < minX) {
            throw new ArgumentException(
                message: $"The grid's maximum X ({maxX}) lies below its minimum ({minX}).",
                paramName: nameof(maxX)
            );
        }

        if (maxZ < minZ) {
            throw new ArgumentException(
                message: $"The grid's maximum Z ({maxZ}) lies below its minimum ({minZ}).",
                paramName: nameof(maxZ)
            );
        }

        var width = AxisCells(
            maxRaw: maxXRaw,
            originRaw: originXRaw,
            paramName: nameof(maxX)
        );
        var height = AxisCells(
            maxRaw: maxZRaw,
            originRaw: originZRaw,
            paramName: nameof(maxZ)
        );
        var cellCountLong = ((((long)width)) * height);

        if (cellCountLong > int.MaxValue) {
            throw new ArgumentException(
                message: $"A {width}x{height} grid holds {cellCountLong} cells, which overflows a 32-bit cell index.",
                paramName: nameof(maxX)
            );
        }

        var cellCount = ((int)cellCountLong);
        var heightRaw = new long[cellCount];
        var blocked = new ulong[WorldQueryArtifact.BlockedWordCount(cellCount: cellCount)];
        var patchIndex = 0;
        var blockerIndex = 0;

        Array.Fill(
            array: heightRaw,
            value: WorldQueryArtifact.NoHeightSentinel
        );

        foreach (var patch in terrain) {
            CheckRectangle(
                index: patchIndex,
                kind: "Terrain",
                maxX: patch.MaxX,
                maxZ: patch.MaxZ,
                minX: patch.MinX,
                minZ: patch.MinZ
            );
            MarkTerrain(
                height: height,
                heightRaw: heightRaw,
                originXRaw: originXRaw,
                originZRaw: originZRaw,
                patch: patch,
                topYRaw: QuantizeTopY(
                    index: patchIndex,
                    value: patch.TopY
                ),
                width: width
            );

            patchIndex++;
        }

        foreach (var blocker in blockers) {
            CheckRectangle(
                index: blockerIndex,
                kind: "Blocker",
                maxX: blocker.MaxX,
                maxZ: blocker.MaxZ,
                minX: blocker.MinX,
                minZ: blocker.MinZ
            );
            MarkBlocked(
                blocked: blocked,
                blocker: blocker,
                height: height,
                originXRaw: originXRaw,
                originZRaw: originZRaw,
                width: width
            );

            blockerIndex++;
        }

        return new WorldQueryArtifact(
            blocked: blocked,
            cellSizeRaw: CellSizeRaw,
            height: height,
            heightRaw: heightRaw,
            originXRaw: originXRaw,
            originZRaw: originZRaw,
            width: width
        );
    }
}
