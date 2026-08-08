using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// The claim bodies for the geometric and monogenic algebras. The declarations these methods back
/// live in <c>laws/geometric-algebra.json</c>; the run bindings belong in <see cref="LawRegistry"/>. Every
/// method here either matches a <see cref="Laws"/> combinator's delegate shape directly (<see cref="BinaryElemOp"/>,
/// <see cref="VectorBinaryOp"/>) or returns the counterexample text / <see langword="null"/> a <c>Claim</c>-shaped
/// combinator expects, exactly as <see cref="CoreSurfaceClaims"/> does.
/// </summary>
internal static class GeometricAlgebraClaims {
    private static readonly GeometricAlgebra ComplexAlgebra = GeometricAlgebra.Create(positiveCount: 0, negativeCount: 1, degenerateCount: 0);
    private static readonly GeometricAlgebra SplitAlgebra = GeometricAlgebra.Create(positiveCount: 1, negativeCount: 0, degenerateCount: 0);
    private static readonly GeometricAlgebra DualAlgebra = GeometricAlgebra.Create(positiveCount: 0, negativeCount: 0, degenerateCount: 1);
    private static readonly GeometricAlgebra QuaternionAlgebra = GeometricAlgebra.Create(positiveCount: 3, negativeCount: 0, degenerateCount: 0);
    private static readonly GeometricAlgebra MotorAlgebra = GeometricAlgebra.Create(positiveCount: 3, negativeCount: 0, degenerateCount: 1);
    private static readonly FixedQ4816 Half = FixedQ4816.FromRawBits(value: 32768L);

    // ================================ (1)-(3) the planar trio ================================
    // GeometricAlgebra's one-generator signatures reproduce FixedComplex/FixedSplit/FixedDual, full raw range. Each
    // twin is witnessed by the SAME Oracles.QuadraticMultiply call the corresponding planar family's own law already
    // uses (complex.mul-vs-oracle, split.mul-vs-oracle, dual.mul-vs-oracle) — reused, not duplicated.

    public static (long U, long V) GeometricPlanarComplexSubject(long u1, long v1, long u2, long v2) {
        var product = ComplexAlgebra.GeometricProduct(
            left: Multivector.FromCoefficients(coefficients: [FixedQ4816.FromRawBits(value: u1), FixedQ4816.FromRawBits(value: v1)]),
            right: Multivector.FromCoefficients(coefficients: [FixedQ4816.FromRawBits(value: u2), FixedQ4816.FromRawBits(value: v2)])
        );

        return (product[0].Value, product[1].Value);
    }

    public static (long U, long V) FixedComplexLanes(long u1, long v1, long u2, long v2) {
        var product = new FixedComplex(Real: FixedQ4816.FromRawBits(value: u1), Imaginary: FixedQ4816.FromRawBits(value: v1)) *
            new FixedComplex(Real: FixedQ4816.FromRawBits(value: u2), Imaginary: FixedQ4816.FromRawBits(value: v2));

        return (product.Real.Value, product.Imaginary.Value);
    }

    public static (long U, long V) ComplexOracleWitness(long u1, long v1, long u2, long v2) =>
        Oracles.QuadraticMultiply(pRaw: 0L, qRaw: -65536L, u1: u1, v1: v1, u2: u2, v2: v2);

    public static (long U, long V) GeometricPlanarSplitSubject(long u1, long v1, long u2, long v2) {
        var product = SplitAlgebra.GeometricProduct(
            left: Multivector.FromCoefficients(coefficients: [FixedQ4816.FromRawBits(value: u1), FixedQ4816.FromRawBits(value: v1)]),
            right: Multivector.FromCoefficients(coefficients: [FixedQ4816.FromRawBits(value: u2), FixedQ4816.FromRawBits(value: v2)])
        );

        return (product[0].Value, product[1].Value);
    }

    public static (long U, long V) FixedSplitLanes(long u1, long v1, long u2, long v2) {
        var product = new FixedSplit(U: FixedQ4816.FromRawBits(value: u1), V: FixedQ4816.FromRawBits(value: v1)) *
            new FixedSplit(U: FixedQ4816.FromRawBits(value: u2), V: FixedQ4816.FromRawBits(value: v2));

        return (product.U.Value, product.V.Value);
    }

    public static (long U, long V) SplitOracleWitness(long u1, long v1, long u2, long v2) =>
        Oracles.QuadraticMultiply(pRaw: 0L, qRaw: 65536L, u1: u1, v1: v1, u2: u2, v2: v2);

    public static (long U, long V) GeometricPlanarDualSubject(long u1, long v1, long u2, long v2) {
        var product = DualAlgebra.GeometricProduct(
            left: Multivector.FromCoefficients(coefficients: [FixedQ4816.FromRawBits(value: u1), FixedQ4816.FromRawBits(value: v1)]),
            right: Multivector.FromCoefficients(coefficients: [FixedQ4816.FromRawBits(value: u2), FixedQ4816.FromRawBits(value: v2)])
        );

        return (product[0].Value, product[1].Value);
    }

    public static (long U, long V) FixedDualLanes(long u1, long v1, long u2, long v2) {
        var product = new FixedDual<FixedQ4816>(Real: FixedQ4816.FromRawBits(value: u1), Dual: FixedQ4816.FromRawBits(value: v1)) *
            new FixedDual<FixedQ4816>(Real: FixedQ4816.FromRawBits(value: u2), Dual: FixedQ4816.FromRawBits(value: v2));

        return (product.Real.Value, product.Dual.Value);
    }

    public static (long U, long V) DualOracleWitness(long u1, long v1, long u2, long v2) =>
        Oracles.QuadraticMultiply(pRaw: 0L, qRaw: 0L, u1: u1, v1: v1, u2: u2, v2: v2);

    // ================================ (4) the quaternion even subalgebra ================================
    // The even (3,0,0) embedding of two quaternions reproduces the Hamilton product, full raw range, and never leaves
    // the even subalgebra. GeometricQuaternionEvenFirst also cross-checks its recovered quaternion against an exact
    // BigInteger Hamilton-product oracle (QuaternionHamiltonOracle) before returning — an independent witness, thrown
    // as an InvalidOperationException on mismatch exactly like the existing IsEven check, rather than wired through
    // Laws.VectorTwin's own witness parameter (that binding lives in LawRegistry.cs, out of scope for this change).
    //
    // ENVELOPE: the embedding negates the swept quaternion's X and Z components (i ↔ -e23, k ↔ -e12; see
    // QuaternionToEven). FixedQ4816's unary negation is documented to WRAP, not throw, at MinValue (FixedQ4816.cs's
    // operator- remarks): MinValue's positive magnitude is exactly 2^47, one raw past MaxValue, so it is genuinely
    // unrepresentable in FixedQ4816 — not a defect of the negation operator, and not something ANY FixedQ4816-based
    // multivector coefficient could hold either. A swept X or Z of exactly long.MinValue would therefore embed as
    // itself instead of the +2^47 the (3,0,0) sign convention requires, corrupting the multivector on entry to
    // GeometricProduct — this was `presented.clifford-quaternion-even-twin`'s reported counterexample
    // (left=[0,1,0,0] right=[0,0,-9223372036854775808,0]): j ↔ e13 needs no negation and is unaffected, but
    // k ↔ -e12 does, and long.MinValue is exactly the operand that exposes it. So this twin never sweeps
    // FixedQuaternion.X or FixedQuaternion.Z at exactly long.MinValue (ExcludeUnembeddableMinValue below folds that
    // one raw value onto MinValue+1); every other value at full raw range, on every lane including Y and W (never
    // negated by this embedding, so MinValue is fine there), is swept as before.

    public static void GeometricQuaternionEvenFirst(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var a = SweptQuaternion(raw: left);
        var b = SweptQuaternion(raw: right);
        var product = QuaternionAlgebra.GeometricProduct(left: QuaternionToEven(value: a), right: QuaternionToEven(value: b));

        if (!QuaternionAlgebra.IsEven(value: product)) {
            throw new InvalidOperationException(message: $"the even (3,0,0) embedding of {a} and {b} left the even subalgebra under GeometricProduct");
        }

        var recovered = EvenToQuaternion(value: product);
        var oracle = QuaternionHamiltonOracle(left: a, right: b);

        if ((recovered.X.Value != oracle.X) || (recovered.Y.Value != oracle.Y) || (recovered.Z.Value != oracle.Z) || (recovered.W.Value != oracle.W)) {
            throw new InvalidOperationException(
                message: $"the even (3,0,0) embedding of {a} and {b} recovered ({recovered.X.Value},{recovered.Y.Value},{recovered.Z.Value},{recovered.W.Value}) " +
                    $"but the independent exact Hamilton-product oracle gives ({oracle.X},{oracle.Y},{oracle.Z},{oracle.W})"
            );
        }

        result[0] = recovered.X.Value;
        result[1] = recovered.Y.Value;
        result[2] = recovered.Z.Value;
        result[3] = recovered.W.Value;
    }

    public static void GeometricQuaternionEvenSecond(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var a = SweptQuaternion(raw: left);
        var b = SweptQuaternion(raw: right);
        var product = (a * b);

        result[0] = product.X.Value;
        result[1] = product.Y.Value;
        result[2] = product.Z.Value;
        result[3] = product.W.Value;
    }

    // Builds the quaternion this twin sweeps from four raw lanes (X,Y,Z,W), folding the one raw value the (3,0,0)
    // even-subalgebra embedding cannot represent (see the ENVELOPE note above) onto an adjacent legal value.
    private static FixedQuaternion SweptQuaternion(ReadOnlySpan<long> raw) =>
        new(
            X: FixedQ4816.FromRawBits(value: ExcludeUnembeddableMinValue(raw: raw[0])),
            Y: FixedQ4816.FromRawBits(value: raw[1]),
            Z: FixedQ4816.FromRawBits(value: ExcludeUnembeddableMinValue(raw: raw[2])),
            W: FixedQ4816.FromRawBits(value: raw[3])
        );

    // FixedQ4816.MinValue negates to itself (wraps) rather than to the unrepresentable +2^47, so a raw of exactly
    // long.MinValue on a lane this twin's embedding negates (X or Z) is folded one raw unit onto MinValue+1, whose
    // negation is exact. See the ENVELOPE note above GeometricQuaternionEvenFirst.
    private static long ExcludeUnembeddableMinValue(long raw) =>
        ((raw == long.MinValue) ? (raw + 1L) : raw);

    // The embedding of a quaternion into the even subalgebra of (3,0,0): scalar + the three Euclidean bivectors,
    // i ↔ -e23, j ↔ e13, k ↔ -e12.
    private static Multivector QuaternionToEven(FixedQuaternion value) {
        var result = new Multivector();

        result[0b0000] = value.W;
        result[0b0011] = -value.Z;
        result[0b0101] = value.Y;
        result[0b0110] = -value.X;

        return result;
    }

    private static FixedQuaternion EvenToQuaternion(Multivector value) =>
        new(X: -value[0b0110], Y: value[0b0101], Z: -value[0b0011], W: value[0b0000]);

    // An independent, exact-BigInteger Hamilton-product reference: the same four-term formula
    // FixedQuaternion.operator* implements, transcribed once more here so the derivation is written down twice, but
    // computed with no call into FixedQuaternion or GeometricAlgebra and rounded through Oracles.RoundDyadic (shift
    // 16 — each term is a raw*raw product, Q16×Q16=Q32, so one Q32→Q16 ties-to-even rounding recovers the raw
    // result), the same dyadic rounding face every classical leg in this suite routes through. Shares no code and no
    // rounding kernel with either shipped side of the twin.
    private static (long X, long Y, long Z, long W) QuaternionHamiltonOracle(FixedQuaternion left, FixedQuaternion right) {
        BigInteger lx = left.X.Value, ly = left.Y.Value, lz = left.Z.Value, lw = left.W.Value;
        BigInteger rx = right.X.Value, ry = right.Y.Value, rz = right.Z.Value, rw = right.W.Value;

        return (
            X: Oracles.RoundDyadic(exact: (((lw * rx) + (lx * rw) + (ly * rz)) - (lz * ry)), shift: 16),
            Y: Oracles.RoundDyadic(exact: ((((lw * ry) - (lx * rz)) + (ly * rw)) + (lz * rx)), shift: 16),
            Z: Oracles.RoundDyadic(exact: ((((lw * rz) + (lx * ry)) - (ly * rx)) + (lz * rw)), shift: 16),
            W: Oracles.RoundDyadic(exact: ((((lw * rw) - (lx * rx)) - (ly * ry)) - (lz * rz)), shift: 16)
        );
    }

    // ================================ (5) motors and FixedRigidTransform ================================
    // Measured-precision claim: SweptClaim rather than an exact twin, because the motor sandwich and
    // FixedRigidTransform reach the same rigid motion through independently rounded transcendental paths.

    private const long RigidTransformToleranceRaw = 128L;

    public static string? GeometricMotorRigidTransformSurface(long[] left, long[] right) {
        var rotation = BoundedRotation(x: left[0], y: left[1], z: left[2], w: left[3]);
        var translation = new FixedVector3(X: ExactFixed(raw: left[4]), Y: ExactFixed(raw: left[5]), Z: ExactFixed(raw: left[6]));
        var point = new FixedVector3(X: ExactFixed(raw: left[7]), Y: ExactFixed(raw: left[8]), Z: ExactFixed(raw: left[9]));

        var motor = Motor(rotation: rotation, translation: translation);
        var transformed = ApplySandwich(motor: motor, point: point);
        var expected = FixedRigidTransform.FromRotationTranslation(rotation: rotation, translation: translation).TransformPoint(point: point);
        var error = RawError(actual: transformed, expected: expected);

        if (error > RigidTransformToleranceRaw) {
            return $"motor sandwich at rotation {rotation}, translation {translation}, point {point} differs from FixedRigidTransform.TransformPoint by {error} raw, exceeding {RigidTransformToleranceRaw}";
        }

        var rotationB = BoundedRotation(x: right[0], y: right[1], z: right[2], w: right[3]);
        var translationB = new FixedVector3(X: ExactFixed(raw: right[4]), Y: ExactFixed(raw: right[5]), Z: ExactFixed(raw: right[6]));
        var motorB = Motor(rotation: rotationB, translation: translationB);
        var composedMotor = MotorAlgebra.GeometricProduct(left: motor, right: motorB);
        var composedTransformed = ApplySandwich(motor: composedMotor, point: point);
        var composedExpected = (
            FixedRigidTransform.FromRotationTranslation(rotation: rotation, translation: translation) *
            FixedRigidTransform.FromRotationTranslation(rotation: rotationB, translation: translationB)
        ).TransformPoint(point: point);
        var composeError = RawError(actual: composedTransformed, expected: composedExpected);

        if (composeError > RigidTransformToleranceRaw) {
            return $"composed motor sandwich differs from composed FixedRigidTransform.TransformPoint by {composeError} raw, exceeding {RigidTransformToleranceRaw}";
        }

        return null;
    }

    // A rotation quaternion bounded to raw ±65536 — a unit-length neighborhood.
    private static FixedQuaternion BoundedRotation(long x, long y, long z, long w) {
        var candidate = new FixedQuaternion(
            X: FixedQ4816.FromRawBits(value: (x % 65537L)),
            Y: FixedQ4816.FromRawBits(value: (y % 65537L)),
            Z: FixedQ4816.FromRawBits(value: (z % 65537L)),
            W: FixedQ4816.FromRawBits(value: (w % 65537L))
        );

        return (((candidate.X.Value | candidate.Y.Value | candidate.Z.Value | candidate.W.Value) == 0L)
            ? FixedQuaternion.Identity
            : candidate.Normalize());
    }

    // A raw folded onto the 8-fractional-bit sublattice within ±2000 integer units — every pairwise product of two
    // such values is exact in Q16.
    private static FixedQ4816 ExactFixed(long raw) =>
        FixedQ4816.FromRawBits(value: ((((raw % 4001L) + 4001L) % 4001L) - 2000L) * 256L);

    // The translator by t: the exponential of the null bivector (t/2)·(e14 + e24 + e34).
    private static Multivector Translator(FixedVector3 translation) {
        var bivector = new Multivector();

        bivector[0b1001] = (translation.X * Half);
        bivector[0b1010] = (translation.Y * Half);
        bivector[0b1100] = (translation.Z * Half);

        return MotorAlgebra.Exponential(bivector: bivector);
    }

    // The motor translator·rotor: rotation applied first (inner), translation second (outer), matching
    // FixedRigidTransform.FromRotationTranslation.
    private static Multivector Motor(FixedQuaternion rotation, FixedVector3 translation) =>
        MotorAlgebra.GeometricProduct(left: Translator(translation: translation), right: QuaternionToEven(value: rotation));

    // Embeds a Euclidean point as the (3,0,1) trivector, sandwiches it, and recovers the moved point.
    private static FixedVector3 ApplySandwich(Multivector motor, FixedVector3 point) {
        var embedded = new Multivector();

        embedded[0b0111] = FixedQ4816.One;
        embedded[0b1011] = -point.Z;
        embedded[0b1101] = point.Y;
        embedded[0b1110] = -point.X;

        var moved = MotorAlgebra.SandwichTransform(motor: motor, vector: embedded);
        var weight = moved[0b0111];

        return new(X: (-moved[0b1110] / weight), Y: (moved[0b1101] / weight), Z: (-moved[0b1011] / weight));
    }

    private static long RawError(FixedVector3 actual, FixedVector3 expected) =>
        Math.Max(
            val1: Math.Abs(value: (actual.X.Value - expected.X.Value)),
            val2: Math.Max(val1: Math.Abs(value: (actual.Y.Value - expected.Y.Value)), val2: Math.Abs(value: (actual.Z.Value - expected.Z.Value)))
        );

    // ================================ (6) the reverse anti-automorphism ================================

    public static string? GeometricReverseSurface(long[] left, long[] right) {
        var x = new Multivector();
        var y = new Multivector();

        for (var lane = 0; (lane < MotorAlgebra.BladeCount); ++lane) {
            x[lane] = FixedQ4816.FromInteger(value: BoundedSmallInteger(raw: left[lane]));
            y[lane] = FixedQ4816.FromInteger(value: BoundedSmallInteger(raw: right[lane]));
        }

        var reverseOfProduct = MotorAlgebra.Reverse(value: MotorAlgebra.GeometricProduct(left: x, right: y));
        var productOfReverses = MotorAlgebra.GeometricProduct(left: MotorAlgebra.Reverse(value: y), right: MotorAlgebra.Reverse(value: x));

        if (!reverseOfProduct.Equals(other: productOfReverses)) {
            return "Reverse(x*y) != Reverse(y)*Reverse(x) at signature (3,0,1) for an exact-integer multivector pair";
        }

        return null;
    }

    // An exact integer in [-4, 4], the range that keeps two chained geometric products exact.
    private static long BoundedSmallInteger(long raw) =>
        ((((raw % 9L) + 9L) % 9L) - 4L);

    // ================================ (7) Multivector construction and decomposition ================================

    public static string? GeometricMultivectorDecompositionSurface(long[] left, long[] right) {
        var n = Multivector.BladeCapacity;
        var coefficients = new FixedQ4816[n];
        var otherCoefficients = new FixedQ4816[n];

        for (var i = 0; (i < n); ++i) {
            coefficients[i] = FixedQ4816.FromInteger(value: BoundedSmallInteger(raw: left[i]));
            otherCoefficients[i] = FixedQ4816.FromInteger(value: BoundedSmallInteger(raw: right[i]));
        }

        var mv = Multivector.FromCoefficients(coefficients: coefficients);
        var other = Multivector.FromCoefficients(coefficients: otherCoefficients);

        for (var i = 0; (i < n); ++i) {
            if (mv[i].Value != coefficients[i].Value) { return $"FromCoefficients did not round-trip coordinate {i}"; }
        }

        var sum = new Multivector();

        for (var grade = 0; (grade <= MotorAlgebra.GeneratorCount); ++grade) {
            var projected = MotorAlgebra.GradeProjection(value: mv, grade: grade);

            for (var lane = 0; (lane < n); ++lane) {
                var isThisGrade = (BitOperations.PopCount(value: (uint)lane) == grade);

                if (isThisGrade) {
                    if (projected[lane].Value != mv[lane].Value) { return $"GradeProjection(grade={grade}) altered blade {lane}"; }
                } else if (projected[lane].Value != 0L) {
                    return $"GradeProjection(grade={grade}) left blade {lane} nonzero";
                }
            }

            sum = (sum + projected);
        }

        for (var i = 0; (i < n); ++i) {
            if (sum[i].Value != mv[i].Value) { return $"the summed grade projections do not reconstruct the original multivector at blade {i}"; }
        }

        var scalarValue = coefficients[0];
        var scalarMv = Multivector.Scalar(value: scalarValue);

        if (scalarMv[0].Value != scalarValue.Value) { return "Multivector.Scalar did not set the scalar blade"; }

        for (var i = 1; (i < n); ++i) {
            if (scalarMv[i].Value != 0L) { return $"Multivector.Scalar left blade {i} nonzero"; }
        }

        var added = (mv + other);
        var subtracted = (mv - other);

        for (var i = 0; (i < n); ++i) {
            var expectedSum = (coefficients[i] + otherCoefficients[i]).Value;
            var expectedDifference = (coefficients[i] - otherCoefficients[i]).Value;

            if (added[i].Value != expectedSum) { return $"op_Addition mismatch at blade {i}"; }
            if (subtracted[i].Value != expectedDifference) { return $"op_Subtraction mismatch at blade {i}"; }
        }

        return null;
    }

    // ================================ (8) the exponential's scalar-square domain ================================
    // GeometricAlgebra.Exponential implements the three closed-form branches of exp(b) for a bivector b whose square
    // is a scalar. That subdomain is a CHECKED precondition, and this law pins both halves of the boundary: what is
    // inside is accepted and comes back a versor, what is outside is refused, and the refusal is warranted rather
    // than merely conservative — the value the closed form would have returned for the nearest outside input is not a
    // versor at all, while the genuine exponential of that same input is.

    // The versor residual of a candidate: how far value·Reverse(value) is from the scalar 1, reported as (scalar
    // lane's distance from raw 65536, largest magnitude on any other lane). A true exponential of a bivector is a
    // versor — Reverse(exp(b)) = exp(-b) because Reverse negates every grade-two blade — so this residual is a
    // classical property of the answer, not a restatement of how the answer was computed.
    private static (long Scalar, long Other) VersorResidual(GeometricAlgebra algebra, Multivector value) {
        var product = algebra.GeometricProduct(left: value, right: algebra.Reverse(value: value));
        var scalar = Math.Abs(value: (product[0].Value - 65536L));
        var other = 0L;

        for (var lane = 1; (lane < algebra.BladeCount); ++lane) {
            other = Math.Max(val1: other, val2: Math.Abs(value: product[lane].Value));
        }

        return (scalar, other);
    }

    private static Multivector Blades(params (int Lane, long Raw)[] lanes) {
        var result = new Multivector();

        foreach (var (lane, raw) in lanes) { result[lane] = FixedQ4816.FromRawBits(value: raw); }

        return result;
    }

    // The rounding ceiling the accepted branches meet: the worst observed versor residual over the accepted cases
    // below is 4 raw on the scalar lane and 0 elsewhere, so 16 is a generous ceiling that still sits four thousand
    // times below the 63942 raw by which the refused input's branch value misses.
    private const long VersorToleranceRaw = 16L;

    public static string? CliffordExponentialDomainSurface() {
        var accepted = new (string Name, GeometricAlgebra Algebra, Multivector Bivector)[] {
            ("circular (2,0,0)", GeometricAlgebra.Create(positiveCount: 2, negativeCount: 0, degenerateCount: 0), Blades((0b0011, 32768L))),
            ("circular (2,0,0) unit", GeometricAlgebra.Create(positiveCount: 2, negativeCount: 0, degenerateCount: 0), Blades((0b0011, 65536L))),
            ("circular (0,2,0)", GeometricAlgebra.Create(positiveCount: 0, negativeCount: 2, degenerateCount: 0), Blades((0b0011, 49152L))),
            ("hyperbolic (1,1,0)", GeometricAlgebra.Create(positiveCount: 1, negativeCount: 1, degenerateCount: 0), Blades((0b0011, 32768L))),
            ("hyperbolic (1,1,0) unit", GeometricAlgebra.Create(positiveCount: 1, negativeCount: 1, degenerateCount: 0), Blades((0b0011, 65536L))),
            ("degenerate (1,0,1)", GeometricAlgebra.Create(positiveCount: 1, negativeCount: 0, degenerateCount: 1), Blades((0b0011, 98304L))),
            ("rotor (3,0,0)", GeometricAlgebra.Create(positiveCount: 3, negativeCount: 0, degenerateCount: 0), Blades((0b0011, 32768L), (0b0101, -16384L), (0b0110, 24576L))),
            ("zero (3,0,0)", GeometricAlgebra.Create(positiveCount: 3, negativeCount: 0, degenerateCount: 0), Blades()),
            ("translator (3,0,1)", GeometricAlgebra.Create(positiveCount: 3, negativeCount: 0, degenerateCount: 1), Blades((0b1001, 65536L), (0b1010, 131072L), (0b1100, -65536L))),
            ("screw generator (3,0,1)", GeometricAlgebra.Create(positiveCount: 3, negativeCount: 0, degenerateCount: 1), Blades((0b0011, 32768L))),
            ("zero scalar algebra", GeometricAlgebra.Create(positiveCount: 0, negativeCount: 0, degenerateCount: 0), Blades()),
        };

        foreach (var (name, algebra, bivector) in accepted) {
            Multivector value;

            try {
                value = algebra.Exponential(bivector: bivector);
            } catch (ArgumentException exception) {
                return $"Exponential refused the in-domain bivector '{name}', whose square is scalar: {exception.Message}";
            }

            var (scalarResidual, otherResidual) = VersorResidual(algebra: algebra, value: value);

            if ((scalarResidual > VersorToleranceRaw) || (otherResidual > VersorToleranceRaw)) {
                return $"exp of the in-domain bivector '{name}' is not a versor: value*Reverse(value) misses the scalar 1 by " +
                    $"{scalarResidual} raw and carries {otherResidual} raw on a non-scalar lane, exceeding {VersorToleranceRaw}";
            }
        }

        var cl300 = GeometricAlgebra.Create(positiveCount: 3, negativeCount: 0, degenerateCount: 0);
        var cl400 = GeometricAlgebra.Create(positiveCount: 4, negativeCount: 0, degenerateCount: 0);
        var refused = new (string Name, GeometricAlgebra Algebra, Multivector Input)[] {
            ("grade zero", cl300, Blades((0b0000, 65536L))),
            ("grade one", cl300, Blades((0b0001, 65536L))),
            ("grade three", cl300, Blades((0b0111, 65536L))),
            ("grade four", cl400, Blades((0b1111, 65536L))),
            ("grade two plus a scalar", cl300, Blades((0b0000, 65536L), (0b0011, 32768L))),
            ("grade two plus a vector", cl300, Blades((0b0001, 1L), (0b0011, 32768L))),
            ("non-simple e12+e34", cl400, Blades((0b0011, 65536L), (0b1100, 65536L))),
            ("non-simple e13+e24", cl400, Blades((0b0101, 65536L), (0b1010, 65536L))),
            ("non-simple, unequal weights", cl400, Blades((0b0011, 32768L), (0b1100, 131072L))),
        };

        foreach (var (name, algebra, input) in refused) {
            try {
                _ = algebra.Exponential(bivector: input);

                return $"Exponential accepted the out-of-domain input '{name}' instead of refusing it";
            } catch (ArgumentException exception) {
                if (exception.ParamName != "bivector") {
                    return $"Exponential's refusal of '{name}' names parameter '{exception.ParamName}' rather than 'bivector'";
                }
            }
        }

        // The warrant. In (4,0,0) the blades e12 and e34 commute and are disjoint, so exp(e12+e34) = exp(e12)·exp(e34)
        // — a product of two IN-DOMAIN exponentials, each accepted above. Its e1234 lane is nonzero, which no branch of
        // the scalar-square closed form can produce, so the closed form's answer for e12+e34 is not merely imprecise:
        // reconstructed here (the circular branch on the square's scalar lane, which is what the unguarded routine
        // computed) it fails the classical versor identity by two thirds of a unit, while the true exponential meets it
        // to one raw.
        var nonSimple = Blades((0b0011, 65536L), (0b1100, 65536L));
        var square = cl400.GeometricProduct(left: nonSimple, right: nonSimple);

        if (square[0b1111].Value == 0L) {
            return "the (4,0,0) bivector e12+e34 was expected to square to a value with a nonzero e1234 lane, but the lane is zero";
        }

        var magnitude = FixedQ4816.Sqrt(value: -square[0]);
        var (sin, cos) = FixedQ4816.SinCos(angle: magnitude);
        var cardinal = (sin / magnitude);
        var branchValue = new Multivector();

        branchValue[0] = cos;
        branchValue[0b0011] = (FixedQ4816.FromRawBits(value: 65536L) * cardinal);
        branchValue[0b1100] = (FixedQ4816.FromRawBits(value: 65536L) * cardinal);

        var branchResidual = VersorResidual(algebra: cl400, value: branchValue);

        if (branchResidual.Other <= 32768L) {
            return $"the scalar-square branch value for the refused bivector e12+e34 was expected to fail the versor identity by " +
                $"more than half a unit, but it misses by only {branchResidual.Other} raw — the refusal would then be over-strict";
        }

        var trueExponential = cl400.GeometricProduct(
            left: cl400.Exponential(bivector: Blades((0b0011, 65536L))),
            right: cl400.Exponential(bivector: Blades((0b1100, 65536L)))
        );

        if (trueExponential[0b1111].Value == 0L) {
            return "exp(e12)*exp(e34) was expected to carry a nonzero e1234 lane, which no scalar-square branch can produce, but the lane is zero";
        }

        var trueResidual = VersorResidual(algebra: cl400, value: trueExponential);

        if ((trueResidual.Scalar > VersorToleranceRaw) || (trueResidual.Other > VersorToleranceRaw)) {
            return $"exp(e12)*exp(e34), the true exponential of the refused bivector, is not a versor: it misses by " +
                $"({trueResidual.Scalar}, {trueResidual.Other}) raw, exceeding {VersorToleranceRaw}";
        }

        return null;
    }

    // ================================ (9) descriptor and multivector identity ================================
    // A GeometricAlgebra descriptor's identity is its signature, and equality must track behavior in both directions:
    // descriptors that compare equal compute the same thing, and descriptors that compare unequal are exhibited
    // computing different things.

    public static string? CliffordDescriptorIdentitySurface() {
        var signatures = new List<(int Positive, int Negative, int Degenerate)>();

        for (var positive = 0; (positive <= 4); ++positive) {
            for (var negative = 0; (negative <= (4 - positive)); ++negative) {
                for (var degenerate = 0; (degenerate <= ((4 - positive) - negative)); ++degenerate) {
                    signatures.Add(item: (positive, negative, degenerate));
                }
            }
        }

        if (signatures.Count != 35) { return $"the signatures with at most four generators number {signatures.Count}, expected 35"; }

        var descriptors = signatures
            .Select(selector: signature => GeometricAlgebra.Create(positiveCount: signature.Positive, negativeCount: signature.Negative, degenerateCount: signature.Degenerate))
            .ToArray();

        for (var index = 0; (index < signatures.Count); ++index) {
            var signature = signatures[index];
            var first = descriptors[index];
            var second = GeometricAlgebra.Create(positiveCount: signature.Positive, negativeCount: signature.Negative, degenerateCount: signature.Degenerate);

            if (!first.Equals(other: second)) { return $"two independently created descriptors of signature {signature} compare unequal"; }
            if (!(first == second)) { return $"operator == disagrees with Equals for signature {signature}"; }
            if (first != second) { return $"operator != disagrees with Equals for signature {signature}"; }
            if (!first.Equals(obj: (object)second)) { return $"the boxed Equals disagrees with the typed one for signature {signature}"; }
            if (first.Equals(obj: "not a descriptor")) { return $"the descriptor of signature {signature} claims equality with a value of another type"; }
            if (first.GetHashCode() != second.GetHashCode()) { return $"two independently created descriptors of signature {signature} hash differently"; }
        }

        var canonical = GeometricAlgebra.Create(positiveCount: 0, negativeCount: 0, degenerateCount: 0);

        if (!default(GeometricAlgebra).Equals(other: canonical)) { return "the default descriptor is unequal to Create(0,0,0) although it behaves as it throughout the public surface"; }
        if (!canonical.Equals(other: default)) { return "Create(0,0,0) is unequal to the default descriptor, so equality is not symmetric there"; }
        if (default(GeometricAlgebra).GetHashCode() != canonical.GetHashCode()) { return "the default descriptor and Create(0,0,0) hash differently"; }

        for (var left = 0; (left < signatures.Count); ++left) {
            for (var right = 0; (right < signatures.Count); ++right) {
                if (left == right) { continue; }

                if (descriptors[left].Equals(other: descriptors[right])) {
                    return $"the distinct signatures {signatures[left]} and {signatures[right]} compare equal";
                }

                var distinction = SignatureDistinction(
                    left: descriptors[left],
                    right: descriptors[right]
                );

                if (distinction is not null) {
                    return $"the distinct signatures {signatures[left]} and {signatures[right]} compare unequal but {distinction}";
                }
            }
        }

        var coefficients = new FixedQ4816[Multivector.BladeCapacity];

        for (var lane = 0; (lane < Multivector.BladeCapacity); ++lane) {
            coefficients[lane] = FixedQ4816.FromRawBits(value: ((lane * 7919L) - 30000L));
        }

        var multivector = Multivector.FromCoefficients(coefficients: coefficients);
        var sameMultivector = Multivector.FromCoefficients(coefficients: coefficients);

        if (!multivector.Equals(other: sameMultivector)) { return "two multivectors built from the same coefficients compare unequal"; }
        if (multivector.GetHashCode() != sameMultivector.GetHashCode()) { return "two equal multivectors hash differently"; }

        for (var lane = 0; (lane < Multivector.BladeCapacity); ++lane) {
            var perturbed = coefficients.ToArray();

            perturbed[lane] = FixedQ4816.FromRawBits(value: (coefficients[lane].Value + 1L));

            if (multivector.Equals(other: Multivector.FromCoefficients(coefficients: perturbed))) {
                return $"a multivector differing by one raw unit at blade {lane} compares equal";
            }
        }

        return null;
    }

    // Exhibits an operand on which two descriptors of different signatures genuinely compute different things, so
    // inequality is behavioral rather than incidental. Returns null when the distinction is found, otherwise the
    // reason it was not. Different generator counts are separated by a lane one admits and the other rejects; equal
    // counts differ in the square of some generator, which the geometric product reads directly.
    private static string? SignatureDistinction(GeometricAlgebra left, GeometricAlgebra right) {
        if (left.GeneratorCount != right.GeneratorCount) {
            var narrow = ((left.GeneratorCount < right.GeneratorCount) ? left : right);
            var wide = ((left.GeneratorCount < right.GeneratorCount) ? right : left);
            var probe = Blades((narrow.BladeCount, 65536L));

            try {
                _ = narrow.GeometricProduct(left: probe, right: probe);

                return $"the narrower one accepted blade {narrow.BladeCount}, which lies outside its signature";
            } catch (ArgumentException) {
                // The narrow descriptor refuses the lane, which is the distinction; the wide one must accept it.
            }

            try {
                _ = wide.GeometricProduct(left: probe, right: probe);
            } catch (ArgumentException) {
                return $"the wider one also refused blade {narrow.BladeCount}, so no distinguishing operand was found";
            }

            return null;
        }

        for (var generator = 0; (generator < left.GeneratorCount); ++generator) {
            if (left.Square(generatorIndex: generator) == right.Square(generatorIndex: generator)) { continue; }

            var probe = Blades(((1 << generator), 65536L));

            if (left.GeometricProduct(left: probe, right: probe)[0].Value == right.GeometricProduct(left: probe, right: probe)[0].Value) {
                return $"generator {generator} squares differently yet the geometric product of that generator with itself agrees";
            }

            return null;
        }

        return "no generator squares differently, so the two signatures cannot be told apart";
    }

    // ================================ monogenic algebra: BigInteger exactness vs an independent reference ================================

    public static string? MonogenicExactSurface(long[] left, long[] right) {
        // ---- degree 2, against BOTH QuadraticAlgebra<BigInteger> and the from-definition reference ----
        var p = Bound(raw: left[0]);
        var q = Bound(raw: left[1]);
        var modulus2 = (BigInteger[])[-q, -p];
        var mono2 = MonogenicAlgebra<BigInteger>.Create(monicModulus: modulus2);
        var quad = QuadraticAlgebra<BigInteger>.Create(p: p, q: q);
        var a2 = (BigInteger[])[Bound(raw: left[2]), Bound(raw: left[3])];
        var b2 = (BigInteger[])[Bound(raw: left[4]), Bound(raw: left[5])];

        if (mono2.Degree != 2) { return "MonogenicAlgebra<BigInteger> degree-2 Degree is not 2"; }

        var modulusReadback = mono2.Modulus;

        if ((modulusReadback[0] != modulus2[0]) || (modulusReadback[1] != modulus2[1])) {
            return "Modulus did not read back the constructing tail at degree 2";
        }

        var elementA2 = mono2.FromCoordinates(coordinates: a2);
        var elementB2 = mono2.FromCoordinates(coordinates: b2);
        var qa2 = new QuadraticAlgebra<BigInteger>.Element(U: a2[0], V: a2[1]);
        var qb2 = new QuadraticAlgebra<BigInteger>.Element(U: b2[0], V: b2[1]);

        var monoSum = mono2.Add(left: elementA2, right: elementB2);
        var monoDiff = mono2.Subtract(left: elementA2, right: elementB2);
        var monoNeg = mono2.Negate(value: elementA2);

        for (var i = 0; (i < 2); ++i) {
            if (monoSum[i] != (a2[i] + b2[i])) { return $"Add mismatch at degree 2, coordinate {i}"; }
            if (monoDiff[i] != (a2[i] - b2[i])) { return $"Subtract mismatch at degree 2, coordinate {i}"; }
            if (monoNeg[i] != -a2[i]) { return $"Negate mismatch at degree 2, coordinate {i}"; }
        }

        var monoProduct2 = mono2.Multiply(left: elementA2, right: elementB2);
        var quadProduct = quad.Multiply(left: qa2, right: qb2);
        var refProduct2 = MonogenicReference<BigInteger>.Multiply(modulus: modulus2, left: a2, right: b2);

        if ((monoProduct2[0] != quadProduct.U) || (monoProduct2[1] != quadProduct.V)) {
            return "degree-2 Multiply disagrees with QuadraticAlgebra<BigInteger>";
        }

        if ((monoProduct2[0] != refProduct2[0]) || (monoProduct2[1] != refProduct2[1])) {
            return "degree-2 Multiply disagrees with the from-definition reference";
        }

        var monoNorm2 = mono2.Norm(value: elementA2);

        if (monoNorm2 != quad.Norm(value: qa2)) { return "degree-2 Norm disagrees with QuadraticAlgebra<BigInteger>"; }
        if (monoNorm2 != MonogenicReference<BigInteger>.Norm(modulus: modulus2, value: a2)) { return "degree-2 Norm disagrees with the from-definition reference"; }

        var monoTrace2 = mono2.Trace(value: elementA2);

        if (monoTrace2 != quad.Trace(value: qa2)) { return "degree-2 Trace disagrees with QuadraticAlgebra<BigInteger>"; }
        if (monoTrace2 != MonogenicReference<BigInteger>.Trace(modulus: modulus2, value: a2)) { return "degree-2 Trace disagrees with the from-definition reference"; }

        var monoDisc2 = mono2.CharacteristicDiscriminant();
        var expectedDisc2 = ((p * p) + (4 * q));

        if (monoDisc2 != expectedDisc2) { return $"degree-2 CharacteristicDiscriminant is {monoDisc2}, expected P^2+4Q = {expectedDisc2}"; }

        const ulong Exponent = 13UL;
        var monoPower2 = mono2.CompanionPower(exponent: Exponent);
        var quadPower = quad.CompanionPower(exponent: Exponent);
        var refPower2 = MonogenicReference<BigInteger>.CompanionPower(modulus: modulus2, exponent: Exponent);

        if ((monoPower2[0] != quadPower.U) || (monoPower2[1] != quadPower.V)) {
            return "degree-2 CompanionPower disagrees with QuadraticAlgebra<BigInteger>";
        }

        if ((monoPower2[0] != refPower2[0]) || (monoPower2[1] != refPower2[1])) {
            return "degree-2 CompanionPower disagrees with the from-definition reference";
        }

        var window2 = mono2.FromWindow(window: a2);

        if ((window2[0] != a2[0]) || (window2[1] != a2[1])) { return "FromWindow did not read back its coordinates at degree 2"; }

        var monoStep2 = mono2.ProjectiveStep(window: window2);
        var quadStep = quad.MobiusStep(pair: new QuadraticAlgebra<BigInteger>.Projective(Numerator: a2[0], Denominator: a2[1]));

        if ((monoStep2[0] != quadStep.Numerator) || (monoStep2[1] != quadStep.Denominator)) {
            return "degree-2 ProjectiveStep disagrees with QuadraticAlgebra<BigInteger>.MobiusStep";
        }

        var one2 = mono2.One;
        var oneTimesA = mono2.Multiply(left: one2, right: elementA2);

        if ((oneTimesA[0] != a2[0]) || (oneTimesA[1] != a2[1])) { return "One is not a left multiplicative identity at degree 2"; }

        var zero2 = mono2.Zero;
        var zeroPlusA = mono2.Add(left: zero2, right: elementA2);

        if ((zeroPlusA[0] != a2[0]) || (zeroPlusA[1] != a2[1])) { return "Zero is not an additive identity at degree 2"; }

        var root2 = mono2.Root;
        var rootSquared = mono2.Multiply(left: root2, right: root2);
        var expectedRootSquared = mono2.Add(
            left: mono2.Multiply(left: mono2.FromCoordinates(coordinates: (BigInteger[])[p, BigInteger.Zero]), right: root2),
            right: mono2.FromCoordinates(coordinates: (BigInteger[])[q, BigInteger.Zero])
        );

        if ((rootSquared[0] != expectedRootSquared[0]) || (rootSquared[1] != expectedRootSquared[1])) {
            return "Root does not satisfy Root^2 = P*Root + Q*One at degree 2";
        }

        // ---- degree 3, against the from-definition reference only (no QuadraticAlgebra sibling exists) ----
        var modulus3 = (BigInteger[])[Bound(raw: left[6]), Bound(raw: left[7]), Bound(raw: right[0])];
        var mono3 = MonogenicAlgebra<BigInteger>.Create(monicModulus: modulus3);
        var a3 = (BigInteger[])[Bound(raw: right[1]), Bound(raw: right[2]), Bound(raw: right[3])];
        var b3 = (BigInteger[])[Bound(raw: right[4]), Bound(raw: right[5]), Bound(raw: right[6])];
        var elementA3 = mono3.FromCoordinates(coordinates: a3);
        var elementB3 = mono3.FromCoordinates(coordinates: b3);

        var monoProduct3 = mono3.Multiply(left: elementA3, right: elementB3);
        var refProduct3 = MonogenicReference<BigInteger>.Multiply(modulus: modulus3, left: a3, right: b3);

        for (var i = 0; (i < 3); ++i) {
            if (monoProduct3[i] != refProduct3[i]) { return $"degree-3 Multiply disagrees with the from-definition reference at coordinate {i}"; }
        }

        var monoNorm3 = mono3.Norm(value: elementA3);

        if (monoNorm3 != MonogenicReference<BigInteger>.Norm(modulus: modulus3, value: a3)) {
            return "degree-3 Norm disagrees with the from-definition reference";
        }

        var monoTrace3 = mono3.Trace(value: elementA3);

        if (monoTrace3 != MonogenicReference<BigInteger>.Trace(modulus: modulus3, value: a3)) {
            return "degree-3 Trace disagrees with the from-definition reference";
        }

        if (elementA3.Dimension != 3) { return "degree-3 Element.Dimension is not 3"; }

        for (var i = 0; (i < 3); ++i) {
            if (elementA3[i] != a3[i]) { return $"degree-3 Element indexer mismatch at coordinate {i}"; }
        }

        var window3 = mono3.FromWindow(window: a3);

        if (window3.Dimension != 3) { return "degree-3 Projective.Dimension is not 3"; }

        for (var i = 0; (i < 3); ++i) {
            if (window3[i] != a3[i]) { return $"degree-3 Projective indexer mismatch at coordinate {i}"; }
        }

        return null;
    }

    // Bounds a domain-drawn raw to [-1000, 1000] so BigInteger arithmetic (CompanionPower, the cofactor determinant)
    // stays cheap regardless of the domain's edge extremes (0, long.MinValue/MaxValue, the 2^31/2^47 seams).
    // ENVELOPE: the twin is exact-integer agreement, so nothing about this bound is a rounding concern — it is purely
    // a cost control.
    private static BigInteger Bound(long raw) =>
        new(value: ((((raw % 2001L) + 2001L) % 2001L) - 1000L));

    // ================================ monogenic algebra: the plastic-ratio world (own basis) ================================

    public static string? MonogenicPlasticRatioSurface() {
        var modulus = (BigInteger[])[BigInteger.MinusOne, BigInteger.MinusOne, BigInteger.Zero]; // x^3 - x - 1
        var mono = MonogenicAlgebra<BigInteger>.Create(monicModulus: modulus);
        var powers = new MonogenicAlgebra<BigInteger>.Element[61];

        for (var n = 0; (n <= 60); ++n) { powers[n] = mono.CompanionPower(exponent: (ulong)n); }

        for (var n = 3; (n <= 60); ++n) {
            for (var coordinate = 0; (coordinate < 3); ++coordinate) {
                if (powers[n][coordinate] != (powers[n - 2][coordinate] + powers[n - 3][coordinate])) {
                    return $"the plastic-ratio recurrence a(n)=a(n-2)+a(n-3) broke at n={n}, coordinate={coordinate}";
                }
            }
        }

        var discriminant = mono.CharacteristicDiscriminant();

        if (discriminant != -23) { return $"CharacteristicDiscriminant of x^3-x-1 is {discriminant}, expected -23"; }

        (BigInteger[] A, BigInteger[] B)[] samples = [
            ((BigInteger[])[1, 0, 0], (BigInteger[])[0, 1, 0]),
            ((BigInteger[])[2, -3, 5], (BigInteger[])[-7, 1, 4]),
            ((BigInteger[])[0, 0, 1], (BigInteger[])[1, 1, 1]),
            ((BigInteger[])[-13, 8, -2], (BigInteger[])[6, -11, 3]),
            ((BigInteger[])[100, -100, 1], (BigInteger[])[-1, 1, 100]),
        ];

        foreach (var (aRaw, bRaw) in samples) {
            var a = mono.FromCoordinates(coordinates: aRaw);
            var b = mono.FromCoordinates(coordinates: bRaw);
            var normOfProduct = mono.Norm(value: mono.Multiply(left: a, right: b));
            var productOfNorms = (mono.Norm(value: a) * mono.Norm(value: b));

            if (normOfProduct != productOfNorms) {
                return $"Norm is not multiplicative for [{string.Join(separator: ",", values: aRaw)}] and [{string.Join(separator: ",", values: bRaw)}]";
            }

            var traceOfSum = mono.Trace(value: mono.Add(left: a, right: b));
            var sumOfTraces = (mono.Trace(value: a) + mono.Trace(value: b));

            if (traceOfSum != sumOfTraces) {
                return $"Trace is not additive for [{string.Join(separator: ",", values: aRaw)}] and [{string.Join(separator: ",", values: bRaw)}]";
            }
        }

        return null;
    }

    // ================================ monogenic algebra: what a value's identity is made of ================================
    // The library draws a deliberate line: a MonogenicAlgebra descriptor IS its modulus tail, while an Element or a
    // Projective IS its coordinate vector and carries no modulus at all — which is exactly what lets a receiver
    // reinterpret an equal-dimensional vector under its own modulus. Equality is structural on both sides of that
    // line, so this law pins the line itself: same-tail descriptors are interchangeable, same-coordinate values are
    // interchangeable ACROSS moduli, and a descriptor with a different tail is not.

    public static string? MonogenicIdentitySurface() {
        var tail = (BigInteger[])[BigInteger.One, BigInteger.One];
        var algebra = MonogenicAlgebra<BigInteger>.Create(monicModulus: tail);
        var twin = MonogenicAlgebra<BigInteger>.Create(monicModulus: (BigInteger[])[BigInteger.One, BigInteger.One]);

        if (!algebra.Equals(other: twin)) { return "two descriptors built from the same modulus tail compare unequal"; }
        if (!(algebra == twin)) { return "operator == disagrees with Equals for two descriptors of the same tail"; }
        if (algebra != twin) { return "operator != disagrees with Equals for two descriptors of the same tail"; }
        if (algebra.GetHashCode() != twin.GetHashCode()) { return "two descriptors built from the same modulus tail hash differently"; }
        if (!algebra.Equals(obj: (object)twin)) { return "the boxed descriptor Equals disagrees with the typed one"; }
        if (algebra.Equals(obj: "not a descriptor")) { return "a descriptor claims equality with a value of another type"; }

        var differentTails = (BigInteger[][])[
            [BigInteger.One, BigInteger.Zero],
            [BigInteger.Zero, BigInteger.One],
            [BigInteger.One],
            [BigInteger.One, BigInteger.One, BigInteger.One],
            [BigInteger.MinusOne, BigInteger.One],
        ];

        foreach (var other in differentTails) {
            var otherAlgebra = MonogenicAlgebra<BigInteger>.Create(monicModulus: other);

            if (algebra.Equals(other: otherAlgebra)) {
                return $"the descriptor of tail [{string.Join(separator: ",", values: tail)}] compares equal to the one of tail [{string.Join(separator: ",", values: other)}]";
            }
        }

        if (!default(MonogenicAlgebra<BigInteger>).Equals(other: default)) { return "two default descriptors compare unequal"; }
        if (algebra.Equals(other: default)) { return "a constructed descriptor compares equal to the default, which names no modulus"; }

        // Element identity: the coordinate vector, and nothing else. Two reads of One are the same value, an identity
        // computation lands back on it, and both are usable as dictionary and set keys.
        if (!algebra.One.Equals(other: algebra.One)) { return "two reads of One compare unequal"; }
        if (algebra.One.GetHashCode() != algebra.One.GetHashCode()) { return "two reads of One hash differently"; }
        if (!algebra.Add(left: algebra.One, right: algebra.Zero).Equals(other: algebra.One)) { return "Add(One, Zero) is not equal to One"; }
        if (!(algebra.Add(left: algebra.One, right: algebra.Zero) == algebra.One)) { return "operator == disagrees with Equals for Add(One, Zero) and One"; }
        if (!algebra.Zero.Equals(other: algebra.FromCoordinates(coordinates: (BigInteger[])[BigInteger.Zero, BigInteger.Zero]))) { return "Zero is not equal to the all-zero coordinate vector"; }
        if (!algebra.Root.Equals(other: algebra.MultiplyByRoot(value: algebra.One))) { return "Root is not equal to MultiplyByRoot(One)"; }
        if (algebra.One.Equals(other: algebra.Zero)) { return "One compares equal to Zero"; }
        if (algebra.One.Equals(obj: "not an element")) { return "an element claims equality with a value of another type"; }

        var set = new HashSet<MonogenicAlgebra<BigInteger>.Element> { algebra.One, };

        if (!set.Contains(item: algebra.Add(left: algebra.One, right: algebra.Zero))) { return "a set holding One does not recognize Add(One, Zero) as the same value"; }
        if (set.Add(item: algebra.One)) { return "a set holding One admitted a second read of One as a distinct value"; }

        if (!default(MonogenicAlgebra<BigInteger>.Element).Equals(other: default)) { return "two default elements compare unequal"; }
        if (algebra.One.Equals(other: default)) { return "One compares equal to a default element, which belongs to no algebra"; }

        // THE LINE. An element carries no modulus, so the same coordinates under a different modulus are the same
        // element — the value-level face of the receiver-affinity contract, under which any degree-2 receiver
        // reinterprets a degree-2 vector as its own. The descriptors that made them stay unequal.
        var foreign = MonogenicAlgebra<BigInteger>.Create(monicModulus: (BigInteger[])[new BigInteger(5), new BigInteger(7)]);

        if (!algebra.One.Equals(other: foreign.One)) {
            return "One under a different modulus of the same degree compares unequal, although an element carries no modulus and every receiver reinterprets an equal-dimensional vector as its own";
        }

        if (algebra.Equals(other: foreign)) { return "the two descriptors that produced those interchangeable elements compare equal, so the modulus is not distinguished anywhere"; }

        // A shorter and a longer vector are different values, so the degree is genuinely part of an element's identity
        // even though the modulus is not.
        var shorter = MonogenicAlgebra<BigInteger>.Create(monicModulus: (BigInteger[])[BigInteger.One]);
        var longer = MonogenicAlgebra<BigInteger>.Create(monicModulus: (BigInteger[])[BigInteger.One, BigInteger.One, BigInteger.One]);

        if (algebra.One.Equals(other: shorter.One)) { return "a degree-2 One compares equal to a degree-1 One"; }
        if (algebra.One.Equals(other: longer.One)) { return "a degree-2 One compares equal to a degree-3 One"; }

        // Projective windows carry the same identity rule.
        var window = algebra.FromWindow(window: (BigInteger[])[new BigInteger(3), new BigInteger(5)]);
        var sameWindow = algebra.FromWindow(window: (BigInteger[])[new BigInteger(3), new BigInteger(5)]);

        if (!window.Equals(other: sameWindow)) { return "two windows built from the same coordinates compare unequal"; }
        if (window.GetHashCode() != sameWindow.GetHashCode()) { return "two equal windows hash differently"; }
        if (!(window == sameWindow)) { return "operator == disagrees with Equals for two equal windows"; }
        if (window.Equals(other: algebra.ProjectiveStep(window: window))) { return "a window compares equal to its own successor under ProjectiveStep"; }
        if (!algebra.ProjectiveStep(window: window).Equals(other: algebra.ProjectiveStep(window: sameWindow))) { return "ProjectiveStep of two equal windows produced unequal results"; }
        if (!window.Equals(other: foreign.FromWindow(window: (BigInteger[])[new BigInteger(3), new BigInteger(5)]))) {
            return "a window under a different modulus of the same degree compares unequal, although a window carries no modulus";
        }
        if (!default(MonogenicAlgebra<BigInteger>.Projective).Equals(other: default)) { return "two default windows compare unequal"; }
        if (window.Equals(other: default)) { return "a constructed window compares equal to a default one"; }

        // A second carrier, to show the rule is the type's and not BigInteger's: the house fixed-point scalar, whose
        // degree-2 surface runs the delegating quadratic lane rather than the general one.
        var fixedTail = (FixedQ4816[])[FixedQ4816.FromRawBits(value: 32768L), FixedQ4816.FromRawBits(value: -49152L)];
        var fixedAlgebra = MonogenicAlgebra<FixedQ4816>.Create(monicModulus: fixedTail);
        var fixedTwin = MonogenicAlgebra<FixedQ4816>.Create(monicModulus: (FixedQ4816[])[FixedQ4816.FromRawBits(value: 32768L), FixedQ4816.FromRawBits(value: -49152L)]);

        if (!fixedAlgebra.Equals(other: fixedTwin)) { return "two FixedQ4816 descriptors built from the same modulus tail compare unequal"; }
        if (fixedAlgebra.GetHashCode() != fixedTwin.GetHashCode()) { return "two FixedQ4816 descriptors built from the same modulus tail hash differently"; }
        if (!fixedAlgebra.One.Equals(other: fixedTwin.One)) { return "One over the FixedQ4816 carrier compares unequal across two equal descriptors"; }
        if (fixedAlgebra.One.GetHashCode() != fixedTwin.One.GetHashCode()) { return "One over the FixedQ4816 carrier hashes differently across two equal descriptors"; }

        var fixedElement = fixedAlgebra.FromCoordinates(coordinates: (FixedQ4816[])[FixedQ4816.FromRawBits(value: 12345L), FixedQ4816.FromRawBits(value: -6789L)]);
        var fixedProduct = fixedAlgebra.Multiply(left: fixedElement, right: fixedAlgebra.One);

        if (!fixedProduct.Equals(other: fixedElement)) { return "multiplying a FixedQ4816 element by One did not land back on an equal element"; }

        if (fixedElement.Equals(other: fixedAlgebra.FromCoordinates(coordinates: (FixedQ4816[])[FixedQ4816.FromRawBits(value: 12346L), FixedQ4816.FromRawBits(value: -6789L)]))) {
            return "two FixedQ4816 elements differing by one raw unit compare equal";
        }

        return null;
    }

    // ================================ monogenic algebra: fused FixedQ4816 diverges from per-product ================================

    private static readonly FixedQ4816[] FusionModulus = [FixedQ4816.FromRawBits(value: 32768L), FixedQ4816.FromRawBits(value: 49152L), FixedQ4816.FromRawBits(value: -28672L)];
    private static readonly MonogenicAlgebra<FixedQ4816> FusionAlgebra = MonogenicAlgebra<FixedQ4816>.Create(monicModulus: FusionModulus);

    public static void MonogenicFusedMultiply(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var a = new FixedQ4816[3];
        var b = new FixedQ4816[3];

        for (var i = 0; (i < 3); ++i) {
            a[i] = FixedQ4816.FromRawBits(value: left[i]);
            b[i] = FixedQ4816.FromRawBits(value: right[i]);
        }

        var product = FusionAlgebra.Multiply(left: FusionAlgebra.FromCoordinates(coordinates: a), right: FusionAlgebra.FromCoordinates(coordinates: b));

        for (var i = 0; (i < 3); ++i) { result[i] = product[i].Value; }
    }

    public static void MonogenicPerProductMultiply(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var a = new FixedQ4816[3];
        var b = new FixedQ4816[3];

        for (var i = 0; (i < 3); ++i) {
            a[i] = FixedQ4816.FromRawBits(value: left[i]);
            b[i] = FixedQ4816.FromRawBits(value: right[i]);
        }

        var product = MonogenicReference<FixedQ4816>.Multiply(modulus: FusionModulus, left: a, right: b);

        for (var i = 0; (i < 3); ++i) { result[i] = product[i].Value; }
    }

    // A standalone, always-general reconstruction of MonogenicAlgebra<TScalar>'s any-degree algorithm — schoolbook
    // convolution, top-down companion reduction, square-and-multiply power, and a recursive cofactor determinant for
    // Norm. It calls no member of
    // MonogenicAlgebra<TScalar> and shares no code with it, so agreement is independent evidence rather than a second
    // expression of the same implementation. Recursion depth is bounded by the small degrees (2-3) the two laws that
    // use this reach, so the recursive determinant (rather than the division-free Berkowitz elimination
    // MonogenicAlgebra<TScalar> itself uses at higher degree) is the right-sized reimplementation here.
    private static class MonogenicReference<TScalar>
        where TScalar : IAdditionOperators<TScalar, TScalar, TScalar>,
                        ISubtractionOperators<TScalar, TScalar, TScalar>,
                        IMultiplyOperators<TScalar, TScalar, TScalar>,
                        IUnaryNegationOperators<TScalar, TScalar>,
                        IAdditiveIdentity<TScalar, TScalar>,
                        IMultiplicativeIdentity<TScalar, TScalar> {
        internal static TScalar[] Multiply(TScalar[] modulus, TScalar[] left, TScalar[] right) {
            var n = modulus.Length;
            var wide = new TScalar[((2 * n) - 1)];

            for (var index = 0; (index < wide.Length); ++index) { wide[index] = TScalar.AdditiveIdentity; }

            for (var i = 0; (i < n); ++i) {
                for (var j = 0; (j < n); ++j) { wide[i + j] = (wide[i + j] + (left[i] * right[j])); }
            }

            for (var degree = (wide.Length - 1); (degree >= n); --degree) {
                var carry = wide[degree];

                for (var j = 0; (j < n); ++j) { wide[(degree - n) + j] = (wide[(degree - n) + j] - (carry * modulus[j])); }

                wide[degree] = TScalar.AdditiveIdentity;
            }

            var result = new TScalar[n];

            Array.Copy(sourceArray: wide, destinationArray: result, length: n);

            return result;
        }

        private static TScalar[] MultiplyByRoot(TScalar[] modulus, TScalar[] value) {
            var n = modulus.Length;
            var top = value[n - 1];
            var result = new TScalar[n];

            result[0] = (TScalar.AdditiveIdentity - (top * modulus[0]));

            for (var index = 1; (index < n); ++index) { result[index] = (value[index - 1] - (top * modulus[index])); }

            return result;
        }

        internal static TScalar[] CompanionPower(TScalar[] modulus, ulong exponent) {
            var result = One(n: modulus.Length);
            var power = MultiplyByRoot(modulus: modulus, value: result);

            while (0UL != exponent) {
                if (0UL != (exponent & 1UL)) { result = Multiply(modulus: modulus, left: result, right: power); }

                exponent >>>= 1;

                if (0UL != exponent) { power = Multiply(modulus: modulus, left: power, right: power); }
            }

            return result;
        }

        internal static TScalar Trace(TScalar[] modulus, TScalar[] value) {
            var n = modulus.Length;
            var column = value;
            var trace = column[0];

            for (var index = 1; (index < n); ++index) {
                column = MultiplyByRoot(modulus: modulus, value: column);
                trace = (trace + column[index]);
            }

            return trace;
        }

        internal static TScalar Norm(TScalar[] modulus, TScalar[] value) {
            var n = modulus.Length;
            var matrix = new TScalar[n * n];
            var column = value;

            for (var columnIndex = 0; (columnIndex < n); ++columnIndex) {
                for (var rowIndex = 0; (rowIndex < n); ++rowIndex) { matrix[(rowIndex * n) + columnIndex] = column[rowIndex]; }

                if (columnIndex < (n - 1)) { column = MultiplyByRoot(modulus: modulus, value: column); }
            }

            return CofactorDeterminant(matrix: matrix, order: n);
        }

        private static TScalar[] One(int n) {
            var result = new TScalar[n];

            for (var index = 0; (index < n); ++index) { result[index] = TScalar.AdditiveIdentity; }

            result[0] = TScalar.MultiplicativeIdentity;

            return result;
        }

        private static TScalar CofactorDeterminant(TScalar[] matrix, int order) {
            if (1 == order) { return matrix[0]; }

            if (2 == order) { return ((matrix[0] * matrix[3]) - (matrix[1] * matrix[2])); }

            var result = TScalar.AdditiveIdentity;
            var minor = new TScalar[(order - 1) * (order - 1)];

            for (var column = 0; (column < order); ++column) {
                var target = 0;

                for (var row = 1; (row < order); ++row) {
                    for (var minorColumn = 0; (minorColumn < order); ++minorColumn) {
                        if (minorColumn == column) { continue; }

                        minor[target++] = matrix[(row * order) + minorColumn];
                    }
                }

                var cofactor = (matrix[column] * CofactorDeterminant(matrix: minor, order: (order - 1)));

                result = ((0 == (column & 1)) ? (result + cofactor) : (result - cofactor));
            }

            return result;
        }
    }
}
