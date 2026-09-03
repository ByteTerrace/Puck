using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One absent observer's reserved storage. Counts include all dimensions and retained duplicate receipts;
/// zero-sized claims still exclude ordinary learning and competing ownership for that incarnation.</summary>
public readonly record struct WorldSocialImportAllowance(WorldEntityAddress Observer, int Impressions, int Receipts);

/// <summary>A detached destination reservation. Member order is part of its exact retry identity.</summary>
public sealed record WorldSocialImportReservationCheckpoint(WorldTransferKey Key, IReadOnlyList<WorldSocialImportAllowance> Members);

/// <summary>One observer's complete incoming history and the immutable policy that gives it meaning.</summary>
public readonly record struct WorldSocialObserverImport(
    WorldEntityAddress Observer, CompiledWorldSocialPolicy SourcePolicy, WorldSocialMemoryCheckpoint Memory);

public sealed partial class WorldSocialMemory {
    /// <summary>Representation ceiling on simultaneously reserved observers, including empty memories. Shares the
    /// authority's body-capacity ceiling; unlike memory-entry quotas, zero-sized claims cannot evade this limit.</summary>
    public const int MaximumReservedObservers = WorldBodiesLimits.CapacityCeiling;

    private sealed record ImportReservation(WorldSocialImportAllowance[] Members, ulong Digest);
    private readonly Dictionary<WorldTransferKey, ImportReservation> m_importReservations = new(MaximumReservedObservers);
    private readonly Dictionary<WorldEntityAddress, WorldTransferKey> m_reservedObservers = new(MaximumReservedObservers);
    private ulong m_reservationSum;
    private ulong m_reservationXor;

    /// <summary>Outstanding destination import groups, independent of retained-memory counts.</summary>
    public int ImportReservationCount => m_importReservations.Count;
    /// <summary>Absent observer incarnations exclusively claimed by outstanding import groups.</summary>
    public int ReservedObserverCount => m_reservedObservers.Count;
    /// <summary>Impression slots unavailable to ordinary learning and unreserved intake.</summary>
    public int ReservedImpressionCount { get; private set; }
    /// <summary>Receipt slots unavailable to ordinary learning and unreserved intake.</summary>
    public int ReservedReceiptCount { get; private set; }

    /// <summary>Atomically reserves exclusive observer identities and bounded storage for one incoming group.
    /// An independently allocated, exactly equal retry succeeds without changing state; an altered retry refuses.</summary>
    /// <remarks>Single-writer operation. Copies the bounded member list, not a capacity-sized memory bank. Reservations
    /// persist through clock advances and checkpoints until explicitly cancelled or successfully imported. The caller
    /// owns authentication, source freezing, deadlines, and body admission; this is not the transfer handshake.</remarks>
    /// <param name="key">Authenticated source namespace and transfer identifier, supplied by the caller.</param>
    /// <param name="members">Ordered observer identities and maximum incoming record counts.</param>
    /// <param name="reason">Empty on success; a named invalid-input, retry, ownership, or capacity refusal otherwise.</param>
    /// <returns>Whether the complete reservation exists. Refusal leaves all state unchanged.</returns>
    public bool TryReserveImport(WorldTransferKey key, IReadOnlyList<WorldSocialImportAllowance> members, out string reason) {
        if (!ValidTransferKey(key) || members is null || members.Count is <= 0 or > MaximumReservedObservers) {
            reason = "invalid social import reservation"; return false;
        }
        WorldSocialImportAllowance[] copy;
        try { copy = CopyRows(members, MaximumReservedObservers, 1); }
        catch (ArgumentException) { reason = "invalid social import reservation members"; return false; }
        if (m_importReservations.TryGetValue(key, out var prior)) {
            var same = prior.Members.AsSpan().SequenceEqual(copy);
            reason = same ? string.Empty : "social import reservation already exists with different members";
            return same;
        }
        if (copy.Length > MaximumReservedObservers - ReservedObserverCount) {
            reason = "social import observer capacity is full"; return false;
        }
        var seen = new HashSet<WorldEntityAddress>(copy.Length);
        var impressions = Policy.ImpressionCapacity - ImpressionCount - ReservedImpressionCount;
        var receipts = Policy.ReceiptCapacity - ReceiptCount - ReservedReceiptCount;
        foreach (var member in copy) {
            if (!ValidAllowance(member, Policy) || !seen.Add(member.Observer)) {
                reason = "invalid or duplicate social import observer"; return false;
            }
            if (m_observers.ContainsKey(member.Observer) || m_reservedObservers.ContainsKey(member.Observer) || IsObserverFrozen(member.Observer)) {
                reason = "social observer memory is already owned or reserved here"; return false;
            }
            if (member.Impressions > impressions || member.Receipts > receipts) {
                reason = "social import memory capacity is full"; return false;
            }
            impressions -= member.Impressions; receipts -= member.Receipts;
        }
        AddImportReservation(key, copy);
        reason = string.Empty;
        return true;
    }

    /// <summary>Releases one destination reservation without changing any retained memory or work/clock state.</summary>
    /// <param name="key">The exact reservation to cancel or expire.</param>
    /// <returns>Whether a reservation was removed. An unknown or invalid key is a no-op.</returns>
    public bool CancelImportReservation(WorldTransferKey key) {
        if (!m_importReservations.Remove(key, out var reservation)) { return false; }
        RemoveDigest(ref m_reservationSum, ref m_reservationXor, reservation.Digest);
        foreach (var member in reservation.Members) {
            m_reservedObservers.Remove(member.Observer);
            ReservedImpressionCount -= member.Impressions; ReservedReceiptCount -= member.Receipts;
        }
        return true;
    }

    /// <summary>Validates and atomically imports every observer in one reserved group, preserving each history's
    /// ages and forgetting boundaries. No member is applied before the last member has passed every check.</summary>
    /// <remarks>Members must match reservation order and fit their individual allowances. Unused reserved storage is
    /// released on success. Failure retains the entire reservation and changes no memory, counters, clock, or ordinal.
    /// Scratch scales with incoming records. This operation does not admit bodies or retire source ownership;
    /// the enclosing transfer must coordinate those steps. The enclosing escrow also owns committed-retry receipts.</remarks>
    /// <param name="key">The exact existing reservation.</param>
    /// <param name="members">Complete, ordered observer histories and their source policies.</param>
    /// <param name="reason">Empty on success; a named reservation, input, policy, capacity, or arithmetic refusal otherwise.</param>
    /// <returns>Whether the whole group was imported and its reservation consumed.</returns>
    public bool TryImportReserved(WorldTransferKey key, IReadOnlyList<WorldSocialObserverImport> members, out string reason) {
        if (!TryPrepareReservedImport(key, members, out var prepared, out reason)) { return false; }
        return TryCommitReservedImport(prepared!, out reason);
    }

    /// <summary>A detached, single-use import prepared against one bank, reservation, clock, and admission ordinal.
    /// This cold-path token owns its row copies and is invalidated by cancellation, replacement, new admission, or clock advance.</summary>
    public sealed class PreparedImport {
        internal WorldSocialMemory Bank { get; }
        internal WorldTransferKey Key { get; }
        internal object Reservation { get; }
        internal ulong Clock { get; }
        internal ulong Ordinal { get; }
        internal ulong NextOrdinal { get; }
        internal WorldSocialMemoryCheckpoint[] Members { get; }
        internal PreparedImport(WorldSocialMemory bank, WorldTransferKey key, object reservation, ulong ordinal,
            ulong nextOrdinal, WorldSocialMemoryCheckpoint[] members) {
            Bank = bank; Key = key; Reservation = reservation; Clock = bank.EngineTick;
            Ordinal = ordinal; NextOrdinal = nextOrdinal; Members = members;
        }
    }

    /// <summary>Checks every reserved member and detaches its rows without modifying the bank. An enclosing
    /// single-writer transaction can validate memory before attempting body admission.</summary>
    /// <param name="key">The existing group reservation.</param>
    /// <param name="members">Complete histories in reservation order.</param>
    /// <param name="prepared">An opaque commit token on success; null on refusal.</param>
    /// <param name="reason">A named validation refusal or the empty string.</param>
    /// <returns>Whether the complete group is ready for the current bank boundary.</returns>
    public bool TryPrepareReservedImport(WorldTransferKey key, IReadOnlyList<WorldSocialObserverImport> members,
        out PreparedImport? prepared, out string reason) {
        prepared = null;
        if (!m_importReservations.TryGetValue(key, out var reservation)) { reason = "social import reservation is missing"; return false; }
        if (members is null || members.Count != reservation.Members.Length) { reason = "social import member count differs from reservation"; return false; }
        WorldSocialObserverImport[] copy;
        try { copy = CopyRows(members, reservation.Members.Length, reservation.Members.Length); }
        catch (ArgumentException) { reason = "invalid social import members"; return false; }
        var rows = new WorldSocialMemoryCheckpoint[copy.Length];
        var ordinal = m_nextOrdinal;
        for (var index = 0; index < copy.Length; index++) {
            var member = copy[index];
            var allowance = reservation.Members[index];
            if (member.Observer != allowance.Observer || member.SourcePolicy is null || member.Memory is null) {
                reason = "social import member differs from reservation or has no history"; return false;
            }
            if (!TryPrepareObserverImport(member.Observer, member.SourcePolicy, member.Memory, ordinal,
                allowance.Impressions, allowance.Receipts, out var incoming, out reason)) { return false; }
            rows[index] = incoming!;
            ordinal = incoming!.NextOrdinal;
        }
        prepared = new(this, key, reservation, m_nextOrdinal, ordinal, rows);
        reason = string.Empty;
        return true;
    }

    /// <summary>Consumes a prepared import without allocating. A different bank, replaced reservation, advancing
    /// clock, or changed admission ordinal refuses before any write; a consumed token cannot be replayed.</summary>
    /// <param name="prepared">The token returned by TryPrepareReservedImport.</param>
    /// <param name="reason">A named stale-token refusal or the empty string.</param>
    /// <returns>Whether every observer was installed and the reservation released.</returns>
    public bool TryCommitReservedImport(PreparedImport prepared, out string reason) {
        if (prepared is null || !ReferenceEquals(prepared.Bank, this) || prepared.Clock != EngineTick ||
            prepared.Ordinal != m_nextOrdinal || !m_importReservations.TryGetValue(prepared.Key, out var reservation) ||
            !ReferenceEquals(prepared.Reservation, reservation)) {
            reason = "social prepared import no longer matches its bank, reservation, clock, or admission ordinal";
            return false;
        }
        CancelImportReservation(prepared.Key);
        foreach (var incoming in prepared.Members) { AddCheckpointRows(incoming); }
        m_nextOrdinal = prepared.NextOrdinal;
        reason = string.Empty;
        return true;
    }

    private static bool ValidTransferKey(WorldTransferKey key) =>
        !string.IsNullOrWhiteSpace(key.SourceAuthority) && key.SourceAuthority.Length <= 512;
    private static bool ValidAllowance(WorldSocialImportAllowance member, CompiledWorldSocialPolicy policy) =>
        Valid(member.Observer) && member.Impressions >= 0 && member.Impressions <= policy.ImpressionsPerObserver &&
        member.Receipts >= 0 && member.Receipts <= policy.ReceiptCapacity;

    private void AddImportReservation(WorldTransferKey key, WorldSocialImportAllowance[] members) {
        var hash = Fnv1aHash.Create();
        hash.Add(Fnv1aHash.Compute(key.SourceAuthority.AsSpan())); hash.Add(key.TransferId); hash.Add((uint)members.Length);
        foreach (var member in members) {
            m_reservedObservers.Add(member.Observer, key);
            ReservedImpressionCount += member.Impressions; ReservedReceiptCount += member.Receipts;
            Add(ref hash, member.Observer); hash.Add((uint)member.Impressions); hash.Add((uint)member.Receipts);
        }
        m_importReservations.Add(key, new(members, hash.Value));
        AddDigest(ref m_reservationSum, ref m_reservationXor, hash.Value);
    }

    private WorldSocialImportReservationCheckpoint[] CaptureImportReservations() {
        var rows = new WorldSocialImportReservationCheckpoint[m_importReservations.Count];
        var index = 0;
        foreach (var (key, value) in m_importReservations) { rows[index++] = new(key, (WorldSocialImportAllowance[])value.Members.Clone()); }
        Array.Sort(rows, static (left, right) => {
            var comparison = StringComparer.Ordinal.Compare(left.Key.SourceAuthority, right.Key.SourceAuthority);
            return comparison != 0 ? comparison : left.Key.TransferId.CompareTo(right.Key.TransferId);
        });
        return rows;
    }

    private static WorldSocialImportReservationCheckpoint[] ValidateImportReservations(CompiledWorldSocialPolicy policy,
        IReadOnlyList<WorldSocialImportReservationCheckpoint>? rows, IEnumerable<WorldEntityAddress> knownObservers,
        int impressionCount, int receiptCount) {
        if (rows is null) { return []; }
        Require(rows.Count >= 0 && rows.Count <= MaximumReservedObservers);
        var copies = CopyRows(rows, MaximumReservedObservers);
        if (copies.Length == 0) { return copies; }
        var observers = new HashSet<WorldEntityAddress>(knownObservers);
        var keys = new HashSet<WorldTransferKey>(copies.Length);
        var remainingObservers = MaximumReservedObservers;
        var impressions = policy.ImpressionCapacity - impressionCount;
        var receipts = policy.ReceiptCapacity - receiptCount;
        for (var index = 0; index < copies.Length; index++) {
            var row = copies[index];
            Require(row is not null && ValidTransferKey(row.Key) && keys.Add(row.Key) && row.Members is not null &&
                row.Members.Count > 0 && row.Members.Count <= remainingObservers);
            var members = CopyRows(row!.Members!, remainingObservers, 1);
            remainingObservers -= members.Length;
            foreach (var member in members) {
                Require(ValidAllowance(member, policy) && observers.Add(member.Observer) &&
                    member.Impressions <= impressions && member.Receipts <= receipts);
                impressions -= member.Impressions; receipts -= member.Receipts;
            }
            copies[index] = new(row.Key, members);
        }
        return copies;
    }
}
