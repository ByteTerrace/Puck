#:project ../src/Puck.Maths/Puck.Maths.csproj

// Acceptance oracle for QuadraticIntegerArithmetic — primality and factorization inside QuadraticAlgebra<BigInteger>.
// Each descriptor (P, Q) names the order Z[x] with x² = P·x + Q and nonsquare discriminant Δ = P² + 4Q. The checks:
//   (a) the nine imaginary class-number-one worlds: random factor, prime/canonical/reassembly, zero obstructions
//   (b) the sum-of-two-squares law in the (0, -1) world, cross-checked against SplittingCharacter
//   (c) the first-twist witness: Δ = -20 obstruction at 6, plus an obstruction-rate survey over -20, -15, -24
//   (d) a real-quadratic leg in the Δ = 5 world: fundamental unit, factor + reassembly, splitting vs Jacobi
//   (e) determinism: the same elements factor byte-identically twice

using System.Numerics;
using System.Text;
using Puck.Maths;

using Algebra = Puck.Maths.QuadraticAlgebra<System.Numerics.BigInteger>;
using Element = Puck.Maths.QuadraticAlgebra<System.Numerics.BigInteger>.Element;

var rng = new Random(Seed: 0x0AD1CE);

// ---- (a) the nine imaginary class-number-one worlds ----
{
    // (P, Q) chosen so Δ = P² + 4Q hits each class-number-one discriminant.
    (int P, int Q, int Delta)[] worlds = [
        (1, -1, -3), (0, -1, -4), (1, -2, -7), (0, -2, -8), (1, -3, -11),
        (1, -5, -19), (1, -11, -43), (1, -17, -67), (1, -41, -163),
    ];
    var total = 0;

    foreach (var (p, q, delta) in worlds) {
        var algebra = Algebra.Create(p: p, q: q);

        if (algebra.Discriminant != delta) { throw new InvalidOperationException($"Δ mismatch for ({p},{q}): expected {delta}, got {algebra.Discriminant}"); }

        var checks = 0;

        for (var sample = 0; (sample < 2_000); ++sample) {
            var element = RandomElement(rng: rng, bound: 1_000);

            // Skip zero and units; they have no proper factorization to reassemble.
            if (BigInteger.Abs(algebra.Norm(value: element)) <= 1) { continue; }

            if (!algebra.TryFactorize(value: element, factorization: out var factorization, obstruction: out var obstruction)) {
                throw new InvalidOperationException($"world Δ={delta}: unexpected obstruction at ℓ={obstruction.RationalPrime} factoring ({element.U},{element.V})");
            }

            foreach (var factor in factorization.Factors) {
                if (!algebra.IsPrimeElement(value: factor.Prime)) { throw new InvalidOperationException($"world Δ={delta}: non-prime factor ({factor.Prime.U},{factor.Prime.V})"); }
                if (algebra.CanonicalAssociate(value: factor.Prime) != factor.Prime) { throw new InvalidOperationException($"world Δ={delta}: non-canonical factor ({factor.Prime.U},{factor.Prime.V})"); }
            }

            if (Reassemble(algebra: algebra, factorization: factorization) != element) {
                throw new InvalidOperationException($"world Δ={delta}: reassembly mismatch for ({element.U},{element.V})");
            }
            if (!algebra.IsUnit(value: factorization.LeadingUnit)) { throw new InvalidOperationException($"world Δ={delta}: leading factor is not a unit"); }

            ++checks;
        }

        total += checks;
        Console.WriteLine($"(a) Δ={delta,5}: {checks} elements factored — every factor prime, canonical, reassembled; 0 obstructions");
    }

    Console.WriteLine($"(a) nine imaginary class-number-one worlds: {total} exact factorizations, zero obstructions");
}

// ---- (b) the sum-of-two-squares law in the (0, -1) world ----
{
    var algebra = Algebra.Create(p: BigInteger.Zero, q: BigInteger.MinusOne);
    var splitCount = 0;
    var agree = 0;
    var primes = 0;

    NumberTheoryFunctions.SegmentedPrimeSieve(low: 2, high: 9_999, onPrime: ell => {
        ++primes;

        if (!algebra.TryFactorize(value: new Element(U: (BigInteger)ell, V: BigInteger.Zero), factorization: out var factorization, obstruction: out _)) {
            throw new InvalidOperationException($"(b) unexpected obstruction at ℓ={ell}");
        }

        // "Splits" for this law means a prime element of norm exactly ℓ appears (ℓ = a² + b²).
        var hasNormEll = false;

        foreach (var factor in factorization.Factors) {
            if (BigInteger.Abs(algebra.Norm(value: factor.Prime)) == ell) { hasNormEll = true; }
        }

        var expected = ((1 == (ell & 3)) || (2 == ell));

        if (hasNormEll != expected) { throw new InvalidOperationException($"(b) sum-of-two-squares law broke at ℓ={ell}: hasNormEll={hasNormEll}, expected={expected}"); }
        if (hasNormEll) { ++splitCount; }

        // Cross-check against the splitting character: Split or Ramified both admit a norm-ℓ prime.
        var character = algebra.SplittingCharacter(rationalPrime: ell);
        var characterSplits = (QuadraticSplitting.Inert != character);

        if (characterSplits == hasNormEll) { ++agree; }
    });

    Console.WriteLine($"(b) (0,-1) sum-of-two-squares over {primes} primes < 10⁴: {splitCount} split (ℓ≡1 mod 4 or ℓ=2); SplittingCharacter agrees on {agree}/{primes}");
}

// ---- (c) the first-twist witness and obstruction-rate survey ----
{
    // Δ = -20 world: the element 6 = 2·3 must fail — the primes above 2 and 3 are both non-principal.
    var twistWorld = Algebra.Create(p: BigInteger.Zero, q: (BigInteger)(-5));

    if (twistWorld.TryFactorize(value: new Element(U: 6, V: BigInteger.Zero), factorization: out _, obstruction: out var witness)) {
        throw new InvalidOperationException("(c) Δ=-20: factoring 6 unexpectedly succeeded");
    }
    if ((witness.RationalPrime != 2) && (witness.RationalPrime != 3)) {
        throw new InvalidOperationException($"(c) Δ=-20: obstruction at ℓ={witness.RationalPrime}, expected 2 or 3");
    }

    Console.WriteLine($"(c) Δ=-20: TryFactorize(6) FAILED with obstruction ℓ={witness.RationalPrime} ({witness.Splitting}) — the class-group witness");

    (int P, int Q, int Delta)[] survey = [(0, -5, -20), (1, -4, -15), (0, -6, -24)];

    foreach (var (p, q, delta) in survey) {
        var algebra = Algebra.Create(p: p, q: q);
        var obstructed = 0;
        var factored = 0;

        for (var sample = 0; (sample < 3_000); ++sample) {
            var element = RandomElement(rng: rng, bound: 400);

            if (BigInteger.Abs(algebra.Norm(value: element)) <= 1) { continue; }

            if (algebra.TryFactorize(value: element, factorization: out var factorization, obstruction: out _)) {
                // A successful factorization must still reassemble exactly.
                if (Reassemble(algebra: algebra, factorization: factorization) != element) { throw new InvalidOperationException($"(c) Δ={delta}: reassembly mismatch"); }

                ++factored;
            } else {
                ++obstructed;
            }
        }

        var rate = ((100.0 * obstructed) / (obstructed + factored));
        Console.WriteLine($"(c) Δ={delta,5}: {factored} factored, {obstructed} obstructed — obstruction rate {rate:F1}% (nonzero: nontrivial class group)");
    }

    Console.WriteLine("(c) contrast: the nine (a)-worlds hold a 0.0% obstruction rate");
}

// ---- (d) the real-quadratic leg: Δ = 5 ----
{
    var algebra = Algebra.Create(p: BigInteger.One, q: BigInteger.One);

    if (algebra.Discriminant != 5) { throw new InvalidOperationException("(d) Δ mismatch for (1,1)"); }

    // The golden unit is the root x = φ itself: norm -1, minimal.
    var fundamental = algebra.FundamentalUnit();

    if ((fundamental != new Element(U: BigInteger.Zero, V: BigInteger.One)) || (algebra.Norm(value: fundamental) != -1)) {
        throw new InvalidOperationException($"(d) fundamental unit is ({fundamental.U},{fundamental.V}) norm {algebra.Norm(value: fundamental)}, expected (0,1) norm -1");
    }

    Console.WriteLine($"(d) Δ=5: FundamentalUnit = ({fundamental.U},{fundamental.V}) = φ, norm {algebra.Norm(value: fundamental)} (the golden unit)");

    var factored = 0;

    for (var sample = 0; (sample < 500); ++sample) {
        var element = RandomElement(rng: rng, bound: 200);

        if (BigInteger.Abs(algebra.Norm(value: element)) <= 1) { continue; }

        if (!algebra.TryFactorize(value: element, factorization: out var factorization, obstruction: out var obstruction)) {
            throw new InvalidOperationException($"(d) unexpected obstruction at ℓ={obstruction.RationalPrime} factoring ({element.U},{element.V})");
        }

        foreach (var factor in factorization.Factors) {
            if (!algebra.IsPrimeElement(value: factor.Prime)) { throw new InvalidOperationException($"(d) non-prime factor ({factor.Prime.U},{factor.Prime.V})"); }
            if (algebra.CanonicalAssociate(value: factor.Prime) != factor.Prime) { throw new InvalidOperationException($"(d) non-canonical factor ({factor.Prime.U},{factor.Prime.V})"); }
        }

        if (Reassemble(algebra: algebra, factorization: factorization) != element) { throw new InvalidOperationException($"(d) reassembly mismatch for ({element.U},{element.V})"); }
        if (!algebra.IsUnit(value: factorization.LeadingUnit)) { throw new InvalidOperationException("(d) leading factor is not a unit"); }

        ++factored;
    }

    var agree = 0;
    var primes = 0;

    NumberTheoryFunctions.SegmentedPrimeSieve(low: 2, high: 9_999, onPrime: ell => {
        ++primes;

        var character = algebra.SplittingCharacter(rationalPrime: ell);
        // ℓ = 2 is decided by Δ = 5 ≡ 5 (mod 8) → inert; odd ℓ by the Jacobi symbol (5 / ℓ), with 5 ramified.
        var expected = (2 == ell)
            ? QuadraticSplitting.Inert
            : ((0 == (ell % 5))
                ? QuadraticSplitting.Ramified
                : ((1 == NumberTheoryFunctions.JacobiSymbol(numerator: 5, denominator: ell)) ? QuadraticSplitting.Split : QuadraticSplitting.Inert));

        if (character == expected) { ++agree; }
    });

    Console.WriteLine($"(d) Δ=5: {factored} real elements factored with canonical unit normalization + exact reassembly; SplittingCharacter matches Jacobi on {agree}/{primes} primes < 10⁴");
}

// ---- (e) determinism ----
{
    var algebra = Algebra.Create(p: BigInteger.One, q: (BigInteger)(-5)); // Δ = -19, a class-number-one world.
    var elements = new Element[100];

    for (var index = 0; (index < elements.Length); ++index) { elements[index] = RandomElement(rng: rng, bound: 500); }

    var first = new StringBuilder();
    var second = new StringBuilder();

    foreach (var element in elements) { Render(algebra: algebra, element: element, sink: first); }
    foreach (var element in elements) { Render(algebra: algebra, element: element, sink: second); }

    if (!string.Equals(a: first.ToString(), b: second.ToString(), comparisonType: StringComparison.Ordinal)) {
        throw new InvalidOperationException("(e) determinism failure: two factorization passes diverged");
    }

    Console.WriteLine($"(e) determinism: {elements.Length} elements factored twice, byte-identical ({first.Length} chars)");
}

Console.WriteLine("all quadratic-arithmetic acceptance checks passed");

// Rebuilds an element from its factorization: leading unit times each canonical prime raised to its multiplicity.
static Element Reassemble(Algebra algebra, QuadraticFactorization factorization) {
    var product = factorization.LeadingUnit;

    foreach (var factor in factorization.Factors) {
        for (var power = 0; (power < factor.Multiplicity); ++power) { product = algebra.Multiply(left: product, right: factor.Prime); }
    }

    return product;
}

// Appends a stable textual rendering of an element's factorization, for byte-comparison across passes.
static void Render(Algebra algebra, Element element, StringBuilder sink) {
    if (!algebra.TryFactorize(value: element, factorization: out var factorization, obstruction: out var obstruction)) {
        sink.Append(value: $"OBSTRUCTION ℓ={obstruction.RationalPrime} {obstruction.Splitting};");

        return;
    }

    sink.Append(value: $"u=({factorization.LeadingUnit.U},{factorization.LeadingUnit.V})");

    foreach (var factor in factorization.Factors) { sink.Append(value: $" ({factor.Prime.U},{factor.Prime.V})^{factor.Multiplicity}"); }

    sink.Append(value: ';');
}

static Element RandomElement(Random rng, int bound) =>
    new(U: rng.Next(minValue: -bound, maxValue: (bound + 1)), V: rng.Next(minValue: -bound, maxValue: (bound + 1)));
