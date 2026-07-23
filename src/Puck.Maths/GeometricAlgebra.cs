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
/// allocation-free struct regardless of signature. The <see cref="GeometricProduct"/> rounds each blade-pair product
/// to Q16 before accumulating (the carrier-agnostic discipline of <see cref="QuadraticAlgebra{TScalar}"/>); on
/// operands drawn from a fractional sublattice where every pairwise product is exact, this coincides bit-for-bit with
/// the fused single-rounding kernels of <see cref="FixedQuaternion"/> and <see cref="FixedComplex"/>.
/// </remarks>
public readonly struct GeometricAlgebra {
    // round(log2(e) · 2^16): converts a natural argument into the base-2 argument Exp2 consumes (mirrors FixedSplit).
    private static readonly FixedQ4816 Log2E = FixedQ4816.FromRawBits(value: 94548L);
    private static readonly FixedQ4816 Half = FixedQ4816.FromRawBits(value: 32768L);

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
    public int GeneratorCount => (PositiveCount + NegativeCount + DegenerateCount);
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

        var generatorCount = (positiveCount + negativeCount + degenerateCount);

        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: generatorCount, other: 4);

        var dimension = (1 << generatorCount);
        var productSign = new sbyte[Multivector.BladeCapacity * Multivector.BladeCapacity];

        for (var left = 0; (left < dimension); ++left) {
            for (var right = 0; (right < dimension); ++right) {
                productSign[(left * Multivector.BladeCapacity) + right] = (sbyte)BladeProductSign(
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
    /// <returns>The geometric product <c>left · right</c>, each blade-pair product rounded once to Q16 before it is
    /// accumulated into its result blade.</returns>
    /// <remarks>The order of the accumulation is fixed (ascending blade index) and must not be reassociated: rounded
    /// fixed-point multiplication is not associative under bitwise equality.</remarks>
    public Multivector GeometricProduct(Multivector left, Multivector right) {
        var dimension = BladeCount;
        var result = new Multivector();

        for (var i = 0; (i < dimension); ++i) {
            var leftCoefficient = left[i];

            if (leftCoefficient.Value == 0L) { continue; }

            var row = (i * Multivector.BladeCapacity);

            for (var j = 0; (j < dimension); ++j) {
                var sign = m_productSign[row + j];

                if (sign == 0) { continue; }

                var term = (leftCoefficient * right[j]);
                var blade = (i ^ j);

                result[blade] = ((sign > 0)
                    ? (result[blade] + term)
                    : (result[blade] - term));
            }
        }

        return result;
    }

    /// <summary>Returns the reverse — the anti-automorphism that reverses the order of the generators in every blade.</summary>
    /// <param name="value">The multivector to reverse.</param>
    /// <returns>The multivector with each grade-<c>g</c> blade scaled by <c>(−1)^(g(g−1)/2)</c>.</returns>
    /// <remarks>The reverse of a product is the product of the reverses in the opposite order:
    /// <c>Reverse(a · b) = Reverse(b) · Reverse(a)</c>. For a unit rotor or motor it is the inverse, which is why
    /// <see cref="SandwichTransform"/> uses it as the closing factor.</remarks>
    public Multivector Reverse(Multivector value) {
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
    public Multivector GradeProjection(Multivector value, int grade) {
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
    public bool IsEven(Multivector value) {
        var dimension = BladeCount;

        for (var i = 0; (i < dimension); ++i) {
            if (((BitOperations.PopCount(value: (uint)i) & 1) != 0) && (value[i].Value != 0L)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Computes the exponential of a bivector whose square is scalar — the rotor or motor generator it produces.</summary>
    /// <param name="bivector">The generating bivector. Its square is taken from the scalar part of
    /// <c>bivector · bivector</c>, which is the whole square for a 2-blade and for the rotation, translation, and
    /// simple-screw generators the transform stack builds.</param>
    /// <returns>The unit element <c>exp(bivector)</c>.</returns>
    /// <remarks>The branch is chosen by the sign of the bivector square, unifying the three planar exponentials:
    /// a negative square is the circular branch <c>cos|b| + (sin|b|/|b|)·bivector</c> (the rotor, matching
    /// <see cref="FixedQuaternion.FromAxisAngle"/> for a rotation bivector); a positive square is the hyperbolic
    /// branch <c>cosh|b| + (sinh|b|/|b|)·bivector</c> (matching <see cref="FixedSplit.FromRapidity"/>); a zero square
    /// is the degenerate branch <c>1 + bivector</c> (the translator of a null bivector). The transcendentals reuse
    /// the house fixed-point <see cref="FixedQ4816.SinCos"/> and <see cref="FixedQ4816.Exp2"/> machinery.</remarks>
    public Multivector Exponential(Multivector bivector) {
        var squareScalar = GeometricProduct(left: bivector, right: bivector)[0];
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
            var scaled = (magnitude * Log2E);
            var forward = FixedQ4816.Exp2(value: scaled);
            var backward = FixedQ4816.Exp2(value: -scaled);
            var cosh = ((forward + backward) * Half);
            var sinh = ((forward - backward) * Half);
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
    public Multivector SandwichTransform(Multivector motor, Multivector vector) =>
        GeometricProduct(
            left: GeometricProduct(left: motor, right: vector),
            right: Reverse(value: motor)
        );

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
        var shared = (left & right);

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
        ((((grade * (grade - 1)) >> 1) & 1) == 0)
            ? 1
            : -1;
}

/// <summary>
/// An element of a <see cref="GeometricAlgebra"/> — one <see cref="FixedQ4816"/> coefficient per basis blade,
/// indexed by the blade's generator subset read as a bitmask. The buffer is a fixed sixteen-lane allocation-free
/// struct sized for the four-generator maximum; a signature with fewer generators leaves the unused high lanes zero.
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

    /// <inheritdoc/>
    public readonly override int GetHashCode() {
        var hash = new HashCode();

        for (var i = 0; (i < BladeCapacity); ++i) {
            hash.Add(value: this[i].Value);
        }

        return hash.ToHashCode();
    }
}
