using System.Text.Json;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldSocialMemory {
    // Transfer intake reads the canonical source policy carried by the snapshot, then owns detached records.
    // It does not allocate a source-sized bank and cannot import another observer or authority-owned holds.
    internal static bool TryReadObserverImport(WorldEntityAddress observer, WorldSocialMemoryCheckpoint checkpoint,
        out WorldSocialObserverImport incoming, out string reason) {
        incoming = default;
        try {
            if (checkpoint is null || string.IsNullOrEmpty(checkpoint.PolicyIdentity) || checkpoint.PolicyIdentity.Length > 65536) {
                reason = "invalid social transfer policy"; return false;
            }
            var declaration = JsonSerializer.Deserialize(checkpoint.PolicyIdentity, WorldJsonContext.Default.WorldSocialPolicy);
            if (declaration is null) { reason = "missing social transfer policy"; return false; }
            var policy = CompiledWorldSocialPolicy.Compile(declaration);
            var copy = ValidateCheckpoint(policy, checkpoint);
            if (!Valid(observer) || copy.Impressions.Any(row => row.Key.Observer != observer) ||
                copy.Receipts.Any(row => row.Impression.Observer != observer) || copy.ImportReservations!.Count != 0 ||
                copy.FrozenObservers!.Count != 0 || copy.EvidenceAttempts != 0 || copy.ReclaimedReceipts != 0) {
                reason = "social transfer must contain only the traveler's exported history"; return false;
            }
            incoming = new(observer, policy, copy);
            reason = string.Empty; return true;
        } catch (JsonException) { reason = "invalid social transfer policy JSON"; return false; }
        catch (ArgumentException) { reason = "invalid social transfer history"; return false; }
        catch (OverflowException) { reason = "social transfer history exceeds its numeric representation"; return false; }
    }

    internal bool AcceptsMemorySemantics(CompiledWorldSocialPolicy source) => SameMemorySemantics(source, Policy);
    internal bool HasImportReservation(WorldTransferKey key, ReadOnlySpan<WorldSocialImportAllowance> members) =>
        m_importReservations.TryGetValue(key, out var reservation) && members.SequenceEqual(reservation.Members);

    internal static WorldSocialMemoryCheckpoint CopyObserverSnapshot(WorldSocialMemoryCheckpoint state) => state with {
        Impressions = state.Impressions.ToArray(), Receipts = state.Receipts.ToArray(), ImportReservations = [], FrozenObservers = [],
    };

    internal static bool ObserverSnapshotMatches(WorldSocialMemoryCheckpoint? left, WorldSocialMemoryCheckpoint? right) =>
        ReferenceEquals(left, right) || (left is not null && right is not null && left.PolicyIdentity == right.PolicyIdentity &&
        left.EngineTick == right.EngineTick && left.EvidenceAttempts == right.EvidenceAttempts && left.ReclaimedReceipts == right.ReclaimedReceipts &&
        left.NextOrdinal == right.NextOrdinal && left.Impressions.SequenceEqual(right.Impressions) && left.Receipts.SequenceEqual(right.Receipts));
}
