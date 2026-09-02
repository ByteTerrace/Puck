using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// The substrate every transform in this wing stands on: the in-place bit-reversal permutation that seeds a radix-2
/// decimation-in-time network, and the refusals a transform makes. One copy, so a transform never carries its own
/// bit-reversal loop or its own length message.
/// </summary>
internal static class TransformKernels {
    /// <summary>Permutes <paramref name="values"/> in place so that index <c>i</c> holds the element that was at the
    /// bit-reverse of <c>i</c> over <c>log2(length)</c> bits — the input order a radix-2 decimation-in-time butterfly
    /// network consumes. An involution: applying it twice restores the sequence.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="values">The sequence; its length must be a power of two (a length of zero or one is a no-op).</param>
    public static void BitReversePermute<T>(Span<T> values) {
        var n = values.Length;

        for (int i = 1, j = 0; (i < n); ++i) {
            var bit = (n >> 1);

            for (; (0 != (j & bit)); bit >>= 1) { j ^= bit; }

            j ^= bit;

            if (i < j) {
                (values[i], values[j]) = (values[j], values[i]);
            }
        }
    }
    /// <summary>Refuses a span whose length is not the plan's.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="expected">The plan's length.</param>
    /// <param name="values">The span offered.</param>
    /// <param name="parameterName">The caller's parameter name, reported on refusal.</param>
    /// <exception cref="ArgumentException"><paramref name="values"/>'s length does not equal <paramref name="expected"/>.</exception>
    public static void RequireLength<T>(int expected, ReadOnlySpan<T> values, string parameterName) {
        if (values.Length != expected) {
            throw new ArgumentException(
                message: $"expected length {expected} (the plan's length); got {values.Length}.",
                paramName: parameterName
            );
        }
    }
    /// <summary>Refuses three spans that do not share one length, naming the first that disagrees with
    /// <paramref name="left"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="left">The span whose length the others must match.</param>
    /// <param name="right">The second operand.</param>
    /// <param name="destination">The destination.</param>
    /// <exception cref="ArgumentException">The three spans do not share one length; <c>ParamName</c> is
    /// <c>right</c> when <paramref name="right"/> disagrees, otherwise <c>destination</c>.</exception>
    public static void RequireMatchingLengths<T>(ReadOnlySpan<T> left, ReadOnlySpan<T> right, ReadOnlySpan<T> destination) {
        if (right.Length != left.Length) {
            throw new ArgumentException(
                message: $"right must have left's length {left.Length}; got {right.Length}.",
                paramName: "right"
            );
        }

        if (destination.Length != left.Length) {
            throw new ArgumentException(
                message: $"destination must have left's length {left.Length}; got {destination.Length}.",
                paramName: "destination"
            );
        }
    }
    /// <summary>Refuses a transform length that is not a positive power of two.</summary>
    /// <param name="length">The length offered.</param>
    /// <param name="parameterName">The caller's parameter name, reported on refusal.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not a positive power of two.</exception>
    public static void RequirePowerOfTwo(int length, string parameterName) {
        if (
            (length <= 0) ||
            !BitOperations.IsPow2(value: ((uint)length))
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: parameterName,
                message: "length must be a positive power of two."
            );
        }
    }
    /// <summary>Refuses a plan-free transform's span whose length is not a positive power of two.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="values">The span offered.</param>
    /// <param name="parameterName">The caller's parameter name, reported on refusal.</param>
    /// <exception cref="ArgumentException"><paramref name="values"/>'s length is not a positive power of two.</exception>
    public static void RequirePowerOfTwoLength<T>(ReadOnlySpan<T> values, string parameterName) {
        if (
            (values.Length <= 0) ||
            !BitOperations.IsPow2(value: ((uint)values.Length))
        ) {
            throw new ArgumentException(
                message: $"length must be a positive power of two; got {values.Length}.",
                paramName: parameterName
            );
        }
    }
}
