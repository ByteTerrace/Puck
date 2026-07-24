#:project ../src/Puck.Maths/Puck.Maths.csproj

// Acceptance oracle for GeometricAlgebra — the signature-parameterized geometric (Clifford) algebra over FixedQ4816.
// It proves by exact comparison and measured precision that freeing the generator count reproduces the whole transform
// stack:
//   (a) ONE GENERATOR reproduces the planar trio: (0,1,0)==FixedComplex, (1,0,0)==FixedSplit, (0,0,1)==FixedDual.
//   (b) the EVEN subalgebra of (3,0,0) reproduces FixedQuaternion multiplication, bit-identical.
//   (c) MOTORS of (3,0,1) reproduce rigid motion: the motor sandwich matches FixedRigidTransform, and motor
//       composition matches transform composition.
//   (d) STRUCTURE: generator anticommutation, signature squares, geometric-product associativity, and the reverse
//       anti-automorphism.
//   (e) DETERMINISM: a byte-identical repeat.
//
// Rounding note for (a) and (b): FixedComplex/FixedSplit/FixedQuaternion accumulate several raw Q32 products and round
// the SUM once, whereas the carrier-agnostic geometric product rounds each product then adds. The two coincide exactly
// when every pairwise product is representable without rounding, so those checks draw operands from the 8-fractional-bit
// sublattice (raw a multiple of 256): each product is then exact in Q16 and the comparison isolates the ALGEBRA from the
// carrier's rounding policy. The (d) associativity and reverse checks use whole-number operands, where two chained
// products stay exact. (c) uses unit rotations that cannot sit on the lattice, so it is a measured-precision check.

using Puck.Maths;

var rng = new Random(0x6E0A17);

// A blade is its generator-subset bitmask (bit k = generator k, ascending); the scalar blade is 0.
const int Scalar = 0b0000;

var half = FixedQ4816.FromRawBits(value: 32768L);

// ---- (a) one generator reproduces the planar trio, bit-identical over the exact sublattice ----
{
    var complexAlg = GeometricAlgebra.Create(positiveCount: 0, negativeCount: 1, degenerateCount: 0);
    var splitAlg = GeometricAlgebra.Create(positiveCount: 1, negativeCount: 0, degenerateCount: 0);
    var dualAlg = GeometricAlgebra.Create(positiveCount: 0, negativeCount: 0, degenerateCount: 1);
    var checks = 0;

    for (var sample = 0; (sample < 10_000); ++sample) {
        var u1 = ExactFixed(rng);
        var v1 = ExactFixed(rng);
        var u2 = ExactFixed(rng);
        var v2 = ExactFixed(rng);
        var eA = Planar(u1, v1);
        var eB = Planar(u2, v2);

        // (0,1,0) == FixedComplex.
        var complex = (new FixedComplex(Real: u1, Imaginary: v1) * new FixedComplex(Real: u2, Imaginary: v2));
        RequirePlanar(complexAlg.GeometricProduct(left: eA, right: eB), complex.Real, complex.Imaginary, "a.complex.mul");
        RequirePlanar(complexAlg.GeometricProduct(left: eA, right: eB) - complexAlg.GeometricProduct(left: eA, right: eB), FixedQ4816.Zero, FixedQ4816.Zero, "a.complex.sub");

        // (1,0,0) == FixedSplit.
        var split = (new FixedSplit(U: u1, V: v1) * new FixedSplit(U: u2, V: v2));
        RequirePlanar(splitAlg.GeometricProduct(left: eA, right: eB), split.U, split.V, "a.split.mul");

        // (0,0,1) == FixedDual.
        var dual = (new FixedDual<FixedQ4816>(Real: u1, Dual: v1) * new FixedDual<FixedQ4816>(Real: u2, Dual: v2));
        RequirePlanar(dualAlg.GeometricProduct(left: eA, right: eB), dual.Real, dual.Dual, "a.dual.mul");

        // Componentwise add/sub agree for every planar carrier (one shared implementation).
        RequirePlanar((eA + eB), (u1 + u2), (v1 + v2), "a.add");
        RequirePlanar((eA - eB), (u1 - u2), (v1 - v2), "a.sub");

        ++checks;
    }

    Console.WriteLine($"(a) planar trio == one-generator algebras: {checks} pairs each bit-identical (complex/split/dual mul + add/sub)");
}

// ---- (b) even subalgebra of (3,0,0) reproduces FixedQuaternion multiplication, bit-identical ----
{
    var alg = GeometricAlgebra.Create(positiveCount: 3, negativeCount: 0, degenerateCount: 0);
    var checks = 0;

    for (var sample = 0; (sample < 10_000); ++sample) {
        var a = new FixedQuaternion(X: ExactFixed(rng), Y: ExactFixed(rng), Z: ExactFixed(rng), W: ExactFixed(rng));
        var b = new FixedQuaternion(X: ExactFixed(rng), Y: ExactFixed(rng), Z: ExactFixed(rng), W: ExactFixed(rng));
        var eA = QuaternionToEven(a);
        var eB = QuaternionToEven(b);
        var product = alg.GeometricProduct(left: eA, right: eB);

        if (!alg.IsEven(value: product)) { throw new InvalidOperationException("b.even: geometric product left the even subalgebra"); }

        var recovered = EvenToQuaternion(product);
        var expected = (a * b);

        if ((recovered.X.Value != expected.X.Value) || (recovered.Y.Value != expected.Y.Value) ||
            (recovered.Z.Value != expected.Z.Value) || (recovered.W.Value != expected.W.Value)) {
            throw new InvalidOperationException($"b.quaternion mismatch: {recovered} != {expected}");
        }

        ++checks;
    }

    Console.WriteLine($"(b) FixedQuaternion == even (3,0,0): {checks} pairs bit-identical (Hamilton product via geometric product)");
}

// ---- (c) motors of (3,0,1) reproduce rigid motion, to measured precision ----
{
    var alg = GeometricAlgebra.Create(positiveCount: 3, negativeCount: 0, degenerateCount: 1);
    var sandwichChecks = 0;
    var composeChecks = 0;
    long maxSandwichError = 0;
    long maxComposeError = 0;

    for (var sample = 0; (sample < 2_000); ++sample) {
        var rotation = RandomRotation(rng);
        var translation = new FixedVector3(X: ExactFixed(rng), Y: ExactFixed(rng), Z: ExactFixed(rng));
        var point = new FixedVector3(X: ExactFixed(rng), Y: ExactFixed(rng), Z: ExactFixed(rng));

        var motor = Motor(alg, rotation, translation, half);
        var transformed = ApplySandwich(alg, motor, point);
        var expected = FixedRigidTransform.FromRotationTranslation(rotation: rotation, translation: translation).TransformPoint(point: point);

        maxSandwichError = Math.Max(maxSandwichError, RawError(transformed, expected));
        ++sandwichChecks;

        // Motor composition matches transform composition: apply the composed motor and the composed transform to the
        // same point and compare.
        var rotationB = RandomRotation(rng);
        var translationB = new FixedVector3(X: ExactFixed(rng), Y: ExactFixed(rng), Z: ExactFixed(rng));
        var motorB = Motor(alg, rotationB, translationB, half);
        var composedMotor = alg.GeometricProduct(left: motor, right: motorB);
        var composedTransformed = ApplySandwich(alg, composedMotor, point);
        var composedTransform = (FixedRigidTransform.FromRotationTranslation(rotation: rotation, translation: translation) *
                                 FixedRigidTransform.FromRotationTranslation(rotation: rotationB, translation: translationB));
        var composedExpected = composedTransform.TransformPoint(point: point);

        maxComposeError = Math.Max(maxComposeError, RawError(composedTransformed, composedExpected));
        ++composeChecks;
    }

    const long ToleranceRaw = 128L; // ~2e-3 world units at the sampled ±8-unit translation/point scale (~2⁻¹⁵ relative).

    if (maxSandwichError > ToleranceRaw) { throw new InvalidOperationException($"c.sandwich precision {maxSandwichError} raw exceeds {ToleranceRaw}"); }
    if (maxComposeError > ToleranceRaw) { throw new InvalidOperationException($"c.compose precision {maxComposeError} raw exceeds {ToleranceRaw}"); }

    Console.WriteLine($"(c) FixedRigidTransform == motor (3,0,1) sandwich: {sandwichChecks} motions ≤ {maxSandwichError} raw (~{(maxSandwichError / 65536.0):0.0e+0} units); {composeChecks} compositions ≤ {maxComposeError} raw");
}

// ---- (d) structure: anticommutation, squares, associativity, reverse anti-automorphism ----
{
    var alg = GeometricAlgebra.Create(positiveCount: 3, negativeCount: 0, degenerateCount: 1);
    var basis = new[] { 0b0001, 0b0010, 0b0100, 0b1000 };
    var expectedSquares = new[] { 1, 1, 1, 0 };

    // Signature squares and anticommutation of distinct generators.
    for (var a = 0; (a < 4); ++a) {
        var ea = Basis(basis[a]);
        var eaea = alg.GeometricProduct(left: ea, right: ea);

        if (eaea[Scalar].Value != FixedQ4816.FromInteger(value: expectedSquares[a]).Value) {
            throw new InvalidOperationException($"d.square: generator {a} squared to {(double)eaea[Scalar]}");
        }

        for (var b = 0; (b < 4); ++b) {
            if (a == b) { continue; }

            var eb = Basis(basis[b]);
            var ab = alg.GeometricProduct(left: ea, right: eb);
            var ba = alg.GeometricProduct(left: eb, right: ea);

            // e_a e_b == -e_b e_a for distinct generators.
            for (var lane = 0; (lane < Multivector.BladeCapacity); ++lane) {
                if (ab[lane].Value != -ba[lane].Value) {
                    throw new InvalidOperationException($"d.anticommute: generators {a},{b} do not anticommute at lane {lane}");
                }
            }
        }
    }

    // Associativity of the geometric product and the reverse anti-automorphism, on whole-number operands where two
    // chained products stay exact.
    var associativityChecks = 0;
    var reverseChecks = 0;

    for (var sample = 0; (sample < 5_000); ++sample) {
        var x = RandomIntegerMultivector(rng, alg.BladeCount);
        var y = RandomIntegerMultivector(rng, alg.BladeCount);
        var z = RandomIntegerMultivector(rng, alg.BladeCount);

        var leftAssoc = alg.GeometricProduct(left: alg.GeometricProduct(left: x, right: y), right: z);
        var rightAssoc = alg.GeometricProduct(left: x, right: alg.GeometricProduct(left: y, right: z));

        if (!leftAssoc.Equals(other: rightAssoc)) { throw new InvalidOperationException($"d.assoc: (xy)z != x(yz) at sample {sample}"); }

        // Reverse(x·y) == Reverse(y)·Reverse(x).
        var reverseOfProduct = alg.Reverse(value: alg.GeometricProduct(left: x, right: y));
        var productOfReverses = alg.GeometricProduct(left: alg.Reverse(value: y), right: alg.Reverse(value: x));

        if (!reverseOfProduct.Equals(other: productOfReverses)) { throw new InvalidOperationException($"d.reverse: Reverse(xy) != Reverse(y)Reverse(x) at sample {sample}"); }

        ++associativityChecks;
        ++reverseChecks;
    }

    Console.WriteLine($"(d) structure: squares {{+1,+1,+1,0}} + generator anticommutation + {associativityChecks} associative triples + {reverseChecks} reverse anti-automorphism checks");
}

// ---- (e) determinism: a byte-identical repeat ----
{
    var first = SuiteDigest();
    var second = SuiteDigest();

    if (first != second) { throw new InvalidOperationException($"e.determinism: digest changed across runs ({first:X16} != {second:X16})"); }

    Console.WriteLine($"(e) determinism: byte-identical repeat (digest {first:X16})");
}

Console.WriteLine("all geometric-algebra acceptance checks passed");

// ================================ helpers ================================

// Operands on the 8-fractional-bit sublattice: every pairwise product is exact in Q16.
static FixedQ4816 ExactFixed(Random rng) =>
    FixedQ4816.FromRawBits(value: ((long)rng.Next(minValue: -2000, maxValue: 2001) * 256));

// A one-generator element U + V·e1 as a two-lane multivector.
static Multivector Planar(FixedQ4816 u, FixedQ4816 v) =>
    Multivector.FromCoefficients(coefficients: [u, v]);

// A single basis blade with unit coefficient.
static Multivector Basis(int blade) {
    var result = new Multivector();

    result[blade] = FixedQ4816.One;

    return result;
}

// The (b) embedding of a quaternion into the even subalgebra of (3,0,0): scalar + the three Euclidean bivectors, with
// i ↔ -e23, j ↔ e13, k ↔ -e12 (the sign convention that makes the geometric product the Hamilton product).
static Multivector QuaternionToEven(FixedQuaternion q) {
    var result = new Multivector();

    result[0b0000] = q.W;
    result[0b0011] = -q.Z; // e12
    result[0b0101] = q.Y;  // e13
    result[0b0110] = -q.X; // e23

    return result;
}

static FixedQuaternion EvenToQuaternion(Multivector mv) =>
    new(X: -mv[0b0110], Y: mv[0b0101], Z: -mv[0b0011], W: mv[0b0000]);

static FixedQuaternion RandomRotation(Random rng) {
    var q = new FixedQuaternion(
        X: FixedQ4816.FromRawBits(value: rng.NextInt64(minValue: -65536L, maxValue: 65537L)),
        Y: FixedQ4816.FromRawBits(value: rng.NextInt64(minValue: -65536L, maxValue: 65537L)),
        Z: FixedQ4816.FromRawBits(value: rng.NextInt64(minValue: -65536L, maxValue: 65537L)),
        W: FixedQ4816.FromRawBits(value: rng.NextInt64(minValue: -65536L, maxValue: 65537L))
    );

    if ((q.X.Value | q.Y.Value | q.Z.Value | q.W.Value) == 0L) { return FixedQuaternion.Identity; }

    return q.Normalize();
}

// The rotor of a rotation quaternion (the even (3,0,0) embedding, extended into (3,0,1)).
static Multivector Rotor(FixedQuaternion rotation) =>
    QuaternionToEven(rotation);

// The translator by t: the exponential of the null bivector (t/2)·(e14 + e24 + e34) (the degenerate exp branch,
// exactly 1 + that bivector).
static Multivector Translator(GeometricAlgebra alg, FixedVector3 t, FixedQ4816 half) {
    var bivector = new Multivector();

    bivector[0b1001] = (t.X * half); // e14
    bivector[0b1010] = (t.Y * half); // e24
    bivector[0b1100] = (t.Z * half); // e34

    return alg.Exponential(bivector: bivector);
}

// The motor translator·rotor: rotation applied first (inner), translation second (outer), matching
// FixedRigidTransform.FromRotationTranslation.
static Multivector Motor(GeometricAlgebra alg, FixedQuaternion rotation, FixedVector3 translation, FixedQ4816 half) =>
    alg.GeometricProduct(left: Translator(alg, translation, half), right: Rotor(rotation));

// Embed a Euclidean point as the (3,0,1) trivector x·(-e234) + y·e134 + z·(-e124) + e123, sandwich it, and recover the
// moved point (dividing out the e123 lane keeps a non-unit motor honest).
static FixedVector3 ApplySandwich(GeometricAlgebra alg, Multivector motor, FixedVector3 point) {
    var embedded = new Multivector();

    embedded[0b0111] = FixedQ4816.One; // e123
    embedded[0b1011] = -point.Z;       // e124
    embedded[0b1101] = point.Y;        // e134
    embedded[0b1110] = -point.X;       // e234

    var moved = alg.SandwichTransform(motor: motor, vector: embedded);
    var weight = moved[0b0111];

    return new(
        X: (-moved[0b1110] / weight),
        Y: (moved[0b1101] / weight),
        Z: (-moved[0b1011] / weight)
    );
}

static long RawError(FixedVector3 actual, FixedVector3 expected) =>
    Math.Max(
        Math.Abs(actual.X.Value - expected.X.Value),
        Math.Max(
            Math.Abs(actual.Y.Value - expected.Y.Value),
            Math.Abs(actual.Z.Value - expected.Z.Value)
        )
    );

// Whole-number blade coefficients: two chained geometric products stay exact, so associativity and the reverse
// anti-automorphism hold bit-for-bit.
static Multivector RandomIntegerMultivector(Random rng, int bladeCount) {
    var result = new Multivector();

    for (var i = 0; (i < bladeCount); ++i) {
        result[i] = FixedQ4816.FromInteger(value: rng.Next(minValue: -4, maxValue: 5));
    }

    return result;
}

static void RequirePlanar(Multivector actual, FixedQ4816 u, FixedQ4816 v, string label) {
    if ((actual[0].Value != u.Value) || (actual[1].Value != v.Value)) {
        throw new InvalidOperationException($"{label} mismatch: ({actual[0].Value},{actual[1].Value}) != ({u.Value},{v.Value})");
    }
}

// A fixed-seed digest over every branch's numeric output; two calls must agree bit-for-bit.
static ulong SuiteDigest() {
    var digestRng = new Random(0x11CE55);
    var hash = Fnv1aHash.Create();
    var half = FixedQ4816.FromRawBits(value: 32768L);
    var complexAlg = GeometricAlgebra.Create(positiveCount: 0, negativeCount: 1, degenerateCount: 0);
    var quaternionAlg = GeometricAlgebra.Create(positiveCount: 3, negativeCount: 0, degenerateCount: 0);
    var motorAlg = GeometricAlgebra.Create(positiveCount: 3, negativeCount: 0, degenerateCount: 1);

    for (var sample = 0; (sample < 512); ++sample) {
        var eA = Planar(ExactFixed(digestRng), ExactFixed(digestRng));
        var eB = Planar(ExactFixed(digestRng), ExactFixed(digestRng));
        var planar = complexAlg.GeometricProduct(left: eA, right: eB);

        hash.Add(value: planar[0].Value);
        hash.Add(value: planar[1].Value);

        var qA = new FixedQuaternion(X: ExactFixed(digestRng), Y: ExactFixed(digestRng), Z: ExactFixed(digestRng), W: ExactFixed(digestRng));
        var qB = new FixedQuaternion(X: ExactFixed(digestRng), Y: ExactFixed(digestRng), Z: ExactFixed(digestRng), W: ExactFixed(digestRng));
        var quaternion = quaternionAlg.GeometricProduct(left: QuaternionToEven(qA), right: QuaternionToEven(qB));

        for (var lane = 0; (lane < Multivector.BladeCapacity); ++lane) { hash.Add(value: quaternion[lane].Value); }

        var rotation = RandomRotation(digestRng);
        var translation = new FixedVector3(X: ExactFixed(digestRng), Y: ExactFixed(digestRng), Z: ExactFixed(digestRng));
        var point = new FixedVector3(X: ExactFixed(digestRng), Y: ExactFixed(digestRng), Z: ExactFixed(digestRng));
        var moved = ApplySandwich(motorAlg, Motor(motorAlg, rotation, translation, half), point);

        hash.Add(value: moved.X.Value);
        hash.Add(value: moved.Y.Value);
        hash.Add(value: moved.Z.Value);
    }

    return hash.Value;
}
