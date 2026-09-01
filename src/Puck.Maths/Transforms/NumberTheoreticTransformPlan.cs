namespace Puck.Maths;

/// <summary>
/// A cached root-of-unity table for one power-of-two transform length, built once and reused across every
/// <see cref="NumberTheoreticTransform.Forward"/>, <see cref="NumberTheoreticTransform.Inverse"/> and
/// <see cref="NumberTheoreticTransform.Convolve"/> call at that length. Building the plan is the only place the
/// transform allocates.
/// </summary>
/// <remarks>Every table entry is held in Montgomery form (multiplied by the ring's radix), the representation the
/// butterfly network runs in; a caller never sees these values, only the ordinary residues the transform encodes
/// into and decodes out of that form at its boundary.</remarks>
public sealed class NumberTheoreticTransformPlan {
    private readonly ulong[] m_forwardTwiddles;
    private readonly ulong[] m_inverseTwiddles;
    private readonly ulong m_lengthInverse;

    private NumberTheoreticTransformPlan(int length, ulong[] forwardTwiddles, ulong[] inverseTwiddles, ulong lengthInverse) {
        Length = length;
        m_forwardTwiddles = forwardTwiddles;
        m_inverseTwiddles = inverseTwiddles;
        m_lengthInverse = lengthInverse;
    }

    /// <summary>Builds the root-of-unity table for a transform length.</summary>
    /// <param name="length">The transform length; must be a positive power of two. The largest power of two an
    /// <see cref="int"/> can name is <c>2^30</c>, far below <c>2^MaximumLog2Length</c>, so every representable
    /// length is legal and the prime's own two-adicity ceiling is never the refusal a caller hits.</param>
    /// <returns>The plan.</returns>
    /// <remarks>The table holds <c>root^0 .. root^(length/2 - 1)</c> for <c>root = PrimitiveRoot^((Modulus - 1) / length)</c>,
    /// the primitive <c>length</c>-th root of unity, built as an exact multiplicative ladder — every entry is a ring
    /// product, so nothing here rounds. The inverse table is the same ladder over the root's inverse.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not a positive power of two.</exception>
    public static NumberTheoreticTransformPlan Create(int length) {
        TransformKernels.RequirePowerOfTwo(
            length: length,
            parameterName: nameof(length)
        );

        var ring = NumberTheoreticTransform.Ring;
        var half = (length >> 1);
        var forward = new ulong[half];
        var inverse = new ulong[half];

        if (half > 0) {
            var root = ring.Power(
                exponent: ((NumberTheoreticTransform.Modulus - 1UL) / ((ulong)length)),
                value: ring.Encode(value: NumberTheoreticTransform.PrimitiveRoot)
            );
            var inverseRoot = ring.Power(
                exponent: (NumberTheoreticTransform.Modulus - 2UL),
                value: root
            );
            var forwardPower = ring.One;
            var inversePower = ring.One;

            for (var k = 0; (k < half); ++k) {
                forward[k] = forwardPower;
                inverse[k] = inversePower;
                forwardPower = ring.Multiply(
                    left: forwardPower,
                    right: root
                );
                inversePower = ring.Multiply(
                    left: inversePower,
                    right: inverseRoot
                );
            }
        }

        // Held as the ordinary residue 1/N rather than its Montgomery form: multiplying a Montgomery-form element by
        // it strips the radix and applies the scale in the same REDC, which is how Inverse decodes.
        var lengthInverse = ring.Decode(value: ring.Power(
            exponent: (NumberTheoreticTransform.Modulus - 2UL),
            value: ring.Encode(value: ((ulong)length))
        ));

        return new(
            forwardTwiddles: forward,
            inverseTwiddles: inverse,
            length: length,
            lengthInverse: lengthInverse
        );
    }

    internal ReadOnlySpan<ulong> ForwardTwiddles => m_forwardTwiddles;
    internal ReadOnlySpan<ulong> InverseTwiddles => m_inverseTwiddles;
    internal ulong LengthInverse => m_lengthInverse;

    /// <summary>Gets the transform length this plan was built for.</summary>
    public int Length { get; }
}
