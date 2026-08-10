using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// The unifying two-dimensional number system: the ring obtained by adjoining a root <c>x</c> of
/// <c>x² = P·x + Q</c> to a carrier ring <typeparamref name="TScalar"/>. Every planar number system in this library is
/// one instance of this object — the pair <c>(P, Q)</c> is the whole of the choice, and the discriminant
/// <c>Δ = P² + 4Q</c> classifies the world it names: <c>Δ &lt; 0</c> gives rotation (the complex numbers, e.g.
/// <c>(0, −1)</c>), <c>Δ = 0</c> gives shear (the dual numbers, <c>(0, 0)</c>), and <c>Δ &gt; 0</c> gives scaling (the
/// split-complex numbers, <c>(0, 1)</c>). Over a finite field the same <c>Δ</c> decides whether the modulus stays inert,
/// ramifies, or splits.
/// </summary>
/// <typeparam name="TScalar">
/// The carrier ring. The constraint set is exactly the one <see cref="FixedDual{TValue}"/> uses — the minimal group of
/// generic-math operator and identity interfaces that expresses the ring operations while admitting both the house
/// fixed-point type <see cref="FixedQ4816"/> and <see cref="System.Numerics.BigInteger"/>. No ordering, no division, and
/// no formatting are required, so exact carriers (integers, rationals) and rounded carriers (fixed point) compose alike;
/// the descriptor never assumes the arithmetic is associative under bitwise equality, so a rounded carrier is
/// well-defined here.
/// </typeparam>
/// <remarks>The defining relation's coefficient degeneracies are classified once at construction — whether <c>P</c> or
/// <c>Q</c> is the carrier's additive identity — and the general arithmetic paths skip the products that a degenerate
/// coefficient renders dead (the dual relation <c>(0, 0)</c> drops both root-product terms entirely). Skipping is
/// bit-identical rather than merely algebraically equal because every carrier in this library satisfies, bitwise, the
/// two ring facts the elision relies on: <c>AdditiveIdentity · x</c> is the additive identity, and <c>x + AdditiveIdentity</c>
/// is <c>x</c>. This is a carrier contract, not an algebra theorem — even a rounded fixed-point carrier honors it, because
/// a zero raw operand yields a zero raw product and the exact addition of a zero raw value changes no bits.</remarks>
public readonly record struct QuadraticAlgebra<TScalar>
    where TScalar : IAdditionOperators<TScalar, TScalar, TScalar>,
                    ISubtractionOperators<TScalar, TScalar, TScalar>,
                    IMultiplyOperators<TScalar, TScalar, TScalar>,
                    IUnaryNegationOperators<TScalar, TScalar>,
                    IAdditiveIdentity<TScalar, TScalar>,
                    IMultiplicativeIdentity<TScalar, TScalar> {
    /// <summary>An element <c>U + V·x</c> of the algebra, with both parts in the carrier ring.</summary>
    /// <param name="U">The scalar part.</param>
    /// <param name="V">The coefficient of the adjoined root <c>x</c>.</param>
    public readonly record struct Element(TScalar U, TScalar V);

    /// <summary>A projective pair <c>(numerator : denominator)</c> standing for the ratio <c>numerator / denominator</c>
    /// without dividing — the state a continued-fraction convergent or a <see cref="MobiusStep"/> iterate carries.</summary>
    /// <param name="Numerator">The numerator.</param>
    /// <param name="Denominator">The denominator.</param>
    public readonly record struct Projective(TScalar Numerator, TScalar Denominator);

    // Derived at construction from (P, Q): true exactly when the carrier is FixedQ4816 AND both coefficients are exact
    // integers (raw an exact multiple of 2^16, any magnitude the carrier can hold). Over FixedQ4816 the fused
    // widen-accumulate-round-once discipline is UNCONDITIONAL — every algebra rounds each returned component exactly
    // once — so this flag never gates whether fusion runs; it only selects WHICH fused lane runs. An integer relation
    // enters the sums as a plain integer multiplier, letting the existing integer kernels (and their narrow long fast
    // paths) serve it, and it additionally makes MobiusStep exact; a fractional relation enters the fractional lane,
    // which accumulates the whole component expression at Q48 in Int128 and rounds once. Both lanes are value-
    // independent per algebra and implement the SAME rational semantics, so they agree bit-for-bit where they overlap.
    // The flag is kept explicit so the lane selection is a single field read on the hot path rather than a repeated
    // typeof/shape test; it is excluded from equality, which compares (P, Q) alone (see Equals).
    private readonly bool mIntegerCoefficients;
    // Degeneracy hints derived from (P, Q) at construction: true when the coefficient is the carrier's additive
    // identity, which makes its root-product term provably dead in the general paths. These are fast-path hints only —
    // a false value merely takes the full expression — so default(QuadraticAlgebra<TScalar>), whose bools are false
    // while P and Q are the (degenerate) default, stays correct: it simply computes the dead terms instead of skipping
    // them. They are excluded from equality alongside the lane flag.
    private readonly bool mPIsAdditiveIdentity;
    private readonly bool mQIsAdditiveIdentity;

    /// <summary>Initializes the algebra <c>TScalar[x] / (x² − P·x − Q)</c>, classifying the coefficient degeneracies once.</summary>
    /// <param name="p">The linear coefficient of the defining relation <c>x² = P·x + Q</c>; the companion trace.</param>
    /// <param name="q">The constant coefficient of the defining relation; the negated companion determinant.</param>
    public QuadraticAlgebra(TScalar p, TScalar q) {
        P = p;
        Q = q;
        // The coefficient shape is classified once, never caller-chosen: an integer-coefficient relation over the house
        // scalar routes to the integer lane (exact-integer multipliers, existing kernels, exact MobiusStep), any other
        // FixedQ4816 relation to the fractional lane. Both honor the one-rounding discipline; this only picks the lane.
        mIntegerCoefficients = HasIntegerCoefficients(p: p, q: q);
        // EqualityComparer<TScalar>.Default devirtualizes for value-type carriers and is evaluated in the cold
        // constructor; it adds no generic constraint beyond what the ring operators already require.
        mPIsAdditiveIdentity = EqualityComparer<TScalar>.Default.Equals(x: p, y: TScalar.AdditiveIdentity);
        mQIsAdditiveIdentity = EqualityComparer<TScalar>.Default.Equals(x: q, y: TScalar.AdditiveIdentity);
    }

    /// <summary>Gets the linear coefficient of the defining relation <c>x² = P·x + Q</c>; the companion trace.</summary>
    public TScalar P { get; }
    /// <summary>Gets the constant coefficient of the defining relation; the negated companion determinant.</summary>
    public TScalar Q { get; }

    /// <summary>Creates the algebra <c>TScalar[x] / (x² − P·x − Q)</c>.</summary>
    /// <param name="p">The linear coefficient of the defining relation.</param>
    /// <param name="q">The constant coefficient of the defining relation.</param>
    /// <returns>The described algebra.</returns>
    /// <remarks>Every <c>(p, q)</c> defines a valid unital rank-two algebra over the carrier, so nothing is rejected; the
    /// factory exists to mirror the validated construction of the sibling structures and to give the discriminant a home.
    /// Whether the result is a field, a product of two lines, or has nilpotents is read from <see cref="Discriminant"/>
    /// rather than enforced here.
    /// <para>Over the house scalar <see cref="FixedQ4816"/> the fused one-rounding discipline is unconditional — every
    /// algebra, whatever its coefficients, has <see cref="Multiply"/>, <see cref="Norm"/>, and <see cref="MobiusStep"/>
    /// return each component as one ties-to-even rounding of the exact rational value of the ideal expression, wrapped to
    /// the raw carrier. The coefficient shape only selects the lane: an integer relation enters the sums as a plain
    /// integer multiplier (so <c>Create(0, −1)</c>, <c>Create(0, +1)</c>, and <c>Create(0, 0)</c> reproduce
    /// <see cref="FixedComplex"/>, <see cref="FixedSplit"/>, and <see cref="FixedDual{TValue}"/> bit-for-bit over the full
    /// raw range, and <see cref="MobiusStep"/> is additionally exact), while a fractional relation accumulates the whole
    /// component expression at Q48 and rounds once. Every other carrier keeps the generic per-product path, which rounds
    /// each carrier product before adding — the only discipline a carrier that cannot express raw fusion can offer.</para></remarks>
    public static QuadraticAlgebra<TScalar> Create(TScalar p, TScalar q) =>
        new(p: p, q: q);

    // The integer lane: the FixedQ4816 carrier with both coefficients exact integers, so they enter the widened sums
    // as plain integer multipliers. No magnitude cap — the lane math is congruent mod 2^128 at any magnitude the
    // carrier can hold, and the narrow long fast paths gate themselves by operand value. The typeof comparison folds
    // to a JIT-time constant for every closed value-type instantiation, so non-FixedQ4816 carriers never reach the raw
    // casts; a false result there simply routes to the generic per-product path, never to the fractional lane (which
    // is unreachable off FixedQ4816).
    private static bool HasIntegerCoefficients(TScalar p, TScalar q) {
        if (typeof(TScalar) != typeof(FixedQ4816)) {
            return false;
        }

        var pRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: p).Value;
        var qRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: q).Value;

        return (FixedQ4816.IsExactInteger(raw: pRaw) && FixedQ4816.IsExactInteger(raw: qRaw));
    }

    /// <summary>Gets the discriminant <c>Δ = P² + 4Q</c>, whose character classifies the algebra.</summary>
    /// <remarks>A negative value names a rotation algebra (the complex numbers), zero a shear algebra (the dual numbers),
    /// and a positive value a scaling algebra (the split-complex numbers). Over a finite field the quadratic character of
    /// this value decides inert versus split; a zero value marks the ramified case.</remarks>
    public TScalar Discriminant =>
        (((P * P) + (Q + Q)) + (Q + Q));
    /// <summary>Gets the multiplicative identity, <c>1 + 0·x</c>.</summary>
    public Element One => new(
        U: TScalar.MultiplicativeIdentity,
        V: TScalar.AdditiveIdentity
    );
    /// <summary>Gets the adjoined root itself, the element <c>0 + 1·x</c> — the generator whose powers drive the companion sequences.</summary>
    public Element Root => new(
        U: TScalar.AdditiveIdentity,
        V: TScalar.MultiplicativeIdentity
    );
    /// <summary>Gets the additive identity, <c>0 + 0·x</c>.</summary>
    public Element Zero => new(
        U: TScalar.AdditiveIdentity,
        V: TScalar.AdditiveIdentity
    );

    /// <summary>Adds two elements.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The componentwise sum.</returns>
    public Element Add(Element left, Element right) =>
        new(
        U: (left.U + right.U),
        V: (left.V + right.V)
    );
    /// <summary>Returns the conjugate — the image under the non-trivial algebra involution.</summary>
    /// <param name="value">The element to conjugate.</param>
    /// <returns>The element <c>(U + P·V) − V·x</c>, the second root of the shared minimal polynomial.</returns>
    /// <remarks>Over <c>(0, −1)</c> this is the complex conjugate <c>U − V·x</c>; the extra <c>P·V</c> term is the
    /// companion trace acting when the basis root is not trace-free.</remarks>
    public Element Conjugate(Element value) =>
        new(
        U: (mPIsAdditiveIdentity ? value.U : (value.U + (P * value.V))),
        V: -value.V
    );
    /// <summary>Multiplies two elements, folding <c>x²</c> back through the defining relation.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The product <c>(u₁u₂ + Q·v₁v₂) + (u₁v₂ + u₂v₁ + P·v₁v₂)·x</c>.</returns>
    public Element Multiply(Element left, Element right) {
        // The typeof comparison folds to a JIT-time constant for every closed value-type instantiation, so over
        // FixedQ4816 every algebra takes a fused lane and non-FixedQ4816 carriers never see the raw casts.
        if (typeof(TScalar) == typeof(FixedQ4816)) {
            return (mIntegerCoefficients
                ? MultiplyFusedInteger(left: left, right: right)
                : MultiplyFusedFractional(left: left, right: right));
        }

        var scalarProduct = (left.U * right.U);
        var cross = ((left.U * right.V) + (left.V * right.U));

        // Dual relation (0, 0): x² folds to the additive identity, so the root product re-enters neither component and is
        // never formed. A single combined test keeps this off the critical path before the multiply happens.
        if (mPIsAdditiveIdentity && mQIsAdditiveIdentity) {
            return new(
                U: scalarProduct,
                V: cross
            );
        }

        var rootProduct = (left.V * right.V);

        return new(
            U: (mQIsAdditiveIdentity ? scalarProduct : (scalarProduct + (Q * rootProduct))),
            V: (mPIsAdditiveIdentity ? cross : (cross + (P * rootProduct)))
        );
    }
    /// <summary>Negates an element.</summary>
    /// <param name="value">The element to negate.</param>
    /// <returns>The componentwise negation.</returns>
    public Element Negate(Element value) =>
        new(
        U: -value.U,
        V: -value.V
    );
    /// <summary>Computes the algebra norm, the product of an element with its <see cref="Conjugate"/>.</summary>
    /// <param name="value">The element whose norm is taken.</param>
    /// <returns>The scalar <c>U² + P·U·V − Q·V²</c>.</returns>
    /// <remarks>This is the determinant of multiplication-by-<paramref name="value"/>; an element is a unit exactly when
    /// its norm is one of the carrier's units, and the zero divisors are precisely the norm-zero elements.</remarks>
    public TScalar Norm(Element value) {
        if (typeof(TScalar) == typeof(FixedQ4816)) {
            return (mIntegerCoefficients
                ? NormFusedInteger(value: value)
                : NormFusedFractional(value: value));
        }

        var norm = (value.U * value.U);

        if (!mPIsAdditiveIdentity) { norm = (norm + ((P * value.U) * value.V)); }
        if (!mQIsAdditiveIdentity) { norm = (norm - ((Q * value.V) * value.V)); }

        return norm;
    }
    /// <summary>Computes the algebra trace, the sum of an element with its <see cref="Conjugate"/>.</summary>
    /// <param name="value">The element whose trace is taken.</param>
    /// <returns>The scalar <c>2U + P·V</c>.</returns>
    public TScalar Trace(Element value) =>
        (mPIsAdditiveIdentity ? (value.U + value.U) : ((value.U + value.U) + (P * value.V)));
    /// <summary>Subtracts one element from another.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The componentwise difference.</returns>
    public Element Subtract(Element left, Element right) =>
        new(
        U: (left.U - right.U),
        V: (left.V - right.V)
    );

    /// <summary>Advances one step of the companion Möbius map <c>y ↦ P + Q/y</c> on a projective pair, without dividing.</summary>
    /// <param name="pair">The current convergent <c>(numerator : denominator)</c>.</param>
    /// <returns>The next convergent <c>(P·numerator + Q·denominator : numerator)</c> — one row of the companion action.</returns>
    /// <remarks>Iterating from <c>(1 : 0)</c> enumerates the convergents of the periodic continued fraction whose value is
    /// the fixed point of the map; the metallic means are the case <c>(P, Q) = (k, 1)</c>.</remarks>
    public Projective MobiusStep(Projective pair) {
        // The typeof comparison folds to a JIT-time constant for every closed value-type instantiation, so over
        // FixedQ4816 every algebra takes a fused lane and non-FixedQ4816 carriers never see the raw casts.
        if (typeof(TScalar) == typeof(FixedQ4816)) {
            return (mIntegerCoefficients
                ? MobiusStepFusedInteger(pair: pair)
                : MobiusStepFusedFractional(pair: pair));
        }

        return new(
            // The numerator is P·n + Q·d; a degenerate coefficient makes its term the additive identity, so the sum
            // reduces to the surviving term (or to the additive identity itself when both coefficients are degenerate).
            Numerator: (mPIsAdditiveIdentity
                ? (mQIsAdditiveIdentity ? TScalar.AdditiveIdentity : (Q * pair.Denominator))
                : (mQIsAdditiveIdentity ? (P * pair.Numerator) : ((P * pair.Numerator) + (Q * pair.Denominator)))),
            Denominator: pair.Numerator
        );
    }
    /// <summary>Raises the adjoined root to a power by fast exponentiation — the closed-form engine for the companion sequences.</summary>
    /// <param name="exponent">The power; zero yields <see cref="One"/>.</param>
    /// <returns>The element <c>x^exponent</c>. Its <see cref="Element.V"/> is the <paramref name="exponent"/>-th term of
    /// the sequence satisfying <c>a_{n+1} = P·a_n + Q·a_{n-1}</c>, and <see cref="Element.U"/> is <c>Q</c> times the
    /// previous term; for <c>(P, Q) = (k, 1)</c> these are the <c>k</c>-metallic sequences.</returns>
    /// <remarks>Square-and-multiply over the exponent's binary expansion, so the operation count depends on the exponent
    /// and the routine is not constant-time in it. Over an exact carrier this is the standard logarithmic-time evaluator
    /// for a second-order linear recurrence.</remarks>
    public Element CompanionPower(ulong exponent) {
        var result = One;
        var power = Root;

        while (0UL != exponent) {
            if (0UL != (exponent & 1UL)) { result = Multiply(left: result, right: power); }

            exponent >>>= 1;

            if (0UL != exponent) { power = Multiply(left: power, right: power); }
        }

        return result;
    }
    /// <summary>Determines whether another descriptor names the same algebra.</summary>
    /// <param name="other">The descriptor to compare against.</param>
    /// <returns><see langword="true"/> when both coefficients are equal; otherwise <see langword="false"/>.</returns>
    /// <remarks>The pair <c>(P, Q)</c> is the whole of a descriptor's identity — the lane and degeneracy classifications
    /// are pure functions of it, and the lanes agree bit-for-bit where they overlap — so descriptors with equal
    /// coefficients are interchangeable. Written explicitly for that reason: the synthesized record equality would also
    /// compare the classification fields, which <see langword="default"/> leaves unset, making a default-initialized
    /// descriptor unequal to <see cref="Create"/> over the carrier's additive identities although the two compute
    /// identical results.</remarks>
    public bool Equals(QuadraticAlgebra<TScalar> other) =>
        (EqualityComparer<TScalar>.Default.Equals(x: P, y: other.P) && EqualityComparer<TScalar>.Default.Equals(x: Q, y: other.Q));
    /// <summary>Returns a hash code over the defining coefficients.</summary>
    /// <returns>A hash code consistent with <see cref="Equals(QuadraticAlgebra{TScalar})"/>.</returns>
    public override int GetHashCode() =>
        HashCode.Combine(value1: P, value2: Q);

    // Integer-lane Multiply for the FixedQ4816 carrier: both coefficients are exact integers, so they enter the widened
    // raw Q32 sums as plain integer multipliers. Widen every product, accumulate the whole component expression, round
    // once. Reproduces FixedComplex for (0, −1) and FixedSplit for (0, +1) bit-for-bit across the full raw range. Reached
    // only when the JIT-constant typeof guard in Multiply holds, so the raw casts are safe. This is the exact-integer
    // reduction of the general Q48 semantics — with p, q integers the Q48 factor 2^16 cancels and the Q48 rounder's
    // shift-32 collapses to this shift-16 rounding, so the two lanes agree bit-for-bit on every integer-coefficient
    // algebra (complex.twin-quad and split.twin-quad assert this lane against the shared-nothing Q48 oracle at the
    // (0,-1) and (0,+1) relations respectively).
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private Element MultiplyFusedInteger(Element left, Element right) {
        var u1 = Unsafe.BitCast<TScalar, FixedQ4816>(source: left.U).Value;
        var v1 = Unsafe.BitCast<TScalar, FixedQ4816>(source: left.V).Value;
        var u2 = Unsafe.BitCast<TScalar, FixedQ4816>(source: right.U).Value;
        var v2 = Unsafe.BitCast<TScalar, FixedQ4816>(source: right.V).Value;
        var pInt = (Unsafe.BitCast<TScalar, FixedQ4816>(source: P).Value >> FixedQ4816.FractionBitCount);
        var qInt = (Unsafe.BitCast<TScalar, FixedQ4816>(source: Q).Value >> FixedQ4816.FractionBitCount);
        var combinedMagnitude = FusedArithmetic.RawMagnitude(value: u1) | FusedArithmetic.RawMagnitude(value: v1) |
                                 FusedArithmetic.RawMagnitude(value: u2) | FusedArithmetic.RawMagnitude(value: v2);
        long u;
        long v;

        if ((pInt == 0L) && (qInt >= -1L) && (qInt <= 1L) && (combinedMagnitude < (1UL << 31))) {
            // Two Q32 products per component fit Int64 in this window; identical to the FixedComplex/FixedSplit fast path.
            u = FixedQ4816.RoundProductSum(productSum: unchecked(((u1 * u2) + (qInt * (v1 * v2)))));
            v = FixedQ4816.RoundProductSum(productSum: unchecked(((u1 * v2) + (v1 * u2))));
        } else if ((pInt >= -1L) && (pInt <= 1L) && (qInt >= -1L) && (qInt <= 1L) && (combinedMagnitude < (1UL << 30))) {
            // V carries three raw Q32 products (the extra P·v₁v₂ term), so the safe Int64 bound tightens from 2^31 to
            // 2^30 — three products each below 2^60 still sum inside Int64 (the four-product bound at FixedQuaternion:100).
            var rootProduct = (v1 * v2);

            u = FixedQ4816.RoundProductSum(productSum: unchecked(((u1 * u2) + (qInt * rootProduct))));
            v = FixedQ4816.RoundProductSum(productSum: unchecked((((u1 * v2) + (v1 * u2)) + (pInt * rootProduct))));
        } else {
            // Int128 accumulation, unchecked: a sum exceeding 128 bits wraps by k·2^128, shifting the rounded Q16 result
            // by k·2^112, which the final 64-bit raw wrapping erases without changing tie parity (see RoundProductSum).
            var rootProduct = ((Int128)v1 * v2);

            u = FixedQ4816.RoundProductSum(productSum: unchecked((((Int128)u1 * u2) + (qInt * rootProduct))));
            v = FixedQ4816.RoundProductSum(productSum: unchecked(((((Int128)u1 * v2) + ((Int128)v1 * u2)) + (pInt * rootProduct))));
        }

        return new(
            U: Unsafe.BitCast<FixedQ4816, TScalar>(source: FixedQ4816.FromRawBits(value: u)),
            V: Unsafe.BitCast<FixedQ4816, TScalar>(source: FixedQ4816.FromRawBits(value: v))
        );
    }

    // Integer-lane Norm for the FixedQ4816 carrier: U² + P·U·V − Q·V² accumulated at full width and rounded once,
    // matching FixedSplit.Norm bit-for-bit for (0, +1). The integer coefficients scale the raw Q32 products exactly, so
    // this is the shift-16 collapse of the general Q48 norm. Reached only under the JIT-constant typeof guard in Norm.
    // The dispatch shape mirrors MultiplyFusedInteger deliberately: the narrow long lanes are the fall-through and the
    // Int128 accumulation is the cold else, so a value-in-regime norm keeps the hot path Int128-free. That matters
    // beyond raw op count — when a static-readonly callsite lets the JIT fold this method's frozen coefficients and
    // inline it into a hot loop, an unconditional Int128 expression fails the widen-multiply recognizer and spills each
    // operator to an out-of-line BCL call; the long fall-through lowers to imul with no calls on either callsite shape.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private TScalar NormFusedInteger(Element value) {
        var u = Unsafe.BitCast<TScalar, FixedQ4816>(source: value.U).Value;
        var v = Unsafe.BitCast<TScalar, FixedQ4816>(source: value.V).Value;
        var pInt = (Unsafe.BitCast<TScalar, FixedQ4816>(source: P).Value >> FixedQ4816.FractionBitCount);
        var qInt = (Unsafe.BitCast<TScalar, FixedQ4816>(source: Q).Value >> FixedQ4816.FractionBitCount);
        var combinedMagnitude = FusedArithmetic.RawMagnitude(value: u) | FusedArithmetic.RawMagnitude(value: v);
        long raw;

        if ((pInt == 0L) && (qInt >= -1L) && (qInt <= 1L) && (combinedMagnitude < (1UL << 31))) {
            // Two Q32 products fit Int64 in this window; identical to the FixedComplex/FixedSplit Norm fast path.
            raw = FixedQ4816.RoundProductSum(productSum: unchecked(((u * u) - (qInt * (v * v)))));
        } else if ((pInt >= -1L) && (pInt <= 1L) && (qInt >= -1L) && (qInt <= 1L) && (combinedMagnitude < (1UL << 30))) {
            // The P·U·V term adds a third raw Q32 product, so the safe Int64 bound tightens from 2^31 to 2^30 — three
            // products each below 2^60 still sum inside Int64 (mirrors MultiplyFusedInteger's V-component bound).
            raw = FixedQ4816.RoundProductSum(productSum: unchecked((((u * u) + (pInt * (u * v))) - (qInt * (v * v)))));
        } else {
            raw = FixedQ4816.RoundProductSum(productSum: unchecked(((((Int128)u * u) + (pInt * ((Int128)u * v))) - (qInt * ((Int128)v * v)))));
        }

        return Unsafe.BitCast<FixedQ4816, TScalar>(source: FixedQ4816.FromRawBits(value: raw));
    }

    // Integer-lane MobiusStep for the FixedQ4816 carrier: P·n + Q·d with integer coefficients is pInt·n_raw + qInt·d_raw
    // — the integers scale the Q16 raws exactly, so the numerator is EXACT (zero roundings), the shift-16 collapse of
    // the general Q32→Q16 numerator whose remainder is identically zero here. No rounding shift follows, so the carrier
    // keeps only the sum's low 64 bits under the wrapping policy every raw operator applies; integer multiplication and
    // addition are both congruences mod 2^64, so wrapping long arithmetic delivers exactly those bits and a widened
    // accumulation would be truncated back to them. Reached only under the JIT-constant guard in MobiusStep, so the raw
    // casts are safe.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private Projective MobiusStepFusedInteger(Projective pair) {
        var pInt = (Unsafe.BitCast<TScalar, FixedQ4816>(source: P).Value >> FixedQ4816.FractionBitCount);
        var qInt = (Unsafe.BitCast<TScalar, FixedQ4816>(source: Q).Value >> FixedQ4816.FractionBitCount);
        var numeratorRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: pair.Numerator).Value;
        var denominatorRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: pair.Denominator).Value;
        var numerator = unchecked(((pInt * numeratorRaw) + (qInt * denominatorRaw)));

        return new(
            Numerator: Unsafe.BitCast<FixedQ4816, TScalar>(source: FixedQ4816.FromRawBits(value: numerator)),
            Denominator: pair.Numerator
        );
    }

    // Fractional-lane Multiply for the FixedQ4816 carrier: coefficients that are not exact integers, carried as raw Q16
    // multipliers p, q. Each returned component is ONE ties-to-even rounding of the exact rational value of the ideal
    // expression, wrapped to the raw carrier — the same discipline the integer lane applies, expressed at Q48:
    //   U: T = u₁·u₂·2^16 + q·v₁·v₂  (Q48),  output = wrap64(round_ties_even(T / 2^32));
    //   V: T = (u₁·v₂ + v₁·u₂)·2^16 + p·v₁·v₂  (Q48),  same rounding.
    // Every T is an integer-coefficient polynomial in the raw inputs, so accumulating it entirely in unchecked Int128
    // yields C ≡ T (mod 2^128); the Q48 rounder's final 64-bit wrap erases the k·2^96 the wrap can introduce while
    // preserving tie parity (see RoundQ48SumToRaw). Reached only under the JIT-constant typeof guard in Multiply.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private Element MultiplyFusedFractional(Element left, Element right) {
        var u1 = Unsafe.BitCast<TScalar, FixedQ4816>(source: left.U).Value;
        var v1 = Unsafe.BitCast<TScalar, FixedQ4816>(source: left.V).Value;
        var u2 = Unsafe.BitCast<TScalar, FixedQ4816>(source: right.U).Value;
        var v2 = Unsafe.BitCast<TScalar, FixedQ4816>(source: right.V).Value;
        var pRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: P).Value;
        var qRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: Q).Value;
        var rootProduct = ((Int128)v1 * v2);
        var u = FusedArithmetic.RoundQ48SumToRaw(productSum: unchecked(((((Int128)u1 * u2) << FixedQ4816.FractionBitCount) + ((Int128)qRaw * rootProduct))));
        var v = FusedArithmetic.RoundQ48SumToRaw(productSum: unchecked((((((Int128)u1 * v2) + ((Int128)v1 * u2)) << FixedQ4816.FractionBitCount) + ((Int128)pRaw * rootProduct))));

        return new(
            U: Unsafe.BitCast<FixedQ4816, TScalar>(source: FixedQ4816.FromRawBits(value: u)),
            V: Unsafe.BitCast<FixedQ4816, TScalar>(source: FixedQ4816.FromRawBits(value: v))
        );
    }

    // Fractional-lane Norm for the FixedQ4816 carrier: T = u·u·2^16 + p·u·v − q·v·v at Q48, output = wrap64(round(T / 2^32)).
    // Reached only under the JIT-constant typeof guard in Norm.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private TScalar NormFusedFractional(Element value) {
        var u = Unsafe.BitCast<TScalar, FixedQ4816>(source: value.U).Value;
        var v = Unsafe.BitCast<TScalar, FixedQ4816>(source: value.V).Value;
        var pRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: P).Value;
        var qRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: Q).Value;
        var raw = FusedArithmetic.RoundQ48SumToRaw(productSum: unchecked((((((Int128)u * u) << FixedQ4816.FractionBitCount) + ((Int128)pRaw * ((Int128)u * v))) - ((Int128)qRaw * ((Int128)v * v)))));

        return Unsafe.BitCast<FixedQ4816, TScalar>(source: FixedQ4816.FromRawBits(value: raw));
    }

    // Fractional-lane MobiusStep for the FixedQ4816 carrier: T = p·n + q·d at Q32, output = wrap64(round(T / 2^16)). The
    // numerator is a single Q32→Q16 rounding — the same shift-16 rounder the raw operators use — so a fractional relation
    // rounds once (the integer lane's exact numerator is the remainder-zero case of this). Reached only under the
    // JIT-constant guard in MobiusStep.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private Projective MobiusStepFusedFractional(Projective pair) {
        var pRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: P).Value;
        var qRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: Q).Value;
        var numeratorRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: pair.Numerator).Value;
        var denominatorRaw = Unsafe.BitCast<TScalar, FixedQ4816>(source: pair.Denominator).Value;
        var numerator = FixedQ4816.RoundProductSum(productSum: unchecked((((Int128)pRaw * numeratorRaw) + ((Int128)qRaw * denominatorRaw))));

        return new(
            Numerator: Unsafe.BitCast<FixedQ4816, TScalar>(source: FixedQ4816.FromRawBits(value: numerator)),
            Denominator: pair.Numerator
        );
    }

}
