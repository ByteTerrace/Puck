using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One committed transfer's checkpointed member/principal/incarnation rows.</summary>
public sealed record WorldTransferCommittedCheckpoint(WorldTransferKey Key, WorldTransferCommitMember[] Members, Principal[] Principals, IReadOnlyList<WorldEntityAddress> Incarnations);
