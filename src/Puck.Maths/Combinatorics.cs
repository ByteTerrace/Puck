using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>Exact counts and dense zero-based ranks for combinations and permutations, carried in <see cref="ulong"/>.</summary>
/// <remarks>Combination elements are strictly increasing ordinals in [0, n); permutations contain every ordinal in [0, length).
/// Successful operations allocate no managed memory. Ordering and carrier limits are part of the encoding contract.</remarks>
public static class Combinatorics {
    /// <summary>Returns n choose k; returns zero when k exceeds n, and one when k is zero.</summary>
    /// <param name="n">Nonnegative universe size.</param>
    /// <param name="k">Nonnegative subset size.</param>
    /// <returns>The exact number of k-element subsets.</returns>
    /// <exception cref="ArgumentOutOfRangeException">n or k is negative.</exception>
    /// <exception cref="OverflowException">The result exceeds <see cref="ulong.MaxValue"/>.</exception>
    public static ulong Binomial(int n, int k) {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        ArgumentOutOfRangeException.ThrowIfNegative(k);
        return Choose(n, k);
    }

    /// <summary>Returns n!, including 0! = 1.</summary>
    /// <param name="n">Nonnegative permutation size.</param>
    /// <returns>The exact number of permutations.</returns>
    /// <exception cref="ArgumentOutOfRangeException">n is negative.</exception>
    /// <exception cref="OverflowException">n exceeds 20, so n! does not fit in a <see cref="ulong"/>.</exception>
    public static ulong Factorial(int n) {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        if (n > 20) { throw new OverflowException("Factorials above 20! exceed UInt64."); }
        return Factorials[n];
    }

    /// <summary>Ranks a sorted subset in colexicographic order: compare the largest differing element first.</summary>
    /// <param name="n">Nonnegative universe size.</param>
    /// <param name="combination">Strictly increasing ordinals in [0, n). The empty subset has rank zero.</param>
    /// <returns>Sum of Binomial(combination[i], i + 1), in [0, Binomial(n, length)).</returns>
    /// <exception cref="ArgumentOutOfRangeException">n is negative or the subset length exceeds n.</exception>
    /// <exception cref="ArgumentException">Elements are out of range, repeated, or not increasing.</exception>
    /// <exception cref="OverflowException">The complete subset space exceeds <see cref="ulong.MaxValue"/>.</exception>
    public static ulong CombinationRank(int n, ReadOnlySpan<int> combination) {
        ValidateCombinationSpace(n, combination.Length, nameof(combination));
        var previous = -1;
        var rank = 0UL;
        for (var i = 0; i < combination.Length; ++i) {
            var element = combination[i];
            if (element <= previous || element >= n) { throw new ArgumentException("Elements must be strictly increasing ordinals in [0, n).", nameof(combination)); }
            rank += Choose(element, i + 1);
            previous = element;
        }
        return rank;
    }

    /// <summary>Decodes a colexicographic rank into an increasing subset; the destination length selects the subset size.</summary>
    /// <param name="n">Nonnegative universe size.</param>
    /// <param name="rank">Zero-based rank below Binomial(n, destination.Length).</param>
    /// <param name="destination">Receives every subset element. Unchanged on failure.</param>
    /// <exception cref="ArgumentOutOfRangeException">n is negative, the destination length exceeds n, or rank is outside the space.</exception>
    /// <exception cref="OverflowException">The complete subset space exceeds <see cref="ulong.MaxValue"/>.</exception>
    public static void CombinationUnrank(int n, ulong rank, Span<int> destination) {
        var count = ValidateCombinationSpace(n, destination.Length, nameof(destination));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(rank, count);
        if (n <= 128 && !destination.IsEmpty) {
            DecodeSmallCombination(n, destination.Length, rank, count, 0, destination);
            return;
        }
        var upper = n - 1;
        for (var k = destination.Length; k > 0; --k) {
            var element = FindElement(upper, k, rank);
            destination[k - 1] = element;
            rank -= Choose(element, k);
            upper = element - 1;
        }
    }

    /// <summary>Decodes one element of a colexicographic rank, without constructing the entire subset.</summary>
    /// <param name="n">Nonnegative universe size.</param>
    /// <param name="k">Subset size in [0, n].</param>
    /// <param name="rank">Zero-based rank below Binomial(n, k).</param>
    /// <param name="index">Zero-based position in the increasing subset, less than k.</param>
    /// <returns>The ordinal at the requested position.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size, rank, or index is outside its domain.</exception>
    /// <exception cref="OverflowException">The complete subset space exceeds <see cref="ulong.MaxValue"/>.</exception>
    public static int CombinationElement(int n, int k, ulong rank, int index) {
        var count = ValidateCombinationSpace(n, k, nameof(k));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(rank, count);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, k);
        if (n <= 128) { return DecodeSmallCombination(n, k, rank, count, index, Span<int>.Empty); }
        var upper = n - 1;
        for (; k > index; --k) {
            var element = FindElement(upper, k, rank);
            if (k == index + 1) { return element; }
            rank -= Choose(element, k);
            upper = element - 1;
        }
        throw new InvalidOperationException("Unreachable subset position.");
    }

    /// <summary>Ranks a permutation lexicographically using its Lehmer code in the factorial number system.</summary>
    /// <param name="permutation">Each ordinal in [0, length) exactly once, with length at most 20. Empty has rank zero.</param>
    /// <returns>A rank in [0, length!).</returns>
    /// <exception cref="ArgumentOutOfRangeException">The length exceeds 20.</exception>
    /// <exception cref="ArgumentException">An ordinal is out of range or repeated.</exception>
    public static ulong PermutationRank(ReadOnlySpan<int> permutation) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permutation.Length, 20, nameof(permutation));
        var available = (1U << permutation.Length) - 1;
        var rank = 0UL;
        for (var i = 0; i < permutation.Length; ++i) {
            var element = permutation[i];
            if ((uint)element >= (uint)permutation.Length || (available & (1U << element)) == 0) {
                throw new ArgumentException("Elements must be a permutation of [0, length).", nameof(permutation));
            }
            var bit = 1U << element;
            rank = (rank * (uint)(permutation.Length - i)) + (uint)BitOperations.PopCount(available & (bit - 1));
            available ^= bit;
        }
        return rank;
    }

    /// <summary>Decodes a lexicographic permutation rank; the destination length selects the permutation size.</summary>
    /// <param name="rank">Zero-based rank below destination.Length!.</param>
    /// <param name="destination">Receives ordinals in [0, length), with length at most 20. Unchanged on failure.</param>
    /// <exception cref="ArgumentOutOfRangeException">The length exceeds 20 or rank is outside the space.</exception>
    public static void PermutationUnrank(ulong rank, Span<int> destination) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(destination.Length, 20, nameof(destination));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(rank, Factorials[destination.Length]);
        var available = (1U << destination.Length) - 1;
        for (var i = 0; i < destination.Length; ++i) {
            var weight = Factorials[destination.Length - i - 1];
            var (quotient, remainder) = ulong.DivRem(rank, weight);
            var digit = (int)quotient;
            rank = remainder;
            var candidates = available;
            for (var j = 0; j < digit; ++j) { candidates &= candidates - 1; }
            var element = BitOperations.TrailingZeroCount(candidates);
            destination[i] = element;
            available ^= 1U << element;
        }
    }

    private static ulong ValidateCombinationSpace(int n, int k, string parameter) {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        ArgumentOutOfRangeException.ThrowIfNegative(k, parameter);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(k, n, parameter);
        return Choose(n, k);
    }

    private static int FindElement(int upper, int k, ulong rank) {
        var lower = k - 1;
        while (lower < upper) {
            var middle = lower + (int)(((long)upper - lower + 1) / 2);
            if (Choose(middle, k) <= rank) { lower = middle; } else { upper = middle - 1; }
        }
        return lower;
    }

    private static int DecodeSmallCombination(int n, int k, ulong rank, ulong count, int stop, Span<int> destination) {
        var upper = n - 1;
        var coefficient = MultiplyDivide(count, (uint)(n - k), (uint)n);
        for (; ; --k) {
            // C(upper - 1, k) = C(upper, k) * (upper - k) / upper.
            while (coefficient > rank) {
                coefficient = MultiplyDivide(coefficient, (uint)(upper - k), (uint)upper);
                --upper;
            }
            if (!destination.IsEmpty) { destination[k - 1] = upper; }
            if (k - 1 == stop) { return upper; }
            rank -= coefficient;
            // After selecting upper, descend to C(upper - 1, k - 1).
            coefficient = MultiplyDivide(coefficient, (uint)k, (uint)upper);
            --upper;
        }
    }

    private static ulong Choose(int n, int k) {
        if (k > n) { return 0; }
        k = Math.Min(k, n - k);
        if (k == 0) { return 1; }
        if (k == 1) { return (uint)n; }
        // This product fits in UInt64 for every nonnegative Int32 n.
        var result = ((ulong)n * (uint)(n - 1)) / 2;
        for (var i = 3; i <= k; ++i) {
            var factor = (uint)(n - i + 1);
            // Keep ordinary hand-sized combinations on hardware UInt64 division.
            result = MultiplyDivide(result, factor, (uint)i);
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong MultiplyDivide(ulong value, uint multiplier, uint divisor) {
        var high = Math.BigMul(value, multiplier, out var low);
        return high == 0 ? low / divisor : checked((ulong)((((UInt128)high << 64) | low) / divisor));
    }

    // 0! through 20!: a span literal is stored in the assembly, not a managed array.
    private static ReadOnlySpan<ulong> Factorials => [1, 1, 2, 6, 24, 120, 720, 5040, 40320, 362880,
        3628800, 39916800, 479001600, 6227020800, 87178291200, 1307674368000,
        20922789888000, 355687428096000, 6402373705728000, 121645100408832000, 2432902008176640000];
}
