using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The escrow's own checkpointed state — every table this row's slice owns, and the first crossing sequence
/// the checkpoint does not reflect, which is where recovery starts redoing the authority's crossing log.</summary>
/// <param name="Leases">The live reservation leases.</param>
/// <param name="Committed">The committed transactions whose exact-retry payload is retained.</param>
/// <param name="LatestCommittedTransfer">Each traveler incarnation's latest committed transfer here.</param>
/// <param name="MobilityLeases">The traveler incarnations a live lease holds, with the epoch it expects.</param>
/// <param name="MobilityAdmissions">The authenticated source aliases of admitted travelers.</param>
/// <param name="BorderAdmissions">The arrived bodies still consuming their border's capacity.</param>
/// <param name="CrossingSequence">The first crossing sequence the checkpoint does not reflect.</param>
/// <param name="SpentKeys">The transfer-key ledger: every key above its source's floor that holds nothing live here any
/// more, whether it committed, and whether its death is recoverable from the crossing log.</param>
/// <param name="Floors">Each source namespace's highest settled floor.</param>
public sealed record WorldTransferEscrowCheckpoint(
    IReadOnlyList<WorldTransferLeaseCheckpoint> Leases,
    IReadOnlyList<WorldTransferCommittedCheckpoint> Committed,
    IReadOnlyList<(WorldEntityAddress Incarnation, WorldTransferKey Transfer)> LatestCommittedTransfer,
    IReadOnlyList<(WorldEntityAddress Incarnation, WorldTransferKey Transfer, ulong ExpectedEpoch)> MobilityLeases,
    IReadOnlyList<(string SourceAuthority, WorldEntityAddress Incarnation, ulong Epoch, Principal Principal)> MobilityAdmissions,
    IReadOnlyList<(int Slot, string Border)> BorderAdmissions,
    ulong CrossingSequence,
    IReadOnlyList<(WorldTransferKey Key, bool Committed, bool Durable)> SpentKeys,
    IReadOnlyList<(string SourceAuthority, ulong Floor)> Floors
);
