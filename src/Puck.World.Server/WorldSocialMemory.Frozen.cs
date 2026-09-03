using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One source observer's exclusive transfer hold. Records remain in the bank, unchanged since FrozenAt;
/// a frozen export uses that clock even while the authority continues advancing.</summary>
public readonly record struct WorldSocialFrozenObserverCheckpoint(WorldEntityAddress Observer, WorldTransferKey Transfer, ulong FrozenAt);

public sealed partial class WorldSocialMemory {
    /// <summary>Representation ceiling on source observers awaiting an ownership verdict, including empty memories.</summary>
    public const int MaximumFrozenObservers = WorldBodiesLimits.CapacityCeiling;
    private readonly record struct FrozenObserver(WorldTransferKey Transfer, ulong FrozenAt, ulong Digest);
    private readonly Dictionary<WorldEntityAddress, FrozenObserver> m_frozenObservers = new(MaximumFrozenObservers);
    private ulong m_frozenSum;
    private ulong m_frozenXor;

    /// <summary>Source observers whose learning, forgetting, and receipt reclamation are paused pending transfer.</summary>
    public int FrozenObserverCount => m_frozenObservers.Count;

    /// <summary>Whether an exact observer incarnation has an unresolved source hold.</summary>
    /// <param name="observer">The original incarnation, not a current body slot.</param>
    /// <returns>True while frozen, including when no impressions or receipts exist.</returns>
    public bool IsObserverFrozen(WorldEntityAddress observer) => m_frozenObservers.ContainsKey(observer);

    /// <summary>Freezes one observer's complete history for an exact transfer. A same-key retry retains the original
    /// freeze clock; a competing transfer or incoming reservation refuses without changing state.</summary>
    /// <remarks>Single-writer operation. Allocates nothing and visits only this observer's receipts, removing their
    /// expiry-index nodes in logarithmic time. Other observers continue learning and reclaiming normally. The caller
    /// authenticates ownership and coordinates body detachment. Timeouts never automatically thaw a possibly committed
    /// export. Ordinary reads continue showing current lazy age; CaptureFrozenObserver preserves the freeze-time cut.</remarks>
    /// <param name="observer">The original mobility incarnation whose source memory is held.</param>
    /// <param name="transfer">The caller-authenticated transfer key allowed to release this hold.</param>
    /// <param name="reason">Empty on success; a named input, conflict, or capacity refusal otherwise.</param>
    /// <returns>Whether this exact transfer holds the observer's history.</returns>
    public bool TryFreezeObserver(WorldEntityAddress observer, WorldTransferKey transfer, out string reason) {
        if (!Valid(observer) || !ValidTransferKey(transfer)) { reason = "invalid social source freeze"; return false; }
        if (m_frozenObservers.TryGetValue(observer, out var frozen)) {
            var same = frozen.Transfer == transfer;
            reason = same ? string.Empty : "social observer is frozen for a different transfer";
            return same;
        }
        if (m_reservedObservers.ContainsKey(observer)) { reason = "social observer has an incoming reservation"; return false; }
        if (FrozenObserverCount == MaximumFrozenObservers) { reason = "social frozen observer capacity is full"; return false; }
        AddFrozenObserver(new(observer, transfer, EngineTick));
        reason = string.Empty;
        return true;
    }

    /// <summary>Copies the exact freeze-time observer snapshot for the matching transfer. Repeated captures remain
    /// logically identical across unrelated learning, authority-clock advancement, and checkpoint restore.</summary>
    /// <param name="observer">The frozen original incarnation.</param>
    /// <param name="transfer">The exact transfer holding the memory.</param>
    /// <returns>A detached observer checkpoint, without authority holds or reservations.</returns>
    /// <exception cref="InvalidOperationException">The observer is not frozen by this transfer.</exception>
    public WorldSocialMemoryCheckpoint CaptureFrozenObserver(WorldEntityAddress observer, WorldTransferKey transfer) {
        if (!m_frozenObservers.TryGetValue(observer, out var frozen) || frozen.Transfer != transfer) {
            throw new InvalidOperationException("social observer is not frozen by this transfer");
        }
        return CaptureObserverCore(observer, frozen.FrozenAt, frozen.FrozenAt);
    }

    /// <summary>Releases a matching source hold after a confirmed refusal or cancellation. Stored anchors are not
    /// refreshed: aging includes elapsed authority time, and expired receipts rejoin ordinary bounded reclamation.</summary>
    /// <param name="observer">The frozen original incarnation.</param>
    /// <param name="transfer">The exact transfer whose non-commit the caller has established.</param>
    /// <returns>Whether the hold was released; an absent or competing key is a no-op.</returns>
    public bool ThawObserver(WorldEntityAddress observer, WorldTransferKey transfer) {
        if (!m_frozenObservers.TryGetValue(observer, out var frozen) || frozen.Transfer != transfer) { return false; }
        var owner = Owner(observer);
        for (var node = owner.ReceiptHead; node >= 0; node = m_receiptOwners.Next(node)) {
            var receipt = m_receipts[m_receiptOwners.Key(node)];
            m_expiry.Add(node, receipt.LocalOccurredAt, receipt.Ordinal);
        }
        RemoveFrozenObserver(observer, frozen);
        return true;
    }

    /// <summary>Retires a matching source hold and all its records after confirmed destination ownership. Other
    /// observers' memories of this individual remain. Never use an expired deadline as proof of a non-ambiguous outcome.</summary>
    /// <param name="observer">The original incarnation whose destination commit is confirmed.</param>
    /// <param name="transfer">The exact transfer that held this source memory.</param>
    /// <returns>Whether the hold and records were retired, including an empty held history.</returns>
    public bool RetireFrozenObserver(WorldEntityAddress observer, WorldTransferKey transfer) {
        if (!m_frozenObservers.TryGetValue(observer, out var frozen) || frozen.Transfer != transfer) { return false; }
        RemoveObserverCore(observer);
        RemoveFrozenObserver(observer, frozen);
        return true;
    }

    private void AddFrozenObserver(WorldSocialFrozenObserverCheckpoint row) {
        var owner = Owner(row.Observer);
        for (var node = owner.ReceiptHead; node >= 0; node = m_receiptOwners.Next(node)) { m_expiry.Remove(node); }
        var hash = Fnv1aHash.Create();
        Add(ref hash, row.Observer); hash.Add(Fnv1aHash.Compute(row.Transfer.SourceAuthority.AsSpan()));
        hash.Add(row.Transfer.TransferId); hash.Add(row.FrozenAt);
        m_frozenObservers.Add(row.Observer, new(row.Transfer, row.FrozenAt, hash.Value));
        AddDigest(ref m_frozenSum, ref m_frozenXor, hash.Value);
    }

    private void RemoveFrozenObserver(WorldEntityAddress observer, FrozenObserver frozen) {
        m_frozenObservers.Remove(observer);
        RemoveDigest(ref m_frozenSum, ref m_frozenXor, frozen.Digest);
    }

    private WorldSocialFrozenObserverCheckpoint[] CaptureFrozenObservers() {
        var rows = new WorldSocialFrozenObserverCheckpoint[FrozenObserverCount];
        var index = 0;
        foreach (var (observer, frozen) in m_frozenObservers) { rows[index++] = new(observer, frozen.Transfer, frozen.FrozenAt); }
        Array.Sort(rows, static (left, right) => Compare(left.Observer, right.Observer));
        return rows;
    }

    private static WorldSocialFrozenObserverCheckpoint[] ValidateFrozenObservers(WorldSocialMemoryCheckpoint checkpoint) {
        if (checkpoint.FrozenObservers is null) { return []; }
        var rows = CopyRows(checkpoint.FrozenObservers, MaximumFrozenObservers);
        if (rows.Length == 0) { return rows; }
        var freezes = new Dictionary<WorldEntityAddress, ulong>(rows.Length);
        foreach (var row in rows) {
            Require(Valid(row.Observer) && ValidTransferKey(row.Transfer) && row.FrozenAt <= checkpoint.EngineTick && freezes.TryAdd(row.Observer, row.FrozenAt));
        }
        foreach (var reservation in checkpoint.ImportReservations!) {
            foreach (var member in reservation.Members) { Require(!freezes.ContainsKey(member.Observer)); }
        }
        foreach (var row in checkpoint.Impressions) {
            Require(!freezes.TryGetValue(row.Key.Observer, out var frozenAt) || row.UpdatedAt <= frozenAt);
        }
        foreach (var row in checkpoint.Receipts) {
            Require(!freezes.TryGetValue(row.Impression.Observer, out var frozenAt) || row.LocalOccurredAt <= frozenAt);
        }
        return rows;
    }
}
