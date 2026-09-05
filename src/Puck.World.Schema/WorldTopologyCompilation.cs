using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
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

/// <summary>Addresses a keyed numeric state row by cells of a named discrete topology.</summary>
/// <param name="Topology">The name in state.lattices.</param>
/// <param name="Empty">The raw value of an unoccupied or unauthored cell.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldStateBoard(string Topology, long Empty = 0);

/// <summary>A compiled immutable adjacency table. Absent neighbours are -1. Direction names come from the
/// topology's own <see cref="WorldStateLatticeTopology.Directions"/> when authored; the unauthored default matches
/// what every kind carried before that field existed — Grid N, NE, E, SE, S, SW, W, NW; Hex E, NE, NW, W, SW, SE;
/// Box the 26 in <see cref="BoxDirectionNames"/>; Ring forward and backward.</summary>
public sealed partial class CompiledWorldTopology {
    private readonly int[] m_neighbours;
    private readonly int[] m_opposite;
    private readonly string[] m_keys;
    private readonly WorldCellName[] m_names;
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
        m_names = new WorldCellName[count];
        for (var cell = 0; cell < count; cell++) {
            m_keys[cell] = cell.ToString(CultureInfo.InvariantCulture);
            m_names[cell] = WorldCellName.Parse(m_keys[cell]);
        }
    }

    /// <summary>Gets a cell's key as a parsed cell name, without re-parsing.</summary>
    /// <param name="cell">The cell ordinal.</param>
    public WorldCellName CellName(int cell) => m_names[cell];

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
    /// <see cref="WorldStateLatticeTopology.Directions"/> was declared, its kind's default names otherwise.</summary>
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
    /// <summary>The most directions an authored <see cref="WorldStateLatticeTopology.Directions"/> list may declare —
    /// above a Box's unauthored 26 (the largest default set), so a custom vocabulary is never narrower than what
    /// every kind already carries, while a per-cell adjacency table stays bounded.</summary>
    public const int MaxDirections = 32;

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
    public static WorldStateLatticeTopology? FindPhysical(WorldStateSection? state) {
        var topologies = state?.Lattices;
        for (var index = 0; index < (topologies?.Count ?? 0); index++) {
            var topology = topologies![index];
            if (topology?.Kind == WorldTopologyKind.Field) {
                return topology;
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
    public static bool TryValidate(WorldStateLatticeTopology topology, out string reason) {
        reason = "a discrete topology requires a defined kind, at most 4096 cells, one layer (a box any number), and no physical reactions";
        if (topology.Kind is not (WorldTopologyKind.Grid or WorldTopologyKind.Ring or WorldTopologyKind.Hex or WorldTopologyKind.Box) ||
            !Enum.IsDefined(topology.Wrap) || (topology.Layers != 1 && topology.Kind != WorldTopologyKind.Box) || topology.Layers < 1 || topology.Reactions is { Count: > 0 } ||
            topology.Width < 1 || topology.Depth < 1 || topology.Width > MaxCells || topology.Depth > MaxCells || topology.Layers > MaxCells) {
            return false;
        }
        var count = (long)topology.Width * topology.Depth * topology.Layers;
        if (topology.Kind == WorldTopologyKind.Box) {
            if (topology.Wrap != WorldTopologyWrap.None || topology.Band != 0f || !float.IsFinite(topology.LayerHeight) || FixedQ4816.FromDouble(topology.LayerHeight) <= FixedQ4816.Zero) {
                reason = "box requires no wrapping, no band, and a positive layerHeight";
                return false;
            }
        } else if (topology.LayerHeight != 0f) {
            reason = "layerHeight belongs to a box";
            return false;
        }
        if (topology.Kind == WorldTopologyKind.Hex) {
            if (topology.Radius < 0 || topology.Radius > MaxHexRadius || topology.Wrap != WorldTopologyWrap.None || topology.Width != 1 || topology.Depth != 1) {
                reason = $"hex requires radius 0..{MaxHexRadius}, default width/depth, and no wrapping";
                return false;
            }
            count = 1L + 3L * topology.Radius * (topology.Radius + 1);
        } else if (topology.Radius != 0 || (topology.Kind == WorldTopologyKind.Ring && (topology.Depth != 1 || topology.Wrap != WorldTopologyWrap.None))) {
            reason = "radius belongs to hex; rings require depth 1 and wrap implicitly";
            return false;
        }
        if (count > MaxCells) {
            return false;
        }
        if (topology.Kind is WorldTopologyKind.Grid or WorldTopologyKind.Box && !TryValidateFrame(topology, out reason)) {
            return false;
        }
        if (topology.Directions is not null && !TryValidateDirections(topology, out reason)) {
            return false;
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>Checks an authored direction vocabulary: 1..<see cref="MaxDirections"/> entries, distinct names and
    /// distinct nonzero steps, a Z step only on a <see cref="WorldTopologyKind.Box"/>, and every step's negation
    /// present as another entry — the closure <see cref="CompiledWorldTopology.Opposite"/> derivation requires so it
    /// never throws.</summary>
    private static bool TryValidateDirections(WorldStateLatticeTopology topology, out string reason) {
        var directions = topology.Directions!;
        if (directions.Count is < 1 or > MaxDirections) {
            reason = $"directions declares {directions.Count} entries; 1..{MaxDirections} are admitted";
            return false;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        var steps = new HashSet<(int, int, int)>();
        foreach (var direction in directions) {
            if (direction is null || !WorldCellName.TryParse(direction.Name, out _, out _) || !names.Add(direction.Name)) {
                reason = "directions requires a distinct, valid name per entry";
                return false;
            }
            if (direction.Z != 0 && topology.Kind != WorldTopologyKind.Box) {
                reason = $"direction '{direction.Name}' declares a layer step outside a box";
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

    /// <summary>Checks the spatial frame a <see cref="WorldTopologyKind.Grid"/> resolves <see cref="CompiledWorldTopology.TryCellOf"/>
    /// against — <see cref="CompiledWorldTopology.TryCellOf"/> divides world-local coordinates by <c>cellSize</c>, so a
    /// non-positive or unrepresentable edge is load-bearing, not cosmetic, and must be refused here rather than crashing or
    /// resolving garbage cells on the per-tick rule path.</summary>
    private static bool TryValidateFrame(WorldStateLatticeTopology topology, out string reason) {
        static bool FitsFixed(float value) => (
            float.IsFinite(f: value) &&
            (value >= (((double)long.MinValue) / 65536.0)) &&
            (value <= (((double)long.MaxValue) / 65536.0))
        );
        if (!FitsFixed(topology.CellSize) || FixedQ4816.FromDouble(topology.CellSize) <= FixedQ4816.Zero) {
            reason = $"grid.cellSize must quantize to a positive Q48.16 value (was {topology.CellSize})";
            return false;
        }
        if (!FitsFixed(topology.Origin.X) || !FitsFixed(topology.Origin.Y) || !FitsFixed(topology.Origin.Z)) {
            reason = "grid.origin must fit Q48.16";
            return false;
        }
        if (!FitsFixed(topology.Band) || topology.Band < 0f) {
            reason = $"grid.band must be a nonnegative Q48.16 half-extent (was {topology.Band})";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    // Cells are (X, Y, Z) triples: a grid or ring keeps Z at 0 and Y as its depth axis, a hex uses (q, r), a box
    // fills layers along Z. Every kind's directions are steps in the same triple, so one neighbour loop serves all.
    private static CompiledWorldTopology Compile(WorldStateLatticeTopology topology) {
        var coordinates = new List<(int X, int Y, int Z)>();
        if (topology.Kind == WorldTopologyKind.Hex) {
            var radius = topology.Radius;
            for (var r = -radius; r <= radius; r++) {
                for (var q = Math.Max(-radius, -r - radius); q <= Math.Min(radius, -r + radius); q++) {
                    coordinates.Add((q, r, 0));
                }
            }
        } else {
            for (var layer = 0; layer < topology.Layers; layer++) {
                for (var y = 0; y < topology.Depth; y++) {
                    for (var x = 0; x < topology.Width; x++) {
                        coordinates.Add((x, y, layer));
                    }
                }
            }
        }
        (int X, int Y, int Z)[] directions;
        string[] directionNames;
        if (topology.Directions is { Count: > 0 } authored) {
            directions = new (int, int, int)[authored.Count];
            directionNames = new string[authored.Count];
            for (var index = 0; index < authored.Count; index++) {
                directions[index] = (authored[index].X, authored[index].Y, authored[index].Z);
                directionNames[index] = authored[index].Name;
            }
        } else {
            var planar = new (int X, int Y, int Z)[] { (0,-1,0),(1,-1,0),(1,0,0),(1,1,0),(0,1,0),(-1,1,0),(-1,0,0),(-1,-1,0) };
            (directions, directionNames) = topology.Kind switch {
                WorldTopologyKind.Grid => (planar, new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }),
                WorldTopologyKind.Hex => ([(1,0,0),(1,-1,0),(0,-1,0),(-1,0,0),(-1,1,0),(0,1,0)], new[] { "E", "NE", "NW", "W", "SW", "SE" }),
                WorldTopologyKind.Box => ([.. planar, (0,0,1), .. planar.Select(p => (p.X, p.Y, 1)), (0,0,-1), .. planar.Select(p => (p.X, p.Y, -1))], CompiledWorldTopology.BoxDirectionNames),
                _ => ([(1,0,0),(-1,0,0)], new[] { "forward", "backward" }),
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
                if (topology.Kind == WorldTopologyKind.Ring || topology.Wrap is WorldTopologyWrap.X or WorldTopologyWrap.Both) {
                    x = (x + topology.Width) % topology.Width;
                }
                if (topology.Wrap is WorldTopologyWrap.Y or WorldTopologyWrap.Both) {
                    y = (y + topology.Depth) % topology.Depth;
                }
                neighbours[cell * directions.Length + direction] = indices.TryGetValue((x,y,z), out var next) ? next : -1;
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
        var (images, elementNames) = CompiledWorldTopology.BuildSymmetry(topology.Kind, topology.Width, topology.Depth, topology.Layers, coordinates, indices);
        return new(topology.Kind, coordinates.Count, directions.Length, neighbours, opposite, topology.Width, topology.Depth, topology.Wrap,
            new FixedVector3(
                X: FixedQ4816.FromDouble(topology.Origin.X),
                Y: FixedQ4816.FromDouble(topology.Origin.Y),
                Z: FixedQ4816.FromDouble(topology.Origin.Z)
            ),
            FixedQ4816.FromDouble(topology.CellSize),
            FixedQ4816.FromDouble(topology.Band),
            images,
            elementNames,
            topology.Layers,
            FixedQ4816.FromDouble(topology.LayerHeight),
            directionNames);
    }
}
