using System.Globalization;
using Puck.Maths;

namespace Puck.State;

/// <summary>A compiled immutable adjacency table. Absent neighbours are -1. Direction names come from the
/// topology's own <see cref="IDiscreteLatticeTopology.Directions"/> when authored; the unauthored default matches
/// what every kind carried before that field existed — Grid N, NE, E, SE, S, SW, W, NW; Hex E, NE, NW, W, SW, SE;
/// Box the 26 in <see cref="BoxDirectionNames"/>; Ring forward and backward.</summary>
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
    private readonly FixedQ4816 m_cellSize;
    private readonly FixedQ4816 m_band;
    // Authored centres relative to the origin, present for a Graph alone; every other kind derives its centres.
    private readonly FixedVector3[]? m_cellCentres;
    // Each cell's axial coordinate — (x, z, layer) on a grid, ring, or box, (q, r, 0) on a hex — and its inverse, present
    // for the lattice kinds alone: what makes a translation between two cells carriable to a third.
    private readonly (int X, int Y, int Z)[]? m_coordinates;
    private readonly Dictionary<(int, int, int), int>? m_coordinateIndex;
    private readonly ulong[]? m_directionShiftMasks;

    internal CompiledTopology(TopologyKind kind, int count, int directions, int[] neighbours, int[] opposite,
        int width, int depth, int radius, TopologyWrap wrap, FixedVector3 origin, FixedQ4816 cellSize, FixedQ4816 band,
        int[][] images, string[] elementNames, int layers, FixedQ4816 layerHeight, string[] directionNames, FixedVector3[]? cellCentres = null,
        (int X, int Y, int Z)[]? coordinates = null) {
        m_cellCentres = cellCentres;
        m_coordinates = coordinates;
        if (coordinates is not null) {
            m_coordinateIndex = new Dictionary<(int, int, int), int>(capacity: coordinates.Length);
            for (var cell = 0; cell < coordinates.Length; cell++) {
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
        for (var cell = 0; cell < count; cell++) {
            m_keys[cell] = cell.ToString(CultureInfo.InvariantCulture);
            m_names[cell] = CellName.Parse(m_keys[cell]);
        }
        if (count <= BoardMask.MaxCells && directions > 0) {
            var shiftMasks = new ulong[directions * BoardMask.MaxCells];
            for (var d = 0; d < directions; d++) {
                var offset = d * BoardMask.MaxCells;
                for (var cell = 0; cell < count; cell++) {
                    var neighbour = neighbours[cell * directions + d];
                    if (neighbour >= 0 && neighbour < BoardMask.MaxCells) {
                        shiftMasks[offset + cell] = 1UL << neighbour;
                    }
                }
            }
            m_directionShiftMasks = shiftMasks;
        }
        if (count <= BoardMask.MaxCells && images is { Length: > 0 }) {
            var imageMasks = new ulong[images.Length * BoardMask.MaxCells];
            for (var elem = 0; elem < images.Length; elem++) {
                var offset = elem * BoardMask.MaxCells;
                var elemImages = images[elem];
                for (var cell = 0; cell < count && cell < elemImages.Length; cell++) {
                    var carried = elemImages[cell];
                    if (carried >= 0 && carried < BoardMask.MaxCells) {
                        imageMasks[offset + cell] = 1UL << carried;
                    }
                }
            }
            m_elementImageMasks = imageMasks;
        }
    }

    /// <summary>Gets a cell's key as a parsed cell name, without re-parsing.</summary>
    /// <param name="cell">The cell ordinal.</param>
    public CellName NameOf(int cell) => m_names[cell];

    /// <summary>Gets the shape.</summary>
    public TopologyKind Kind { get; }
    /// <summary>Gets the number of cells.</summary>
    public int CellCount { get; }
    /// <summary>Gets the number of directions at each cell.</summary>
    public int DirectionCount { get; }
    /// <summary>Gets the declared minimum corner — the spatial frame a <see cref="Kind"/> of
    /// <see cref="TopologyKind.Grid"/> resolves <see cref="TryCellOf"/>/<see cref="TryOffset"/> against.</summary>
    public FixedVector3 Origin => m_origin;
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
    private static readonly FixedQ4816 s_hexRowSpacing = FixedQ4816.FromDouble(value: 0.8660254037844386);
    private static readonly FixedQ4816 s_half = FixedQ4816.FromDouble(value: 0.5);

    /// <summary>Returns the centre of a cell: a grid or box cell's square (or cube) centre, a hex cell's lattice
    /// point, in the topology's anchored frame.</summary>
    /// <param name="cell">The cell ordinal.</param>
    public FixedVector3 CellCentre(int cell) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: ((uint)cell), other: ((uint)CellCount));
        if (m_cellCentres is { } centres) {
            var centre = centres[cell];
            return new FixedVector3(X: (m_origin.X + centre.X), Y: (m_origin.Y + centre.Y), Z: (m_origin.Z + centre.Z));
        }
        if (Kind == TopologyKind.Hex) {
            var coordinate = new HexagonalIndex(value: cell).ToCoordinate();
            var q = FixedQ4816.FromInteger(value: coordinate.Q);
            var r = FixedQ4816.FromInteger(value: coordinate.R);
            return new FixedVector3(
                X: (m_origin.X + (m_cellSize * (q - (r * s_half)))),
                Y: m_origin.Y,
                Z: (m_origin.Z + (m_cellSize * (r * s_hexRowSpacing)))
            );
        }
        var planar = (cell % (m_width * m_depth));
        var layer = (cell / (m_width * m_depth));
        return new FixedVector3(
            X: (m_origin.X + (m_cellSize * (FixedQ4816.FromInteger(value: (planar % m_width)) + s_half))),
            Y: (m_origin.Y + (m_layerHeight * FixedQ4816.FromInteger(value: layer))),
            Z: (m_origin.Z + (m_cellSize * (FixedQ4816.FromInteger(value: (planar / m_width)) + s_half)))
        );
    }

    /// <summary>Resolves the grid cell a world position falls in, X/Z only — a board carries one layer, so no
    /// height test applies. Only <see cref="TopologyKind.Grid"/> carries a rectangular X/Z frame; every other
    /// kind answers <see langword="false"/>.</summary>
    /// <param name="position">The world position (a body's resolved pose).</param>
    /// <param name="cell">The resolved cell ordinal.</param>
    /// <returns>Whether the position lies over a declared cell.</returns>
    public bool TryCellOf(in FixedVector3 position, out int cell) {
        cell = -1;
        if (m_cellCentres is { } centres) {
            return TryNearestCellOf(position: in position, centres: centres, cell: out cell);
        }
        if (Kind == TopologyKind.Hex) {
            return TryHexCellOf(position: in position, cell: out cell);
        }
        if (Kind is not (TopologyKind.Grid or TopologyKind.Box)) {
            return false;
        }
        var layer = 0;
        if (Kind == TopologyKind.Box) {
            var localY = ((Int128)position.Y.Value) - m_origin.Y.Value;
            if (localY < Int128.Zero) {
                return false;
            }
            var y = localY / m_layerHeight.Value;
            if (y >= m_layers) {
                return false;
            }
            layer = (int)y;
        } else if (m_band > FixedQ4816.Zero) {
            var localY = ((Int128)position.Y.Value) - m_origin.Y.Value;
            if (localY > m_band.Value || localY < -(Int128)m_band.Value) {
                return false;
            }
        }
        var localX = ((Int128)position.X.Value) - m_origin.X.Value;
        var localZ = ((Int128)position.Z.Value) - m_origin.Z.Value;
        if (localX < Int128.Zero || localZ < Int128.Zero) {
            return false;
        }
        var x = localX / m_cellSize.Value;
        var z = localZ / m_cellSize.Value;
        if (x >= m_width || z >= m_depth) {
            return false;
        }
        cell = (((layer * m_depth) + (int)z) * m_width) + (int)x;
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
        for (var index = 0; index < centres.Length; index++) {
            var centre = centres[index];
            var dx = (((Int128)position.X.Value) - (((Int128)m_origin.X.Value) + centre.X.Value));
            var dy = (((Int128)position.Y.Value) - (((Int128)m_origin.Y.Value) + centre.Y.Value));
            var dz = (((Int128)position.Z.Value) - (((Int128)m_origin.Z.Value) + centre.Z.Value));
            var distance = ((dx * dx) + (dy * dy) + (dz * dz));
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
            if ((localY > m_band.Value) || (localY < -(Int128)m_band.Value)) {
                return false;
            }
        }
        var localX = ((position.X - m_origin.X) / m_cellSize);
        var localZ = ((position.Z - m_origin.Z) / m_cellSize);
        var r = (localZ / s_hexRowSpacing);
        var q = (localX + (r * s_half));
        var coordinate = HexagonalCoordinate.Round(q: q, r: r);
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
        if ((m_coordinates is null) || ((uint)from >= (uint)CellCount) || ((uint)to >= (uint)CellCount)) {
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
        if ((m_coordinates is null) || (m_coordinateIndex is null) || ((uint)cell >= (uint)CellCount)) {
            return false;
        }
        var source = m_coordinates[cell];
        var x = (source.X + dx);
        var y = (source.Y + dz);
        var z = (source.Z + dy);
        if ((Kind == TopologyKind.Ring) || (m_wrap is TopologyWrap.X or TopologyWrap.Both)) {
            x = (((x % m_width) + m_width) % m_width);
        }
        if (m_wrap is TopologyWrap.Y or TopologyWrap.Both) {
            y = (((y % m_depth) + m_depth) % m_depth);
        }
        if (!m_coordinateIndex.TryGetValue((x, y, z), out result)) {
            result = -1;
            return false;
        }
        return true;
    }
    /// <summary>Reads one precomputed neighbour.</summary>
    /// <param name="cell">The source cell ordinal.</param>
    /// <param name="direction">The direction ordinal in this shape's vocabulary.</param>
    /// <returns>The neighbour, or -1 for an edge or invalid address.</returns>
    public int Neighbour(int cell, int direction) => (uint)cell < CellCount && (uint)direction < DirectionCount
        ? m_neighbours[cell * DirectionCount + direction] : -1;

    /// <summary>Reads the direction ordinal whose step vector is the negation of <paramref name="direction"/>'s —
    /// compiled once from each direction's own offset rather than assumed from ordinal arithmetic, so an
    /// asymmetrically-ordered direction table (a <see cref="TopologyKind.Box"/>'s 26) still resolves correctly.</summary>
    /// <param name="direction">The direction ordinal.</param>
    /// <returns>The opposite direction ordinal, or -1 for an invalid address.</returns>
    public int Opposite(int direction) => (uint)direction < DirectionCount ? m_opposite[direction] : -1;

    /// <summary>Returns a precompiled canonical cell key.</summary>
    /// <param name="cell">The cell ordinal.</param>
    /// <returns>The decimal key.</returns>
    public string Key(int cell) => m_keys[cell];

    /// <summary>Resolves a canonical decimal cell key without allocation.</summary>
    /// <param name="key">The key.</param>
    /// <param name="cell">The ordinal.</param>
    /// <returns>Whether the key names a cell.</returns>
    public bool TryCell(string key, out int cell) => int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out cell)
        && (uint)cell < CellCount && string.Equals(key, m_keys[cell], StringComparison.Ordinal);

    /// <summary>Resolves a direction token for this topology — this topology's own authored names when
    /// <see cref="IDiscreteLatticeTopology.Directions"/> was declared, its kind's default names otherwise.</summary>
    /// <param name="token">The case-sensitive direction name.</param>
    /// <returns>The direction ordinal or -1.</returns>
    public int Direction(string token) => Array.IndexOf(m_directionNames, token);
    /// <summary>Gets a direction's own name.</summary>
    /// <param name="direction">The direction ordinal.</param>
    /// <returns>The name, or <see langword="null"/> for an invalid ordinal.</returns>
    public string? DirectionName(int direction) => ((uint)direction < (uint)m_directionNames.Length) ? m_directionNames[direction] : null;

    /// <summary>Attempts to read precomputed 64-bit shift masks for a direction when the topology has at most 64 cells.</summary>
    /// <param name="direction">The direction ordinal.</param>
    /// <param name="masks">The 64-element span of destination bitmasks indexed by source cell ordinal.</param>
    /// <returns><see langword="true"/> when precomputed masks are available; otherwise <see langword="false"/>.</returns>
    public bool TryGetShiftMasks(int direction, out ReadOnlySpan<ulong> masks) {
        if (m_directionShiftMasks is not null && (uint)direction < (uint)DirectionCount) {
            masks = m_directionShiftMasks.AsSpan(direction * BoardMask.MaxCells, BoardMask.MaxCells);
            return true;
        }
        masks = default;
        return false;
    }
}

public sealed partial class CompiledTopology {
    private readonly int[][] m_images = [];
    private readonly string[] m_elementNames = [];
    private readonly Dictionary<string, int> m_elementAliases = new(StringComparer.Ordinal);
    private readonly ulong[]? m_elementImageMasks;

    /// <summary>Attempts to read precomputed 64-bit image masks for a point-group element when the topology has at most 64 cells.</summary>
    /// <param name="element">The element ordinal.</param>
    /// <param name="masks">The 64-element span of destination bitmasks indexed by source cell ordinal.</param>
    /// <returns><see langword="true"/> when precomputed masks are available; otherwise <see langword="false"/>.</returns>
    public bool TryGetImageMasks(int element, out ReadOnlySpan<ulong> masks) {
        if (m_elementImageMasks is not null && (uint)element < (uint)ElementCount) {
            masks = m_elementImageMasks.AsSpan(element * BoardMask.MaxCells, BoardMask.MaxCells);
            return true;
        }
        masks = default;
        return false;
    }

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
        var canonical = Array.IndexOf(m_elementNames, name);
        return (canonical >= 0) ? canonical : (m_elementAliases.TryGetValue(name, out var aliased) ? aliased : -1);
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
        var group = EnumerateGroup(kind, width, depth, layers);
        return (kind == TopologyKind.Hex)
            ? MaterializeHex(group.Elements, coordinates, indices)
            : MaterializeAxis(group.Elements, group.AxisCount, group.Letters, group.Extents, coordinates, indices);
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
        var group = EnumerateGroup(kind, width, depth, layers);
        var names = new string[group.Elements.Count];
        for (var element = 0; element < names.Length; element++) {
            names[element] = group.Elements[element].Name(group.AxisCount, group.Letters);
        }
        return names;
    }

    private readonly record struct Group(List<AxisMap> Elements, int AxisCount, string Letters, int[] Extents);

    private static Group EnumerateGroup(TopologyKind kind, int width, int depth, int layers) => kind switch {
        TopologyKind.Grid => EnumerateAxisGroup(axisCount: 2, extents: [width, depth, 1], letters: "xz"),
        TopologyKind.Box => EnumerateAxisGroup(axisCount: 3, extents: [width, depth, layers], letters: "xyz"),
        TopologyKind.Hex => EnumerateHexGroup(),
        _ => new([AxisMap.Identity], 3, "xyz", [width, depth, layers]),
    };

    // A signed permutation of up to three axes: axis k's SOURCE is this[k], carried with sign Sign(k). Slots beyond
    // an axis count in play (Grid uses 2) stay the identity (axis 2, sign +1) by construction — no generator ever
    // touches them — so the same three-slot representation and per-cell loop serve every kind.
    private readonly record struct AxisMap(int A0, int S0, int A1, int S1, int A2, int S2) {
        public static readonly AxisMap Identity = new(0, 1, 1, 1, 2, 1);
        public int this[int axis] => axis switch { 0 => A0, 1 => A1, _ => A2 };
        public int Sign(int axis) => axis switch { 0 => S0, 1 => S1, _ => S2 };
        public bool IsIdentity(int axisCount) {
            for (var axis = 0; axis < axisCount; axis++) {
                if (this[axis] != axis || Sign(axis) != 1) { return false; }
            }
            return true;
        }
        public string Name(int axisCount, string letters) {
            if (IsIdentity(axisCount)) { return "identity"; }
            var name = string.Empty;
            for (var axis = 0; axis < axisCount; axis++) {
                name += (Sign(axis) > 0 ? "+" : "-") + letters[this[axis]];
            }
            return name;
        }
        public AxisMap Then(AxisMap next) => new(
            this[next.A0], Sign(next.A0) * next.S0,
            this[next.A1], Sign(next.A1) * next.S1,
            this[next.A2], Sign(next.A2) * next.S2
        );
    }

    // Grid's and Box's point group is generated by mirroring each in-play axis and swapping every pair of equal
    // extent, closed by breadth-first composition — the same closure a Box's cube symmetry always used, now shared
    // with Grid's rectangle/square case instead of a second hand-written generator list.
    private static Group EnumerateAxisGroup(int axisCount, int[] extents, string letters) {
        var generators = new List<AxisMap>();
        for (var axis = 0; axis < axisCount; axis++) {
            generators.Add(FlipAxis(axis));
        }
        for (var a = 0; a < axisCount; a++) {
            for (var b = a + 1; b < axisCount; b++) {
                if (extents[a] == extents[b]) { generators.Add(SwapAxes(a, b)); }
            }
        }
        var elements = new List<AxisMap> { AxisMap.Identity };
        var seen = new HashSet<AxisMap>(elements);
        for (var index = 0; index < elements.Count; index++) {
            foreach (var generator in generators) {
                var composed = elements[index].Then(generator);
                if (seen.Add(composed)) { elements.Add(composed); }
            }
        }
        return new(elements, axisCount, letters, extents);
    }

    private static AxisMap FlipAxis(int axis) => axis switch {
        0 => new(0, -1, 1, 1, 2, 1),
        1 => new(0, 1, 1, -1, 2, 1),
        _ => new(0, 1, 1, 1, 2, -1),
    };

    private static AxisMap SwapAxes(int a, int b) {
        Span<int> axis = [0, 1, 2];
        (axis[a], axis[b]) = (axis[b], axis[a]);
        return new(axis[0], 1, axis[1], 1, axis[2], 1);
    }

    private static (int[][] Images, string[] Names) MaterializeAxis(List<AxisMap> elements, int axisCount, string letters, int[] extents,
        IReadOnlyList<(int X, int Y, int Z)> coordinates, Dictionary<(int, int, int), int> indices) {
        var images = new int[elements.Count][];
        var names = new string[elements.Count];
        for (var element = 0; element < elements.Count; element++) {
            var map = elements[element];
            names[element] = map.Name(axisCount, letters);
            var image = new int[coordinates.Count];
            for (var cell = 0; cell < coordinates.Count; cell++) {
                var p = coordinates[cell];
                int[] source = [p.X, p.Y, p.Z];
                var target = new int[3];
                for (var axis = 0; axis < 3; axis++) {
                    var value = source[map[axis]];
                    target[axis] = (map.Sign(axis) > 0) ? value : (extents[map[axis]] - 1 - value);
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
        Permute(identityAxes, 0, permutations);

        var elements = new List<AxisMap>();
        foreach (var sign in new[] { 1, -1 }) {
            foreach (var permutation in permutations) {
                elements.Add(new(permutation[0], sign, permutation[1], sign, permutation[2], sign));
            }
        }
        elements.Sort((left, right) => left.IsIdentity(3) ? -1 : right.IsIdentity(3) ? 1 : 0);
        return new(elements, 3, "qrs", [0, 0, 0]);
    }

    private static (int[][] Images, string[] Names) MaterializeHex(List<AxisMap> elements, IReadOnlyList<(int X, int Y, int Z)> coordinates, Dictionary<(int, int, int), int> indices) {
        var images = new int[elements.Count][];
        var names = new string[elements.Count];
        for (var element = 0; element < elements.Count; element++) {
            var map = elements[element];
            names[element] = map.Name(3, "qrs");
            var image = new int[coordinates.Count];
            for (var cell = 0; cell < coordinates.Count; cell++) {
                // The symmetry walk is spelled over cube coordinates of the 60° axial basis; a cell's Eisenstein
                // (Q, R) maps to axial (Q, −R). KEEP IN SYNC with the inverse on the image lookup below.
                var (q, negatedR, _) = coordinates[cell];
                var r = -negatedR;
                var s = -q - r;
                int[] cube = [q, r, s];
                var target = new int[3];
                for (var axis = 0; axis < 3; axis++) {
                    target[axis] = map.Sign(axis) * cube[map[axis]];
                }
                image[cell] = indices[(target[0], -target[1], 0)];
            }
            images[element] = image;
        }
        return (images, names);
    }

    private static void Permute(Span<int> axes, int from, List<int[]> results) {
        if (from == axes.Length) {
            results.Add(axes.ToArray());
            return;
        }
        for (var index = from; index < axes.Length; index++) {
            (axes[from], axes[index]) = (axes[index], axes[from]);
            Permute(axes, from + 1, results);
            (axes[from], axes[index]) = (axes[index], axes[from]);
        }
    }

    // Authored friendlier names ("rot90" for whatever axis permutation a square grid's quarter turn is) resolve
    // through Element(string) alongside the canonical spelling; ElementName always answers the canonical form. The
    // validator already proved every alias names a real element, so a miss here can only mean the alias outlived
    // its topology's own recompile — install it defensively rather than throw.
    internal void InstallElementAliases(IReadOnlyList<TopologyElementAlias>? aliases) {
        m_elementAliases.Clear();
        foreach (var alias in aliases ?? []) {
            var canonical = Array.IndexOf(m_elementNames, alias.Element);
            if (canonical >= 0) {
                m_elementAliases[alias.Name] = canonical;
            }
        }
    }
}
