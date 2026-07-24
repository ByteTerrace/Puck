#:project ../src/Puck.Maths/Puck.Maths.csproj

// Acceptance oracle for DoublingAlgebra<TInner> — the doubling construction that builds each rung of the real
// division-algebra ladder from ordered pairs of the rung beneath it. It absorbs FixedComplex and FixedQuaternion into
// one recursive family and continues one floor above them into the octonions.
//
// The product convention proved here is  (a,b)·(c,d) = (a·c − Conj(d)·b, d·a + b·Conj(c))  with  Conj(a,b) = (Conj(a), −b).
// It is DERIVED against the house Hamilton oracle, not assumed: under (w,x,y,z) ↦ ((w,x),(y,z)) the floor-two double
// reproduces FixedQuaternion component-for-component.
//
//   (a) FLOOR 1: Double(scalar) reproduces FixedComplex, bit-identical over the 8-fractional-bit exact sublattice.
//   (b) FLOOR 2: Double(Double(scalar)) reproduces FixedQuaternion. Bit-identical over the exact sublattice; off the
//       sublattice FixedQuaternion's fused four-product accumulation and the pairing's per-product rounding diverge
//       (evidenced), so exact algebraic agreement is proved over a BigInteger carrier instead.
//   (c) FLOOR 3: the octonion floor over BigInteger — norm multiplicativity, alternativity (associator vanishes when
//       two arguments coincide) with a concrete non-associativity witness, and the full price ladder floors 0..3.
//   (d) DETERMINISM: byte-identical on repeat.
//
// Rounding note for (a)/(b): FixedComplex and FixedQuaternion accumulate their raw products and round the SUM once,
// while the carrier-agnostic doubling rounds each scalar product then adds. The two coincide exactly when every
// pairwise product is representable without rounding, so the fixed-point checks draw operands from the sublattice
// (raw a multiple of 256): each product is then exact in Q16 and the comparison isolates the ALGEBRA from rounding.

using System.Numerics;
using Puck.Maths;

using Scalar = Puck.Maths.FixedScalarRing;
using Floor1 = Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>;
using Floor2 = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>>;

using BScalar = BigIntegerRing;
using BFloor1 = Puck.Maths.DoublingAlgebra<BigIntegerRing>;
using BFloor2 = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<BigIntegerRing>>;
using BFloor3 = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<BigIntegerRing>>>;

var rng = new Random(0x2B10C);

// ---- (a) FLOOR 1: DoublingAlgebra<FixedScalarRing> reproduces FixedComplex, bit-identical ----
{
    var checks = 0;

    for (var sample = 0; (sample < 10_000); ++sample) {
        var a = new FixedComplex(Real: ExactFixed(rng), Imaginary: ExactFixed(rng));
        var b = new FixedComplex(Real: ExactFixed(rng), Imaginary: ExactFixed(rng));
        var eA = ToFloor1(a);
        var eB = ToFloor1(b);

        RequireFloor1(Floor1.Add(left: eA, right: eB), (a + b), "a.add");
        RequireFloor1(Floor1.Subtract(left: eA, right: eB), (a - b), "a.sub");
        RequireFloor1(Floor1.Multiply(left: eA, right: eB), (a * b), "a.mul");
        RequireFloor1(Floor1.Negate(value: eA), -a, "a.neg");
        RequireFloor1(Floor1.Conjugate(value: eA), a.Conjugate(), "a.conj");

        // Norm lands in the base scalar directly at floor 1: a·ā = |a|².
        if (Floor1.Norm(value: eA).Value.Value != a.MagnitudeSquared.Value) {
            throw new InvalidOperationException("a.norm mismatch");
        }

        ++checks;
    }

    Console.WriteLine($"(a) FLOOR 1  Double(scalar) == FixedComplex: {checks} pairs bit-identical (add/sub/mul/neg/conj/norm)");
}

// ---- (b) FLOOR 2: DoublingAlgebra<DoublingAlgebra<FixedScalarRing>> reproduces FixedQuaternion ----
{
    var checks = 0;

    for (var sample = 0; (sample < 10_000); ++sample) {
        var qa = new FixedQuaternion(X: ExactFixed(rng), Y: ExactFixed(rng), Z: ExactFixed(rng), W: ExactFixed(rng));
        var qb = new FixedQuaternion(X: ExactFixed(rng), Y: ExactFixed(rng), Z: ExactFixed(rng), W: ExactFixed(rng));
        var eA = ToFloor2(qa);
        var eB = ToFloor2(qb);

        RequireFloor2(Floor2.Add(left: eA, right: eB), (qa + qb), "b.add");
        RequireFloor2(Floor2.Subtract(left: eA, right: eB), (qa - qb), "b.sub");
        RequireFloor2(Floor2.Multiply(left: eA, right: eB), (qa * qb), "b.mul");
        RequireFloor2(Floor2.Negate(value: eA), -qa, "b.neg");
        RequireFloor2(Floor2.Conjugate(value: eA), qa.Conjugate(), "b.conj");

        // Norm sits one floor down as a real embedded in the complex plane (imaginary part zero); project through
        // Left to the base scalar and match FixedQuaternion.LengthSquared (exact on the sublattice).
        var norm = Floor2.Norm(value: eA);

        if ((norm.Right.Value.Value != 0L) || (norm.Left.Value.Value != qa.LengthSquared.Value)) {
            throw new InvalidOperationException("b.norm mismatch");
        }

        ++checks;
    }

    Console.WriteLine($"(b) FLOOR 2  Double(Double(scalar)) == FixedQuaternion: {checks} pairs bit-identical over the exact sublattice (mul/conj/norm)");

    // Evidence of the fused-accumulation boundary: off the sublattice, FixedQuaternion rounds each component's four
    // products as one sum, while the pairing rounds each scalar product before adding. They must diverge.
    var divergences = 0;

    for (var sample = 0; (sample < 20_000); ++sample) {
        var qa = new FixedQuaternion(X: RoughFixed(rng), Y: RoughFixed(rng), Z: RoughFixed(rng), W: RoughFixed(rng));
        var qb = new FixedQuaternion(X: RoughFixed(rng), Y: RoughFixed(rng), Z: RoughFixed(rng), W: RoughFixed(rng));
        var fused = (qa * qb);
        var paired = FromFloor2(Floor2.Multiply(left: ToFloor2(qa), right: ToFloor2(qb)));

        if (fused != paired) { ++divergences; }
    }

    if (divergences == 0) {
        throw new InvalidOperationException("expected fused-vs-per-product divergence off the sublattice");
    }

    Console.WriteLine($"    fused-accumulation boundary confirmed: {divergences}/20000 off-sublattice products differ (fused sum-round vs per-product round)");

    // The required fallback: exact algebraic agreement over a BigInteger carrier, where no rounding occurs. The
    // doubling product must equal the Hamilton product component-for-component.
    var exact = 0;

    for (var sample = 0; (sample < 10_000); ++sample) {
        var la = RandomQuaternionComponents(rng);
        var lb = RandomQuaternionComponents(rng);
        var product = Components(BFloor2.Multiply(left: BQuaternion(la), right: BQuaternion(lb)));
        var reference = HamiltonReference(la, lb);

        for (var c = 0; (c < 4); ++c) {
            if (product[c] != reference[c]) { throw new InvalidOperationException("b.exact Hamilton mismatch"); }
        }

        ++exact;
    }

    Console.WriteLine($"    BigInteger carrier: {exact} pairs — doubling product == Hamilton product exactly (no rounding)");
}

// ---- (c) FLOOR 3: the octonion floor over BigInteger, plus the full price ladder ----
{
    var normChecks = 0;
    var alternativeChecks = 0;
    var nonAssociativeWitnesses = 0;
    BigInteger[]? associatorWitness = null;
    BigInteger[]? witnessA = null;
    BigInteger[]? witnessB = null;
    BigInteger[]? witnessC = null;

    for (var sample = 0; (sample < 2_000); ++sample) {
        var a = RandomOctonionComponents(rng);
        var b = RandomOctonionComponents(rng);
        var c = RandomOctonionComponents(rng);
        var eA = BOctonion(a);
        var eB = BOctonion(b);
        var eC = BOctonion(c);

        // Norm multiplicativity N(ab) = N(a)·N(b) as base scalars (sum of the eight squares).
        var product = BFloor3.Multiply(left: eA, right: eB);

        if (OctonionNorm(Components3(product)) != (OctonionNorm(a) * OctonionNorm(b))) {
            throw new InvalidOperationException("c.norm multiplicativity mismatch");
        }

        // The library Norm must agree, projected through Left three floors to the base scalar.
        if (ProjectNorm(BFloor3.Norm(value: eA)) != OctonionNorm(a)) {
            throw new InvalidOperationException("c.norm projection mismatch");
        }

        ++normChecks;

        // Alternativity: the associator vanishes whenever any two arguments coincide.
        RequireZero3(BFloor3.Associator(left: eA, middle: eA, right: eB), "c.alt.aab");
        RequireZero3(BFloor3.Associator(left: eA, middle: eB, right: eA), "c.alt.aba");
        RequireZero3(BFloor3.Associator(left: eB, middle: eA, right: eA), "c.alt.baa");
        RequireZero3(BFloor3.Associator(left: eA, middle: eB, right: eB), "c.alt.abb");
        RequireZero3(BFloor3.Associator(left: eB, middle: eA, right: eB), "c.alt.bab");
        RequireZero3(BFloor3.Associator(left: eB, middle: eB, right: eA), "c.alt.bba");
        ++alternativeChecks;

        // Non-associativity: a generic triple has a nonzero associator.
        var associator = Components3(BFloor3.Associator(left: eA, middle: eB, right: eC));

        if (!IsZero(associator)) {
            ++nonAssociativeWitnesses;

            if (associatorWitness is null) {
                associatorWitness = associator;
                witnessA = a;
                witnessB = b;
                witnessC = c;
            }
        }
    }

    if (nonAssociativeWitnesses == 0) {
        throw new InvalidOperationException("expected a non-associativity witness at the octonion floor");
    }

    Console.WriteLine($"(c) FLOOR 3  octonions over BigInteger: {normChecks} triples — N(ab)=N(a)N(b); {alternativeChecks} alternativity checks (6 coinciding-argument patterns each) vanish; {nonAssociativeWitnesses}/2000 triples give a nonzero associator");
    Console.WriteLine($"    non-associativity witness |assoc(a,b,c)| first nonzero component: {FirstNonzero(associatorWitness!)}");

    // ---- the full price ladder, over the exact BigInteger carrier ----
    var ladderChecks = 0;

    for (var sample = 0; (sample < 4_000); ++sample) {
        // Floor 0 (ℤ): commutative + associative.
        var s0 = new BScalar(RandomBig(rng));
        var s1 = new BScalar(RandomBig(rng));
        var s2 = new BScalar(RandomBig(rng));

        Require(BScalar.Multiply(left: s0, right: s1).Value == BScalar.Multiply(left: s1, right: s0).Value, "ladder.f0.commute");
        Require(
            BScalar.Multiply(left: BScalar.Multiply(left: s0, right: s1), right: s2).Value ==
            BScalar.Multiply(left: s0, right: BScalar.Multiply(left: s1, right: s2)).Value,
            "ladder.f0.assoc");

        // Floor 1 (ℤ[i], complex): commutative + associative — both witnesses zero.
        var c0 = BComplex(RandomBig(rng), RandomBig(rng));
        var c1 = BComplex(RandomBig(rng), RandomBig(rng));
        var c2 = BComplex(RandomBig(rng), RandomBig(rng));

        RequireZero1(BFloor1.Commutator(left: c0, right: c1), "ladder.f1.commute");
        RequireZero1(BFloor1.Associator(left: c0, middle: c1, right: c2), "ladder.f1.assoc");

        // Floor 2 (Lipschitz quaternions): associative, NOT commutative — both witnesses present.
        var q0 = BQuaternion(RandomQuaternionComponents(rng));
        var q1 = BQuaternion(RandomQuaternionComponents(rng));
        var q2 = BQuaternion(RandomQuaternionComponents(rng));

        RequireZero2(BFloor2.Associator(left: q0, middle: q1, right: q2), "ladder.f2.assoc");

        ++ladderChecks;
    }

    // Floor 2 non-commutativity is a structural fact — exhibit the canonical i·j ≠ j·i witness explicitly.
    var qi = BQuaternion([BigInteger.Zero, BigInteger.One, BigInteger.Zero, BigInteger.Zero]);
    var qj = BQuaternion([BigInteger.Zero, BigInteger.Zero, BigInteger.One, BigInteger.Zero]);
    var f2Commutator = Components(BFloor2.Commutator(left: qi, right: qj));

    if (IsZero(f2Commutator)) {
        throw new InvalidOperationException("expected nonzero floor-2 commutator (i·j − j·i)");
    }

    Console.WriteLine($"    price ladder ({ladderChecks} samples): f0 commutative+associative; f1 commutative+associative; f2 associative but NOT commutative ([i,j] = 2k, component z = {f2Commutator[3]}); f3 alternative but NOT associative");
}

// ---- (d) DETERMINISM: byte-identical repeat ----
{
    var first = ReplayDigest();
    var second = ReplayDigest();

    if (first != second) {
        throw new InvalidOperationException($"determinism mismatch: {first:X16} != {second:X16}");
    }

    Console.WriteLine($"(d) DETERMINISM: byte-identical replay digest 0x{first:X16}");
}

Console.WriteLine("all doubling-algebra acceptance checks passed");

// ---------------------------------------------------------------------------------------------------------------
// Operand generators.

// 8-fractional-bit sublattice: every pairwise product is exact in Q16, so per-product and fused rounding coincide.
static FixedQ4816 ExactFixed(Random rng) =>
    FixedQ4816.FromRawBits(value: ((long)rng.Next(minValue: -2000, maxValue: 2001) * 256));
// Off-lattice, moderate magnitude (no overflow): products carry nonzero low bits, so rounding disciplines differ.
static FixedQ4816 RoughFixed(Random rng) =>
    FixedQ4816.FromRawBits(value: rng.NextInt64(minValue: -70000L, maxValue: 70001L));
static BigInteger RandomBig(Random rng) =>
    new(value: rng.Next(minValue: -1000, maxValue: 1001));

static BigInteger[] RandomQuaternionComponents(Random rng) =>
    [RandomBig(rng), RandomBig(rng), RandomBig(rng), RandomBig(rng)];
static BigInteger[] RandomOctonionComponents(Random rng) =>
    [RandomBig(rng), RandomBig(rng), RandomBig(rng), RandomBig(rng), RandomBig(rng), RandomBig(rng), RandomBig(rng), RandomBig(rng)];

// ---------------------------------------------------------------------------------------------------------------
// Fixed-point tower construction / extraction (mapping (w,x,y,z) ↦ ((w,x),(y,z))).

static Floor1 ToFloor1(FixedComplex value) =>
    new(Left: new Scalar(Value: value.Real), Right: new Scalar(Value: value.Imaginary));
static Floor2 ToFloor2(FixedQuaternion value) =>
    new(
        Left: new Floor1(Left: new Scalar(Value: value.W), Right: new Scalar(Value: value.X)),
        Right: new Floor1(Left: new Scalar(Value: value.Y), Right: new Scalar(Value: value.Z))
    );
static FixedQuaternion FromFloor2(Floor2 value) =>
    new(X: value.Left.Right.Value, Y: value.Right.Left.Value, Z: value.Right.Right.Value, W: value.Left.Left.Value);

// ---------------------------------------------------------------------------------------------------------------
// BigInteger tower construction / extraction.

static BFloor1 BComplex(BigInteger real, BigInteger imaginary) =>
    new(Left: new BScalar(Value: real), Right: new BScalar(Value: imaginary));
static BFloor2 BQuaternion(BigInteger[] c) =>
    new(Left: BComplex(c[0], c[1]), Right: BComplex(c[2], c[3]));
static BFloor3 BOctonion(BigInteger[] c) =>
    new(Left: BQuaternion([c[0], c[1], c[2], c[3]]), Right: BQuaternion([c[4], c[5], c[6], c[7]]));

static BigInteger[] Components(BFloor2 q) =>
    [q.Left.Left.Value, q.Left.Right.Value, q.Right.Left.Value, q.Right.Right.Value];
static BigInteger[] Components3(BFloor3 o) => [
    o.Left.Left.Left.Value, o.Left.Left.Right.Value, o.Left.Right.Left.Value, o.Left.Right.Right.Value,
    o.Right.Left.Left.Value, o.Right.Left.Right.Value, o.Right.Right.Left.Value, o.Right.Right.Right.Value,
];

static BigInteger OctonionNorm(BigInteger[] c) {
    var sum = BigInteger.Zero;

    for (var i = 0; (i < c.Length); ++i) { sum += (c[i] * c[i]); }

    return sum;
}
// The floor-3 norm sits two floors down as a real embedded in the quaternions; project through Left to the scalar.
static BigInteger ProjectNorm(BFloor2 norm) =>
    norm.Left.Left.Value;

// The independent Hamilton reference for the exact floor-2 agreement, under (w,x,y,z) = (c0,c1,c2,c3).
static BigInteger[] HamiltonReference(BigInteger[] l, BigInteger[] r) {
    var (w1, x1, y1, z1) = (l[0], l[1], l[2], l[3]);
    var (w2, x2, y2, z2) = (r[0], r[1], r[2], r[3]);

    return [
        (((w1 * w2) - (x1 * x2)) - (y1 * y2)) - (z1 * z2),
        (((w1 * x2) + (x1 * w2)) + (y1 * z2)) - (z1 * y2),
        (((w1 * y2) - (x1 * z2)) + (y1 * w2)) + (z1 * x2),
        (((w1 * z2) + (x1 * y2)) - (y1 * x2)) + (z1 * w2),
    ];
}

// ---------------------------------------------------------------------------------------------------------------
// The determinism replay: fold a fixed batch of octonion products and norms into one digest.

static ulong ReplayDigest() {
    var rng = new Random(0x0C7A5);
    var hash = Fnv1aHash.Create();

    for (var sample = 0; (sample < 500); ++sample) {
        var a = new BigInteger[8];
        var b = new BigInteger[8];

        for (var i = 0; (i < 8); ++i) { a[i] = new BigInteger(rng.Next(minValue: -1000, maxValue: 1001)); }
        for (var i = 0; (i < 8); ++i) { b[i] = new BigInteger(rng.Next(minValue: -1000, maxValue: 1001)); }

        var product = BFloor3.Multiply(left: BOctonion(a), right: BOctonion(b));
        var components = Components3(product);

        for (var i = 0; (i < components.Length); ++i) { FoldBig(ref hash, components[i]); }

        FoldBig(ref hash, OctonionNorm(components));
    }

    return hash.Value;
}

static void FoldBig(ref Fnv1aHash hash, BigInteger value) {
    var negative = (value.Sign < 0);
    var magnitude = (negative ? -value : value);

    hash.Add(value: (negative ? (byte)1 : (byte)0));

    foreach (var b in magnitude.ToByteArray()) { hash.Add(value: b); }

    hash.Add(value: (byte)0xFF);
}

// ---------------------------------------------------------------------------------------------------------------
// Assertions.

static void Require(bool condition, string label) {
    if (!condition) { throw new InvalidOperationException($"{label} mismatch"); }
}

static void RequireFloor1(Floor1 actual, FixedComplex expected, string label) {
    if ((actual.Left.Value.Value != expected.Real.Value) || (actual.Right.Value.Value != expected.Imaginary.Value)) {
        throw new InvalidOperationException($"{label} mismatch");
    }
}

static void RequireFloor2(Floor2 actual, FixedQuaternion expected, string label) {
    var q = FromFloor2(actual);

    if ((q.X.Value != expected.X.Value) || (q.Y.Value != expected.Y.Value) ||
        (q.Z.Value != expected.Z.Value) || (q.W.Value != expected.W.Value)) {
        throw new InvalidOperationException($"{label} mismatch");
    }
}

static bool IsZero(BigInteger[] components) {
    for (var i = 0; (i < components.Length); ++i) {
        if (components[i] != BigInteger.Zero) { return false; }
    }

    return true;
}

static BigInteger FirstNonzero(BigInteger[] components) {
    for (var i = 0; (i < components.Length); ++i) {
        if (components[i] != BigInteger.Zero) { return components[i]; }
    }

    return BigInteger.Zero;
}

static void RequireZero1(BFloor1 value, string label) {
    if ((value.Left.Value != BigInteger.Zero) || (value.Right.Value != BigInteger.Zero)) {
        throw new InvalidOperationException($"{label} not zero");
    }
}

static void RequireZero2(BFloor2 value, string label) {
    if (!IsZero(Components(value))) { throw new InvalidOperationException($"{label} not zero"); }
}

static void RequireZero3(BFloor3 value, string label) {
    if (!IsZero(Components3(value))) { throw new InvalidOperationException($"{label} not zero"); }
}

// ---------------------------------------------------------------------------------------------------------------
// The floor-zero exact carrier: BigInteger presented as a conjugation ring (conjugation is the identity).

internal readonly record struct BigIntegerRing(BigInteger Value)
    : IConjugationRing<BigIntegerRing> {
    public static BigIntegerRing AdditiveIdentity => new(Value: BigInteger.Zero);
    public static BigIntegerRing MultiplicativeIdentity => new(Value: BigInteger.One);

    public static BigIntegerRing Add(BigIntegerRing left, BigIntegerRing right) => new(Value: (left.Value + right.Value));
    public static BigIntegerRing Conjugate(BigIntegerRing value) => value;
    public static BigIntegerRing Multiply(BigIntegerRing left, BigIntegerRing right) => new(Value: (left.Value * right.Value));
    public static BigIntegerRing Negate(BigIntegerRing value) => new(Value: -value.Value);
    public static BigIntegerRing Subtract(BigIntegerRing left, BigIntegerRing right) => new(Value: (left.Value - right.Value));
}
