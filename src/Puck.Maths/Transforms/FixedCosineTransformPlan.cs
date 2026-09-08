using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// A cached plan for one power-of-two cosine-transform length: the <see cref="FixedFourierTransformPlan"/> the
/// transform rides on, plus the quarter-turn post-twiddles <c>exp(-i*pi*k/(2N))</c> that fold the half-sample shift of
/// the DCT-II into the Fourier route. Building the plan is the only place the transform allocates.
/// </summary>
public sealed class FixedCosineTransformPlan {
    private readonly FixedComplex[] m_forwardTwiddles;
    private readonly FixedComplex[] m_inverseTwiddles;

    private FixedCosineTransformPlan(FixedFourierTransformPlan fourierPlan, FixedComplex[] forwardTwiddles, FixedComplex[] inverseTwiddles) {
        FourierPlan = fourierPlan;
        m_forwardTwiddles = forwardTwiddles;
        m_inverseTwiddles = inverseTwiddles;
    }

    /// <summary>Builds the plan for a transform length.</summary>
    /// <param name="length">The transform length; must be a positive power of two.</param>
    /// <returns>The plan.</returns>
    /// <remarks>Forward twiddles represent <c>exp(-i*pi*k/(2*length))</c> for <c>k = 0 .. length - 1</c>.
    /// Exact binary turn fractions enter <see cref="FixedQ4816.SinCosTurns"/> directly. Complementary angles share
    /// one evaluation, with no accumulated error; inverse twiddles are exact conjugates. The Fourier plan is built by
    /// <see cref="FixedFourierTransformPlan.Create"/> at the same length.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not a positive power of two.</exception>
    public static FixedCosineTransformPlan Create(int length) {
        TransformKernels.RequirePowerOfTwo(
            length: length,
            parameterName: nameof(length)
        );

        var fourierPlan = FixedFourierTransformPlan.Create(length: length);
        var forward = new FixedComplex[length];
        var inverse = new FixedComplex[length];
        var phaseShift = (62 - BitOperations.Log2((uint)length));

        for (var k = 0; (k < length); ++k) {
            if (k <= (length >> 1)) {
                var phase = unchecked((0UL - (ulong)k) << phaseShift);
                var (sin, cos) = FixedQ4816.SinCosTurns(fractionalTurns: phase);
                forward[k] = new(Real: cos, Imaginary: sin);
            }
            else {
                var reflected = forward[length - k];
                forward[k] = new(Real: -reflected.Imaginary, Imaginary: -reflected.Real);
            }
            inverse[k] = forward[k].Conjugate();
        }

        return new(
            forwardTwiddles: forward,
            fourierPlan: fourierPlan,
            inverseTwiddles: inverse
        );
    }

    internal ReadOnlySpan<FixedComplex> ForwardTwiddles => m_forwardTwiddles;
    internal FixedFourierTransformPlan FourierPlan { get; }
    internal ReadOnlySpan<FixedComplex> InverseTwiddles => m_inverseTwiddles;

    /// <summary>Gets the transform length this plan was built for.</summary>
    public int Length => FourierPlan.Length;
}
