#:project ../src/Puck.Maths/Puck.Maths.csproj

// Acceptance oracle for QuadraticAlgebra<TScalar> and FixedSplit. The unifying generic adjoins a root of x² = P·x + Q to
// a carrier ring; this verifier proves by exact comparison that it reproduces every hand-written planar number system:
//   (a) (0, -1) over FixedQ4816  == FixedComplex        (b) (0, 0) over FixedQ4816 == FixedDual<FixedQ4816>
//   (c) (k, 1) over BigInteger   == QuadraticSurd in Q(√Δ) under x = (k + √Δ)/2, Δ = k² + 4
//   (d) (0, d) over F_p          == QuadraticExtensionField64 (d = smallest non-square)
//   (e) (0, +1) over FixedQ4816  == FixedSplit           (f) CompanionPower(k, 1) == the k-metallic recurrence
//
// Rounding note for (a) and (e): FixedComplex and FixedSplit accumulate two raw Q32 products and round the SUM once,
// whereas the carrier-agnostic generic rounds each product then adds. The two disciplines coincide exactly when every
// pairwise product is representable without rounding, so those two checks draw operands from the 8-fractional-bit
// sublattice (raw a multiple of 256): each product is then exact in Q16 and the comparison isolates the ALGEBRA from the
// carrier's rounding policy. (b) and (d) round per product like the generic, so they run over the full operand range.

using System.Numerics;
using Puck.Maths;

var rng = new Random(0x5140A);

// ---- (a) (0, -1) over FixedQ4816 reproduces FixedComplex, bit-identical ----
{
    var alg = QuadraticAlgebra<FixedQ4816>.Create(p: FixedQ4816.Zero, q: FixedQ4816.NegativeOne);
    var checks = 0;

    for (var sample = 0; (sample < 10_000); ++sample) {
        var a = new FixedComplex(Real: ExactFixed(rng), Imaginary: ExactFixed(rng));
        var b = new FixedComplex(Real: ExactFixed(rng), Imaginary: ExactFixed(rng));
        var eA = new QuadraticAlgebra<FixedQ4816>.Element(U: a.Real, V: a.Imaginary);
        var eB = new QuadraticAlgebra<FixedQ4816>.Element(U: b.Real, V: b.Imaginary);

        RequireComplex(alg.Add(left: eA, right: eB), (a + b), "a.add");
        RequireComplex(alg.Subtract(left: eA, right: eB), (a - b), "a.sub");
        RequireComplex(alg.Multiply(left: eA, right: eB), (a * b), "a.mul");
        RequireComplex(alg.Negate(value: eA), -a, "a.neg");
        RequireComplex(alg.Conjugate(value: eA), a.Conjugate(), "a.conj");

        ++checks;
    }

    Console.WriteLine($"(a) FixedComplex == (0,-1)/FixedQ4816: {checks} pairs bit-identical (add/sub/mul/neg/conj)");
}

// ---- (b) (0, 0) over FixedQ4816 reproduces FixedDual, bit-identical over the full range ----
{
    var alg = QuadraticAlgebra<FixedQ4816>.Create(p: FixedQ4816.Zero, q: FixedQ4816.Zero);
    var checks = 0;

    for (var sample = 0; (sample < 10_000); ++sample) {
        var a = new FixedDual<FixedQ4816>(Real: WideFixed(rng), Dual: WideFixed(rng));
        var b = new FixedDual<FixedQ4816>(Real: WideFixed(rng), Dual: WideFixed(rng));
        var eA = new QuadraticAlgebra<FixedQ4816>.Element(U: a.Real, V: a.Dual);
        var eB = new QuadraticAlgebra<FixedQ4816>.Element(U: b.Real, V: b.Dual);

        RequireFixed(alg.Add(left: eA, right: eB), ((a + b).Real, (a + b).Dual), "b.add");
        RequireFixed(alg.Subtract(left: eA, right: eB), ((a - b).Real, (a - b).Dual), "b.sub");
        RequireFixed(alg.Multiply(left: eA, right: eB), ((a * b).Real, (a * b).Dual), "b.mul");
        RequireFixed(alg.Negate(value: eA), ((-a).Real, (-a).Dual), "b.neg");

        ++checks;
    }

    Console.WriteLine($"(b) FixedDual == (0,0)/FixedQ4816: {checks} pairs bit-identical (add/sub/mul/neg)");
}

// ---- (c) (k, 1) over BigInteger reproduces QuadraticSurd in Q(√(k²+4)) under x = (k + √Δ)/2 ----
{
    var checks = 0;

    foreach (var k in (int[])[1, 2, 3, 5]) {
        var alg = QuadraticAlgebra<BigInteger>.Create(p: k, q: BigInteger.One);
        var radicand = ((BigInteger)k * k) + 4; // Δ = k² + 4, a non-square for each chosen k.

        // Basis change x = (k + √Δ)/2:  U + V·x = ((2U + Vk) + V·√Δ) / 2.
        QuadraticSurd ToSurd(QuadraticAlgebra<BigInteger>.Element e) =>
            QuadraticSurd.Create(rationalNumerator: ((2 * e.U) + (e.V * k)), surdNumerator: e.V, radicand: radicand, denominator: 2);

        for (var sample = 0; (sample < 2_500); ++sample) {
            var eA = new QuadraticAlgebra<BigInteger>.Element(U: RandomBig(rng), V: RandomBig(rng));
            var eB = new QuadraticAlgebra<BigInteger>.Element(U: RandomBig(rng), V: RandomBig(rng));
            var sA = ToSurd(eA);
            var sB = ToSurd(eB);
            var conjA = QuadraticSurd.Create(rationalNumerator: sA.RationalNumerator, surdNumerator: -sA.SurdNumerator, radicand: sA.Radicand, denominator: sA.Denominator);

            Require(ToSurd(alg.Add(left: eA, right: eB)) == (sA + sB), "c.add");
            Require(ToSurd(alg.Subtract(left: eA, right: eB)) == (sA - sB), "c.sub");
            Require(ToSurd(alg.Multiply(left: eA, right: eB)) == (sA * sB), "c.mul");
            Require(ToSurd(alg.Conjugate(value: eA)) == conjA, "c.conj");
            Require(QuadraticSurd.Rational(value: alg.Norm(value: eA)) == (sA * conjA), "c.norm");
            Require(QuadraticSurd.Rational(value: alg.Trace(value: eA)) == (sA + conjA), "c.trace");

            ++checks;
        }

        // CompanionPower matches repeated multiplication of the metallic mean x itself.
        var running = QuadraticSurd.One;
        var x = ToSurd(alg.Root);

        for (var n = 0; (n <= 40); ++n) {
            Require(ToSurd(alg.CompanionPower(exponent: (ulong)n)) == running, "c.power");
            running *= x;
        }
    }

    Console.WriteLine($"(c) QuadraticSurd == (k,1)/BigInteger over k∈{{1,2,3,5}}: {checks} pairs + companion powers exact");
}

// ---- (d) (0, d) over F_p reproduces QuadraticExtensionField64 (d = smallest non-square) ----
{
    var field = PrimeField64.Create(modulus: 1_000_000_007UL);
    var extension = QuadraticExtensionField64.CreateCanonical(baseField: field);
    var p = field.Modulus;
    var d = extension.NonSquare;
    var alg = QuadraticAlgebra<ModP>.Create(p: new ModP(Value: 0UL, Modulus: p), q: new ModP(Value: d, Modulus: p));
    var checks = 0;

    for (var sample = 0; (sample < 10_000); ++sample) {
        var xA = new QuadraticExtensionField64.Element(A: RandomResidue(rng, p), B: RandomResidue(rng, p));
        var xB = new QuadraticExtensionField64.Element(A: RandomResidue(rng, p), B: RandomResidue(rng, p));
        var eA = new QuadraticAlgebra<ModP>.Element(U: new ModP(Value: xA.A, Modulus: p), V: new ModP(Value: xA.B, Modulus: p));
        var eB = new QuadraticAlgebra<ModP>.Element(U: new ModP(Value: xB.A, Modulus: p), V: new ModP(Value: xB.B, Modulus: p));

        RequireExt(alg.Add(left: eA, right: eB), extension.Add(left: xA, right: xB), "d.add");
        RequireExt(alg.Subtract(left: eA, right: eB), extension.Subtract(left: xA, right: xB), "d.sub");
        RequireExt(alg.Multiply(left: eA, right: eB), extension.Multiply(left: xA, right: xB), "d.mul");
        RequireExt(alg.Negate(value: eA), extension.Negate(value: xA), "d.neg");
        RequireExt(alg.Conjugate(value: eA), extension.Frobenius(value: xA), "d.conj");
        Require(alg.Norm(value: eA).Value == extension.Norm(value: xA), "d.norm");
        Require(alg.Trace(value: eA).Value == extension.Trace(value: xA), "d.trace");

        ++checks;
    }

    Console.WriteLine($"(d) QuadraticExtensionField64 == (0,{d})/F_{p}: {checks} pairs exact (add/sub/mul/neg/conj/norm/trace)");
}

// ---- (e) (0, +1) over FixedQ4816 reproduces FixedSplit, bit-identical ----
{
    var alg = QuadraticAlgebra<FixedQ4816>.Create(p: FixedQ4816.Zero, q: FixedQ4816.One);
    var checks = 0;

    for (var sample = 0; (sample < 10_000); ++sample) {
        var a = new FixedSplit(U: ExactFixed(rng), V: ExactFixed(rng));
        var b = new FixedSplit(U: ExactFixed(rng), V: ExactFixed(rng));
        var eA = new QuadraticAlgebra<FixedQ4816>.Element(U: a.U, V: a.V);
        var eB = new QuadraticAlgebra<FixedQ4816>.Element(U: b.U, V: b.V);

        RequireFixed(alg.Add(left: eA, right: eB), ((a + b).U, (a + b).V), "e.add");
        RequireFixed(alg.Subtract(left: eA, right: eB), ((a - b).U, (a - b).V), "e.sub");
        RequireFixed(alg.Multiply(left: eA, right: eB), ((a * b).U, (a * b).V), "e.mul");
        RequireFixed(alg.Negate(value: eA), ((-a).U, (-a).V), "e.neg");
        RequireFixed(alg.Conjugate(value: eA), (a.Conjugate().U, a.Conjugate().V), "e.conj");

        if (alg.Norm(value: eA).Value != a.Norm.Value) { throw new InvalidOperationException("e.norm mismatch"); }

        ++checks;
    }

    Console.WriteLine($"(e) FixedSplit == (0,+1)/FixedQ4816: {checks} pairs bit-identical (add/sub/mul/neg/conj/norm)");
}

// ---- (f) CompanionPower(k, 1) reproduces the k-metallic recurrence ----
{
    var fibChecks = 0;
    var recurrenceChecks = 0;

    foreach (var k in (int[])[1, 2, 3]) {
        var alg = QuadraticAlgebra<BigInteger>.Create(p: k, q: BigInteger.One);
        var terms = new BigInteger[51];

        for (var n = 0; (n <= 50); ++n) { terms[n] = alg.CompanionPower(exponent: (ulong)n).V; }

        if ((terms[0] != BigInteger.Zero) || (terms[1] != BigInteger.One)) {
            throw new InvalidOperationException($"metallic seed wrong for k={k}");
        }

        for (var n = 1; (n < 50); ++n) {
            if (terms[n + 1] != ((k * terms[n]) + terms[n - 1])) {
                throw new InvalidOperationException($"metallic recurrence broke at k={k}, n={n}");
            }

            // x^n = Q·a_{n-1} + a_n·x, so the scalar part equals the previous term (Q = 1).
            if (alg.CompanionPower(exponent: (ulong)n).U != terms[n - 1]) {
                throw new InvalidOperationException($"companion scalar part wrong at k={k}, n={n}");
            }

            ++recurrenceChecks;
        }
    }

    // k = 1 is the classic additive sequence 1, 1, 2, 3, 5, 8, ...
    var golden = QuadraticAlgebra<BigInteger>.Create(p: BigInteger.One, q: BigInteger.One);
    BigInteger previous = 0;
    BigInteger current = 1;

    for (var n = 1; (n <= 50); ++n) {
        if (golden.CompanionPower(exponent: (ulong)n).V != current) {
            throw new InvalidOperationException($"Fibonacci term {n} mismatch");
        }

        (previous, current) = (current, (previous + current));
        ++fibChecks;
    }

    Console.WriteLine($"(f) CompanionPower metallic recurrence: {recurrenceChecks} recurrence steps + {fibChecks} Fibonacci terms verified");
}

Console.WriteLine("all quadratic-algebra acceptance checks passed");

// Operands on the 8-fractional-bit sublattice: every pairwise product is exact in Q16.
static FixedQ4816 ExactFixed(Random rng) =>
    FixedQ4816.FromRawBits(value: ((long)rng.Next(minValue: -2000, maxValue: 2001) * 256));
// Full-range operands for the per-product-rounding checks.
static FixedQ4816 WideFixed(Random rng) =>
    FixedQ4816.FromRawBits(value: rng.NextInt64(minValue: -(1L << 44), maxValue: (1L << 44)));
static BigInteger RandomBig(Random rng) =>
    new(value: rng.Next(minValue: -1000, maxValue: 1001));
static ulong RandomResidue(Random rng, ulong modulus) =>
    ((ulong)rng.NextInt64(minValue: 0L, maxValue: (long)modulus));

static void Require(bool condition, string label) {
    if (!condition) { throw new InvalidOperationException($"{label} mismatch"); }
}

static void RequireFixed(QuadraticAlgebra<FixedQ4816>.Element actual, (FixedQ4816 U, FixedQ4816 V) expected, string label) {
    if ((actual.U.Value != expected.U.Value) || (actual.V.Value != expected.V.Value)) {
        throw new InvalidOperationException($"{label} mismatch: ({actual.U.Value},{actual.V.Value}) != ({expected.U.Value},{expected.V.Value})");
    }
}

static void RequireComplex(QuadraticAlgebra<FixedQ4816>.Element actual, FixedComplex expected, string label) =>
    RequireFixed(actual: actual, expected: (expected.Real, expected.Imaginary), label: label);

static void RequireExt(QuadraticAlgebra<ModP>.Element actual, QuadraticExtensionField64.Element expected, string label) {
    if ((actual.U.Value != expected.A) || (actual.V.Value != expected.B)) {
        throw new InvalidOperationException($"{label} mismatch: ({actual.U.Value},{actual.V.Value}) != ({expected.A},{expected.B})");
    }
}

// A residue in F_p carried as a value plus its modulus, so the generic-math operators can reduce without a static
// modulus. The identity elements carry modulus zero; every binary operation adopts the operative (non-zero) modulus of
// its operands, which is p for every real element in the check above.
internal readonly record struct ModP(ulong Value, ulong Modulus)
    : IAdditionOperators<ModP, ModP, ModP>,
      ISubtractionOperators<ModP, ModP, ModP>,
      IMultiplyOperators<ModP, ModP, ModP>,
      IUnaryNegationOperators<ModP, ModP>,
      IAdditiveIdentity<ModP, ModP>,
      IMultiplicativeIdentity<ModP, ModP> {
    static ModP IAdditiveIdentity<ModP, ModP>.AdditiveIdentity => new(Value: 0UL, Modulus: 0UL);
    static ModP IMultiplicativeIdentity<ModP, ModP>.MultiplicativeIdentity => new(Value: 1UL, Modulus: 0UL);

    public static ModP operator +(ModP left, ModP right) {
        var modulus = Operative(left: left, right: right);

        if (0UL == modulus) { return new(Value: unchecked(left.Value + right.Value), Modulus: 0UL); }

        var sum = (left.Value + right.Value);

        return new(Value: ((sum >= modulus) ? (sum - modulus) : sum), Modulus: modulus);
    }
    public static ModP operator -(ModP left, ModP right) {
        var modulus = Operative(left: left, right: right);

        if (0UL == modulus) { return new(Value: unchecked(left.Value - right.Value), Modulus: 0UL); }

        return new(Value: ((left.Value >= right.Value) ? (left.Value - right.Value) : ((left.Value + modulus) - right.Value)), Modulus: modulus);
    }
    public static ModP operator *(ModP left, ModP right) {
        var modulus = Operative(left: left, right: right);

        if (0UL == modulus) { return new(Value: unchecked(left.Value * right.Value), Modulus: 0UL); }

        return new(Value: ((ulong)(((UInt128)left.Value * right.Value) % modulus)), Modulus: modulus);
    }
    public static ModP operator -(ModP value) {
        if ((0UL == value.Modulus) || (0UL == value.Value)) { return new(Value: unchecked(0UL - value.Value), Modulus: value.Modulus); }

        return new(Value: (value.Modulus - value.Value), Modulus: value.Modulus);
    }

    private static ulong Operative(ModP left, ModP right) =>
        Math.Max(val1: left.Modulus, val2: right.Modulus);
}
