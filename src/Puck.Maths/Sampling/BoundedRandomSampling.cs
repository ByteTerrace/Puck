using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// Provides unbiased bounded integer sampling routines over 32-bit draw generators.
/// </summary>
internal static class BoundedRandomSampling {
    // Lemire reduction at 64 bits. Count raw draws, including biased-window rejections, so a degenerate
    // caller generator cannot turn a finite prime-selection budget into an unbounded inner loop.
    internal readonly struct Range64(ulong exclusiveHigh) {
        private readonly ulong m_exclusiveHigh = exclusiveHigh;
        private readonly ulong m_threshold = (unchecked((0UL - exclusiveHigh)) % exclusiveHigh);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TrySample<TGenerator>(ref TGenerator generator, ref int attempts, out ulong value)
            where TGenerator : struct, IDrawGenerator {
            while (attempts > 0) {
                --attempts;
                var word = (((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32();

                // The halves of word * exclusiveHigh, taken separately: a widened product hands its low half back
                // through memory. The bound is the left factor, the operand MULX reads implicitly, so the
                // generator's state never has to make way for it.
                if (unchecked((word * m_exclusiveHigh)) < m_threshold) { continue; }
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
