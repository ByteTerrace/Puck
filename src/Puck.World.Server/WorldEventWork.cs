using Puck.Abstractions.Counting;

namespace Puck.World.Server;

/// <summary>
/// The work <see cref="WorldEventFeed.Collect"/> spends computing a tick's world events, counted in one
/// <see cref="WorkCounterSet"/> (<see cref="Process"/>) and read under the source name <see cref="SourceName"/>: the
/// collections, the bodies some event listener could be told about, the broadphase candidates and exact overlap tests
/// collision sensing spends, the overlap relationships it holds, the body/region containment tests, and the edges
/// emitted.
/// </summary>
/// <remarks>Each count is a monotonic total over the ledger's life, never reset; a reader takes a window by reading
/// twice and subtracting. None is part of simulation state: nothing hashes, exports or checkpoints them. Collision work
/// that grows while no listener observes a body, or that grows with bodies rather than with the contacts they make,
/// names a sensing pass that is not proportional to what is listened to.</remarks>
public static class WorldEventWork {
    /// <summary>The name a counters report heads this source's section with.</summary>
    public const string SourceName = "world.events";

    /// <summary>Gets the kind counting collections: one per tick the feed collects.</summary>
    public static WorkKind Collects { get; } = new(name: "world.events.collects", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting, per collection, the bodies whose collision edges some held event-budgeted
    /// observe grant could deliver: every body under a wildcard grant, otherwise each body a grant names.</summary>
    public static WorkKind CollisionListenedBodies { get; } = new(name: "world.events.collision-listened-bodies", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the bodies a collection placed in its collision broadphase.</summary>
    public static WorkKind CollisionBroadphaseBodies { get; } = new(name: "world.events.collision-broadphase-bodies", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the box-overlapping broadphase candidates new-pair discovery inspected.</summary>
    public static WorkKind CollisionCandidates { get; } = new(name: "world.events.collision-candidates", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting exact body-pair overlap tests: the pairs tested, retained and new.</summary>
    public static WorkKind CollisionPairTests { get; } = new(name: "world.events.collision-pair-tests", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting, per collection, the overlap relationships held after it.</summary>
    public static WorkKind CollisionOverlaps { get; } = new(name: "world.events.collision-overlaps", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the exact body/region containment tests.</summary>
    public static WorkKind RegionBodyTests { get; } = new(name: "world.events.region-body-tests", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the edges collections emitted, every family.</summary>
    public static WorkKind Edges { get; } = new(name: "world.events.edges", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the collision begin and end edges among them.</summary>
    public static WorkKind CollisionEdges { get; } = new(name: "world.events.collision-edges", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the ledger a host's feeds count into. Declared after the kinds, which it reads as it is
    /// created.</summary>
    public static WorkCounterSet Process { get; } = new(
        kinds: [Collects, CollisionListenedBodies, CollisionBroadphaseBodies, CollisionCandidates, CollisionPairTests, CollisionOverlaps, RegionBodyTests, Edges, CollisionEdges],
        name: SourceName
    );
}
