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

/// <summary>A compiled immutable adjacency table. Absent neighbours are -1. Grid directions are N, NE, E, SE,
/// S, SW, W, NW; hex directions are E, NE, NW, W, SW, SE; ring directions are forward and backward.</summary>
public sealed class CompiledWorldTopology {
    private readonly int[] m_neighbours;
    private readonly string[] m_keys;
    private readonly int m_width;
    private readonly int m_depth;
    private readonly WorldTopologyWrap m_wrap;
    private readonly FixedVector3 m_origin;
    private readonly FixedQ4816 m_cellSize;

    internal CompiledWorldTopology(WorldTopologyKind kind, int count, int directions, int[] neighbours,
        int width, int depth, WorldTopologyWrap wrap, FixedVector3 origin, FixedQ4816 cellSize) {
        Kind = kind;
        CellCount = count;
        DirectionCount = directions;
        m_neighbours = neighbours;
        m_width = width;
        m_depth = depth;
        m_wrap = wrap;
        m_origin = origin;
        m_cellSize = cellSize;
        m_keys = new string[count];
        for (var cell = 0; cell < count; cell++) {
            m_keys[cell] = cell.ToString(CultureInfo.InvariantCulture);
        }
    }

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
        if (Kind != WorldTopologyKind.Grid) {
            return false;
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
        cell = ((int)z * m_width) + (int)x;
        return true;
    }

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

    /// <summary>Resolves a direction token for this topology.</summary>
    /// <param name="token">The case-sensitive direction name.</param>
    /// <returns>The direction ordinal or -1.</returns>
    public int Direction(string token) => Kind switch {
        WorldTopologyKind.Grid => token switch { "N" => 0, "NE" => 1, "E" => 2, "SE" => 3, "S" => 4, "SW" => 5, "W" => 6, "NW" => 7, _ => -1 },
        WorldTopologyKind.Hex => token switch { "E" => 0, "NE" => 1, "NW" => 2, "W" => 3, "SW" => 4, "SE" => 5, _ => -1 },
        WorldTopologyKind.Ring => token switch { "forward" => 0, "backward" => 1, _ => -1 },
        _ => -1,
    };
}

/// <summary>Validates and compiles discrete addressing independently from physical field simulation.</summary>
public static class WorldTopologyCompilation {
    /// <summary>The maximum cells in one discrete topology and board row.</summary>
    public const int MaxCells = 4096;
    /// <summary>The maximum named topologies in one document.</summary>
    public const int MaxTopologies = 16;
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
        reason = "a discrete topology requires a defined kind, at most 4096 cells, one layer, and no physical reactions";
        if (topology.Kind is not (WorldTopologyKind.Grid or WorldTopologyKind.Ring or WorldTopologyKind.Hex) ||
            !Enum.IsDefined(topology.Wrap) || topology.Layers != 1 || topology.Reactions is { Count: > 0 } ||
            topology.Width < 1 || topology.Depth < 1 || topology.Width > MaxCells || topology.Depth > MaxCells) {
            return false;
        }
        var count = (long)topology.Width * topology.Depth;
        if (topology.Kind == WorldTopologyKind.Hex) {
            if (topology.Radius < 0 || topology.Radius > 36 || topology.Wrap != WorldTopologyWrap.None || topology.Width != 1 || topology.Depth != 1) {
                reason = "hex requires radius 0..36, default width/depth, and no wrapping";
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
        if (topology.Kind == WorldTopologyKind.Grid && !TryValidateFrame(topology, out reason)) {
            return false;
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
        reason = string.Empty;
        return true;
    }

    private static CompiledWorldTopology Compile(WorldStateLatticeTopology topology) {
        var coordinates = new List<(int X, int Y)>();
        if (topology.Kind == WorldTopologyKind.Hex) {
            var radius = topology.Radius;
            for (var r = -radius; r <= radius; r++) {
                for (var q = Math.Max(-radius, -r - radius); q <= Math.Min(radius, -r + radius); q++) {
                    coordinates.Add((q, r));
                }
            }
        } else {
            for (var y = 0; y < topology.Depth; y++) {
                for (var x = 0; x < topology.Width; x++) {
                    coordinates.Add((x, y));
                }
            }
        }
        (int X, int Y)[] directions = topology.Kind switch {
            WorldTopologyKind.Grid => [(0,-1),(1,-1),(1,0),(1,1),(0,1),(-1,1),(-1,0),(-1,-1)],
            WorldTopologyKind.Hex => [(1,0),(1,-1),(0,-1),(-1,0),(-1,1),(0,1)],
            _ => [(1,0),(-1,0)],
        };
        var indices = new Dictionary<(int, int), int>();
        for (var index = 0; index < coordinates.Count; index++) {
            indices.Add(coordinates[index], index);
        }
        var neighbours = new int[coordinates.Count * directions.Length];
        for (var cell = 0; cell < coordinates.Count; cell++) {
            for (var direction = 0; direction < directions.Length; direction++) {
                var x = coordinates[cell].X + directions[direction].X;
                var y = coordinates[cell].Y + directions[direction].Y;
                if (topology.Kind == WorldTopologyKind.Ring || topology.Wrap is WorldTopologyWrap.X or WorldTopologyWrap.Both) {
                    x = (x + topology.Width) % topology.Width;
                }
                if (topology.Wrap is WorldTopologyWrap.Y or WorldTopologyWrap.Both) {
                    y = (y + topology.Depth) % topology.Depth;
                }
                neighbours[cell * directions.Length + direction] = indices.TryGetValue((x,y), out var next) ? next : -1;
            }
        }
        return new(topology.Kind, coordinates.Count, directions.Length, neighbours, topology.Width, topology.Depth, topology.Wrap,
            new FixedVector3(
                X: FixedQ4816.FromDouble(topology.Origin.X),
                Y: FixedQ4816.FromDouble(topology.Origin.Y),
                Z: FixedQ4816.FromDouble(topology.Origin.Z)
            ),
            FixedQ4816.FromDouble(topology.CellSize));
    }
}
