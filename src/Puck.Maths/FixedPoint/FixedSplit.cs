using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// A split-complex number of <see cref="FixedQ4816"/> components — the deterministic 2D <em>scaling</em> primitive, the
/// hyperbolic sibling of <see cref="FixedComplex"/>. An element is <c>u + v·j</c> where the adjoined root satisfies
/// <c>j² = +1</c> (the complex unit satisfies <c>i² = −1</c>; the dual unit <c>ε² = 0</c>). Multiplication composes
/// squeezes, and <see cref="Transform"/> applies one to a vector. Pure integer arithmetic; identical inputs produce
/// identical bits on every machine. Polynomial operations widen every product, accumulate the complete expression, and
/// round once per returned component.
/// </summary>
/// <remarks>
/// The quadratic form <see cref="Norm"/> <c>= u² − v²</c> is <em>indefinite</em>: it is zero on the two diagonal lines
/// <c>u = ±v</c> (the light cone) and negative beyond them. Consequently the ring has zero divisors — for instance
/// <c>(1 + j)(1 − j) = 1 − j² = 0</c> — so a non-zero element need not be invertible, and <see cref="op_Division"/>
/// requires the divisor to be a unit: <see cref="IsUnit"/>, the exact off-cone test <c>|u| ≠ |v|</c>. The rounded
/// <see cref="Norm"/> is not that predicate — it reads zero for a neighborhood of small invertible elements and wraps
/// at the extremes. This is the algebra behind scaling flows and
/// rate/boost composition: the metallic matrix <c>[[k, 1], [1, 0]]</c> and, generally, any real diagonalizable planar
/// map acts naturally here, where <see cref="FixedComplex"/> would model a rotation instead.
/// </remarks>
/// <param name="U">The scalar component.</param>
/// <param name="V">The component along the split unit <c>j</c>.</param>
public readonly record struct FixedSplit(FixedQ4816 U, FixedQ4816 V)
    : IAdditionOperators<FixedSplit, FixedSplit, FixedSplit>,
      ISubtractionOperators<FixedSplit, FixedSplit, FixedSplit>,
      IMultiplyOperators<FixedSplit, FixedSplit, FixedSplit>,
      IDivisionOperators<FixedSplit, FixedSplit, FixedSplit>,
      IUnaryNegationOperators<FixedSplit, FixedSplit>,
      IAdditiveIdentity<FixedSplit, FixedSplit>,
      IMultiplicativeIdentity<FixedSplit, FixedSplit> {
    /// <summary>Gets the additive identity, zero.</summary>
    public static FixedSplit AdditiveIdentity => default;
    /// <summary>Gets the multiplicative identity, one (the identity squeeze).</summary>
    public static FixedSplit MultiplicativeIdentity => new(
        U: FixedQ4816.One,
        V: FixedQ4816.Zero
    );

    /// <summary>Negates a split-complex number.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The componentwise negation.</returns>
    public static FixedSplit operator -(FixedSplit value) =>
        new(
        U: -value.U,
        V: -value.V
    );
    /// <summary>Adds two split-complex numbers.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The componentwise sum.</returns>
    public static FixedSplit operator +(FixedSplit left, FixedSplit right) =>
        new(
        U: (left.U + right.U),
        V: (left.V + right.V)
    );
    /// <summary>Subtracts <paramref name="right"/> from <paramref name="left"/>.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The componentwise difference.</returns>
    public static FixedSplit operator -(FixedSplit left, FixedSplit right) =>
        new(
        U: (left.U - right.U),
        V: (left.V - right.V)
    );
    /// <summary>Multiplies two split-complex numbers (composes squeezes for unit operands).</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The product <c>(u₁u₂ + v₁v₂) + (u₁v₂ + u₂v₁)·j</c>, each component's two products accumulated exactly with one rounding.</returns>
    public static FixedSplit operator *(FixedSplit left, FixedSplit right) {
        const ulong NarrowLimit = (1UL << 31);
        var combinedMagnitude = FusedArithmetic.RawMagnitude(value: left.U.Value) |
                                 FusedArithmetic.RawMagnitude(value: left.V.Value) |
                                 FusedArithmetic.RawMagnitude(value: right.U.Value) |
                                 FusedArithmetic.RawMagnitude(value: right.V.Value);

        if (combinedMagnitude < NarrowLimit) {
            return new(
                U: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((left.U.Value * right.U.Value) + (left.V.Value * right.V.Value))))),
                V: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((left.U.Value * right.V.Value) + (left.V.Value * right.U.Value)))))
            );
        }

        return new(
            U: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((((Int128)left.U.Value * right.U.Value) + ((Int128)left.V.Value * right.V.Value))))),
            V: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((((Int128)left.U.Value * right.V.Value) + ((Int128)left.V.Value * right.U.Value)))))
        );
    }
    /// <summary>Divides <paramref name="left"/> by <paramref name="right"/>.</summary>
    /// <param name="left">The dividend.</param>
    /// <param name="right">The divisor; must be a unit (<see cref="IsUnit"/> — off the lines <c>u = ±v</c>).</param>
    /// <returns>The quotient <c>left·conj(right) / (c² − d²)</c>, each component rounded once.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="right"/> lies on the light cone — the exact 128-bit
    /// test <c>|u| = |v|</c>, the complement of <see cref="IsUnit"/>; a zero divisor has no inverse even when it is
    /// itself non-zero. The rounded <see cref="Norm"/> is NOT the refusal predicate: it reads
    /// <see cref="FixedQ4816.Zero"/> for small invertible elements (every <c>(u, 0)</c> with <c>|u| ≤ 181</c> raw)
    /// that divide correctly.</exception>
    public static FixedSplit operator /(FixedSplit left, FixedSplit right) {
        // conj(c + d·j) = c − d·j, and (c + d·j)(c − d·j) = c² − d². The numerators are left·conj(right).
        var realNumerator = FusedArithmetic.AddProducts(
            firstLeft: left.U.Value,
            firstRight: right.U.Value,
            secondLeft: left.V.Value,
            secondRight: right.V.Value,
            subtractSecond: true
        );
        var splitNumerator = FusedArithmetic.AddProducts(
            firstLeft: left.V.Value,
            firstRight: right.U.Value,
            secondLeft: left.U.Value,
            secondRight: right.V.Value,
            subtractSecond: true
        );
        var denominator = FusedArithmetic.AddProducts(
            firstLeft: right.U.Value,
            firstRight: right.U.Value,
            secondLeft: right.V.Value,
            secondRight: right.V.Value,
            subtractSecond: true
        );

        if (denominator.Magnitude == UInt128.Zero) {
            throw new DivideByZeroException(message: "A split-complex zero divisor (|u| = |v|) has no inverse.");
        }

        return new(
            U: FixedQ4816.FromRawBits(value: FusedArithmetic.DivideProductSum(numerator: realNumerator, denominator: denominator)),
            V: FixedQ4816.FromRawBits(value: FusedArithmetic.DivideProductSum(numerator: splitNumerator, denominator: denominator))
        );
    }

    /// <summary>Creates the squeeze of hyperbolic angle <paramref name="rapidity"/> — the split exponential map
    /// <c>exp(j·φ) = cosh φ + j·sinh φ</c>, the scaling analog of <see cref="FixedComplex.FromAngle"/>.</summary>
    /// <param name="rapidity">The hyperbolic angle; rapidities add under multiplication, so squeezes compose by summing this parameter.</param>
    /// <returns>The split-complex number <c>cosh φ + j·sinh φ</c>. Its <see cref="Norm"/> tracks one only while the
    /// backward exponential <c>e^−|φ|</c> is comfortably representable; the deviation grows as that term approaches a
    /// Q16 ULP, and from raw rapidity ±726822 (<c>|φ| &gt; 16·ln 2 ≈ 11.0904</c>, where the term rounds to zero) the
    /// two components collide bit-for-bit onto the light cone: <see cref="IsUnit"/> is <see langword="false"/> there,
    /// the result has no inverse — division by it throws and multiplying by its <see cref="Conjugate"/> yields the
    /// zero element. Both components saturate to <see cref="FixedQ4816.MaxValue"/> once the true value leaves the
    /// carrier (<c>|φ| ≳ 33.27</c>), the sine carrying the rapidity's sign.</returns>
    /// <remarks>Built from <see cref="FixedQ4816.Exp2"/> with the halving folded into the exponent: for
    /// <c>s = φ·log₂ e</c>, formed wide and clamped, <c>cosh φ = 2^(s−1) + 2^(−s−1)</c> and
    /// <c>sinh φ = 2^(s−1) − 2^(−s−1)</c> — one rounding per term, the sum exact. Deterministic and bit-identical
    /// across machines. The light-cone collapse is representational — Q16 cannot hold <c>cosh</c> and <c>sinh</c>
    /// apart once their difference <c>e^−|φ|</c> drops below an ULP — and the boundary, the saturation band, and the
    /// per-row norm envelope are pinned by the <c>split.rapidity-ladder</c> law.</remarks>
    public static FixedSplit FromRapidity(FixedQ4816 rapidity) {
        var (cosh, sinh) = FixedQ4816.CoshSinh(argument: rapidity);

        return new(
            U: cosh,
            V: sinh
        );
    }

    /// <summary>Gets the indefinite quadratic form <c>u² − v²</c> — the invariant a unit squeeze preserves.</summary>
    /// <remarks>The mathematical form is positive inside the light cone (<c>|u| &gt; |v|</c>), zero on it, and
    /// negative outside; it is not a magnitude and admits no real square root beyond the interior. The returned value
    /// is the exact raw Q32 difference rounded once to Q16, wrapping rather than saturating — so it carries neither
    /// the sign nor the vanishing of the form outside the carrier's window: a strictly-inside pair can read negative
    /// once the Q32 difference wraps, and an element whose exact form is below half a raw unit (every <c>(u, 0)</c>
    /// with <c>|u| ≤ 181</c>) reads zero while remaining invertible. The exact cone test is <see cref="IsUnit"/>,
    /// never this member.</remarks>
    public FixedQ4816 Norm {
        get {
            // Both raws below 2^31 keep each raw Q32 square below 2^62, so their difference stays inside a signed long
            // — the same gate operator * and Transform use, and the window the generic twin's narrow norm tier takes.
            const ulong NarrowLimit = (1UL << 31);
            var combinedMagnitude = FusedArithmetic.RawMagnitude(value: U.Value) | FusedArithmetic.RawMagnitude(value: V.Value);

            if (combinedMagnitude < NarrowLimit) {
                return FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((U.Value * U.Value) - (V.Value * V.Value)))));
            }

            return FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((((Int128)U.Value * U.Value) - ((Int128)V.Value * V.Value)))));
        }
    }
    /// <summary>Gets whether this element is a unit — invertible, off the light cone.</summary>
    public bool IsUnit => ((U.Value != V.Value) && (U.Value != -V.Value));

    /// <summary>Returns the conjugate <c>u − v·j</c>. The product <c>s·Conjugate()</c> is <c>(Norm, 0)</c>, so the
    /// conjugate is the inverse squeeze only for a squeeze of norm ONE; a norm-minus-one unit's conjugate is minus
    /// its inverse, and a light-cone element's conjugate annihilates it to the zero element.</summary>
    /// <returns>The split-complex number with the split component negated.</returns>
    public FixedSplit Conjugate() =>
        new(
        U: U,
        V: -V
    );
    /// <summary>Applies this split-complex number to a 2D vector as a squeeze (hyperbolic rotation).</summary>
    /// <param name="vector">The vector to transform, read as <c>x + y·j</c>.</param>
    /// <returns>The transformed vector <c>(u·x + v·y, u·y + v·x)</c> — the split product, two products per component with one rounding.</returns>
    public FixedVector2 Transform(FixedVector2 vector) {
        const ulong NarrowLimit = (1UL << 31);
        var combinedMagnitude = FusedArithmetic.RawMagnitude(value: U.Value) |
                                 FusedArithmetic.RawMagnitude(value: V.Value) |
                                 FusedArithmetic.RawMagnitude(value: vector.X.Value) |
                                 FusedArithmetic.RawMagnitude(value: vector.Y.Value);

        if (combinedMagnitude < NarrowLimit) {
            return new(
                X: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((U.Value * vector.X.Value) + (V.Value * vector.Y.Value))))),
                Y: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked(((U.Value * vector.Y.Value) + (V.Value * vector.X.Value)))))
            );
        }

        return new(
            X: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((((Int128)U.Value * vector.X.Value) + ((Int128)V.Value * vector.Y.Value))))),
            Y: FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: unchecked((((Int128)U.Value * vector.Y.Value) + ((Int128)V.Value * vector.X.Value)))))
        );
    }

}
