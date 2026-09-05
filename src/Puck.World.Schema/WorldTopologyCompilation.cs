using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>The closed topology shapes sharing the state lattice registry.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldTopologyKind>))]
public enum WorldTopologyKind : byte {
    /// <summary>A physical scalar field.</summary>
    Field,
    /// <summary>A rectangular board, indexed by y times width plus x.</summary>
    Grid,
    /// <summary>A cyclic sequence, indexed from zero.</summary>
    Ring,
    /// <summary>An axial hexagon, indexed in ascending r then q order.</summary>
    Hex,
    /// <summary>A box of width by layers by depth cells with the 26 space directions, indexed by (layer times depth
    /// plus z) times width plus x.</summary>
    Box,
}

/// <summary>The axes that wrap on a discrete grid.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldTopologyWrap>))]
public enum WorldTopologyWrap : byte {
    /// <summary>Neither axis wraps.</summary>
    None,
    /// <summary>The x axis wraps.</summary>
    X,
    /// <summary>The y axis wraps.</summary>
    Y,
    /// <summary>Both axes wrap.</summary>
    Both,
}

/// <summary>A compiled immutable adjacency table. Absent neighbours are -1. Direction names come from the
/// topology's own <see cref="IDiscreteLatticeTopology.Directions"/> when authored; the unauthored default matches
/// what every kind carried before that field existed — Grid N, NE, E, SE, S, SW, W, NW; Hex E, NE, NW, W, SW, SE;
/// Box the 26 in <see cref="BoxDirectionNames"/>; Ring forward and backward.</summary>
public sealed partial class CompiledWorldTopology {
    private readonly int[] m_neighbours;
    private readonly int[] m_opposite;
    private readonly string[] m_keys;
    private readonly CellName[] m_names;
    private readonly string[] m_directionNames;
    private readonly int m_width;
    private readonly int m_depth;
    private readonly int m_layers;
    private readonly FixedQ4816 m_layerHeight;
    private readonly WorldTopologyWrap m_wrap;
    private readonly FixedVector3 m_origin;
    private readonly FixedQ4816 m_cellSize;
    private readonly FixedQ4816 m_band;

    internal CompiledWorldTopology(WorldTopologyKind kind, int count, int directions, int[] neighbours, int[] opposite,
        int width, int depth, WorldTopologyWrap wrap, FixedVector3 origin, FixedQ4816 cellSize, FixedQ4816 band,
        int[][] images, string[] elementNames, int layers, FixedQ4816 layerHeight, string[] directionNames) {
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
        m_wrap = wrap;
        m_origin = origin;
        m_cellSize = cellSize;
        m_keys = new string[count];
        m_names = new CellName[count];
        for (var cell = 0; cell < count; cell++) {
            m_keys[cell] = cell.ToString(CultureInfo.InvariantCulture);
            m_names[cell] = CellName.Parse(m_keys[cell]);
        }
    }

    /// <summary>Gets a cell's key as a parsed cell name, without re-parsing.</summary>
    /// <param name="cell">The cell ordinal.</param>
    public CellName NameOf(int cell) => m_names[cell];

    /// <summary>Gets the shape.</summary>
    public WorldTopologyKind Kind { get; }
    /// <summary>Gets the number of cells.</summary>
    public int CellCount { get; }
    /// <summary>Gets the number of directions at each cell.</summary>
    public int DirectionCount { get; }
    /// <summary>Gets the declared minimum corner — the spatial frame a <see cref="Kind"/> of
    /// <see cref="WorldTopologyKind.Grid"/> resolves <see cref="TryCellOf"/>/<see cref="TryOffset"/> against.</summary>
    public FixedVector3 Origin => m_origin;
    /// <summary>Gets the declared cell edge, world units.</summary>
    public FixedQ4816 CellSize => m_cellSize;
    /// <summary>Gets the cell count along +X.</summary>
    public int Width => m_width;
    /// <summary>Gets the cell count along +Z.</summary>
    public int Depth => m_depth;

    /// <summary>Resolves the grid cell a world position falls in, X/Z only — a board carries one layer, so no
    /// height test applies. Only <see cref="WorldTopologyKind.Grid"/> carries a rectangular X/Z frame; every other
    /// kind answers <see langword="false"/>.</summary>
    /// <param name="position">The world position (a body's resolved pose).</param>
    /// <param name="cell">The resolved cell ordinal.</param>
    /// <returns>Whether the position lies over a declared cell.</returns>
    public bool TryCellOf(in FixedVector3 position, out int cell) {
        cell = -1;
        if (Kind is not (WorldTopologyKind.Grid or WorldTopologyKind.Box)) {
            return false;
        }
        var layer = 0;
        if (Kind == WorldTopologyKind.Box) {
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

    /// <summary>The 26 space directions of a <see cref="WorldTopologyKind.Box"/>: the grid's eight compass names in
    /// the layer, then each prefixed <c>U</c> (up one layer) and <c>D</c> (down one), with <c>U</c> and <c>D</c> alone
    /// for the vertical.</summary>
    public static readonly string[] BoxDirectionNames = [
        "N", "NE", "E", "SE", "S", "SW", "W", "NW",
        "U", "UN", "UNE", "UE", "USE", "US", "USW", "UW", "UNW",
        "D", "DN", "DNE", "DE", "DSE", "DS", "DSW", "DW", "DNW",
    ];

    /// <summary>Resolves the cell reached by moving <paramref name="dx"/>/<paramref name="dz"/> grid steps from
    /// <paramref name="cell"/>, wrapping the axes this topology declares — the arbitrary-offset sibling of
    /// <see cref="Neighbour"/>'s fixed eight directions, what a leaper (a knight, or a chess-variant piece with no
    /// ray shape) authors its reach against. Only <see cref="WorldTopologyKind.Grid"/> carries rectangular
    /// coordinates; every other kind answers <see langword="false"/>.</summary>
    /// <param name="cell">The source cell ordinal.</param>
    /// <param name="dx">The signed step along +X.</param>
    /// <param name="dz">The signed step along +Z.</param>
    /// <param name="result">The resolved cell ordinal.</param>
    /// <returns>Whether the offset lands on a declared cell.</returns>
    public bool TryOffset(int cell, int dx, int dz, out int result) {
        result = -1;
        if (Kind != WorldTopologyKind.Grid || (uint)cell >= (uint)CellCount) {
            return false;
        }
        var x = (cell % m_width) + dx;
        var z = (cell / m_width) + dz;
        if (m_wrap is WorldTopologyWrap.X or WorldTopologyWrap.Both) {
            x = ((x % m_width) + m_width) % m_width;
        }
        if (m_wrap is WorldTopologyWrap.Y or WorldTopologyWrap.Both) {
            z = ((z % m_depth) + m_depth) % m_depth;
        }
        if ((uint)x >= (uint)m_width || (uint)z >= (uint)m_depth) {
            return false;
        }
        result = (z * m_width) + x;
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
    /// asymmetrically-ordered direction table (a <see cref="WorldTopologyKind.Box"/>'s 26) still resolves correctly.</summary>
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
}

/// <summary>Validates and compiles discrete addressing independently from physical field simulation.</summary>
public static class WorldTopologyCompilation {
    /// <summary>The maximum cells in one discrete topology and board row.</summary>
    public const int MaxCells = 4096;
    /// <summary>The maximum named topologies in one document.</summary>
    public const int MaxTopologies = 16;
    /// <summary>The document-wide board storage ceiling — every declared topology at its own <see cref="MaxCells"/>.</summary>
    public const int MaxTotalCells = (MaxTopologies * MaxCells);
    /// <summary>The greatest axial hexagon radius whose cell count (<c>1 + 3r(r + 1)</c>) still fits
    /// <see cref="MaxCells"/>, computed rather than authored so the two bounds can never drift apart.</summary>
    public static readonly int MaxHexRadius = ComputeMaxHexRadius();
    /// <summary>The most directions an authored <see cref="IDiscreteLatticeTopology.Directions"/> list may declare —
    /// the bit width of the <c>long</c> mask <c>$match:</c>'s direction-mask facet packs one bit per direction into
    /// (<c>1L &lt;&lt; direction</c>), above a Box's unauthored 26 (the largest default set) so a custom vocabulary is
    /// never narrower than what every kind already carries.</summary>
    public const int MaxDirections = sizeof(long) * 8;

    private static int ComputeMaxHexRadius() {
        var radius = 0;
        while ((1L + (3L * (radius + 1) * (radius + 2))) <= MaxCells) {
            radius++;
        }
        return radius;
    }
    private static readonly ConditionalWeakTable<WorldStateLatticeTopology, CompiledWorldTopology> s_cache = new();

    /// <summary>Finds the physical topology, if any. Discrete boards never allocate a fluid field.</summary>
    /// <param name="state">The state section.</param>
    /// <returns>The first physical topology or null.</returns>
    public static WorldStateLatticeTopology.Field? FindPhysical(WorldStateSection? state) {
        var topologies = state?.Lattices;
        for (var index = 0; index < (topologies?.Count ?? 0); index++) {
            if (topologies![index] is WorldStateLatticeTopology.Field field) {
                return field;
            }
        }
        return null;
    }

    /// <summary>Finds and compiles a discrete topology by name.</summary>
    /// <param name="state">The state section.</param>
    /// <param name="name">The topology name.</param>
    /// <returns>The compiled topology, or null if absent or malformed.</returns>
    public static CompiledWorldTopology? Find(WorldStateSection? state, string name) {
        var topologies = state?.Lattices;
        for (var index = 0; index < (topologies?.Count ?? 0); index++) {
            var topology = topologies![index];
            if (topology is not null && topology.Name == name && TryValidate(topology, out _)) {
                return s_cache.GetValue(topology, Compile);
            }
        }
        return null;
    }

    /// <summary>Checks shape and representation bounds before adjacency allocation.</summary>
    /// <param name="topology">The declaration.</param>
    /// <param name="reason">The refusal reason.</param>
    /// <returns>Whether this is a valid discrete topology.</returns>
    public static bool TryValidate(WorldStateLatticeTopology topology, out string reason) => topology switch {
        WorldStateLatticeTopology.Grid grid => TryValidateGrid(grid, out reason),
        WorldStateLatticeTopology.Ring ring => TryValidateRing(ring, out reason),
        WorldStateLatticeTopology.Hex hex => TryValidateHex(hex, out reason),
        WorldStateLatticeTopology.Box box => TryValidateBox(box, out reason),
        _ => Refuse(out reason, "a discrete topology requires kind grid, ring, hex, or box"),
    };

    private static bool Refuse(out string reason, string detail) {
        reason = detail;
        return false;
    }

    private static bool TryValidateFootprint(int width, int depth, int layers, out string reason) {
        if (width < 1 || depth < 1 || layers < 1 || width > MaxCells || depth > MaxCells || layers > MaxCells || ((long)width * depth * layers) > MaxCells) {
            reason = $"a discrete topology requires 1..{MaxCells} cells along each declared axis and at most {MaxCells} cells total";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static bool TryValidateGrid(WorldStateLatticeTopology.Grid grid, out string reason) {
        if (!Enum.IsDefined(grid.Wrap)) {
            reason = $"grid.wrap '{grid.Wrap}' is not a defined WorldTopologyWrap";
            return false;
        }
        if (!TryValidateFootprint(grid.Width, grid.Depth, 1, out reason)) {
            return false;
        }
        if (!TryValidateFrame(grid.CellSize, grid.Origin, out reason)) {
            return false;
        }
        if (!FitsFixed(grid.Band) || grid.Band < 0f) {
            reason = $"grid.band must be a nonnegative Q48.16 half-extent (was {grid.Band})";
            return false;
        }
        return TryValidateDiscreteVocabulary(grid, grid.Width, grid.Depth, 1, grid.Wrap, out reason);
    }

    private static bool TryValidateRing(WorldStateLatticeTopology.Ring ring, out string reason) {
        if (!TryValidateFootprint(ring.Width, 1, 1, out reason)) {
            return false;
        }
        return TryValidateDiscreteVocabulary(ring, ring.Width, 1, 1, WorldTopologyWrap.None, out reason);
    }

    private static bool TryValidateHex(WorldStateLatticeTopology.Hex hex, out string reason) {
        if (hex.Radius < 0 || hex.Radius > MaxHexRadius) {
            reason = $"hex requires radius 0..{MaxHexRadius}";
            return false;
        }
        if ((1L + (3L * hex.Radius * (hex.Radius + 1))) > MaxCells) {
            reason = $"a discrete topology requires at most {MaxCells} cells total";
            return false;
        }
        return TryValidateDiscreteVocabulary(hex, 1, 1, 1, WorldTopologyWrap.None, out reason);
    }

    private static bool TryValidateBox(WorldStateLatticeTopology.Box box, out string reason) {
        if (!TryValidateFootprint(box.Width, box.Depth, box.Layers, out reason)) {
            return false;
        }
        if (!TryValidateFrame(box.CellSize, box.Origin, out reason)) {
            return false;
        }
        if (!float.IsFinite(box.LayerHeight) || FixedQ4816.FromDouble(box.LayerHeight) <= FixedQ4816.Zero) {
            reason = $"box requires a positive layerHeight (was {box.LayerHeight})";
            return false;
        }
        return TryValidateDiscreteVocabulary(box, box.Width, box.Depth, box.Layers, WorldTopologyWrap.None, out reason);
    }

    private static bool TryValidateDiscreteVocabulary<T>(T topology, int width, int depth, int layers, WorldTopologyWrap wrap, out string reason)
        where T : WorldStateLatticeTopology, IDiscreteLatticeTopology {
        if (topology.Directions is not null && !TryValidateDirections(topology.Kind, topology.Directions, width, depth, wrap, out reason)) {
            return false;
        }
        if (topology.ElementAliases is not null && !TryValidateElementAliases(topology.Kind, topology.ElementAliases, width, depth, layers, out reason)) {
            return false;
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>Checks an authored element-alias list: 1..<see cref="MaxDirections"/> entries, distinct alias names
    /// that are not themselves a canonical element name, and an <see cref="WorldTopologyElementAlias.Element"/> that
    /// names a real element of this kind's point group.</summary>
    private static bool TryValidateElementAliases(WorldTopologyKind kind, IReadOnlyList<WorldTopologyElementAlias> aliases, int width, int depth, int layers, out string reason) {
        if (aliases.Count is < 1 or > MaxDirections) {
            reason = $"elementAliases declares {aliases.Count} entries; 1..{MaxDirections} are admitted";
            return false;
        }
        var canonical = CompiledWorldTopology.ElementNames(kind, width, depth, layers);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alias in aliases) {
            if (alias is null || !CellName.TryParse(alias.Name, out _, out _) || Array.IndexOf(canonical, alias.Name) >= 0 || !names.Add(alias.Name)) {
                reason = "elementAliases requires a distinct name per entry that is not already a canonical element name";
                return false;
            }
            if (Array.IndexOf(canonical, alias.Element) < 0) {
                reason = $"elementAliases entry '{alias.Name}' names no element '{alias.Element}' of this topology's point group";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>Checks an authored direction vocabulary: 1..<see cref="MaxDirections"/> entries, distinct names and
    /// distinct nonzero steps, a Z step only on a <see cref="WorldTopologyKind.Box"/>, no Y step on a
    /// <see cref="WorldTopologyKind.Ring"/> (which has no second axis), a step magnitude under the wrapped axis'
    /// own width or depth (a Ring always wraps X) so <see cref="CompiledWorldTopology"/>'s modulo wrap never folds a
    /// step past the origin or onto itself, and every step's negation present as another entry — the closure
    /// <see cref="CompiledWorldTopology.Opposite"/> derivation requires so it never throws.</summary>
    private static bool TryValidateDirections(WorldTopologyKind kind, IReadOnlyList<WorldTopologyDirection> directions, int width, int depth, WorldTopologyWrap wrap, out string reason) {
        if (directions.Count is < 1 or > MaxDirections) {
            reason = $"directions declares {directions.Count} entries; 1..{MaxDirections} are admitted";
            return false;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        var steps = new HashSet<(int, int, int)>();
        foreach (var direction in directions) {
            if (direction is null || !CellName.TryParse(direction.Name, out _, out _) || !names.Add(direction.Name)) {
                reason = "directions requires a distinct, valid name per entry";
                return false;
            }
            if (direction.Z != 0 && kind != WorldTopologyKind.Box) {
                reason = $"direction '{direction.Name}' declares a layer step outside a box";
                return false;
            }
            if (kind == WorldTopologyKind.Ring && direction.Y != 0) {
                reason = $"direction '{direction.Name}' declares a row step on a ring, which has no second axis";
                return false;
            }
            if ((kind == WorldTopologyKind.Ring || wrap is WorldTopologyWrap.X or WorldTopologyWrap.Both) && Math.Abs(direction.X) >= width) {
                reason = $"direction '{direction.Name}' steps {direction.X} on a wrapped axis {width} wide; magnitude must be under the width";
                return false;
            }
            if (wrap is WorldTopologyWrap.Y or WorldTopologyWrap.Both && Math.Abs(direction.Y) >= depth) {
                reason = $"direction '{direction.Name}' steps {direction.Y} on a wrapped axis {depth} deep; magnitude must be under the depth";
                return false;
            }
            if ((direction.X == 0 && direction.Y == 0 && direction.Z == 0) || !steps.Add((direction.X, direction.Y, direction.Z))) {
                reason = $"direction '{direction.Name}' repeats another entry's step or is the zero step";
                return false;
            }
        }
        foreach (var direction in directions) {
            if (!steps.Contains((-direction.X, -direction.Y, -direction.Z))) {
                reason = $"direction '{direction.Name}' has no opposite step in the same list";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    private static bool FitsFixed(float value) => (
        float.IsFinite(f: value) &&
        (value >= (((double)long.MinValue) / 65536.0)) &&
        (value <= (((double)long.MaxValue) / 65536.0))
    );

    /// <summary>Checks the spatial frame <see cref="CompiledWorldTopology.TryCellOf"/> resolves positions against —
    /// it divides world-local coordinates by <c>cellSize</c>, so a non-positive or unrepresentable edge is
    /// load-bearing, not cosmetic, and must be refused here rather than crashing or resolving garbage cells on the
    /// per-tick rule path.</summary>
    private static bool TryValidateFrame(float cellSize, DocumentVector3 origin, out string reason) {
        if (!FitsFixed(cellSize) || FixedQ4816.FromDouble(cellSize) <= FixedQ4816.Zero) {
            reason = $"cellSize must quantize to a positive Q48.16 value (was {cellSize})";
            return false;
        }
        if (!FitsFixed(origin.X) || !FitsFixed(origin.Y) || !FitsFixed(origin.Z)) {
            reason = "origin must fit Q48.16";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>Every case normalizes to the same (width, depth, layers, wrap, band, layerHeight, radius, directions,
    /// elementAliases) tuple <see cref="CompiledWorldTopology"/>'s own flat, kind-agnostic representation already
    /// expects (and <c>WorldRuntimeStateHash.AppendDiscreteTopologies</c> hashes on the same terms the flat record
    /// this split replaces always did) — the seam between the per-kind authored union above and every kind-agnostic
    /// reader below, which this split does not otherwise touch.</summary>
    public static (int Width, int Depth, int Layers, WorldTopologyWrap Wrap, float Band, float LayerHeight, int Radius,
        IReadOnlyList<WorldTopologyDirection>? Directions, IReadOnlyList<WorldTopologyElementAlias>? ElementAliases) Normalize(WorldStateLatticeTopology topology) => topology switch {
            WorldStateLatticeTopology.Field field => (field.Width, field.Depth, field.Layers, WorldTopologyWrap.None, 0f, 0f, 0, null, null),
            WorldStateLatticeTopology.Grid grid => (grid.Width, grid.Depth, 1, grid.Wrap, grid.Band, 0f, 0, grid.Directions, grid.ElementAliases),
            WorldStateLatticeTopology.Ring ring => (ring.Width, 1, 1, WorldTopologyWrap.None, 0f, 0f, 0, ring.Directions, ring.ElementAliases),
            WorldStateLatticeTopology.Hex hex => (1, 1, 1, WorldTopologyWrap.None, 0f, 0f, hex.Radius, hex.Directions, hex.ElementAliases),
            WorldStateLatticeTopology.Box box => (box.Width, box.Depth, box.Layers, WorldTopologyWrap.None, 0f, box.LayerHeight, 0, box.Directions, box.ElementAliases),
            _ => throw new InvalidOperationException($"'{topology.Kind}' is not a defined WorldTopologyKind"),
        };

    // Cells are (X, Y, Z) triples: a grid or ring keeps Z at 0 and Y as its depth axis, a hex uses (q, r), a box
    // fills layers along Z. Every kind's directions are steps in the same triple, so one neighbour loop serves all.
    private static CompiledWorldTopology Compile(WorldStateLatticeTopology topology) {
        var (width, depth, layers, wrap, band, layerHeight, radius, authoredDirections, elementAliases) = Normalize(topology);
        var coordinates = new List<(int X, int Y, int Z)>();
        if (topology.Kind == WorldTopologyKind.Hex) {
            for (var r = -radius; r <= radius; r++) {
                for (var q = Math.Max(-radius, -r - radius); q <= Math.Min(radius, -r + radius); q++) {
                    coordinates.Add((q, r, 0));
                }
            }
        } else {
            for (var layer = 0; layer < layers; layer++) {
                for (var y = 0; y < depth; y++) {
                    for (var x = 0; x < width; x++) {
                        coordinates.Add((x, y, layer));
                    }
                }
            }
        }
        (int X, int Y, int Z)[] directions;
        string[] directionNames;
        if (authoredDirections is { Count: > 0 } authored) {
            directions = new (int, int, int)[authored.Count];
            directionNames = new string[authored.Count];
            for (var index = 0; index < authored.Count; index++) {
                directions[index] = (authored[index].X, authored[index].Y, authored[index].Z);
                directionNames[index] = authored[index].Name;
            }
        } else {
            var planar = new (int X, int Y, int Z)[] { (0, -1, 0), (1, -1, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0), (-1, 1, 0), (-1, 0, 0), (-1, -1, 0) };
            (directions, directionNames) = topology.Kind switch {
                WorldTopologyKind.Grid => (planar, new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }),
                WorldTopologyKind.Hex => ([(1, 0, 0), (1, -1, 0), (0, -1, 0), (-1, 0, 0), (-1, 1, 0), (0, 1, 0)], new[] { "E", "NE", "NW", "W", "SW", "SE" }),
                WorldTopologyKind.Box => ([.. planar, (0, 0, 1), .. planar.Select(p => (p.X, p.Y, 1)), (0, 0, -1), .. planar.Select(p => (p.X, p.Y, -1))], CompiledWorldTopology.BoxDirectionNames),
                _ => ([(1, 0, 0), (-1, 0, 0)], new[] { "forward", "backward" }),
            };
        }
        var indices = new Dictionary<(int, int, int), int>();
        for (var index = 0; index < coordinates.Count; index++) {
            indices.Add(coordinates[index], index);
        }
        var neighbours = new int[coordinates.Count * directions.Length];
        for (var cell = 0; cell < coordinates.Count; cell++) {
            for (var direction = 0; direction < directions.Length; direction++) {
                var x = coordinates[cell].X + directions[direction].X;
                var y = coordinates[cell].Y + directions[direction].Y;
                var z = coordinates[cell].Z + directions[direction].Z;
                if (topology.Kind == WorldTopologyKind.Ring || wrap is WorldTopologyWrap.X or WorldTopologyWrap.Both) {
                    x = (x + width) % width;
                }
                if (wrap is WorldTopologyWrap.Y or WorldTopologyWrap.Both) {
                    y = (y + depth) % depth;
                }
                neighbours[cell * directions.Length + direction] = indices.TryGetValue((x, y, z), out var next) ? next : -1;
            }
        }
        var opposite = new int[directions.Length];
        for (var direction = 0; direction < directions.Length; direction++) {
            var negated = (X: -directions[direction].X, Y: -directions[direction].Y, Z: -directions[direction].Z);
            var found = Array.IndexOf(directions, negated);
            if (found < 0) {
                throw new InvalidOperationException($"{topology.Kind} direction {direction} has no opposite in its own direction table.");
            }
            opposite[direction] = found;
        }
        var (images, elementNames) = CompiledWorldTopology.BuildSymmetry(topology.Kind, width, depth, layers, coordinates, indices);
        var compiled = new CompiledWorldTopology(topology.Kind, coordinates.Count, directions.Length, neighbours, opposite, width, depth, wrap,
            new FixedVector3(
                X: FixedQ4816.FromDouble(topology.Origin.X),
                Y: FixedQ4816.FromDouble(topology.Origin.Y),
                Z: FixedQ4816.FromDouble(topology.Origin.Z)
            ),
            FixedQ4816.FromDouble(topology.CellSize),
            FixedQ4816.FromDouble(band),
            images,
            elementNames,
            layers,
            FixedQ4816.FromDouble(layerHeight),
            directionNames);
        compiled.InstallElementAliases(elementAliases);
        return compiled;
    }
}
