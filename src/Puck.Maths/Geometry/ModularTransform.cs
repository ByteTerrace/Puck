using System.Numerics;
using System.Text;

namespace Puck.Maths;

/// <summary>The conjugacy class of a <see cref="ModularTransform"/>, read from the absolute trace: the three kinds of motion the modular group performs on the hyperbolic plane.</summary>
public enum ModularClass {
    /// <summary>A rotation about an interior fixed point — absolute trace below <c>2</c>. Finite order in the projective group.</summary>
    Elliptic,
    /// <summary>A shear fixing a single cusp on the boundary — absolute trace exactly <c>2</c>. The tick/translation motion.</summary>
    Parabolic,
    /// <summary>A translation along an axis geodesic between two boundary fixed points — absolute trace above <c>2</c>. The scaling/inflation motion.</summary>
    Hyperbolic,
}
/// <summary>The result of <see cref="ModularTransform.GaussReduce(long, long, long)"/>: the reduced positive-definite form and the exact transform that carries the original point to it.</summary>
/// <param name="Transform">The word in <see cref="ModularTransform.S"/> and <see cref="ModularTransform.T"/> that maps the original point to the fundamental-domain point; it factors uniquely into those two generators.</param>
/// <param name="A">The leading coefficient of the reduced form, satisfying <c>-A &lt; B ≤ A ≤ C</c>.</param>
/// <param name="B">The middle coefficient of the reduced form.</param>
/// <param name="C">The trailing coefficient of the reduced form.</param>
public readonly record struct GaussReduction(ModularTransform Transform, long A, long B, long C);
/// <summary>
/// An exact element of the modular group — a <c>2×2</c> integer matrix <c>[[A, B], [C, D]]</c> of determinant one — that
/// acts on the hyperbolic plane by the Möbius map <c>z ↦ (A·z + B) / (C·z + D)</c>. This is the single object beneath the
/// library's three motions: the elliptic rotations (the sixth root of unity of <see cref="HexagonalCoordinate"/>), the parabolic
/// tick shear (the kinematics update of <see cref="LayerSequence"/>), and the hyperbolic golden inflation (the step of
/// <see cref="MetallicQuasicrystal"/>) are the three conjugacy classes <see cref="Classify"/> distinguishes by trace.
/// </summary>
/// <remarks>
/// The determinant one invariant makes the inverse the adjugate <c>[[D, −B], [−C, A]]</c> — exact, with no division —
/// and multiplication composes maps (matrix product), so the group law is pure integer arithmetic. <see cref="Apply(long, long)"/>
/// moves a cusp (a rational <c>p/q</c>, with <c>∞</c> written <c>1/0</c>) to another cusp, reduced to lowest terms. The one
/// approximate seam is <see cref="Apply(FixedComplex)"/>, which realizes the Möbius map on a fixed-point interior point:
/// the combinatorial action on cusps and forms above it is exact, only the interior evaluation rounds — the same seam
/// convention as <see cref="MetallicQuasicrystal.Position(int, long, long)"/>. <see cref="GaussReduce(long, long, long)"/> reduces a
/// positive-definite integer form — a point of the upper half-plane addressed by its integer minimal polynomial — into the
/// fundamental domain, returning the exact word that takes it there.
/// The zero-initialized value is <see cref="Identity"/>, so <see langword="default"/> is a valid group element.
/// </remarks>
public readonly record struct ModularTransform
    : IMultiplyOperators<ModularTransform, ModularTransform, ModularTransform>,
      IMultiplicativeIdentity<ModularTransform, ModularTransform> {
    // Encode the diagonal through a bijection that maps logical one to stored zero. A zero-initialized struct therefore
    // represents Identity rather than the invalid zero matrix, while every long entry remains representable.
    private readonly long m_encodedA;
    private readonly long m_encodedD;

    private ModularTransform(long a, long b, long c, long d) {
        m_encodedA = a ^ 1L;
        B = b;
        C = c;
        m_encodedD = d ^ 1L;
    }

    /// <summary>Gets the top-left entry.</summary>
    public long A =>
        m_encodedA ^ 1L;
    /// <summary>Gets the top-right entry.</summary>
    public long B { get; }
    /// <summary>Gets the bottom-left entry.</summary>
    public long C { get; }
    /// <summary>Gets the bottom-right entry.</summary>
    public long D =>
        m_encodedD ^ 1L;
    /// <summary>Gets the identity transform <c>[[1, 0], [0, 1]]</c> — the map that fixes every point.</summary>
    public static ModularTransform Identity =>
        new(
            a: 1L,
            b: 0L,
            c: 0L,
            d: 1L
        );
    /// <summary>Gets the inverse transform — the adjugate <c>[[D, −B], [−C, A]]</c>, which is the inverse exactly because the determinant is one.</summary>
    /// <exception cref="OverflowException">An off-diagonal entry is <see cref="long.MinValue"/> and its negation is not representable.</exception>
    public ModularTransform Inverse =>
        new(
            a: D,
            b: checked(-B),
            c: checked(-C),
            d: A
        );
    /// <summary>Gets the multiplicative identity, the identity transform.</summary>
    public static ModularTransform MultiplicativeIdentity =>
        Identity;
    /// <summary>Gets the inversion <c>S = [[0, −1], [1, 0]]</c> — the map <c>z ↦ −1/z</c>; elliptic of order four (order two in the projective group).</summary>
    public static ModularTransform S =>
        new(
            a: 0L,
            b: -1L,
            c: 1L,
            d: 0L
        );
    /// <summary>Gets the translation <c>T = [[1, 1], [0, 1]]</c> — the map <c>z ↦ z + 1</c>; the parabolic generator that fixes the cusp <c>∞</c>.</summary>
    public static ModularTransform T =>
        new(
            a: 1L,
            b: 1L,
            c: 0L,
            d: 1L
        );
    /// <summary>Gets the trace <c>A + D</c> — the conjugacy invariant that <see cref="Classify"/> reads.</summary>
    /// <exception cref="OverflowException">The sum of the diagonal exceeds <see cref="long"/>.</exception>
    public long Trace =>
        checked((A + D));

    private static FixedComplex FromInteger(long value) =>
        new(
            Real: FixedQ4816.FromInteger(value: value),
            Imaginary: FixedQ4816.Zero
        );
    private ModularTransform LeftInvert() =>
        // S · this = [[−C, −D], [A, B]] — the inversion applied on the left, skipping the general product's zero and one multiplies.
        new(
            a: checked(-C),
            b: checked(-D),
            c: A,
            d: B
        );
    private ModularTransform LeftTranslate(long amount) =>
        // Tᵃᵐᵒᵘⁿᵗ · this = [[A + amount·C, B + amount·D], [C, D]] — the translation applied on the left, two products instead of four.
        new(
            a: MultiplyAdd(
                left: amount,
                right: C,
                otherLeft: 1L,
                otherRight: A
            ),
            b: MultiplyAdd(
                left: amount,
                right: D,
                otherLeft: 1L,
                otherRight: B
            ),
            c: C,
            d: D
        );
    private static long MultiplyAdd(long left, long right, long otherLeft, long otherRight) =>
        checked((long)(checked(((((Int128)left) * right) + (((Int128)otherLeft) * otherRight)))));
    // The image coordinates are each a sum of two products of longs, so they fit Int128 exactly and never reach the
    // extreme that would make Int128.Abs throw; the reduction runs entirely on machine-width words.
    private static (long Numerator, long Denominator) NormalizeCusp(Int128 numerator, Int128 denominator) {
        if (Int128.Zero == denominator) {
            // Every ∞ collapses to the canonical (1, 0), regardless of the numerator's sign.
            return (1L, 0L);
        }

        if (Int128.Zero == numerator) {
            return (0L, 1L);
        }

        var divisor = Int128.Abs(value: numerator).GreatestCommonDivisor(other: Int128.Abs(value: denominator));

        numerator /= divisor;
        denominator /= divisor;

        if (Int128.Zero > denominator) {
            numerator = -numerator;
            denominator = -denominator;
        }

        return (
            Numerator: checked((long)numerator),
            Denominator: checked((long)denominator)
        );
    }
    private bool PrintMembers(StringBuilder builder) {
        builder.Append(value: "A = ");
        builder.Append(value: A);
        builder.Append(value: ", B = ");
        builder.Append(value: B);
        builder.Append(value: ", C = ");
        builder.Append(value: C);
        builder.Append(value: ", D = ");
        builder.Append(value: D);

        return true;
    }

    /// <summary>Applies the Möbius map to a cusp — a rational <c>numerator / denominator</c>, with <c>∞</c> written as <c>1 / 0</c>.</summary>
    /// <param name="numerator">The numerator of the cusp.</param>
    /// <param name="denominator">The denominator of the cusp; zero denotes the cusp <c>∞</c>.</param>
    /// <returns>The image cusp in lowest terms, with a non-negative denominator; <c>∞</c> is returned as <c>(1, 0)</c> and zero as <c>(0, 1)</c>.</returns>
    /// <exception cref="OverflowException">A coordinate of the reduced image cusp exceeds <see cref="long"/>.</exception>
    public (long Numerator, long Denominator) Apply(long numerator, long denominator) =>
        NormalizeCusp(
            numerator: ((((Int128)A) * numerator) + (((Int128)B) * denominator)),
            denominator: ((((Int128)C) * numerator) + (((Int128)D) * denominator))
        );
    /// <summary>Applies the Möbius map to an interior point of the upper half-plane.</summary>
    /// <param name="point">The fixed-point interior point.</param>
    /// <returns>The image <c>(A·point + B) / (C·point + D)</c>. This is the one approximate operation: the integer action on cusps and forms is exact, but this interior evaluation rounds each complex product and the final division, like <see cref="MetallicQuasicrystal.Position(int, long, long)"/>.</returns>
    public FixedComplex Apply(FixedComplex point) {
        var numerator = ((FromInteger(value: A) * point) + FromInteger(value: B));
        var denominator = ((FromInteger(value: C) * point) + FromInteger(value: D));

        return (numerator / denominator);
    }
    /// <summary>Classifies the transform by its motion on the hyperbolic plane.</summary>
    /// <returns><see cref="ModularClass.Elliptic"/> when the absolute trace is below two, <see cref="ModularClass.Parabolic"/> when it equals two, and <see cref="ModularClass.Hyperbolic"/> when it exceeds two.</returns>
    public ModularClass Classify() {
        var trace = Int128.Abs(value: (((Int128)A) + D));

        return ((trace < 2)
            ? ModularClass.Elliptic
            : ((trace == 2)
                ? ModularClass.Parabolic
                : ModularClass.Hyperbolic
        ));
    }
    /// <summary>Creates a validated transform from its four entries.</summary>
    /// <param name="a">The top-left entry.</param>
    /// <param name="b">The top-right entry.</param>
    /// <param name="c">The bottom-left entry.</param>
    /// <param name="d">The bottom-right entry.</param>
    /// <returns>The transform <c>[[a, b], [c, d]]</c>.</returns>
    /// <exception cref="ArgumentException">The determinant <c>a·d − b·c</c> is not one.</exception>
    public static ModularTransform Create(long a, long b, long c, long d) {
        if (Int128.One != ((((Int128)a) * d) - (((Int128)b) * c))) {
            throw new ArgumentException(message: "a modular transform must have determinant one (a·d − b·c = 1)");
        }

        return new ModularTransform(
            a: a,
            b: b,
            c: c,
            d: d
        );
    }
    /// <summary>Reduces a positive-definite integer binary quadratic form into the fundamental domain and returns the exact transform that does it.</summary>
    /// <param name="a">The leading coefficient; it must be positive.</param>
    /// <param name="b">The middle coefficient.</param>
    /// <param name="c">The trailing coefficient.</param>
    /// <returns>The reduced form <c>-A &lt; B ≤ A ≤ C</c> and the word in <see cref="S"/> and <see cref="T"/> that carries the form's root — the upper half-plane point <c>(−b + √(b² − 4ac)) / (2a)</c> — into the fundamental domain <c>|Re z| ≤ ½</c>, <c>|z| ≥ 1</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="a"/> is not positive, or the discriminant <c>b² − 4ac</c> is not negative (the form is not positive-definite, so its root is not an interior point).</exception>
    /// <exception cref="OverflowException">A reduction intermediate exceeds <see cref="long"/>.</exception>
    /// <remarks>
    /// The two reduction moves are the group generators acting on the form: <c>S</c> sends <c>(a, b, c)</c> to <c>(c, −b, a)</c>
    /// (the root <c>z</c> to <c>−1/z</c>), and <c>Tᵐ</c> sends it to <c>(a, b − 2am, am² − bm + c)</c> (the root to <c>z + m</c>).
    /// Each pass first picks the <c>m</c> that lands <c>b</c> in <c>(−a, a]</c>, then applies <c>S</c> only when <c>c &lt; a</c>.
    /// <strong>Termination is exact and finite:</strong> every <c>S</c> replaces the positive integer leading coefficient <c>a</c> with a
    /// strictly smaller positive integer <c>c</c>, and a positive integer cannot strictly decrease forever — so the number of
    /// <c>S</c> steps is bounded, and one translation sits between consecutive <c>S</c> steps. When no <c>S</c> applies the form
    /// is reduced, which is exactly the statement that its root lies in the fundamental domain.
    /// <para>The positive-definiteness test is exact over the entire <see cref="long"/> domain. The discriminant itself needs
    /// 129 signed bits at the carrier's corners, so it is never formed: with <paramref name="a"/> positive the form is definite
    /// exactly when <paramref name="c"/> is positive and <c>b²</c> is strictly below <c>4ac</c>, a comparison of two unsigned
    /// magnitudes that both fit. Accepting a form is not a promise that its reduction fits: a definite form whose discriminant
    /// is wide enough can still overflow a reduction intermediate, which throws.</para>
    /// </remarks>
    public static GaussReduction GaussReduce(long a, long b, long c) {
        if (0L >= a) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(a),
                message: "the leading coefficient must be positive for a positive-definite form"
            );
        }

        // b² − 4ac needs 129 signed bits at the carrier's corners — (long.MaxValue, 0, long.MinValue) alone exceeds
        // Int128 — so the sign is decided without ever forming the difference. With a already positive, the form is
        // positive-definite exactly when c is positive and b² is strictly below 4ac, and each side of that comparison
        // fits unsigned: b² is at most 2¹²⁶ and 4ac at most 2¹²⁸ − 2⁶⁶ + 4. Nothing here allocates.
        if (
            (0L >= c) ||
            (((UInt128)(((Int128)b) * b)) >= ((((UInt128)((ulong)a)) * ((ulong)c)) << 2))
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(b),
                message: "the discriminant b² − 4ac must be negative (the form must be positive-definite)"
            );
        }

        var transform = Identity;
        var formA = a;
        var formB = b;
        var formC = c;

        while (true) {
            // Translate the root so the middle coefficient lands in (−A, A]: this is |Re z| ≤ ½. When B is already there the
            // step is a no-op, so the common already-normalized pass (which every S step produces) skips the wide division.
            if (
                (formB <= -formA) ||
                (formB > formA)
            ) {
                var twiceA = (2L * ((Int128)formA));
                var shift = (((Int128)formB) + formA).FloorDivide(divisor: twiceA);
                var nextB = (((Int128)formB) - (twiceA * shift));

                if (nextB == -formA) {
                    nextB = formA;
                    shift -= Int128.One;
                }

                formC = checked((long)((((((Int128)formA) * shift) * shift) - (((Int128)formB) * shift)) + formC));
                formB = checked((long)nextB);
                transform = transform.LeftTranslate(amount: checked((long)shift));
            }

            // Reduced once the trailing coefficient is no smaller than the leading one: this is |z| ≥ 1.
            if (formC < formA) {
                (formA, formB, formC) = (formC, checked(-formB), formA);
                transform = transform.LeftInvert();
            } else if (
                (formC == formA) &&
                (0L > formB)
            ) {
                formB = checked(-formB);
                transform = transform.LeftInvert();

                break;
            } else {
                break;
            }
        }

        return new GaussReduction(
            A: formA,
            B: formB,
            C: formC,
            Transform: transform
        );
    }

    /// <summary>Composes two transforms: the result applies <paramref name="right"/> first, then <paramref name="left"/>.</summary>
    /// <param name="left">The outer map.</param>
    /// <param name="right">The inner map, applied first.</param>
    /// <returns>The matrix product <c>left · right</c>, itself of determinant one.</returns>
    /// <exception cref="OverflowException">An entry of the product exceeds <see cref="long"/>.</exception>
    public static ModularTransform operator *(ModularTransform left, ModularTransform right) =>
        new(
            a: MultiplyAdd(
                left: left.A,
                right: right.A,
                otherLeft: left.B,
                otherRight: right.C
            ),
            b: MultiplyAdd(
                left: left.A,
                right: right.B,
                otherLeft: left.B,
                otherRight: right.D
            ),
            c: MultiplyAdd(
                left: left.C,
                right: right.A,
                otherLeft: left.D,
                otherRight: right.C
            ),
            d: MultiplyAdd(
                left: left.C,
                right: right.B,
                otherLeft: left.D,
                otherRight: right.D
            )
        );
}
