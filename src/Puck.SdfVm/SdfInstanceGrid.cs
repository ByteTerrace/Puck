using System.Numerics;

namespace Puck.SdfVm;

/// <summary>One instance's world-space cull footprint, as the grid packer sees it (produced by
/// <see cref="SdfProgram"/> from the same packed radius <c>PackInstances</c> writes, so the grid bins EXACTLY the
/// spheres the beam later tests).</summary>
/// <param name="Center">The instance's world-space bound center. At program pack time this is the static center; the
/// ring-local frame builder resolves dynamic centers before binning.</param>
/// <param name="Radius">The packed bound radius: the float-safety-padded radius for a live instance, the
/// <see cref="SdfProgram.UnmaskableBoundRadius"/> sentinel for an unmaskable one, or negative for a PARKED slot.</param>
/// <param name="Binnable">Whether this instance has a fixed, finite world-space bound the program-time grid can bin:
/// active, static, and maskable.</param>
/// <param name="FrameBinnable">Whether the per-frame grid may bin it after resolving a dynamic center: active and
/// maskable. Dynamic instances are false for <paramref name="Binnable"/> but true here; unmaskable and parked instances
/// remain false and stay in the always-list / excluded respectively.</param>
internal readonly record struct SdfInstanceGridInput(Vector3 Center, float Radius, bool Binnable, bool FrameBinnable);

/// <summary>Builds the deterministic world-space uniform grid the tile-cull beam prepass walks instead of testing every
/// instance in every tile. Static, maskable instances are
/// counting-sorted BY CENTER into a CSR cell directory — exactly ONE cell per instance (count → exclusive prefix-sum →
/// scatter, all in instance-index order — deterministic by construction, no atomics, no wave intrinsics). The immutable
/// program block keeps dynamic and unmaskable instances in an always-tested list; the live frame-grid rebuild resolves
/// and bins maskable dynamic instances, leaving only unmaskable instances always-tested. The beam then tests only the
/// instances in the grid cells its cone footprint overlaps plus the always-list, so beam cost tracks instances NEAR
/// the tile's cone, not the total. The immutable program block provides the construction/reference representation;
/// live rendering rebuilds the same layout in a reusable ring-local workspace only when an active maskable dynamic
/// instance can move. Otherwise program upload seeds the invariant table into every ring slot once.
/// <para>BIN-BY-CENTER is the load-bearing pairing with the header's <c>footprintPad</c>: because an instance occupies
/// only its center's cell, the beam's query AABB must be inflated by the LARGEST binned bound radius (footprintPad =
/// max binned radius) — an instance whose bound touches the cone at a point q has its center within that radius of q,
/// so the padded query reaches the center's cell. The pad is CORRECTNESS, not slop: shrinking it below the max binned
/// radius holes the mask. The rejected alternative (scatter each instance into every covered cell + pad anyway)
/// duplicated entries and re-tested each instance from many neighboring cells — measured as a net beam REGRESSION at
/// every bench rung.</para>
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
/// (host-baked 1/cellSize — the shader never divides by the cell edge); <c>[8]</c> cellSize (world edge, the beam's
/// slab-march step unit); <c>[9]</c> footprintPad (the max binned bound radius + the float-safety epsilon — the world
/// margin every cone-footprint query MUST add; see the bin-by-center note above); <c>[10]</c> cellStartWord;
/// <c>[11]</c> entryWord; <c>[12]</c> alwaysWord (all uint offsets from the block start); <c>[13]</c> alwaysCount;
/// <c>[14]</c> cellCount (= dimX·dimY·dimZ); <c>[15]</c> reserved.</description></item>
/// <item><description><b>cellStart</b> (<c>cellCount + 1</c> uints @ <c>cellStartWord</c>): the CSR exclusive
/// prefix-sums — cell <c>c</c>'s entries are <c>entries[cellStart[c] .. cellStart[c+1])</c>; <c>cellStart[cellCount]</c>
/// is the entry total.</description></item>
/// <item><description><b>entries</b> (one uint per BINNED instance @ <c>entryWord</c>): instance indices, grouped by
/// cell, ascending within a cell — the beam recovers each instance's bound (and, if a future consumer wants them, its
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
    /// envelope stays O(maxInstances) (a probe grows it automatically through the same Build). The entry total is
    /// exactly the binned instance count (bin-by-center — one cell per instance), so the whole block is bounded by
    /// HeaderWords + (CellCapacityFactor + 1) × maxInstances + binned + always ≤ HeaderWords +
    /// (CellCapacityFactor + 2) × maxInstances uints. The derivation coarsens the cell size until the resolution fits.</summary>
    private const int CellCapacityFactor = 4;
    /// <summary>The per-AXIS cell-count cap. Bounds the beam's slab-march length (the ray∩grid interval spans at most
    /// ~√3·MaxDimension cells), so a degenerate near-1-D instance layout cannot make the beam walk thousands of slabs.
    /// Coarsening enforces it alongside <see cref="CellCapacityFactor"/>. KEEP IN SYNC with the slab-budget reasoning at
    /// <c>SDF_GRID_MAX_SLABS</c> in sdf-vm.hlsli.</summary>
    private const int MaxDimension = 64;
    /// <summary>The float-safety epsilon folded into the packed <c>footprintPad</c>, in CELL EDGES: the host bins a
    /// center by <c>floor((center − origin) · invCellSize)</c> and the beam rasterizes its query AABB through the same
    /// mapping — a center within a few ulps of a cell wall could land on either side, so the query is padded strictly
    /// past every rounding disagreement. 1e-3 of a cell edge dwarfs the ~1e-6 relative float error while staying
    /// cost-wise negligible and avoids a whole-cell over-cover ring that can multiply the walked cells by up to 27×.</summary>
    private const float FootprintEpsilonCells = 1.0e-3f;
    /// <summary>The smallest cell edge the derivation admits, so an all-coincident binnable set (zero extent) cannot
    /// produce a zero or denormal cell size.</summary>
    private const float MinCellSize = 1.0e-4f;

    /// <summary>The largest packed block <see cref="Build"/> can produce for an instance envelope. The frame-local
    /// grid buffers use this exact ceiling: header + at most 4N cells' N+1 prefix entries + N binned entries + N
    /// always-list entries, rounded to a uint4 boundary.</summary>
    internal static int WordCapacity(int maxInstances) {
        var count = Math.Max(val1: 0, val2: maxInstances);
        var rawLength = (HeaderWords + 1 + ((CellCapacityFactor + 2) * count));

        return ((rawLength + 3) & ~3);
    }

    /// <summary>Allocation-free scratch for rebuilding the packed grid when a live program has active maskable dynamic
    /// instances (or once at program upload for an invariant table). One workspace belongs to one engine and is reused
    /// only after that engine's frame-slot fence retires; no array or list escapes a build.</summary>
    internal sealed class Workspace {
        private readonly int[] m_alwaysList;
        private readonly int[] m_binnableIndices;
        private readonly int[] m_cellStart;
        private readonly int[] m_cursor;
        private readonly float[] m_diameters;
        private readonly int[] m_entries;
        private readonly int[] m_homeCell;
        private readonly int m_maxInstances;
        private readonly uint[] m_words;

        internal Workspace(int maxInstances) {
            m_maxInstances = Math.Max(val1: 0, val2: maxInstances);
            var instanceScratchLength = Math.Max(val1: 1, val2: m_maxInstances);
            var cellScratchLength = Math.Max(val1: 1, val2: (CellCapacityFactor * m_maxInstances));

            m_alwaysList = new int[instanceScratchLength];
            m_binnableIndices = new int[instanceScratchLength];
            m_cellStart = new int[(cellScratchLength + 1)];
            m_cursor = new int[cellScratchLength];
            m_diameters = new float[instanceScratchLength];
            m_entries = new int[instanceScratchLength];
            m_homeCell = new int[instanceScratchLength];
            m_words = new uint[WordCapacity(maxInstances: m_maxInstances)];
        }

        /// <summary>Rebuilds the same packed block as <see cref="SdfInstanceGrid.Build"/> into owned scratch. The
        /// returned span remains valid until this workspace's next build.</summary>
        internal ReadOnlySpan<uint> Build(ReadOnlySpan<SdfInstanceGridInput> instances, bool enabled) {
            if (instances.Length > m_maxInstances) {
                throw new ArgumentException(message: $"The frame grid has {instances.Length} instances; its workspace was sized for {m_maxInstances}.", paramName: nameof(instances));
            }

            if (!enabled) {
                return DisabledWords();
            }

            var binnableCount = 0;
            var minBounds = new Vector3(value: float.PositiveInfinity);
            var maxBounds = new Vector3(value: float.NegativeInfinity);
            var maxBinnedRadius = 0.0f;

            for (var index = 0; (index < instances.Length); index++) {
                var input = instances[index];

                if (!input.Binnable) {
                    continue;
                }

                m_binnableIndices[binnableCount] = index;
                m_diameters[binnableCount] = (2.0f * MathF.Max(x: input.Radius, y: 0.0f));
                binnableCount++;
                maxBinnedRadius = MathF.Max(x: maxBinnedRadius, y: input.Radius);

                var radius = new Vector3(value: input.Radius);

                minBounds = Vector3.Min(value1: minBounds, value2: (input.Center - radius));
                maxBounds = Vector3.Max(value1: maxBounds, value2: (input.Center + radius));
            }

            if (binnableCount == 0) {
                return DisabledWords();
            }

            var origin = minBounds;
            var extent = Vector3.Max(value1: (maxBounds - minBounds), value2: Vector3.Zero);
            var diameters = m_diameters.AsSpan(start: 0, length: binnableCount);

            diameters.Sort();

            var cellSize = MathF.Max(x: (CellDiameterFactor * diameters[(binnableCount / 2)]), y: MinCellSize);
            var maxCells = (CellCapacityFactor * m_maxInstances);
            var dimensions = DimensionsFor(extent: extent, cellSize: cellSize);

            while (
                ((((long)dimensions.X * dimensions.Y) * dimensions.Z) > maxCells) ||
                (dimensions.X > MaxDimension) ||
                (dimensions.Y > MaxDimension) ||
                (dimensions.Z > MaxDimension)
            ) {
                cellSize *= 2.0f;
                dimensions = DimensionsFor(extent: extent, cellSize: cellSize);
            }

            var cellCount = ((dimensions.X * dimensions.Y) * dimensions.Z);

            if (cellCount <= 1) {
                return DisabledWords();
            }

            var invCellSize = (1.0f / cellSize);
            var cellStart = m_cellStart.AsSpan(start: 0, length: (cellCount + 1));

            cellStart.Clear();

            for (var slot = 0; (slot < binnableCount); slot++) {
                var input = instances[m_binnableIndices[slot]];
                var cell = CellOf(point: input.Center, origin: origin, invCellSize: invCellSize, dimensions: dimensions);

                m_homeCell[slot] = cell;
                cellStart[cell]++;
            }

            var running = 0;

            for (var cell = 0; (cell <= cellCount); cell++) {
                var count = cellStart[cell];

                cellStart[cell] = running;
                running += count;
            }

            cellStart[..cellCount].CopyTo(destination: m_cursor);

            for (var slot = 0; (slot < binnableCount); slot++) {
                var cell = m_homeCell[slot];

                m_entries[m_cursor[cell]] = m_binnableIndices[slot];
                m_cursor[cell]++;
            }

            var alwaysCount = 0;

            for (var index = 0; (index < instances.Length); index++) {
                var input = instances[index];

                if (!input.Binnable && (input.Radius >= 0.0f)) {
                    m_alwaysList[alwaysCount] = index;
                    alwaysCount++;
                }
            }

            var cellStartWord = HeaderWords;
            var entryWord = (cellStartWord + cellCount + 1);
            var alwaysWord = (entryWord + running);
            var rawLength = (alwaysWord + alwaysCount);
            var paddedLength = ((rawLength + 3) & ~3);
            var words = m_words.AsSpan(start: 0, length: paddedLength);

            words.Clear();
            words[0] = 1u;
            words[1] = (uint)dimensions.X;
            words[2] = (uint)dimensions.Y;
            words[3] = (uint)dimensions.Z;
            words[4] = BitConverter.SingleToUInt32Bits(value: origin.X);
            words[5] = BitConverter.SingleToUInt32Bits(value: origin.Y);
            words[6] = BitConverter.SingleToUInt32Bits(value: origin.Z);
            words[7] = BitConverter.SingleToUInt32Bits(value: invCellSize);
            words[8] = BitConverter.SingleToUInt32Bits(value: cellSize);
            words[9] = BitConverter.SingleToUInt32Bits(value: (maxBinnedRadius + (FootprintEpsilonCells * cellSize)));
            words[10] = (uint)cellStartWord;
            words[11] = (uint)entryWord;
            words[12] = (uint)alwaysWord;
            words[13] = (uint)alwaysCount;
            words[14] = (uint)cellCount;

            for (var index = 0; (index <= cellCount); index++) {
                words[(cellStartWord + index)] = (uint)cellStart[index];
            }

            for (var index = 0; (index < running); index++) {
                words[(entryWord + index)] = (uint)m_entries[index];
            }

            for (var index = 0; (index < alwaysCount); index++) {
                words[(alwaysWord + index)] = (uint)m_alwaysList[index];
            }

            return words;
        }

        private ReadOnlySpan<uint> DisabledWords() {
            var words = m_words.AsSpan(start: 0, length: HeaderWords);

            words.Clear();

            return words;
        }
    }

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
        // always-list member (active but dynamic/unmaskable) or excluded (parked, negative radius). The grid AABB covers
        // center ± radius, so every binnable CENTER is strictly inside it by construction.
        var binnableIndices = new List<int>();
        var minBounds = new Vector3(value: float.PositiveInfinity);
        var maxBounds = new Vector3(value: float.NegativeInfinity);
        var maxBinnedRadius = 0.0f;

        for (var index = 0; (index < instances.Count); index++) {
            var input = instances[index];

            if (!input.Binnable) {
                continue;
            }

            binnableIndices.Add(item: index);
            maxBinnedRadius = MathF.Max(x: maxBinnedRadius, y: input.Radius);

            var radius = new Vector3(value: input.Radius);

            minBounds = Vector3.Min(value1: minBounds, value2: (input.Center - radius));
            maxBounds = Vector3.Max(value1: maxBounds, value2: (input.Center + radius));
        }

        // No binnable instance ⇒ a DISABLED block: the beam flat-fallbacks over every instance, so a dynamic-only or
        // flat program keeps working with no cells at all.
        if (binnableIndices.Count == 0) {
            return DisabledBlock();
        }

        var origin = minBounds;
        var extent = Vector3.Max(value1: (maxBounds - minBounds), value2: Vector3.Zero);
        var cellSize = DeriveCellSize(instances: instances, binnableIndices: binnableIndices, extent: extent, maxInstances: maxInstances, dimensions: out var dimensions);

        // A single cell buys no culling (every binnable instance would test every tile anyway) ⇒ fall back to the flat
        // loop, which is cheaper than a one-cell walk plus the always-list.
        if (((dimensions.X * dimensions.Y) * dimensions.Z) <= 1) {
            return DisabledBlock();
        }

        var invCellSize = (1.0f / cellSize);
        var cellCount = ((dimensions.X * dimensions.Y) * dimensions.Z);

        // Pass 1: COUNT per cell — each binnable instance in exactly its CENTER's cell (see the bin-by-center note in
        // the type doc: the beam's footprintPad-inflated query is what reaches a bound whose center sits in a
        // neighboring cell).
        var cellStart = new int[(cellCount + 1)];
        var homeCell = new int[binnableIndices.Count];

        for (var slot = 0; (slot < binnableIndices.Count); slot++) {
            var input = instances[binnableIndices[slot]];
            var cell = CellOf(point: input.Center, origin: origin, invCellSize: invCellSize, dimensions: dimensions);

            homeCell[slot] = cell;
            cellStart[cell]++;
        }

        // Exclusive prefix-sum: cellStart[c] becomes the first entry index of cell c; cellStart[cellCount] the total.
        var running = 0;

        for (var cell = 0; (cell <= cellCount); cell++) {
            var count = cellStart[cell];

            cellStart[cell] = running;
            running += count;
        }

        // Pass 2: SCATTER in instance-index order (binnableIndices is already ascending), so a cell's entries ascend by
        // instance index — deterministic by construction. A moving cursor per cell advances from its prefix-sum start.
        var entries = new int[running];
        var cursor = new int[cellCount];

        Array.Copy(sourceArray: cellStart, destinationArray: cursor, length: cellCount);

        for (var slot = 0; (slot < binnableIndices.Count); slot++) {
            var cell = homeCell[slot];

            entries[cursor[cell]] = binnableIndices[slot];
            cursor[cell]++;
        }

        // The always-list: every ACTIVE non-binnable instance (dynamic or unmaskable — radius ≥ 0), in ascending
        // instance-index order (the walk order). Parked instances (radius < 0) are omitted entirely — a parked pool
        // costs zero.
        var alwaysList = new List<int>();

        for (var index = 0; (index < instances.Count); index++) {
            var input = instances[index];

            if (!input.Binnable && (input.Radius >= 0.0f)) {
                alwaysList.Add(item: index);
            }
        }

        // footprintPad = the max binned radius (load-bearing — see the type doc) + the float-safety epsilon that keeps
        // the beam's floor() of a padded query AABB corner from disagreeing with the host's floor() of a center by a cell.
        var footprintPad = (maxBinnedRadius + (FootprintEpsilonCells * cellSize));

        return Pack(dimensions: dimensions, origin: origin, invCellSize: invCellSize, cellSize: cellSize, footprintPad: footprintPad, cellStart: cellStart, entries: entries, alwaysList: alwaysList);
    }

    // Derives the cell edge from the median binned-bound diameter, then coarsens until the resolution honors both the
    // total-cell cap and the per-axis cap. Returns the cell size and (via out) the resulting per-axis cell counts.
    private static float DeriveCellSize(IReadOnlyList<SdfInstanceGridInput> instances, List<int> binnableIndices, Vector3 extent, int maxInstances, out (int X, int Y, int Z) dimensions) {
        var diameters = new float[binnableIndices.Count];

        for (var slot = 0; (slot < binnableIndices.Count); slot++) {
            diameters[slot] = (2.0f * MathF.Max(x: instances[binnableIndices[slot]].Radius, y: 0.0f));
        }

        Array.Sort(array: diameters);

        var medianDiameter = diameters[(diameters.Length / 2)];
        var cellSize = MathF.Max(x: (CellDiameterFactor * medianDiameter), y: MinCellSize);
        var maxCells = (CellCapacityFactor * maxInstances);

        dimensions = DimensionsFor(extent: extent, cellSize: cellSize);

        // Coarsen (grow the cell size) until the resolution fits both caps. Each step doubles the cell size, so the
        // loop terminates quickly (the extent is finite and dims fall toward 1).
        while (
            ((((long)dimensions.X * dimensions.Y) * dimensions.Z) > maxCells) ||
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
            Math.Max(val1: 1, val2: (int)MathF.Ceiling(x: (extent.X / cellSize))),
            Math.Max(val1: 1, val2: (int)MathF.Ceiling(x: (extent.Y / cellSize))),
            Math.Max(val1: 1, val2: (int)MathF.Ceiling(x: (extent.Z / cellSize)))
        );
    }

    // The cell index of a world point, clamped into the grid (floor of the scaled offset; KEEP IN SYNC with the
    // shader's identical mapping in collectInstanceGridMask — the host bins by this, the beam rasterizes its padded
    // cone footprint by it).
    private static int CellOf(Vector3 point, Vector3 origin, float invCellSize, (int X, int Y, int Z) dimensions) {
        var x = Math.Clamp((int)MathF.Floor(x: ((point.X - origin.X) * invCellSize)), 0, (dimensions.X - 1));
        var y = Math.Clamp((int)MathF.Floor(x: ((point.Y - origin.Y) * invCellSize)), 0, (dimensions.Y - 1));
        var z = Math.Clamp((int)MathF.Floor(x: ((point.Z - origin.Z) * invCellSize)), 0, (dimensions.Z - 1));

        return ((((z * dimensions.Y) + y) * dimensions.X) + x);
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
        var paddedLength = (rawLength + 3) & ~3; // round up to a uint4 boundary so the containing stream stays aligned
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
        block[14] = (uint)((dimensions.X * dimensions.Y) * dimensions.Z);
        block[15] = 0u;

        for (var index = 0; (index < cellStart.Length); index++) {
            block[(cellStartWord + index)] = (uint)cellStart[index];
        }

        for (var index = 0; (index < entries.Length); index++) {
            block[(entryWord + index)] = (uint)entries[index];
        }

        for (var index = 0; (index < alwaysList.Count); index++) {
            block[(alwaysWord + index)] = (uint)alwaysList[index];
        }

        return block;
    }
}
