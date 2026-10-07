using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Name-keyed bounded shadow selection and handoffs, advanced only at delivered ticks.</summary>
/// <remarks>Each current and prior interval owns F fixed 32-byte handoffs. Reads derive progress from their
/// integer crossing ticks and never advance storage. The owning selection serializes delivery with reads.</remarks>
public sealed partial class WorldShadowAllocator {
    /// <summary>The maximum held slot count.</summary>
    public const int MaxSlots = 4;
    /// <summary>The maximum additional incoming march count.</summary>
    public const int MaxFadeSlots = 2;

    private readonly State m_current = new();
    private readonly State m_previous = new();
    private readonly WorldShadowSlot?[] m_selected = new WorldShadowSlot?[MaxSlots];
    private readonly WorldShadowSlot?[] m_targets = new WorldShadowSlot?[MaxSlots];
    private readonly bool[] m_assigned = new bool[MaxSlots];
    private readonly bool[] m_instant = new bool[MaxSlots];

    private WorldShadowSettings m_settings;
    private ulong m_tick;
    private long m_revision;
    private bool m_initialized;
    private bool m_resetDelivery;

    /// <summary>Resolves one completed delivery. Seeks, reloads, revisions, policy changes and backward ticks reset.</summary>
    /// <param name="tick">The delivered engine tick.</param>
    /// <param name="structuralRevision">The candidate structure's revision.</param>
    /// <param name="discontinuity">An explicit seek or reload.</param>
    /// <param name="candidates">The current named candidates in the eight-record light table.</param>
    /// <param name="settings">The resolved quality policy.</param>
    /// <param name="completeDelivery">Same-tick field samples completing a definition install.</param>
    /// <exception cref="ArgumentOutOfRangeException">A policy or candidate value is outside its domain.</exception>
    /// <exception cref="ArgumentException">Candidate names, ordinals or table indices repeat.</exception>
    public void Advance(ulong tick, long structuralRevision, bool discontinuity, ReadOnlySpan<WorldShadowCandidate> candidates, in WorldShadowSettings settings, bool completeDelivery = false) {
        Validate(candidates: candidates, settings: settings);
        var reset = (!m_initialized || discontinuity || (tick < m_tick) || (structuralRevision != m_revision) || (settings != m_settings));

        if (!reset && !completeDelivery && (tick == m_tick)) { return; }
        Select(candidates: candidates, count: settings.Slots);
        if (reset) {
            AssignTargets(count: settings.Slots, heldOnly: true);
            m_current.Clear();
            m_current.UpdateLights(candidates: candidates);
            m_targets.CopyTo(array: m_current.Held, index: 0);
            m_previous.CopyFrom(source: m_current);
        } else {
            if (tick != m_tick) { m_previous.CopyFrom(source: m_current); }
            m_current.UpdateLights(candidates: candidates);
            Retire(tick: tick);
            AssignTargets(count: settings.Slots);
            if (completeDelivery && m_resetDelivery && (tick == m_tick)) {
                Array.Clear(array: m_current.Fades);
                m_current.QueuedCount = 0;
                m_targets.CopyTo(array: m_current.Held, index: 0);
                m_previous.CopyFrom(source: m_current);
            } else {
                ApplyTargets(settings: settings, tick: tick);
            }
        }
        if (reset || (tick != m_tick)) { m_resetDelivery = reset; }
        m_tick = tick;
        m_revision = structuralRevision;
        m_settings = settings;
        m_initialized = true;
    }

    private void Retire(ulong tick) {
        for (var index = 0; (index < MaxFadeSlots); index++) {
            var fade = m_current.Fades[index];

            if (!fade.Active || (tick < fade.CrossingTick) || ((tick - fade.CrossingTick) < fade.DurationTicks)) { continue; }
            m_current.Held[fade.Slot] = Slot(candidate: m_current.Light(index: fade.IncomingLight), slot: fade.Slot);
            m_current.Fades[index] = default;
        }
    }
    private WorldShadowSlot Slot(in WorldShadowCandidate candidate, int slot) {
        return new WorldShadowSlot(slot, candidate, Rank(candidate: candidate, source: m_current));
    }
    private void Select(ReadOnlySpan<WorldShadowCandidate> candidates, int count) {
        Array.Clear(array: m_selected);
        foreach (var candidate in candidates) {
            if (candidate.Mode == WorldShadowMode.Never) { continue; }
            for (var rank = 0; (rank < count); rank++) {
                if ((m_selected[rank] is { } current) && !Before(left: candidate, right: current.Candidate, source: m_current)) { continue; }
                for (var move = (count - 1); (move > rank); move--) { m_selected[move] = m_selected[(move - 1)]; }
                m_selected[rank] = new WorldShadowSlot(Candidate: candidate, Rank: 0, Slot: 0);
                break;
            }
        }
        for (var rank = 0; (rank < count); rank++) {
            if (m_selected[rank] is { } selected) { m_selected[rank] = selected with { Rank = (rank + 1) }; }
        }
    }
    private static bool Before(in WorldShadowCandidate left, in WorldShadowCandidate right, State source) {
        if (left.Mode != right.Mode) { return (left.Mode == WorldShadowMode.Always); }
        if ((left.Mode == WorldShadowMode.Auto) && (left.Luminance != right.Luminance)) { return (left.Luminance > right.Luminance); }
        var leftSlot = source.HeldSlot(candidate: left);
        var rightSlot = source.HeldSlot(candidate: right);
        var leftHeld = (leftSlot >= 0);
        var rightHeld = (rightSlot >= 0);

        if (leftHeld != rightHeld) { return leftHeld; }
        if (leftHeld) { return (leftSlot < rightSlot); }
        return (left.ListOrdinal < right.ListOrdinal);
    }
    private void AssignTargets(int count, bool heldOnly = false) {
        Array.Clear(array: m_targets);
        Array.Clear(array: m_assigned);
        for (var slot = 0; (slot < count); slot++) {
            var future = (heldOnly ? m_current.Held[slot]?.Candidate : m_current.Future(slot: slot));

            for (var rank = 0; (rank < count); rank++) {
                if ((future is not { } held) || (m_selected[rank] is not { } selected) || !Same(left: held, right: selected.Candidate)) { continue; }
                m_targets[slot] = selected with { Slot = slot };
                m_assigned[rank] = true;
                break;
            }
        }
        for (var slot = 0; (slot < count); slot++) {
            if (m_targets[slot] is not null) { continue; }
            for (var rank = 0; (rank < count); rank++) {
                if (m_assigned[rank] || (m_selected[rank] is not { } selected)) { continue; }
                m_targets[slot] = selected with { Slot = slot };
                m_assigned[rank] = true;
                break;
            }
        }
    }
    private void ApplyTargets(ulong tick, in WorldShadowSettings settings) {
        Array.Clear(array: m_instant);
        for (var slot = 0; (slot < settings.Slots); slot++) {
            if (Same(left: m_current.Future(slot: slot), right: m_targets[slot]?.Candidate)) { continue; }
            if ((m_targets[slot] is null) || (settings.FadeTicks == 0) || (settings.FadeSlots == 0)) { m_instant[slot] = true; } else if ((Conflict(slot: slot, target: m_targets[slot]!.Value.Candidate) is not null) && InstantOverlap(overflow: settings.Overflow)) { m_instant[slot] = true; }
        }
        CompleteInstantComponent(count: settings.Slots);
        var oldQueuedCount = m_current.QueuedCount;
        Span<int> order = stackalloc int[MaxSlots];

        CrossingOrder(order: order, count: settings.Slots, oldQueuedCount: oldQueuedCount);

        m_current.QueuedCount = 0;
        for (var position = 0; (position < settings.Slots); position++) {
            var slot = order[position];

            if (m_instant[slot]) { continue; }
            var target = m_targets[slot];

            if (target is not { } incoming) { continue; }
            if (Same(left: m_current.Future(slot: slot), right: incoming.Candidate)) {
                if (m_current.FadeFor(slot: slot) < 0) { m_current.Held[slot] = incoming; }
                continue;
            }
            var conflict = Conflict(slot: slot, target: incoming.Candidate);

            if (conflict is { } reason) { Queue(slot, incoming.Candidate, reason); continue; }
            if (m_current.Held[slot] is not { } outgoing) { m_current.Held[slot] = incoming; continue; }
            var free = m_current.FreeFade(count: settings.FadeSlots);

            if (free < 0) {
                if (settings.Overflow == WorldShadowOverflow.Instant) { m_current.Held[slot] = incoming; } else { Queue(slot, incoming.Candidate, WorldShadowQueueReason.FadeCapacity); }
                continue;
            }
            var flags = WorldShadowHandoffFlags.Active | WorldShadowHandoffFlags.SelectionChanged;

            for (var queued = 0; (queued < oldQueuedCount); queued++) {
                var prior = m_current.PriorQueue[queued];

                if ((prior.Slot == slot) && Same(left: prior.Incoming, right: incoming.Candidate)) { flags |= WorldShadowHandoffFlags.FromQueue; }
            }
            m_current.Fades[free] = new WorldShadowFade(m_current.Index(candidate: outgoing.Candidate), m_current.Index(candidate: incoming.Candidate), slot, flags, tick, settings.FadeTicks);
        }
        m_current.Queued.CopyTo(array: m_current.PriorQueue, index: 0);
    }
    private void CrossingOrder(Span<int> order, int count, int oldQueuedCount) {
        var length = 0;

        for (var index = 0; (index < oldQueuedCount); index++) {
            var prior = m_current.PriorQueue[index];

            if ((prior.Slot < count) && Same(left: prior.Incoming, right: m_targets[prior.Slot]?.Candidate)) { order[length++] = prior.Slot; }
        }
        for (var slot = 0; (slot < count); slot++) {
            if (!order[..length].Contains(value: slot)) { order[length++] = slot; }
        }
    }
    private static bool InstantOverlap(WorldShadowOverflow overflow) => (overflow == WorldShadowOverflow.Instant);
    private WorldShadowQueueReason? Conflict(int slot, in WorldShadowCandidate target) {
        for (var other = 0; (other < MaxSlots); other++) {
            if (other == slot) { continue; }
            if (Same(left: m_current.Held[other]?.Candidate, right: target) || Same(left: m_current.Future(slot: other), right: target)) { return WorldShadowQueueReason.IdentityInUse; }
        }
        return ((m_current.FadeFor(slot: slot) >= 0) ? WorldShadowQueueReason.SlotInHandoff : null);
    }
    private void CompleteInstantComponent(int count) {
        // Close over every old owner of an incoming identity before publishing any replacement.
        for (var pass = 0; (pass < count); pass++) {
            for (var slot = 0; (slot < count); slot++) {
                if (!m_instant[slot] || (m_targets[slot] is not { } target)) { continue; }
                for (var other = 0; (other < count); other++) {
                    if (Same(left: m_current.Held[other]?.Candidate, right: target.Candidate) || Same(left: m_current.Future(slot: other), right: target.Candidate)) { m_instant[other] = true; }
                }
            }
        }
        for (var index = 0; (index < MaxFadeSlots); index++) {
            if ((m_current.Fades[index] is { Active: true } fade) && m_instant[fade.Slot]) { m_current.Fades[index] = default; }
        }
        for (var slot = 0; (slot < count); slot++) {
            if (m_instant[slot]) { m_current.Held[slot] = m_targets[slot]; }
        }
    }
    private void Queue(int slot, in WorldShadowCandidate incoming, WorldShadowQueueReason reason) =>
        m_current.Queued[m_current.QueuedCount++] = new WorldShadowQueued(Incoming: incoming, Reason: reason, Slot: slot);
    private static bool Same(WorldShadowCandidate? left, WorldShadowCandidate? right) =>
        ((left is { } first) && (right is { } second) && StringComparer.Ordinal.Equals(x: first.Name, y: second.Name));
    private static void Validate(ReadOnlySpan<WorldShadowCandidate> candidates, in WorldShadowSettings settings) {
        if ((settings.Slots is < 0 or > MaxSlots) || (settings.FadeSlots is < 0 or > MaxFadeSlots) || !Enum.IsDefined(value: settings.Overflow)) { throw new ArgumentOutOfRangeException(paramName: nameof(settings)); }
        for (var index = 0; (index < candidates.Length); index++) {
            var candidate = candidates[index];

            if (string.IsNullOrWhiteSpace(value: candidate.Name) || (candidate.LightIndex is < 0 or >= SdfLights.MaxLights) || (candidate.ListOrdinal < 0) || !double.IsFinite(d: candidate.Luminance) || (candidate.Luminance < 0) || !Enum.IsDefined(value: candidate.Mode)) { throw new ArgumentOutOfRangeException(paramName: nameof(candidates)); }
            for (var prior = 0; (prior < index); prior++) {
                if (Same(left: candidates[prior], right: candidate) || (candidates[prior].ListOrdinal == candidate.ListOrdinal) || (candidates[prior].LightIndex == candidate.LightIndex)) { throw new ArgumentException(message: "Shadow candidates require unique names, table indices and authored ordinals.", paramName: nameof(candidates)); }
            }
        }
    }
}
