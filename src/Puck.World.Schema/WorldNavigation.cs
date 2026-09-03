using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.World;

/// <summary>The authored bounded surface, free-volume, and live-medium navigation domains available to body producers.</summary>
/// <param name="Domains">Finite rectangular domains, each compiled from the world's deterministic solid field.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldNavigationSection(IReadOnlyList<WorldNavigationDomain>? Domains = null) {
    /// <summary>Gets a section containing no navigation domains.</summary>
    public static WorldNavigationSection Absent { get; } = new();
    /// <summary>Gets the declared domain rows.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldNavigationDomain> Rows => (Domains ?? []);
}

/// <summary>The topology and occupancy constraint of an authored navigation domain.</summary>
[JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<WorldNavigationKind>))]
public enum WorldNavigationKind : byte {
    /// <summary>A 2D grid whose Y values are sampled from solid ground.</summary>
    Surface,
    /// <summary>A 3D grid of collision-free space.</summary>
    Volume,
    /// <summary>A 3D grid constrained to one live fluid-medium field.</summary>
    Medium,
}

/// <summary>The neighbour set used by a volume navigation domain.</summary>
[JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<WorldNavigationConnectivity>))]
public enum WorldNavigationConnectivity : byte {
    /// <summary>The six axis-aligned neighbours.</summary>
    Axis,
    /// <summary>Axis and two-axis diagonal neighbours.</summary>
    FacesAndEdges,
    /// <summary>All 26 neighbours, including three-axis diagonals.</summary>
    Full,
}

/// <summary>One finite navigation grid compiled from the world's collision and optional medium truth.</summary>
/// <param name="Name">The stable domain name referenced by navigated producer targets.</param>
/// <param name="Kind">Whether cells follow ground, free 3D space, or a live medium.</param>
/// <param name="Origin">The world-space center of cell (0,0,0); for a surface domain, Y is the ground-probe baseline.</param>
/// <param name="CellSize">The square cell width in world units.</param>
/// <param name="Width">The number of cells along world X.</param>
/// <param name="Depth">The number of cells along world Z.</param>
/// <param name="AgentRadius">The solid-clearance sphere radius. A medium domain also keeps this whole volume submerged.</param>
/// <param name="ArrivalDistance">How close a body must come before advancing to the next waypoint.</param>
/// <param name="MaxExpandedNodes">The hard A* expansion budget for one route search.</param>
/// <param name="MaxPathNodes">The hard stored-waypoint budget for one route.</param>
/// <param name="Layers">The number of cells along world Y; surface domains require one.</param>
/// <param name="Connectivity">The volume neighbour set; ignored by surface domains.</param>
/// <param name="ProbeUp">For a surface domain, the ground-search distance above origin Y.</param>
/// <param name="ProbeDown">For a surface domain, the ground-search distance below origin Y.</param>
/// <param name="AgentHeight">For a surface domain, the clearance capsule height.</param>
/// <param name="MaxStepHeight">For a surface domain, the greatest adjacent height delta.</param>
/// <param name="MaxSlopeDegrees">For a surface domain, the greatest adjacent slope.</param>
/// <param name="Medium">For a medium domain, the named lattice field that cells must remain inside.</param>
/// <param name="Shared">Optional shared reverse-search policy. Absent uses independent bounded A* searches.
/// Shared searches are queued and advanced on subsequent simulation ticks, independently of any leader.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldNavigationDomain(
    string Name,
    WorldNavigationKind Kind,
    Vector3 Origin,
    float CellSize,
    int Width,
    int Depth,
    float AgentRadius,
    float ArrivalDistance,
    int MaxExpandedNodes,
    int MaxPathNodes,
    int Layers = 1,
    WorldNavigationConnectivity Connectivity = WorldNavigationConnectivity.Full,
    float ProbeUp = 0f,
    float ProbeDown = 0f,
    float AgentHeight = 0f,
    float MaxStepHeight = 0f,
    float MaxSlopeDegrees = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Medium = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldNavigationSharing? Shared = null
);

/// <summary>Bounds reusable destination trees and their aggregate expansion work in one navigation domain.</summary>
/// <param name="GoalCapacity">Resident destination-cell trees. A full cache with pending work refuses another
/// destination as capacity-limited; it never launches an unbudgeted independent search.</param>
/// <param name="ExpandedNodesPerTick">Total reverse-Dijkstra expansions per simulation tick, shared fairly between
/// pending resident goals. Each expansion inspects at most 26 edges. A tree can eventually settle every domain cell;
/// the independent A* MaxExpandedNodes bound does not truncate a shared tree.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldNavigationSharing(int GoalCapacity, int ExpandedNodesPerTick);

/// <summary>Hard representation ceilings for authored navigation work and memory.</summary>
public static class WorldNavigationCapacity {
    /// <summary>The greatest number of named domains in one world.</summary>
    public const int MaxDomains = 16;
    /// <summary>The greatest cell count in one domain.</summary>
    public const int MaxCellsPerDomain = 65_536;
    /// <summary>The greatest total cell count compiled by one world.</summary>
    public const int MaxCellsPerWorld = 262_144;
    /// <summary>The greatest route length retained per body.</summary>
    public const int MaxPathNodes = 1_024;
    /// <summary>The greatest number of parallel sphere sweeps used to prove one tall surface-agent transition.</summary>
    public const int MaxSurfaceClearanceSweeps = 16;
    /// <summary>The greatest number of equal subsegments checked along one live medium edge.</summary>
    public const int MaxMediumSegmentSubdivisions = 32;
    /// <summary>The greatest resident destination-tree count per shared domain.</summary>
    public const int MaxSharedGoals = 16;
    /// <summary>The greatest sum of domain cells times resident shared goals; bounds boot workspace and checkpoints.</summary>
    public const int MaxSharedCellsPerWorld = 1_048_576;
    /// <summary>The greatest aggregate authored shared-tree expansion budget per simulation tick.</summary>
    public const int MaxSharedExpandedPerTick = 65_536;
}

/// <summary>The stable authored-name to navigation-domain ordinal table.</summary>
public sealed class WorldNavigationDomainTable {
    private readonly OrdinalTable m_table;
    private readonly WorldNavigationKind[] m_kinds;

    private WorldNavigationDomainTable(OrdinalTable table, WorldNavigationKind[] kinds) {
        m_table = table;
        m_kinds = kinds;
    }

    /// <summary>Gets an empty domain table.</summary>
    public static WorldNavigationDomainTable Empty { get; } = new(table: OrdinalTable.Empty, kinds: []);
    /// <summary>Gets the number of domains.</summary>
    public int Count => m_table.Count;
    /// <summary>Compiles authored order into stable ordinals.</summary>
    public static WorldNavigationDomainTable Compile(IReadOnlyList<WorldNavigationDomain> domains) => new(
        table: OrdinalTable.Build(
            names: domains.Select(selector: static domain => domain.Name).ToArray(),
            comparer: StringComparer.Ordinal
        ),
        kinds: domains.Select(selector: static domain => domain.Kind).ToArray()
    );
    /// <summary>Gets an ordinal's authored name.</summary>
    public string Name(int index) => m_table.Name(ordinal: index);
    /// <summary>Gets an ordinal's navigation topology.</summary>
    public WorldNavigationKind Kind(int index) => m_kinds[index];
    /// <summary>Resolves a domain name.</summary>
    public bool TryGetIndex(string name, out int index) => m_table.TryGetOrdinal(name: name, ordinal: out index);
}

/// <summary>The fixed-point runtime tuning for one validated navigation domain.</summary>
public readonly record struct FixedWorldNavigationDomain(
    WorldNavigationKind Kind,
    FixedVector3 Origin,
    FixedQ4816 CellSize,
    int Width,
    int Depth,
    int Layers,
    WorldNavigationConnectivity Connectivity,
    FixedQ4816 ProbeUp,
    FixedQ4816 ProbeDown,
    FixedQ4816 AgentRadius,
    FixedQ4816 AgentHeight,
    FixedQ4816 MaxStepHeight,
    FixedQ4816 MaximumSlopeRise,
    FixedQ4816 ArrivalDistance,
    int MaxExpandedNodes,
    int MaxPathNodes,
    string? Medium
) {
    /// <summary>Compiles authoring values once at the document boundary.</summary>
    public static FixedWorldNavigationDomain Compile(WorldNavigationDomain domain) => new(
        Kind: domain.Kind,
        Origin: FixedVector3.FromVector3(value: domain.Origin),
        CellSize: FixedQ4816.FromDouble(value: domain.CellSize),
        Width: domain.Width,
        Depth: domain.Depth,
        Layers: domain.Layers,
        Connectivity: domain.Connectivity,
        ProbeUp: FixedQ4816.FromDouble(value: domain.ProbeUp),
        ProbeDown: FixedQ4816.FromDouble(value: domain.ProbeDown),
        AgentRadius: FixedQ4816.FromDouble(value: domain.AgentRadius),
        AgentHeight: FixedQ4816.FromDouble(value: domain.AgentHeight),
        MaxStepHeight: FixedQ4816.FromDouble(value: domain.MaxStepHeight),
        MaximumSlopeRise: FixedQ4816.FromDouble(value: (domain.CellSize * Math.Tan(domain.MaxSlopeDegrees * (Math.PI / 180.0)))),
        ArrivalDistance: FixedQ4816.FromDouble(value: domain.ArrivalDistance),
        MaxExpandedNodes: domain.MaxExpandedNodes,
        MaxPathNodes: domain.MaxPathNodes,
        Medium: domain.Medium
    );
}
