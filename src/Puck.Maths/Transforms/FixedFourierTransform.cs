namespace Puck.Maths;

/// <summary>
/// The fixed-point fast Fourier transform over <see cref="FixedComplex"/>: in-place radix-2 forward and inverse
/// transforms, real-valued convenience wrappers over the same engine, a pointwise product, and the cyclic
/// convolution built from them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scaling convention.</b> <see cref="Forward"/> is unscaled — <c>X[k] = sum over n of x[n] * exp(-i*2*pi*k*n/N)</c>,
/// the textbook sum, so an impulse, a DC-only input and a Nyquist-alternating input all produce exact bin values (the
/// twiddle at those bins is exactly <c>±1</c> or <c>±i</c>, so the one rounding a general product carries never
/// happens). <see cref="Inverse"/> instead halves every component at each of the <c>log2(N)</c> butterfly stages, so
/// the accumulated <c>1/N</c> normalization is reached by exact bit shifts of a representable quantity rather than by
/// one late multiply by <c>1/N</c> — which underflows to zero once <c>N &gt; 2^16</c>, past <see cref="FixedQ4816"/>'s
/// sixteen fraction bits. The stage halving bounds ideal inverse growth, but each butterfly still forms a complex
/// sum or difference before narrowing to <see cref="FixedQ4816"/>; a full-scale arbitrary spectrum can therefore
/// overflow a component. Arithmetic is unchecked in both directions. Callers must pre-scale any input whose
/// intermediate values can leave <see cref="FixedQ4816"/>'s raw range; <see cref="Forward"/> can grow by up to
/// <c>N</c> and an arbitrary inverse spectrum can grow during component mixing despite the per-stage halving.
/// </para>
/// <para>
/// Every butterfly rounds each returned component once. The forward butterfly's twiddle multiply is
/// <see cref="FixedComplex"/>'s own operator — the two leaf products accumulate exactly and round once
/// (<see cref="FixedQ4816.RoundProductSum(Int128)"/>) — and its sum and difference are exact. The inverse butterfly
/// fuses the multiply, the sum or difference, and the stage's halving into one rounding: the twiddle product is kept
/// at its exact Q32 width, the other operand is lifted to the same width, and the combined value rounds once at a
/// seventeen-bit shift, so the halving costs no rounding of its own.
/// </para>
/// <para>
/// <c>Inverse(Forward(x))</c> recovers <c>x</c> within a bound measured and pinned by the <c>fft.*</c> law family, never
/// exactly bit-for-bit: the twiddle multiplies round. Twiddles come from <see cref="FixedQ4816.SinCos"/> — the
/// existing, independently accurate kernel — computed once per length and cached in
/// <see cref="FixedFourierTransformPlan"/>, never rebuilt per call.
/// </para>
/// </remarks>
public static class FixedFourierTransform {
    // The inverse butterfly's narrow lane forms (u << 16) ± (two twiddle products) in a signed long: with the data
    // below 2^45 each addend is below 2^62 and their sum below 2^63.
    private const ulong InverseNarrowLimit = (1UL << 45);
    private const long HalvingHalf = (1L << (HalvingShift - 1));
    private const long HalvingMask = ((1L << HalvingShift) - 1L);
    private const int HalvingShift = (FixedQ4816.FractionBitCount + 1);

    private static void ForwardButterfly(ReadOnlySpan<FixedComplex> twiddles, Span<FixedComplex> values) {
        var n = values.Length;

        if (n <= 1) { return; }

        TransformKernels.BitReversePermute(values: values);

        for (var length = 2; ; length <<= 1) {
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
                    var t = (twiddles[twiddleIndex] * high[j]);

                    low[j] = (u + t);
                    high[j] = (u - t);
                    twiddleIndex += step;
                }
            }

            if (length == n) { break; }
        }
    }
    private static void InverseButterfly(ReadOnlySpan<FixedComplex> twiddles, Span<FixedComplex> values) {
        var n = values.Length;

        if (n <= 1) { return; }

        TransformKernels.BitReversePermute(values: values);

        for (var length = 2; ; length <<= 1) {
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
                    (low[j], high[j]) = HalvedButterfly(
                        twiddle: twiddles[twiddleIndex],
                        u: low[j],
                        v: high[j]
                    );
                    twiddleIndex += step;
                }
            }

            if (length == n) { break; }
        }
    }
    // Returns ((u + w·v) / 2, (u − w·v) / 2) with one rounding per component: w·v at exact Q32, u lifted to Q32, the
    // sum rounded once at a seventeen-bit shift (ties to even, sign-magnitude, matching RoundProductSum's discipline).
    private static (FixedComplex Sum, FixedComplex Difference) HalvedButterfly(FixedComplex u, FixedComplex v, FixedComplex twiddle) {
        var magnitude = FusedArithmetic.RawMagnitude(value: u.Real.Value) | FusedArithmetic.RawMagnitude(value: u.Imaginary.Value) |
                         FusedArithmetic.RawMagnitude(value: v.Real.Value) | FusedArithmetic.RawMagnitude(value: v.Imaginary.Value);

        if (magnitude < InverseNarrowLimit) {
            var productReal = unchecked(((twiddle.Real.Value * v.Real.Value) - (twiddle.Imaginary.Value * v.Imaginary.Value)));
            var productImaginary = unchecked(((twiddle.Real.Value * v.Imaginary.Value) + (twiddle.Imaginary.Value * v.Real.Value)));
            var liftedReal = (u.Real.Value << FixedQ4816.FractionBitCount);
            var liftedImaginary = (u.Imaginary.Value << FixedQ4816.FractionBitCount);

            return (
                Sum: new(
                    Real: FixedQ4816.FromRawBits(value: RoundHalved(productSum: (liftedReal + productReal))),
                    Imaginary: FixedQ4816.FromRawBits(value: RoundHalved(productSum: (liftedImaginary + productImaginary)))
                ),
                Difference: new(
                    Real: FixedQ4816.FromRawBits(value: RoundHalved(productSum: (liftedReal - productReal))),
                    Imaginary: FixedQ4816.FromRawBits(value: RoundHalved(productSum: (liftedImaginary - productImaginary)))
                )
            );
        }

        var wideReal = unchecked(((((Int128)twiddle.Real.Value) * v.Real.Value) - (((Int128)twiddle.Imaginary.Value) * v.Imaginary.Value)));
        var wideImaginary = unchecked(((((Int128)twiddle.Real.Value) * v.Imaginary.Value) + (((Int128)twiddle.Imaginary.Value) * v.Real.Value)));
        var wideLiftedReal = (((Int128)u.Real.Value) << FixedQ4816.FractionBitCount);
        var wideLiftedImaginary = (((Int128)u.Imaginary.Value) << FixedQ4816.FractionBitCount);

        return (
            Sum: new(
                Real: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProduct(fractionBitCount: HalvingShift, product: (wideLiftedReal + wideReal))),
                Imaginary: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProduct(fractionBitCount: HalvingShift, product: (wideLiftedImaginary + wideImaginary)))
            ),
            Difference: new(
                Real: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProduct(fractionBitCount: HalvingShift, product: (wideLiftedReal - wideReal))),
                Imaginary: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProduct(fractionBitCount: HalvingShift, product: (wideLiftedImaginary - wideImaginary)))
            )
        );
    }
    private static long RoundHalved(long productSum) {
        var sign = (productSum >> 63);
        var magnitude = unchecked((ulong)((productSum ^ sign) - sign));
        var truncated = (magnitude >> HalvingShift);
        var remainder = magnitude & ((ulong)HalvingMask);

        if (
            (remainder > ((ulong)HalvingHalf)) ||
            ((remainder == ((ulong)HalvingHalf)) && (0UL != (truncated & 1UL)))
        ) {
            ++truncated;
        }

        var result = unchecked((long)truncated);

        return unchecked(((result ^ sign) - sign));
    }

    /// <summary>Computes the cyclic convolution of two length-<c>N</c> sequences.</summary>
    /// <param name="plan">The plan for length <c>N</c>; <paramref name="left"/>, <paramref name="right"/> and <paramref name="destination"/> must all have length <c>N</c>.</param>
    /// <param name="left">The first sequence; overwritten with its forward transform.</param>
    /// <param name="right">The second sequence; overwritten with its forward transform. It may be the exact same span as <paramref name="left"/> for a self-convolution, but may not otherwise overlap it.</param>
    /// <param name="destination">Receives the convolution; may not alias <paramref name="left"/> or <paramref name="right"/>.</param>
    /// <remarks>
    /// Forward both operands, multiply pointwise, and invert — the convolution theorem. Ideally
    /// <c>destination[k] = sum over i of left[i] * right[(k - i) mod N]</c>; in fixed point the result holds within a
    /// bound the <c>fft.*</c> laws measure, and its magnitude grows as <c>N</c> times the product of the operands'
    /// amplitudes, so a caller at a wide length or a large amplitude pre-scales the operands as for <see cref="Forward"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">A span's length does not equal <paramref name="plan"/>'s length; <paramref name="left"/> and <paramref name="right"/> partially overlap; or <paramref name="destination"/> overlaps an operand.</exception>
    public static void Convolve(FixedFourierTransformPlan plan, Span<FixedComplex> left, Span<FixedComplex> right, Span<FixedComplex> destination) {
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(left),
            values: ((ReadOnlySpan<FixedComplex>)left)
        );
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(right),
            values: ((ReadOnlySpan<FixedComplex>)right)
        );
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(destination),
            values: ((ReadOnlySpan<FixedComplex>)destination)
        );
        var sameOperands = TransformKernels.RequireConvolutionAliasing(
            destination: ((ReadOnlySpan<FixedComplex>)destination),
            left: ((ReadOnlySpan<FixedComplex>)left),
            right: ((ReadOnlySpan<FixedComplex>)right)
        );

        Forward(
            plan: plan,
            values: left
        );
        if (!sameOperands) {
            Forward(
                plan: plan,
                values: right
            );
        }
        PointwiseMultiply(
            destination: destination,
            left: left,
            right: (sameOperands ? left : right)
        );
        Inverse(
            plan: plan,
            values: destination
        );
    }
    /// <summary>Computes the forward transform in place.</summary>
    /// <param name="plan">The plan for the length of <paramref name="values"/>.</param>
    /// <param name="values">The sequence, transformed in place.</param>
    /// <exception cref="ArgumentException">The length of <paramref name="values"/> does not equal the length of <paramref name="plan"/>.</exception>
    public static void Forward(FixedFourierTransformPlan plan, Span<FixedComplex> values) {
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(values),
            values: ((ReadOnlySpan<FixedComplex>)values)
        );
        ForwardButterfly(
            twiddles: plan.ForwardTwiddles,
            values: values
        );
    }
    /// <summary>Embeds a real sequence (zero imaginary parts) and computes its forward transform.</summary>
    /// <param name="plan">The plan for the length of <paramref name="real"/>.</param>
    /// <param name="real">The real-valued input sequence.</param>
    /// <param name="destination">Receives the transform; the same length as <paramref name="real"/>.</param>
    /// <exception cref="ArgumentException">A span's length does not equal <paramref name="plan"/>'s length.</exception>
    public static void ForwardReal(FixedFourierTransformPlan plan, ReadOnlySpan<FixedQ4816> real, Span<FixedComplex> destination) {
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(real),
            values: real
        );
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(destination),
            values: ((ReadOnlySpan<FixedComplex>)destination)
        );

        for (var i = 0; (i < real.Length); ++i) {
            destination[i] = new(
                Real: real[i],
                Imaginary: FixedQ4816.Zero
            );
        }

        Forward(
            plan: plan,
            values: destination
        );
    }
    /// <summary>Computes the inverse transform in place.</summary>
    /// <param name="plan">The plan for the length of <paramref name="values"/>.</param>
    /// <param name="values">The transformed sequence, restored in place. Callers must pre-scale arbitrary spectra whose butterfly intermediates can exceed <see cref="FixedQ4816"/>'s raw range.</param>
    /// <exception cref="ArgumentException">The length of <paramref name="values"/> does not equal the length of <paramref name="plan"/>.</exception>
    public static void Inverse(FixedFourierTransformPlan plan, Span<FixedComplex> values) {
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(values),
            values: ((ReadOnlySpan<FixedComplex>)values)
        );
        InverseButterfly(
            twiddles: plan.InverseTwiddles,
            values: values
        );
    }
    /// <summary>Computes the inverse transform and discards the imaginary part, for a spectrum known to represent a
    /// real sequence (Hermitian-symmetric).</summary>
    /// <param name="plan">The plan for the length of <paramref name="spectrum"/>.</param>
    /// <param name="spectrum">The spectrum; overwritten with its inverse transform.</param>
    /// <param name="destination">Receives the real part of each restored sample; the same length as <paramref name="spectrum"/>.</param>
    /// <exception cref="ArgumentException">A span's length does not equal <paramref name="plan"/>'s length.</exception>
    public static void InverseReal(FixedFourierTransformPlan plan, Span<FixedComplex> spectrum, Span<FixedQ4816> destination) {
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(spectrum),
            values: ((ReadOnlySpan<FixedComplex>)spectrum)
        );
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(destination),
            values: ((ReadOnlySpan<FixedQ4816>)destination)
        );

        Inverse(
            plan: plan,
            values: spectrum
        );

        for (var i = 0; (i < spectrum.Length); ++i) {
            destination[i] = spectrum[i].Real;
        }
    }
    /// <summary>Multiplies two sequences elementwise — each element is one <see cref="FixedComplex"/> product, each
    /// component rounded once.</summary>
    /// <param name="left">The first sequence.</param>
    /// <param name="right">The second sequence, the same length as <paramref name="left"/>.</param>
    /// <param name="destination">Receives the elementwise product, the same length as the operands; may be the exact same span as either operand, but may not otherwise overlap one.</param>
    /// <exception cref="ArgumentException">The three spans do not share one length, or <paramref name="destination"/> partially overlaps an operand. <c>ParamName</c> names the refused span.</exception>
    public static void PointwiseMultiply(ReadOnlySpan<FixedComplex> left, ReadOnlySpan<FixedComplex> right, Span<FixedComplex> destination) {
        TransformKernels.RequireMatchingLengths(
            destination: ((ReadOnlySpan<FixedComplex>)destination),
            left: left,
            right: right
        );
        TransformKernels.RequirePointwiseAliasing(
            destination: ((ReadOnlySpan<FixedComplex>)destination),
            left: left,
            right: right
        );

        for (var i = 0; (i < left.Length); ++i) {
            destination[i] = (left[i] * right[i]);
        }
    }
}
