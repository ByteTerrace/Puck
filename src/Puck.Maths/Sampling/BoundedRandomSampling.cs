using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// Provides unbiased bounded integer sampling routines over 32-bit draw generators.
/// </summary>
internal static class BoundedRandomSampling {
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
