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
    private static void CheckFiniteBound(string paramName, float value) {
        if (!float.IsFinite(f: value)) {
            throw new ArgumentException(
                message: $"The grid bound is not finite ({value}).",
                paramName: paramName
            );
        }
    }
    private static long CeilDiv(long dividend, long divisor) {
        var quotient = (dividend / divisor);
        var remainder = (dividend % divisor);

        return (((remainder != 0L) && ((remainder < 0L) == (divisor < 0L)))
            ? (quotient + 1L)
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
    private static void MarkTerrain(long[] heightRaw, int width, int height, long originXRaw, long originZRaw, WorldQueryTerrainInput patch) {
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

        var topYRaw = FixedQ4816.FromDouble(value: patch.TopY).Value;

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
        var minIndex = ((int)Math.Clamp(
            value: (minRaw - originRaw).FloorDivide(divisor: CellSizeRaw),
            min: 0L,
            max: axisCells
        ));
        var maxIndex = ((int)Math.Clamp(
            value: CeilDiv(
                dividend: (maxRaw - originRaw),
                divisor: CellSizeRaw
            ),
            min: 0L,
            max: axisCells
        ));

        minCell = minIndex;
        maxCellExclusive = maxIndex;

        return (maxIndex > minIndex);
    }

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
    /// <exception cref="ArgumentException">A grid bound, a rectangle edge, or a terrain height is not finite, or a
    /// maximum edge lies below its minimum.</exception>
    public static WorldQueryArtifact Bake(float minX, float minZ, float maxX, float maxZ, IEnumerable<WorldQueryTerrainInput> terrain, IEnumerable<WorldQueryBlockerInput> blockers) {
        ArgumentNullException.ThrowIfNull(argument: terrain);
        ArgumentNullException.ThrowIfNull(argument: blockers);
        CheckFiniteBound(
            paramName: nameof(minX),
            value: minX
        );
        CheckFiniteBound(
            paramName: nameof(minZ),
            value: minZ
        );
        CheckFiniteBound(
            paramName: nameof(maxX),
            value: maxX
        );
        CheckFiniteBound(
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

        var originXRaw = FixedQ4816.FromDouble(value: minX).Value;
        var originZRaw = FixedQ4816.FromDouble(value: minZ).Value;
        var maxXRaw = FixedQ4816.FromDouble(value: maxX).Value;
        var maxZRaw = FixedQ4816.FromDouble(value: maxZ).Value;
        var width = ((int)Math.Max(
            val1: 0L,
            val2: CeilDiv(
                dividend: (maxXRaw - originXRaw),
                divisor: CellSizeRaw
            )
        ));
        var height = ((int)Math.Max(
            val1: 0L,
            val2: CeilDiv(
                dividend: (maxZRaw - originZRaw),
                divisor: CellSizeRaw
            )
        ));
        var cellCount = (width * height);
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
            CheckFinite(
                index: patchIndex,
                kind: "Terrain",
                name: "TopY",
                value: patch.TopY
            );
            MarkTerrain(
                height: height,
                heightRaw: heightRaw,
                originXRaw: originXRaw,
                originZRaw: originZRaw,
                patch: patch,
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
