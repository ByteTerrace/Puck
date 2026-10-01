namespace Puck.World;

/// <summary>
/// The anchors one presentation-tier recipient holds, one row per state clock its projection carries: the last anchor
/// the authority sent it. The authority predicts each clock from the row exactly as the recipient does
/// (<see cref="WorldClockAnchor.Predict"/>) and sends a new anchor whenever that prediction differs from its own phase
/// at an authoritative tick, and at no other time. A clock whose row holds no number sends nothing: a recipient that
/// holds an anchor keeps predicting from it, and a recipient that joins then seeds from the clock's load-validated
/// phase (<see cref="WorldClockAnchors.Seeds"/>), so early and late views may differ while the clock reads none.
/// <para>Each row is counted retained when it is first sent and released when the recipient leaves or loses
/// disclosure (<see cref="Release"/>), under <see cref="WorldProjectionWork"/>. Not thread-safe: one recipient's
/// deliveries run on the thread that steps its world.</para>
/// </summary>
/// <param name="seeds">The phases a late view seeds a clock from while its row holds no number, by clock name, or
/// <see langword="null"/> for none.</param>
public sealed class WorldClockAnchorLedger(IReadOnlyDictionary<string, ulong>? seeds = null) {
    private readonly List<string> m_carried = [];
    private readonly Dictionary<string, WorldClockAnchor> m_held = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets the number of anchor rows held.</summary>
    public int Count => m_held.Count;

    /// <summary>Returns the anchor the recipient holds for a clock.</summary>
    /// <param name="clock">The clock's name.</param>
    /// <param name="anchor">The held anchor, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the recipient holds an anchor of the clock.</returns>
    public bool TryHeld(string clock, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldClockAnchor? anchor) => m_held.TryGetValue(
        key: clock,
        value: out anchor
    );
    /// <summary>Returns the clock a projection composed at an authoritative tick carries for a state clock: an anchored
    /// clock holding the anchor the recipient already holds while its prediction still matches, else a fresh anchor,
    /// recorded as sent; a clock whose row holds no number keeps the held anchor, or takes its seed when none is held,
    /// or carries none.</summary>
    /// <param name="definition">The authority's installed document.</param>
    /// <param name="clock">The state clock.</param>
    /// <param name="tick">The authoritative simulation tick.</param>
    /// <param name="engineTick">The engine tick that simulation tick stands at.</param>
    /// <returns>The anchored clock, named and spanned as the state clock is.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="clock"/> is
    /// <see langword="null"/>.</exception>
    public WorldClock Carry(WorldDefinition definition, WorldClock clock, ulong tick, ulong engineTick) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: clock);

        if (!m_carried.Contains(item: clock.Name)) {
            m_carried.Add(item: clock.Name);
        }

        var current = WorldClockAnchors.Read(
            clock: clock,
            definition: definition,
            engineTick: engineTick,
            tick: tick
        );

        if ((current is null) && !m_held.ContainsKey(key: clock.Name) && (seeds is not null) && seeds.TryGetValue(
            key: clock.Name,
            value: out var seed
        )) {
            current = new WorldClockAnchor(
                Phase: seed,
                Tick: engineTick
            );
        }

        if (current is not null) {
            _ = Note(
                clock: clock.Name,
                current: current,
                engineTick: engineTick
            );
        }

        return new WorldClock(
            Anchor: (m_held.TryGetValue(
                key: clock.Name,
                value: out var held
            )
                ? held
                : null),
            Name: clock.Name,
            SpanSeconds: clock.SpanSeconds
        );
    }
    /// <summary>Re-anchors, at an authoritative tick, every carried clock whose held prediction differs from the
    /// authority's phase, and only those.</summary>
    /// <param name="definition">The authority's installed document.</param>
    /// <param name="tick">The authoritative simulation tick.</param>
    /// <param name="engineTick">The engine tick that simulation tick stands at.</param>
    /// <returns><see langword="true"/> when an anchor was sent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public bool Step(WorldDefinition definition, ulong tick, ulong engineTick) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var sent = false;

        foreach (var name in m_carried) {
            if (
                !WorldKeyResolver.TryClock(
                clock: out var clock,
                name: name,
                timeline: definition.Timeline
            ) ||
                !clock.IsStateClock ||
                (WorldClockAnchors.Read(
                clock: clock,
                definition: definition,
                engineTick: engineTick,
                tick: tick
            ) is not { } current)
            ) {
                continue;
            }

            sent |= Note(
                clock: name,
                current: current,
                engineTick: engineTick
            );
        }

        return sent;
    }
    /// <summary>Releases every row the recipient holds: it left, or lost disclosure, and is sent nothing derived until a
    /// projection is composed for it again.</summary>
    public void Release() {
        if (m_held.Count > 0) {
            WorldProjectionWork.Count(
                amount: m_held.Count,
                kind: WorldProjectionWork.AnchorRowsReleased
            );
        }

        m_held.Clear();
        m_carried.Clear();
    }

    // Records the current anchor when the recipient's prediction misses it; answers whether one was sent.
    private bool Note(string clock, WorldClockAnchor current, ulong engineTick) {
        var retained = m_held.TryGetValue(
            key: clock,
            value: out var held
        );

        if (retained && (held!.Predict(engineTick: engineTick) == current.Phase)) {
            return false;
        }

        if (!retained) {
            WorldProjectionWork.Count(kind: WorldProjectionWork.AnchorRowsRetained);
        }

        m_held[clock] = current;
        WorldProjectionWork.Count(kind: WorldProjectionWork.Anchors);

        return true;
    }
}
