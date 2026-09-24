using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The escrow's own checkpointed state — every table this row's slice owns.</summary>
public sealed record WorldTransferEscrowCheckpoint(
    IReadOnlyList<WorldTransferLeaseCheckpoint> Leases,
    IReadOnlyList<WorldTransferCommittedCheckpoint> Committed,
    IReadOnlyList<(WorldEntityAddress Incarnation, WorldTransferKey Transfer)> LatestCommittedTransfer,
    IReadOnlyList<(WorldEntityAddress Incarnation, WorldTransferKey Transfer, ulong ExpectedEpoch)> MobilityLeases,
    IReadOnlyList<(string SourceAuthority, WorldEntityAddress Incarnation, ulong Epoch, Principal Principal)> MobilityAdmissions,
    IReadOnlyList<(int Slot, string Border)> BorderAdmissions
);
