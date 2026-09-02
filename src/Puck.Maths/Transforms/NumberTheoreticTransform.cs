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
/// <para>
/// <b>Scaling convention.</b> <see cref="Forward"/> is the unscaled sum <c>X[k] = sum over n of x[n] * root^(n*k)</c>;
/// <see cref="Inverse"/> runs the conjugate network and multiplies every element by the field inverse of <c>N</c>,
/// which exists because <c>N</c> is a power of two below the modulus. Both are in place, and neither allocates —
/// the plan holds every table.
/// </para>
/// <para>
/// The butterfly network runs in the Montgomery representation of <see cref="ScaledResidueRing64"/>: a transform
/// encodes its operand once on entry, pays one REDC per butterfly product instead of a hardware division, and decodes
/// once on exit. <see cref="Convolve"/> never leaves that representation between its three transforms.
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

    internal static readonly ScaledResidueRing64 Ring = new(modulus: Modulus);

    // In-place radix-2 decimation-in-time over Montgomery-form values: the shared bit-reversal permutation followed by
    // log2(N) butterfly stages. Each stage walks disjoint low/high halves so the JIT drops the bounds checks, and
    // strides through the SAME twiddle table (size N/2, holding root^0 .. root^(N/2 - 1)) with a running cursor.
    private static void Butterfly(ReadOnlySpan<ulong> twiddles, Span<ulong> values) {
        var n = values.Length;

        if (n <= 1) { return; }

        TransformKernels.BitReversePermute(values: values);

        var ring = Ring;

        for (var length = 2; (length <= n); length <<= 1) {
            var half = (length >> 1);
            var step = (n / length);

            for (var i = 0; (i < n); i += length) {
                var low = values.Slice(
                    length: half,
                    start: i
                );
                var high = values.Slice(
                    length: half,
                    start: (i + half)
                );
                var twiddleIndex = 0;

                for (var j = 0; (j < low.Length); ++j) {
                    var u = low[j];
                    var v = ring.Multiply(
                        left: high[j],
                        right: twiddles[twiddleIndex]
                    );

                    low[j] = ring.Add(
                        left: u,
                        right: v
                    );
                    high[j] = ring.Subtract(
                        left: u,
                        right: v
                    );
                    twiddleIndex += step;
                }
            }
        }
    }
    private static void Encode(Span<ulong> values) {
        var ring = Ring;

        for (var i = 0; (i < values.Length); ++i) {
            values[i] = ring.Encode(value: values[i]);
        }
    }
    // One REDC per element strips the radix and applies scale at once: scale is an ordinary residue, so the product
    // of a Montgomery-form element with it decodes to (element * scale).
    private static void DecodeScaled(Span<ulong> values, ulong scale) {
        var ring = Ring;

        for (var i = 0; (i < values.Length); ++i) {
            values[i] = ring.Multiply(
                left: values[i],
                right: scale
            );
        }
    }

    /// <summary>Computes the exact cyclic convolution of two length-<c>N</c> sequences.</summary>
    /// <param name="plan">The plan for length <c>N</c>; <paramref name="left"/>, <paramref name="right"/> and <paramref name="destination"/> must all have length <c>N</c>.</param>
    /// <param name="left">The first sequence; overwritten with its forward transform.</param>
    /// <param name="right">The second sequence; overwritten with its forward transform.</param>
    /// <param name="destination">Receives the convolution; may not alias <paramref name="left"/> or <paramref name="right"/>.</param>
    /// <remarks>
    /// Forward both operands, multiply pointwise, and invert — the convolution theorem, exact because every step is
    /// exact field arithmetic. <c>destination[k] = sum over i of left[i] * right[(k - i) mod N]</c>, reduced modulo
    /// <see cref="Modulus"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">A span's length does not equal <paramref name="plan"/>'s length.</exception>
    public static void Convolve(NumberTheoreticTransformPlan plan, Span<ulong> left, Span<ulong> right, Span<ulong> destination) {
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(left),
            values: ((ReadOnlySpan<ulong>)left)
        );
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(right),
            values: ((ReadOnlySpan<ulong>)right)
        );
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(destination),
            values: ((ReadOnlySpan<ulong>)destination)
        );

        var ring = Ring;

        Encode(values: left);
        Encode(values: right);
        Butterfly(
            twiddles: plan.ForwardTwiddles,
            values: left
        );
        Butterfly(
            twiddles: plan.ForwardTwiddles,
            values: right
        );

        for (var i = 0; (i < destination.Length); ++i) {
            destination[i] = ring.Multiply(
                left: left[i],
                right: right[i]
            );
        }

        Butterfly(
            twiddles: plan.InverseTwiddles,
            values: destination
        );
        DecodeScaled(
            scale: plan.LengthInverse,
            values: destination
        );
        DecodeScaled(
            scale: 1UL,
            values: left
        );
        DecodeScaled(
            scale: 1UL,
            values: right
        );
    }
    /// <summary>Computes the forward transform in place: <c>X[k] = sum over n of x[n] * root^(n*k)</c>.</summary>
    /// <param name="plan">The plan for the length of <paramref name="values"/>.</param>
    /// <param name="values">The sequence, transformed in place.</param>
    /// <exception cref="ArgumentException">The length of <paramref name="values"/> does not equal the length of <paramref name="plan"/>.</exception>
    public static void Forward(NumberTheoreticTransformPlan plan, Span<ulong> values) {
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(values),
            values: ((ReadOnlySpan<ulong>)values)
        );
        Encode(values: values);
        Butterfly(
            twiddles: plan.ForwardTwiddles,
            values: values
        );
        DecodeScaled(
            scale: 1UL,
            values: values
        );
    }
    /// <summary>Computes the inverse transform in place, undoing <see cref="Forward"/> exactly.</summary>
    /// <param name="plan">The plan for the length of <paramref name="values"/>.</param>
    /// <param name="values">The transformed sequence, restored in place.</param>
    /// <exception cref="ArgumentException">The length of <paramref name="values"/> does not equal the length of <paramref name="plan"/>.</exception>
    public static void Inverse(NumberTheoreticTransformPlan plan, Span<ulong> values) {
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(values),
            values: ((ReadOnlySpan<ulong>)values)
        );
        Encode(values: values);
        Butterfly(
            twiddles: plan.InverseTwiddles,
            values: values
        );
        DecodeScaled(
            scale: plan.LengthInverse,
            values: values
        );
    }
    /// <summary>Multiplies two sequences elementwise in the field.</summary>
    /// <param name="left">The first sequence.</param>
    /// <param name="right">The second sequence, the same length as <paramref name="left"/>.</param>
    /// <param name="destination">Receives the elementwise product, the same length as the operands; may alias either.</param>
    /// <exception cref="ArgumentException">The three spans do not share one length; <c>ParamName</c> names the first that disagrees with <paramref name="left"/>.</exception>
    public static void PointwiseMultiply(ReadOnlySpan<ulong> left, ReadOnlySpan<ulong> right, Span<ulong> destination) {
        TransformKernels.RequireMatchingLengths(
            destination: ((ReadOnlySpan<ulong>)destination),
            left: left,
            right: right
        );

        var field = Field;

        for (var i = 0; (i < left.Length); ++i) {
            destination[i] = field.Multiply(
                left: left[i],
                right: right[i]
            );
        }
    }
}
