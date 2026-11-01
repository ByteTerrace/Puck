using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// Provides unbiased bounded integer sampling routines over 32-bit draw generators.
/// </summary>
public static class BoundedRandomSampling {
    /// <summary>Maps one uniform raw word to an unbiased bounded result, or rejects the biased window.</summary>
    /// <param name="word">The raw word. A rejection still consumes this word.</param>
    /// <param name="exclusiveHigh">The nonzero exclusive upper bound.</param>
    /// <param name="value">The bounded result, valid only on success.</param>
    /// <returns>Whether the word is accepted. The caller owns retries, source failure and work accounting.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The bound is zero.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryMap(uint word, uint exclusiveHigh, out uint value) {
        ArgumentOutOfRangeException.ThrowIfZero(exclusiveHigh);
        var product = unchecked((((ulong)word) * exclusiveHigh));
        var low = unchecked((uint)product);

        value = ((uint)(product >> 32));
        return ((low >= exclusiveHigh) || (low >= (unchecked((0U - exclusiveHigh)) % exclusiveHigh)));
    }

    /// <summary>The refusal text when a bounded draw spends its whole attempt budget on rejected words.</summary>
    public const string ExhaustedReason = "bounded draw exhausted its raw-word work budget";

    /// <summary>
    /// Draws an unbiased value from an inclusive range within a finite raw-word budget, refusing rather than
    /// falling back to a biased answer.
    /// </summary>
    /// <typeparam name="TSource">The word source type.</typeparam>
    /// <param name="source">The word source, advanced by every word drawn, rejected words included.</param>
    /// <param name="minimum">One end of the inclusive range.</param>
    /// <param name="maximum">The other end of the inclusive range; the bounds may be given in either order.</param>
    /// <param name="maxAttempts">The most raw words one result may consume.</param>
    /// <param name="value">The result in the range, valid only on success.</param>
    /// <param name="word">The last raw word drawn, valid only on success.</param>
    /// <param name="words">The raw words consumed, accepted and rejected, whatever the outcome.</param>
    /// <param name="reason">The source's refusal, <see cref="ExhaustedReason"/> when every attempt was rejected, or empty on success.</param>
    /// <returns>Whether a value was drawn. A full-width range accepts its first word.</returns>
    public static bool TrySampleRange<TSource>(ref TSource source, uint minimum, uint maximum, int maxAttempts, out uint value, out uint word, out int words, out string reason) where TSource : IRawWordSource {
        if (maximum < minimum) {
            (minimum, maximum) = (maximum, minimum);
        }

        var range = (maximum - minimum);

        words = 0;
        value = 0;
        word = 0;
        reason = string.Empty;
        for (var attempt = 0; (attempt < maxAttempts); attempt++) {
            if (!source.TryNextWord(out word, out reason)) { return false; }
            ++words;
            if (range == uint.MaxValue) {
                value = word;
                return true;
            }
            if (TryMap(exclusiveHigh: (range + 1U), value: out value, word: word)) {
                value = unchecked((value + minimum));
                return true;
            }
        }
        word = 0;
        reason = ExhaustedReason;
        return false;
    }
    /// <summary>
    /// Draws an unbiased value below a bound that may exceed 32 bits, within a finite raw-word budget. A bound that
    /// fits one word runs <see cref="TrySampleRange{TSource}"/>; a wider bound draws enough words to cover its bit
    /// width, masks them to that width and accepts a candidate below the bound, so every value stays equally likely
    /// and each attempt is rejected with probability below one half.
    /// </summary>
    /// <typeparam name="TSource">The word source type.</typeparam>
    /// <param name="source">The word source, advanced by every word drawn, rejected words included.</param>
    /// <param name="exclusiveHigh">The nonzero exclusive upper bound.</param>
    /// <param name="maxAttempts">The most candidates one result may draw; a wide candidate spans several words.</param>
    /// <param name="value">The result in <c>[0, exclusiveHigh)</c>, valid only on success.</param>
    /// <param name="words">The raw words consumed, accepted and rejected, whatever the outcome.</param>
    /// <param name="reason">The source's refusal, <see cref="ExhaustedReason"/> when every attempt was rejected, or empty on success.</param>
    /// <returns>Whether a value was drawn.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="exclusiveHigh"/> is zero.</exception>
    public static bool TrySampleWide<TSource>(ref TSource source, UInt128 exclusiveHigh, int maxAttempts, out UInt128 value, out int words, out string reason) where TSource : IRawWordSource {
        ArgumentOutOfRangeException.ThrowIfZero(exclusiveHigh);
        if (exclusiveHigh <= (((UInt128)uint.MaxValue) + 1U)) {
            var drawn = TrySampleRange(
                maxAttempts: maxAttempts,
                maximum: unchecked((uint)(exclusiveHigh - 1U)),
                minimum: 0U,
                reason: out reason,
                source: ref source,
                value: out var narrow,
                word: out _,
                words: out words
            );

            value = narrow;
            return drawn;
        }

        var bits = (128 - ((int)UInt128.LeadingZeroCount(exclusiveHigh - 1U)));
        var wordsPerCandidate = ((bits + 31) / 32);
        var mask = (UInt128.MaxValue >>> (128 - bits));

        words = 0;
        value = UInt128.Zero;
        reason = string.Empty;
        for (var attempt = 0; (attempt < maxAttempts); attempt++) {
            var candidate = UInt128.Zero;

            for (var index = 0; (index < wordsPerCandidate); index++) {
                if (!source.TryNextWord(out var word, out reason)) { return false; }
                ++words;
                candidate |= (((UInt128)word) << (32 * index));
            }
            candidate &= mask;
            if (candidate < exclusiveHigh) {
                value = candidate;
                return true;
            }
        }
        reason = ExhaustedReason;
        return false;
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
    internal static uint Sample<TGenerator>(ref TGenerator generator, uint exclusiveHigh) where TGenerator : struct, IDrawGenerator {
        while (true) {
            if (TryMap(generator.NextUInt32(), exclusiveHigh, out var value)) { return value; }
        }
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
    internal static uint SampleRange<TGenerator>(ref TGenerator generator, uint minimum, uint maximum) where TGenerator : struct, IDrawGenerator {
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
