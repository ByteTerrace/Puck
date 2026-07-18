namespace Puck.Demo.Garden;

/// <summary>
/// A deterministic, integer-only pseudo-random stream (the SplitMix64 algorithm): every advance is pure 64-bit
/// integer arithmetic (add, xor, shift, multiply — no hardware floating point, no <see cref="System.Random"/>, no
/// wall-clock), so the same seed always produces the same draw sequence on every machine. This is the garden's OWN
/// PRNG advance — <see cref="Overworld.OverworldWorld"/> reserves a seed slot (<c>m_rng</c>) for a future xorshift
/// advance but does not yet drive one; the garden needed a real stream today, so it grows its own here rather than
/// wait on that seam. HOIST-READY: nothing below reaches into <c>Puck.Demo</c> types — this is exactly the kind of
/// small, contract-heavy, seed-in/values-out primitive <c>Puck.Maths</c> collects; it stays demo-side for now only
/// because the garden is its first and only consumer.
/// </summary>
internal struct GardenRng {
    private ulong m_state;

    /// <summary>Initializes the stream from a 32-bit seed.</summary>
    /// <param name="seed">The garden's planted seed.</param>
    internal GardenRng(uint seed) {
        // Fold the 32-bit seed into a full 64-bit state with one golden-ratio-constant mix so seed 0 (and small
        // seeds generally) still produce a well-distributed first draw — SplitMix64's own recommended seeding.
        m_state = seed ^ 0x9E3779B97F4A7C15UL;
    }

    /// <summary>Advances the stream and returns the next 64-bit value.</summary>
    /// <returns>The next pseudo-random value in the deterministic sequence.</returns>
    internal ulong NextUInt64() {
        m_state = unchecked((m_state + 0x9E3779B97F4A7C15UL));

        var z = m_state;

        z = unchecked(((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL));
        z = unchecked(((z ^ (z >> 27)) * 0x94D049BB133111EBUL));

        return z ^ (z >> 31);
    }

    /// <summary>Advances the stream and returns the next 32-bit value (the high bits of one 64-bit draw — the better-
    /// mixed half).</summary>
    /// <returns>The next pseudo-random 32-bit value.</returns>
    internal uint NextUInt32() =>
        (uint)(NextUInt64() >> 32);

    /// <summary>Draws a deterministic integer in <c>[minInclusive, maxExclusive)</c>.</summary>
    /// <param name="minInclusive">The inclusive lower bound.</param>
    /// <param name="maxExclusive">The exclusive upper bound; must be greater than <paramref name="minInclusive"/>.</param>
    /// <returns>A value in the requested range.</returns>
    internal int NextRange(int minInclusive, int maxExclusive) {
        var span = (uint)(maxExclusive - minInclusive);

        return (minInclusive + (int)(NextUInt32() % span));
    }
}
