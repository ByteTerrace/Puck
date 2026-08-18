using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// The fixed-point fast Fourier transform over <see cref="FixedComplex"/>: in-place radix-2 forward and inverse
/// transforms, and real-valued convenience wrappers over the same engine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scaling convention.</b> <see cref="Forward"/> is UNSCALED — <c>X[k] = sum over n of x[n] * exp(-i*2*pi*k*n/N)</c>,
/// the textbook sum, so an impulse, a DC-only input and a Nyquist-alternating input all produce EXACT bin values (the
/// twiddle at those bins is exactly <c>±1</c> or <c>±i</c>, so the one rounding a general product carries never
/// happens). <see cref="Inverse"/> instead halves every component at EACH of the <c>log2(N)</c> butterfly stages, so
/// the accumulated <c>1/N</c> normalization is reached by exact bit shifts of a representable quantity rather than by
/// one late multiply by <c>1/N</c> — which underflows to zero once <c>N &gt; 2^16</c>, past <see cref="FixedQ4816"/>'s
/// sixteen fraction bits. <see cref="Inverse"/> therefore never overflows past its own input's scale (repeated
/// halving only shrinks), while <see cref="Forward"/> can grow a factor of up to <c>N</c> across its stages and
/// documents that as an envelope: callers with wide-length, full-scale inputs must pre-scale to stay inside
/// <see cref="FixedQ4816"/>'s raw range.
/// </para>
/// <para>
/// Every butterfly's twiddle multiply is <see cref="FixedComplex"/>'s own operator, which is the ONE-ROUNDING fused
/// kernel — each returned component accumulates its two leaf products exactly and rounds once
/// (<see cref="FixedQ4816.RoundProductSum(Int128)"/>). <see cref="Inverse"/>'s per-stage halving is one further
/// ties-to-even <see cref="FixedQ4816"/> division by two per component per stage.
/// </para>
/// <para>
/// <c>Inverse(Forward(x))</c> recovers <c>x</c> within a bound measured and pinned by the <c>fft.*</c> law family, never
/// exactly bit-for-bit: the twiddle multiplies round. Twiddles come from <see cref="FixedQ4816.SinCos"/> — the
/// existing, independently accurate kernel — computed once per length and cached in <see cref="FixedFourierPlan"/>,
/// never rebuilt per call.
/// </para>
/// </remarks>
public static class FixedFourierTransform {
    /// <summary>Computes the forward transform in place.</summary>
    /// <param name="plan">The plan for <paramref name="values"/>' length.</param>
    /// <param name="values">The sequence, transformed in place.</param>
    /// <exception cref="ArgumentException"><paramref name="values"/>'s length does not equal <paramref name="plan"/>'s length.</exception>
    public static void Forward(FixedFourierPlan plan, Span<FixedComplex> values) {
        RequireLength(plan: plan, values: values, parameterName: nameof(values));
        Butterfly(halveEachStage: false, twiddles: plan.ForwardTwiddles, values: values);
    }
    /// <summary>Embeds a real sequence (zero imaginary parts) and computes its forward transform.</summary>
    /// <param name="plan">The plan for <paramref name="real"/>'s length.</param>
    /// <param name="real">The real-valued input sequence.</param>
    /// <param name="destination">Receives the transform; the same length as <paramref name="real"/>.</param>
    /// <exception cref="ArgumentException">A span's length does not equal <paramref name="plan"/>'s length.</exception>
    public static void ForwardReal(FixedFourierPlan plan, ReadOnlySpan<FixedQ4816> real, Span<FixedComplex> destination) {
        RequireLength(plan: plan, values: real, parameterName: nameof(real));
        RequireLength(plan: plan, values: destination, parameterName: nameof(destination));

        for (var i = 0; (i < real.Length); ++i) {
            destination[i] = new(Real: real[i], Imaginary: FixedQ4816.Zero);
        }

        Forward(plan: plan, values: destination);
    }
    /// <summary>Computes the inverse transform in place.</summary>
    /// <param name="plan">The plan for <paramref name="values"/>' length.</param>
    /// <param name="values">The transformed sequence, restored in place.</param>
    /// <exception cref="ArgumentException"><paramref name="values"/>'s length does not equal <paramref name="plan"/>'s length.</exception>
    public static void Inverse(FixedFourierPlan plan, Span<FixedComplex> values) {
        RequireLength(plan: plan, values: values, parameterName: nameof(values));
        Butterfly(halveEachStage: true, twiddles: plan.InverseTwiddles, values: values);
    }
    /// <summary>Computes the inverse transform and discards the imaginary part, for a spectrum known to represent a
    /// real sequence (Hermitian-symmetric).</summary>
    /// <param name="plan">The plan for <paramref name="spectrum"/>'s length.</param>
    /// <param name="spectrum">The spectrum; OVERWRITTEN with its inverse transform.</param>
    /// <param name="destination">Receives the real part of each restored sample; the same length as <paramref name="spectrum"/>.</param>
    /// <exception cref="ArgumentException">A span's length does not equal <paramref name="plan"/>'s length.</exception>
    public static void InverseReal(FixedFourierPlan plan, Span<FixedComplex> spectrum, Span<FixedQ4816> destination) {
        RequireLength(plan: plan, values: spectrum, parameterName: nameof(spectrum));
        RequireLength(plan: plan, values: destination, parameterName: nameof(destination));

        Inverse(plan: plan, values: spectrum);

        for (var i = 0; (i < spectrum.Length); ++i) {
            destination[i] = spectrum[i].Real;
        }
    }

    // In-place radix-2 decimation-in-time, the same bit-reversal-then-stages shape as NumberTheoreticTransform.Butterfly.
    // halveEachStage carries the inverse's per-stage 1/N normalization: applied to every stage rather than once at the
    // end, so it never asks FixedQ4816 to represent 1/N directly.
    private static void Butterfly(ReadOnlySpan<FixedComplex> twiddles, Span<FixedComplex> values, bool halveEachStage) {
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

        for (var length = 2; (length <= n); length <<= 1) {
            var half = (length >> 1);
            var step = (n / length);

            for (var i = 0; (i < n); i += length) {
                for (var j = 0; (j < half); ++j) {
                    var w = twiddles[(j * step)];
                    var u = values[(i + j)];
                    var t = (w * values[((i + j) + half)]);
                    var sum = (u + t);
                    var difference = (u - t);

                    values[(i + j)] = (halveEachStage ? Half(value: sum) : sum);
                    values[((i + j) + half)] = (halveEachStage ? Half(value: difference) : difference);
                }
            }
        }
    }
    private static FixedComplex Half(FixedComplex value) =>
        new(Real: (value.Real / TwoRaw), Imaginary: (value.Imaginary / TwoRaw));
    private static void RequireLength<T>(FixedFourierPlan plan, ReadOnlySpan<T> values, string parameterName) {
        if (values.Length != plan.Length) {
            throw new ArgumentException(message: $"expected length {plan.Length} (the plan's length); got {values.Length}.", paramName: parameterName);
        }
    }

    private static readonly FixedQ4816 TwoRaw = FixedQ4816.FromInteger(value: 2);
}
/// <summary>
/// A cached twiddle-factor table for one power-of-two transform length, built once from
/// <see cref="FixedQ4816.SinCos"/> and reused across every <see cref="FixedFourierTransform.Forward"/> and
/// <see cref="FixedFourierTransform.Inverse"/> call at that length.
/// </summary>
public sealed class FixedFourierPlan {
    private readonly FixedComplex[] m_forwardTwiddles;
    private readonly FixedComplex[] m_inverseTwiddles;

    private FixedFourierPlan(int length, FixedComplex[] forwardTwiddles, FixedComplex[] inverseTwiddles) {
        Length = length;
        m_forwardTwiddles = forwardTwiddles;
        m_inverseTwiddles = inverseTwiddles;
    }

    /// <summary>Builds the twiddle table for a transform length.</summary>
    /// <param name="length">The transform length; must be a power of two.</param>
    /// <returns>The plan.</returns>
    /// <remarks>Each forward twiddle is <c>FixedComplex.FromAngle(FromDouble(-2*pi*k/length))</c> — an independent
    /// <see cref="FixedQ4816.SinCos"/> call per entry rather than an incrementally multiplied ladder, so each
    /// twiddle's error stays at <see cref="FixedQ4816.SinCos"/>'s own bound instead of compounding over the table.
    /// The double-precision angle is the deterministic authoring boundary <see cref="FixedQ4816.FromDouble"/>
    /// documents: IEEE-754 multiply and divide are correctly rounded, so the same table is built on every machine.
    /// Inverse twiddles are the EXACT conjugates of the forward ones — no second table generator.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not a positive power of two.</exception>
    public static FixedFourierPlan Create(int length) {
        if (
            (length <= 0) ||
            !BitOperations.IsPow2(value: ((uint)length))
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(length), message: "length must be a positive power of two.");
        }

        var half = (length >> 1);
        var forward = new FixedComplex[half];
        var inverse = new FixedComplex[half];
        var turn = ((-2.0 * Math.PI) / length);

        for (var k = 0; (k < half); ++k) {
            var angle = FixedQ4816.FromDouble(value: (turn * k));

            forward[k] = FixedComplex.FromAngle(angle: angle);
            inverse[k] = forward[k].Conjugate();
        }

        return new(
            forwardTwiddles: forward,
            inverseTwiddles: inverse,
            length: length
        );
    }

    /// <summary>Gets the transform length this plan was built for.</summary>
    public int Length { get; }

    internal ReadOnlySpan<FixedComplex> ForwardTwiddles => m_forwardTwiddles;
    internal ReadOnlySpan<FixedComplex> InverseTwiddles => m_inverseTwiddles;
}
