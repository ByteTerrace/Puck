using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;

namespace Puck.State;

/// <summary>The closed topology shapes sharing the state lattice registry.</summary>
[JsonConverter(typeof(StrictEnumConverter<TopologyKind>))]
public enum TopologyKind : byte {
    /// <summary>A dense scalar field over the footprint — one value per cell, evolved by the host; the discrete
    /// addressing below never applies to it.</summary>
    Field,
    /// <summary>A rectangular board, indexed by y times width plus x.</summary>
    Grid,
    /// <summary>A cyclic sequence, indexed from zero.</summary>
    Ring,
    /// <summary>A hexagonal disk of the given radius, indexed in <c>HexagonalIndex</c> order: rings outward from the origin, consecutive indices adjacent.</summary>
    Hex,
    /// <summary>An explicit graph: authored cells with centres and directed edges along named direction slots — a
    /// territory map, a star board, or any tiling a tool emits — with no axial structure and identity symmetry alone.</summary>
    Graph,
    /// <summary>A box of width by layers by depth cells with the 26 space directions, indexed by (layer times depth
    /// plus z) times width plus x.</summary>
    Box,
}

/// <summary>The axes that wrap on a discrete grid.</summary>
[JsonConverter(typeof(StrictEnumConverter<TopologyWrap>))]
public enum TopologyWrap : byte {
    /// <summary>Neither axis wraps.</summary>
    None,
    /// <summary>The x axis wraps.</summary>
    X,
    /// <summary>The y axis wraps.</summary>
    Y,
    /// <summary>Both axes wrap.</summary>
    Both,
}
/// <summary>Shared shape for the four discrete topology cases — their own neighbour/element vocabulary, so a
/// validator or reader that must accept any of them (<see cref="TopologyCompilation.TryValidate"/>) needs no
/// type-pattern switch enumerating every discrete kind.</summary>
public interface IDiscreteLatticeTopology {
    /// <summary>The topology's own direction vocabulary — its neighbour steps and the compass/element names a rule,
    /// <c>$board:</c>/<c>$match:</c> token, or leaper reach spells them with. <see langword="null"/> (the unauthored
    /// default) compiles to the fixed set each <see cref="TopologyKind"/> carries — Grid's eight compass points,
    /// Hex's six, Box's 26, Ring's forward/backward. An authored list replaces that default wholesale: it becomes the
    /// topology's only directions, its only compass names, and the only vocabulary
    /// <see cref="CompiledTopology.Direction"/> resolves — the seam a 4-connected grid (orthogonal steps only) or a
    /// custom leaper reach declares without inventing a parallel mechanism.</summary>
    IReadOnlyList<TopologyDirection>? Directions { get; }
    /// <summary>Friendlier names for this topology's point-group elements (<c>"rot90"</c> for whatever signed-axis
    /// permutation a square grid's quarter turn spells) — <see cref="CompiledTopology.Element"/> resolves an alias
    /// alongside the canonical name; <see cref="CompiledTopology.ElementName"/> always answers the canonical
    /// spelling.</summary>
    IReadOnlyList<TopologyElementAlias>? ElementAliases { get; }
}

/// <summary>One <c>state.lattices</c> topology — the footprint every lattice-shaped state row referencing it
/// shares. The discrete cases are declared here, one per kind, each carrying only its own parameters —
/// <see cref="Hex.Radius"/>, <see cref="Grid.Band"/>, and <see cref="Box.LayerHeight"/> exist nowhere else. A host
/// registers its dense <see cref="TopologyKind.Field"/> case as a further derived record under the same
/// <c>$type</c> discriminator. <see cref="Kind"/> is never authored directly; it is the discriminant every case's
/// own JSON <c>$type</c> already carries, exposed as a property because so much of the compiler branches on it.</summary>
/// <param name="Name">The topology's name — what a row's <c>cellsOf</c> domain references.</param>
/// <param name="Origin">The minimum corner, world units.</param>
/// <param name="CellSize">The cubic cell edge, world units.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Grid), typeDiscriminator: "grid")]
[JsonDerivedType(typeof(Ring), typeDiscriminator: "ring")]
[JsonDerivedType(typeof(Hex), typeDiscriminator: "hex")]
[JsonDerivedType(typeof(Box), typeDiscriminator: "box")]
[JsonDerivedType(typeof(Graph), typeDiscriminator: "graph")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LatticeTopology(string Name, DocumentVector3 Origin, float CellSize) {
    /// <summary>A dense field or a discrete grid, ring, hex, or box — the case's own kind, never authored.</summary>
    [JsonIgnore]
    public abstract TopologyKind Kind { get; }

    /// <summary>A rectangular board, indexed by y times width plus x.</summary>
    /// <param name="Name">The topology's name.</param>
    /// <param name="Origin">The minimum corner, world units.</param>
    /// <param name="CellSize">The cubic cell edge, world units.</param>
    /// <param name="Width">Cells along +X.</param>
    /// <param name="Depth">Cells along +Z.</param>
    /// <param name="Wrap">Wrapped axes.</param>
    /// <param name="Band">The vertical half-extent about the origin's Y a position must lie within to resolve to a
    /// cell (<c>cellOf</c>); 0 resolves any height, so a piece on the floor beneath a table still reads as on its
    /// square.</param>
    /// <param name="Directions">See <see cref="IDiscreteLatticeTopology.Directions"/>.</param>
    /// <param name="ElementAliases">See <see cref="IDiscreteLatticeTopology.ElementAliases"/>.</param>
    public sealed record Grid(
        string Name, DocumentVector3 Origin, float CellSize,
        int Width, int Depth, TopologyWrap Wrap = TopologyWrap.None, float Band = 0f,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TopologyDirection>? Directions = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TopologyElementAlias>? ElementAliases = null
    ) : LatticeTopology(Name, Origin, CellSize), IDiscreteLatticeTopology {
        /// <inheritdoc/>
        [JsonIgnore]
        public override TopologyKind Kind => TopologyKind.Grid;
    }

    /// <summary>A cyclic sequence, indexed from zero — always wraps.</summary>
    /// <param name="Name">The topology's name.</param>
    /// <param name="Origin">The minimum corner, world units.</param>
    /// <param name="CellSize">The cubic cell edge, world units.</param>
    /// <param name="Width">The cycle length.</param>
    /// <param name="Directions">See <see cref="IDiscreteLatticeTopology.Directions"/>.</param>
    /// <param name="ElementAliases">See <see cref="IDiscreteLatticeTopology.ElementAliases"/>.</param>
    public sealed record Ring(
        string Name, DocumentVector3 Origin, float CellSize, int Width,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TopologyDirection>? Directions = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TopologyElementAlias>? ElementAliases = null
    ) : LatticeTopology(Name, Origin, CellSize), IDiscreteLatticeTopology {
        /// <inheritdoc/>
        [JsonIgnore]
        public override TopologyKind Kind => TopologyKind.Ring;
    }

    /// <summary>A hexagonal disk of the given radius, indexed in <c>HexagonalIndex</c> order: rings outward from the origin, consecutive indices adjacent.</summary>
    /// <param name="Name">The topology's name.</param>
    /// <param name="Origin">The minimum corner, world units.</param>
    /// <param name="CellSize">The cubic cell edge, world units.</param>
    /// <param name="Radius">The axial hexagon radius.</param>
    /// <param name="Directions">See <see cref="IDiscreteLatticeTopology.Directions"/>.</param>
    /// <param name="ElementAliases">See <see cref="IDiscreteLatticeTopology.ElementAliases"/>.</param>
    public sealed record Hex(
        string Name, DocumentVector3 Origin, float CellSize, int Radius,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TopologyDirection>? Directions = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TopologyElementAlias>? ElementAliases = null
    ) : LatticeTopology(Name, Origin, CellSize), IDiscreteLatticeTopology {
        /// <inheritdoc/>
        [JsonIgnore]
        public override TopologyKind Kind => TopologyKind.Hex;
    }

    /// <summary>A box of width by layers by depth cells with the 26 space directions, indexed by (layer times depth
    /// plus z) times width plus x.</summary>
    /// <param name="Name">The topology's name.</param>
    /// <param name="Origin">The minimum corner, world units.</param>
    /// <param name="CellSize">The cubic cell edge, world units.</param>
    /// <param name="Width">Cells along +X.</param>
    /// <param name="Depth">Cells along +Z.</param>
    /// <param name="Layers">Cells along +Y.</param>
    /// <param name="LayerHeight">The world-space height of one layer, so a position's Y above the origin resolves to
    /// a layer the way X and Z resolve to a column; positive.</param>
    /// <param name="Directions">See <see cref="IDiscreteLatticeTopology.Directions"/>.</param>
    /// <param name="ElementAliases">See <see cref="IDiscreteLatticeTopology.ElementAliases"/>.</param>
    public sealed record Box(
        string Name, DocumentVector3 Origin, float CellSize,
        int Width, int Depth, int Layers, float LayerHeight,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TopologyDirection>? Directions = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TopologyElementAlias>? ElementAliases = null
    ) : LatticeTopology(Name, Origin, CellSize), IDiscreteLatticeTopology {
        /// <inheritdoc/>
        [JsonIgnore]
        public override TopologyKind Kind => TopologyKind.Box;
    }
    /// <summary>An explicit graph. Cell <c>i</c> is <c>cells[i]</c>; its centre is relative to <c>origin</c> in world
    /// units, and <c>cellSize</c> is the resolution radius — a position within half a cell size of a centre resolves to
    /// it, nearest first. Every direction is a slot: an edge from a cell along a direction fills that slot, and unless
    /// <see cref="GraphEdge.OneWay"/> also fills the destination's slot along the direction's opposite, so a cell with
    /// k neighbours needs k distinct directions among its edges. Symmetry is the identity alone.</summary>
    /// <param name="Name">The topology's name.</param>
    /// <param name="Origin">The frame every centre is relative to.</param>
    /// <param name="CellSize">The resolution diameter of a cell, in world units.</param>
    /// <param name="Cells">The cells, in ordinal order.</param>
    /// <param name="Directions">The direction slots, each naming its opposite (possibly itself).</param>
    /// <param name="Edges">The edges.</param>
    public sealed record Graph(
        string Name, DocumentVector3 Origin, float CellSize,
        IReadOnlyList<GraphCell> Cells, IReadOnlyList<GraphDirection> Directions, IReadOnlyList<GraphEdge> Edges
    ) : LatticeTopology(Name, Origin, CellSize) {
        /// <inheritdoc/>
        [JsonIgnore]
        public override TopologyKind Kind => TopologyKind.Graph;
    }
}
/// <summary>One authored direction of a discrete <see cref="LatticeTopology"/>: the (X, Y, Z) cell step a
/// neighbour walk, ray, or leaper offset takes, and the case-sensitive token a rule or <c>$board:</c>/<c>$match:</c>
/// channel names it by. <paramref name="X"/>/<paramref name="Y"/> are the topology's own planar axes (a Grid's
/// column/row, a Hex's q/r); <paramref name="Z"/> is a Box's layer step and must be zero on every other kind.</summary>
/// <param name="Name">The direction's token — dot-free, distinct within the topology, matched case-sensitively.</param>
/// <param name="X">The signed planar column step.</param>
/// <param name="Y">The signed planar row step.</param>
/// <param name="Z">The signed layer step; zero except on a <see cref="TopologyKind.Box"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TopologyDirection(string Name, int X, int Y, int Z = 0);
/// <summary>A friendlier name for one point-group element, resolved by <see cref="CompiledTopology.Element"/>
/// alongside its canonical signed-axis spelling (<see cref="CompiledTopology.ElementName"/> always answers the
/// canonical form).</summary>
/// <param name="Name">The alias token a rule or console verb may use instead of <paramref name="Element"/>.</param>
/// <param name="Element">The canonical element name (a <see cref="CompiledTopology.ElementName"/> value) this
/// alias resolves to.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TopologyElementAlias(string Name, string Element);

/// <summary>One cell of a <see cref="LatticeTopology.Graph"/>.</summary>
/// <param name="Id">The id edges name it by; distinct within the graph.</param>
/// <param name="Centre">The centre, relative to the topology's origin, in world units.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GraphCell(string Id, DocumentVector3 Centre);
/// <summary>One direction slot of a <see cref="LatticeTopology.Graph"/>.</summary>
/// <param name="Name">The direction's name, as rules and patterns spell it.</param>
/// <param name="Opposite">The direction an edge is followed back along; a symmetric link names itself.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GraphDirection(string Name, string Opposite);
/// <summary>One edge of a <see cref="LatticeTopology.Graph"/>: <paramref name="From"/> reaches <paramref name="To"/>
/// along <paramref name="Direction"/>, and unless <paramref name="OneWay"/>, <paramref name="To"/> reaches
/// <paramref name="From"/> along the direction's opposite.</summary>
/// <param name="From">The source cell id.</param>
/// <param name="To">The destination cell id.</param>
/// <param name="Direction">The direction slot.</param>
/// <param name="OneWay">Whether the reverse edge is left unfilled (a ladder, a one-way street).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GraphEdge(string From, string To, string Direction, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool OneWay = false);
