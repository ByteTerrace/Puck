using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One grantee's checkpointed capability rows — the five subject sets plus, for an actor, its composed control
/// applications. The handle tables projected from these rows ride beside them
/// (<see cref="WorldGrantsCheckpoint.HandleTables"/>).</summary>
public sealed record WorldGrantsGranteeCheckpoint(
    Grantee Grantee,
    IReadOnlyList<GrantSubject> Drive,
    IReadOnlyList<GrantSubject> Observe,
    IReadOnlyList<GrantSubject> Control,
    IReadOnlyList<GrantSubject> Mutate,
    IReadOnlyList<GrantSubject> Edit,
    IReadOnlyList<ControlApplication> Applications
);
