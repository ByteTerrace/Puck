using Puck.Maths;

namespace Puck.Demo.World;

/// <summary>One author-placed walkability override in float — the authoring-side mirror of
/// <see cref="WalkOverrideDocument"/>, applied in the document's own order.</summary>
public readonly record struct WalkOverrideInput(bool IsWalkable, float MinX, float MinZ, float MaxX, float MaxZ) {
    /// <summary>Builds an input from a wire <see cref="WalkOverrideDocument"/> (the <c>Kind</c> string resolved to a
    /// bool once, here).</summary>
    /// <param name="document">The override document.</param>
    /// <returns>The authoring-side override.</returns>
    public static WalkOverrideInput FromDocument(WalkOverrideDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        return new WalkOverrideInput(
            IsWalkable: string.Equals(a: document.Kind, b: "walkable", comparisonType: StringComparison.OrdinalIgnoreCase),
            MaxX: document.MaxX,
            MaxZ: document.MaxZ,
            MinX: document.MinX,
            MinZ: document.MinZ
        );
    }
}

/// <summary>The tessellation a bake produces — square cells (the default, and the wire default: a square document
/// omits its <c>Kind</c>) or pointy-top hexagonal cells (the Voronoi regions of staggered row centers; see
/// <see cref="WalkGridDocument"/> for the wire shape and the √3-at-bake-only determinism doctrine).</summary>
public enum WalkGridKind {
    /// <summary>Axis-aligned square cells, floor-divide point location.</summary>
    Square,
    /// <summary>Pointy-top hexagonal cells on staggered rows, nearest-center point location.</summary>
    Hex,
}

/// <summary>
/// The save-time walk-grid bake: authoring-side floats in (bounds, world-space footprint rectangles, the walk-band
/// parameters, author overrides, the player's half-extents), a deterministic <see cref="WalkGridDocument"/> out. Float
/// never crosses into the document — every stored value is either an integer dimension or a raw Q48.16 quantity, so
/// re-baking identical inputs any number of times, on any machine, produces byte-identical bytes.
/// <para>
/// Bake order (settled, and load-bearing for reproducibility): (1) mark cells blocked from footprints whose vertical
/// span intersects the walk band; (2) apply author overrides LAST, in document order (a <c>blocker</c> override adds
/// to the blocked set, a <c>walkable</c> override carves it back out — so an override authored after another can
/// undo it, exactly as documents read top to bottom); (3) DILATE the blocked set by the player's XZ half-extents, the
/// same fold-in <see cref="Overworld.FixedRoom.From"/> applies to walls and obstacles, so the sim can query a single
/// cell for the body's CENTER and get an answer valid for the body's whole box. The HEX bake keeps the same order but
/// folds step 3 into the marking itself: a hex cell is blocked iff its exact center lies within the footprint
/// rectangle EXPANDED by the player half-extent plus the hex circumradius (blocker overrides expand identically;
/// walkable carves use the raw rectangle — carving smaller is the conservative direction).
/// </para>
/// </summary>
public static class WalkGridBaker {
    /// <summary>The baked cell edge length in world units — fine enough to trace a player-sized gap accurately, coarse
    /// enough to keep the bitmap small. <c>0.25</c> world units is exactly <c>16384</c> in Q48.16 (16 fractional bits),
    /// so the quantization from world units to the raw cell size is exact, not rounded. A hex bake reuses it as the
    /// horizontal center-to-center spacing.</summary>
    public const float CellSize = 0.25f;

    /// <summary>Bakes a <see cref="WalkGridDocument"/> from decoupled authoring inputs. Deterministic: identical inputs
    /// (down to enumeration order) always produce byte-identical output.</summary>
    /// <param name="bounds">The authored world bounds — the grid spans exactly this rectangle.</param>
    /// <param name="footprints">The world-space blocking footprints (placements, terrain, cabinet/shelf stands),
    /// enumerated once in a stable order (the caller's order is the bake's order — see the type remarks on
    /// determinism).</param>
    /// <param name="walkBandFloorY">The walk band's floor — typically the room's floor height.</param>
    /// <param name="walkBandHeight">The walk band's height above <paramref name="walkBandFloorY"/> (typically the
    /// player's full height) — a footprint blocks only when its own vertical span intersects
    /// <c>[walkBandFloorY, walkBandFloorY + walkBandHeight]</c>.</param>
    /// <param name="overrides">The author overrides, applied in this exact order after every footprint.</param>
    /// <param name="playerHalfExtentX">The player box's X half-extent — the dilation radius on X.</param>
    /// <param name="playerHalfExtentZ">The player box's Z half-extent — the dilation radius on Z.</param>
    /// <param name="kind">The tessellation to bake (default <see cref="WalkGridKind.Square"/> — the pre-hex behavior,
    /// byte-identical documents included).</param>
    /// <returns>The baked, wire-ready document.</returns>
    public static WalkGridDocument Bake(
        WorldBoundsDocument bounds,
        IEnumerable<WorldFootprint> footprints,
        float walkBandFloorY,
        float walkBandHeight,
        IEnumerable<WalkOverrideInput> overrides,
        float playerHalfExtentX,
        float playerHalfExtentZ,
        WalkGridKind kind = WalkGridKind.Square
    ) {
        ArgumentNullException.ThrowIfNull(argument: bounds);
        ArgumentNullException.ThrowIfNull(argument: footprints);
        ArgumentNullException.ThrowIfNull(argument: overrides);

        // Branch by kind — the two tessellations keep separate bake paths on purpose (unifying them would force a
        // lossy abstraction over genuinely different marking/dilation models), and the square path is byte-for-byte
        // the pre-hex code.
        return ((kind == WalkGridKind.Hex)
            ? BakeHex(bounds: bounds, footprints: footprints, overrides: overrides, playerHalfExtentX: playerHalfExtentX, playerHalfExtentZ: playerHalfExtentZ, walkBandFloorY: walkBandFloorY, walkBandHeight: walkBandHeight)
            : BakeSquare(bounds: bounds, footprints: footprints, overrides: overrides, playerHalfExtentX: playerHalfExtentX, playerHalfExtentZ: playerHalfExtentZ, walkBandFloorY: walkBandFloorY, walkBandHeight: walkBandHeight));
    }

    // The square bake — the pre-hex Bake body, moved verbatim (its output documents stay byte-identical: Kind and
    // RowStrideRaw ride their null defaults, exactly what the wire emitted before hex existed).
    private static WalkGridDocument BakeSquare(
        WorldBoundsDocument bounds,
        IEnumerable<WorldFootprint> footprints,
        float walkBandFloorY,
        float walkBandHeight,
        IEnumerable<WalkOverrideInput> overrides,
        float playerHalfExtentX,
        float playerHalfExtentZ
    ) {
        var originXRaw = FixedQ4816.FromDouble(value: bounds.MinX).Value;
        var originZRaw = FixedQ4816.FromDouble(value: bounds.MinZ).Value;
        const long CellSizeRaw = 16384L; // CellSize (0.25) in Q48.16 — exact, not a rounded FromDouble conversion.
        var width = CellSpan(minRaw: originXRaw, maxRaw: FixedQ4816.FromDouble(value: bounds.MaxX).Value);
        var height = CellSpan(minRaw: originZRaw, maxRaw: FixedQ4816.FromDouble(value: bounds.MaxZ).Value);

        if ((width <= 0) || (height <= 0)) {
            return new WalkGridDocument(Cells: null, CellSizeRaw: CellSizeRaw, Height: 0, OriginXRaw: originXRaw, OriginZRaw: originZRaw, Width: 0);
        }

        var wordCount = (((width * height) + 63) / 64);
        var cells = new ulong[wordCount];
        var bandMinY = walkBandFloorY;
        var bandMaxY = (walkBandFloorY + walkBandHeight);

        // Step 1: mark cells blocked from every footprint whose vertical span intersects the walk band.
        foreach (var footprint in footprints) {
            if ((footprint.MaxY < bandMinY) || (footprint.MinY > bandMaxY)) {
                continue; // entirely outside the band (e.g. a lamp head overhanging it, or a flush floor slab) — harmless.
            }

            MarkRectangle(cells: cells, width: width, height: height, originXRaw: originXRaw, originZRaw: originZRaw, minX: footprint.MinX, minZ: footprint.MinZ, maxX: footprint.MaxX, maxZ: footprint.MaxZ, blocked: true);
        }

        // Step 2: author overrides, LAST, in document order — a blocker adds, a walkable carves.
        foreach (var over in overrides) {
            MarkRectangle(cells: cells, width: width, height: height, originXRaw: originXRaw, originZRaw: originZRaw, minX: over.MinX, minZ: over.MinZ, maxX: over.MaxX, maxZ: over.MaxZ, blocked: !over.IsWalkable);
        }

        // Step 3: dilate the blocked set by the player's half-extents (in CELLS, rounded up) so a single center-cell
        // query is valid for the whole body box — the same fold-in FixedRoom.From applies to walls/obstacles.
        var dilateX = DilationCells(halfExtent: playerHalfExtentX);
        var dilateZ = DilationCells(halfExtent: playerHalfExtentZ);
        var dilated = (((dilateX > 0) || (dilateZ > 0)) ? Dilate(cells: cells, width: width, height: height, dilateX: dilateX, dilateZ: dilateZ) : cells);

        return new WalkGridDocument(
            Cells: Convert.ToBase64String(inArray: PackBytes(cells: dilated)),
            CellSizeRaw: CellSizeRaw,
            Height: height,
            OriginXRaw: originXRaw,
            OriginZRaw: originZRaw,
            Width: width
        );
    }

    // The hex bake: pointy-top hexagonal cells as the Voronoi regions of staggered row centers (row r at
    // Z = origin + r·rowStride; X = origin + (r odd ? cellSize/2 : 0) + c·cellSize). The irrational √3 exists ONLY
    // here, float-side, rounded ONCE into the stored RowStrideRaw and into the circumradius margin — every query
    // after this is exact raw math against the stored document.
    private static WalkGridDocument BakeHex(
        WorldBoundsDocument bounds,
        IEnumerable<WorldFootprint> footprints,
        float walkBandFloorY,
        float walkBandHeight,
        IEnumerable<WalkOverrideInput> overrides,
        float playerHalfExtentX,
        float playerHalfExtentZ
    ) {
        const long CellSizeRaw = 16384L; // 0.25 world units in Q48.16 — the hex horizontal center-to-center spacing.
        const string HexKind = "hex";

        var originXRaw = FixedQ4816.FromDouble(value: bounds.MinX).Value;
        var originZRaw = FixedQ4816.FromDouble(value: bounds.MinZ).Value;
        // Row stride = CellSize × √3/2, chosen and rounded ONCE (16384 × 0.8660254… = 14188.96… → 14189 raw).
        var rowStrideRaw = ((long)Math.Round(value: (CellSizeRaw * (Math.Sqrt(d: 3d) / 2d)), mode: MidpointRounding.ToEven));
        var spanXRaw = (FixedQ4816.FromDouble(value: bounds.MaxX).Value - originXRaw);
        var spanZRaw = (FixedQ4816.FromDouble(value: bounds.MaxZ).Value - originZRaw);
        // +1 column: odd rows stagger their centers +half a cell, so covering the far X edge on those rows needs one
        // slot past the even-row count. Rows ceil-divide the Z span by the stride (a partial trailing row gets a slot).
        var width = ((spanXRaw <= 0L) ? 0 : ((int)((spanXRaw + (CellSizeRaw - 1L)) / CellSizeRaw) + 1));
        var height = ((spanZRaw <= 0L) ? 0 : (int)((spanZRaw + (rowStrideRaw - 1L)) / rowStrideRaw));

        if ((width <= 0) || (height <= 0)) {
            return new WalkGridDocument(Cells: null, CellSizeRaw: CellSizeRaw, Height: 0, Kind: HexKind, OriginXRaw: originXRaw, OriginZRaw: originZRaw, RowStrideRaw: rowStrideRaw, Width: 0);
        }

        var cells = new ulong[(((width * height) + 63) / 64)];
        var bandMinY = walkBandFloorY;
        var bandMaxY = (walkBandFloorY + walkBandHeight);
        // Hex has NO post-pass dilation: a hex cell is blocked iff its EXACT CENTER (from raws) lies within the
        // footprint rectangle expanded by the player half-extent plus the hex circumradius — CellSize/√3, the farthest
        // any point of a cell sits from its center — the same conservative fold-in the square path achieves by
        // dilating whole cells after marking.
        var circumradius = (CellSize / MathF.Sqrt(x: 3f));
        var expandX = (playerHalfExtentX + circumradius);
        var expandZ = (playerHalfExtentZ + circumradius);

        // Step 1: footprints whose vertical span intersects the walk band, expanded, in caller order.
        foreach (var footprint in footprints) {
            if ((footprint.MaxY < bandMinY) || (footprint.MinY > bandMaxY)) {
                continue; // entirely outside the band (e.g. a lamp head overhanging it, or a flush floor slab) — harmless.
            }

            MarkHexRectangle(cells: cells, width: width, height: height, originXRaw: originXRaw, originZRaw: originZRaw, cellSizeRaw: CellSizeRaw, rowStrideRaw: rowStrideRaw, minX: (footprint.MinX - expandX), minZ: (footprint.MinZ - expandZ), maxX: (footprint.MaxX + expandX), maxZ: (footprint.MaxZ + expandZ), blocked: true);
        }

        // Step 2: author overrides, LAST, in document order — a blocker expands like a footprint (the body's size
        // still folds in), a walkable carve uses the RAW rectangle (carving smaller is the conservative direction).
        foreach (var over in overrides) {
            if (over.IsWalkable) {
                MarkHexRectangle(cells: cells, width: width, height: height, originXRaw: originXRaw, originZRaw: originZRaw, cellSizeRaw: CellSizeRaw, rowStrideRaw: rowStrideRaw, minX: over.MinX, minZ: over.MinZ, maxX: over.MaxX, maxZ: over.MaxZ, blocked: false);
            } else {
                MarkHexRectangle(cells: cells, width: width, height: height, originXRaw: originXRaw, originZRaw: originZRaw, cellSizeRaw: CellSizeRaw, rowStrideRaw: rowStrideRaw, minX: (over.MinX - expandX), minZ: (over.MinZ - expandZ), maxX: (over.MaxX + expandX), maxZ: (over.MaxZ + expandZ), blocked: true);
            }
        }

        return new WalkGridDocument(
            Cells: Convert.ToBase64String(inArray: PackBytes(cells: cells)),
            CellSizeRaw: CellSizeRaw,
            Height: height,
            Kind: HexKind,
            OriginXRaw: originXRaw,
            OriginZRaw: originZRaw,
            RowStrideRaw: rowStrideRaw,
            Width: width
        );
    }

    // Sets (blocked) or clears (walkable carve) every hex cell whose exact CENTER — computed from the stored raws —
    // lies inside the world rectangle [minX,maxX] × [minZ,maxZ] (inclusive edges; the float rect quantizes ONCE per
    // edge, then the per-cell loop is pure raw arithmetic).
    private static void MarkHexRectangle(ulong[] cells, int width, int height, long originXRaw, long originZRaw, long cellSizeRaw, long rowStrideRaw, float minX, float minZ, float maxX, float maxZ, bool blocked) {
        var minXRel = (FixedQ4816.FromDouble(value: minX).Value - originXRaw);
        var maxXRel = (FixedQ4816.FromDouble(value: maxX).Value - originXRaw);
        var minZRel = (FixedQ4816.FromDouble(value: minZ).Value - originZRaw);
        var maxZRel = (FixedQ4816.FromDouble(value: maxZ).Value - originZRaw);
        // Rows whose center Z (= row·rowStride) lies inside the rect: ceil(min/stride) .. floor(max/stride), clamped.
        var minRow = ((int)Math.Clamp(value: CeilDivBy(dividend: minZRel, divisor: rowStrideRaw), min: 0L, max: height));
        var maxRow = ((int)Math.Clamp(value: FloorDivBy(dividend: maxZRel, divisor: rowStrideRaw), min: -1L, max: (height - 1L)));

        for (var row = minRow; (row <= maxRow); row++) {
            var parityOffset = (((row & 1) != 0) ? (cellSizeRaw / 2L) : 0L);
            var minColumn = ((int)Math.Clamp(value: CeilDivBy(dividend: (minXRel - parityOffset), divisor: cellSizeRaw), min: 0L, max: width));
            var maxColumn = ((int)Math.Clamp(value: FloorDivBy(dividend: (maxXRel - parityOffset), divisor: cellSizeRaw), min: -1L, max: (width - 1L)));

            for (var column = minColumn; (column <= maxColumn); column++) {
                var cellIndex = ((row * width) + column);
                var word = (cellIndex >> 6);
                var bit = cellIndex & 63;

                cells[word] = (blocked ? cells[word] | (1UL << bit) : cells[word] & ~(1UL << bit));
            }
        }
    }

    // Integer floor/ceiling division on raw Q48.16 longs with an arbitrary positive divisor (the hex row stride is
    // not a power of two, unlike the square path's fixed 16384) — exact integer arithmetic, no float.
    private static long FloorDivBy(long dividend, long divisor) {
        var quotient = (dividend / divisor);
        var remainder = (dividend % divisor);

        return (((remainder != 0L) && ((remainder < 0L) != (divisor < 0L))) ? (quotient - 1L) : quotient);
    }
    private static long CeilDivBy(long dividend, long divisor) =>
        -FloorDivBy(dividend: -dividend, divisor: divisor);

    // The cell span across [min, max) at the fixed 0.25-unit cell size, rounded UP so the grid always fully covers
    // the authored bounds (a partial trailing cell still gets a slot, blocked by default only if something marks it).
    private static int CellSpan(long minRaw, long maxRaw) {
        var spanRaw = (maxRaw - minRaw);

        if (spanRaw <= 0L) {
            return 0;
        }

        return (int)((spanRaw + 16383L) / 16384L); // ceil-divide by the raw cell size (16384 = 0.25 in Q48.16).
    }

    // How many whole cells a half-extent spans, rounded UP — the dilation radius in cell units (world units / 0.25,
    // ceiling). A zero or negative half-extent dilates by zero cells (no fold-in, matching an author who declares no
    // player size).
    private static int DilationCells(float halfExtent) {
        if (halfExtent <= 0f) {
            return 0;
        }

        return (int)MathF.Ceiling(x: (halfExtent / CellSize));
    }

    // Sets (or, when !blocked and reused for carving, clears) every cell whose center falls inside [minX,maxX) x
    // [minZ,maxZ) in world space. Uses raw Q48.16 arithmetic to convert the rectangle to a cell-index range so the
    // authoring float only ever quantizes ONCE per rectangle edge (no float touches the per-cell loop).
    private static void MarkRectangle(ulong[] cells, int width, int height, long originXRaw, long originZRaw, float minX, float minZ, float maxX, float maxZ, bool blocked) {
        var minCellX = Math.Max(val1: CellIndex(originRaw: originXRaw, worldValue: minX), val2: 0);
        var maxCellX = Math.Min(val1: (CellIndex(originRaw: originXRaw, worldValue: maxX) + 1), val2: width);
        var minCellZ = Math.Max(val1: CellIndex(originRaw: originZRaw, worldValue: minZ), val2: 0);
        var maxCellZ = Math.Min(val1: (CellIndex(originRaw: originZRaw, worldValue: maxZ) + 1), val2: height);

        for (var cellZ = minCellZ; (cellZ < maxCellZ); cellZ++) {
            for (var cellX = minCellX; (cellX < maxCellX); cellX++) {
                var cellIndex = ((cellZ * width) + cellX);
                var word = (cellIndex >> 6);
                var bit = cellIndex & 63;

                cells[word] = (blocked ? cells[word] | (1UL << bit) : cells[word] & ~(1UL << bit));
            }
        }
    }

    // Quantizes one world-space coordinate to its cell index relative to a raw origin — the ONE float-to-fixed
    // conversion per rectangle edge (floor toward negative infinity, matching FixedWalkGrid's own query math).
    private static int CellIndex(long originRaw, float worldValue) {
        var raw = FixedQ4816.FromDouble(value: worldValue).Value;
        var offsetRaw = (raw - originRaw);

        return (int)FloorDivRaw(dividend: offsetRaw);
    }
    private static long FloorDivRaw(long dividend) {
        const long Divisor = 16384L;
        var quotient = (dividend / Divisor);
        var remainder = (dividend % Divisor);

        return (((remainder != 0L) && (remainder < 0L)) ? (quotient - 1L) : quotient);
    }

    // Grows the blocked set by dilateX/dilateZ cells in every direction: a cell is blocked in the dilated set when any
    // cell within that Chebyshev-per-axis rectangle of it was blocked in the source set. O(width*height*dilateX*dilateZ)
    // — the grid is small (a town lot at 0.25 units/cell) and this runs once at save time, never per tick.
    private static ulong[] Dilate(ulong[] cells, int width, int height, int dilateX, int dilateZ) {
        var result = new ulong[cells.Length];

        for (var cellZ = 0; (cellZ < height); cellZ++) {
            for (var cellX = 0; (cellX < width); cellX++) {
                if (!AnyBlockedInWindow(cells: cells, width: width, height: height, centerX: cellX, centerZ: cellZ, dilateX: dilateX, dilateZ: dilateZ)) {
                    continue;
                }

                var cellIndex = ((cellZ * width) + cellX);

                result[(cellIndex >> 6)] |= (1UL << (cellIndex & 63));
            }
        }

        return result;
    }
    private static bool AnyBlockedInWindow(ulong[] cells, int width, int height, int centerX, int centerZ, int dilateX, int dilateZ) {
        var minX = Math.Max(val1: (centerX - dilateX), val2: 0);
        var maxX = Math.Min(val1: (centerX + dilateX), val2: (width - 1));
        var minZ = Math.Max(val1: (centerZ - dilateZ), val2: 0);
        var maxZ = Math.Min(val1: (centerZ + dilateZ), val2: (height - 1));

        for (var z = minZ; (z <= maxZ); z++) {
            for (var x = minX; (x <= maxX); x++) {
                var cellIndex = ((z * width) + x);

                if (0UL != (cells[(cellIndex >> 6)] & (1UL << (cellIndex & 63)))) {
                    return true;
                }
            }
        }

        return false;
    }
    private static byte[] PackBytes(ulong[] cells) {
        var bytes = new byte[(cells.Length * 8)];

        for (var word = 0; (word < cells.Length); word++) {
            BitConverter.TryWriteBytes(destination: bytes.AsSpan(start: (word * 8), length: 8), value: cells[word]);
        }

        return bytes;
    }
}
