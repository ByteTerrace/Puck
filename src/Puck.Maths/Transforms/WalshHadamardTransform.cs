using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.Maths;

/// <summary>
/// The exact Walsh–Hadamard transform over any binary integer: the in-place radix-2 network whose every butterfly is
/// one addition and one subtraction, with no twiddle table at all — which is why, alone in this wing, it takes no
/// plan.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Forward{T}"/> computes the unscaled Sylvester (natural) ordering,
/// <c>X[k] = sum over n of x[n] * (-1)^popcount(n AND k)</c>: bin zero is the plain sum, and the ordering is the one
/// the recursive doubling <c>H(2N) = [[H(N), H(N)], [H(N), -H(N)]]</c> produces, not the sequency ordering. Because
/// <c>H * H = N * I</c>, applying <see cref="Forward{T}"/> twice returns <c>N</c> times the input, and
/// <see cref="Inverse{T}"/> is exactly that: a second forward pass followed by an arithmetic shift right by
/// <c>log2(N)</c> — a floor division by <c>N</c>, which is exact on any spectrum <see cref="Forward{T}"/> produced
/// (every element of <c>H * H * x</c> is a multiple of <c>N</c>) and a floor on any other.
/// </para>
/// <para>
/// Arithmetic is unchecked, the posture <see cref="FixedQ4816"/>'s own <c>+</c> takes: the transform is exact
/// whenever <c>N * max|x|</c> fits the carrier, and wraps modulo the carrier's width otherwise — silently, so the
/// caller owns that envelope. A <see cref="FixedQ4816"/> sequence is transformed by passing its raw
/// <see cref="FixedQ4816.Value"/>s: the transform is linear over the integers, so the fixed-point grid rides along
/// unchanged. The <see cref="long"/> and <see cref="int"/> carriers run the wide stages through
/// <see cref="Vector{T}"/>; every carrier returns the same bits, because wrapping addition is the same operation in
/// every lane width.
/// </para>
/// </remarks>
public static class WalshHadamardTransform {
    private static void Butterfly<T>(Span<T> values) where T : unmanaged, IBinaryInteger<T> {
        if (typeof(T) == typeof(long)) {
            ButterflyVector(values: MemoryMarshal.Cast<T, long>(span: values));

            return;
        }

        if (typeof(T) == typeof(int)) {
            ButterflyVector(values: MemoryMarshal.Cast<T, int>(span: values));

            return;
        }

        ButterflyScalar(values: values);
    }
    private static void ButterflyScalar<T>(Span<T> values) where T : unmanaged, IBinaryInteger<T> {
        var n = values.Length;

        for (var half = 1; (half < n); half <<= 1) {
            var length = (half << 1);

            for (var i = 0; (i < n); i += length) {
                var low = values.Slice(
                    length: half,
                    start: i
                );
                var high = values.Slice(
                    length: half,
                    start: (i + half)
                );

                for (var j = 0; (j < low.Length); ++j) {
                    var u = low[j];
                    var v = high[j];

                    low[j] = (u + v);
                    high[j] = (u - v);
                }
            }
        }
    }
    // The same network with every stage whose half-length covers a whole vector run lane-parallel; the early narrow
    // stages stay scalar. Vector<T> addition wraps exactly like the scalar operator, so the two paths agree bit for bit.
    private static void ButterflyVector<T>(Span<T> values) where T : unmanaged, IBinaryInteger<T> {
        var n = values.Length;
        var lanes = Vector<T>.Count;
        var half = 1;

        for (; ((half < n) && (half < lanes)); half <<= 1) {
            var length = (half << 1);

            for (var i = 0; (i < n); i += length) {
                var low = values.Slice(
                    length: half,
                    start: i
                );
                var high = values.Slice(
                    length: half,
                    start: (i + half)
                );

                for (var j = 0; (j < low.Length); ++j) {
                    var u = low[j];
                    var v = high[j];

                    low[j] = (u + v);
                    high[j] = (u - v);
                }
            }
        }

        for (; (half < n); half <<= 1) {
            var length = (half << 1);

            for (var i = 0; (i < n); i += length) {
                var low = values.Slice(
                    length: half,
                    start: i
                );
                var high = values.Slice(
                    length: half,
                    start: (i + half)
                );

                for (var j = 0; (j < low.Length); j += lanes) {
                    var lowLane = low.Slice(
                        length: lanes,
                        start: j
                    );
                    var highLane = high.Slice(
                        length: lanes,
                        start: j
                    );
                    var u = new Vector<T>(values: lowLane);
                    var v = new Vector<T>(values: highLane);

                    (u + v).CopyTo(destination: lowLane);
                    (u - v).CopyTo(destination: highLane);
                }
            }
        }
    }

    /// <summary>Computes the forward transform in place: <c>X[k] = sum over n of x[n] * (-1)^popcount(n AND k)</c>,
    /// in Sylvester (natural) order.</summary>
    /// <typeparam name="T">The integer carrier.</typeparam>
    /// <param name="values">The sequence, transformed in place; its length must be a positive power of two.</param>
    /// <exception cref="ArgumentException">The length of <paramref name="values"/> is not a positive power of two.</exception>
    public static void Forward<T>(Span<T> values) where T : unmanaged, IBinaryInteger<T> {
        TransformKernels.RequirePowerOfTwoLength(
            parameterName: nameof(values),
            values: ((ReadOnlySpan<T>)values)
        );
        Butterfly(values: values);
    }
    /// <summary>Computes the inverse transform in place: a second forward pass, then an arithmetic shift right by
    /// <c>log2(N)</c> — exact on any spectrum <see cref="Forward{T}"/> produced, a floor division by <c>N</c> on any
    /// other.</summary>
    /// <typeparam name="T">The integer carrier.</typeparam>
    /// <param name="values">The transformed sequence, restored in place; its length must be a positive power of two.</param>
    /// <exception cref="ArgumentException">The length of <paramref name="values"/> is not a positive power of two.</exception>
    public static void Inverse<T>(Span<T> values) where T : unmanaged, IBinaryInteger<T> {
        TransformKernels.RequirePowerOfTwoLength(
            parameterName: nameof(values),
            values: ((ReadOnlySpan<T>)values)
        );
        Butterfly(values: values);

        var shift = BitOperations.Log2(value: ((uint)values.Length));

        if (0 == shift) { return; }

        for (var i = 0; (i < values.Length); ++i) {
            values[i] >>= shift;
        }
    }
}
