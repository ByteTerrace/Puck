using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Assets.Documents;
using Puck.Maths;

namespace Puck.State;

/// <summary>Validates and compiles discrete addressing independently from dense field simulation.</summary>
public static class TopologyCompilation {
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
    // Unanchored compiles are a pure function of the topology instance, so one weak entry per instance serves every
    // reader; a host that anchors a topology's frame elsewhere keeps its own cache keyed on the anchor too.
    private static readonly ConditionalWeakTable<LatticeTopology, CompiledTopology> s_cache = new();

    /// <summary>Finds and compiles a discrete topology by name, unanchored: a Grid's <c>origin</c> resolves exactly
    /// as authored. A host that composes a topology's frame over another row's transform compiles through
    /// <see cref="Compile"/> with that offset instead.</summary>
    /// <param name="lattices">The section's lattice topologies, or <see langword="null"/> for none.</param>
    /// <param name="name">The topology name.</param>
    /// <returns>The compiled topology, or <see langword="null"/> if absent, dense, or malformed.</returns>
    public static CompiledTopology? Find(IReadOnlyList<LatticeTopology>? lattices, string name) {
        for (var index = 0; index < (lattices?.Count ?? 0); index++) {
            var topology = lattices![index];
            if (topology is not null && topology.Name == name && TryValidate(topology, out _)) {
                return s_cache.GetValue(
                    key: topology,
                    createValueCallback: static topology => Compile(topology: topology, anchorOffset: Vector3.Zero)
                );
            }
        }
        return null;
    }
    /// <summary>Finds and compiles a discrete topology by name over a section (see
    /// <see cref="Find(IReadOnlyList{LatticeTopology}?, string)"/>).</summary>
    /// <param name="section">The state section.</param>
    /// <param name="name">The topology name.</param>
    /// <returns>The compiled topology, or <see langword="null"/> if absent, dense, or malformed.</returns>
    public static CompiledTopology? Find(IStateSection? section, string name) => Find(lattices: section?.Lattices, name: name);

    /// <summary>Checks shape and representation bounds before adjacency allocation.</summary>
    /// <param name="topology">The declaration.</param>
    /// <param name="reason">The refusal reason.</param>
    /// <returns>Whether this is a valid discrete topology.</returns>
    public static bool TryValidate(LatticeTopology topology, out string reason) => topology switch {
        LatticeTopology.Grid grid => TryValidateGrid(grid, out reason),
        LatticeTopology.Ring ring => TryValidateRing(ring, out reason),
        LatticeTopology.Hex hex => TryValidateHex(hex, out reason),
        LatticeTopology.Box box => TryValidateBox(box, out reason),
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

    private static bool TryValidateGrid(LatticeTopology.Grid grid, out string reason) {
        if (!Enum.IsDefined(grid.Wrap)) {
            reason = $"grid.wrap '{grid.Wrap}' is not a defined TopologyWrap";
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

    private static bool TryValidateRing(LatticeTopology.Ring ring, out string reason) {
        if (!TryValidateFootprint(ring.Width, 1, 1, out reason)) {
            return false;
        }
        return TryValidateDiscreteVocabulary(ring, ring.Width, 1, 1, TopologyWrap.None, out reason);
    }

    private static bool TryValidateHex(LatticeTopology.Hex hex, out string reason) {
        if (hex.Radius < 0 || hex.Radius > MaxHexRadius) {
            reason = $"hex requires radius 0..{MaxHexRadius}";
            return false;
        }
        if ((1L + (3L * hex.Radius * (hex.Radius + 1))) > MaxCells) {
            reason = $"a discrete topology requires at most {MaxCells} cells total";
            return false;
        }
        return TryValidateDiscreteVocabulary(hex, 1, 1, 1, TopologyWrap.None, out reason);
    }

    private static bool TryValidateBox(LatticeTopology.Box box, out string reason) {
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
        return TryValidateDiscreteVocabulary(box, box.Width, box.Depth, box.Layers, TopologyWrap.None, out reason);
    }

    private static bool TryValidateDiscreteVocabulary<T>(T topology, int width, int depth, int layers, TopologyWrap wrap, out string reason)
        where T : LatticeTopology, IDiscreteLatticeTopology {
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
    /// that are not themselves a canonical element name, and an <see cref="TopologyElementAlias.Element"/> that
    /// names a real element of this kind's point group.</summary>
    private static bool TryValidateElementAliases(TopologyKind kind, IReadOnlyList<TopologyElementAlias> aliases, int width, int depth, int layers, out string reason) {
        if (aliases.Count is < 1 or > MaxDirections) {
            reason = $"elementAliases declares {aliases.Count} entries; 1..{MaxDirections} are admitted";
            return false;
        }
        var canonical = CompiledTopology.ElementNames(kind, width, depth, layers);
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
    /// distinct nonzero steps, a Z step only on a <see cref="TopologyKind.Box"/>, no Y step on a
    /// <see cref="TopologyKind.Ring"/> (which has no second axis), a step magnitude under the wrapped axis' own
    /// width or depth (a Ring always wraps X) so <see cref="CompiledTopology"/>'s modulo wrap never folds a step past
    /// the origin or onto itself, and every step's negation present as another entry — the closure
    /// <see cref="CompiledTopology.Opposite"/> derivation requires so it never throws.</summary>
    private static bool TryValidateDirections(TopologyKind kind, IReadOnlyList<TopologyDirection> directions, int width, int depth, TopologyWrap wrap, out string reason) {
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
            if (direction.Z != 0 && kind != TopologyKind.Box) {
                reason = $"direction '{direction.Name}' declares a layer step outside a box";
                return false;
            }
            if (kind == TopologyKind.Ring && direction.Y != 0) {
                reason = $"direction '{direction.Name}' declares a row step on a ring, which has no second axis";
                return false;
            }
            if ((kind == TopologyKind.Ring || wrap is TopologyWrap.X or TopologyWrap.Both) && Math.Abs(direction.X) >= width) {
                reason = $"direction '{direction.Name}' steps {direction.X} on a wrapped axis {width} wide; magnitude must be under the width";
                return false;
            }
            if (wrap is TopologyWrap.Y or TopologyWrap.Both && Math.Abs(direction.Y) >= depth) {
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

    /// <summary>Checks the spatial frame <see cref="CompiledTopology.TryCellOf"/> resolves positions against — it
    /// divides world-local coordinates by <c>cellSize</c>, so a non-positive or unrepresentable edge is load-bearing,
    /// not cosmetic, and must be refused here rather than crashing or resolving garbage cells on the per-tick rule
    /// path.</summary>
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

    /// <summary>Every discrete case normalizes to the same (width, depth, layers, wrap, band, layerHeight, radius,
    /// directions, elementAliases) tuple <see cref="CompiledTopology"/>'s own flat, kind-agnostic representation
    /// expects — the seam between the per-kind authored union and every kind-agnostic reader.</summary>
    /// <param name="topology">A discrete topology.</param>
    /// <exception cref="InvalidOperationException"><paramref name="topology"/> is not a discrete case.</exception>
    public static (int Width, int Depth, int Layers, TopologyWrap Wrap, float Band, float LayerHeight, int Radius,
        IReadOnlyList<TopologyDirection>? Directions, IReadOnlyList<TopologyElementAlias>? ElementAliases) Normalize(LatticeTopology topology) => topology switch {
            LatticeTopology.Grid grid => (grid.Width, grid.Depth, 1, grid.Wrap, grid.Band, 0f, 0, grid.Directions, grid.ElementAliases),
            LatticeTopology.Ring ring => (ring.Width, 1, 1, TopologyWrap.None, 0f, 0f, 0, ring.Directions, ring.ElementAliases),
            LatticeTopology.Hex hex => (1, 1, 1, TopologyWrap.None, 0f, 0f, hex.Radius, hex.Directions, hex.ElementAliases),
            LatticeTopology.Box box => (box.Width, box.Depth, box.Layers, TopologyWrap.None, 0f, box.LayerHeight, 0, box.Directions, box.ElementAliases),
            _ => throw new InvalidOperationException($"'{topology?.Kind}' is not a discrete TopologyKind"),
        };

    /// <summary>Compiles a validated discrete topology into its adjacency table, translating the authored origin by
    /// <paramref name="anchorOffset"/> — the frame a host composes over another row's transform, or zero for the
    /// authored frame. Translation only: the grid's own axes stay world-axis-aligned whatever the anchor's heading.</summary>
    /// <param name="topology">A discrete topology that passed <see cref="TryValidate"/>.</param>
    /// <param name="anchorOffset">The world-space translation added to the authored origin.</param>
    /// <returns>The compiled topology.</returns>
    public static CompiledTopology Compile(LatticeTopology topology, Vector3 anchorOffset) {
        ArgumentNullException.ThrowIfNull(argument: topology);

        // Cells are (X, Y, Z) triples: a grid or ring keeps Z at 0 and Y as its depth axis, a hex uses (q, r), a box
        // fills layers along Z. Every kind's directions are steps in the same triple, so one neighbour loop serves all.
        var (width, depth, layers, wrap, band, layerHeight, radius, authoredDirections, elementAliases) = Normalize(topology);
        var coordinates = new List<(int X, int Y, int Z)>();
        if (topology.Kind == TopologyKind.Hex) {
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
                TopologyKind.Grid => (planar, new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }),
                TopologyKind.Hex => ([(1, 0, 0), (1, -1, 0), (0, -1, 0), (-1, 0, 0), (-1, 1, 0), (0, 1, 0)], new[] { "E", "NE", "NW", "W", "SW", "SE" }),
                TopologyKind.Box => ([.. planar, (0, 0, 1), .. planar.Select(p => (p.X, p.Y, 1)), (0, 0, -1), .. planar.Select(p => (p.X, p.Y, -1))], CompiledTopology.BoxDirectionNames),
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
                if (topology.Kind == TopologyKind.Ring || wrap is TopologyWrap.X or TopologyWrap.Both) {
                    x = (x + width) % width;
                }
                if (wrap is TopologyWrap.Y or TopologyWrap.Both) {
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
        var (images, elementNames) = CompiledTopology.BuildSymmetry(topology.Kind, width, depth, layers, coordinates, indices);
        var compiled = new CompiledTopology(topology.Kind, coordinates.Count, directions.Length, neighbours, opposite, width, depth, wrap,
            new FixedVector3(
                X: FixedQ4816.FromDouble(topology.Origin.X + anchorOffset.X),
                Y: FixedQ4816.FromDouble(topology.Origin.Y + anchorOffset.Y),
                Z: FixedQ4816.FromDouble(topology.Origin.Z + anchorOffset.Z)
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
