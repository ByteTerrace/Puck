namespace Puck.Maths;

/// <summary>The SplitMix64 generator: a 64-bit counter advanced by the golden-ratio increment and mixed through
/// <see cref="InvertibleBitMix.Mix64"/>, its output finalizer. Pure integer arithmetic, so a seed gives the same words
/// on every machine; its whole state is one word. It serves a fixed table or a seed expansion (a few words drawn from
/// one seed) rather than a simulation's draws, which belong to <see cref="Pcg32XshRr"/>.</summary>
public struct SplitMix64 {
    /// <summary>The increment added to the state before each output: 2^64 divided by the golden ratio, rounded to odd.</summary>
    public const ulong GoldenGamma = 0x9E3779B97F4A7C15UL;

    private ulong m_state;

    /// <summary>Initializes a new instance of the <see cref="SplitMix64"/> struct.</summary>
    /// <param name="seed">The initial state; every 64-bit value is a valid seed.</param>
    public SplitMix64(ulong seed) {
        m_state = seed;
    }

    /// <summary>Gets the raw state, which restores the generator when passed to the constructor.</summary>
    public readonly ulong State => m_state;

    /// <summary>Draws the next 64-bit word.</summary>
    /// <returns>The mixed word after advancing the state by <see cref="GoldenGamma"/>.</returns>
    public ulong NextUInt64() {
        m_state = unchecked((m_state + GoldenGamma));

        return InvertibleBitMix.Mix64(value: m_state);
    }
}
