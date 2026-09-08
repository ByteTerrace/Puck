namespace Puck.Maths;

/// <summary>
/// The fixed-point discrete cosine transform over <see cref="FixedQ4816"/>: the DCT-II forward and its DCT-III
/// inverse, computed through one <see cref="FixedFourierTransform"/> of the same length rather than a double-length
/// embedding.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scaling convention.</b> <see cref="Forward"/> is the unscaled DCT-II,
/// <c>X[k] = sum over n of x[n] * cos(pi * (2n + 1) * k / (2N))</c>, so a constant input's spectrum is exactly
/// <c>N * value</c> at bin zero and exactly zero elsewhere. <see cref="Inverse"/> is the matching DCT-III with the
/// <c>1/N</c> normalization folded in — <c>x[n] = X[0]/N + (2/N) * sum over k &gt;= 1 of X[k] * cos(pi * (2n + 1) * k / (2N))</c>
/// — reached through <see cref="FixedFourierTransform.Inverse"/>'s per-stage halving, never a late multiply by
/// <c>1/N</c>.
/// </para>
/// <para>
/// <b>The route.</b> The even samples ascend into the front half of a complex scratch sequence and the odd samples
/// descend into the back half (<c>v[n] = x[2n]</c>, <c>v[N-1-n] = x[2n+1]</c>); one forward
/// <see cref="FixedFourierTransform"/> of that sequence, then one post-twiddle by <c>exp(-i*pi*k/(2N))</c> per bin,
/// yields the DCT-II as the real part. The inverse runs the same route backwards: the spectrum is folded into
/// <c>V[k] = exp(+i*pi*k/(2N)) * (X[k] - i*X[N-k])</c> (with <c>X[N] = 0</c>), inverted, and un-permuted. Every
/// twiddle multiply is <see cref="FixedComplex"/>'s one-rounding kernel, so the transform carries one rounding per
/// component per stage plus one for the post-twiddle; the <c>dct.*</c> laws measure and pin what that accumulates
/// to.
/// </para>
/// <para>
/// Both directions are in place over the caller's real sequence and use a caller-supplied
/// <see cref="FixedComplex"/> scratch span of the same length, so nothing allocates beyond the plan and two threads
/// sharing one plan never share a buffer.
/// </para>
/// </remarks>
public static class FixedCosineTransform {
    // The even/odd fold: sample i lands at index i/2 when i is even, and at N - 1 - i/2 when odd, so the front half
    // holds the even samples ascending and the back half the odd samples descending.
    private static int FoldedIndex(int sample, int length) =>
        ((0 == (sample & 1))
            ? (sample >> 1)
            : ((length - 1) - (sample >> 1)));

    /// <summary>Computes the unscaled DCT-II in place.</summary>
    /// <param name="plan">The plan for the length of <paramref name="values"/>.</param>
    /// <param name="values">The real sequence, replaced by its cosine spectrum.</param>
    /// <param name="scratch">A complex working span of the same length; its contents on entry are ignored and on exit are unspecified.</param>
    /// <exception cref="ArgumentException">A span's length does not equal <paramref name="plan"/>'s length.</exception>
    public static void Forward(FixedCosineTransformPlan plan, Span<FixedQ4816> values, Span<FixedComplex> scratch) {
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(values),
            values: ((ReadOnlySpan<FixedQ4816>)values)
        );
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(scratch),
            values: ((ReadOnlySpan<FixedComplex>)scratch)
        );

        var n = values.Length;

        for (var i = 0; (i < n); ++i) {
            scratch[FoldedIndex(length: n, sample: i)] = new(
                Real: values[i],
                Imaginary: FixedQ4816.Zero
            );
        }

        FixedFourierTransform.Forward(
            plan: plan.FourierPlan,
            values: scratch
        );

        var twiddles = plan.ForwardTwiddles;

        for (var k = 0; (k < n); ++k) {
            values[k] = FixedComplex.RealOfProduct(left: scratch[k], right: twiddles[k]);
        }
    }
    /// <summary>Computes the normalized DCT-III in place, undoing <see cref="Forward"/> within the measured bound.</summary>
    /// <param name="plan">The plan for the length of <paramref name="values"/>.</param>
    /// <param name="values">The cosine spectrum, replaced by the restored real sequence.</param>
    /// <param name="scratch">A complex working span of the same length; its contents on entry are ignored and on exit are unspecified.</param>
    /// <exception cref="ArgumentException">A span's length does not equal <paramref name="plan"/>'s length.</exception>
    public static void Inverse(FixedCosineTransformPlan plan, Span<FixedQ4816> values, Span<FixedComplex> scratch) {
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(values),
            values: ((ReadOnlySpan<FixedQ4816>)values)
        );
        TransformKernels.RequireLength(
            expected: plan.Length,
            parameterName: nameof(scratch),
            values: ((ReadOnlySpan<FixedComplex>)scratch)
        );

        var n = values.Length;
        var twiddles = plan.InverseTwiddles;

        for (var k = 0; (k < n); ++k) {
            var partner = ((0 == k)
                ? FixedQ4816.Zero
                : values[(n - k)]);

            scratch[k] = (new FixedComplex(
                Real: values[k],
                Imaginary: -partner
            ) * twiddles[k]);
        }

        FixedFourierTransform.Inverse(
            plan: plan.FourierPlan,
            values: scratch
        );

        for (var i = 0; (i < n); ++i) {
            values[i] = scratch[FoldedIndex(length: n, sample: i)].Real;
        }
    }
}
