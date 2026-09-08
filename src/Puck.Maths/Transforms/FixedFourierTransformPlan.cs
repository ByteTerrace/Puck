using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// A cached twiddle-factor table for one power-of-two transform length, built once from
/// <see cref="FixedQ4816.SinCosTurns"/> and reused across every <see cref="FixedFourierTransform.Forward"/>,
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
    /// <remarks>Forward twiddles represent <c>exp(-2*pi*i*k/length)</c>. Power-of-two lengths have exact binary turn
    /// fractions, evaluated directly by <see cref="FixedQ4816.SinCosTurns"/> without quantizing radians. The second
    /// quarter is reflected from the first, so no error compounds across the table.
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
        var phaseShift = (64 - BitOperations.Log2((uint)length));

        for (var k = 0; (k < half); ++k) {
            if (k <= (length >> 2)) {
                // length == 1 has no twiddles and never executes the otherwise masked shift by 64.
                var phase = unchecked((0UL - (ulong)k) << phaseShift);
                var (sin, cos) = FixedQ4816.SinCosTurns(fractionalTurns: phase);
                forward[k] = new(Real: cos, Imaginary: sin);
            }
            else {
                var reflected = forward[half - k];
                forward[k] = new(Real: -reflected.Real, Imaginary: reflected.Imaginary);
            }
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
