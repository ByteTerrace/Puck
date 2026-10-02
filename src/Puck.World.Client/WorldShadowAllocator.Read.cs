using Puck.Hosting;

namespace Puck.World.Client;

public sealed partial class WorldShadowAllocator {
    /// <summary>Copies the most recent delivered interval's identities at a presented tick, without advancing or allocating.</summary>
    /// <param name="tick">The frame's presented tick; normally between the two most recent deliveries.</param>
    /// <param name="stable">Receives held stable slots, at most four. A completed handoff emits only its incoming body here.</param>
    /// <param name="handoffs">Receives extra incoming marches, at most two, paired with stable outgoing slots.</param>
    /// <param name="queued">Receives the current desired assignments waiting on a slot, handoff or identity.</param>
    /// <returns>Initialized span-prefix counts and exact active march count.</returns>
    /// <exception cref="ArgumentException">An output span cannot hold the result; no output is written.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The tick fraction is nonfinite or outside zero inclusive to one exclusive.</exception>
    public WorldShadowReadout CopyTo(in PresentedTick tick, Span<WorldShadowSlot> stable, Span<WorldShadowHandoff> handoffs, Span<WorldShadowQueued> queued) {
        if (!double.IsFinite(d: tick.Fraction) || (tick.Fraction < 0) || (tick.Fraction >= 1)) { throw new ArgumentOutOfRangeException(paramName: nameof(tick)); }
        if (!m_initialized) { return default; }
        var source = ((tick.Whole < m_tick) ? m_previous : m_current);
        var heldCount = 0;
        var fadeCount = 0;

        for (var slot = 0; (slot < m_settings.Slots); slot++) {
            if (source.Held[slot] is null) { continue; }
            heldCount++;
            if ((source.Incoming[slot] is not null) && (Weight(tick: in tick, crossing: source.Crossing[slot]) < 1)) { fadeCount++; }
        }
        if ((stable.Length < heldCount) || (handoffs.Length < fadeCount) || (queued.Length < source.QueuedCount)) {
            throw new ArgumentException(message: "The shadow output spans cannot hold this presented result.");
        }
        var heldIndex = 0;
        var fadeIndex = 0;

        for (var slot = 0; (slot < m_settings.Slots); slot++) {
            if (source.Held[slot] is not { } outgoing) { continue; }
            if (source.Incoming[slot] is { } incoming) {
                var weight = Weight(tick: in tick, crossing: source.Crossing[slot]);

                if (weight >= 1) { outgoing = incoming; } else { handoffs[fadeIndex++] = new WorldShadowHandoff(Slot: slot, Outgoing: outgoing, Incoming: incoming, CrossingTick: source.Crossing[slot], Weight: weight); }
            }
            stable[heldIndex++] = new WorldShadowSlot(Candidate: outgoing, Slot: slot);
        }
        source.Queued.AsSpan(length: source.QueuedCount, start: 0).CopyTo(destination: queued);
        return new WorldShadowReadout(HandoffCount: fadeCount, MarchSlots: (heldCount + fadeCount), QueuedCount: source.QueuedCount, StableCount: heldCount);
    }

    private double Weight(in PresentedTick tick, ulong crossing) {
        if (tick.Whole < crossing) { return 0; }
        var elapsed = (tick.Whole - crossing);

        if ((m_settings.FadeTicks == 0) || (elapsed >= m_settings.FadeTicks)) { return 1; }
        return Math.Clamp(value: ((elapsed + tick.Fraction) / m_settings.FadeTicks), min: 0, max: 1);
    }
}
