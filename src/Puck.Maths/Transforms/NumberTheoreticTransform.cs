using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// The exact number-theoretic transform over <see cref="Field"/>: in-place radix-2 forward and inverse transforms,
/// a pointwise product, and the exact cyclic convolution built from them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Modulus"/> is prime and <c>Modulus - 1 = PrimeFactor * 2^MaximumLog2Length</c> with
/// <c>PrimeFactor = 262111</c>, so every power-of-two length up to <c>2^MaximumLog2Length</c> divides the
/// multiplicative group's order and has a primitive <c>N</c>-th root of unity. <see cref="PrimitiveRoot"/> generates
/// the whole group; <c>PrimitiveRoot^((Modulus - 1) / N)</c> is that root for length <c>N</c>.
/// </para>
/// <para>
/// Every element is a reduced <see cref="ulong"/> in <c>[0, Modulus)</c>; the precondition is not enforced, the same
/// posture <see cref="PrimeField64"/> takes toward its own operands. Arithmetic is exact — no rounding, no
/// saturation — so a produced value is the unique correct answer modulo <see cref="Modulus"/>, and the convolution
/// theorem holds bit-for-bit rather than within a bound.
/// </para>
/// </remarks>
public static class NumberTheoreticTransform {
    /// <summary>The maximum power-of-two transform length as a power of two; <c>Modulus - 1</c>'s two-adic valuation.</summary>
    public const int MaximumLog2Length = 44;
    /// <summary>The NTT-friendly prime modulus, <c>262111 * 2^44 + 1</c>.</summary>
    public const ulong Modulus = 4611105476287922177UL;
    /// <summary>A generator of the whole multiplicative group <c>(Z/Modulus)*</c>, of order <c>Modulus - 1</c>.</summary>
    public const ulong PrimitiveRoot = 3UL;

    /// <summary>Gets the prime field the transform runs over.</summary>
    public static readonly PrimeField64 Field = PrimeField64.Create(modulus: Modulus);

    // In-place radix-2 decimation-in-time: a bit-reversal permutation followed by log2(N) butterfly stages, striding
    // through the SAME twiddle table (size N/2, holding root^0 .. root^(N/2 - 1)) at every stage rather than building
    // one per stage.
    private static void Butterfly(ReadOnlySpan<ulong> twiddles, Span<ulong> values) {
        var n = values.Length;

        if (n <= 1) { return; }

        for (int i = 1, j = 0; (i < n); ++i) {
            var bit = (n >> 1);

            for (; (0 != (j & bit)); bit >>= 1) { j ^= bit; }

            j ^= bit;

            if (i < j) {
                (values[i], values[j]) = (values[j], values[i]);
            }
        }

        var field = Field;

        for (var length = 2; (length <= n); length <<= 1) {
            var half = (length >> 1);
            var step = (n / length);

            for (var i = 0; (i < n); i += length) {
                for (var j = 0; (j < half); ++j) {
                    var w = twiddles[(j * step)];
                    var u = values[(i + j)];
                    var v = field.Multiply(
                        left: values[((i + j) + half)],
                        right: w
                    );

                    values[(i + j)] = field.Add(
                        left: u,
                        right: v
                    );
                    values[((i + j) + half)] = field.Subtract(
                        left: u,
                        right: v
                    );
                }
            }
        }
    }
    private static void RequireLength(NttPlan plan, ReadOnlySpan<ulong> values, string parameterName) {
        if (values.Length != plan.Length) {
            throw new ArgumentException(
                message: $"expected length {plan.Length} (the plan's length); got {values.Length}.",
                paramName: parameterName
            );
        }
    }

    /// <summary>Computes the exact cyclic convolution of two length-<c>N</c> sequences.</summary>
    /// <param name="plan">The plan for length <c>N</c>; <c>left</c>, <c>right</c> and <paramref name="destination"/> must all have length <c>N</c>.</param>
    /// <param name="left">The first sequence; OVERWRITTEN with its forward transform.</param>
    /// <param name="right">The second sequence; OVERWRITTEN with its forward transform.</param>
    /// <param name="destination">Receives the convolution; may not alias <paramref name="left"/> or <paramref name="right"/>.</param>
    /// <remarks>
    /// Forward both operands, multiply pointwise, and invert — the convolution theorem, exact because every step is
    /// exact field arithmetic. <c>destination[k] = sum over i of left[i] * right[(k - i) mod N]</c>, reduced modulo
    /// <see cref="Modulus"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">A span's length does not equal <paramref name="plan"/>'s length.</exception>
    public static void Convolve(NttPlan plan, Span<ulong> left, Span<ulong> right, Span<ulong> destination) {
        RequireLength(
            plan: plan,
            values: left,
            parameterName: nameof(left)
        );
        RequireLength(
            plan: plan,
            values: right,
            parameterName: nameof(right)
        );
        RequireLength(
            plan: plan,
            values: destination,
            parameterName: nameof(destination)
        );

        Forward(
            plan: plan,
            values: left
        );
        Forward(
            plan: plan,
            values: right
        );
        PointwiseMultiply(
            destination: destination,
            left: left,
            right: right
        );
        Inverse(
            plan: plan,
            values: destination
        );
    }
    /// <summary>Computes the forward transform in place: <c>X[k] = sum over n of x[n] * root^(n*k)</c>.</summary>
    /// <param name="plan">The plan for <paramref name="values"/>' length.</param>
    /// <param name="values">The sequence, transformed in place.</param>
    /// <exception cref="ArgumentException"><paramref name="values"/>'s length does not equal <paramref name="plan"/>'s length.</exception>
    public static void Forward(NttPlan plan, Span<ulong> values) {
        RequireLength(
            plan: plan,
            values: values,
            parameterName: nameof(values)
        );
        Butterfly(
            twiddles: plan.ForwardTwiddles,
            values: values
        );
    }
    /// <summary>Computes the inverse transform in place, undoing <see cref="Forward"/> exactly.</summary>
    /// <param name="plan">The plan for <paramref name="values"/>' length.</param>
    /// <param name="values">The transformed sequence, restored in place.</param>
    /// <exception cref="ArgumentException"><paramref name="values"/>'s length does not equal <paramref name="plan"/>'s length.</exception>
    public static void Inverse(NttPlan plan, Span<ulong> values) {
        RequireLength(
            plan: plan,
            values: values,
            parameterName: nameof(values)
        );
        Butterfly(
            twiddles: plan.InverseTwiddles,
            values: values
        );

        var field = Field;
        var lengthInverse = plan.LengthInverse;

        for (var i = 0; (i < values.Length); ++i) {
            values[i] = field.Multiply(
                left: values[i],
                right: lengthInverse
            );
        }
    }
    /// <summary>Multiplies two sequences elementwise in the field.</summary>
    /// <param name="left">The first sequence.</param>
    /// <param name="right">The second sequence, the same length as <paramref name="left"/>.</param>
    /// <param name="destination">Receives the elementwise product, the same length as the operands.</param>
    /// <exception cref="ArgumentException">The three spans do not share one length.</exception>
    public static void PointwiseMultiply(ReadOnlySpan<ulong> left, ReadOnlySpan<ulong> right, Span<ulong> destination) {
        if (
            (left.Length != right.Length) ||
            (left.Length != destination.Length)
        ) {
            throw new ArgumentException(
                message: "left, right and destination must share one length.",
                paramName: nameof(right)
            );
        }

        var field = Field;

        for (var i = 0; (i < left.Length); ++i) {
            destination[i] = field.Multiply(
                left: left[i],
                right: right[i]
            );
        }
    }
}
/// <summary>
/// A cached root-of-unity table for one power-of-two transform length, built once and reused across every
/// <see cref="NumberTheoreticTransform.Forward"/>, <see cref="NumberTheoreticTransform.Inverse"/> and
/// <see cref="NumberTheoreticTransform.Convolve"/> call at that length.
/// </summary>
public sealed class NttPlan {
    private readonly ulong[] m_forwardTwiddles;
    private readonly ulong[] m_inverseTwiddles;
    private readonly ulong m_lengthInverse;

    private NttPlan(int length, ulong[] forwardTwiddles, ulong[] inverseTwiddles, ulong lengthInverse) {
        Length = length;
        m_forwardTwiddles = forwardTwiddles;
        m_inverseTwiddles = inverseTwiddles;
        m_lengthInverse = lengthInverse;
    }

    /// <summary>Builds the twiddle table for a transform length.</summary>
    /// <param name="length">The transform length; must be a positive power of two. The largest power of two an
    /// <see cref="int"/> can name is <c>2^30</c>, far below <c>2^MaximumLog2Length</c>, so every representable
    /// length is legal and the prime's own two-adicity ceiling is never the refusal a caller hits.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not a positive power of two.</exception>
    public static NttPlan Create(int length) {
        if (
            (length <= 0) ||
            !BitOperations.IsPow2(value: ((uint)length))
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(length),
                message: "length must be a positive power of two."
            );
        }

        var field = NumberTheoreticTransform.Field;
        var half = (length >> 1);
        var forward = new ulong[half];
        var inverse = new ulong[half];

        if (half > 0) {
            var root = field.Pow(
                exponent: ((NumberTheoreticTransform.Modulus - 1UL) / ((ulong)length)),
                value: NumberTheoreticTransform.PrimitiveRoot
            );
            var inverseRoot = field.Inverse(value: root);
            var forwardPower = field.One;
            var inversePower = field.One;

            for (var k = 0; (k < half); ++k) {
                forward[k] = forwardPower;
                inverse[k] = inversePower;
                forwardPower = field.Multiply(
                    left: forwardPower,
                    right: root
                );
                inversePower = field.Multiply(
                    left: inversePower,
                    right: inverseRoot
                );
            }
        }

        return new(
            length: length,
            forwardTwiddles: forward,
            inverseTwiddles: inverse,
            lengthInverse: field.Inverse(value: field.Reduce(value: ((ulong)length)))
        );
    }

    internal ReadOnlySpan<ulong> ForwardTwiddles => m_forwardTwiddles;
    internal ReadOnlySpan<ulong> InverseTwiddles => m_inverseTwiddles;
    internal ulong LengthInverse => m_lengthInverse;

    /// <summary>Gets the transform length this plan was built for.</summary>
    public int Length { get; }
}
