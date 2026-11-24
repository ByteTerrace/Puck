using Puck.Abstractions.Documents;
using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.World;

/// <summary>The authored bounded surface, free-volume, and live-medium navigation domains available to body producers,
/// the area types their cells carry, and the cost profiles that price those areas per kind of agent.</summary>
/// <param name="Domains">Finite rectangular domains, each compiled from the world's deterministic solid field.</param>
/// <param name="AreaTypes">The area type names the world's areas, domains and profiles use, in a stable order. The
/// engine gives no type a meaning: a type costs what each profile says.</param>
/// <param name="Areas">Marked volumes in precedence order: a cell takes the type of the last area holding the point a
/// body standing on it stands at, else its domain's own <see cref="WorldNavigationDomain.Area"/>.</param>
/// <param name="Profiles">Named cost profiles a navigated producer target selects
/// (<see cref="Puck.Physics.Motion.BodyTargetSource.Navigated.Profile"/>). A target with no profile pays one for every
/// area.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldNavigationSection(
    IReadOnlyList<WorldNavigationDomain>? Domains = null,
    [property: DocumentText("a navigation area type")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AreaTypes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldNavigationArea>? Areas = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldNavigationProfile>? Profiles = null
) {
    /// <summary>Gets a section containing no navigation domains.</summary>
    public static WorldNavigationSection Absent { get; } = new();
    /// <summary>Gets the declared domain rows.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldNavigationDomain> Rows => (Domains ?? []);
    /// <summary>Gets the declared area type names.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> AreaTypeRows => (AreaTypes ?? []);
    /// <summary>Gets the declared area rows.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldNavigationArea> AreaRows => (Areas ?? []);
    /// <summary>Gets the declared profile rows.</summary>
    [JsonIgnore]
    public IReadOnlyList<WorldNavigationProfile> ProfileRows => (Profiles ?? []);
}
/// <summary>One marked navigation area: a volume whose cells take an area type. Exactly one of
/// <paramref name="Placement"/> and <paramref name="Region"/> is authored.</summary>
/// <param name="Type">The area type, one of <see cref="WorldNavigationSection.AreaTypes"/>.</param>
/// <param name="Placement">A static placement whose <see cref="WorldPlacement.Region"/> is the area's volume, so one
/// authored region both senses bodies and marks the ground under it.</param>
/// <param name="Region">The area's own volume in world space: a sphere of <see cref="WorldPlacementRegion.Radius"/> or a
/// box of <see cref="WorldPlacementRegion.HalfExtents"/> about <see cref="WorldPlacementRegion.Center"/>, turned by
/// <see cref="WorldPlacementRegion.YawDegrees"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldNavigationArea(
    [property: DocumentText("a navigation area type")] string Type,
    [property: DocumentName(WorldNameKind.Placement, WorldNameRole.Names)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Placement = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPlacementRegion? Region = null
);
/// <summary>A named navigation cost profile: what one kind of agent pays to cross each area type. A step costs its length
/// (and the route search's clearance surcharge) times the multiplier of the area it steps onto.</summary>
/// <param name="Name">The name a navigated target's <c>profile</c> selects.</param>
/// <param name="Costs">Each priced area type's multiplier, positive and at most
/// <see cref="WorldNavigationCapacity.MaxAreaCost"/>; a type left out, and an unmarked cell, cost one.</param>
/// <param name="Exclude">The area types the agent never steps onto. A body already standing on one may step off it.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldNavigationProfile(
    [property: DocumentText("a navigation profile name")] string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, float>? Costs = null,
    [property: DocumentText("a navigation area type")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Exclude = null
);
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
/// <param name="Origin">The center of cell (0,0,0), relative to Parent when present and world-space otherwise;
/// for a surface domain, resolved world Y is the ground-probe baseline.</param>
/// <param name="CellSize">The square cell width in world units.</param>
/// <param name="Width">The number of cells along the frame's X axis.</param>
/// <param name="Depth">The number of cells along the frame's Z axis.</param>
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
/// <param name="Shared">Whether bodies navigating this domain follow shared goal fields: one field per goal over the
/// domain's network, derived once and read a step at a time by every body heading there, in place of a route search per
/// body. False keeps independent bounded A* searches, each within this domain.</param>
/// <param name="Parent">Optional static placement frame. Origin and grid axes are local to that frame;
/// probes remain vertical. The frame must be undistributed and unit scale.</param>
/// <param name="Network">The network the domain joins. Domains join where they share a cell only when their networks
/// match, so a route or goal field crosses from one district's pavement to the next but never onto a road of another
/// network. Absent joins the one unnamed network.</param>
/// <param name="Area">The area type of the domain's cells no area marks, one of
/// <see cref="WorldNavigationSection.AreaTypes"/>; absent leaves them unmarked, costing one under every profile.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldNavigationDomain(
    [property: DocumentText("a navigation domain name")] string Name,
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
    [property: DocumentName(WorldNameKind.Field, WorldNameRole.Names)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Medium = null,
    bool Shared = false,
    [property: DocumentName(WorldNameKind.Placement, WorldNameRole.Names)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Parent = null,
    [property: DocumentText("a navigation network name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Network = null,
    [property: DocumentText("a navigation area type")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Area = null
);
/// <summary>Hard representation ceilings for authored navigation work and memory.</summary>
public static class WorldNavigationCapacity {
    /// <summary>The greatest cell count in one domain.</summary>
    public const int MaxCellsPerDomain = 65_536;
    /// <summary>The greatest total cell count compiled by one world.</summary>
    public const int MaxCellsPerWorld = 262_144;
    /// <summary>The greatest number of named domains in one world.</summary>
    public const int MaxDomains = 16;
    /// <summary>The greatest number of equal subsegments checked along one live medium edge.</summary>
    public const int MaxMediumSegmentSubdivisions = 32;
    /// <summary>The greatest route length retained per body.</summary>
    public const int MaxPathNodes = 1_024;
    /// <summary>The bytes a world's resident goal fields hold together before the least recently read is dropped: a
    /// field holds one byte per cell of its network, so this keeps a few hundred goals over the largest world resident.
    /// A dropped field is derived again with the same steps, so the bound sets memory and work, never a route.</summary>
    public const long MaxGoalFieldBytes = 64L * 1024L * 1024L;
    /// <summary>The greatest area cost multiplier a profile names: a step's cost times it stays far inside a route's
    /// fixed-point budget.</summary>
    public const float MaxAreaCost = 1000f;
}
/// <summary>The stable authored-name to navigation-domain ordinal table.</summary>
public sealed class WorldNavigationDomainTable {
    private readonly WorldNavigationKind[] m_kinds;
    private readonly OrdinalTable m_table;
    private readonly OrdinalTable m_profiles;

    private WorldNavigationDomainTable(OrdinalTable table, WorldNavigationKind[] kinds, OrdinalTable profiles) {
        m_table = table;
        m_kinds = kinds;
        m_profiles = profiles;
    }

    /// <summary>Gets an empty domain table.</summary>
    public static WorldNavigationDomainTable Empty { get; } = new(
        table: OrdinalTable.Empty,
        kinds: [],
        profiles: OrdinalTable.Empty
    );

    /// <summary>Compiles authored order into stable ordinals: the domains, and the cost profiles a navigated target
    /// selects.</summary>
    /// <param name="section">The navigation section.</param>
    public static WorldNavigationDomainTable Compile(WorldNavigationSection section) => new(
        table: OrdinalTable.Build(
            names: section.Rows.Select(selector: static domain => domain.Name).ToArray(),
            comparer: StringComparer.Ordinal
        ),
        kinds: section.Rows.Select(selector: static domain => domain.Kind).ToArray(),
        profiles: OrdinalTable.Build(
            names: section.ProfileRows.Select(selector: static profile => profile.Name).Distinct(comparer: StringComparer.Ordinal).ToArray(),
            comparer: StringComparer.Ordinal
        )
    );
    /// <summary>Resolves a cost profile name to its ordinal.</summary>
    /// <param name="name">The profile name, or <see langword="null"/>.</param>
    /// <returns>The ordinal, or -1 for <see langword="null"/> or an undeclared name.</returns>
    public int ProfileOf(string? name) => (((name is not null) && m_profiles.TryGetOrdinal(name: name, ordinal: out var ordinal)) ? ordinal : -1);
    /// <summary>Gets an ordinal's navigation topology.</summary>
    public WorldNavigationKind Kind(int index) => m_kinds[index];
    /// <summary>Gets an ordinal's authored name.</summary>
    public string Name(int index) => m_table.Name(ordinal: index);
    /// <summary>Resolves a domain name.</summary>
    public bool TryGetIndex(string name, out int index) => m_table.TryGetOrdinal(
        name: name,
        ordinal: out index
    );
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
        MaximumSlopeRise: SlopeRise(
            cellSize: FixedQ4816.FromDouble(value: domain.CellSize),
            slopeDegrees: domain.MaxSlopeDegrees
        ),
        ArrivalDistance: FixedQ4816.FromDouble(value: domain.ArrivalDistance),
        MaxExpandedNodes: domain.MaxExpandedNodes,
        MaxPathNodes: domain.MaxPathNodes,
        Medium: domain.Medium
    );

    // The rise across one cell at the steepest admitted slope, cellSize · sin / cos of the pinned fixed-point sine and
    // cosine, rounded once. A slope whose cosine is not positive rises without bound and saturates; the validator
    // caps a surface slope below that.
    private static FixedQ4816 SlopeRise(FixedQ4816 cellSize, float slopeDegrees) {
        var (sine, cosine) = FixedQ4816.SinCos(angle: FixedQ4816.FromDegrees(degrees: slopeDegrees));

        return ((cosine <= FixedQ4816.Zero)
            ? FixedQ4816.MaxValue
            : FixedQ4816.FromRawBits(value: ((long)FixedPointRounding.RoundRational(
                denominator: cosine.Value,
                fractionBitCount: 0,
                numerator: (((BigInteger)cellSize.Value) * sine.Value)
            ))));
    }
}
