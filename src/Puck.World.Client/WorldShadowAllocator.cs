namespace Puck.World.Client;

/// <summary>One residency's bounded shadow identities, driven by delivered ticks and read at presented ticks.</summary>
/// <remarks>No field is resolved here. Authored identity and list ordinals must be unique. Repeated deliveries
/// cannot detect another crossing. A structural revision, changed policy, explicit discontinuity or backward
/// delivery installs the current selection without fades. A forward gap alone is not a discontinuity.</remarks>
public sealed partial class WorldShadowAllocator {
    private const int MaxSlots = 4;

    private readonly State m_current = new();
    private readonly State m_previous = new();
    private readonly WorldShadowCandidate?[] m_desired = new WorldShadowCandidate?[MaxSlots];
    private readonly WorldShadowCandidate?[] m_targets = new WorldShadowCandidate?[MaxSlots];
    private readonly bool[] m_assigned = new bool[MaxSlots];

    private WorldShadowSettings m_settings;
    private ulong m_tick;
    private long m_revision;
    private bool m_initialized;

    /// <summary>Resolves selection once at a delivered engine tick, without allocating.</summary>
    /// <param name="tick">The delivered integer engine tick.</param>
    /// <param name="structuralRevision">Changes when candidate identity, light-table layout or authored policy changes.</param>
    /// <param name="discontinuity">An explicit seek, scrub or reload; completes all old handoffs immediately.</param>
    /// <param name="candidates">The caller's already-resolved tick-state candidates.</param>
    /// <param name="settings">The owning view's already-resolved tier policy.</param>
    /// <exception cref="ArgumentOutOfRangeException">A setting or candidate lies outside its documented domain.</exception>
    public void Advance(ulong tick, long structuralRevision, bool discontinuity, ReadOnlySpan<WorldShadowCandidate> candidates, in WorldShadowSettings settings) {
        Validate(candidates: candidates, settings: in settings);
        var reset = (!m_initialized || discontinuity || (tick < m_tick) || (m_revision != structuralRevision) || (m_settings != settings));

        if (!reset && (tick == m_tick)) { return; }
        Select(candidates: candidates, count: settings.Slots);
        if (reset) {
            m_current.Clear();
            for (var slot = 0; (slot < settings.Slots); slot++) { m_current.Held[slot] = m_desired[slot]; }
            m_previous.Copy(source: m_current);
        } else {
            m_previous.Copy(source: m_current);
            for (var slot = 0; (slot < settings.Slots); slot++) {
                if ((m_current.Incoming[slot] is { } incoming) && ((tick - m_current.Crossing[slot]) >= settings.FadeTicks)) {
                    m_current.Held[slot] = incoming;
                    m_current.Incoming[slot] = null;
                }
            }
            AssignTargets(count: settings.Slots);
            ApplyTargets(settings: in settings, tick: tick);
        }
        m_tick = tick;
        m_revision = structuralRevision;
        m_settings = settings;
        m_initialized = true;
    }

    private void Select(ReadOnlySpan<WorldShadowCandidate> candidates, int count) {
        Array.Clear(array: m_desired);
        foreach (var candidate in candidates) {
            if (candidate.Mode == WorldShadowMode.Never) { continue; }
            for (var index = 0; (index < count); index++) {
                if ((m_desired[index] is { } current) && !Before(left: candidate, right: current)) { continue; }
                for (var move = (count - 1); (move > index); move--) { m_desired[move] = m_desired[(move - 1)]; }
                m_desired[index] = candidate;
                break;
            }
        }
    }
    private static bool Before(in WorldShadowCandidate left, in WorldShadowCandidate right) {
        if (left.Mode != right.Mode) { return (left.Mode == WorldShadowMode.Always); }
        if ((left.Mode == WorldShadowMode.Auto) && (left.Luminance != right.Luminance)) { return (left.Luminance > right.Luminance); }
        return (left.ListOrdinal < right.ListOrdinal);
    }
    private void AssignTargets(int count) {
        Array.Clear(array: m_targets);
        Array.Clear(array: m_assigned);
        // Preserve selected identities in their existing future slots; ranking changes alone never shuffle them.
        for (var slot = 0; (slot < count); slot++) {
            var future = (m_current.Incoming[slot] ?? m_current.Held[slot]);

            for (var wanted = 0; (wanted < count); wanted++) {
                if (!Same(left: future, right: m_desired[wanted])) { continue; }
                m_targets[slot] = m_desired[wanted];
                m_assigned[wanted] = true;
                break;
            }
        }
        for (var slot = 0; (slot < count); slot++) {
            if (m_targets[slot] is not null) { continue; }
            for (var wanted = 0; (wanted < count); wanted++) {
                if (m_assigned[wanted] || (m_desired[wanted] is null)) { continue; }
                m_targets[slot] = m_desired[wanted];
                m_assigned[wanted] = true;
                break;
            }
        }
    }
    private void ApplyTargets(ulong tick, in WorldShadowSettings settings) {
        m_current.QueuedCount = 0;
        var fading = 0;

        foreach (var incoming in m_current.Incoming) { if (incoming is not null) { fading++; } }
        for (var slot = 0; (slot < settings.Slots); slot++) {
            var target = m_targets[slot];
            var future = (m_current.Incoming[slot] ?? m_current.Held[slot]);

            if (Same(left: future, right: target)) {
                if (m_current.Incoming[slot] is not null) { m_current.Incoming[slot] = target; } else { m_current.Held[slot] = target; }
                continue;
            }
            if (target is not { } incoming) {
                if (m_current.Incoming[slot] is null) { m_current.Held[slot] = null; }
                continue;
            }
            var conflict = ParticipatesElsewhere(candidate: incoming, slot: slot);

            if ((m_current.Incoming[slot] is not null) || conflict) {
                if (settings.Overflow == WorldShadowOverflow.Instant) {
                    throw new InvalidOperationException(message: "Overlapping instant shadow handoffs require the pending owner policy.");
                }
                Queue(incoming: incoming, reason: (conflict ? WorldShadowQueueReason.IdentityInUse : WorldShadowQueueReason.SlotInHandoff), slot: slot);
            } else if ((m_current.Held[slot] is null) || (settings.FadeTicks == 0) || (settings.FadeSlots == 0) || ((fading >= settings.FadeSlots) && (settings.Overflow == WorldShadowOverflow.Instant))) {
                m_current.Held[slot] = incoming;
            } else if (fading < settings.FadeSlots) {
                m_current.Incoming[slot] = incoming;
                m_current.Crossing[slot] = tick;
                fading++;
            } else {
                Queue(incoming: incoming, reason: WorldShadowQueueReason.FadeCapacity, slot: slot);
            }
        }
    }
    private bool ParticipatesElsewhere(in WorldShadowCandidate candidate, int slot) {
        for (var index = 0; (index < MaxSlots); index++) {
            if ((index != slot) && (Same(left: m_current.Held[index], right: candidate) || Same(left: m_current.Incoming[index], right: candidate))) { return true; }
        }
        return false;
    }
    private void Queue(int slot, in WorldShadowCandidate incoming, WorldShadowQueueReason reason) =>
        m_current.Queued[m_current.QueuedCount++] = new WorldShadowQueued(Incoming: incoming, Reason: reason, Slot: slot);
    private static bool Same(WorldShadowCandidate? left, WorldShadowCandidate? right) =>
        ((left is { } first) && (right is { } second) && StringComparer.Ordinal.Equals(x: first.Name, y: second.Name));
    private static void Validate(ReadOnlySpan<WorldShadowCandidate> candidates, in WorldShadowSettings settings) {
        if ((settings.Slots < 0) || (settings.Slots > MaxSlots) || (settings.FadeSlots < 0) || (settings.FadeSlots > 2) || !Enum.IsDefined(value: settings.Overflow)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(settings));
        }
        var always = 0;

        foreach (var candidate in candidates) {
            if (string.IsNullOrEmpty(value: candidate.Name) || (candidate.ListOrdinal < 0) || (candidate.LightIndex < 0) ||
                !double.IsFinite(d: candidate.Luminance) || (candidate.Luminance < 0) || !Enum.IsDefined(value: candidate.Mode)) {
                throw new ArgumentOutOfRangeException(paramName: nameof(candidates));
            }
            if (candidate.Mode == WorldShadowMode.Always) { always++; }
        }
        if (always > MaxSlots) { throw new ArgumentOutOfRangeException(paramName: nameof(candidates), message: "At most four bodies may always cast shadows."); }
    }

    private sealed class State {
        public readonly WorldShadowCandidate?[] Held = new WorldShadowCandidate?[MaxSlots];
        public readonly WorldShadowCandidate?[] Incoming = new WorldShadowCandidate?[MaxSlots];
        public readonly ulong[] Crossing = new ulong[MaxSlots];
        public readonly WorldShadowQueued[] Queued = new WorldShadowQueued[MaxSlots];

        public int QueuedCount;

        public void Clear() { Array.Clear(array: Held); Array.Clear(array: Incoming); Array.Clear(array: Queued); QueuedCount = 0; }
        public void Copy(State source) {
            source.Held.CopyTo(array: Held, index: 0);
            source.Incoming.CopyTo(array: Incoming, index: 0);
            source.Crossing.CopyTo(array: Crossing, index: 0);
            source.Queued.CopyTo(array: Queued, index: 0);
            QueuedCount = source.QueuedCount;
        }
    }
}
