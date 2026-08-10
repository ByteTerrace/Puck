using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// The multi-generator quadratic algebra over <see cref="FixedQ4816"/> — the geometric (Clifford) algebra of a
/// signature <c>(p, q, r)</c> with up to four generators, freeing the generator count that
/// <see cref="QuadraticAlgebra{TScalar}"/> fixes at one. A signature adjoins <c>p</c> generators squaring to
/// <c>+1</c>, <c>q</c> squaring to <c>−1</c>, and <c>r</c> squaring to <c>0</c> (degenerate), and its geometric
/// product is driven by a blade-multiplication table computed once per signature. Every planar number system in the
/// library is the one-generator case — <c>(0, 1, 0)</c> is <see cref="FixedComplex"/>, <c>(1, 0, 0)</c> is
/// <see cref="FixedSplit"/>, <c>(0, 0, 1)</c> is <see cref="FixedDual{TValue}"/> — and the engine's transform stack is
/// the multi-generator case: the even subalgebra of <c>(3, 0, 0)</c> is <see cref="FixedQuaternion"/>, and rigid
/// motions are the <see cref="SandwichTransform"/> action of <em>motors</em> — the even subalgebra of
/// <c>(3, 0, 1)</c>, which is the dual quaternion behind <see cref="FixedRigidTransform"/>. Pure integer arithmetic:
/// identical inputs produce identical bits on every machine.
/// </summary>
/// <remarks>
/// A <see cref="Multivector"/> carries one <see cref="FixedQ4816"/> coefficient per basis blade, indexed by the
/// generator subset it spans read as a bitmask (bit <c>k</c> set means generator <c>k</c> is present, in ascending
/// canonical order). Four generators give sixteen blades, so the coefficient buffer is a fixed sixteen-lane
/// allocation-free struct regardless of signature. A descriptor accepts a multivector only when every lane at or above
/// its <see cref="BladeCount"/> is zero; semantic operations reject a nonzero unused lane instead of silently projecting
/// it away. The default descriptor is intentionally the zero-generator scalar algebra and behaves like
/// <c>Create(0, 0, 0)</c> throughout the public surface. The <see cref="GeometricProduct"/> accumulates every signed
/// blade-pair product contributing to a result blade at raw Q32 width and rounds that blade exactly once (the
/// accumulate-wide-round-once discipline of <see cref="FixedQuaternion"/> and <see cref="FixedComplex"/>), so the
/// even subalgebra of <c>(3, 0, 0)</c> reproduces <see cref="FixedQuaternion"/> bit-for-bit over the full raw range,
/// not merely on a fractional sublattice.
/// </remarks>
public readonly struct GeometricAlgebra : IEquatable<GeometricAlgebra> {
    // Signed reordering + square sign for every ordered pair of basis blades (row·16 + column). A zero marks a
    // product annihilated by a shared degenerate generator; the result blade is always the bitwise XOR of the pair,
    // so only the sign is tabulated. Built once by Create.
    private readonly sbyte[] m_productSign;

    private GeometricAlgebra(int positiveCount, int negativeCount, int degenerateCount, sbyte[] productSign) {
        PositiveCount = positiveCount;
        NegativeCount = negativeCount;
        DegenerateCount = degenerateCount;
        m_productSign = productSign;
    }

    /// <summary>Gets the number of generators squaring to <c>+1</c>.</summary>
    public int PositiveCount { get; }
    /// <summary>Gets the number of generators squaring to <c>−1</c>.</summary>
    public int NegativeCount { get; }
    /// <summary>Gets the number of degenerate generators, squaring to <c>0</c>.</summary>
    public int DegenerateCount { get; }
    /// <summary>Gets the total number of generators, <c>p + q + r</c> (at most four).</summary>
    public int GeneratorCount => ((PositiveCount + NegativeCount) + DegenerateCount);
    /// <summary>Gets the number of basis blades, <c>2^(p + q + r)</c> (at most sixteen).</summary>
    public int BladeCount => (1 << GeneratorCount);

    /// <summary>Creates the geometric algebra of signature <c>(p, q, r)</c>.</summary>
    /// <param name="positiveCount">The number of generators squaring to <c>+1</c>.</param>
    /// <param name="negativeCount">The number of generators squaring to <c>−1</c>.</param>
    /// <param name="degenerateCount">The number of degenerate generators, squaring to <c>0</c>.</param>
    /// <returns>The described algebra, with its blade-multiplication table computed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative, or the total <c>p + q + r</c> exceeds four.</exception>
    public static GeometricAlgebra Create(int positiveCount, int negativeCount, int degenerateCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: positiveCount);
        ArgumentOutOfRangeException.ThrowIfNegative(value: negativeCount);
        ArgumentOutOfRangeException.ThrowIfNegative(value: degenerateCount);

        // Validate against the remaining capacity before adding. Apart from preventing overflow, this reports the
        // offending public parameter rather than a private generatorCount local.
        if (positiveCount > 4) { ThrowSignatureTooLarge(value: positiveCount, paramName: nameof(positiveCount)); }
        if (negativeCount > (4 - positiveCount)) { ThrowSignatureTooLarge(value: negativeCount, paramName: nameof(negativeCount)); }
        if (degenerateCount > ((4 - positiveCount) - negativeCount)) { ThrowSignatureTooLarge(value: degenerateCount, paramName: nameof(degenerateCount)); }

        var generatorCount = ((positiveCount + negativeCount) + degenerateCount);

        var dimension = (1 << generatorCount);
        var productSign = new sbyte[(Multivector.BladeCapacity * Multivector.BladeCapacity)];

        for (var left = 0; (left < dimension); ++left) {
            for (var right = 0; (right < dimension); ++right) {
                productSign[((left * Multivector.BladeCapacity) + right)] = (sbyte)BladeProductSign(
                    left: left,
                    right: right,
                    positiveCount: positiveCount,
                    negativeCount: negativeCount
                );
            }
        }

        return new(
            positiveCount: positiveCount,
            negativeCount: negativeCount,
            degenerateCount: degenerateCount,
            productSign: productSign
        );
    }

    /// <summary>Gets the square of a single generator, <c>+1</c>, <c>−1</c>, or <c>0</c> per the signature.</summary>
    /// <param name="generatorIndex">The zero-based generator index, below <see cref="GeneratorCount"/>.</param>
    /// <returns>The generator's square as an integer.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="generatorIndex"/> is outside the generator range.</exception>
    public int Square(int generatorIndex) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: generatorIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: generatorIndex, other: GeneratorCount);

        return GeneratorSquare(
            generatorIndex: generatorIndex,
            positiveCount: PositiveCount,
            negativeCount: NegativeCount
        );
    }

    /// <summary>Multiplies two multivectors under the geometric product of this signature.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The geometric product <c>left · right</c>: every signed blade-pair product contributing to a result
    /// blade is accumulated at raw Q32 width and the blade is rounded exactly once to Q16, ties to even.</returns>
    /// <remarks>The result is independent of the order in which the contributing products are summed: the narrow path
    /// accumulates exactly (sixteen products below <c>2^58</c> stay inside signed 64-bit) and the wide path accumulates
    /// modulo <c>2^128</c>, which is itself order-independent — and harmless, because a wrapped blade sum differs from
    /// the true one by a multiple of <c>2^128</c>, which the rounding shift of 16 turns into a multiple of <c>2^112</c>
    /// that vanishes under the final 64-bit raw wrap without changing tie parity (the shift-at-or-below-64 argument
    /// <see cref="FixedQ4816.RoundProductSum(Int128)"/> rests on). The product as a whole is still not associative under
    /// bitwise equality, since each result blade carries its own rounding.</remarks>
    /// <exception cref="ArgumentException"><paramref name="left"/> or <paramref name="right"/> has a nonzero coefficient
    /// outside this signature's <see cref="BladeCount"/> lanes.</exception>
    public Multivector GeometricProduct(Multivector left, Multivector right) {
        ValidateOperand(value: left, paramName: nameof(left));
        ValidateOperand(value: right, paramName: nameof(right));

        return GeometricProductCore(left: left, right: right);
    }

    // The product kernel without operand validation. Callers that have already validated their inputs — or that pass
    // an intermediate produced by one of these kernels, which is lane-clean by construction — use this directly.
    private Multivector GeometricProductCore(Multivector left, Multivector right) {
        var dimension = BladeCount;
        var productSign = m_productSign;
        var result = new Multivector();

        // The null table is the intentional encoding of default(GeometricAlgebra), whose public signature is the
        // zero-generator scalar algebra. Keep its only product on the scalar hot path.
        if (productSign is null) {
            result[0] = (left[0] * right[0]);

            return result;
        }

        // A result blade receives at most one product per left blade, so it accumulates at most BladeCount ≤ 16
        // signed raw Q32 products (worst case: four non-degenerate generators, the degenerate ones annihilating
        // nothing). Sixteen products of raw operands below 2^29 stay under 16·2^58 = 2^62 < 2^63, so magnitudes
        // below the narrow limit keep every sum in signed 64-bit; larger operands accumulate at Int128 width.
        const ulong NarrowLimit = (1UL << 29);
        var combinedMagnitude = 0UL;

        for (var i = 0; (i < dimension); ++i) {
            combinedMagnitude |= FixedVectorMath.RawMagnitude(value: left[i].Value) | FixedVectorMath.RawMagnitude(value: right[i].Value);
        }

        if (combinedMagnitude < NarrowLimit) {
            // Zeroed by the allocation itself: the assembly carries no [SkipLocalsInit], so the localloc is emitted under
            // .locals init and the runtime clears it.
            Span<long> accumulator = stackalloc long[Multivector.BladeCapacity];

            for (var i = 0; (i < dimension); ++i) {
                var leftValue = left[i].Value;

                if (leftValue == 0L) { continue; }

                var row = (i * Multivector.BladeCapacity);

                for (var j = 0; (j < dimension); ++j) {
                    var sign = productSign[(row + j)];

                    if (sign == 0) { continue; }

                    var product = unchecked((leftValue * right[j].Value));
                    var blade = i ^ j;

                    accumulator[blade] = unchecked(((sign > 0)
                        ? (accumulator[blade] + product)
                        : (accumulator[blade] - product)));
                }
            }

            for (var blade = 0; (blade < dimension); ++blade) {
                result[blade] = FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: accumulator[blade]));
            }

            return result;
        }

        // Zeroed by the allocation itself, as above.
        Span<Int128> wideAccumulator = stackalloc Int128[Multivector.BladeCapacity];

        for (var i = 0; (i < dimension); ++i) {
            var leftValue = left[i].Value;

            if (leftValue == 0L) { continue; }

            var row = (i * Multivector.BladeCapacity);

            for (var j = 0; (j < dimension); ++j) {
                var sign = productSign[(row + j)];

                if (sign == 0) { continue; }

                var product = ((Int128)leftValue * right[j].Value);
                var blade = i ^ j;

                wideAccumulator[blade] = unchecked(((sign > 0)
                    ? (wideAccumulator[blade] + product)
                    : (wideAccumulator[blade] - product)));
            }
        }

        for (var blade = 0; (blade < dimension); ++blade) {
            result[blade] = FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: wideAccumulator[blade]));
        }

        return result;
    }

    /// <summary>Returns the reverse — the anti-automorphism that reverses the order of the generators in every blade.</summary>
    /// <param name="value">The multivector to reverse.</param>
    /// <returns>The multivector with each grade-<c>g</c> blade scaled by <c>(−1)^(g(g−1)/2)</c>.</returns>
    /// <remarks>The reverse of a product is the product of the reverses in the opposite order:
    /// <c>Reverse(a · b) = Reverse(b) · Reverse(a)</c>. For a unit rotor or motor it is the inverse, which is why
    /// <see cref="SandwichTransform"/> uses it as the closing factor.</remarks>
    /// <exception cref="ArgumentException"><paramref name="value"/> has a nonzero coefficient outside this signature's
    /// <see cref="BladeCount"/> lanes.</exception>
    public Multivector Reverse(Multivector value) {
        ValidateOperand(value: value, paramName: nameof(value));

        return ReverseCore(value: value);
    }

    // The reverse kernel without operand validation; see GeometricProductCore.
    private Multivector ReverseCore(Multivector value) {
        var dimension = BladeCount;
        var result = new Multivector();

        for (var i = 0; (i < dimension); ++i) {
            result[i] = ((ReverseSign(grade: BitOperations.PopCount(value: (uint)i)) > 0)
                ? value[i]
                : -value[i]);
        }

        return result;
    }

    /// <summary>Projects a multivector onto a single grade, zeroing every blade of a different grade.</summary>
    /// <param name="value">The multivector to project.</param>
    /// <param name="grade">The grade to retain, from <c>0</c> (scalar) through <see cref="GeneratorCount"/> (pseudoscalar).</param>
    /// <returns>The grade-<paramref name="grade"/> part of <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> has a nonzero coefficient outside this signature's
    /// <see cref="BladeCount"/> lanes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="grade"/> is outside the range from zero through
    /// <see cref="GeneratorCount"/>.</exception>
    public Multivector GradeProjection(Multivector value, int grade) {
        ValidateOperand(value: value, paramName: nameof(value));
        ArgumentOutOfRangeException.ThrowIfNegative(value: grade);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: grade, other: GeneratorCount);

        var dimension = BladeCount;
        var result = new Multivector();

        for (var i = 0; (i < dimension); ++i) {
            if (BitOperations.PopCount(value: (uint)i) == grade) {
                result[i] = value[i];
            }
        }

        return result;
    }

    /// <summary>Indicates whether a multivector lies in the even subalgebra — no blade of odd grade.</summary>
    /// <param name="value">The multivector to test.</param>
    /// <returns><see langword="true"/> when every odd-grade blade is zero; otherwise <see langword="false"/>.</returns>
    /// <remarks>The even subalgebra is closed under the geometric product and is where rotors and motors live: the
    /// even part of <c>(3, 0, 0)</c> is the quaternions and the even part of <c>(3, 0, 1)</c> is the dual quaternions.</remarks>
    /// <exception cref="ArgumentException"><paramref name="value"/> has a nonzero coefficient outside this signature's
    /// <see cref="BladeCount"/> lanes.</exception>
    public bool IsEven(Multivector value) {
        ValidateOperand(value: value, paramName: nameof(value));

        var dimension = BladeCount;

        for (var i = 0; (i < dimension); ++i) {
            if (((BitOperations.PopCount(value: (uint)i) & 1) != 0) && (value[i].Value != 0L)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Computes the exponential of a bivector whose square is scalar — the rotor or motor generator it produces.</summary>
    /// <param name="bivector">The generating bivector: every lane of a grade other than two must be zero, and
    /// <c>bivector · bivector</c> must be a pure scalar. That is the domain of the rotation, translation, and
    /// simple-screw generators the transform stack builds.</param>
    /// <returns>The unit element <c>exp(bivector)</c>.</returns>
    /// <remarks>The branch is chosen by the sign of the bivector square, unifying the three planar exponentials:
    /// a negative square is the circular branch <c>cos|b| + (sin|b|/|b|)·bivector</c> (the rotor, matching
    /// <see cref="FixedQuaternion.FromAxisAngle"/> for a rotation bivector); a positive square is the hyperbolic
    /// branch <c>cosh|b| + (sinh|b|/|b|)·bivector</c> (matching <see cref="FixedSplit.FromRapidity"/>); a zero square
    /// is the degenerate branch <c>1 + bivector</c> (the translator of a null bivector). The transcendentals reuse
    /// the house fixed-point <see cref="FixedQ4816.SinCos"/> and <see cref="FixedQ4816.Exp2"/> machinery.
    /// <para>The scalar square is a checked precondition, not an assumption. Both halves of it bite: a lane of grade
    /// zero, one, three, or four is not a bivector at all and the closed form would silently drop it, and a genuine
    /// grade-two element need not square to a scalar once the signature has four generators — in <c>(4, 0, 0)</c> the
    /// bivector <c>e12 + e34</c> squares to <c>−2 + 2·e1234</c>, and its exponential is the product
    /// <c>exp(e12)·exp(e34)</c>, which carries a pseudoscalar no single-branch closed form can reach. Outside the
    /// domain this refuses with <see cref="ArgumentException"/> rather than returning the branch value; the general
    /// multivector exponential is deliberately not implemented here.</para></remarks>
    /// <exception cref="ArgumentException"><paramref name="bivector"/> has a nonzero coefficient outside this
    /// signature's <see cref="BladeCount"/> lanes, carries a nonzero lane of a grade other than two, or squares to a
    /// value with a nonzero non-scalar lane.</exception>
    public Multivector Exponential(Multivector bivector) {
        ValidateOperand(value: bivector, paramName: nameof(bivector));

        var dimension = BladeCount;

        for (var blade = 0; (blade < dimension); ++blade) {
            if ((BitOperations.PopCount(value: (uint)blade) != 2) && (bivector[blade].Value != 0L)) {
                ThrowExponentialOperandNotBivector(paramName: nameof(bivector), blade: blade);
            }
        }

        var square = GeometricProductCore(left: bivector, right: bivector);

        for (var blade = 1; (blade < dimension); ++blade) {
            if (square[blade].Value != 0L) {
                ThrowExponentialSquareNotScalar(paramName: nameof(bivector), blade: blade);
            }
        }

        var squareScalar = square[0];
        var result = new Multivector();

        if (squareScalar.Value < 0L) {
            // Circular branch: |b| = sqrt(-b²), exp = cos|b| + (sin|b|/|b|)·b.
            var magnitude = FixedQ4816.Sqrt(value: -squareScalar);

            var (sin, cos) = FixedQ4816.SinCos(angle: magnitude);
            var cardinal = ((magnitude.Value == 0L)
                ? FixedQ4816.One
                : (sin / magnitude));

            result[0] = cos;
            AddScaledBivector(result: ref result, bivector: bivector, scale: cardinal);

            return result;
        }

        if (squareScalar.Value > 0L) {
            // Hyperbolic branch: |b| = sqrt(b²), exp = cosh|b| + (sinh|b|/|b|)·b.
            var magnitude = FixedQ4816.Sqrt(value: squareScalar);

            var (cosh, sinh) = FixedQ4816.CoshSinh(argument: magnitude);
            var cardinal = ((magnitude.Value == 0L)
                ? FixedQ4816.One
                : (sinh / magnitude));

            result[0] = cosh;
            AddScaledBivector(result: ref result, bivector: bivector, scale: cardinal);

            return result;
        }

        // Degenerate branch: b² = 0, exp = 1 + b (the translator of a null bivector).
        result[0] = FixedQ4816.One;
        AddScaledBivector(result: ref result, bivector: bivector, scale: FixedQ4816.One);

        return result;
    }

    /// <summary>Applies the sandwich action of a unit rotor or motor to a multivector — the transform
    /// <c>motor · vector · Reverse(motor)</c>.</summary>
    /// <param name="motor">The unit rotor or motor; a rotor of <c>(3, 0, 0)</c> rotates a vector, a motor of
    /// <c>(3, 0, 1)</c> moves a rigidly embedded point.</param>
    /// <param name="vector">The element to transform.</param>
    /// <returns>The transformed element.</returns>
    /// <remarks>For a unit argument (<c>motor · Reverse(motor) = 1</c>) this is the two-sided orthogonal-group
    /// action: <c>rotor · v · Reverse(rotor)</c> rotates a Euclidean vector by the double angle exactly as
    /// <see cref="FixedQuaternion.Rotate"/> does, and the motor sandwich reproduces
    /// <see cref="FixedRigidTransform.TransformPoint"/> on an embedded point.</remarks>
    /// <exception cref="ArgumentException"><paramref name="motor"/> or <paramref name="vector"/> has a nonzero
    /// coefficient outside this signature's <see cref="BladeCount"/> lanes.</exception>
    public Multivector SandwichTransform(Multivector motor, Multivector vector) {
        ValidateOperand(value: motor, paramName: nameof(motor));
        ValidateOperand(value: vector, paramName: nameof(vector));

        return GeometricProductCore(
            left: GeometricProductCore(left: motor, right: vector),
            right: ReverseCore(value: motor)
        );
    }

    /// <summary>Indicates whether another descriptor names the same algebra.</summary>
    /// <param name="other">The descriptor to compare against.</param>
    /// <returns><see langword="true"/> when both signatures are the same <c>(p, q, r)</c>; otherwise <see langword="false"/>.</returns>
    /// <remarks>The signature is the whole of a descriptor's identity: the blade-multiplication table is a pure
    /// function of <c>(p, q, r)</c>, computed once per <see cref="Create"/> call, so two descriptors of the same
    /// signature compute identical results and are interchangeable. Comparing the generated table array instead would
    /// be reference identity — every <see cref="Create"/> call would produce a descriptor unequal to every other, and
    /// the <see langword="default"/> descriptor (whose null table is the deliberate encoding of the zero-generator
    /// scalar algebra) would be unequal to <c>Create(0, 0, 0)</c> despite behaving as it throughout this surface.</remarks>
    public bool Equals(GeometricAlgebra other) =>
        ((PositiveCount == other.PositiveCount) &&
            (NegativeCount == other.NegativeCount) &&
            (DegenerateCount == other.DegenerateCount));

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        ((obj is GeometricAlgebra other) && Equals(other: other));

    /// <summary>Returns a hash code over the signature.</summary>
    /// <returns>A hash code consistent with <see cref="Equals(GeometricAlgebra)"/>.</returns>
    /// <remarks>Folded with <see cref="Fnv1aHash"/> — pure integer arithmetic — rather than
    /// <see cref="System.HashCode"/>, whose seed is randomized per process: a descriptor hash is therefore the same
    /// value on every run and every machine, and safe to fingerprint.</remarks>
    public override int GetHashCode() {
        var hash = Fnv1aHash.Create();

        hash.Add(value: (uint)PositiveCount);
        hash.Add(value: (uint)NegativeCount);
        hash.Add(value: (uint)DegenerateCount);

        return unchecked((int)(hash.Value ^ (hash.Value >> 32)));
    }

    /// <summary>Indicates whether two descriptors name the same algebra.</summary>
    /// <param name="left">The first descriptor.</param>
    /// <param name="right">The second descriptor.</param>
    /// <returns><see langword="true"/> when both signatures are the same; otherwise <see langword="false"/>.</returns>
    public static bool operator ==(GeometricAlgebra left, GeometricAlgebra right) =>
        left.Equals(other: right);

    /// <summary>Indicates whether two descriptors name different algebras.</summary>
    /// <param name="left">The first descriptor.</param>
    /// <param name="right">The second descriptor.</param>
    /// <returns><see langword="true"/> when the signatures differ; otherwise <see langword="false"/>.</returns>
    public static bool operator !=(GeometricAlgebra left, GeometricAlgebra right) =>
        !left.Equals(other: right);

    private void AddScaledBivector(ref Multivector result, Multivector bivector, FixedQ4816 scale) {
        var dimension = BladeCount;

        for (var i = 0; (i < dimension); ++i) {
            if (BitOperations.PopCount(value: (uint)i) == 2) {
                result[i] = (result[i] + (bivector[i] * scale));
            }
        }
    }

    // The signed reordering-plus-squares factor for one ordered blade pair. Zero when a shared degenerate generator
    // annihilates the product; otherwise ±1 from the number of adjacent transpositions to merge the two ascending
    // generator lists plus the squares of the generators they share.
    private static int BladeProductSign(int left, int right, int positiveCount, int negativeCount) {
        var swaps = 0;
        var shifted = (left >> 1);

        // Count inversions: each generator of the right blade that sits below a generator already placed from the
        // left blade costs one transposition.
        while (shifted != 0) {
            swaps += BitOperations.PopCount(value: (uint)(shifted & right));
            shifted >>= 1;
        }

        var sign = (((swaps & 1) == 0)
            ? 1
            : -1);
        var shared = left & right;

        while (shared != 0) {
            var generatorIndex = BitOperations.TrailingZeroCount(value: (uint)shared);
            var square = GeneratorSquare(
                generatorIndex: generatorIndex,
                positiveCount: positiveCount,
                negativeCount: negativeCount
            );

            if (square == 0) { return 0; }

            sign *= square;
            shared &= (shared - 1);
        }

        return sign;
    }
    private static int GeneratorSquare(int generatorIndex, int positiveCount, int negativeCount) =>
        ((generatorIndex < positiveCount)
            ? 1
            : ((generatorIndex < (positiveCount + negativeCount))
                ? -1
                : 0));
    private static int ReverseSign(int grade) =>
        (((((grade * (grade - 1)) >> 1) & 1) == 0)
            ? 1
            : -1);

    /// <summary>Validates that a fixed-capacity carrier is an element of this signature rather than a larger one.</summary>
    /// <param name="value">The multivector to validate.</param>
    /// <param name="paramName">The public parameter name to report.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> has a nonzero unused lane.</exception>
    private void ValidateOperand(in Multivector value, string paramName) {
        for (var blade = BladeCount; (blade < Multivector.BladeCapacity); ++blade) {
            if (value[blade].Value != 0L) { ThrowOperandOutsideSignature(paramName: paramName, blade: blade); }
        }
    }

    /// <summary>Throws the oversized-signature diagnosis against a public factory parameter.</summary>
    /// <param name="value">The offending count.</param>
    /// <param name="paramName">The public parameter name.</param>
    /// <exception cref="ArgumentOutOfRangeException">Always.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowSignatureTooLarge(int value, string paramName) =>
        throw new ArgumentOutOfRangeException(
            paramName: paramName,
            actualValue: value,
            message: "The geometric signature may contain at most four generators in total."
        );

    /// <summary>Throws the receiver-signature affinity diagnosis.</summary>
    /// <param name="paramName">The public parameter name.</param>
    /// <param name="blade">The first nonzero blade outside the receiver signature.</param>
    /// <exception cref="ArgumentException">Always.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOperandOutsideSignature(string paramName, int blade) =>
        throw new ArgumentException(
            message: $"Blade {blade} is outside this geometric algebra's signature; every lane at or above BladeCount must be zero.",
            paramName: paramName
        );

    /// <summary>Throws the non-bivector-operand diagnosis for the exponential.</summary>
    /// <param name="paramName">The public parameter name.</param>
    /// <param name="blade">The first nonzero blade of a grade other than two.</param>
    /// <exception cref="ArgumentException">Always.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowExponentialOperandNotBivector(string paramName, int blade) =>
        throw new ArgumentException(
            message: ($"The exponential is defined here only for a bivector, but blade {blade} is of grade " +
                $"{BitOperations.PopCount(value: (uint)blade)} and nonzero; every lane of a grade other than two must be zero."),
            paramName: paramName
        );

    /// <summary>Throws the non-scalar-square diagnosis for the exponential.</summary>
    /// <param name="paramName">The public parameter name.</param>
    /// <param name="blade">The first nonzero non-scalar blade of the operand's square.</param>
    /// <exception cref="ArgumentException">Always.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowExponentialSquareNotScalar(string paramName, int blade) =>
        throw new ArgumentException(
            message: ($"The exponential's closed form is valid only where the bivector squares to a scalar, but blade " +
                $"{blade} of the square is nonzero; the general multivector exponential is not implemented."),
            paramName: paramName
        );
}

/// <summary>
/// An element of a <see cref="GeometricAlgebra"/> — one <see cref="FixedQ4816"/> coefficient per basis blade,
/// indexed by the blade's generator subset read as a bitmask. The buffer is a fixed sixteen-lane allocation-free
/// struct sized for the four-generator maximum. A signature with fewer generators requires its unused high lanes to be
/// zero; its semantic operations reject a multivector that violates that receiver-affinity invariant.
/// </summary>
[InlineArray(length: BladeCapacity)]
public struct Multivector : IEquatable<Multivector> {
    /// <summary>The number of blade lanes, sized for the four-generator maximum (<c>2⁴</c>).</summary>
    public const int BladeCapacity = 16;

    private FixedQ4816 m_element0;

    // The blade indexer (get and set, from 0/scalar through 15/four-generator pseudoscalar) is supplied by the
    // inline-array language support rather than a declared member.

    /// <summary>Builds a multivector from a span of blade coefficients in ascending blade-index order.</summary>
    /// <param name="coefficients">The coefficients, at most <see cref="BladeCapacity"/> entries; missing high lanes are zero.</param>
    /// <returns>The multivector carrying the given coefficients.</returns>
    /// <exception cref="ArgumentException"><paramref name="coefficients"/> has more than <see cref="BladeCapacity"/> entries.</exception>
    public static Multivector FromCoefficients(ReadOnlySpan<FixedQ4816> coefficients) {
        if (coefficients.Length > BladeCapacity) {
            throw new ArgumentException(message: $"A multivector holds at most {BladeCapacity} blade coefficients.", paramName: nameof(coefficients));
        }

        var result = new Multivector();

        for (var i = 0; (i < coefficients.Length); ++i) {
            result[i] = coefficients[i];
        }

        return result;
    }

    /// <summary>Creates a pure-scalar multivector.</summary>
    /// <param name="value">The scalar (grade-zero) coefficient.</param>
    /// <returns>The multivector <c>value</c> with every higher blade zero.</returns>
    public static Multivector Scalar(FixedQ4816 value) {
        var result = new Multivector();

        result[0] = value;

        return result;
    }

    /// <summary>Returns the componentwise sum of two multivectors.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The blade-by-blade sum.</returns>
    public static Multivector operator +(Multivector left, Multivector right) {
        var result = new Multivector();

        for (var i = 0; (i < BladeCapacity); ++i) {
            result[i] = (left[i] + right[i]);
        }

        return result;
    }

    /// <summary>Returns the componentwise difference of two multivectors.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The blade-by-blade difference.</returns>
    public static Multivector operator -(Multivector left, Multivector right) {
        var result = new Multivector();

        for (var i = 0; (i < BladeCapacity); ++i) {
            result[i] = (left[i] - right[i]);
        }

        return result;
    }

    /// <summary>Indicates whether this multivector equals another blade for blade.</summary>
    /// <param name="other">The multivector to compare against.</param>
    /// <returns><see langword="true"/> when every blade coefficient is bitwise equal; otherwise <see langword="false"/>.</returns>
    public readonly bool Equals(Multivector other) {
        for (var i = 0; (i < BladeCapacity); ++i) {
            if (this[i].Value != other[i].Value) {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) =>
        ((obj is Multivector other) && Equals(other: other));

    /// <summary>Returns a hash code over every blade coefficient.</summary>
    /// <returns>A hash code consistent with <see cref="Equals(Multivector)"/>.</returns>
    /// <remarks>Folded with <see cref="Fnv1aHash"/> — pure integer arithmetic — rather than
    /// <see cref="System.HashCode"/>, whose seed is randomized per process: the same multivector hashes to the same
    /// value on every run and every machine, so the digest is safe to fingerprint.</remarks>
    public readonly override int GetHashCode() {
        var hash = Fnv1aHash.Create();

        for (var i = 0; (i < BladeCapacity); ++i) {
            hash.Add(value: this[i].Value);
        }

        return unchecked((int)(hash.Value ^ (hash.Value >> 32)));
    }
}
