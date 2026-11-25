using Puck.Abstractions.Counting;

namespace Puck.World.Server;

/// <summary>
/// The work a host's authorities spend reading and acting across worlds, counted in one <see cref="WorkCounterSet"/>
/// (<see cref="Process"/>) under the source name <see cref="SourceName"/>: the followed rows compared with their leaders'
/// deliveries, and the adoptions those comparisons wrote (<see cref="WorldStateFollow"/>).
/// </summary>
/// <remarks>Each count is a monotonic total over the host's life; a reader takes a window by reading twice and
/// subtracting. None is simulation state. A steady world that follows a day adopts once and then compares every tick
/// without writing: adoptions that grow while no leader is written name rows whose traits disagree.</remarks>
public static class WorldFederationWork {
    /// <summary>The name a counters report heads this source's section with.</summary>
    public const string SourceName = "world.federation";

    /// <summary>Gets the kind counting followed rows compared with their leader's latest delivery, one per row per
    /// boundary.</summary>
    public static WorkKind FollowReads { get; } = new(name: "world.federation.follow-reads", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the leader's values adopted into followed rows: one taped write each.</summary>
    public static WorkKind FollowAdoptions { get; } = new(name: "world.federation.follow-adoptions", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the leader's writes a delayed follow put in flight: each arrives, and counts as
    /// an adoption, one delay later.</summary>
    public static WorkKind FollowDelayed { get; } = new(name: "world.federation.follow-delayed", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting values a delayed follow replaced in flight because its leader wrote more often
    /// inside one delay than the line holds.</summary>
    public static WorkKind FollowDelayOverflows { get; } = new(name: "world.federation.follow-delay-overflows", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the ledger a host's authorities count into. Declared after the kinds, which it reads as it is
    /// created.</summary>
    public static WorkCounterSet Process { get; } = new(
        kinds: [FollowReads, FollowAdoptions, FollowDelayed, FollowDelayOverflows],
        name: SourceName
    );
}
