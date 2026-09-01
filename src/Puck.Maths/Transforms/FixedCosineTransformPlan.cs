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
    /// <remarks>Each forward twiddle is <c>FixedComplex.FromAngle(FromDouble(-pi*k/(2*length)))</c> for
    /// <c>k = 0 .. length - 1</c>, an independent <see cref="FixedQ4816.SinCos"/> call per entry so no error compounds
    /// across the table; inverse twiddles are the exact conjugates. The Fourier plan beneath is built by
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
        var turn = (-Math.PI / (2.0 * length));

        for (var k = 0; (k < length); ++k) {
            var angle = FixedQ4816.FromDouble(value: (turn * k));

            forward[k] = FixedComplex.FromAngle(angle: angle);
            inverse[k] = forward[k].Conjugate();
        }

        return new(
            forwardTwiddles: forward,
            fourierPlan: fourierPlan,
            inverseTwiddles: inverse
        );
    }

    internal FixedFourierTransformPlan FourierPlan { get; }
    internal ReadOnlySpan<FixedComplex> ForwardTwiddles => m_forwardTwiddles;
    internal ReadOnlySpan<FixedComplex> InverseTwiddles => m_inverseTwiddles;

    /// <summary>Gets the transform length this plan was built for.</summary>
    public int Length => FourierPlan.Length;
}
