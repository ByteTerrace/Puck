using System.Globalization;
using Puck.Abstractions;
using Puck.Maths;

namespace Puck.State;

/// <summary>A compiled immutable adjacency table. Absent neighbours are -1. Direction names come from the
/// topology's own <see cref="IDiscreteLatticeTopology.Directions"/> when authored; the unauthored defaults are
/// Grid N, NE, E, SE, S, SW, W, NW; Hex the six in <see cref="TopologyCompilation.HexDirectionNames"/>; Box the 26
/// in <see cref="BoxDirectionNames"/>; Ring forward and backward.</summary>
public sealed partial class CompiledTopology {
    private readonly int[] m_neighbours;
    private readonly int[] m_opposite;
    private readonly string[] m_keys;
    private readonly CellName[] m_names;
    private readonly string[] m_directionNames;
    private readonly int m_width;
    private readonly int m_depth;
    private readonly int m_radius;
    private readonly int m_layers;
    private readonly FixedQ4816 m_layerHeight;
    private readonly TopologyWrap m_wrap;
    private readonly FixedVector3 m_origin;
    // The board's heading about +Y (a unit rotor, exact at quarter turns), and whether it is anything but the
    // identity: an unturned board resolves positions on the world axes with no rotation at all.
    private readonly FixedComplex m_heading;
    private readonly bool m_turned;
    private readonly FixedQ4816 m_cellSize;
    private readonly FixedQ4816 m_band;
    // Authored centres relative to the origin, present for a Graph or Tiling alone; every other kind derives its centres.
    private readonly FixedVector3[]? m_cellCentres;
    // Each cell's axial coordinate — (x, z, layer) on a grid, ring, or box, (q, r, 0) on a hex — and its inverse, present
    // for the lattice kinds alone: what makes a translation between two cells carriable to a third.
    private readonly (int X, int Y, int Z)[]? m_coordinates;
    private readonly Dictionary<(int, int, int), int>? m_coordinateIndex;
    private readonly ulong[]? m_directionShiftMasks;
    // Per direction, the one signed bit distance every cell's neighbour lies at, and the cells a step lands on; zero
    // where the neighbours lie at different distances (a wrapped edge, a graph).
    private readonly int[]? m_directionSteps;
    private readonly ulong[]? m_directionLandings;

    // Each direction's longest ray, measured on first use; every measurement is the same, so a race only repeats it.
    private int[]? m_longestRays;

    internal CompiledTopology(LatticeTopology source, TopologyKind kind, int count, int directions, int[] neighbours, int[] opposite,
        int width, int depth, int radius, TopologyWrap wrap, FixedVector3 origin, FixedQ4816 cellSize, FixedQ4816 band,
        int[][] images, string[] elementNames, int layers, FixedQ4816 layerHeight, string[] directionNames, FixedVector3[]? cellCentres = null,
        (int X, int Y, int Z)[]? coordinates = null, FixedComplex? heading = null) {
        Source = source;
        m_heading = (heading ?? FixedComplex.MultiplicativeIdentity);
        m_turned = (m_heading != FixedComplex.MultiplicativeIdentity);
        m_cellCentres = cellCentres;
        m_coordinates = coordinates;
        if (coordinates is not null) {
            m_coordinateIndex = new Dictionary<(int, int, int), int>(capacity: coordinates.Length);
            for (var cell = 0; (cell < coordinates.Length); cell++) {
                m_coordinateIndex[coordinates[cell]] = cell;
            }
        }
        m_band = band;
        m_layers = layers;
        m_layerHeight = layerHeight;
        m_images = images;
        m_elementNames = elementNames;
        Kind = kind;
        CellCount = count;
        DirectionCount = directions;
        m_neighbours = neighbours;
        m_opposite = opposite;
        m_directionNames = directionNames;
        m_width = width;
        m_depth = depth;
        m_radius = radius;
        m_wrap = wrap;
        m_origin = origin;
        m_cellSize = cellSize;
        m_keys = new string[count];
        m_names = new CellName[count];
        for (var cell = 0; (cell < count); cell++) {
            m_keys[cell] = cell.ToString(provider: CultureInfo.InvariantCulture);
            m_names[cell] = CellName.Parse(candidate: m_keys[cell]);
        }
        CellWords = BitWords.WordsFor(members: count);
        if (directions > 0) {
            var shiftMasks = ((count <= BoardMask.MaxCells) ? new ulong[(directions * BoardMask.MaxCells)] : null);
            var steps = new int[directions];
            var landings = new ulong[(directions * CellWords)];

            for (var d = 0; (d < directions); d++) {
                var offset = (d * BoardMask.MaxCells);
                var landing = landings.AsSpan(start: (d * CellWords), length: CellWords);
                var step = 0;
                var uniform = true;

                for (var cell = 0; (cell < count); cell++) {
                    var neighbour = neighbours[((cell * directions) + d)];

                    if (neighbour >= 0) {
                        if (shiftMasks is not null) {
                            shiftMasks[(offset + cell)] = (1UL << neighbour);
                        }
                        BitWords.Add(index: neighbour, words: landing);
                        uniform &= ((neighbour != cell) && ((step == 0) || (step == (neighbour - cell))));
                        step = (neighbour - cell);
                    }
                }
                steps[d] = (uniform ? step : 0);
            }
            m_directionShiftMasks = shiftMasks;
            m_directionSteps = steps;
            m_directionLandings = landings;
        }
        if (
            (count <= BoardMask.MaxCells) &&
            (images is { Length: > 0 })
        ) {
            var imageMasks = new ulong[(images.Length * BoardMask.MaxCells)];

            for (var elem = 0; (elem < images.Length); elem++) {
                var offset = (elem * BoardMask.MaxCells);
                var elemImages = images[elem];

                for (var cell = 0; ((cell < count) && (cell < elemImages.Length)); cell++) {
                    var carried = elemImages[cell];

                    if (
                        (carried >= 0) &&
                        (carried < BoardMask.MaxCells)
                    ) {
                        imageMasks[(offset + cell)] = (1UL << carried);
                    }
                }
            }
            m_elementImageMasks = imageMasks;
        }
    }

    /// <summary>Gets a cell's key as a parsed cell name, without re-parsing.</summary>
    /// <param name="cell">The cell ordinal.</param>
    public CellName NameOf(int cell) => m_names[cell];

    /// <summary>Gets the authored topology this compiled from. Two compiles of one authored topology differ only in
    /// the frame an anchor places them in (its world origin and heading), so this is the board's identity across
    /// anchorings and is what a reader compares to decide that a query and a row address the same board.</summary>
    public LatticeTopology Source { get; }
    /// <summary>Gets the shape.</summary>
    public TopologyKind Kind { get; }
    /// <summary>Gets the number of cells.</summary>
    public int CellCount { get; }
    /// <summary>Gets the number of directions at each cell.</summary>
    public int DirectionCount { get; }
    /// <summary>Gets the anchored origin <see cref="CellCentre"/> and <see cref="TryCellOf"/> resolve against, in
    /// world units: a grid or box's minimum corner, a hex's origin-cell lattice point, and the point a graph's or
    /// tiling's authored centres are relative to. The board's own axes turn about it by <see cref="Heading"/>.</summary>
    public FixedVector3 Origin => m_origin;
    /// <summary>Gets the board's heading about +Y: the unit rotation (<see cref="FixedComplex.RotateAboutY"/>) carrying
    /// the board's own axes (columns along its +X, rows along its +Z) onto the world's, its anchor's yaw. Exact at a
    /// whole number of quarter turns (<see cref="FixedComplex.FromAngleCardinal"/>), and the identity for an unanchored
    /// board.</summary>
    public FixedComplex Heading => m_heading;
    /// <summary>Carries an offset in the board's own frame to the world's: the offset turned by <see cref="Heading"/>.
    /// Add <see cref="Origin"/> to place a board point.</summary>
    /// <param name="offset">The offset along the board's own axes, in world units.</param>
    /// <returns>The same offset along the world's axes; exactly <paramref name="offset"/> on an unturned board.</returns>
    public FixedVector3 Turn(in FixedVector3 offset) => (m_turned ? m_heading.RotateAboutY(vector: offset) : offset);
    /// <summary>Carries an offset along the world's axes into the board's own frame, the inverse of
    /// <see cref="Turn"/>.</summary>
    /// <param name="offset">The offset along the world's axes, in world units (a world point less <see cref="Origin"/>).</param>
    /// <returns>The same offset along the board's own axes; exactly <paramref name="offset"/> on an unturned board.</returns>
    public FixedVector3 Unturn(in FixedVector3 offset) => (m_turned ? m_heading.Conjugate().RotateAboutY(vector: offset) : offset);
    /// <summary>Gets the declared cell edge, world units.</summary>
    public FixedQ4816 CellSize => m_cellSize;
    /// <summary>Gets the cell count along +X.</summary>
    public int Width => m_width;
    /// <summary>Gets the cell count along +Z.</summary>
    public int Depth => m_depth;
    /// <summary>Gets the ring count around the origin cell of a hex topology; 0 for every other kind.</summary>
    public int Radius => m_radius;

    // Row spacing of a pointy-top hex lattice in cell units: √3/2. Cell (q, r) sits at origin + cellSize · (q − r/2, 0, r·√3/2),
    // so +q is +X and +r leans toward +Z. KEEP IN SYNC with TryCellOf's inverse.
    private static readonly FixedQ4816 HexRowSpacing = FixedQ4816.FromDouble(value: 0.8660254037844386);
    private static readonly FixedQ4816 Half = FixedQ4816.FromDouble(value: 0.5);

    /// <summary>Returns the centre of a cell in world units: a grid or box cell's square (or cube) centre, a hex cell's
    /// lattice point, placed in the topology's anchored frame (<see cref="Origin"/> plus its
    /// <see cref="LocalCellCentre"/> turned by <see cref="Heading"/>).</summary>
    /// <param name="cell">The cell ordinal.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cell"/> is not a cell of the topology.</exception>
    public FixedVector3 CellCentre(int cell) => (m_origin + Turn(offset: LocalCellCentre(cell: cell)));
    /// <summary>Returns the centre of a cell in the board's own frame: its offset from <see cref="Origin"/> along the
    /// board's axes, columns along +X and rows along +Z, before <see cref="Heading"/> turns it.</summary>
    /// <param name="cell">The cell ordinal.</param>
    /// <returns>The offset, in world units.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cell"/> is not a cell of the topology.</exception>
    public FixedVector3 LocalCellCentre(int cell) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            value: ((uint)cell),
            other: ((uint)CellCount)
        );
        if (m_cellCentres is { } centres) {
            return centres[cell];
        }
        if (Kind == TopologyKind.Hex) {
            var coordinate = new HexagonalIndex(value: cell).ToCoordinate();
            var q = FixedQ4816.FromInteger(value: coordinate.Q);
            var r = FixedQ4816.FromInteger(value: coordinate.R);

            return new FixedVector3(
                X: (m_cellSize * (q - (r * Half))),
                Y: FixedQ4816.Zero,
                Z: (m_cellSize * (r * HexRowSpacing))
            );
        }
        var planar = (cell % (m_width * m_depth));
        var layer = (cell / (m_width * m_depth));

        return new FixedVector3(
            X: (m_cellSize * (FixedQ4816.FromInteger(value: (planar % m_width)) + Half)),
            Y: (m_layerHeight * FixedQ4816.FromInteger(value: layer)),
            Z: (m_cellSize * (FixedQ4816.FromInteger(value: (planar / m_width)) + Half))
        );
    }
    /// <summary>Resolves the cell a world position falls in, read in the board's own frame (the position less
    /// <see cref="Origin"/>, turned back by <see cref="Heading"/>). A <see cref="TopologyKind.Grid"/> resolves X/Z,
    /// within its vertical band when it declares one; a <see cref="TopologyKind.Box"/> also resolves the layer from
    /// Y; a <see cref="TopologyKind.Hex"/> rounds X/Z to the nearest axial coordinate within its radius and band; a
    /// <see cref="TopologyKind.Graph"/> or <see cref="TopologyKind.Tiling"/> takes the nearest authored centre within
    /// half a cell size, the lowest ordinal on a tie. A <see cref="TopologyKind.Ring"/> has no spatial frame and
    /// answers <see langword="false"/>.</summary>
    /// <param name="position">The world position (a body's resolved pose).</param>
    /// <param name="cell">The resolved cell ordinal.</param>
    /// <returns>Whether the position lies over a declared cell.</returns>
    public bool TryCellOf(in FixedVector3 position, out int cell) => TryUnturnedCellOf(
        cell: out cell,
        position: (m_turned ? (m_origin + Unturn(offset: (position - m_origin))) : position)
    );
    // Resolves a position already carried onto the board's own axes, so the origin's offsets are along columns and rows.
    private bool TryUnturnedCellOf(in FixedVector3 position, out int cell) {
        cell = -1;
        if (m_cellCentres is { } centres) {
            return TryNearestCellOf(
                cell: out cell,
                centres: centres,
                position: in position
            );
        }
        if (Kind == TopologyKind.Hex) {
            return TryHexCellOf(
                cell: out cell,
                position: in position
            );
        }
        if (Kind is not (TopologyKind.Grid or TopologyKind.Box)) {
            return false;
        }
        var layer = 0;

        if (Kind == TopologyKind.Box) {
            var localY = (((Int128)position.Y.Value) - m_origin.Y.Value);

            if (localY < Int128.Zero) {
                return false;
            }
            var y = (localY / m_layerHeight.Value);

            if (y >= m_layers) {
                return false;
            }
            layer = ((int)y);
        } else if (m_band > FixedQ4816.Zero) {
            var localY = (((Int128)position.Y.Value) - m_origin.Y.Value);

            if (
                (localY > m_band.Value) ||
                (localY < -((Int128)m_band.Value))
            ) {
                return false;
            }
        }
        var localX = (((Int128)position.X.Value) - m_origin.X.Value);
        var localZ = (((Int128)position.Z.Value) - m_origin.Z.Value);

        if (
            (localX < Int128.Zero) ||
            (localZ < Int128.Zero)
        ) {
            return false;
        }
        var x = (localX / m_cellSize.Value);
        var z = (localZ / m_cellSize.Value);

        if (
            (x >= m_width) ||
            (z >= m_depth)
        ) {
            return false;
        }
        cell = ((((layer * m_depth) + ((int)z)) * m_width) + ((int)x));
        return true;
    }

    /// <summary>The 26 space directions of a <see cref="TopologyKind.Box"/>: the grid's eight compass names in
    /// the layer, then each prefixed <c>U</c> (up one layer) and <c>D</c> (down one), with <c>U</c> and <c>D</c> alone
    /// for the vertical.</summary>
    public static readonly string[] BoxDirectionNames = [
        "N", "NE", "E", "SE", "S", "SW", "W", "NW",
        "U", "UN", "UNE", "UE", "USE", "US", "USW", "UW", "UNW",
        "D", "DN", "DNE", "DE", "DSE", "DS", "DSW", "DW", "DNW",
    ];

    // The nearest authored centre within half a cell size, lowest ordinal on a tie; squared distances in raw Q48.16
    // units cannot overflow an Int128.
    private bool TryNearestCellOf(in FixedVector3 position, FixedVector3[] centres, out int cell) {
        cell = -1;
        var half = ((Int128)(m_cellSize.Value >> 1));
        var best = ((half * half) + Int128.One);

        for (var index = 0; (index < centres.Length); index++) {
            var centre = centres[index];
            var dx = (((Int128)position.X.Value) - (((Int128)m_origin.X.Value) + centre.X.Value));
            var dy = (((Int128)position.Y.Value) - (((Int128)m_origin.Y.Value) + centre.Y.Value));
            var dz = (((Int128)position.Z.Value) - (((Int128)m_origin.Z.Value) + centre.Z.Value));
            var distance = (((dx * dx) + (dy * dy)) + (dz * dz));

            if (distance < best) {
                best = distance;
                cell = index;
            }
        }
        return (cell >= 0);
    }
    private bool TryHexCellOf(in FixedVector3 position, out int cell) {
        cell = -1;
        if (m_band > FixedQ4816.Zero) {
            var localY = (((Int128)position.Y.Value) - m_origin.Y.Value);

            if (
                (localY > m_band.Value) ||
                (localY < -((Int128)m_band.Value))
            ) {
                return false;
            }
        }
        var localX = ((position.X - m_origin.X) / m_cellSize);
        var localZ = ((position.Z - m_origin.Z) / m_cellSize);
        var r = (localZ / HexRowSpacing);
        var q = (localX + (r * Half));
        var coordinate = HexagonalCoordinate.Round(
            q: q,
            r: r
        );

        if (coordinate.Length > m_radius) {
            return false;
        }
        cell = ((int)HexagonalIndex.FromCoordinate(coordinate: coordinate).Value);
        return true;
    }

    /// <summary>Gets whether the topology is a lattice with translations — a grid, ring, hex, or box, whose cells
    /// carry axial coordinates — so the step carrying one cell to another can be carried to a third
    /// (<see cref="TryTranslation"/>, <see cref="TryOffset"/>). A graph or tiling has adjacency alone.</summary>
    public bool HasTranslations => (m_coordinates is not null);

    /// <summary>Returns the lattice translation carrying <paramref name="from"/> to <paramref name="to"/> — the
    /// coordinate difference along the topology's own axes: (dx, dz) on a grid or ring, (dq, dr) on a hex, plus the
    /// layer step on a box — or <see langword="false"/> on a topology without translations or for an invalid cell.
    /// On a wrapping axis the difference is the raw one; <see cref="TryOffset"/> wraps it back.</summary>
    /// <param name="from">The source cell ordinal.</param>
    /// <param name="to">The destination cell ordinal.</param>
    /// <param name="dx">The signed step along +X, or +q.</param>
    /// <param name="dz">The signed step along +Z (the depth axis), or +r.</param>
    /// <param name="dy">The signed layer step; zero on every kind but a box.</param>
    public bool TryTranslation(int from, int to, out int dx, out int dz, out int dy) {
        dx = 0;
        dz = 0;
        dy = 0;
        if (
            (m_coordinates is null) ||
            (((uint)from) >= ((uint)CellCount)) ||
            (((uint)to) >= ((uint)CellCount))
        ) {
            return false;
        }
        var source = m_coordinates[from];
        var target = m_coordinates[to];

        dx = (target.X - source.X);
        dz = (target.Y - source.Y);
        dy = (target.Z - source.Z);
        return true;
    }
    /// <summary>Returns the cell a lattice translation away — (dx, dz) on a grid or ring, (dq, dr) on a hex, with
    /// <paramref name="dy"/> the layer step on a box — or <see langword="false"/> off the board or on a topology
    /// without translations. A wrapping axis (a ring, a grid declaring <c>wrap</c>) folds the step back.</summary>
    /// <param name="cell">The source cell ordinal.</param>
    /// <param name="dx">The signed step along +X, or +q.</param>
    /// <param name="dz">The signed step along +Z, or +r.</param>
    /// <param name="result">The resolved cell ordinal.</param>
    /// <param name="dy">The signed layer step; must be zero on every kind but a box.</param>
    public bool TryOffset(int cell, int dx, int dz, out int result, int dy = 0) {
        result = -1;
        if (
            (m_coordinates is null) ||
            (m_coordinateIndex is null) ||
            (((uint)cell) >= ((uint)CellCount))
        ) {
            return false;
        }
        var source = m_coordinates[cell];
        var x = (source.X + dx);
        var y = (source.Y + dz);
        var z = (source.Z + dy);

        if (
            (Kind == TopologyKind.Ring) ||
            (m_wrap is TopologyWrap.X or TopologyWrap.Both)
        ) {
            x = x.FloorModulo(modulus: m_width);
        }
        if (m_wrap is TopologyWrap.Y or TopologyWrap.Both) {
            y = y.FloorModulo(modulus: m_depth);
        }
        if (!m_coordinateIndex.TryGetValue(
            key: (x, y, z),
            value: out result
        )) {
            result = -1;
            return false;
        }
        return true;
    }
    /// <summary>Reads one precomputed neighbour.</summary>
    /// <param name="cell">The source cell ordinal.</param>
    /// <param name="direction">The direction ordinal in this shape's vocabulary.</param>
    /// <returns>The neighbour, or -1 for an edge or invalid address.</returns>
    public int Neighbour(int cell, int direction) => (((((uint)cell) < CellCount) && (((uint)direction) < DirectionCount))
        ? m_neighbours[((cell * DirectionCount) + direction)]
        : -1
    );

    /// <summary>Walks the cells after an origin, stopping at an edge, a return to the origin, or the cell limit.</summary>
    public RayCells Ray(int origin, int direction) => new(this, origin, direction);

    /// <summary>Allocation-free traversal of the ordinals on a topology ray.</summary>
    public struct RayCells {
        private readonly CompiledTopology m_topology;
        private readonly int m_origin;
        private readonly int m_direction;
        private int m_remaining;

        internal RayCells(CompiledTopology topology, int origin, int direction) {
            m_topology = topology;
            m_origin = origin;
            m_direction = direction;
            m_remaining = (topology.CellCount - 1);
            Current = origin;
        }

        /// <summary>Gets the current cell ordinal.</summary>
        public int Current { get; private set; }
        /// <summary>Returns the traversal cursor.</summary>
        public readonly RayCells GetEnumerator() => this;
        /// <summary>Steps to the next cell, if the ray continues.</summary>
        public bool MoveNext() {
            if (m_remaining-- <= 0) {
                return false;
            }
            Current = m_topology.Neighbour(cell: Current, direction: m_direction);
            if ((Current < 0) || (Current == m_origin)) {
                m_remaining = 0;
                return false;
            }
            return true;
        }
    }
    /// <summary>Returns the most cells a ray read in one direction can hold, from any origin: the walk stops at an edge
    /// or on returning to its origin, and never takes more than <see cref="CellCount"/> less one step.</summary>
    /// <param name="direction">The direction ordinal.</param>
    /// <returns>The longest ray's cell count, or 0 for an invalid direction.</returns>
    /// <remarks>An axial shape's rays are walked once and remembered; a graph or tiling answers the ceiling, since its
    /// rays are bounded by nothing narrower than the whole cell set.</remarks>
    public int LongestRay(int direction) {
        if (((uint)direction) >= ((uint)DirectionCount)) {
            return 0;
        }
        if (Kind is not (TopologyKind.Grid or TopologyKind.Box or TopologyKind.Hex or TopologyKind.Ring)) {
            return Math.Max(val1: 0, val2: (CellCount - 1));
        }

        var rays = m_longestRays;

        if (rays is null) {
            rays = new int[DirectionCount];
            for (var way = 0; (way < DirectionCount); way++) {
                for (var origin = 0; (origin < CellCount); origin++) {
                    var length = 0;

                    foreach (var _ in Ray(origin: origin, direction: way)) {
                        length++;
                    }
                    rays[way] = Math.Max(val1: rays[way], val2: length);
                }
            }
            m_longestRays = rays;
        }

        return rays[direction];
    }
    /// <summary>Returns the sum of the longest rays in all directions.</summary>
    public long LongestRays() {
        var total = 0L;

        for (var direction = 0; (direction < DirectionCount); direction++) {
            total += LongestRay(direction: direction);
        }
        return total;
    }
    /// <summary>Reads the direction ordinal whose step vector is the negation of <paramref name="direction"/>'s —
    /// compiled once from each direction's own offset rather than assumed from ordinal arithmetic, so an
    /// asymmetrically-ordered direction table (a <see cref="TopologyKind.Box"/>'s 26) still resolves correctly.</summary>
    /// <param name="direction">The direction ordinal.</param>
    /// <returns>The opposite direction ordinal, or -1 for an invalid address.</returns>
    public int Opposite(int direction) => ((((uint)direction) < DirectionCount)
        ? m_opposite[direction]
        : -1
    );
    /// <summary>Returns a precompiled canonical cell key.</summary>
    /// <param name="cell">The cell ordinal.</param>
    /// <returns>The decimal key.</returns>
    public string Key(int cell) => m_keys[cell];
    /// <summary>Resolves a cell key, the <see cref="CanonicalDecimal"/> spelling of its ordinal, without
    /// allocation.</summary>
    /// <param name="key">The key.</param>
    /// <param name="cell">The ordinal on success; otherwise <c>-1</c>.</param>
    /// <returns>Whether the key names a cell.</returns>
    public bool TryCell(ReadOnlySpan<char> key, out int cell) {
        var named = (CanonicalDecimal.TryParse(text: key, value: out var ordinal) && (((ulong)ordinal) < ((ulong)CellCount)));

        cell = (named ? ((int)ordinal) : -1);

        return named;
    }
    /// <summary>Resolves a direction token for this topology — this topology's own authored names when
    /// <see cref="IDiscreteLatticeTopology.Directions"/> was declared, its kind's default names otherwise.</summary>
    /// <param name="token">The case-sensitive direction name.</param>
    /// <returns>The direction ordinal or -1.</returns>
    public int Direction(string token) => Array.IndexOf(
        array: m_directionNames,
        value: token
    );
    /// <summary>Gets a direction's own name.</summary>
    /// <param name="direction">The direction ordinal.</param>
    /// <returns>The name, or <see langword="null"/> for an invalid ordinal.</returns>
    public string? DirectionName(int direction) => ((((uint)direction) < ((uint)m_directionNames.Length))
        ? m_directionNames[direction]
        : null
    );
    /// <summary>Moves every set bit of a cell mask to its neighbour in a direction through the precomputed shift
    /// table, dropping a bit whose cell has no neighbour that way. The table exists only for a topology of at most
    /// 64 cells.</summary>
    /// <param name="direction">The direction ordinal.</param>
    /// <param name="mask">The mask, bit c set for cell ordinal c.</param>
    /// <param name="shifted">The shifted mask, or zero when no table answers.</param>
    /// <returns><see langword="true"/> when the precomputed table answered; otherwise <see langword="false"/>.</returns>
    public bool TryShiftMask(int direction, ulong mask, out ulong shifted) => TryGather(
        mask: mask,
        ordinal: direction,
        ordinalCount: DirectionCount,
        result: out shifted,
        table: m_directionShiftMasks
    );

    /// <summary>Reads whether every cell's neighbour in a direction lies at one signed bit distance, so a shift of a cell
    /// mask in that direction is a word shift masked by the cells a step lands on.</summary>
    /// <param name="direction">The direction ordinal.</param>
    /// <param name="step">The signed bit distance from a cell to its neighbour, or zero when no one distance holds.</param>
    /// <param name="landing">The cells that are some cell's neighbour that way, or zero when no one distance holds.</param>
    /// <returns><see langword="true"/> for a topology of at most 64 cells whose direction steps uniformly; otherwise
    /// <see langword="false"/>.</returns>
    public bool TryUniformStep(int direction, out int step, out ulong landing) {
        step = 0;
        landing = 0UL;
        if ((CellCount > BoardMask.MaxCells) || !TryUniformStepWords(direction: direction, landing: out var words, step: out step)) {
            step = 0;

            return false;
        }
        landing = words[0];

        return true;
    }
    /// <summary>Reads whether every cell's neighbour in a direction lies at one signed bit distance, for a topology of
    /// any size: a shift of a cell set in that direction is then a shift of its words (<see cref="BitWords.Shift"/>)
    /// masked by the cells a step lands on.</summary>
    /// <param name="direction">The direction ordinal.</param>
    /// <param name="step">The signed bit distance from a cell to its neighbour, or zero when no one distance holds.</param>
    /// <param name="landing">The cells that are some cell's neighbour that way, <see cref="CellWords"/> words, or empty
    /// when no one distance holds.</param>
    /// <returns><see langword="true"/> when the direction steps uniformly; otherwise <see langword="false"/>.</returns>
    public bool TryUniformStepWords(int direction, out int step, out ReadOnlySpan<ulong> landing) {
        step = 0;
        landing = default;
        if ((m_directionSteps is not { } steps) || (((uint)direction) >= ((uint)steps.Length)) || (steps[direction] == 0)) {
            return false;
        }
        step = steps[direction];
        landing = m_directionLandings.AsSpan(start: (direction * CellWords), length: CellWords);

        return true;
    }
    /// <summary>Gets how many 64-bit words hold one bit per cell of this topology.</summary>
    public int CellWords { get; }
    /// <summary>Writes the cells a cell set's members step to in one direction, dropping a member whose cell has no
    /// neighbour that way: a word shift masked by the landing cells for a uniform direction
    /// (<see cref="TryUniformStepWords"/>), and one neighbour read per member for any other.</summary>
    /// <param name="direction">The direction ordinal.</param>
    /// <param name="source">The cell set, <see cref="CellWords"/> words with no bit past the last cell.</param>
    /// <param name="destination">The stepped set, <see cref="CellWords"/> words; must not overlap
    /// <paramref name="source"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="direction"/> is not a direction of the
    /// topology.</exception>
    public void ShiftCells(int direction, ReadOnlySpan<ulong> source, Span<ulong> destination) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: (uint)direction, other: (uint)DirectionCount, paramName: nameof(direction));

        var words = destination[..CellWords];

        if (TryUniformStepWords(direction: direction, landing: out var landing, step: out var step)) {
            BitWords.Shift(destination: words, distance: step, source: source[..CellWords]);
            for (var word = 0; (word < words.Length); word++) {
                words[word] &= landing[word];
            }

            return;
        }

        words.Clear();
        foreach (var cell in BitWords.Members(words: source[..CellWords])) {
            var neighbour = Neighbour(cell: cell, direction: direction);

            if (neighbour >= 0) {
                BitWords.Add(index: neighbour, words: words);
            }
        }
    }
    /// <summary>Writes the cells reachable from a set of seeds through a set of passable cells, stepping in every
    /// direction: the seeds themselves, and every passable cell joined to one by a path of passable cells. The seeds
    /// need not be passable. The answer is the fixpoint of dilating by one step and keeping the passable cells, so it
    /// is the same set however it is computed. While the newest cells are many enough that one whole-word dilation step
    /// costs no more than visiting each of them (<see cref="ReachCellVisit"/> apiece), the walk dilates them a word at a
    /// time, every direction in turn; once they are fewer, or on a topology a direction of which steps unevenly, it
    /// visits the rest cell by cell from a queue. A cell is expanded once either way, so the work never exceeds
    /// <see cref="ReachWork"/>.</summary>
    /// <param name="seeds">The cells the reach starts from, <see cref="CellWords"/> words with no bit past the last
    /// cell.</param>
    /// <param name="passable">The cells a path may pass through, <see cref="CellWords"/> words.</param>
    /// <param name="reach">The reached cells, <see cref="CellWords"/> words; must overlap neither source.</param>
    /// <param name="scratch">The working storage the walk borrows its frontier words and cell queue from.</param>
    /// <returns>How many whole-word dilation steps the walk took.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scratch"/> is <see langword="null"/>.</exception>
    public int Reach(ReadOnlySpan<ulong> seeds, ReadOnlySpan<ulong> passable, Span<ulong> reach, ArenaScratch scratch) {
        ArgumentNullException.ThrowIfNull(argument: scratch);

        var words = CellWords;
        var reached = reach[..words];
        using var frontierLease = scratch.Rent<ulong>(length: words);
        using var stepLease = scratch.Rent<ulong>(length: words);
        var frontier = frontierLease.Span;
        var stepped = stepLease.Span;
        var wordStep = ReachWordStep;
        var size = (long)BitWords.Count(words: seeds[..words]);
        var steps = 0;

        seeds[..words].CopyTo(destination: reached);
        seeds[..words].CopyTo(destination: frontier);

        // A word step expands the whole frontier at once: it is taken only while that costs no more than visiting the
        // frontier's cells, so the steps together cost at most a visit of every cell they expand.
        while (StepsUniformly && (size > 0L) && (wordStep <= (size * ReachCellVisit))) {
            using var nextLease = scratch.Rent<ulong>(length: words);
            var next = nextLease.Span;

            for (var direction = 0; (direction < DirectionCount); direction++) {
                ShiftCells(destination: stepped, direction: direction, source: frontier);
                for (var word = 0; (word < words); word++) {
                    next[word] |= stepped[word];
                }
            }
            size = 0L;
            for (var word = 0; (word < words); word++) {
                next[word] &= (passable[word] & ~reached[word]);
                reached[word] |= next[word];
                size += System.Numerics.BitOperations.PopCount(value: next[word]);
            }
            next.CopyTo(destination: frontier);
            steps++;
        }
        if (size == 0L) {
            return steps;
        }

        // The frontier's cells are expanded one at a time, each passable cell entering the queue once.
        using var queueLease = scratch.Rent<int>(length: CellCount);
        var queue = queueLease.Span;
        var tail = 0;

        foreach (var cell in BitWords.Members(words: frontier)) {
            queue[tail++] = cell;
        }
        for (var head = 0; (head < tail); head++) {
            var cell = queue[head];

            for (var direction = 0; (direction < DirectionCount); direction++) {
                var neighbour = Neighbour(cell: cell, direction: direction);

                if ((neighbour >= 0) && BitWords.Contains(index: neighbour, words: passable) && !BitWords.Contains(index: neighbour, words: reached)) {
                    BitWords.Add(index: neighbour, words: reached);
                    queue[tail++] = neighbour;
                }
            }
        }

        return steps;
    }
    /// <summary>Gets the element operations <see cref="Reach"/> spends expanding one cell from its queue: taking the
    /// cell, then for every direction its neighbour's read and two membership tests, and a mark and a push for a
    /// neighbour it reaches.</summary>
    public long ReachCellVisit => (3L + (3L * DirectionCount));
    /// <summary>Gets the element operations one whole-word dilation step of <see cref="Reach"/> costs: every
    /// direction's shift, mask and union over every word, and the frontier's mask, union and count.</summary>
    public long ReachWordStep => (((long)CellWords) * ((3L * DirectionCount) + 4L));
    /// <summary>Returns the most element operations one <see cref="Reach"/> costs, read from the topology alone: every
    /// cell expanded once, by a word step that costs no more than visiting the cells it expands or by a visit from the
    /// queue, and the seeds' words copied and counted.</summary>
    /// <returns>The element operations.</returns>
    public long ReachWork() => ((CellCount * ReachCellVisit) + (4L * CellWords));
    // Whether every direction steps at one bit distance, so a word step is a shift and a mask of each word.
    private bool StepsUniformly => ((m_directionSteps is { } steps) && !steps.AsSpan().Contains(value: 0));
    /// <summary>Writes the cells a point-group element carries a cell set's members to.</summary>
    /// <param name="element">The element ordinal.</param>
    /// <param name="source">The cell set, <see cref="CellWords"/> words.</param>
    /// <param name="destination">The carried set, <see cref="CellWords"/> words; must not overlap
    /// <paramref name="source"/>.</param>
    public void ImageCells(int element, ReadOnlySpan<ulong> source, Span<ulong> destination) {
        var words = destination[..CellWords];

        if ((CellCount <= BoardMask.MaxCells) && TryImageMask(element: element, image: out var image, mask: source[0])) {
            words[0] = image;

            return;
        }

        words.Clear();
        foreach (var cell in BitWords.Members(words: source[..CellWords])) {
            var carried = Image(cell: cell, element: element);

            if (carried >= 0) {
                BitWords.Add(index: carried, words: words);
            }
        }
    }

    // One row of a precomputed per-cell mask table: 64 destination masks per ordinal, indexed by source cell. The
    // image of a mask is the union of the rows its set bits select.
    private static bool TryGather(ulong[]? table, int ordinal, int ordinalCount, ulong mask, out ulong result) {
        result = 0UL;

        if (
            (table is null) ||
            (((uint)ordinal) >= ((uint)ordinalCount))
        ) {
            return false;
        }

        var row = table.AsSpan(
            length: BoardMask.MaxCells,
            start: (ordinal * BoardMask.MaxCells)
        );

        while (mask != 0UL) {
            result |= row[System.Numerics.BitOperations.TrailingZeroCount(value: mask)];
            mask &= (mask - 1UL);
        }

        return true;
    }
    /// <summary>Returns whether two topologies are one lattice wherever each stands: the same kind, cells, directions
    /// and adjacency, so a query that reads cells and their neighbours (a mask, a ray, a neighbour) answers alike over
    /// either. Their origins, cell sizes and anchors may differ.</summary>
    /// <param name="left">The one topology.</param>
    /// <param name="right">The other topology.</param>
    /// <returns><see langword="true"/> when the two are one lattice.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="left"/> or <paramref name="right"/> is
    /// <see langword="null"/>.</exception>
    public static bool SameShape(CompiledTopology left, CompiledTopology right) {
        ArgumentNullException.ThrowIfNull(argument: left);
        ArgumentNullException.ThrowIfNull(argument: right);

        return (ReferenceEquals(objA: left, objB: right) || (
            (left.Kind == right.Kind) &&
            (left.CellCount == right.CellCount) &&
            left.m_directionNames.AsSpan().SequenceEqual(other: right.m_directionNames) &&
            left.m_neighbours.AsSpan().SequenceEqual(other: right.m_neighbours)
        ));
    }
}
public sealed partial class CompiledTopology {
    private readonly int[][] m_images = [];
    private readonly string[] m_elementNames = [];
    private readonly Dictionary<string, int> m_elementAliases = new(comparer: StringComparer.Ordinal);

    private readonly ulong[]? m_elementImageMasks;

    /// <summary>Carries every set bit of a cell mask through a point-group element by the precomputed image table.
    /// The table exists only for a topology of at most 64 cells.</summary>
    /// <param name="element">The element ordinal.</param>
    /// <param name="mask">The mask, bit c set for cell ordinal c.</param>
    /// <param name="image">The image mask, or zero when no table answers.</param>
    /// <returns><see langword="true"/> when the precomputed table answered; otherwise <see langword="false"/>.</returns>
    public bool TryImageMask(int element, ulong mask, out ulong image) => TryGather(
        mask: mask,
        ordinal: element,
        ordinalCount: ElementCount,
        result: out image,
        table: m_elementImageMasks
    );

    /// <summary>Gets the number of elements in the topology's point group, the identity included: 8 for a square
    /// grid, 4 for a rectangle, 12 for a hex board, 1 for a ring.</summary>
    public int ElementCount => m_elementNames.Length;

    /// <summary>Gets an element's canonical signed-axis name by ordinal; ordinal 0 is the identity.</summary>
    /// <param name="element">The element ordinal.</param>
    public string ElementName(int element) => m_elementNames[element];
    /// <summary>Finds an element by its canonical name or an authored alias.</summary>
    /// <param name="name">A name <see cref="ElementName"/> answers, or an authored
    /// <see cref="IDiscreteLatticeTopology.ElementAliases"/> entry.</param>
    /// <returns>The element ordinal, or -1.</returns>
    public int Element(string name) {
        var canonical = Array.IndexOf(
            array: m_elementNames,
            value: name
        );

        return ((canonical >= 0)
            ? canonical
            : (m_elementAliases.TryGetValue(
                key: name,
                value: out var aliased
            )
                ? aliased
                : -1
        ));
    }
    /// <summary>Gets every authored alias name and the canonical element name it resolves to, for a read-back.</summary>
    public IEnumerable<(string Alias, string Canonical)> ElementAliases() {
        foreach (var (alias, element) in m_elementAliases) {
            yield return (alias, m_elementNames[element]);
        }
    }
    /// <summary>Gets the cell an element carries a cell to.</summary>
    /// <param name="element">The element ordinal.</param>
    /// <param name="cell">The cell ordinal.</param>
    public int Image(int element, int cell) => m_images[element][cell];

    // Every point-group element — Grid's, Hex's, and Box's alike — is a signed-axis permutation: it carries source
    // axis A to output position k with sign S, spelled "+x-y+z" (letter per axis, sign first). A Box names 48 cube
    // elements this way; Grid (2 planar axes, letters "xz") and Hex (3 cube coordinates q/r/s summing to zero,
    // letters "qrs") read the same AxisMap/Spell mechanism. Element 0 is always the identity, so a caller may fold
    // over all elements and rely on the untransformed board being among the images.
    internal static (int[][] Images, string[] Names) BuildSymmetry(TopologyKind kind, int width, int depth, int layers,
        IReadOnlyList<(int X, int Y, int Z)> coordinates, Dictionary<(int, int, int), int> indices) {
        var group = EnumerateGroup(
            depth: depth,
            kind: kind,
            layers: layers,
            width: width
        );

        return ((kind == TopologyKind.Hex)
            ? MaterializeHex(
                group.Elements,
                coordinates,
                indices
            )
            : MaterializeAxis(
                group.Elements,
                group.AxisCount,
                group.Letters,
                group.Extents,
                coordinates,
                indices
            )
        );
    }
    /// <summary>Names every element of a topology's point group without materializing per-cell images — the
    /// bare-group enumeration a validator uses to check an authored <see cref="IDiscreteLatticeTopology.ElementAliases"/>
    /// entry names a real element before any topology cell exists to carry.</summary>
    /// <param name="kind">The topology kind.</param>
    /// <param name="width">Cells along +X.</param>
    /// <param name="depth">Cells along +Z.</param>
    /// <param name="layers">Cells along +Y.</param>
    /// <returns>Every element's canonical signed-axis name, identity first.</returns>
    internal static string[] ElementNames(TopologyKind kind, int width, int depth, int layers) {
        var group = EnumerateGroup(
            depth: depth,
            kind: kind,
            layers: layers,
            width: width
        );
        var names = new string[group.Elements.Count];

        for (var element = 0; (element < names.Length); element++) {
            names[element] = group.Elements[element].Name(
                axisCount: group.AxisCount,
                letters: group.Letters
            );
        }
        return names;
    }

    private readonly record struct Group(List<AxisMap> Elements, int AxisCount, string Letters, int[] Extents);

    private static Group EnumerateGroup(TopologyKind kind, int width, int depth, int layers) => kind switch {
        TopologyKind.Grid => EnumerateAxisGroup(
        axisCount: 2,
        extents: [width, depth, 1],
        letters: "xz"
    ),
        TopologyKind.Box => EnumerateAxisGroup(
        axisCount: 3,
        extents: [width, depth, layers],
        letters: "xyz"
    ),
        TopologyKind.Hex => EnumerateHexGroup(),
        _ => new(
        AxisCount: 3,
        Elements: [AxisMap.Identity],
        Extents: [width, depth, layers],
        Letters: "xyz"
    ),
    };

    // A signed permutation of up to three axes: axis k's SOURCE is this[k], carried with sign Sign(k). Slots beyond
    // an axis count in play (Grid uses 2) stay the identity (axis 2, sign +1) by construction — no generator ever
    // touches them — so the same three-slot representation and per-cell loop serve every kind.
    private readonly record struct AxisMap(int A0, int S0, int A1, int S1, int A2, int S2) {
        public static readonly AxisMap Identity = new(
            A0: 0,
            A1: 1,
            A2: 2,
            S0: 1,
            S1: 1,
            S2: 1
        );

        public int this[int axis] => axis switch { 0 => A0, 1 => A1, _ => A2 };

        public bool IsIdentity(int axisCount) {
            for (var axis = 0; (axis < axisCount); axis++) {
                if (
                    (this[axis] != axis) ||
                    (Sign(axis: axis) != 1)
                ) { return false; }
            }
            return true;
        }
        public string Name(int axisCount, string letters) {
            if (IsIdentity(axisCount: axisCount)) { return "identity"; }
            var name = string.Empty;

            for (var axis = 0; (axis < axisCount); axis++) {
                name += (((Sign(axis: axis) > 0)
                    ? "+"
                    : "-") + letters[this[axis]]);
            }
            return name;
        }
        public int Sign(int axis) => axis switch { 0 => S0, 1 => S1, _ => S2 };
        public AxisMap Then(AxisMap next) => new(
            this[next.A0],
            (Sign(axis: next.A0) * next.S0),
            this[next.A1],
            (Sign(axis: next.A1) * next.S1),
            this[next.A2],
            (Sign(axis: next.A2) * next.S2)
        );
    }

    // Grid's and Box's point group is generated by mirroring each in-play axis and swapping every pair of equal
    // extent, closed by breadth-first composition — the same closure a Box's cube symmetry always used, now shared
    // with Grid's rectangle/square case instead of a second hand-written generator list.
    private static Group EnumerateAxisGroup(int axisCount, int[] extents, string letters) {
        var generators = new List<AxisMap>();

        for (var axis = 0; (axis < axisCount); axis++) {
            generators.Add(item: FlipAxis(axis: axis));
        }
        for (var a = 0; (a < axisCount); a++) {
            for (var b = (a + 1); (b < axisCount); b++) {
                if (extents[a] == extents[b]) {
                    generators.Add(item: SwapAxes(
                    a: a,
                    b: b
                ));
                }
            }
        }
        var elements = new List<AxisMap> { AxisMap.Identity };
        var seen = new HashSet<AxisMap>(collection: elements);

        for (var index = 0; (index < elements.Count); index++) {
            foreach (var generator in generators) {
                var composed = elements[index].Then(next: generator);

                if (seen.Add(item: composed)) { elements.Add(item: composed); }
            }
        }
        return new(
            AxisCount: axisCount,
            Elements: elements,
            Extents: extents,
            Letters: letters
        );
    }
    private static AxisMap FlipAxis(int axis) => axis switch {
        0 => new(
        A0: 0,
        A1: 1,
        A2: 2,
        S0: -1,
        S1: 1,
        S2: 1
    ),
        1 => new(
        A0: 0,
        A1: 1,
        A2: 2,
        S0: 1,
        S1: -1,
        S2: 1
    ),
        _ => new(
        A0: 0,
        A1: 1,
        A2: 2,
        S0: 1,
        S1: 1,
        S2: -1
    ),
    };
    private static AxisMap SwapAxes(int a, int b) {
        Span<int> axis = [0, 1, 2];

        (axis[a], axis[b]) = (axis[b], axis[a]);
        return new(
            axis[0],
            1,
            axis[1],
            1,
            axis[2],
            1
        );
    }
    private static (int[][] Images, string[] Names) MaterializeAxis(List<AxisMap> elements, int axisCount, string letters, int[] extents,
        IReadOnlyList<(int X, int Y, int Z)> coordinates, Dictionary<(int, int, int), int> indices) {
        var images = new int[elements.Count][];
        var names = new string[elements.Count];

        for (var element = 0; (element < elements.Count); element++) {
            var map = elements[element];

            names[element] = map.Name(
                axisCount: axisCount,
                letters: letters
            );
            var image = new int[coordinates.Count];

            for (var cell = 0; (cell < coordinates.Count); cell++) {
                var p = coordinates[cell];
                int[] source = [p.X, p.Y, p.Z];
                var target = new int[3];

                for (var axis = 0; (axis < 3); axis++) {
                    var value = source[map[axis]];

                    target[axis] = ((map.Sign(axis: axis) > 0)
                        ? value
                        : ((extents[map[axis]] - 1) - value)
                    );
                }
                image[cell] = indices[(target[0], target[1], target[2])];
            }
            images[element] = image;
        }
        return (images, names);
    }
    // A hex's axial (q, r) is one plane of the cube coordinates (q, r, s) with q + r + s = 0. The elements of its
    // point group are exactly the signed permutations of (q, r, s) that keep every point on that plane: a bare
    // permutation of the three (six of them), or the same six permutations composed with negating all three — any
    // OTHER sign pattern moves a sum-zero point off the plane. That is 12 elements, matching the hexagon's own
    // dihedral group, enumerated directly rather than discovered by closure.
    private static Group EnumerateHexGroup() {
        Span<int> identityAxes = [0, 1, 2];
        var permutations = new List<int[]>();

        Permute(
            axes: identityAxes,
            from: 0,
            results: permutations
        );

        var elements = new List<AxisMap>();

        foreach (var sign in new[] { 1, -1 }) {
            foreach (var permutation in permutations) {
                elements.Add(item: new(
                    permutation[0],
                    sign,
                    permutation[1],
                    sign,
                    permutation[2],
                    sign
                ));
            }
        }
        elements.Sort(comparison: (left, right) => (left.IsIdentity(axisCount: 3)
            ? -1
            : (right.IsIdentity(axisCount: 3)
                ? 1
                : 0)));
        return new(
            AxisCount: 3,
            Elements: elements,
            Extents: [0, 0, 0],
            Letters: "qrs"
        );
    }
    private static (int[][] Images, string[] Names) MaterializeHex(List<AxisMap> elements, IReadOnlyList<(int X, int Y, int Z)> coordinates, Dictionary<(int, int, int), int> indices) {
        var images = new int[elements.Count][];
        var names = new string[elements.Count];

        for (var element = 0; (element < elements.Count); element++) {
            var map = elements[element];

            names[element] = map.Name(
                axisCount: 3,
                letters: "qrs"
            );
            var image = new int[coordinates.Count];

            for (var cell = 0; (cell < coordinates.Count); cell++) {
                // The symmetry walk is spelled over cube coordinates of the 60° axial basis; a cell's Eisenstein
                // (Q, R) maps to axial (Q, −R). KEEP IN SYNC with the inverse on the image lookup below.
                var (q, negatedR, _) = coordinates[cell];
                var r = -negatedR;
                var s = (-q - r);
                int[] cube = [q, r, s];
                var target = new int[3];

                for (var axis = 0; (axis < 3); axis++) {
                    target[axis] = (map.Sign(axis: axis) * cube[map[axis]]);
                }
                image[cell] = indices[(target[0], -target[1], 0)];
            }
            images[element] = image;
        }
        return (images, names);
    }
    private static void Permute(Span<int> axes, int from, List<int[]> results) {
        if (from == axes.Length) {
            results.Add(item: axes.ToArray());
            return;
        }
        for (var index = from; (index < axes.Length); index++) {
            (axes[from], axes[index]) = (axes[index], axes[from]);
            Permute(
                axes: axes,
                from: (from + 1),
                results: results
            );
            (axes[from], axes[index]) = (axes[index], axes[from]);
        }
    }

    // Authored friendlier names ("rot90" for whatever axis permutation a square grid's quarter turn is) resolve
    // through Element(string) alongside the canonical spelling; ElementName always answers the canonical form. The
    // validator already proved every alias names a real element, so a miss here can only mean the alias outlived
    // its topology's own recompile — install it defensively rather than throw.
    internal void InstallElementAliases(IReadOnlyList<TopologyElementAlias>? aliases) {
        m_elementAliases.Clear();
        foreach (var alias in (aliases ?? [])) {
            var canonical = Array.IndexOf(
                array: m_elementNames,
                value: alias.Element
            );

            if (canonical >= 0) {
                m_elementAliases[alias.Name] = canonical;
            }
        }
    }
}
