using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>A directed impression. Use a mobility identity's original Incarnation, not its current body slot or ownership epoch.</summary>
public readonly record struct WorldSocialImpressionKey(WorldEntityAddress Observer, WorldEntityAddress Subject, int Dimension);

/// <summary>The underlying event's identity, retained unchanged across relays. Aspect distinguishes separately observable
/// attempts/outcomes; it is a non-empty token of at most 64 UTF-16 characters. Sequence is minted by Origin, not a relay.</summary>
public readonly record struct WorldSocialEventKey(WorldEntityAddress Origin, string Aspect, ulong Sequence);

/// <summary>An admitted observation or report. Value and Quality are raw Q48.16; quality lies in [0,1]. OccurredAt is
/// the immutable original event timestamp, not the report's arrival tick. LocalOccurredAt optionally projects that
/// instant onto this bank's clock; absent a projection, OccurredAt must already use this clock. A retained receipt
/// keeps its existing aging anchor, so a later projection cannot refresh that event. Null Source means direct
/// observation. This value carries no clock, authority, or visibility proof: the caller must enforce clock projection,
/// actual perception, provenance, and authorization, including after an expired receipt has been reclaimed.</summary>
public readonly record struct WorldSocialEvidence(
    WorldSocialImpressionKey Impression, WorldSocialEventKey Event, ulong OccurredAt,
    long Value, long Quality, WorldEntityAddress? Source = null, Int128? LocalOccurredAt = null
);

/// <summary>An evidence-ingestion outcome; refusals never partially change impressions or the receipt ledger.</summary>
public enum WorldSocialEvidenceResult : byte {
    /// <summary>A new independent event changed memory.</summary>
    Accepted,
    /// <summary>A direct observation superseded the remembered report, without adding an independent event.</summary>
    Upgraded,
    /// <summary>The first contradictory version of a remembered event raised uncertainty, without adding support.</summary>
    Conflict,
    /// <summary>The same event was already processed, or an earlier direct observation dominates this copy.</summary>
    Duplicate,
    /// <summary>An address, dimension, value, quality, event token, or contradictory original timestamp is invalid.</summary>
    Invalid,
    /// <summary>The new event's projected occurrence is later than this bank's clock.</summary>
    Future,
    /// <summary>The original event is older than the authored admission window.</summary>
    Stale,
    /// <summary>Quality, source reliability, or authored weight produced no effective evidence.</summary>
    ZeroWeight,
    /// <summary>The authored ingestion-attempt budget is exhausted.</summary>
    WorkLimited,
    /// <summary>The exact unexpired-receipt ledger is full; no old receipt was discarded.</summary>
    ReceiptCapacityLimited,
    /// <summary>The total or per-observer impression capacity is full; no impression was evicted.</summary>
    ImpressionCapacityLimited,
    /// <summary>A monotonic receipt ordinal or independent-event counter cannot advance without overflow.</summary>
    SequenceExhausted,
    /// <summary>The observer is reserved for incoming ownership; ordinary evidence cannot create memory for it.</summary>
    ObserverReserved,
    /// <summary>The observer's source memory is frozen for an unresolved ownership transfer.</summary>
    ObserverFrozen,
}

/// <summary>A read-only, lazily aged impression. Numeric values are raw Q48.16. Confidence is a bounded heuristic,
/// not a probability; AgeTicks is time since the last accepted update, not time since the underlying event,
/// saturated at UInt64.MaxValue when an imported history is older than that representation.</summary>
public readonly record struct WorldSocialImpression(
    bool Known, long Value, long Weight, long Confidence, long Uncertainty, ulong IndependentEvents, ulong AgeTicks
);

/// <summary>A bounded, deterministic social-memory bank. Direct and reported evidence update contextual impressions;
/// source reliability is a separate dimension. Persistent impressions and the duplicate ledger have separate lifetimes.</summary>
/// <remarks>Single-writer simulation object. Dictionaries, observer indexes, and the expiry heap are reserved at construction. Ingestion,
/// reads, bounded reclamation, and the cached digest do not allocate after construction. Simulation methods do not scan all pairs.
/// Capture/Restore are deliberate cold paths. Repeated reports cannot accumulate support; a report may be upgraded to
/// direct evidence once. This accumulator is not itself a sensor, an authoring rule binding, or a transfer owner.</remarks>
public sealed partial class WorldSocialMemory {
    private static readonly long One = FixedQ4816.One.Value;
    private readonly Dictionary<WorldSocialImpressionKey, ImpressionState> m_impressions;
    private readonly Dictionary<WorldEntityAddress, ObserverState> m_observers;
    private readonly ObserverLinks<WorldSocialImpressionKey> m_impressionOwners;
    private readonly ObserverLinks<ReceiptKey> m_receiptOwners;
    private readonly Dictionary<ReceiptKey, ReceiptState> m_receipts;
    private readonly ReceiptExpiry m_expiry;
    private readonly ulong m_policyHash;
    private ulong m_impressionSum;
    private ulong m_impressionXor;
    private ulong m_receiptSum;
    private ulong m_receiptXor;
    private ulong m_nextOrdinal;

    private readonly record struct ReceiptKey(WorldSocialImpressionKey Impression, WorldSocialEventKey Event);
    private readonly record struct ImpressionState(long Value, long Weight, long Uncertainty, Int128 UpdatedAt, ulong IndependentEvents, ulong FirstReceiptOrdinal, ulong CachedDigest = 0, int OwnerNode = -1);
    private readonly record struct ReceiptState(
        ulong OccurredAt, Int128 LocalOccurredAt, ulong Ordinal, long Value, long Weight, bool Direct, bool ConflictSeen,
        WorldEntityAddress? OriginalSource, long OriginalValue, ulong CachedDigest = 0, int OwnerNode = -1
    );

    /// <summary>Constructs a bank and reserves its bounded storage.</summary>
    /// <param name="policy">An immutable, validated policy.</param>
    /// <exception cref="ArgumentNullException">The policy is null.</exception>
    public WorldSocialMemory(CompiledWorldSocialPolicy policy) {
        ArgumentNullException.ThrowIfNull(policy);
        Policy = policy;
        m_impressions = new(policy.ImpressionCapacity);
        // An observer can retain only receipts after forgetting every impression. The two disjoint populations
        // therefore bound this directory together; reserving only impression capacity could allocate during Observe.
        m_observers = new(checked(policy.ImpressionCapacity + policy.ReceiptCapacity));
        m_impressionOwners = new(policy.ImpressionCapacity);
        m_receiptOwners = new(policy.ReceiptCapacity);
        m_receipts = new(policy.ReceiptCapacity);
        m_expiry = new(policy.ReceiptCapacity);
        m_policyHash = Fnv1aHash.Compute(policy.Identity.AsSpan());
    }

    /// <summary>The immutable policy owning this bank.</summary>
    public CompiledWorldSocialPolicy Policy { get; }
    /// <summary>The current engine tick, changed only by Advance or checkpoint restore.</summary>
    public ulong EngineTick { get; private set; }
    /// <summary>Ingestion attempts consumed at the current clock boundary, including invalid and repeated evidence.</summary>
    public int EvidenceAttempts { get; private set; }
    /// <summary>Receipts reclaimed at the current clock boundary.</summary>
    public int ReclaimedReceipts { get; private set; }
    /// <summary>Retained directed impression count; each dimension counts separately.</summary>
    public int ImpressionCount => m_impressions.Count;
    /// <summary>Retained exact-deduplication receipts, including any expired entries awaiting bounded reclamation.</summary>
    public int ReceiptCount => m_receipts.Count;

    /// <summary>Advances the monotonic engine clock, resets the ingestion allowance, and reclaims no more than the
    /// authored receipt budget. Repeating the same tick does nothing; a large jump grants one allowance, not catch-up work.</summary>
    /// <param name="engineTick">The new engine clock boundary.</param>
    /// <exception cref="ArgumentOutOfRangeException">The clock would go backwards.</exception>
    public void Advance(ulong engineTick) {
        if (engineTick < EngineTick) { throw new ArgumentOutOfRangeException(nameof(engineTick), "social clock cannot go backwards"); }
        if (engineTick == EngineTick) { return; }
        EngineTick = engineTick;
        EvidenceAttempts = 0;
        ReclaimedReceipts = 0;
        while (ReclaimedReceipts < Policy.ExpiredReceiptsPerTick && m_expiry.TryPeek(out var node, out var occurredAt) &&
            Elapsed(occurredAt) > Policy.EvidenceLifetimeTicks) {
            RemoveReceipt(m_receiptOwners.Key(node));
            ReclaimedReceipts++;
        }
    }

    /// <summary>Reads one directed impression without allocating or changing its decay anchor. An unknown valid key
    /// returns its dimension baseline and zero confidence. Invalid keys return false and the default result.</summary>
    /// <param name="key">Observer, subject, and dimension.</param>
    /// <param name="impression">The current lazily aged value.</param>
    /// <returns>Whether the key is valid, independently of whether it is remembered.</returns>
    public bool TryRead(WorldSocialImpressionKey key, out WorldSocialImpression impression) {
        impression = default;
        if (!Valid(key)) { return false; }
        var d = Policy.Dimensions[key.Dimension];
        if (!m_impressions.TryGetValue(key, out var state)) {
            impression = new(false, d.Baseline, 0, 0, 0, 0, 0);
            return true;
        }
        var aged = Age(state, d);
        var confidence = Multiply(Ratio(aged.Weight, d.PriorWeight + aged.Weight), One - aged.Uncertainty);
        impression = new(true, aged.Value, aged.Weight, confidence, aged.Uncertainty, aged.IndependentEvents,
            (ulong)UInt128.Min(Elapsed(state.UpdatedAt), ulong.MaxValue));
        return true;
    }

    /// <summary>Forgets one impression. Unexpired evidence receipts remain, so the same rumor cannot recreate it.
    /// A later independent event can create a new impression if capacity allows. Frozen source owners refuse forgetting.</summary>
    /// <param name="key">The exact directed impression to forget.</param>
    /// <returns>Whether a remembered entry was removed; false for an absent entry or frozen observer.</returns>
    public bool Forget(WorldSocialImpressionKey key) => !IsObserverFrozen(key.Observer) && ForgetCore(key);

    private bool ForgetCore(WorldSocialImpressionKey key) {
        if (!m_impressions.Remove(key, out var old)) { return false; }
        RemoveDigest(ref m_impressionSum, ref m_impressionXor, old.CachedDigest);
        var owner = Owner(key.Observer);
        SetOwner(key.Observer, owner with {
            ImpressionHead = m_impressionOwners.Remove(old.OwnerNode, owner.ImpressionHead),
            ImpressionCount = owner.ImpressionCount - 1,
        });
        return true;
    }

    /// <summary>Consumes one bounded evidence attempt. Original event IDs, timestamps, and provenance must be
    /// retained across relays. Returns an explicit result on all capacity and input refusals.</summary>
    /// <param name="evidence">Evidence the caller has already authorized and made observable to this observer.</param>
    /// <returns>The ingestion outcome. A refusal changes only the attempt counter.</returns>
    public WorldSocialEvidenceResult Observe(in WorldSocialEvidence evidence) {
        if (EvidenceAttempts >= Policy.EvidenceAttemptsPerTick) { return WorldSocialEvidenceResult.WorkLimited; }
        EvidenceAttempts++;
        if (!Valid(evidence.Impression) || !Valid(evidence.Event.Origin) ||
            string.IsNullOrWhiteSpace(evidence.Event.Aspect) || evidence.Event.Aspect.Length > 64 ||
            (evidence.Source is { } source && !Valid(source)) || evidence.Quality < 0 || evidence.Quality > One) {
            return WorldSocialEvidenceResult.Invalid;
        }
        var dimension = Policy.Dimensions[evidence.Impression.Dimension];
        if (evidence.Value < dimension.Minimum || evidence.Value > dimension.Maximum) { return WorldSocialEvidenceResult.Invalid; }
        if (IsObserverFrozen(evidence.Impression.Observer)) { return WorldSocialEvidenceResult.ObserverFrozen; }
        if (m_reservedObservers.ContainsKey(evidence.Impression.Observer)) { return WorldSocialEvidenceResult.ObserverReserved; }
        var receiptKey = new ReceiptKey(evidence.Impression, evidence.Event);
        var localOccurredAt = evidence.LocalOccurredAt ?? (Int128)evidence.OccurredAt;
        var repeated = m_receipts.TryGetValue(receiptKey, out var previous);
        if (repeated) {
            if (previous.OccurredAt != evidence.OccurredAt) { return WorldSocialEvidenceResult.Invalid; }
            if (Elapsed(previous.LocalOccurredAt) > Policy.EvidenceLifetimeTicks) { return WorldSocialEvidenceResult.Stale; }
        } else {
            if (localOccurredAt > EngineTick) { return WorldSocialEvidenceResult.Future; }
            if (Elapsed(localOccurredAt) > Policy.EvidenceLifetimeTicks) { return WorldSocialEvidenceResult.Stale; }
        }
        var weight = EvidenceWeight(evidence);
        if (weight == 0) { return WorldSocialEvidenceResult.ZeroWeight; }
        var known = m_impressions.TryGetValue(evidence.Impression, out var stored);
        if (repeated) {
            // Forgotten impressions stay forgotten even if a direct copy of an old event arrives.
            if (!known || previous.Ordinal < stored.FirstReceiptOrdinal) { return WorldSocialEvidenceResult.Duplicate; }
            if (!previous.Direct && evidence.Source is null) {
                var current = Age(stored, dimension);
                var upgraded = Learn(current, dimension, evidence.Value, weight, Math.Max(0, weight - previous.Weight), false);
                Put(evidence.Impression, upgraded);
                Put(receiptKey, previous with { Value = evidence.Value, Weight = Math.Max(previous.Weight, weight), Direct = true });
                return WorldSocialEvidenceResult.Upgraded;
            }
            if (!previous.ConflictSeen && evidence.Value != previous.Value && !previous.Direct) {
                var current = Age(stored, dimension);
                var gain = Multiply(dimension.ConflictGain, Ratio(weight, dimension.PriorWeight + weight));
                Put(evidence.Impression, current with { Uncertainty = Math.Min(One, current.Uncertainty + gain), UpdatedAt = EngineTick });
                Put(receiptKey, previous with { ConflictSeen = true });
                return WorldSocialEvidenceResult.Conflict;
            }
            return WorldSocialEvidenceResult.Duplicate;
        }
        if (m_receipts.Count >= Policy.ReceiptCapacity - ReservedReceiptCount) { return WorldSocialEvidenceResult.ReceiptCapacityLimited; }
        if (!known && (m_impressions.Count >= Policy.ImpressionCapacity - ReservedImpressionCount ||
            Owner(evidence.Impression.Observer).ImpressionCount >= Policy.ImpressionsPerObserver)) {
            return WorldSocialEvidenceResult.ImpressionCapacityLimited;
        }
        if (m_nextOrdinal == ulong.MaxValue || (known && stored.IndependentEvents == ulong.MaxValue)) {
            return WorldSocialEvidenceResult.SequenceExhausted;
        }
        var state = known ? Age(stored, dimension) : new ImpressionState(dimension.Baseline, 0, 0, EngineTick, 0, m_nextOrdinal);
        if (evidence.Source is null) {
            // Only a NEW direct event receives the follow-up boost; relays and upgrades cannot pump it.
            weight = Multiply(weight, One + Multiply(state.Uncertainty, dimension.FollowUpBoost));
        }
        var next = Learn(state, dimension, evidence.Value, weight, weight, true);
        Put(evidence.Impression, next);
        var receipt = new ReceiptState(evidence.OccurredAt, localOccurredAt, m_nextOrdinal++, evidence.Value, weight,
            evidence.Source is null, false, evidence.Source, evidence.Value);
        Put(receiptKey, receipt);
        return WorldSocialEvidenceResult.Accepted;
    }

    private long EvidenceWeight(in WorldSocialEvidence evidence) {
        if (evidence.Source is not { } source) { return Multiply(evidence.Quality, Policy.DirectWeight); }
        var reliability = Policy.UnfamiliarReliability;
        if (Policy.ReliabilityDimension >= 0 && TryRead(new(evidence.Impression.Observer, source, Policy.ReliabilityDimension), out var learned)) {
            reliability += Multiply(learned.Value - reliability, learned.Confidence);
        }
        return Multiply(Multiply(evidence.Quality, Policy.ReportWeight), reliability);
    }

    private ImpressionState Learn(ImpressionState state, CompiledWorldSocialDimension d, long value, long weight, long support, bool independent) {
        var difference = value - state.Value;
        var fraction = Ratio(weight, d.PriorWeight + state.Weight + weight);
        var delta = Multiply(Multiply(difference, fraction), d.LearningRate);
        delta = Math.Clamp(delta, -d.MaximumChange, d.MaximumChange);
        var uncertainty = state.Uncertainty;
        if (state.IndependentEvents != 0 && independent) {
            var strength = Ratio(weight, d.PriorWeight + weight);
            uncertainty += Math.Abs(difference) > d.ConflictThreshold
                ? Multiply(d.ConflictGain, strength) : -Multiply(d.ConsistencyGain, strength);
        }
        return new(Math.Clamp(state.Value + delta, d.Minimum, d.Maximum), Math.Min(d.WeightCapacity, state.Weight + support),
            Math.Clamp(uncertainty, 0, One), EngineTick, state.IndependentEvents + (independent ? 1UL : 0UL), state.FirstReceiptOrdinal);
    }

    private ImpressionState Age(ImpressionState state, CompiledWorldSocialDimension d) {
        var elapsed = Elapsed(state.UpdatedAt);
        var value = state.Value;
        var weight = state.Weight;
        if (d.RecoveryTicks != 0) {
            value += (long)(((Int128)(d.Baseline - value) * (Int128)UInt128.Min(elapsed, d.RecoveryTicks)) / d.RecoveryTicks);
        }
        if (d.ConfidenceDecayTicks != 0) {
            weight -= (long)(((UInt128)(ulong)weight * UInt128.Min(elapsed, d.ConfidenceDecayTicks)) / d.ConfidenceDecayTicks);
        }
        return state with { Value = value, Weight = weight };
    }

    // Imported anchors can precede the destination clock's zero. This widening handles even Int128.MinValue
    // without negating it or overflowing a signed subtraction; valid anchors never exceed EngineTick.
    private UInt128 Elapsed(Int128 anchor) => anchor >= 0 ? (UInt128)((Int128)EngineTick - anchor) :
        (UInt128)EngineTick + (UInt128)(-(anchor + 1)) + 1;

    // Bounded Q48.16 operations use widened intermediates and truncate toward zero at each narrowing.
    private static long Multiply(long a, long b) => (long)(((Int128)a * b) / One);
    private static long Ratio(long numerator, long denominator) => (long)(((Int128)numerator * One) / denominator);
    private bool Valid(WorldSocialImpressionKey key) => Valid(key, Policy);
    private static bool Valid(WorldSocialImpressionKey key, CompiledWorldSocialPolicy policy) =>
        Valid(key.Observer) && Valid(key.Subject) && (uint)key.Dimension < (uint)policy.Dimensions.Length;
    private static bool Valid(WorldEntityAddress address) => !string.IsNullOrWhiteSpace(address.Authority) && address.Authority.Length <= 512 && address.Index >= 0 && address.Generation >= 0;

    private void Put(WorldSocialImpressionKey key, ImpressionState value) {
        if (m_impressions.TryGetValue(key, out var old)) {
            RemoveDigest(ref m_impressionSum, ref m_impressionXor, old.CachedDigest);
            value = value with { OwnerNode = old.OwnerNode };
        } else {
            var owner = Owner(key.Observer);
            var node = m_impressionOwners.Add(key, owner.ImpressionHead);
            value = value with { OwnerNode = node };
            SetOwner(key.Observer, owner with { ImpressionHead = node, ImpressionCount = owner.ImpressionCount + 1 });
        }
        value = value with { CachedDigest = Digest(key, value) };
        m_impressions[key] = value;
        AddDigest(ref m_impressionSum, ref m_impressionXor, value.CachedDigest);
    }
    private void Put(ReceiptKey key, ReceiptState value) {
        if (m_receipts.TryGetValue(key, out var old)) {
            RemoveDigest(ref m_receiptSum, ref m_receiptXor, old.CachedDigest);
            value = value with { OwnerNode = old.OwnerNode };
        } else {
            var observer = key.Impression.Observer;
            var owner = Owner(observer);
            var node = m_receiptOwners.Add(key, owner.ReceiptHead);
            m_expiry.Add(node, value.LocalOccurredAt, value.Ordinal);
            value = value with { OwnerNode = node };
            SetOwner(observer, owner with { ReceiptHead = node, ReceiptCount = owner.ReceiptCount + 1 });
        }
        value = value with { CachedDigest = Digest(key, value) };
        m_receipts[key] = value;
        AddDigest(ref m_receiptSum, ref m_receiptXor, value.CachedDigest);
    }

    private void RemoveReceipt(ReceiptKey key) {
        var receipt = m_receipts[key];
        if (!IsObserverFrozen(key.Impression.Observer)) { m_expiry.Remove(receipt.OwnerNode); }
        RemoveDigest(ref m_receiptSum, ref m_receiptXor, receipt.CachedDigest);
        m_receipts.Remove(key);
        var owner = Owner(key.Impression.Observer);
        SetOwner(key.Impression.Observer, owner with {
            ReceiptHead = m_receiptOwners.Remove(receipt.OwnerNode, owner.ReceiptHead),
            ReceiptCount = owner.ReceiptCount - 1,
        });
    }

    /// <summary>Gets a cached, order-independent digest of logical memory and future-affecting clock/work state.
    /// It is a replay diagnostic, not a cryptographic commitment. Dictionary layout and allocation history are excluded.</summary>
    public ulong StateHash {
        get {
            var hash = Fnv1aHash.Create();
            hash.Add(m_policyHash); hash.Add(EngineTick); hash.Add((uint)EvidenceAttempts); hash.Add((uint)ReclaimedReceipts);
            hash.Add(m_nextOrdinal); hash.Add((uint)m_impressions.Count); hash.Add(m_impressionSum); hash.Add(m_impressionXor);
            hash.Add((uint)m_receipts.Count); hash.Add(m_receiptSum); hash.Add(m_receiptXor);
            hash.Add((uint)ImportReservationCount); hash.Add((uint)ReservedObserverCount);
            hash.Add((uint)ReservedImpressionCount); hash.Add((uint)ReservedReceiptCount);
            hash.Add(m_reservationSum); hash.Add(m_reservationXor);
            hash.Add((uint)FrozenObserverCount); hash.Add(m_frozenSum); hash.Add(m_frozenXor);
            return hash.Value;
        }
    }

    private static void AddDigest(ref ulong sum, ref ulong xor, ulong digest) { sum = unchecked(sum + digest); xor ^= BitOperations.RotateLeft(digest, (int)(digest & 63)); }
    private static void RemoveDigest(ref ulong sum, ref ulong xor, ulong digest) { sum = unchecked(sum - digest); xor ^= BitOperations.RotateLeft(digest, (int)(digest & 63)); }
    private static void Add(ref Fnv1aHash hash, WorldEntityAddress address) {
        hash.Add(Fnv1aHash.Compute(address.Authority.AsSpan())); hash.Add((long)address.Index); hash.Add((long)address.Generation);
    }
    private static void Add(ref Fnv1aHash hash, WorldSocialImpressionKey key) {
        Add(ref hash, key.Observer); Add(ref hash, key.Subject); hash.Add((long)key.Dimension);
    }
    private static void AddTime(ref Fnv1aHash hash, Int128 anchor) {
        hash.Add(unchecked((ulong)anchor)); hash.Add(unchecked((ulong)(anchor >> 64)));
    }
    private static ulong Digest(WorldSocialImpressionKey key, ImpressionState state) {
        var hash = Fnv1aHash.Create(); Add(ref hash, key);
        hash.Add(state.Value); hash.Add(state.Weight); hash.Add(state.Uncertainty); AddTime(ref hash, state.UpdatedAt); hash.Add(state.IndependentEvents); hash.Add(state.FirstReceiptOrdinal);
        return hash.Value;
    }
    private static ulong Digest(ReceiptKey key, ReceiptState state) {
        var hash = Fnv1aHash.Create(); Add(ref hash, key.Impression); Add(ref hash, key.Event.Origin);
        hash.Add(Fnv1aHash.Compute(key.Event.Aspect.AsSpan())); hash.Add(key.Event.Sequence);
        hash.Add(state.OccurredAt); AddTime(ref hash, state.LocalOccurredAt); hash.Add(state.Ordinal); hash.Add(state.Value); hash.Add(state.Weight);
        hash.Add((byte)(state.Direct ? 1 : 0)); hash.Add((byte)(state.ConflictSeen ? 1 : 0));
        hash.Add((byte)(state.OriginalSource.HasValue ? 1 : 0));
        if (state.OriginalSource is { } source) { Add(ref hash, source); }
        hash.Add(state.OriginalValue);
        return hash.Value;
    }
}
