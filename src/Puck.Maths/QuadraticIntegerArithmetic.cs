using System.Numerics;
using Element = Puck.Maths.QuadraticAlgebra<System.Numerics.BigInteger>.Element;

namespace Puck.Maths;

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
/// sidesteps norm overflow; a fixed-width fast tier could specialize the same operations once the norm range is bounded.
/// </para>
/// <para>
/// When a rational prime dividing the norm has no prime element of matching norm above it — the ideal is not principal —
/// factorization fails honestly with a <see cref="QuadraticFactorizationObstruction"/>. That failure is the class-group
/// witness and is a feature, not a defect: over a class-number-one order it never fires; over a larger class group it
/// pinpoints the non-principal rational prime.
/// </para>
/// </remarks>
public static class QuadraticIntegerArithmetic {
    /// <summary>Validates that a descriptor names an order in a quadratic field — that is, that its discriminant is not a perfect square.</summary>
    /// <param name="algebra">The descriptor to validate.</param>
    /// <exception cref="ArgumentException">The discriminant <c>Δ = P² + 4Q</c> is a perfect square (including zero), so the algebra is a split or degenerate ring rather than a quadratic-field order.</exception>
    public static void ValidateDescriptor(this QuadraticAlgebra<BigInteger> algebra) {
        var discriminant = algebra.Discriminant;

        if (discriminant.Sign >= 0) {
            var root = BigIntegerMath.SquareRoot(value: discriminant);

            if ((root * root) == discriminant) {
                throw new ArgumentException(message: "The discriminant P² + 4Q must be a nonsquare; a square (or zero) discriminant does not name a quadratic-field order.", paramName: nameof(algebra));
            }
        }
    }
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
            var residue = (int)(((discriminant % 8) + 8) % 8);

            if (0 == (residue & 1)) { return QuadraticSplitting.Ramified; }

            return ((1 == residue) ? QuadraticSplitting.Split : QuadraticSplitting.Inert);
        }

        return NumberTheoryFunctions.JacobiSymbol(numerator: discriminant, denominator: rationalPrime) switch {
            0 => QuadraticSplitting.Ramified,
            1 => QuadraticSplitting.Split,
            _ => QuadraticSplitting.Inert,
        };
    }
    /// <summary>Determines whether an element is a unit of the order.</summary>
    /// <param name="algebra">The order descriptor.</param>
    /// <param name="value">The element to test.</param>
    /// <returns><see langword="true"/> when the norm has magnitude one; otherwise <see langword="false"/>.</returns>
    public static bool IsUnit(this QuadraticAlgebra<BigInteger> algebra, Element value) =>
        BigInteger.Abs(algebra.Norm(value: value)).IsOne;
    /// <summary>Returns the fundamental unit of a real quadratic order — the smallest unit greater than one under the embedding that sends the root to its larger real value.</summary>
    /// <param name="algebra">The order descriptor; its discriminant must be positive.</param>
    /// <returns>The fundamental unit, an element of norm plus or minus one.</returns>
    /// <remarks>
    /// The units are the elements <c>a + b·x</c> with <c>a² + P·a·b − Q·b² = ±1</c>; substituting <c>X = 2a + Pb</c>,
    /// <c>Y = b</c> turns that into the Pell equation <c>X² − Δ·Y² = ±4</c>. The fundamental unit corresponds to the
    /// minimal positive <c>Y</c>, preferring the norm-minus-one solution when both signs occur at that <c>Y</c>. The walk
    /// increases <c>Y</c> until a solution appears, mirroring the continued-fraction ascent; this duplicates the intent of
    /// <see cref="PellEquation.FundamentalUnit(BigInteger)"/>, which instead returns the norm-one unit of <c>Z[√Δ]</c>, so
    /// a later dedup could unify the two once a shared order-unit primitive exists.
    /// </remarks>
    /// <exception cref="ArgumentException">The discriminant is not positive, so the order is not real.</exception>
    public static Element FundamentalUnit(this QuadraticAlgebra<BigInteger> algebra) {
        var discriminant = algebra.Discriminant;

        if (discriminant.Sign <= 0) {
            throw new ArgumentException(message: "The fundamental unit is defined only for a real order with positive discriminant.", paramName: nameof(algebra));
        }

        var p = algebra.P;

        for (var y = BigInteger.One; ; ++y) {
            var deltaYSquared = (discriminant * y * y);

            // Prefer the norm -1 branch (X² = Δy² - 4); fall back to norm +1 (X² = Δy² + 4).
            foreach (var target in (BigInteger[])[(deltaYSquared - 4), (deltaYSquared + 4)]) {
                if (target.Sign <= 0) { continue; }

                var x = BigIntegerMath.SquareRoot(value: target);

                if ((x * x) != target) { continue; }

                // X ≡ Py (mod 2) is guaranteed, so a is integral.
                return new Element(U: ((x - (p * y)) / 2), V: y);
            }
        }
    }
    /// <summary>Returns the deterministic canonical associate of an element — one distinguished representative of its unit orbit.</summary>
    /// <param name="algebra">The order descriptor.</param>
    /// <param name="value">The element to normalize; zero maps to zero.</param>
    /// <returns>The canonical associate of <paramref name="value"/>.</returns>
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
        if ((value.U.IsZero) && (value.V.IsZero)) { return algebra.Zero; }

        if (algebra.Discriminant.Sign < 0) {
            Element best = default;
            var seen = false;

            foreach (var unit in ImaginaryUnits(algebra: algebra)) {
                var candidate = algebra.Multiply(left: unit, right: value);

                if ((!seen) || IsScalarRootGreater(left: candidate, right: best)) {
                    best = candidate;
                    seen = true;
                }
            }

            return best;
        }

        var fundamental = algebra.FundamentalUnit();
        var inverse = InverseUnit(algebra: algebra, unit: fundamental);
        var current = value;

        if (0 > Embedding1Sign(algebra: algebra, value: current)) { current = algebra.Negate(value: current); }

        while (0 <= Embedding1Compare(algebra: algebra, left: current, right: fundamental)) {
            current = algebra.Multiply(left: current, right: inverse);
        }

        while (0 > Embedding1CompareToOne(algebra: algebra, value: current)) {
            current = algebra.Multiply(left: current, right: fundamental);
        }

        return current;
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
        var norm = BigInteger.Abs(algebra.Norm(value: value));

        if (norm.IsZero || norm.IsOne) { return false; }
        if (IsRationalPrime(value: norm)) { return true; }

        var root = BigIntegerMath.SquareRoot(value: norm);

        if (((root * root) == norm) && IsRationalPrime(value: root) && (QuadraticSplitting.Inert == algebra.SplittingCharacter(rationalPrime: root))) {
            var inert = algebra.CanonicalAssociate(value: new Element(U: root, V: BigInteger.Zero));

            return (algebra.CanonicalAssociate(value: value) == inert);
        }

        return false;
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
    /// bounded lattice search in the imaginary case and by a continued-fraction orbit walk in the real case. When no such
    /// generator exists the prime above is not principal and the method fails with that prime as the obstruction. On
    /// success the prime factors are canonical associates, ordered ascending by norm magnitude then by scalar and root
    /// coefficient, and the leading unit closes the exact product.
    /// </remarks>
    public static bool TryFactorize(this QuadraticAlgebra<BigInteger> algebra, Element value, out QuadraticFactorization factorization, out QuadraticFactorizationObstruction obstruction) {
        algebra.ValidateDescriptor();

        obstruction = default;
        factorization = default;

        var norm = algebra.Norm(value: value);

        if (norm.IsZero) {
            factorization = new QuadraticFactorization(LeadingUnit: algebra.Zero, Factors: []);

            return true;
        }

        var magnitude = BigInteger.Abs(norm);

        if (magnitude.IsOne) {
            factorization = new QuadraticFactorization(LeadingUnit: value, Factors: []);

            return true;
        }

        var residual = value;
        var factors = new List<QuadraticPrimeFactor>();

        foreach (var (rationalPrime, _) in FactorInteger(value: magnitude)) {
            var splitting = algebra.SplittingCharacter(rationalPrime: rationalPrime);

            if (QuadraticSplitting.Inert == splitting) {
                var prime = algebra.CanonicalAssociate(value: new Element(U: rationalPrime, V: BigInteger.Zero));

                AddDividedOut(algebra: algebra, prime: prime, factors: factors, residual: ref residual);

                continue;
            }

            var generator = FindNormElement(algebra: algebra, magnitude: rationalPrime);

            if (generator is null) {
                obstruction = new QuadraticFactorizationObstruction(RationalPrime: rationalPrime, Splitting: splitting);
                factorization = default;

                return false;
            }

            var primeA = algebra.CanonicalAssociate(value: generator.Value);
            var primeB = algebra.CanonicalAssociate(value: algebra.Conjugate(value: generator.Value));

            AddDividedOut(algebra: algebra, prime: primeA, factors: factors, residual: ref residual);

            if (primeB != primeA) { AddDividedOut(algebra: algebra, prime: primeB, factors: factors, residual: ref residual); }
        }

        factors.Sort(comparison: (left, right) => {
            var comparison = BigInteger.Abs(algebra.Norm(value: left.Prime)).CompareTo(other: BigInteger.Abs(algebra.Norm(value: right.Prime)));

            if (0 != comparison) { return comparison; }

            comparison = left.Prime.U.CompareTo(other: right.Prime.U);

            return ((0 != comparison) ? comparison : left.Prime.V.CompareTo(other: right.Prime.V));
        });

        factorization = new QuadraticFactorization(LeadingUnit: residual, Factors: factors);

        return true;
    }

    /// <summary>Divides a prime out of the residual as many times as it divides exactly, recording the multiplicity.</summary>
    private static void AddDividedOut(QuadraticAlgebra<BigInteger> algebra, Element prime, List<QuadraticPrimeFactor> factors, ref Element residual) {
        var multiplicity = 0;

        while (TryDivideExact(algebra: algebra, dividend: residual, divisor: prime, quotient: out var quotient)) {
            residual = quotient;
            ++multiplicity;
        }

        if (0 < multiplicity) { factors.Add(item: new QuadraticPrimeFactor(Prime: prime, Multiplicity: multiplicity)); }
    }
    /// <summary>Attempts the exact ring division <c>dividend / divisor</c> by multiplying through the conjugate and testing the norm divides both components.</summary>
    private static bool TryDivideExact(QuadraticAlgebra<BigInteger> algebra, Element dividend, Element divisor, out Element quotient) {
        var norm = algebra.Norm(value: divisor);
        var product = algebra.Multiply(left: dividend, right: algebra.Conjugate(value: divisor));

        if ((product.U % norm).IsZero && (product.V % norm).IsZero) {
            quotient = new Element(U: (product.U / norm), V: (product.V / norm));

            return true;
        }

        quotient = default;

        return false;
    }
    /// <summary>Finds a ring element of norm magnitude equal to the given rational prime, or reports its absence.</summary>
    private static Element? FindNormElement(QuadraticAlgebra<BigInteger> algebra, BigInteger magnitude) {
        var discriminant = algebra.Discriminant;
        var p = algebra.P;

        if (discriminant.Sign < 0) {
            // Positive-definite norm form: 4ℓ = (2a + Pb)² + |Δ|b², so b and a are bounded — a finite lattice search.
            var absoluteDiscriminant = -discriminant;
            var fourMagnitude = (4 * magnitude);
            var bBound = BigIntegerMath.SquareRoot(value: (fourMagnitude / absoluteDiscriminant));

            for (var b = -bBound; b <= bBound; ++b) {
                var remainder = (fourMagnitude - (absoluteDiscriminant * b * b));

                if (remainder.Sign < 0) { continue; }

                var root = BigIntegerMath.SquareRoot(value: remainder);

                if ((root * root) != remainder) { continue; }

                foreach (var signedRoot in ((root.IsZero) ? (BigInteger[])[root] : (BigInteger[])[root, -root])) {
                    var numerator = (signedRoot - (p * b));

                    if (!numerator.IsEven) { continue; }

                    var candidate = new Element(U: (numerator / 2), V: b);

                    if (algebra.Norm(value: candidate) == magnitude) { return candidate; }
                }
            }

            return null;
        }

        // Indefinite norm form: walk the continued-fraction orbit reps of X² - ΔY² = ±4ℓ and keep an integral generator.
        foreach (var target in (BigInteger[])[magnitude, -magnitude]) {
            foreach (var representative in PellEquation.OrbitRepresentatives(radicand: discriminant, norm: (4 * target))) {
                var numerator = (representative.X - (p * representative.Y));

                if (!numerator.IsEven) { continue; }

                var candidate = new Element(U: (numerator / 2), V: representative.Y);

                if (algebra.Norm(value: candidate) == target) { return candidate; }
            }
        }

        return null;
    }
    /// <summary>Enumerates the unit group of an imaginary order by the finite lattice of norm-one elements.</summary>
    private static List<Element> ImaginaryUnits(QuadraticAlgebra<BigInteger> algebra) {
        var absoluteDiscriminant = -algebra.Discriminant;
        var p = algebra.P;
        var bBound = BigIntegerMath.SquareRoot(value: (new BigInteger(value: 4) / absoluteDiscriminant));
        var units = new List<Element>();

        for (var b = -bBound; b <= bBound; ++b) {
            var remainder = (4 - (absoluteDiscriminant * b * b));

            if (remainder.Sign < 0) { continue; }

            var root = BigIntegerMath.SquareRoot(value: remainder);

            if ((root * root) != remainder) { continue; }

            foreach (var signedRoot in ((root.IsZero) ? (BigInteger[])[root] : (BigInteger[])[root, -root])) {
                var numerator = (signedRoot - (p * b));

                if (!numerator.IsEven) { continue; }

                var candidate = new Element(U: (numerator / 2), V: b);

                if (algebra.Norm(value: candidate).IsOne && (!units.Contains(item: candidate))) { units.Add(item: candidate); }
            }
        }

        return units;
    }
    /// <summary>Returns the inverse of a unit as <c>conjugate / norm</c>, where the norm is plus or minus one.</summary>
    private static Element InverseUnit(QuadraticAlgebra<BigInteger> algebra, Element unit) {
        var conjugate = algebra.Conjugate(value: unit);

        return (algebra.Norm(value: unit).IsOne ? conjugate : algebra.Negate(value: conjugate));
    }
    /// <summary>Compares two elements by the scalar-then-root descending total order used for the imaginary canonical associate.</summary>
    private static bool IsScalarRootGreater(Element left, Element right) {
        var comparison = left.U.CompareTo(other: right.U);

        return ((0 != comparison) ? (0 < comparison) : (0 < left.V.CompareTo(other: right.V)));
    }
    /// <summary>Returns the sign of the larger real embedding of an element in a real order.</summary>
    private static int Embedding1Sign(QuadraticAlgebra<BigInteger> algebra, Element value) =>
        SurdSign(rational: ((2 * value.U) + (algebra.P * value.V)), surd: value.V, radicand: algebra.Discriminant);
    /// <summary>Returns the sign of the larger real embedding of an element minus one.</summary>
    private static int Embedding1CompareToOne(QuadraticAlgebra<BigInteger> algebra, Element value) =>
        SurdSign(rational: (((2 * value.U) + (algebra.P * value.V)) - 2), surd: value.V, radicand: algebra.Discriminant);
    /// <summary>Compares the larger real embeddings of two elements.</summary>
    private static int Embedding1Compare(QuadraticAlgebra<BigInteger> algebra, Element left, Element right) =>
        SurdSign(rational: ((2 * (left.U - right.U)) + (algebra.P * (left.V - right.V))), surd: (left.V - right.V), radicand: algebra.Discriminant);
    /// <summary>Returns the sign of <c>rational + surd·√radicand</c> exactly, for a positive radicand.</summary>
    private static int SurdSign(BigInteger rational, BigInteger surd, BigInteger radicand) {
        if (surd.IsZero) { return rational.Sign; }
        if (rational.IsZero) { return surd.Sign; }
        if (rational.Sign == surd.Sign) { return rational.Sign; }

        // Opposite signs: square both terms and compare magnitudes, restoring the sign of the dominant term.
        var rationalSquared = (rational * rational);
        var surdSquared = (surd * surd * radicand);

        return ((surd.Sign > 0) ? surdSquared.CompareTo(other: rationalSquared) : rationalSquared.CompareTo(other: surdSquared));
    }
    /// <summary>Factors a positive integer into ascending rational primes with multiplicity.</summary>
    private static List<(BigInteger Prime, int Multiplicity)> FactorInteger(BigInteger value) {
        var flat = new List<BigInteger>();

        FactorRecurse(value: value, flat: flat);
        flat.Sort();

        var grouped = new List<(BigInteger Prime, int Multiplicity)>();

        foreach (var prime in flat) {
            if ((grouped.Count > 0) && (grouped[^1].Prime == prime)) {
                grouped[^1] = (prime, (grouped[^1].Multiplicity + 1));
            } else {
                grouped.Add(item: (prime, 1));
            }
        }

        return grouped;
    }
    /// <summary>Splits a value into rational primes by peeling twos, a strong-pseudoprime gate, then a cycle-walk splitter.</summary>
    private static void FactorRecurse(BigInteger value, List<BigInteger> flat) {
        if (value <= BigInteger.One) { return; }
        if (value.IsEven) {
            flat.Add(item: 2);
            FactorRecurse(value: (value / 2), flat: flat);

            return;
        }
        if (IsRationalPrime(value: value)) {
            flat.Add(item: value);

            return;
        }

        var divisor = FindDivisor(value: value);

        FactorRecurse(value: divisor, flat: flat);
        FactorRecurse(value: (value / divisor), flat: flat);
    }
    /// <summary>Returns a nontrivial divisor of an odd composite by a deterministic cycle-walk over <c>y² + c</c>.</summary>
    private static BigInteger FindDivisor(BigInteger value) {
        for (var addend = BigInteger.One; ; ++addend) {
            var slow = new BigInteger(value: 2);
            var fast = new BigInteger(value: 2);
            var divisor = BigInteger.One;

            do {
                slow = (((slow * slow) + addend) % value);
                fast = (((fast * fast) + addend) % value);
                fast = (((fast * fast) + addend) % value);
                divisor = BigInteger.GreatestCommonDivisor(left: BigInteger.Abs(slow - fast), right: value);
            } while (divisor.IsOne);

            if (divisor != value) { return divisor; }
        }
    }
    /// <summary>Decides primality of a positive integer exactly: the reused word-sized gate below its ceiling, a fixed-base strong-pseudoprime test above it.</summary>
    private static bool IsRationalPrime(BigInteger value) {
        if (value < 2) { return false; }
        if (value <= ulong.MaxValue) { return PrimeField64.IsPrime(value: (ulong)value); }

        // A twelve-base witness set proven complete for every value below 3.317 * 10²⁴, past the norms this layer meets.
        ReadOnlySpan<int> witnesses = [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37];
        var oddPart = (value - 1);
        var twoExponent = 0;

        while (oddPart.IsEven) {
            oddPart >>= 1;
            ++twoExponent;
        }

        foreach (var witnessBase in witnesses) {
            var witness = new BigInteger(value: witnessBase);

            if (witness >= value) { continue; }

            var residue = BigInteger.ModPow(value: witness, exponent: oddPart, modulus: value);

            if (residue.IsOne || (residue == (value - 1))) { continue; }

            var composite = true;

            for (var round = 1; (round < twoExponent); ++round) {
                residue = ((residue * residue) % value);

                if (residue == (value - 1)) {
                    composite = false;

                    break;
                }
            }

            if (composite) { return false; }
        }

        return true;
    }
}
