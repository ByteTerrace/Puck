using Puck.Hosting;

namespace Puck.World.Client;

public sealed partial class WorldShadowAllocator {
    /// <summary>Copies a presented assignment and its extra marches without allocating or advancing any state.</summary>
    /// <param name="tick">The tick in the latest delivered interval.</param>
    /// <param name="stable">Held outgoing slots, or the sole incoming owner after exact completion.</param>
    /// <param name="handoffs">Active extra incoming marches and per-light visibility controls.</param>
    /// <param name="queued">Current desired targets waiting at the represented delivery.</param>
    /// <returns>The initialized prefixes of the caller-owned spans and the complete active march count.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The fraction is outside zero inclusive to one exclusive.</exception>
    /// <exception cref="ArgumentException">Any span is too short; no span is changed.</exception>
    public WorldShadowReadout CopyTo(in PresentedTick tick, Span<WorldShadowSlot> stable, Span<WorldShadowHandoff> handoffs, Span<WorldShadowQueued> queued) {
        if (!double.IsFinite(d: tick.Fraction) || (tick.Fraction < 0) || (tick.Fraction >= 1)) { throw new ArgumentOutOfRangeException(paramName: nameof(tick)); }
        if (!m_initialized) { return default; }
        var source = ((tick.Whole < m_tick) ? m_previous : m_current);
        var heldCount = 0;
        var fadeCount = 0;
        Span<double> weights = stackalloc double[MaxFadeSlots];

        foreach (var held in source.Held) { if (held is not null) { heldCount++; } }
        for (var index = 0; (index < MaxFadeSlots); index++) {
            var fade = source.Fades[index];

            if (!fade.Active) { continue; }
            weights[index] = Weight(fade: fade, tick: tick);
            if (weights[index] < 1) { fadeCount++; }
        }
        if ((stable.Length < heldCount) || (handoffs.Length < fadeCount) || (queued.Length < source.QueuedCount)) {
            throw new ArgumentException(message: "The shadow output spans cannot hold this presented result.");
        }
        var heldIndex = 0;
        var fadeIndex = 0;

        for (var slot = 0; (slot < MaxSlots); slot++) {
            if (source.Held[slot] is not { } held) { continue; }
            var fading = source.FadeFor(slot: slot);

            if (fading >= 0) {
                var fade = source.Fades[fading];
                var incoming = source.Light(index: fade.IncomingLight);
                var weight = weights[fading];

                if (weight >= 1) { held = held with { Candidate = incoming, Rank = Rank(candidate: incoming, source: source) }; } else { handoffs[fadeIndex++] = new WorldShadowHandoff(slot, held.Candidate, incoming, fade.CrossingTick, fade.DurationTicks, weight, fade.Flags); }
            }
            stable[heldIndex++] = held;
        }
        source.Queued.AsSpan(length: source.QueuedCount, start: 0).CopyTo(destination: queued);
        return new WorldShadowReadout(FadeCount: fadeCount, QueuedCount: source.QueuedCount, StableCount: heldCount);
    }

    private static int Rank(State source, in WorldShadowCandidate candidate) {
        if (candidate.LightIndex < 0) { return 0; }
        var rank = 0;

        foreach (var light in source.Lights) {
            if ((light is { Mode: not WorldShadowMode.Never } other) && (other.LightIndex >= 0) && (Same(left: candidate, right: other) || Before(left: other, right: candidate, source: source))) { rank++; }
        }
        return rank;
    }
    private static double Weight(in PresentedTick tick, in WorldShadowFade fade) {
        if (tick.Whole < fade.CrossingTick) { return 0; }
        var elapsed = (tick.Whole - fade.CrossingTick);

        if ((fade.DurationTicks == 0) || (elapsed >= fade.DurationTicks)) { return 1; }
        return Math.Clamp(((elapsed + tick.Fraction) / fade.DurationTicks), 0d, Math.BitDecrement(x: 1d));
    }
}
