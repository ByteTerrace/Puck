using Puck.Maths;

namespace Puck.Physics.Navigation;

/// <summary>The topology and occupancy constraint of one navigation domain. Mirrors the authored document's own
/// domain-kind vocabulary one level up the seam; the kernel reads no document, so a host maps its own authoring
/// enum onto this one when it builds a <see cref="NavigationDomainInput"/>.</summary>
public enum NavigationKind : byte {
    /// <summary>A 2D grid whose Y values are sampled from solid ground.</summary>
    Surface,
    /// <summary>A 3D grid of collision-free space.</summary>
    Volume,
    /// <summary>A 3D grid constrained to one live fluid-medium field.</summary>
    Medium,
}

/// <summary>The neighbour set used by a volume navigation domain. Mirrors the authored document's own connectivity
/// vocabulary one level up the seam.</summary>
public enum NavigationConnectivity : byte {
    /// <summary>The six axis-aligned neighbours.</summary>
    Axis,
    /// <summary>Axis and two-axis diagonal neighbours.</summary>
    FacesAndEdges,
    /// <summary>All 26 neighbours, including three-axis diagonals.</summary>
    Full,
}

/// <summary>Bounds one domain's reusable destination trees and their aggregate expansion work — a plain mirror of
/// an authored sharing policy the kernel itself never parses.</summary>
/// <param name="GoalCapacity">Resident destination-cell trees. A full cache with pending work refuses another
/// destination as capacity-limited; it never launches an unbudgeted independent search.</param>
/// <param name="ExpandedNodesPerTick">Total reverse-Dijkstra expansions per simulation tick, shared fairly between
/// pending resident goals. Each expansion inspects at most 26 edges. A tree can eventually settle every domain
/// cell; the independent A* <see cref="NavigationDomainInput.MaxExpandedNodes"/> bound does not truncate a shared
/// tree.</param>
public sealed record NavigationSharing(int GoalCapacity, int ExpandedNodesPerTick);

/// <summary>One finite navigation grid's fixed-point tuning, compiled once at a host's document boundary and handed
/// to the kernel as plain data — the kernel parses no document, so every field here is already in the kernel's own
/// units and vocabulary.</summary>
/// <param name="Name">The stable domain name a route-search caller and a checkpoint both address it by.</param>
/// <param name="Kind">Whether cells follow ground, free 3D space, or a live medium.</param>
/// <param name="Origin">The world-space center of cell (0,0,0); for a surface domain, Y is the ground-probe baseline.</param>
/// <param name="CellSize">The square cell width in world units.</param>
/// <param name="Width">The number of cells along world X.</param>
/// <param name="Depth">The number of cells along world Z.</param>
/// <param name="Layers">The number of cells along world Y; surface domains carry one.</param>
/// <param name="Connectivity">The volume neighbour set; ignored by surface domains.</param>
/// <param name="ProbeUp">For a surface domain, the ground-search distance above origin Y.</param>
/// <param name="ProbeDown">For a surface domain, the ground-search distance below origin Y.</param>
/// <param name="AgentRadius">The solid-clearance sphere radius. A medium domain also keeps this whole volume submerged.</param>
/// <param name="AgentHeight">For a surface domain, the clearance capsule height.</param>
/// <param name="MaxStepHeight">For a surface domain, the greatest adjacent height delta.</param>
/// <param name="MaximumSlopeRise">For a surface domain, the greatest adjacent rise one authored slope degree bound
/// admits over one diagonal cell step.</param>
/// <param name="ArrivalDistance">How close a body must come before advancing to the next waypoint.</param>
/// <param name="MaxExpandedNodes">The hard A* expansion budget for one independent route search.</param>
/// <param name="MaxPathNodes">The hard stored-waypoint budget for one route.</param>
/// <param name="Medium">For a medium domain, the named lattice field cells must remain inside.</param>
/// <param name="Shared">Optional shared reverse-search policy. Absent uses independent bounded A* searches.</param>
public readonly record struct NavigationDomainInput(
    string Name,
    NavigationKind Kind,
    FixedVector3 Origin,
    FixedQ4816 CellSize,
    int Width,
    int Depth,
    int Layers,
    NavigationConnectivity Connectivity,
    FixedQ4816 ProbeUp,
    FixedQ4816 ProbeDown,
    FixedQ4816 AgentRadius,
    FixedQ4816 AgentHeight,
    FixedQ4816 MaxStepHeight,
    FixedQ4816 MaximumSlopeRise,
    FixedQ4816 ArrivalDistance,
    int MaxExpandedNodes,
    int MaxPathNodes,
    string? Medium,
    NavigationSharing? Shared
);

/// <summary>Representation ceilings a host names when constructing a <see cref="NavigationRuntime"/>. The kernel
/// parses no document, so it cannot own these as its own constants; a host's values here must equal whatever its
/// own authoring ceiling declares, or a checkpoint round-trip and the host's own validator disagree.</summary>
/// <param name="MaxSurfaceClearanceSweeps">The greatest number of parallel sphere sweeps used to prove one tall
/// surface-agent transition.</param>
/// <param name="MaxMediumSegmentSubdivisions">The greatest number of equal subsegments checked along one live
/// medium edge.</param>
/// <param name="MaxConcurrentRequesters">The greatest number of distinct bodies that can hold a pending shared-tree
/// request at once; bounds a domain's per-tree pending-request list.</param>
public readonly record struct NavigationCapacity(int MaxSurfaceClearanceSweeps, int MaxMediumSegmentSubdivisions, int MaxConcurrentRequesters);

/// <summary>The live-medium field seam a medium navigation domain reads through. A host's own field-lattice
/// representation implements this narrow view rather than the kernel depending on that representation directly.</summary>
public interface INavigationMediumField {
    /// <summary>Gets one field's write-generation counter, so a domain can detect a live medium edit and invalidate
    /// its shared destination trees.</summary>
    /// <param name="field">The field's compiled ordinal.</param>
    ulong ValueRevision(int field);
    /// <summary>Reports whether an axis-aligned clearance cube around a point remains inside a live medium.</summary>
    /// <param name="field">The field's compiled ordinal.</param>
    /// <param name="position">The cube's center.</param>
    /// <param name="clearance">The cube's half-extent; must be in [0, half a lattice cell].</param>
    bool IsInsideMedium(int field, in FixedVector3 position, FixedQ4816 clearance);
    /// <summary>Conservatively proves an entire clearance-cube sweep inside one live medium's free surface.</summary>
    /// <param name="field">The field's compiled ordinal.</param>
    /// <param name="from">The sweep's start center.</param>
    /// <param name="to">The sweep's end center.</param>
    /// <param name="clearance">The swept cube's half-extent; must be in [0, half a lattice cell].</param>
    /// <param name="maximumSubdivisions">The caller's subdivision ceiling; a longer segment refuses.</param>
    bool IsSegmentInsideMedium(int field, in FixedVector3 from, in FixedVector3 to, FixedQ4816 clearance, int maximumSubdivisions);
    /// <summary>Resolves a field's authored name to its compiled ordinal.</summary>
    /// <param name="name">The authored field name.</param>
    /// <param name="field">The compiled ordinal, when the method returns <see langword="true"/>.</param>
    bool TryFieldIndex(string name, out int field);
}
