using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// Provides unbiased bounded integer sampling routines over 32-bit draw generators.
/// </summary>
internal static class BoundedRandomSampling {
    /// <summary>Lemire's nearly divisionless bounded draw at 64 bits, over a bound fixed for the request.</summary>
    /// <param name="exclusiveHigh">The exclusive bound, at least two.</param>
    /// <remarks>
    /// <para>
    /// Raw draws are counted, biased-window rejections included, so a degenerate generator cannot turn a finite
    /// prime-selection budget into an unbounded inner loop.
    /// </para>
    /// <para>
    /// The rejection threshold <c>2^64 mod exclusiveHigh</c> is the one hardware divide of a request, and it is
    /// derived on the first draw whose low product is below the bound: every rejected low product is below the
    /// threshold, which is below the bound, so a draw whose low product is at or past the bound is accepted without
    /// it, and a request whose draws all land there never pays for it. Until then the bound itself stands in for the
    /// threshold, so the accepting path is one compare against a field either way; a threshold is always below the
    /// bound, so the bound also marks it underived.
    /// </para>
    /// </remarks>
    internal struct Range64(ulong exclusiveHigh) {
        private readonly ulong m_exclusiveHigh = exclusiveHigh;
        private ulong m_threshold = exclusiveHigh;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TrySample<TGenerator>(ref TGenerator generator, ref int attempts, out ulong value)
            where TGenerator : struct, IDrawGenerator {
            while (attempts > 0) {
                --attempts;
                var word = (((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32();

                // The halves of word * exclusiveHigh, taken separately: a widened product hands its low half back
                // through memory. The bound is the left factor, the operand MULX reads implicitly, so the
                // generator's state never has to make way for it.
                var low = unchecked((word * m_exclusiveHigh));

                if (low < m_threshold) {
                    if (m_threshold != m_exclusiveHigh) { continue; }

                    m_threshold = (unchecked((0UL - m_exclusiveHigh)) % m_exclusiveHigh);

                    if (low < m_threshold) { continue; }
                }
                value = ScaledResidueRing64.MultiplyHigh(
                    left: m_exclusiveHigh,
                    right: word
                );
                return true;
            }
            value = 0;
            return false;
        }
    }

    /// <summary>
    /// Computes a nearly-divisionless bounded draw in <c>[0, exclusiveHigh)</c> using Lemire's algorithm,
    /// rejecting the small biased window (<c>threshold = 2^32 mod exclusiveHigh</c>) so every value is
    /// exactly equally likely.
    /// </summary>
    /// <typeparam name="TGenerator">The generator type implementing <see cref="IDrawGenerator"/>.</typeparam>
    /// <param name="generator">The generator instance, passed by reference.</param>
    /// <param name="exclusiveHigh">The upper bound (exclusive).</param>
    /// <returns>A uniformly distributed value in <c>[0, exclusiveHigh)</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Sample<TGenerator>(ref TGenerator generator, uint exclusiveHigh) where TGenerator : struct, IDrawGenerator {
        var product = unchecked((((ulong)generator.NextUInt32()) * exclusiveHigh));
        var lowBits = unchecked((uint)product);

        if (lowBits < exclusiveHigh) {
            var threshold = unchecked((((uint)(-((int)exclusiveHigh))) % exclusiveHigh));

            while (lowBits < threshold) {
                product = unchecked((((ulong)generator.NextUInt32()) * exclusiveHigh));
                lowBits = unchecked((uint)product);
            }
        }

        return ((uint)(product >> 32));
    }
    /// <summary>
    /// Draws a uniformly random value from an inclusive range.
    /// </summary>
    /// <typeparam name="TGenerator">The generator type implementing <see cref="IDrawGenerator"/>.</typeparam>
    /// <param name="generator">The generator instance, passed by reference.</param>
    /// <param name="minimum">One end of the inclusive range.</param>
    /// <param name="maximum">The other end of the inclusive range.</param>
    /// <returns>A uniformly distributed value in the inclusive range.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint SampleRange<TGenerator>(ref TGenerator generator, uint minimum, uint maximum) where TGenerator : struct, IDrawGenerator {
        if (maximum < minimum) {
            (minimum, maximum) = (maximum, minimum);
        }

        var range = (maximum - minimum);

        return ((range != uint.MaxValue)
            ? unchecked((Sample(
                exclusiveHigh: (range + 1U),
                generator: ref generator
            ) + minimum))
            : generator.NextUInt32()
        );
    }
}
