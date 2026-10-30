using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One committed transfer's checkpointed member/principal/incarnation rows, and the request-local ids of the
/// members it landed (a companion the commit left out is absent).</summary>
public sealed record WorldTransferCommittedCheckpoint(WorldTransferKey Key, WorldTransferCommitMember[] Members, Principal[] Principals, IReadOnlyList<WorldEntityAddress> Incarnations, IReadOnlyList<int>? Landed = null) {
    /// <summary>Gets the request-local ids of the members the commit landed.</summary>
    public IReadOnlyList<int> Landed { get; init; } = (Landed ?? []);
}
