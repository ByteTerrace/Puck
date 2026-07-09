using System.Numerics;

namespace Puck.SdfVm;

/// <summary>One instance's world-space cull footprint, as the grid packer sees it (produced by
/// <see cref="SdfProgram"/> from the same packed radius <c>PackInstances</c> writes, so the grid bins EXACTLY the
/// spheres the beam later tests).</summary>
/// <param name="Center">The instance's STATIC world-space bound center. Meaningful only when <see cref="Binnable"/> is
/// <see langword="true"/> (a dynamic instance's center moves per frame, so it is never binned).</param>
/// <param name="Radius">The packed bound radius: the float-safety-padded radius for a live instance, the
/// <see cref="SdfProgram.UnmaskableBoundRadius"/> sentinel for an unmaskable one, or negative for a PARKED slot.</param>
/// <param name="Binnable">Whether this instance has a fixed, finite world-space bound the frozen grid can bin: active,
/// static, and maskable. A dynamic or unmaskable (but still active) instance is <see langword="false"/> here yet lands
/// in the ALWAYS-tested list; a parked instance (negative <see cref="Radius"/>) is neither binned nor always-tested.</param>
internal readonly record struct SdfInstanceGridInput(Vector3 Center, float Radius, bool Binnable);

/// <summary>Builds the deterministic world-space uniform grid the tile-cull beam prepass walks instead of testing every
/// instance in every tile (docs/sdf-bench-notes.md, the 2026-07-09 carve ladder). Static, maskable instances are
/// counting-sorted into a CSR cell directory (count → exclusive prefix-sum → scatter, all in instance-index order — no
/// atomics, no wave intrinsics, bit-identical every run); dynamic, unmaskable, and sprawling instances go in an
/// always-tested list (the world-segment-list pattern). The beam then tests only the instances in the grid cells the
/// tile's cone footprint overlaps plus the always-list, so beam cost tracks instances NEAR the cone, not the total.
/// <para>The packed block is a self-contained <see cref="uint"/> array appended to the program word stream after the
/// world-segment list. KEEP IN SYNC with the grid decode in Assets/Shaders/Sdf/sdf-vm.hlsli (<c>sdfGrid*</c>) and the
/// cell walk in sdf-world.hlsli (<c>collectInstanceGridMask</c>). Extracted from <see cref="SdfProgram"/> as its own
/// type both to keep that class under its analyzer complexity ceilings and because the grid build is a self-contained
/// packer.</para>
/// <para>Block layout (all <see cref="uint"/>-granular, indices RELATIVE to the block start; the whole block is padded to
/// a 4-uint boundary so the containing <c>uint4</c> stream stays aligned):</para>
/// <list type="table">
/// <item><description><b>Header</b> (16 uints = 4 <c>uint4</c> rows): <c>[0]</c> enabled (1 = grid path, 0 = flat
/// fallback); <c>[1..3]</c> dimX/dimY/dimZ; <c>[4..6]</c> grid-AABB origin xyz (float bits); <c>[7]</c> invCellSize
/// (host-baked 1/cellSize — the shader never divides); <c>[8]</c> cellSize (world edge, for the cone-march step);
/// <c>[9]</c> footprintPad (host-baked = the max binned radius, the world margin the cone footprint adds); <c>[10]</c>
/// cellStartWord; <c>[11]</c> entryWord; <c>[12]</c> alwaysWord (all uint offsets from the block start); <c>[13]</c>
/// alwaysCount; <c>[14]</c> cellCount (= dimX·dimY·dimZ); <c>[15]</c> reserved.</description></item>
/// <item><description><b>cellStart</b> (<c>cellCount + 1</c> uints @ <c>cellStartWord</c>): the CSR exclusive
/// prefix-sums — cell <c>c</c>'s entries are <c>entries[cellStart[c] .. cellStart[c+1])</c>; <c>cellStart[cellCount]</c>
/// is the entry total.</description></item>
/// <item><description><b>entries</b> (<c>entryTotal</c> uints @ <c>entryWord</c>): instance indices, grouped by cell,
/// ascending within a cell — the beam recovers each instance's bound (and, if a future consumer wants them, its
/// segment range) from the instance directory by this index.</description></item>
/// <item><description><b>alwaysList</b> (<c>alwaysCount</c> uints @ <c>alwaysWord</c>): the always-tested instance
/// indices, ascending.</description></item>
/// </list>
/// A DISABLED block is the 16-uint header alone (enabled = 0): the beam then falls back to the flat per-instance loop
/// over EVERY instance (the pre-grid path), which is what makes a zero-binnable or single-cell program correct with no
/// special case.</summary>
internal static class SdfInstanceGrid {
    /// <summary>The header length in uints (4 <c>uint4</c> rows). KEEP IN SYNC with <c>SDF_GRID_HEADER_WORDS</c> in
    /// Assets/Shaders/Sdf/sdf-vm.hlsli.</summary>
    internal const int HeaderWords = 16;
    /// <summary>Target cell edge in median binned-bound DIAMETERS (the 2–4× band the bench notes call for; 3× is the
    /// middle). DERIVED from the median, never hand-picked per scene — the median is robust to a few outliers, and a
    /// too-fine derivation is coarsened by <see cref="CellCapacityFactor"/> / <see cref="MaxDimension"/>.</summary>
    private const float CellDiameterFactor = 3.0f;
    /// <summary>The grid-resolution ceiling: <c>cellCount ≤ CellCapacityFactor · maxInstances</c>, so the frozen word
    /// envelope stays O(maxInstances) (a probe grows it automatically through the same Build). The derivation coarsens
    /// the cell size until the resolution fits. Worst-case entry total is bounded the same way: an instance is scattered
    /// into at most <see cref="MaxCellSpanPerInstance"/> cells, so entries ≤ that × binnable ≤ that × maxInstances.</summary>
    private const int CellCapacityFactor = 4;
    /// <summary>The per-AXIS cell-count cap. Bounds the cone-march SLAB count (≤ √3·MaxDimension), so a degenerate
    /// near-1-D instance layout cannot make the beam walk thousands of slabs. Coarsening enforces it alongside
    /// <see cref="CellCapacityFactor"/>. KEEP IN SYNC with <c>SDF_GRID_MAX_DIM</c> in sdf-vm.hlsli.</summary>
    private const int MaxDimension = 64;
    /// <summary>The per-instance cell-span cap: a binnable instance whose AABB covers more than this many cells is moved
    /// to the always-tested list instead of scattered (its bound is large relative to the cell, so it would occupy a big
    /// fraction of cells anyway, and testing it unconditionally is cheaper than fattening the CSR). 27 = a 3×3×3 span,
    /// so a bound up to ~1.5 cells in radius still bins; only genuine outliers (a bound several × the median) overflow.</summary>
    private const int MaxCellSpanPerInstance = 27;
    /// <summary>The smallest cell edge the derivation admits, so an all-coincident binnable set (zero extent) cannot
    /// produce a zero or denormal cell size.</summary>
    private const float MinCellSize = 1.0e-4f;

    /// <summary>Builds the packed grid block for a program's instances. Deterministic: pure integer/float host math in
    /// instance-index order, no wall-clock, no RNG.</summary>
    /// <param name="instances">One <see cref="SdfInstanceGridInput"/> per program instance, in declaration (index) order.</param>
    /// <param name="maxInstances">The instance ceiling (<see cref="SdfProgramBuilder.MaxInstances"/>) — the resolution
    /// cap is measured against it so the envelope is a real ceiling.</param>
    /// <param name="enabled">When <see langword="false"/>, return a DISABLED block regardless of the instances, so the
    /// beam falls back to the flat per-instance loop — the reference path the grid-cull gate compares against.</param>
    /// <returns>The grid block as a <see cref="uint"/> array whose length is a multiple of 4 (padded), ready to copy into
    /// the program word stream after the world-segment list.</returns>
    internal static uint[] Build(IReadOnlyList<SdfInstanceGridInput> instances, int maxInstances, bool enabled = true) {
        ArgumentNullException.ThrowIfNull(instances);

        if (!enabled) {
            return DisabledBlock();
        }

        // Pass 0: partition the instances. A binnable candidate carries its center/radius; everything else is either an
        // always-list member (active but dynamic/unmaskable) or excluded (parked, negative radius).
        var binnableIndices = new List<int>();
        var minBounds = new Vector3(float.PositiveInfinity);
        var maxBounds = new Vector3(float.NegativeInfinity);

        for (var index = 0; (index < instances.Count); index++) {
            var input = instances[index];

            if (!input.Binnable) {
                continue;
            }

            binnableIndices.Add(item: index);

            var radius = new Vector3(input.Radius);

            minBounds = Vector3.Min(minBounds, (input.Center - radius));
            maxBounds = Vector3.Max(maxBounds, (input.Center + radius));
        }

        // No binnable instance ⇒ a DISABLED block: the beam flat-fallbacks over every instance, so a dynamic-only or
        // flat program keeps working with no cells at all.
        if (binnableIndices.Count == 0) {
            return DisabledBlock();
        }

        var origin = minBounds;
        var extent = Vector3.Max((maxBounds - minBounds), Vector3.Zero);
        var cellSize = DeriveCellSize(instances: instances, binnableIndices: binnableIndices, extent: extent, maxInstances: maxInstances, dimensions: out var dimensions);

        // A single cell buys no culling (every binnable instance would test every tile anyway) ⇒ fall back to the flat
        // loop, which is cheaper than a one-cell walk plus the always-list.
        if ((dimensions.X * dimensions.Y * dimensions.Z) <= 1) {
            return DisabledBlock();
        }

        var invCellSize = (1.0f / cellSize);
        var cellCount = (dimensions.X * dimensions.Y * dimensions.Z);

        // Pass 1: classify each binnable candidate as SCATTERED (its cell-AABB fits the span cap) or OVERFLOW (too many
        // cells ⇒ always-list), remembering the cell-AABB of the scattered ones. Overflow + the non-binnable actives
        // form the always-list, kept in ascending instance-index order.
        var cellLow = new (int X, int Y, int Z)[binnableIndices.Count];
        var cellHigh = new (int X, int Y, int Z)[binnableIndices.Count];
        var scattered = new bool[binnableIndices.Count];
        var overflow = new List<int>();
        var maxBinnedRadius = 0.0f;

        for (var slot = 0; (slot < binnableIndices.Count); slot++) {
            var input = instances[binnableIndices[slot]];
            var low = CellOf(point: (input.Center - new Vector3(input.Radius)), origin: origin, invCellSize: invCellSize, dimensions: dimensions);
            var high = CellOf(point: (input.Center + new Vector3(input.Radius)), origin: origin, invCellSize: invCellSize, dimensions: dimensions);
            var span = (((high.X - low.X) + 1) * ((high.Y - low.Y) + 1) * ((high.Z - low.Z) + 1));

            cellLow[slot] = low;
            cellHigh[slot] = high;

            if (span > MaxCellSpanPerInstance) {
                scattered[slot] = false;
                overflow.Add(item: binnableIndices[slot]);
            } else {
                scattered[slot] = true;
                maxBinnedRadius = MathF.Max(maxBinnedRadius, input.Radius);
            }
        }

        // Pass 2: COUNT per cell.
        var cellStart = new int[cellCount + 1];

        for (var slot = 0; (slot < binnableIndices.Count); slot++) {
            if (!scattered[slot]) {
                continue;
            }

            AddSpanCounts(cellStart: cellStart, low: cellLow[slot], high: cellHigh[slot], dimensions: dimensions);
        }

        // Exclusive prefix-sum: cellStart[c] becomes the first entry index of cell c; cellStart[cellCount] the total.
        var running = 0;

        for (var cell = 0; (cell <= cellCount); cell++) {
            var count = cellStart[cell];

            cellStart[cell] = running;
            running += count;
        }

        var entryTotal = running;

        // Pass 3: SCATTER in instance-index order (binnableIndices is already ascending), so a cell's entries ascend by
        // instance index — deterministic by construction. A moving cursor per cell advances from its prefix-sum start.
        var entries = new int[entryTotal];
        var cursor = new int[cellCount];

        Array.Copy(sourceArray: cellStart, destinationArray: cursor, length: cellCount);

        for (var slot = 0; (slot < binnableIndices.Count); slot++) {
            if (!scattered[slot]) {
                continue;
            }

            ScatterSpan(entries: entries, cursor: cursor, instanceIndex: binnableIndices[slot], low: cellLow[slot], high: cellHigh[slot], dimensions: dimensions);
        }

        // The always-list: every ACTIVE non-binnable instance (dynamic or unmaskable — radius ≥ 0) plus the overflow,
        // merged in ascending instance-index order. Parked instances (radius < 0) are omitted entirely — a parked pool
        // costs zero. The two sources are each ascending, so a merge keeps the whole list ascending.
        var alwaysList = BuildAlwaysList(instances: instances, overflow: overflow);

        return Pack(dimensions: dimensions, origin: origin, invCellSize: invCellSize, cellSize: cellSize, footprintPad: maxBinnedRadius, cellStart: cellStart, entries: entries, alwaysList: alwaysList);
    }

    // The active non-binnable instances (dynamic/unmaskable, radius >= 0) merged with the overflow list, both ascending
    // by instance index, into one ascending always-tested list.
    private static List<int> BuildAlwaysList(IReadOnlyList<SdfInstanceGridInput> instances, List<int> overflow) {
        var always = new List<int>(capacity: overflow.Count);
        var overflowCursor = 0;

        for (var index = 0; (index < instances.Count); index++) {
            while ((overflowCursor < overflow.Count) && (overflow[overflowCursor] < index)) {
                always.Add(item: overflow[overflowCursor]);
                overflowCursor++;
            }

            if ((overflowCursor < overflow.Count) && (overflow[overflowCursor] == index)) {
                always.Add(item: overflow[overflowCursor]);
                overflowCursor++;

                continue; // an overflowed binnable is already recorded; do not also test its Binnable flag below
            }

            var input = instances[index];

            // Active (radius >= 0) AND non-binnable ⇒ a dynamic or unmaskable instance the frozen grid cannot bin: it
            // must be tested every tile, exactly as the flat loop tested it. A parked slot (radius < 0) contributes
            // nothing and is skipped. A binnable instance is in the cells (or already handled as overflow above).
            if (!input.Binnable && (input.Radius >= 0.0f)) {
                always.Add(item: index);
            }
        }

        while (overflowCursor < overflow.Count) {
            always.Add(item: overflow[overflowCursor]);
            overflowCursor++;
        }

        return always;
    }

    // Derives the cell edge from the median binned-bound diameter, then coarsens until the resolution honors both the
    // total-cell cap and the per-axis cap. Returns the cell size and (via out) the resulting per-axis cell counts.
    private static float DeriveCellSize(IReadOnlyList<SdfInstanceGridInput> instances, List<int> binnableIndices, Vector3 extent, int maxInstances, out (int X, int Y, int Z) dimensions) {
        var diameters = new float[binnableIndices.Count];

        for (var slot = 0; (slot < binnableIndices.Count); slot++) {
            diameters[slot] = (2.0f * MathF.Max(instances[binnableIndices[slot]].Radius, 0.0f));
        }

        Array.Sort(array: diameters);

        var medianDiameter = diameters[(diameters.Length / 2)];
        var cellSize = MathF.Max((CellDiameterFactor * medianDiameter), MinCellSize);
        var maxCells = (CellCapacityFactor * maxInstances);

        dimensions = DimensionsFor(extent: extent, cellSize: cellSize);

        // Coarsen (grow the cell size) until the resolution fits both caps. Each step at least doubles the cell size, so
        // the loop terminates quickly (the extent is finite and dims fall toward 1).
        while (
            (((long)dimensions.X * dimensions.Y * dimensions.Z) > maxCells) ||
            (dimensions.X > MaxDimension) ||
            (dimensions.Y > MaxDimension) ||
            (dimensions.Z > MaxDimension)
        ) {
            cellSize *= 2.0f;
            dimensions = DimensionsFor(extent: extent, cellSize: cellSize);
        }

        return cellSize;
    }

    private static (int X, int Y, int Z) DimensionsFor(Vector3 extent, float cellSize) {
        return (
            Math.Max(1, (int)MathF.Ceiling(extent.X / cellSize)),
            Math.Max(1, (int)MathF.Ceiling(extent.Y / cellSize)),
            Math.Max(1, (int)MathF.Ceiling(extent.Z / cellSize))
        );
    }

    // The cell coordinate of a world point, clamped into the grid (floor of the scaled offset; KEEP IN SYNC with the
    // shader's identical mapping in collectInstanceGridMask — the host bins by this, the beam rasterizes the cone by it).
    private static (int X, int Y, int Z) CellOf(Vector3 point, Vector3 origin, float invCellSize, (int X, int Y, int Z) dimensions) {
        return (
            Math.Clamp((int)MathF.Floor((point.X - origin.X) * invCellSize), 0, (dimensions.X - 1)),
            Math.Clamp((int)MathF.Floor((point.Y - origin.Y) * invCellSize), 0, (dimensions.Y - 1)),
            Math.Clamp((int)MathF.Floor((point.Z - origin.Z) * invCellSize), 0, (dimensions.Z - 1))
        );
    }

    private static int CellIndex(int x, int y, int z, (int X, int Y, int Z) dimensions) {
        return (((z * dimensions.Y) + y) * dimensions.X) + x;
    }

    private static void AddSpanCounts(int[] cellStart, (int X, int Y, int Z) low, (int X, int Y, int Z) high, (int X, int Y, int Z) dimensions) {
        for (var z = low.Z; (z <= high.Z); z++) {
            for (var y = low.Y; (y <= high.Y); y++) {
                for (var x = low.X; (x <= high.X); x++) {
                    cellStart[CellIndex(x: x, y: y, z: z, dimensions: dimensions)]++;
                }
            }
        }
    }

    private static void ScatterSpan(int[] entries, int[] cursor, int instanceIndex, (int X, int Y, int Z) low, (int X, int Y, int Z) high, (int X, int Y, int Z) dimensions) {
        for (var z = low.Z; (z <= high.Z); z++) {
            for (var y = low.Y; (y <= high.Y); y++) {
                for (var x = low.X; (x <= high.X); x++) {
                    var cell = CellIndex(x: x, y: y, z: z, dimensions: dimensions);

                    entries[cursor[cell]] = instanceIndex;
                    cursor[cell]++;
                }
            }
        }
    }

    private static uint[] DisabledBlock() {
        // enabled = 0; every other lane 0. The beam reads [0] == 0 and takes the flat per-instance fallback.
        return new uint[HeaderWords];
    }

    private static uint[] Pack((int X, int Y, int Z) dimensions, Vector3 origin, float invCellSize, float cellSize, float footprintPad, int[] cellStart, int[] entries, List<int> alwaysList) {
        var cellStartWord = HeaderWords;
        var entryWord = (cellStartWord + cellStart.Length);
        var alwaysWord = (entryWord + entries.Length);
        var rawLength = (alwaysWord + alwaysList.Count);
        var paddedLength = ((rawLength + 3) & ~3); // round up to a uint4 boundary so the containing stream stays aligned
        var block = new uint[paddedLength];

        block[0] = 1u; // enabled
        block[1] = (uint)dimensions.X;
        block[2] = (uint)dimensions.Y;
        block[3] = (uint)dimensions.Z;
        block[4] = BitConverter.SingleToUInt32Bits(value: origin.X);
        block[5] = BitConverter.SingleToUInt32Bits(value: origin.Y);
        block[6] = BitConverter.SingleToUInt32Bits(value: origin.Z);
        block[7] = BitConverter.SingleToUInt32Bits(value: invCellSize);
        block[8] = BitConverter.SingleToUInt32Bits(value: cellSize);
        block[9] = BitConverter.SingleToUInt32Bits(value: footprintPad);
        block[10] = (uint)cellStartWord;
        block[11] = (uint)entryWord;
        block[12] = (uint)alwaysWord;
        block[13] = (uint)alwaysList.Count;
        block[14] = (uint)(dimensions.X * dimensions.Y * dimensions.Z);
        block[15] = 0u;

        for (var index = 0; (index < cellStart.Length); index++) {
            block[cellStartWord + index] = (uint)cellStart[index];
        }

        for (var index = 0; (index < entries.Length); index++) {
            block[entryWord + index] = (uint)entries[index];
        }

        for (var index = 0; (index < alwaysList.Count); index++) {
            block[alwaysWord + index] = (uint)alwaysList[index];
        }

        return block;
    }
}
