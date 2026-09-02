namespace Puck.Maths;

/// <summary>
/// A cached twiddle-factor table for one power-of-two transform length, built once from
/// <see cref="FixedQ4816.SinCos"/> and reused across every <see cref="FixedFourierTransform.Forward"/>,
/// <see cref="FixedFourierTransform.Inverse"/> and <see cref="FixedFourierTransform.Convolve"/> call at that length.
/// Building the plan is the only place the transform allocates.
/// </summary>
public sealed class FixedFourierTransformPlan {
    private readonly FixedComplex[] m_forwardTwiddles;
    private readonly FixedComplex[] m_inverseTwiddles;

    private FixedFourierTransformPlan(int length, FixedComplex[] forwardTwiddles, FixedComplex[] inverseTwiddles) {
        Length = length;
        m_forwardTwiddles = forwardTwiddles;
        m_inverseTwiddles = inverseTwiddles;
    }

    /// <summary>Builds the twiddle table for a transform length.</summary>
    /// <param name="length">The transform length; must be a positive power of two.</param>
    /// <returns>The plan.</returns>
    /// <remarks>Each forward twiddle is <c>FixedComplex.FromAngle(FromDouble(-2*pi*k/length))</c> — an independent
    /// <see cref="FixedQ4816.SinCos"/> call per entry rather than an incrementally multiplied ladder, so each
    /// twiddle's error stays at <see cref="FixedQ4816.SinCos"/>'s own bound instead of compounding over the table.
    /// The double-precision angle is the deterministic authoring boundary <see cref="FixedQ4816.FromDouble"/>
    /// documents: IEEE-754 multiply and divide are correctly rounded, so the same table is built on every machine.
    /// Inverse twiddles are the exact conjugates of the forward ones — no second table generator.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not a positive power of two.</exception>
    public static FixedFourierTransformPlan Create(int length) {
        TransformKernels.RequirePowerOfTwo(
            length: length,
            parameterName: nameof(length)
        );

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

    internal ReadOnlySpan<FixedComplex> ForwardTwiddles => m_forwardTwiddles;
    internal ReadOnlySpan<FixedComplex> InverseTwiddles => m_inverseTwiddles;

    /// <summary>Gets the transform length this plan was built for.</summary>
    public int Length { get; }
}
