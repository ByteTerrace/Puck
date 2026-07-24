#:project ../src/Puck.Maths/Puck.Maths.csproj

// Acceptance oracle for MonogenicAlgebra<TScalar>. The any-degree adjunction frees the degree that QuadraticAlgebra
// fixes at two: degree 2 IS QuadraticAlgebra, and degree k over the two-element carrier IS the BinaryField tower. This
// verifier proves both unifications by exact comparison, then opens degree-3 ground:
//   (a) DEGREE-2 REGRESSION: modulus x² − P·x − Q (tail [−Q, −P]) reproduces QuadraticAlgebra<BigInteger> bit-for-bit.
//   (b) CHAR-2 TOWER:        the degree-8 canonical byte-field modulus reproduces BinaryField<byte>, BigInteger-free.
//   (c) DEGREE-3 NEW GROUND: x³ − x − 1 (the plastic ratio world, discriminant −23) — recurrence, norm, trace, disc.
//   (d) DETERMINISM:         two independent passes agree on a rolling digest of every computed value.

using System.Numerics;
using Puck.Maths;

var (digest, summary) = RunAll(report: true);

foreach (var line in summary) { Console.WriteLine(value: line); }

var (repeatDigest, _) = RunAll(report: false);

if (digest != repeatDigest) { throw new InvalidOperationException("(d) determinism: the two passes disagree."); }

Console.WriteLine(value: $"(d) determinism: two passes agree, digest {digest}");
Console.WriteLine(value: "all monogenic-algebra acceptance checks passed");

static (string Digest, List<string> Summary) RunAll(bool report) {
    var summary = new List<string>();
    var digest = BigInteger.Zero;

    void Mix(BigInteger value) {
        // A rolling hash mod 2^127 − 1, so a whole pass folds to one fixed-width residue regardless of value magnitude.
        var prime = ((BigInteger.One << 127) - BigInteger.One);

        digest = (((digest * 1099511628211) + (((value % prime) + prime) % prime)) % prime);
    }

    // ---- (a) degree-2 regression: MonogenicAlgebra tail [−Q, −P] == QuadraticAlgebra (P, Q) ----
    {
        var rng = new Random(Seed: 0x30D_E62);
        var checks = 0;

        for (var trial = 0; (trial < 10_000); ++trial) {
            var p = RandomBig(rng: rng);
            var q = RandomBig(rng: rng);
            var quad = QuadraticAlgebra<BigInteger>.Create(p: p, q: q);
            var mono = MonogenicAlgebra<BigInteger>.Create(monicModulus: (BigInteger[])[-q, -p]);

            var aU = RandomBig(rng: rng);
            var aV = RandomBig(rng: rng);
            var bU = RandomBig(rng: rng);
            var bV = RandomBig(rng: rng);
            var qa = new QuadraticAlgebra<BigInteger>.Element(U: aU, V: aV);
            var qb = new QuadraticAlgebra<BigInteger>.Element(U: bU, V: bV);
            var ma = mono.FromCoordinates(coordinates: (BigInteger[])[aU, aV]);
            var mb = mono.FromCoordinates(coordinates: (BigInteger[])[bU, bV]);

            RequireQuad(actual: mono.Add(left: ma, right: mb), expected: quad.Add(left: qa, right: qb), label: "a.add");
            RequireQuad(actual: mono.Subtract(left: ma, right: mb), expected: quad.Subtract(left: qa, right: qb), label: "a.sub");
            RequireQuad(actual: mono.Negate(value: ma), expected: quad.Negate(value: qa), label: "a.neg");
            RequireQuad(actual: mono.Multiply(left: ma, right: mb), expected: quad.Multiply(left: qa, right: qb), label: "a.mul");

            var monoNorm = mono.Norm(value: ma);
            var monoTrace = mono.Trace(value: ma);

            Require(condition: (monoNorm == quad.Norm(value: qa)), label: "a.norm");
            Require(condition: (monoTrace == quad.Trace(value: qa)), label: "a.trace");
            Require(condition: (mono.CharacteristicDiscriminant() == quad.Discriminant), label: "a.disc");

            var exponent = (ulong)rng.Next(minValue: 0, maxValue: 24);

            RequireQuad(actual: mono.CompanionPower(exponent: exponent), expected: quad.CompanionPower(exponent: exponent), label: "a.power");

            var monoStep = mono.ProjectiveStep(window: mono.FromWindow(window: (BigInteger[])[aU, aV]));
            var quadStep = quad.MobiusStep(pair: new QuadraticAlgebra<BigInteger>.Projective(Numerator: aU, Denominator: aV));

            Require(condition: ((monoStep[0] == quadStep.Numerator) && (monoStep[1] == quadStep.Denominator)), label: "a.step");

            Mix(value: monoNorm);
            Mix(value: monoTrace);
            Mix(value: mono.Multiply(left: ma, right: mb)[0]);

            ++checks;
        }

        if (report) { summary.Add(item: $"(a) QuadraticAlgebra == degree-2 MonogenicAlgebra: {checks} op sequences bit-identical (add/sub/neg/mul/norm/trace/disc/power/step)"); }
    }

    // ---- (b) char-2 tower: degree-8 canonical byte-field modulus over GF(2) == BinaryField<byte> ----
    {
        var rng = new Random(Seed: 0x0B17F1E);
        var field = BinaryFields.Degree8;
        // Tail of t^8 + t^4 + t^3 + t + 1 as one-bit carrier coordinates, low exponent first.
        var tail = new Gf2[8];

        for (var index = 0; (index < 8); ++index) { tail[index] = new Gf2(Bit: (byte)((0x1B >> index) & 1)); }

        var mono = MonogenicAlgebra<Gf2>.Create(monicModulus: tail);
        var checks = 0;

        for (var trial = 0; (trial < 10_000); ++trial) {
            var a = (byte)rng.Next(minValue: 0, maxValue: 256);
            var b = (byte)rng.Next(minValue: 0, maxValue: 256);
            var ma = ToElement(algebra: mono, value: a);
            var mb = ToElement(algebra: mono, value: b);

            var sum = ToByte(element: mono.Add(left: ma, right: mb));
            var product = ToByte(element: mono.Multiply(left: ma, right: mb));
            var shifted = ToByte(element: mono.MultiplyByRoot(value: ma));

            Require(condition: (sum == field.Add(left: a, right: b)), label: "b.add");
            Require(condition: (product == field.Multiply(left: a, right: b)), label: "b.mul");
            // BinaryField's carrier-shift: multiply by t, packed as 0x02.
            Require(condition: (shifted == field.Multiply(left: a, right: 0x02)), label: "b.root");

            Mix(value: sum);
            Mix(value: product);
            Mix(value: shifted);

            ++checks;
        }

        if (report) { summary.Add(item: $"(b) BinaryField<byte> == degree-8 MonogenicAlgebra over GF(2): {checks} op sequences exact (add/mul/multiply-by-root)"); }
    }

    // ---- (c) degree-3 new ground: x³ − x − 1, the plastic ratio world ----
    {
        var rng = new Random(Seed: 0x2317ACE);
        var mono = MonogenicAlgebra<BigInteger>.Create(monicModulus: (BigInteger[])[-BigInteger.One, -BigInteger.One, BigInteger.Zero]);

        // CompanionPower reproduces the order-3 recurrence a(n) = a(n−2) + a(n−3), element-wise, for 60 terms.
        var powers = new MonogenicAlgebra<BigInteger>.Element[61];

        for (var n = 0; (n <= 60); ++n) { powers[n] = mono.CompanionPower(exponent: (ulong)n); }

        var recurrenceTerms = 0;

        for (var n = 3; (n <= 60); ++n) {
            for (var coordinate = 0; (coordinate < 3); ++coordinate) {
                if (powers[n][coordinate] != (powers[n - 2][coordinate] + powers[n - 3][coordinate])) {
                    throw new InvalidOperationException($"(c) recurrence broke at n={n}, coordinate={coordinate}");
                }
            }

            Mix(value: powers[n][2]);
            ++recurrenceTerms;
        }

        // Discriminant of x³ − x − 1 is −23 (exercises the size-5 Sylvester determinant through Berkowitz).
        Require(condition: (mono.CharacteristicDiscriminant() == -23), label: "c.disc");

        var normPairs = 0;

        for (var trial = 0; (trial < 2_000); ++trial) {
            var a = mono.FromCoordinates(coordinates: (BigInteger[])[RandomBig(rng: rng), RandomBig(rng: rng), RandomBig(rng: rng)]);
            var b = mono.FromCoordinates(coordinates: (BigInteger[])[RandomBig(rng: rng), RandomBig(rng: rng), RandomBig(rng: rng)]);

            var normProduct = mono.Norm(value: mono.Multiply(left: a, right: b));
            var traceSum = mono.Trace(value: mono.Add(left: a, right: b));

            Require(condition: (normProduct == (mono.Norm(value: a) * mono.Norm(value: b))), label: "c.norm");
            Require(condition: (traceSum == (mono.Trace(value: a) + mono.Trace(value: b))), label: "c.trace");

            Mix(value: normProduct);
            Mix(value: traceSum);

            ++normPairs;
        }

        if (report) {
            summary.Add(item: $"(c) x³ − x − 1 plastic-ratio world: {recurrenceTerms} recurrence terms a(n)=a(n−2)+a(n−3), discriminant −23, {normPairs} norm-multiplicative + trace-additive pairs");
        }
    }

    return (Digest: digest.ToString(), Summary: summary);
}

static BigInteger RandomBig(Random rng) =>
    new(value: rng.Next(minValue: -1000, maxValue: 1001));

static void Require(bool condition, string label) {
    if (!condition) { throw new InvalidOperationException($"{label} mismatch"); }
}

static void RequireQuad(MonogenicAlgebra<BigInteger>.Element actual, QuadraticAlgebra<BigInteger>.Element expected, string label) {
    if ((actual[0] != expected.U) || (actual[1] != expected.V)) {
        throw new InvalidOperationException($"{label} mismatch: ({actual[0]},{actual[1]}) != ({expected.U},{expected.V})");
    }
}

static MonogenicAlgebra<Gf2>.Element ToElement(MonogenicAlgebra<Gf2> algebra, byte value) {
    var coordinates = new Gf2[8];

    for (var index = 0; (index < 8); ++index) { coordinates[index] = new Gf2(Bit: (byte)((value >> index) & 1)); }

    return algebra.FromCoordinates(coordinates: coordinates);
}

static byte ToByte(MonogenicAlgebra<Gf2>.Element element) {
    var value = 0;

    for (var index = 0; (index < 8); ++index) { value |= (element[index].Bit << index); }

    return (byte)value;
}

// The two-element carrier as a one-bit struct: addition and subtraction are exclusive or, multiplication is conjunction,
// negation is the identity. It satisfies exactly the six operator interfaces MonogenicAlgebra requires — the same set
// that admits BigInteger — which is the whole of the char-2 unification: the degree-k tower is this carrier substituted.
internal readonly record struct Gf2(byte Bit)
    : IAdditionOperators<Gf2, Gf2, Gf2>,
      ISubtractionOperators<Gf2, Gf2, Gf2>,
      IMultiplyOperators<Gf2, Gf2, Gf2>,
      IUnaryNegationOperators<Gf2, Gf2>,
      IAdditiveIdentity<Gf2, Gf2>,
      IMultiplicativeIdentity<Gf2, Gf2> {
    static Gf2 IAdditiveIdentity<Gf2, Gf2>.AdditiveIdentity => new(Bit: 0);
    static Gf2 IMultiplicativeIdentity<Gf2, Gf2>.MultiplicativeIdentity => new(Bit: 1);

    public static Gf2 operator +(Gf2 left, Gf2 right) => new(Bit: (byte)(left.Bit ^ right.Bit));
    public static Gf2 operator -(Gf2 left, Gf2 right) => new(Bit: (byte)(left.Bit ^ right.Bit));
    public static Gf2 operator *(Gf2 left, Gf2 right) => new(Bit: (byte)(left.Bit & right.Bit));
    public static Gf2 operator -(Gf2 value) => value;
}
