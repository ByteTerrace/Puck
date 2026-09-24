using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One grantee's checkpointed capability rows — the five subject sets plus, for an actor, its composed control
/// applications.
/// Excludes <see cref="WorldGrants.m_handleTables"/>: a per-(principal, capability) handle table is a pure projection of
/// this table, re-derived lazily from <see cref="WorldGrants.ProjectSubjects"/> the moment its own revision cache goes
/// stale, and every live handle a guest could hold is meaningless the instant that guest's own connection drops
/// — which every checkpoint restart already forces (the arm gate refuses a checkpoint of a server any addon has
/// ever pumped, and a remote human is parked, not left connected, across a restore) — the same "subscribers
/// re-attach" exclusion <see cref="WorldOutputHub"/>/<see cref="WorldPeerHost"/> connections already carry.</summary>
public sealed record WorldGrantsGranteeCheckpoint(
    Grantee Grantee,
    IReadOnlyList<GrantSubject> Drive,
    IReadOnlyList<GrantSubject> Observe,
    IReadOnlyList<GrantSubject> Control,
    IReadOnlyList<GrantSubject> Mutate,
    IReadOnlyList<GrantSubject> Edit,
    IReadOnlyList<ControlApplication> Applications
);
