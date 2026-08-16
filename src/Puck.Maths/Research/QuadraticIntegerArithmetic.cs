using System.Numerics;
using Element = Puck.Maths.QuadraticAlgebra<System.Numerics.BigInteger>.Element;

namespace Puck.Maths.Research;

/// <summary>The splitting character of a rational prime in a quadratic order: how its principal ideal decomposes.</summary>
public enum QuadraticSplitting {
    /// <summary>The prime factors into two distinct prime ideals of norm equal to the prime.</summary>
    Split,
    /// <summary>The prime stays prime; the prime element has norm equal to the prime's square.</summary>
    Inert,
    /// <summary>The prime is the square of a single prime ideal of norm equal to the prime; this is the divides-the-discriminant case.</summary>
    Ramified,
}
/// <summary>A prime element of a quadratic order together with the exponent it carries in a factorization.</summary>
/// <param name="Prime">The canonical prime element.</param>
/// <param name="Multiplicity">The positive exponent of <paramref name="Prime"/> in the factored element.</param>
public readonly record struct QuadraticPrimeFactor(Element Prime, int Multiplicity);
/// <summary>The exact factorization of a nonzero element of a quadratic order into a leading unit and canonical prime powers.</summary>
/// <param name="LeadingUnit">The unit <c>u</c> for which <c>u · ∏ Prime_i^{Multiplicity_i}</c> reassembles the input exactly.</param>
/// <param name="Factors">The prime factors in ascending order by norm magnitude, ties broken by scalar then root coefficient.</param>
public readonly record struct QuadraticFactorization(Element LeadingUnit, IReadOnlyList<QuadraticPrimeFactor> Factors);
/// <summary>The obstruction returned when a factorization fails because a prime above a rational prime is not principal — the class-group witness.</summary>
/// <param name="RationalPrime">The rational prime whose ideal above it has no generator of matching norm.</param>
/// <param name="Splitting">The splitting character that prime carries, so the witness records why the search was attempted.</param>
public readonly record struct QuadraticFactorizationObstruction(BigInteger RationalPrime, QuadraticSplitting Splitting);
/// <summary>
/// Primality and factorization of quadratic integers — the arithmetic layer built inside <see cref="QuadraticAlgebra{TScalar}"/>
/// over the <see cref="BigInteger"/> carrier. The descriptor <c>(P, Q)</c> names the order <c>Z[x]</c> with <c>x² = P·x + Q</c>;
/// its nonsquare discriminant <c>Δ = P² + 4Q</c> classifies the order (imaginary when <c>Δ &lt; 0</c>, real when <c>Δ &gt; 0</c>).
/// </summary>
/// <remarks>
/// <para>
/// This ports the concepts of <see cref="PrimeExtensions"/> one level up: exact primality by norm, and a deterministic
/// factorization into canonical prime powers with multiplicity in ascending order. The <see cref="BigInteger"/> carrier
/// sidesteps norm overflow; when every coefficient magnitude fits the proven <c>Int128</c> envelope, the norm, multiply,
/// and exact divide-out route through an allocation-free fixed-width tier with bit-identical results.
/// </para>
/// <para>
/// When a rational prime dividing the norm has no prime element of matching norm above it — the ideal is not principal —
/// factorization fails honestly with a <see cref="QuadraticFactorizationObstruction"/>. That failure is the class-group
/// witness and is a feature, not a defect: over a class-number-one order it never fires; over a larger class group it
/// pinpoints the non-principal rational prime.
/// </para>
/// </remarks>
public static class QuadraticIntegerArithmetic {
    /// <summary>Divides a prime out of the residual as many times as it divides exactly, recording the multiplicity.</summary>
    private static void AddDividedOut(QuadraticAlgebra<BigInteger> algebra, Element prime, List<QuadraticPrimeFactor> factors, ref Element residual) {
        var multiplicity = 0;

        while (TryDivideExact(
            algebra: algebra,
            dividend: residual,
            divisor: prime,
            quotient: out var quotient
        )) {
            residual = quotient;
            ++multiplicity;
        }

        if (0 < multiplicity) {
            factors.Add(item: new QuadraticPrimeFactor(
                Multiplicity: multiplicity,
                Prime: prime
            ));
        }
    }
    /// <summary>Returns the canonical associate against unit data already built for the descriptor.</summary>
    private static Element CanonicalAssociate(QuadraticAlgebra<BigInteger> algebra, Element value, in UnitBasis units) {
        if (
            (value.U.IsZero) &&
            (value.V.IsZero)
        ) { return algebra.Zero; }

        if (units.ImaginaryGroup is not null) {
            Element best = default;
            var seen = false;

            foreach (var unit in units.ImaginaryGroup) {
                var candidate = FastMultiply(
                    algebra: algebra,
                    left: unit,
                    right: value
                );

                if (
                    (!seen) ||
                    IsScalarRootGreater(
                    left: candidate,
                    right: best
                )
                ) {
                    best = candidate;
                    seen = true;
                }
            }

            return best;
        }

        var current = value;

        if (0 > Embedding1Sign(
            units: units,
            value: current
        )) { current = algebra.Negate(value: current); }

        while (0 <= Embedding1Compare(
            units: units,
            left: current,
            right: units.Fundamental
        )) {
            current = FastMultiply(
                algebra: algebra,
                left: current,
                right: units.FundamentalInverse
            );
        }

        while (0 > Embedding1CompareToOne(
            units: units,
            value: current
        )) {
            current = FastMultiply(
                algebra: algebra,
                left: current,
                right: units.Fundamental
            );
        }

        return current;
    }
    /// <summary>Compares the larger real embeddings of two elements.</summary>
    private static int Embedding1Compare(in UnitBasis units, Element left, Element right) =>
        SurdSign(
            rational: ((2 * (left.U - right.U)) + (units.P * (left.V - right.V))),
            surd: (left.V - right.V),
            radicand: units.Discriminant
        );
    /// <summary>Returns the sign of the larger real embedding of an element minus one.</summary>
    private static int Embedding1CompareToOne(in UnitBasis units, Element value) =>
        SurdSign(
            rational: (((2 * value.U) + (units.P * value.V)) - 2),
            surd: value.V,
            radicand: units.Discriminant
        );
    /// <summary>Returns the sign of the larger real embedding of an element in a real order.</summary>
    private static int Embedding1Sign(in UnitBasis units, Element value) =>
        SurdSign(
            rational: ((2 * value.U) + (units.P * value.V)),
            surd: value.V,
            radicand: units.Discriminant
        );
    /// <summary>Multiplies two elements in Int128 when the coefficients and both operands fit the routing bound, else on the wide carrier.</summary>
    private static Element FastMultiply(QuadraticAlgebra<BigInteger> algebra, Element left, Element right) {
        if (
            TryExtractCoefficients(
            algebra: algebra,
            p: out var p,
            q: out var q
        ) &&
            TryExtract(
            value: left.U,
            result: out var u1
        ) &&
            TryExtract(
            value: left.V,
            result: out var v1
        ) &&
            TryExtract(
            value: right.U,
            result: out var u2
        ) &&
            TryExtract(
            value: right.V,
            result: out var v2
        )
        ) {
            var rootProduct = (((Int128)v1) * v2);

            return new Element(
                U: ((BigInteger)((((Int128)u1) * u2) + (q * rootProduct))),
                V: ((BigInteger)(((((Int128)u1) * v2) + (((Int128)v1) * u2)) + (p * rootProduct)))
            );
        }

        return algebra.Multiply(
            left: left,
            right: right
        );
    }
    /// <summary>Evaluates the norm in Int128 when the coefficients and element fit the routing bound, else on the wide carrier.</summary>
    private static BigInteger FastNorm(QuadraticAlgebra<BigInteger> algebra, Element value) {
        if (
            TryExtractCoefficients(
            algebra: algebra,
            p: out var p,
            q: out var q
        ) &&
            TryExtract(
            value: value.U,
            result: out var u
        ) &&
            TryExtract(
            value: value.V,
            result: out var v
        )
        ) {
            return ((BigInteger)(((((Int128)u) * u) + (p * (((Int128)u) * v))) - (q * (((Int128)v) * v))));
        }

        return algebra.Norm(value: value);
    }
    /// <summary>Finds a ring element of norm magnitude equal to the given rational prime, or reports its absence.</summary>
    /// <remarks>
    /// Both branches solve the same norm equation <c>X² − Δ·Y² = ±4ℓ</c> under the substitution <c>X = 2a + Pb</c>,
    /// <c>Y = b</c>, and both decide absence exactly rather than giving up: a definite form bounds its solutions
    /// outright, while an indefinite one has infinitely many unit multiples and is settled instead by the
    /// continued-fraction cycle of the ideal above <c>ℓ</c>, which certifies non-principality when it closes.
    /// </remarks>
    private static Element? FindNormElement(QuadraticAlgebra<BigInteger> algebra, BigInteger magnitude) {
        var discriminant = algebra.Discriminant;
        var p = algebra.P;

        if (discriminant.Sign < 0) {
            // Positive-definite norm form: 4ℓ = (2a + Pb)² + |Δ|b², so b and a are bounded — a finite lattice search.
            var absoluteDiscriminant = -discriminant;
            var fourMagnitude = (4 * magnitude);
            var bBound = BigIntegerFunctions.SquareRoot(value: (fourMagnitude / absoluteDiscriminant));

            for (var b = -bBound; (b <= bBound); ++b) {
                var remainder = (fourMagnitude - ((absoluteDiscriminant * b) * b));

                if (remainder.Sign < 0) { continue; }

                var root = BigIntegerFunctions.SquareRoot(value: remainder);

                if ((root * root) != remainder) { continue; }

                foreach (var signedRoot in ((root.IsZero)
                    ? (BigInteger[])[root]
                    : (BigInteger[])[root, -root])) {
                    var numerator = (signedRoot - (p * b));

                    if (!numerator.IsEven) { continue; }

                    var candidate = new Element(
                        U: (numerator / 2),
                        V: b
                    );

                    if (FastNorm(
                        algebra: algebra,
                        value: candidate
                    ) == magnitude) { return candidate; }
                }
            }

            return null;
        }

        // Indefinite norm form: the solution set is a union of unit orbits, so it is unbounded and the ideal above ℓ
        // decides it instead. X ≡ P·Y (mod 2) holds for every solution, since Δ ≡ P² (mod 4) makes X² − P²Y² a multiple
        // of four, so the scalar coordinate is integral with no parity screen.
        var solution = QuadraticNormEquation.SolveForPrimeNorm(
            discriminant: discriminant,
            rationalPrime: magnitude
        );

        if (solution is null) { return null; }

        return new Element(
            U: ((solution.Value.X - (p * solution.Value.Y)) / 2),
            V: solution.Value.Y
        );
    }
    /// <summary>Enumerates the unit group of an imaginary order by the finite lattice of norm-one elements.</summary>
    private static List<Element> ImaginaryUnits(QuadraticAlgebra<BigInteger> algebra) {
        var absoluteDiscriminant = -algebra.Discriminant;
        var p = algebra.P;
        var bBound = BigIntegerFunctions.SquareRoot(value: (new BigInteger(value: 4) / absoluteDiscriminant));
        var units = new List<Element>();

        for (var b = -bBound; (b <= bBound); ++b) {
            var remainder = (4 - ((absoluteDiscriminant * b) * b));

            if (remainder.Sign < 0) { continue; }

            var root = BigIntegerFunctions.SquareRoot(value: remainder);

            if ((root * root) != remainder) { continue; }

            foreach (var signedRoot in ((root.IsZero)
                ? (BigInteger[])[root]
                : (BigInteger[])[root, -root])) {
                var numerator = (signedRoot - (p * b));

                if (!numerator.IsEven) { continue; }

                var candidate = new Element(
                    U: (numerator / 2),
                    V: b
                );

                if (
                    FastNorm(
                    algebra: algebra,
                    value: candidate
                ).IsOne &&
                    (!units.Contains(item: candidate))
                ) { units.Add(item: candidate); }
            }
        }

        return units;
    }
    /// <summary>Returns the inverse of a unit as <c>conjugate / norm</c>, where the norm is plus or minus one.</summary>
    private static Element InverseUnit(QuadraticAlgebra<BigInteger> algebra, Element unit) {
        var conjugate = algebra.Conjugate(value: unit);

        return (algebra.Norm(value: unit).IsOne
            ? conjugate
            : algebra.Negate(value: conjugate)
        );
    }
    /// <summary>Compares two elements by the scalar-then-root descending total order used for the imaginary canonical associate.</summary>
    private static bool IsScalarRootGreater(Element left, Element right) {
        var comparison = left.U.CompareTo(other: right.U);

        return ((0 != comparison)
            ? (0 < comparison)
            : (0 < left.V.CompareTo(other: right.V))
        );
    }
    /// <summary>Returns the sign of <c>rational + surd·√radicand</c> exactly, for a positive radicand.</summary>
    private static int SurdSign(BigInteger rational, BigInteger surd, BigInteger radicand) {
        if (surd.IsZero) { return rational.Sign; }
        if (rational.IsZero) { return surd.Sign; }
        if (rational.Sign == surd.Sign) { return rational.Sign; }

        // Opposite signs: square both terms and compare magnitudes, restoring the sign of the dominant term.
        var rationalSquared = (rational * rational);
        var surdSquared = ((surd * surd) * radicand);

        return ((surd.Sign > 0)
            ? surdSquared.CompareTo(other: rationalSquared)
            : rationalSquared.CompareTo(other: surdSquared)
        );
    }
    /// <summary>Attempts the exact ring division <c>dividend / divisor</c> by multiplying through the conjugate and testing the norm divides both components.</summary>
    private static bool TryDivideExact(QuadraticAlgebra<BigInteger> algebra, Element dividend, Element divisor, out Element quotient) {
        if (
            TryExtractCoefficients(
            algebra: algebra,
            p: out var p,
            q: out var q
        ) &&
            TryExtract(
            value: dividend.U,
            result: out var a
        ) &&
            TryExtract(
            value: dividend.V,
            result: out var b
        ) &&
            TryExtract(
            value: divisor.U,
            result: out var c
        ) &&
            TryExtract(
            value: divisor.V,
            result: out var e
        )
        ) {
            // The conjugate product's root-coefficient terms cancel, so its two components — and the divisor norm — reduce
            // to the bounded expressions below, all exact in Int128 under the routing bound (see FastNorm's derivation).
            var norm = (((((Int128)c) * c) + (p * (((Int128)c) * e))) - (q * (((Int128)e) * e)));
            var productU = (((((Int128)a) * c) + (p * (((Int128)a) * e))) - (q * (((Int128)b) * e)));
            var productV = ((((Int128)b) * c) - (((Int128)a) * e));

            if (
                ((productU % norm) == Int128.Zero) &&
                ((productV % norm) == Int128.Zero)
            ) {
                quotient = new Element(
                    U: ((BigInteger)(productU / norm)),
                    V: ((BigInteger)(productV / norm))
                );

                return true;
            }

            quotient = default;

            return false;
        }

        var wideNorm = algebra.Norm(value: divisor);
        var product = algebra.Multiply(
            left: dividend,
            right: algebra.Conjugate(value: divisor)
        );

        if (
            (product.U % wideNorm).IsZero &&
            (product.V % wideNorm).IsZero
        ) {
            quotient = new Element(
                U: (product.U / wideNorm),
                V: (product.V / wideNorm)
            );

            return true;
        }

        quotient = default;

        return false;
    }
    /// <summary>Extracts a value as a machine word when its magnitude is within the routing bound.</summary>
    private static bool TryExtract(BigInteger value, out long result) {
        if (
            (value >= FastTierLowerBound) &&
            (value <= FastTierUpperBound)
        ) {
            result = ((long)value);

            return true;
        }

        result = 0L;

        return false;
    }
    /// <summary>Extracts both defining coefficients as machine words, reporting whether each fits the routing bound.</summary>
    private static bool TryExtractCoefficients(QuadraticAlgebra<BigInteger> algebra, out long p, out long q) {
        var fitsP = TryExtract(
            value: algebra.P,
            result: out p
        );
        var fitsQ = TryExtract(
            value: algebra.Q,
            result: out q
        );

        return (
            fitsP &&
            fitsQ
        );
    }

    /// <summary>Returns the deterministic canonical associate of an element — one distinguished representative of its unit orbit.</summary>
    /// <param name="algebra">The order descriptor.</param>
    /// <param name="value">The element to normalize; zero maps to zero.</param>
    /// <returns>The canonical associate of <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentException">The discriminant is a perfect square, so the descriptor names no quadratic-field order; the check runs while the unit basis builds its fundamental unit.</exception>
    /// <remarks>
    /// <para>
    /// Imaginary case (finite unit group): the canonical associate is the unit multiple <c>u·z</c> that is greatest under
    /// the total order comparing the scalar part first and the root coefficient second, both descending. Because the unit
    /// group is finite and the order is a domain, that maximum is unique; for the two-unit orbit <c>{z, −z}</c> it selects
    /// positive scalar part, or — when the scalar part is zero — positive root coefficient, so the representative lies in a
    /// fixed half-plane.
    /// </para>
    /// <para>
    /// Real case (infinite unit group): the canonical associate is the unique unit multiple whose larger real embedding is
    /// positive and lies in the half-open fundamental interval <c>[1, ε₁)</c>, where <c>ε₁ &gt; 1</c> is the larger
    /// embedding of the fundamental unit. Multiplying by the fundamental unit scales that embedding by <c>ε₁</c>, so exactly
    /// one associate lands in the interval. All comparisons are exact integer comparisons of the surd-valued embedding.
    /// </para>
    /// </remarks>
    public static Element CanonicalAssociate(this QuadraticAlgebra<BigInteger> algebra, Element value) {
        if (
            (value.U.IsZero) &&
            (value.V.IsZero)
        ) { return algebra.Zero; }

        return CanonicalAssociate(
            algebra: algebra,
            value: value,
            units: new UnitBasis(algebra: algebra)
        );
    }
    /// <summary>Returns the fundamental unit of a real quadratic order — the smallest unit greater than one under the embedding that sends the root to its larger real value.</summary>
    /// <param name="algebra">The order descriptor; its discriminant must be positive and not a perfect square.</param>
    /// <returns>The fundamental unit, an element of norm plus or minus one.</returns>
    /// <remarks>
    /// The units are the elements <c>a + b·x</c> with <c>a² + P·a·b − Q·b² = ±1</c>; substituting <c>X = 2a + Pb</c>,
    /// <c>Y = b</c> turns that into the order's norm equation at <c>N = ±1</c>, <c>X² − Δ·Y² = ±4</c>, which
    /// <see cref="QuadraticNormEquation"/> solves from the discriminant alone. That solver walks the continued fraction
    /// of the reduced root <c>(b + √Δ)/2</c>, where <c>b</c> is the largest integer of <c>Δ</c>'s parity below
    /// <c>√Δ</c>, and returns the first closure of its period; the norm sign is the period length's parity. Because the
    /// first closure is the smallest unit above one outright, the minimal-<c>Y</c> preference for the norm-minus-one
    /// solution is subsumed by the walk rather than applied as a branch — nothing here needs to re-add it.
    /// </remarks>
    /// <exception cref="ArgumentException">The discriminant is not positive, so the order is not real, or it is a perfect square, so the descriptor names no quadratic-field order.</exception>
    public static Element FundamentalUnit(this QuadraticAlgebra<BigInteger> algebra) {
        var discriminant = algebra.Discriminant;

        if (discriminant.Sign <= 0) {
            throw new ArgumentException(
                message: "The fundamental unit is defined only for a real order with positive discriminant.",
                paramName: nameof(algebra)
            );
        }

        // A positive SQUARE discriminant names a split or degenerate ring with no fundamental unit, and its rational
        // square root leaves the walk no periodic cycle to close on; rejecting it here is what keeps the walk
        // terminating. The solver's third precondition — Δ ≡ 0 or 1 (mod 4) — holds automatically, since Δ = P² + 4Q.
        algebra.ValidateDescriptor();

        var solution = QuadraticNormEquation.FundamentalUnit(discriminant: discriminant);

        // X ≡ P·Y (mod 2), because X² ≡ Δ·Y² ≡ P²·Y² (mod 2), so the scalar coordinate is integral.
        return new Element(
            U: ((solution.X - (algebra.P * solution.Y)) / 2),
            V: solution.Y
        );
    }
    /// <summary>Determines whether an element is a prime element of the order.</summary>
    /// <param name="algebra">The order descriptor.</param>
    /// <param name="value">The element to test.</param>
    /// <returns><see langword="true"/> when the element is prime; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// Zero and units are not prime, by convention. An element whose norm has prime magnitude is prime — it generates a
    /// prime ideal of that norm. An element whose norm magnitude is the square of an inert rational prime is prime exactly
    /// when it is an associate of that rational prime, which is the standing form of an inert prime. Every other element is
    /// composite.
    /// </remarks>
    public static bool IsPrimeElement(this QuadraticAlgebra<BigInteger> algebra, Element value) {
        var norm = BigInteger.Abs(value: FastNorm(
            algebra: algebra,
            value: value
        ));

        if (
            norm.IsZero ||
            norm.IsOne
        ) { return false; }
        if (BigIntegerFunctions.IsPrime(value: norm)) { return true; }

        var root = BigIntegerFunctions.SquareRoot(value: norm);

        if (
            ((root * root) == norm) &&
            BigIntegerFunctions.IsPrime(value: root) &&
            (QuadraticSplitting.Inert == algebra.SplittingCharacter(rationalPrime: root))
        ) {
            // The unit search belongs to the descriptor, not to either element, so one basis serves both associates.
            var units = new UnitBasis(algebra: algebra);
            var inert = CanonicalAssociate(
                algebra: algebra,
                value: new Element(
                    U: root,
                    V: BigInteger.Zero
                ),
                units: units
            );

            return (CanonicalAssociate(
                algebra: algebra,
                units: units,
                value: value
            ) == inert);
        }

        return false;
    }
    /// <summary>Determines whether an element is a unit of the order.</summary>
    /// <param name="algebra">The order descriptor.</param>
    /// <param name="value">The element to test.</param>
    /// <returns><see langword="true"/> when the norm has magnitude one; otherwise <see langword="false"/>.</returns>
    public static bool IsUnit(this QuadraticAlgebra<BigInteger> algebra, Element value) =>
        BigInteger.Abs(value: FastNorm(
            algebra: algebra,
            value: value
        )).IsOne;
    /// <summary>Determines how a rational prime decomposes in the order.</summary>
    /// <param name="algebra">The order descriptor.</param>
    /// <param name="rationalPrime">The rational prime whose character is taken.</param>
    /// <returns><see cref="QuadraticSplitting.Split"/>, <see cref="QuadraticSplitting.Inert"/>, or <see cref="QuadraticSplitting.Ramified"/>.</returns>
    /// <remarks>
    /// For an odd prime the character is the Jacobi symbol <c>(Δ / ℓ)</c>: zero is ramified, one is split, minus one is
    /// inert. The prime two is decided by the discriminant modulo eight — even discriminant ramifies, one splits, five is
    /// inert — since the Jacobi symbol is undefined for an even lower argument.
    /// </remarks>
    public static QuadraticSplitting SplittingCharacter(this QuadraticAlgebra<BigInteger> algebra, BigInteger rationalPrime) {
        var discriminant = algebra.Discriminant;

        if (rationalPrime == 2) {
            var residue = ((int)(((discriminant % 8) + 8) % 8));

            if (0 == (residue & 1)) { return QuadraticSplitting.Ramified; }

            return ((1 == residue)
                ? QuadraticSplitting.Split
                : QuadraticSplitting.Inert
            );
        }

        return NumberTheoryFunctions.JacobiSymbol(
            denominator: rationalPrime,
            numerator: discriminant
        ) switch {
            0 => QuadraticSplitting.Ramified,
            1 => QuadraticSplitting.Split,
            _ => QuadraticSplitting.Inert,
        };
    }
    /// <summary>Attempts to factor a nonzero element into a leading unit and canonical prime powers.</summary>
    /// <param name="algebra">The order descriptor.</param>
    /// <param name="value">The element to factor.</param>
    /// <param name="factorization">On success, the leading unit and ascending prime powers whose product reassembles <paramref name="value"/> exactly.</param>
    /// <param name="obstruction">On failure, the non-principal rational prime and its splitting character; otherwise the default.</param>
    /// <returns><see langword="true"/> when a factorization was produced; <see langword="false"/> when a prime above some rational prime is not principal.</returns>
    /// <remarks>
    /// The norm magnitude is factored into rational primes (a small trial ladder, a deterministic strong-pseudoprime gate,
    /// and a deterministic cycle-walk splitter). Each rational prime is lifted through the splitting law: an inert prime
    /// contributes itself, while a split or ramified prime needs a generator of norm plus or minus the prime — found by a
    /// bounded lattice search in the imaginary case and by the continued-fraction cycle of the ideal above the prime in
    /// the real case. When no such generator exists the prime above is not principal and the method fails with that
    /// prime as the obstruction. On
    /// success the prime factors are canonical associates, ordered ascending by norm magnitude then by scalar and root
    /// coefficient, and the leading unit closes the exact product.
    /// </remarks>
    public static bool TryFactorize(this QuadraticAlgebra<BigInteger> algebra, Element value, out QuadraticFactorization factorization, out QuadraticFactorizationObstruction obstruction) {
        algebra.ValidateDescriptor();

        obstruction = default;
        factorization = default;

        var norm = FastNorm(
            algebra: algebra,
            value: value
        );

        if (norm.IsZero) {
            factorization = new QuadraticFactorization(
                LeadingUnit: algebra.Zero,
                Factors: []
            );

            return true;
        }

        var magnitude = BigInteger.Abs(value: norm);

        if (magnitude.IsOne) {
            factorization = new QuadraticFactorization(
                Factors: [],
                LeadingUnit: value
            );

            return true;
        }

        var residual = value;
        var factors = new List<QuadraticPrimeFactor>();
        // The norm magnitude exceeds one here, so the rational ladder yields at least one prime and every branch below
        // normalizes at least one element: one unit basis serves the whole factorization.
        var units = new UnitBasis(algebra: algebra);

        // Distinct primes, ascending: the multiplicity is the ELEMENT's business, and AddDividedOut counts it off the
        // residual itself. Deduplicating a sorted sequence keeps the ascending order the factorization contract states.
        foreach (var rationalPrime in BigIntegerFunctions.EnumeratePrimeFactors(value: magnitude).Distinct()) {
            var splitting = algebra.SplittingCharacter(rationalPrime: rationalPrime);

            if (QuadraticSplitting.Inert == splitting) {
                var prime = CanonicalAssociate(
                    algebra: algebra,
                    value: new Element(
                        U: rationalPrime,
                        V: BigInteger.Zero
                    ),
                    units: units
                );

                AddDividedOut(
                    algebra: algebra,
                    factors: factors,
                    prime: prime,
                    residual: ref residual
                );

                continue;
            }

            var generator = FindNormElement(
                algebra: algebra,
                magnitude: rationalPrime
            );

            if (generator is null) {
                obstruction = new QuadraticFactorizationObstruction(
                    RationalPrime: rationalPrime,
                    Splitting: splitting
                );
                factorization = default;

                return false;
            }

            var primeA = CanonicalAssociate(
                algebra: algebra,
                value: generator.Value,
                units: units
            );
            var primeB = CanonicalAssociate(
                algebra: algebra,
                value: algebra.Conjugate(value: generator.Value),
                units: units
            );

            AddDividedOut(
                algebra: algebra,
                factors: factors,
                prime: primeA,
                residual: ref residual
            );

            if (primeB != primeA) {
                AddDividedOut(
                    algebra: algebra,
                    factors: factors,
                    prime: primeB,
                    residual: ref residual
                );
            }
        }

        factors.Sort(comparison: (left, right) => {
            var comparison = BigInteger.Abs(value: algebra.Norm(value: left.Prime)).CompareTo(other: BigInteger.Abs(value: algebra.Norm(value: right.Prime)));

            if (0 != comparison) { return comparison; }

            comparison = left.Prime.U.CompareTo(other: right.Prime.U);

            return ((0 != comparison)
                ? comparison
                : left.Prime.V.CompareTo(other: right.Prime.V)
            );
        });

        factorization = new QuadraticFactorization(
            Factors: factors,
            LeadingUnit: residual
        );

        return true;
    }
    /// <summary>Validates that a descriptor names an order in a quadratic field — that is, that its discriminant is not a perfect square.</summary>
    /// <param name="algebra">The descriptor to validate.</param>
    /// <exception cref="ArgumentException">The discriminant <c>Δ = P² + 4Q</c> is a perfect square (including zero), so the algebra is a split or degenerate ring rather than a quadratic-field order.</exception>
    public static void ValidateDescriptor(this QuadraticAlgebra<BigInteger> algebra) {
        var discriminant = algebra.Discriminant;

        if (discriminant.Sign >= 0) {
            var root = BigIntegerFunctions.SquareRoot(value: discriminant);

            if ((root * root) == discriminant) {
                throw new ArgumentException(
                    message: "The discriminant P² + 4Q must be a nonsquare; a square (or zero) discriminant does not name a quadratic-field order.",
                    paramName: nameof(algebra)
                );
            }
        }
    }

    /// <summary>
    /// The unit data a canonical-associate search consumes, derived from the descriptor alone: the finite unit group in
    /// the imaginary case, or the fundamental unit and its inverse in the real case, together with the discriminant and
    /// linear coefficient the embedding comparisons need. None of it depends on the element being normalized and the
    /// real case walks the continued-fraction period of the order's root to build it, so a top-level operation
    /// constructs one and threads it through every internal call it makes. The lifetime is the call; nothing is cached
    /// between operations.
    /// </summary>
    private readonly struct UnitBasis {
        internal UnitBasis(QuadraticAlgebra<BigInteger> algebra) {
            Discriminant = algebra.Discriminant;
            P = algebra.P;

            if (Discriminant.Sign < 0) {
                ImaginaryGroup = ImaginaryUnits(algebra: algebra);
                Fundamental = default;
                FundamentalInverse = default;
            } else {
                ImaginaryGroup = null;
                Fundamental = algebra.FundamentalUnit();
                FundamentalInverse = InverseUnit(
                    algebra: algebra,
                    unit: Fundamental
                );
            }
        }

        /// <summary>Gets the discriminant <c>Δ = P² + 4Q</c>, formed once instead of once per embedding comparison.</summary>
        internal BigInteger Discriminant { get; }
        /// <summary>Gets the fundamental unit; meaningful only in the real case.</summary>
        internal Element Fundamental { get; }
        /// <summary>Gets the inverse of <see cref="Fundamental"/>; meaningful only in the real case.</summary>
        internal Element FundamentalInverse { get; }
        /// <summary>Gets the finite unit group, non-<see langword="null"/> exactly in the imaginary case.</summary>
        internal List<Element>? ImaginaryGroup { get; }
        /// <summary>Gets the descriptor's linear coefficient.</summary>
        internal BigInteger P { get; }
    }

    // ---- Fixed-width fast tier ----
    //
    // The norm and divide-out inner loop dominate the factorization profile; this tier extracts the coefficients
    // (P, Q) and an element's parts as machine words, computes in Int128 when they clear a proven bound, and
    // otherwise falls through to the BigInteger methods above — bit-identically. Callers never choose a tier.
    //
    // Bound proof. Let B be the routing bound, so |P|, |Q|, |U|, |V| ≤ B for every accepted operand. The norm
    // U² + P·U·V − Q·V², the divisor norm, and the conjugate-product components a·c + P·a·e − Q·b·e and b·c − a·e
    // are each a sum of at most three terms, each a product of at most three bounded factors, so |term| ≤ B³ and
    // the whole expression is bounded by B² + 2·B³ < 3·B³; every partial product formed en route (e.g. U·V before
    // P·(U·V)) is itself at most B³. With B = 2⁴¹ the widest value is below 2⁸² + 2¹²⁴ < 2¹²⁵ — under the signed
    // Int128 ceiling 2¹²⁷ — so no intermediate sum of signed 64×64 products can wrap. Division and remainder
    // truncate toward zero in Int128 exactly as in BigInteger, so the divisibility test and quotient agree
    // bit-for-bit with the wide path.
    private const long FastTierBound = (1L << 41);

    private static readonly BigInteger FastTierUpperBound = FastTierBound;
    private static readonly BigInteger FastTierLowerBound = -FastTierBound;
}
