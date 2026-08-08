using System.Numerics;
using Puck.Maths.Research;
using Xunit;

using Algebra = Puck.Maths.QuadraticAlgebra<System.Numerics.BigInteger>;
using Element = Puck.Maths.QuadraticAlgebra<System.Numerics.BigInteger>.Element;

namespace Puck.Maths.Tests;

/// <summary>
/// The claim bodies for <see cref="Puck.Maths.Research.QuadraticIntegerArithmetic"/> and
/// <see cref="Puck.Maths.PellEquation"/>. Every reference computation here is written fresh against
/// <see cref="BigInteger"/> and
/// calls no member of <c>Puck.Maths</c>, so agreement is independent evidence rather than a re-spelling; where a helper
/// mirrors a RETIRED implementation (the ascending-Y unit scan, the orbit-box existence search, the pre-delegation Pell
/// convergent loop) the leg in <c>laws/quadratic-integer.json</c> says so and names what independent evidence
/// still stands beside it. Every operand stream here is a deterministic arithmetic progression — never
/// <see cref="Random"/> and never wall-clock — so a failure reproduces from the printed step alone.
/// </summary>
internal static class QuadraticIntegerClaims {
    // ---- Deterministic operand generation (no System.Random, no wall clock) ----

    // A component in [-bound, bound], derived from the step by a fixed affine recurrence — the same deterministic
    // "looks random, isn't" shape CoreSurfaceClaims already uses for its own swept ladders.
    private static BigInteger DeterministicComponent(long step, long multiplier, long offset, long bound) {
        var span = ((2 * bound) + 1);
        var reduced = (((step * multiplier) + offset) % span);

        if (reduced < 0) { reduced += span; }

        return (reduced - bound);
    }

    private static Element DeterministicElement(long step, long bound) =>
        new(
            U: DeterministicComponent(step: step, multiplier: 2654435761L, offset: 17L, bound: bound),
            V: DeterministicComponent(step: step, multiplier: 6364136223846793005L, offset: 11L, bound: bound)
        );

    // ---- Shared-nothing reference arithmetic (none of these call any Puck.Maths member) ----

    private static BigInteger RefNorm(BigInteger p, BigInteger q, BigInteger u, BigInteger v) =>
        (((u * u) + ((p * u) * v)) - ((q * v) * v));

    // The floor integer square root by Newton descent, independent of BigIntegerFunctions.SquareRoot.
    private static BigInteger ISqrtBig(BigInteger value) {
        if (value.Sign <= 0) { return BigInteger.Zero; }

        var estimate = (BigInteger.One << checked((int)((value.GetBitLength() + 1L) / 2L)));

        while (true) {
            var next = ((estimate + (value / estimate)) >> 1);

            if (next >= estimate) { return estimate; }

            estimate = next;
        }
    }

    private static bool IsPerfectSquare(BigInteger value) {
        if (value.Sign < 0) { return false; }

        var root = ISqrtBig(value: value);

        return ((root * root) == value);
    }

    // A deterministic strong-pseudoprime test over a fixed witness ladder, independent of BigIntegerFunctions.IsPrime.
    private static bool IsProbablePrimeBig(BigInteger value) {
        if (value < 2) { return false; }

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

    // Trial division over a small ceiling, independent of NumberTheoryFunctions.SegmentedPrimeSieve/EnumeratePrimes.
    private static IReadOnlyList<int> EnumerateSmallPrimesUpTo(int ceiling) {
        var primes = new List<int>();

        for (var candidate = 2; (candidate <= ceiling); ++candidate) {
            var isPrime = true;

            for (var divisor = 2; (((long)divisor * divisor) <= candidate); ++divisor) {
                if ((candidate % divisor) == 0) {
                    isPrime = false;

                    break;
                }
            }

            if (isPrime) { primes.Add(item: candidate); }
        }

        return primes;
    }

    // The Jacobi symbol by binary reciprocity descent with the two's supplement — written here, sharing no
    // line with NumberTheoryFunctions.JacobiSymbol or UnsignedNumberFunctions.JacobiSymbol.
    private static int RefJacobiSymbol(BigInteger numerator, BigInteger denominator) {
        var upper = (((numerator % denominator) + denominator) % denominator);
        var lower = denominator;
        var symbol = 1;

        while (!upper.IsZero) {
            while (upper.IsEven) {
                upper >>= 1;

                var residue = (int)(lower % 8);

                if ((3 == residue) || (5 == residue)) { symbol = -symbol; }
            }

            (upper, lower) = (lower, upper);

            if ((3 == (int)(upper % 4)) && (3 == (int)(lower % 4))) { symbol = -symbol; }

            upper %= lower;
        }

        return (lower.IsOne ? symbol : 0);
    }

    // The inert branch of the splitting law, recomputed locally rather than through QuadraticIntegerArithmetic's own
    // SplittingCharacter or NumberTheoryFunctions.JacobiSymbol.
    private static bool RefIsInert(BigInteger discriminant, BigInteger rationalPrime) {
        if (rationalPrime == 2) {
            var residue = (int)(((discriminant % 8) + 8) % 8);

            return ((1 == (residue & 1)) && (1 != residue));
        }

        return (-1 == RefJacobiSymbol(numerator: discriminant, denominator: rationalPrime));
    }

    // The prime-element predicate recomputed from the reference norm alone: calls no QuadraticIntegerArithmetic member.
    private static bool RefIsPrimeElement(Element value, BigInteger p, BigInteger q) {
        var norm = BigInteger.Abs(RefNorm(p: p, q: q, u: value.U, v: value.V));

        if (norm <= BigInteger.One) { return false; }
        if (IsProbablePrimeBig(value: norm)) { return true; }

        var root = ISqrtBig(value: norm);

        if (((root * root) != norm) || (!IsProbablePrimeBig(value: root)) || (!RefIsInert(discriminant: ((p * p) + (4 * q)), rationalPrime: root))) {
            return false;
        }

        return ((value.U % root).IsZero && (value.V % root).IsZero);
    }

    // Rebuilds an element from its factorization using the SUBJECT's own Multiply — this is the reassembly the
    // factorization contract promises, not a second implementation of it.
    private static Element Reassemble(Algebra algebra, QuadraticFactorization factorization) {
        var product = factorization.LeadingUnit;

        foreach (var factor in factorization.Factors) {
            for (var power = 0; (power < factor.Multiplicity); ++power) { product = algebra.Multiply(left: product, right: factor.Prime); }
        }

        return product;
    }

    private static bool SameFactorization(QuadraticFactorization left, QuadraticFactorization right) {
        if (left.LeadingUnit != right.LeadingUnit) { return false; }
        if (left.Factors.Count != right.Factors.Count) { return false; }

        for (var index = 0; (index < left.Factors.Count); ++index) {
            if (left.Factors[index] != right.Factors[index]) { return false; }
        }

        return true;
    }

    // The convergent loop PellEquation.FundamentalUnit ran before it delegated to the shared unit-equation primitive,
    // transcribed here so quadratic-integer.pell-delegation-vs-retired-convergent-loop compares the delegation against
    // code that shares nothing with the layer under test except the theorem: expand the continued fraction of sqrt(D)
    // and return the first convergent whose norm is exactly one.
    private static (BigInteger X, BigInteger Y) RetiredPellConvergentLoop(BigInteger radicand) {
        var root = ISqrtBig(value: radicand);
        var remainder = BigInteger.Zero;
        var denominator = BigInteger.One;
        var quotient = root;
        var previousPreviousNumerator = BigInteger.Zero;
        var previousNumerator = BigInteger.One;
        var previousPreviousDenominator = BigInteger.One;
        var previousDenominator = BigInteger.Zero;

        while (true) {
            var numerator = ((quotient * previousNumerator) + previousPreviousNumerator);
            var denominatorConvergent = ((quotient * previousDenominator) + previousPreviousDenominator);

            if (((numerator * numerator) - (radicand * denominatorConvergent * denominatorConvergent)) == BigInteger.One) {
                return (numerator, denominatorConvergent);
            }

            previousPreviousNumerator = previousNumerator;
            previousNumerator = numerator;
            previousPreviousDenominator = previousDenominator;
            previousDenominator = denominatorConvergent;
            remainder = ((denominator * quotient) - remainder);
            denominator = ((radicand - (remainder * remainder)) / denominator);
            quotient = ((root + remainder) / denominator);
        }
    }

    // The orbit box the real-order generator search walked before it followed the ideal's continued fraction, kept here
    // as the shared-nothing existence oracle for quadratic-integer.real-order-prime-norm-existence-vs-retired-orbit-box:
    // every solution of X^2 - Delta*Y^2 = 4N lies in some orbit of the norm-one unit, and every orbit meets the box
    // X^2 < 2|N|U, Delta*Y^2 < 2|N|U. Returns null when the box exceeds the budget.
    private static bool? RetiredOrbitBoxHasNormElement(BigInteger discriminant, BigInteger rationalPrime, long budget) {
        var unit = RetiredPellConvergentLoop(radicand: discriminant);

        foreach (var norm in (BigInteger[])[(4 * rationalPrime), (-4 * rationalPrime)]) {
            var strictSquareCeiling = ((2 * BigInteger.Abs(norm) * unit.X) - 1);
            var yBound = ISqrtBig(value: (strictSquareCeiling / discriminant));

            if (yBound > budget) { return null; }

            for (var y = BigInteger.Zero; (y <= yBound); ++y) {
                var square = (norm + (discriminant * y * y));

                if (square.Sign < 0) { continue; }

                var root = ISqrtBig(value: square);

                if ((root * root) == square) { return true; }
            }
        }

        return false;
    }

    // The retired ascending-Y unit scan: test Delta*y^2 -+ 4 for squareness, -4 branch first, stop at the first hit.
    // Kept as the shared-nothing minimality oracle for quadratic-integer.real-order-fundamental-unit-vs-retired-scan.
    private static bool TryRetiredAscendingUnitScan(BigInteger delta, BigInteger yCeiling, out BigInteger scanX, out BigInteger scanY, out int scanSign) {
        for (var candidate = BigInteger.One; (candidate <= yCeiling); ++candidate) {
            var deltaYSquared = (delta * candidate * candidate);

            foreach (var target in (BigInteger[])[(deltaYSquared - 4), (deltaYSquared + 4)]) {
                if (target.Sign <= 0) { continue; }

                var root = ISqrtBig(value: target);

                if ((root * root) != target) { continue; }

                scanX = root;
                scanY = candidate;
                scanSign = ((target == (deltaYSquared - 4)) ? -1 : 1);

                return true;
            }
        }

        scanX = default;
        scanY = default;
        scanSign = default;

        return false;
    }

    // A discriminant residue check: whether SOME (x, y) in [0, 4) makes x^2 - d*y^2 congruent to -1 modulo four. Used to
    // verify the FORCED sign argument at d = 991 and d = 99991 (both 3 mod 4, so x^2 - d*y^2 = x^2 + y^2 mod 4, which is
    // never 3) without hard-coding either result.
    private static bool SomeResidueIsNegativeOneModFour(BigInteger d) {
        for (var x = 0; (x < 4); ++x) {
            for (var y = 0; (y < 4); ++y) {
                if (3 == ((((x * x) - (d * y * y)) % 4) + 4) % 4) { return true; }
            }
        }

        return false;
    }

    // ---- (a) + (d): the nine imaginary class-number-one worlds and the real Delta = 5 world factor exactly ----
    public static string? ClassNumberOneWorldsFactorSurface() {
        (int P, int Q, int Delta)[] worlds = [
            (1, -1, -3), (0, -1, -4), (1, -2, -7), (0, -2, -8), (1, -3, -11),
            (1, -5, -19), (1, -11, -43), (1, -17, -67), (1, -41, -163),
            (1, 1, 5),
        ];

        foreach (var (p, q, delta) in worlds) {
            var algebra = Algebra.Create(p: p, q: q);

            Assert.Equal(expected: (BigInteger)delta, actual: algebra.Discriminant);

            var factored = 0;

            for (var step = 0L; (step < 250L); ++step) {
                var element = DeterministicElement(step: step, bound: 500L);

                if (BigInteger.Abs(algebra.Norm(value: element)) <= 1) { continue; }

                if (!algebra.TryFactorize(value: element, factorization: out var factorization, obstruction: out var obstruction)) {
                    return $"world Delta={delta}: unexpected obstruction at rational prime {obstruction.RationalPrime} factoring ({element.U},{element.V})";
                }

                foreach (var factor in factorization.Factors) {
                    if (!algebra.IsPrimeElement(value: factor.Prime)) { return $"world Delta={delta}: non-prime factor ({factor.Prime.U},{factor.Prime.V})"; }
                    if (algebra.CanonicalAssociate(value: factor.Prime) != factor.Prime) { return $"world Delta={delta}: non-canonical factor ({factor.Prime.U},{factor.Prime.V})"; }
                }

                if (Reassemble(algebra: algebra, factorization: factorization) != element) { return $"world Delta={delta}: reassembly mismatch for ({element.U},{element.V})"; }
                if (!algebra.IsUnit(value: factorization.LeadingUnit)) { return $"world Delta={delta}: leading factor is not a unit"; }

                ++factored;
            }

            if (factored < 100) { return $"world Delta={delta}: only {factored} elements carried a proper factorization, too few to trust the sweep"; }
        }

        return null;
    }

    // ---- (d): the golden fundamental unit at Delta = 5, and SplittingCharacter against an independent reciprocity descent ----
    public static string? GoldenUnitAndSplittingSurface() {
        var algebra = Algebra.Create(p: BigInteger.One, q: BigInteger.One);

        Assert.Equal(expected: (BigInteger)5, actual: algebra.Discriminant);

        var fundamental = algebra.FundamentalUnit();

        // x itself (0, 1) is Phi by construction of the descriptor (P, Q) = (1, 1): its norm is 0^2 + 1*0*1 - 1*1^2 = -1,
        // an exact hand computation of the SAME formula Norm implements, not a value taken from running the subject.
        Assert.Equal(expected: new Element(U: BigInteger.Zero, V: BigInteger.One), actual: fundamental);
        Assert.Equal(expected: BigInteger.MinusOne, actual: algebra.Norm(value: fundamental));

        var primes = EnumerateSmallPrimesUpTo(ceiling: 2000);
        var agreements = 0;

        foreach (var ell in primes) {
            var rationalPrime = (BigInteger)ell;
            var character = algebra.SplittingCharacter(rationalPrime: rationalPrime);
            var expectedInert = RefIsInert(discriminant: 5, rationalPrime: rationalPrime);
            var expected = (0 == (rationalPrime % 5))
                ? QuadraticSplitting.Ramified
                : (expectedInert ? QuadraticSplitting.Inert : QuadraticSplitting.Split);

            if (character != expected) { return $"Delta=5: SplittingCharacter({ell}) = {character}, expected {expected} from the independent reciprocity descent"; }

            ++agreements;
        }

        if (agreements != primes.Count) { return "Delta=5: the splitting sweep did not visit every enumerated prime"; }

        return null;
    }

    // ---- (b) + (c): the sum-of-two-squares law and the first-twist class-group witness ----
    public static string? SumOfTwoSquaresAndWitnessSurface() {
        var sumOfSquares = Algebra.Create(p: BigInteger.Zero, q: BigInteger.MinusOne);
        var primes = EnumerateSmallPrimesUpTo(ceiling: 9_999);
        var splitCount = 0;

        foreach (var ell in primes) {
            var rationalPrime = (BigInteger)ell;

            if (!sumOfSquares.TryFactorize(value: new Element(U: rationalPrime, V: BigInteger.Zero), factorization: out var factorization, obstruction: out _)) {
                return $"(0,-1) world: unexpected obstruction at {ell}";
            }

            var hasNormEll = factorization.Factors.Any(predicate: factor => (BigInteger.Abs(sumOfSquares.Norm(value: factor.Prime)) == rationalPrime));
            var expected = ((1 == (ell & 3)) || (2 == ell));

            if (hasNormEll != expected) { return $"(0,-1) world: sum-of-two-squares law broke at {ell}: hasNormEll={hasNormEll}, expected={expected}"; }
            if (hasNormEll) { ++splitCount; }

            var character = sumOfSquares.SplittingCharacter(rationalPrime: rationalPrime);
            var characterSplits = (QuadraticSplitting.Inert != character);

            if (characterSplits != hasNormEll) { return $"(0,-1) world: SplittingCharacter({ell}) disagrees with the norm-{ell} factor test"; }
        }

        if (splitCount == 0) { return "(0,-1) world: no prime split, so the law was never exercised"; }

        // The first-twist witness: 6 = 2*3 must fail in the Delta = -20 world.
        var twist = Algebra.Create(p: BigInteger.Zero, q: (BigInteger)(-5));

        if (twist.TryFactorize(value: new Element(U: 6, V: BigInteger.Zero), factorization: out _, obstruction: out var witness)) {
            return "Delta=-20: factoring 6 unexpectedly succeeded";
        }
        if ((witness.RationalPrime != 2) && (witness.RationalPrime != 3)) { return $"Delta=-20: obstruction at {witness.RationalPrime}, expected 2 or 3"; }

        // The obstruction-rate survey: three non-class-number-one worlds must show a nonzero obstruction rate, in
        // contrast with the zero-obstruction worlds quadratic-integer.class-number-one-worlds-factor-prime-canonical pins.
        (int P, int Q, int Delta)[] survey = [(0, -5, -20), (1, -4, -15), (0, -6, -24)];

        foreach (var (p, q, delta) in survey) {
            var algebra = Algebra.Create(p: p, q: q);
            var obstructed = 0;
            var factored = 0;

            for (var step = 0L; (step < 300L); ++step) {
                var element = DeterministicElement(step: step, bound: 200L);

                if (BigInteger.Abs(algebra.Norm(value: element)) <= 1) { continue; }

                if (algebra.TryFactorize(value: element, factorization: out var factorization, obstruction: out _)) {
                    if (Reassemble(algebra: algebra, factorization: factorization) != element) { return $"Delta={delta}: reassembly mismatch"; }

                    ++factored;
                } else {
                    ++obstructed;
                }
            }

            if (obstructed == 0) { return $"Delta={delta}: zero obstructions observed, so the nontrivial class group was never exercised"; }
        }

        return null;
    }

    // ---- (e): factorization is deterministic across repeated calls ----
    public static string? FactorizationDeterminismSurface() {
        var algebra = Algebra.Create(p: BigInteger.One, q: (BigInteger)(-5)); // Delta = -19, class number one.
        var elements = new Element[100];

        for (var step = 0L; (step < elements.Length); ++step) { elements[step] = DeterministicElement(step: step, bound: 500L); }

        foreach (var element in elements) {
            var firstOk = algebra.TryFactorize(value: element, factorization: out var first, obstruction: out var firstObstruction);
            var secondOk = algebra.TryFactorize(value: element, factorization: out var second, obstruction: out var secondObstruction);

            if (firstOk != secondOk) { return $"({element.U},{element.V}): two passes disagreed on success"; }
            if (!firstOk) {
                if (firstObstruction != secondObstruction) { return $"({element.U},{element.V}): two passes reported different obstructions"; }

                continue;
            }

            if (!SameFactorization(left: first, right: second)) { return $"({element.U},{element.V}): two factorization passes diverged"; }
        }

        return null;
    }

    // ---- (f): the fixed-width fast tier vs an independent BigInteger reference, across three routing regimes ----
    public static string? FastTierRoutingSurface() {
        const long FastBoundLong = (1L << 41);

        (int P, int Q, int Delta)[] worlds = [(1, -1, -3), (0, -2, -8), (1, -41, -163), (0, -5, -20)];
        var cheapChecks = 0;

        foreach (var (p, q, delta) in worlds) {
            var algebra = Algebra.Create(p: p, q: q);
            var bp = (BigInteger)p;
            var bq = (BigInteger)q;

            for (var regime = 0; (regime < 3); ++regime) {
                for (var step = 0L; (step < 60L); ++step) {
                    var element = (regime switch {
                        0 => DeterministicElement(step: step, bound: 5_000L),
                        1 => new Element(
                            U: (((step & 1) == 0) ? DeterministicComponent(step: step, multiplier: 2654435761L, offset: 17L, bound: 5_000L) : ((FastBoundLong + DeterministicComponent(step: step, multiplier: 97L, offset: 3L, bound: 32L)) * ((0 == (step & 2)) ? 1 : -1))),
                            V: (((step & 4) == 0) ? DeterministicComponent(step: step, multiplier: 6364136223846793005L, offset: 11L, bound: 5_000L) : ((FastBoundLong + DeterministicComponent(step: step, multiplier: 131L, offset: 7L, bound: 32L)) * ((0 == (step & 8)) ? 1 : -1)))
                        ),
                        _ => new Element(
                            U: (((FastBoundLong * 2) + DeterministicComponent(step: step, multiplier: 251L, offset: 13L, bound: 1_000_000L)) * ((0 == (step & 1)) ? 1 : -1)),
                            V: (((FastBoundLong * 2) + DeterministicComponent(step: step, multiplier: 401L, offset: 19L, bound: 1_000_000L)) * ((0 == (step & 2)) ? 1 : -1))
                        ),
                    });

                    var referenceNorm = RefNorm(p: bp, q: bq, u: element.U, v: element.V);

                    if (algebra.Norm(value: element) != referenceNorm) { return $"Delta={delta} regime {regime}: norm mismatch for ({element.U},{element.V})"; }
                    if (algebra.IsUnit(value: element) != BigInteger.Abs(referenceNorm).IsOne) { return $"Delta={delta} regime {regime}: IsUnit mismatch for ({element.U},{element.V})"; }
                    if (algebra.IsPrimeElement(value: element) != RefIsPrimeElement(value: element, p: bp, q: bq)) { return $"Delta={delta} regime {regime}: IsPrimeElement mismatch for ({element.U},{element.V})"; }

                    ++cheapChecks;
                }
            }

            // The inert-square branch: an element of norm ell^2 with ell inert is prime exactly when it is an associate
            // of ell; random components almost never land here, so it is driven explicitly.
            var probePrimes = EnumerateSmallPrimesUpTo(ceiling: 60);
            var discriminant = ((bp * bp) + (4 * bq));

            foreach (var ell in probePrimes) {
                var rationalPrime = (BigInteger)ell;
                var inert = RefIsInert(discriminant: discriminant, rationalPrime: rationalPrime);

                foreach (var candidate in (Element[])[
                    new Element(U: rationalPrime, V: BigInteger.Zero),
                    new Element(U: -rationalPrime, V: BigInteger.Zero),
                    algebra.Multiply(left: new Element(U: rationalPrime, V: BigInteger.Zero), right: algebra.Root),
                ]) {
                    if (BigInteger.Abs(RefNorm(p: bp, q: bq, u: candidate.U, v: candidate.V)) != (rationalPrime * rationalPrime)) { continue; }

                    var expected = RefIsPrimeElement(value: candidate, p: bp, q: bq);

                    if (algebra.IsPrimeElement(value: candidate) != expected) { return $"Delta={delta}: IsPrimeElement mismatch on the {ell} square probe ({candidate.U},{candidate.V})"; }
                    if (expected != inert) { return $"Delta={delta}: ({candidate.U},{candidate.V}) of norm {ell}^2 judged {expected}, but the splitting character says inert={inert}"; }

                    ++cheapChecks;
                }
            }
        }

        if (cheapChecks < 500) { return "the fast-tier cheap-operation sweep visited too few operands to trust"; }

        // The divide-out loop, scaled across the routing boundary: a smooth base scaled by a power of two keeps the
        // factorization cheap while pushing components across or over the 2^41 seam.
        long[] scales = [1L, (1L << 37), (1L << 43)];
        var factorChecks = 0;

        foreach (var (p, q, delta) in worlds) {
            var algebra = Algebra.Create(p: p, q: q);
            var bp = (BigInteger)p;
            var bq = (BigInteger)q;

            foreach (var scale in scales) {
                var k = (BigInteger)scale;

                for (var step = 0L; (step < 20L); ++step) {
                    var baseElement = DeterministicElement(step: step, bound: 63L);
                    var element = new Element(U: (k * baseElement.U), V: (k * baseElement.V));
                    var referenceNorm = RefNorm(p: bp, q: bq, u: element.U, v: element.V);

                    if (BigInteger.Abs(referenceNorm) <= 1) { continue; }

                    var succeeded = algebra.TryFactorize(value: element, factorization: out var factorization, obstruction: out var obstruction);
                    var succeededAgain = algebra.TryFactorize(value: element, factorization: out var factorizationAgain, obstruction: out var obstructionAgain);

                    if ((succeeded != succeededAgain) || (obstruction != obstructionAgain)) { return $"Delta={delta} k={scale}: nondeterministic factorization of ({element.U},{element.V})"; }

                    if (succeeded) {
                        if (!SameFactorization(left: factorization, right: factorizationAgain)) { return $"Delta={delta} k={scale}: divergent repeated factorization of ({element.U},{element.V})"; }
                        if (Reassemble(algebra: algebra, factorization: factorization) != element) { return $"Delta={delta} k={scale}: reassembly mismatch for ({element.U},{element.V})"; }
                        if (!algebra.IsUnit(value: factorization.LeadingUnit)) { return $"Delta={delta} k={scale}: leading factor is not a unit"; }

                        var normProduct = BigInteger.Abs(RefNorm(p: bp, q: bq, u: factorization.LeadingUnit.U, v: factorization.LeadingUnit.V));

                        foreach (var factor in factorization.Factors) {
                            if (!algebra.IsPrimeElement(value: factor.Prime)) { return $"Delta={delta} k={scale}: non-prime factor"; }
                            if (algebra.CanonicalAssociate(value: factor.Prime) != factor.Prime) { return $"Delta={delta} k={scale}: non-canonical factor"; }

                            var factorNorm = BigInteger.Abs(RefNorm(p: bp, q: bq, u: factor.Prime.U, v: factor.Prime.V));

                            for (var power = 0; (power < factor.Multiplicity); ++power) { normProduct *= factorNorm; }
                        }

                        if (normProduct != BigInteger.Abs(referenceNorm)) { return $"Delta={delta} k={scale}: factor-norm product {normProduct} != reference |norm| {BigInteger.Abs(referenceNorm)}"; }
                    } else if (QuadraticSplitting.Inert == obstruction.Splitting) {
                        return $"Delta={delta} k={scale}: obstruction reported an inert prime, which is always principal";
                    }

                    ++factorChecks;
                }
            }
        }

        return ((factorChecks < 50) ? "the fast-tier divide-out sweep visited too few scaled factorizations to trust" : null);
    }

    // ---- (h1): FundamentalUnit over every real order in [5, 4000] vs the retired ascending-Y scan ----
    public static string? RealOrderFundamentalUnitVsRetiredScanSurface() {
        const int DiscriminantCeiling = 4_000;
        const int ScanBudget = 10_000;

        var realized = 0;
        var scanned = 0;

        for (var delta = 5; (delta <= DiscriminantCeiling); ++delta) {
            var residue = (delta & 3);

            if ((0 != residue) && (1 != residue)) { continue; }
            if (IsPerfectSquare(value: delta)) { continue; }

            var p = (delta & 1);
            var algebra = Algebra.Create(p: (BigInteger)p, q: (BigInteger)((delta - (p * p)) / 4));

            if (algebra.Discriminant != delta) { return $"({p},{(delta - (p * p)) / 4}) has Delta={algebra.Discriminant}, expected {delta}"; }

            var unit = algebra.FundamentalUnit();
            var unitX = ((2 * unit.U) + (p * unit.V));
            var unitY = unit.V;
            var certificate = ((unitX * unitX) - (delta * unitY * unitY));

            if (BigInteger.Abs(certificate) != 4) { return $"Delta={delta}: X^2-Delta*Y^2 = {certificate}, expected +-4"; }
            if (algebra.Norm(value: unit) != certificate.Sign) { return $"Delta={delta}: norm {algebra.Norm(value: unit)} disagrees with the certificate sign {certificate.Sign}"; }
            if ((unitX.Sign <= 0) || (unitY.Sign <= 0)) { return $"Delta={delta}: non-positive coordinate ({unitX},{unitY})"; }

            ++realized;

            if (unitY > ScanBudget) { continue; }

            if (!TryRetiredAscendingUnitScan(delta: delta, yCeiling: unitY, scanX: out var scanX, scanY: out var scanY, scanSign: out var scanSign)) {
                return $"Delta={delta}: the retired scan found no solution at or below Y={unitY}, so ({unitX},{unitY}) is not minimal";
            }
            if ((scanX != unitX) || (scanY != unitY) || (scanSign != certificate.Sign)) {
                return $"Delta={delta}: the retired scan's first hit ({scanX},{scanY}) sign {scanSign} != ({unitX},{unitY}) sign {certificate.Sign}";
            }

            ++scanned;
        }

        if (realized < 1000) { return "too few real orders were realized in [5, 4000] to trust the sweep"; }
        if (scanned < 500) { return "too few real orders fell inside the scan budget to trust the minimality cross-check"; }

        return null;
    }

    // ---- (h2) restructured + (h3) + (h5): the landmine, descriptor invariance, and invalid input ----
    public static string? LandmineAndDescriptorInvarianceSurface() {
        // The landmine: at these five discriminants the norm-minus-one +-4 unit is strictly smaller than the norm-one
        // Pell unit — no hard-coded coordinates here, only the two live computations compared against each other and
        // against their own defining equations.
        (int Delta, int P, int Q)[] landmine = [(5, 1, 1), (13, 1, 3), (61, 1, 15), (109, 1, 27), (181, 1, 45)];

        foreach (var (delta, p, q) in landmine) {
            var algebra = Algebra.Create(p: p, q: q);

            if (algebra.Discriminant != delta) { return $"(landmine) descriptor ({p},{q}) has Delta={algebra.Discriminant}, expected {delta}"; }

            var unit = algebra.FundamentalUnit();

            if (algebra.Norm(value: unit) != BigInteger.MinusOne) { return $"(landmine) Delta={delta}: norm {algebra.Norm(value: unit)}, expected -1 — the +-4 unit at this discriminant has norm minus one"; }

            var pell = PellEquation.FundamentalUnit(radicand: delta);

            if (((pell.X * pell.X) - ((BigInteger)delta * pell.Y * pell.Y)) != BigInteger.One) { return $"(landmine) Delta={delta}: PellEquation.FundamentalUnit's own answer fails X^2-Delta*Y^2=1"; }
            if (unit.V >= pell.Y) { return $"(landmine) Delta={delta}: Y4={unit.V} is not strictly below Y1={pell.Y}, so the landmine is not being demonstrated"; }
        }

        // Descriptor invariance: (1,1) and (3,-1) are both Delta = 5; the recovered (X, Y) is identical.
        {
            var first = Algebra.Create(p: BigInteger.One, q: BigInteger.One);
            var second = Algebra.Create(p: (BigInteger)3, q: BigInteger.MinusOne);

            if ((first.Discriminant != 5) || (second.Discriminant != 5)) { return "(h3) (1,1) and (3,-1) must both be Delta=5"; }

            var firstUnit = first.FundamentalUnit();
            var secondUnit = second.FundamentalUnit();
            var firstX = ((2 * firstUnit.U) + firstUnit.V);
            var secondX = ((2 * secondUnit.U) + (3 * secondUnit.V));

            if ((firstX != secondX) || (firstUnit.V != secondUnit.V)) { return $"(h3) the two Delta=5 descriptors recovered ({firstX},{firstUnit.V}) and ({secondX},{secondUnit.V})"; }
            if ((firstUnit.U - secondUnit.U) != (((3 - 1) / 2) * firstUnit.V)) { return "(h3) the scalar parts do not differ by (P'-P)/2*Y"; }
        }

        // Invalid input: the discriminants that looped forever before the nonsquare guard landed, and the Pell
        // radicands that must throw rather than hang.
        (int P, int Q, int Delta)[] rejected = [(0, -1, -4), (0, 0, 0), (0, 1, 4), (1, 2, 9), (0, 4, 16)];

        foreach (var (p, q, delta) in rejected) {
            var algebra = Algebra.Create(p: p, q: q);

            if (algebra.Discriminant != delta) { return $"(h5) descriptor ({p},{q}) has Delta={algebra.Discriminant}, expected {delta}"; }

            var refusal = Record.Exception(testCode: () => algebra.FundamentalUnit());

            if (refusal is not ArgumentException) { return $"(h5) Delta={delta}: FundamentalUnit threw {refusal?.GetType().ToString() ?? "nothing"} instead of ArgumentException"; }
        }

        foreach (var radicand in (BigInteger[])[BigInteger.Zero, (BigInteger)(-5), (BigInteger)49]) {
            var refusal = Record.Exception(testCode: () => PellEquation.FundamentalUnit(radicand: radicand));

            if (refusal is not ArgumentOutOfRangeException) { return $"(h5) D={radicand}: PellEquation.FundamentalUnit threw {refusal?.GetType().ToString() ?? "nothing"} instead of ArgumentOutOfRangeException"; }
        }

        return null;
    }

    // ---- (h6): PellEquation.FundamentalUnit vs a verbatim transcription of its own former convergent loop ----
    public static string? PellDelegationVsRetiredConvergentLoopSurface() {
        var agreements = 0;

        for (var radicand = 2; (radicand <= 3_000); ++radicand) {
            if (IsPerfectSquare(value: radicand)) { continue; }

            var delegated = PellEquation.FundamentalUnit(radicand: radicand);
            var (referenceX, referenceY) = RetiredPellConvergentLoop(radicand: radicand);

            if ((delegated.X != referenceX) || (delegated.Y != referenceY)) { return $"D={radicand}: delegated ({delegated.X},{delegated.Y}) != retired loop ({referenceX},{referenceY})"; }
            if (((delegated.X * delegated.X) - (radicand * delegated.Y * delegated.Y)) != BigInteger.One) { return $"D={radicand}: the delegated unit fails X^2-D*Y^2=1"; }

            ++agreements;
        }

        return ((agreements < 2_000) ? "too few nonsquare radicands agreed to trust the delegation sweep" : null);
    }

    // ---- (h4) restructured: the Delta=3964/D=991 audit reproduction and Delta=399964, without pinned literals ----
    public static string? AuditHangCompletesForcedSignSurface() {
        foreach (var (q, label) in ((BigInteger Q, string Label)[])[
            ((BigInteger)991, "D=991 (Delta=3964, the audit's former hang)"),
            ((BigInteger)99_991, "D=99991 (Delta=399964)"),
        ]) {
            var algebra = Algebra.Create(p: BigInteger.Zero, q: q);
            var discriminant = algebra.Discriminant;

            if (discriminant != (4 * q)) { return $"{label}: Delta={discriminant}, expected 4*D={4 * q} for a (P,Q)=(0,D) descriptor"; }

            // The forced-sign argument is about the order's own norm form a^2 - D*b^2 (Norm(unit) = Certificate/4), so it
            // is checked on D = q itself, never on Delta = 4*D, which is always 0 (mod 4) and would trip on nothing.
            if ((((q % 4) + 4) % 4) != 3) { return $"{label}: D={q} is not 3 (mod 4), so the forced-sign argument below does not apply to it"; }
            if (SomeResidueIsNegativeOneModFour(d: q)) { return $"{label}: some (x,y) makes x^2-D*y^2 = -1 (mod 4), so the forced-sign argument is wrong"; }

            var worker = System.Threading.Tasks.Task.Run(function: () => algebra.FundamentalUnit());

            if (!worker.Wait(timeout: TimeSpan.FromSeconds(20))) { return $"{label}: FundamentalUnit did not complete inside the bounded wait"; }

            var unit = worker.Result;
            var unitX = (2 * unit.U);
            var certificate = ((unitX * unitX) - (discriminant * unit.V * unit.V));

            if (BigInteger.Abs(certificate) != 4) { return $"{label}: X^2-Delta*Y^2 = {certificate}, expected +-4"; }

            var norm = algebra.Norm(value: unit);

            if (BigInteger.Abs(norm) != BigInteger.One) { return $"{label}: norm {norm} is not +-1"; }
            if (norm != BigInteger.One) { return $"{label}: norm {norm}, but the mod-4 argument forces +1"; }

            foreach (var shifted in (BigInteger[])[(unitX + 2), (unitX - 2)]) {
                if (IsPerfectSquare(value: shifted)) { return $"{label}: X{((shifted > unitX) ? "+" : "-")}2 is a perfect square, so the answer could be a unit's square"; }
            }
        }

        return null;
    }

    // ---- (i1): the real-order prime-norm existence decision vs the retired orbit box ----
    public static string? RealOrderPrimeNormExistenceVsRetiredOrbitBoxSurface() {
        const int DiscriminantCeiling = 200;
        const int PrimeCeiling = 60;
        const long BoxBudget = 40_000L;

        var agreements = 0;
        var decisions = 0;
        var primes = EnumerateSmallPrimesUpTo(ceiling: PrimeCeiling);

        for (var delta = 5; (delta <= DiscriminantCeiling); ++delta) {
            var residue = (delta & 3);

            if ((0 != residue) && (1 != residue)) { continue; }
            if (IsPerfectSquare(value: delta)) { continue; }

            var p = (delta & 1);
            var algebra = Algebra.Create(p: (BigInteger)p, q: (BigInteger)((delta - (p * p)) / 4));

            foreach (var ell in primes) {
                var rationalPrime = (BigInteger)ell;
                var splitting = algebra.SplittingCharacter(rationalPrime: rationalPrime);

                if (QuadraticSplitting.Inert == splitting) { continue; }

                var square = new Element(U: rationalPrime, V: BigInteger.Zero);
                var succeeded = algebra.TryFactorize(value: square, factorization: out var factorization, obstruction: out var obstruction);

                if (succeeded) {
                    if (Reassemble(algebra: algebra, factorization: factorization) != square) { return $"Delta={delta} ell={ell}: reassembly mismatch"; }
                    if (!factorization.Factors.Any(predicate: factor => (BigInteger.Abs(algebra.Norm(value: factor.Prime)) == rationalPrime))) { return $"Delta={delta} ell={ell}: factored without producing a prime of norm +-ell"; }
                } else {
                    if (obstruction.RationalPrime != rationalPrime) { return $"Delta={delta} ell={ell}: obstruction names {obstruction.RationalPrime}"; }
                    if (obstruction.Splitting != splitting) { return $"Delta={delta} ell={ell}: obstruction reports {obstruction.Splitting}, expected {splitting}"; }
                }

                ++decisions;

                var oracle = RetiredOrbitBoxHasNormElement(discriminant: delta, rationalPrime: rationalPrime, budget: BoxBudget);

                if (oracle is null) { continue; }
                if (oracle.Value != succeeded) { return $"Delta={delta} ell={ell}: the walk says {succeeded}, the retired orbit box says {oracle.Value}"; }

                ++agreements;
            }
        }

        if (decisions < 100) { return "too few principal/non-principal decisions to trust the sweep"; }
        if (agreements < 50) { return "too few retired-orbit-box agreements to trust the cross-check"; }

        return null;
    }

    // ---- (i2) + (i3): factorizations beyond the orbit box's reach, with the expected norm computed independently ----
    public static string? RealOrderFactorizationBeyondOrbitBoxSurface() {
        {
            var order = Algebra.Create(p: BigInteger.Zero, q: (BigInteger)991);
            var element = new Element(U: 15, V: 2);
            var expectedNorm = RefNorm(p: BigInteger.Zero, q: 991, u: 15, v: 2);
            var norm = order.Norm(value: element);

            if (norm != expectedNorm) { return $"D=991: N(15,2) = {norm}, expected the independently recomputed {expectedNorm}"; }

            var magnitude = BigInteger.Abs(expectedNorm);

            if (!IsProbablePrimeBig(value: magnitude)) { return $"D=991: |N(15,2)| = {magnitude} is expected to be a rational prime"; }
            if (QuadraticSplitting.Inert == order.SplittingCharacter(rationalPrime: magnitude)) { return "D=991: the rational prime above N(15,2) must be non-inert, or the generator search is never entered"; }

            var worker = System.Threading.Tasks.Task.Run(function: () => order.TryFactorize(value: element, factorization: out var factorization, obstruction: out _)
                ? factorization
                : throw new InvalidOperationException(message: "(15,2) failed to factor, but it generates its own prime ideal"));

            if (!worker.Wait(timeout: TimeSpan.FromSeconds(20))) { return "D=991: TryFactorize(15,2) did not complete inside the bounded wait"; }

            var result = worker.Result;

            if (Reassemble(algebra: order, factorization: result) != element) { return "D=991: reassembly mismatch"; }
            if (1 != result.Factors.Count) { return $"D=991: {result.Factors.Count} factors, expected 1"; }
            if (BigInteger.Abs(order.Norm(value: result.Factors[0].Prime)) != magnitude) { return "D=991: the single factor does not carry the expected norm magnitude"; }
        }

        {
            var order = Algebra.Create(p: BigInteger.Zero, q: (BigInteger)99_991);
            var element = new Element(U: 401, V: 3);
            var expectedMagnitude = BigInteger.Abs(RefNorm(p: BigInteger.Zero, q: 99_991, u: 401, v: 3));
            var worker = System.Threading.Tasks.Task.Run(function: () => order.TryFactorize(value: element, factorization: out var factorization, obstruction: out _)
                ? factorization
                : throw new InvalidOperationException(message: "(401,3) failed to factor"));

            if (!worker.Wait(timeout: TimeSpan.FromSeconds(20))) { return "D=99991: TryFactorize(401,3) did not complete inside the bounded wait"; }

            var result = worker.Result;

            if (Reassemble(algebra: order, factorization: result) != element) { return "D=99991: reassembly mismatch"; }
            if (!order.IsUnit(value: result.LeadingUnit)) { return "D=99991: leading factor is not a unit"; }

            var normProduct = BigInteger.Abs(order.Norm(value: result.LeadingUnit));

            foreach (var factor in result.Factors) {
                var factorNorm = BigInteger.Abs(order.Norm(value: factor.Prime));

                for (var power = 0; (power < factor.Multiplicity); ++power) { normProduct *= factorNorm; }
            }

            if (normProduct != expectedMagnitude) { return $"D=99991: factor-norm product {normProduct} != independently recomputed |norm| {expectedMagnitude}"; }
        }

        return null;
    }
}
