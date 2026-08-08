using System.Numerics;

namespace Puck.Maths.Tests;

using Floor1 = Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>;
using Floor2 = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>>;
using Floor3 = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>>>;
using BFloor1 = Puck.Maths.DoublingAlgebra<Puck.Maths.Tests.DoublingTowerClaims.BigIntegerRing>;
using BFloor2 = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.Tests.DoublingTowerClaims.BigIntegerRing>>;

/// <summary>
/// The claim bodies for the quadratic algebra and the Cayley-Dickson doubling tower. The declarations these methods
/// back live in <c>laws/doubling-tower.json</c>; the run bindings belong in <see cref="LawRegistry"/>.
/// </summary>
internal static class DoublingTowerClaims {
    // A splitmix64-style deterministic generator — no System.Random anywhere in law logic, per house rule. `lane`
    // distinguishes the several operands drawn within one step so they do not collide with each other.
    private static long DeterministicFullRangeRaw(int step, int lane) {
        var x = unchecked((ulong)step * 0x9E3779B97F4A7C15UL + (ulong)lane * 0xBF58476D1CE4E5B9UL + 0x2545F4914F6CDD1DUL);

        x ^= (x >> 30);
        x = unchecked(x * 0xBF58476D1CE4E5B9UL);
        x ^= (x >> 27);
        x = unchecked(x * 0x94D049BB133111EBUL);
        x ^= (x >> 31);

        return unchecked((long)x);
    }

    // A small deterministic integer in [-bound, bound] for BigInteger-carrier operands, where a huge magnitude adds
    // no evidence (the twin's arithmetic is exact at every magnitude) but would slow the exact-rational comparisons.
    private static BigInteger DeterministicSmallInteger(int step, int lane, int bound) {
        var range = ((2 * bound) + 1);
        var raw = DeterministicFullRangeRaw(step: step, lane: lane);
        var reduced = (int)(((raw % range) + range) % range);

        return (new BigInteger(value: reduced) - bound);
    }

    // A deterministic long strictly bounded in magnitude by `bound` — used to force a kernel's narrow (Int64) branch.
    private static long DeterministicBoundedRaw(int step, int lane, long bound) {
        var range = ((2 * bound) + 1);
        var raw = DeterministicFullRangeRaw(step: step, lane: lane);
        var reduced = (((raw % range) + range) % range);

        return (reduced - bound);
    }

    // The narrow/wide seam and the wrap extremes, reused across the quadratic and Floor 1/2 twin sweeps below.
    private static readonly long[] FullRangeEdgeRaws = [
        0L, 1L, -1L, long.MinValue, long.MaxValue, (1L << 31), -(1L << 31), (1L << 47), -(1L << 47),
    ];

    // ---------------------------------------------------------------------------------------------------------
    // QuadraticAlgebra<BigInteger> vs QuadraticSurd, under x = (k + √Δ)/2, Δ = k² + 4 — the (k,1)/BigInteger ==
    // QuadraticSurd twin. No other law compares QuadraticAlgebra's own arithmetic against QuadraticSurd, and without
    // this one Add, Subtract, Conjugate and Trace stand uncovered.

    public static string? QuadraticSurdTwinLaneSurface() {
        foreach (var k in new long[] { 1L, 2L, 3L, 5L }) {
            var kBig = new BigInteger(value: k);
            var alg = QuadraticAlgebra<BigInteger>.Create(p: kBig, q: BigInteger.One);
            var radicand = ((kBig * kBig) + 4);

            QuadraticSurd ToSurd(QuadraticAlgebra<BigInteger>.Element e) =>
                QuadraticSurd.Create(rationalNumerator: ((2 * e.U) + (e.V * kBig)), surdNumerator: e.V, radicand: radicand, denominator: 2);

            for (var step = 0; step < 300; ++step) {
                var eA = new QuadraticAlgebra<BigInteger>.Element(U: DeterministicSmallInteger(step: step, lane: 0, bound: 1000), V: DeterministicSmallInteger(step: step, lane: 1, bound: 1000));
                var eB = new QuadraticAlgebra<BigInteger>.Element(U: DeterministicSmallInteger(step: step, lane: 2, bound: 1000), V: DeterministicSmallInteger(step: step, lane: 3, bound: 1000));
                var sA = ToSurd(eA);
                var sB = ToSurd(eB);
                var conjA = QuadraticSurd.Create(rationalNumerator: sA.RationalNumerator, surdNumerator: -sA.SurdNumerator, radicand: sA.Radicand, denominator: sA.Denominator);

                if (ToSurd(e: alg.Add(left: eA, right: eB)) != (sA + sB)) { return $"k={k}: Add disagrees with QuadraticSurd at step {step}"; }
                if (ToSurd(e: alg.Subtract(left: eA, right: eB)) != (sA - sB)) { return $"k={k}: Subtract disagrees with QuadraticSurd at step {step}"; }
                if (ToSurd(e: alg.Multiply(left: eA, right: eB)) != (sA * sB)) { return $"k={k}: Multiply disagrees with QuadraticSurd at step {step}"; }
                if (ToSurd(e: alg.Conjugate(value: eA)) != conjA) { return $"k={k}: Conjugate disagrees with QuadraticSurd at step {step}"; }
                if (QuadraticSurd.Rational(value: alg.Norm(value: eA)) != (sA * conjA)) { return $"k={k}: Norm disagrees with QuadraticSurd at step {step}"; }
                if (QuadraticSurd.Rational(value: alg.Trace(value: eA)) != (sA + conjA)) { return $"k={k}: Trace disagrees with QuadraticSurd at step {step}"; }
            }
        }

        return null;
    }

    // ---------------------------------------------------------------------------------------------------------
    // QuadraticAlgebra<FixedQ4816>.{Add,Subtract,Negate,Conjugate} at the (0,-1) relation, full raw range — these
    // four members were entirely uncovered in the ratchet (only Multiply/Norm/MobiusStep have twin laws).

    public static string? QuadraticTwinLinearOpsFullRangeSurface() {
        var algebra = QuadraticAlgebra<FixedQ4816>.Create(p: FixedQ4816.Zero, q: FixedQ4816.NegativeOne);

        for (var step = 0; step < 2000; ++step) {
            var error = CheckQuadraticLinearOps(
                algebra: algebra,
                leftU: FixedQ4816.FromRawBits(value: DeterministicFullRangeRaw(step: step, lane: 0)),
                leftV: FixedQ4816.FromRawBits(value: DeterministicFullRangeRaw(step: step, lane: 1)),
                rightU: FixedQ4816.FromRawBits(value: DeterministicFullRangeRaw(step: step, lane: 2)),
                rightV: FixedQ4816.FromRawBits(value: DeterministicFullRangeRaw(step: step, lane: 3)),
                step: step
            );

            if (error is not null) { return error; }
        }

        foreach (var leftU in FullRangeEdgeRaws) {
            foreach (var leftV in FullRangeEdgeRaws) {
                foreach (var rightU in FullRangeEdgeRaws) {
                    foreach (var rightV in FullRangeEdgeRaws) {
                        var error = CheckQuadraticLinearOps(
                            algebra: algebra,
                            leftU: FixedQ4816.FromRawBits(value: leftU),
                            leftV: FixedQ4816.FromRawBits(value: leftV),
                            rightU: FixedQ4816.FromRawBits(value: rightU),
                            rightV: FixedQ4816.FromRawBits(value: rightV),
                            step: -1
                        );

                        if (error is not null) { return error; }
                    }
                }
            }
        }

        return null;
    }

    private static string? CheckQuadraticLinearOps(QuadraticAlgebra<FixedQ4816> algebra, FixedQ4816 leftU, FixedQ4816 leftV, FixedQ4816 rightU, FixedQ4816 rightV, int step) {
        var left = new QuadraticAlgebra<FixedQ4816>.Element(U: leftU, V: leftV);
        var right = new QuadraticAlgebra<FixedQ4816>.Element(U: rightU, V: rightV);
        var sum = algebra.Add(left: left, right: right);
        var difference = algebra.Subtract(left: left, right: right);
        var negated = algebra.Negate(value: left);
        var conjugated = algebra.Conjugate(value: left);

        if ((sum.U.Value != (leftU + rightU).Value) || (sum.V.Value != (leftV + rightV).Value)) {
            return $"Add mismatch at step {step}: ({sum.U.Value},{sum.V.Value}) != ({(leftU + rightU).Value},{(leftV + rightV).Value})";
        }
        if ((difference.U.Value != (leftU - rightU).Value) || (difference.V.Value != (leftV - rightV).Value)) {
            return $"Subtract mismatch at step {step}: ({difference.U.Value},{difference.V.Value}) != ({(leftU - rightU).Value},{(leftV - rightV).Value})";
        }
        if ((negated.U.Value != (-leftU).Value) || (negated.V.Value != (-leftV).Value)) {
            return $"Negate mismatch at step {step}: ({negated.U.Value},{negated.V.Value}) != ({(-leftU).Value},{(-leftV).Value})";
        }
        if ((conjugated.U.Value != leftU.Value) || (conjugated.V.Value != (-leftV).Value)) {
            return $"Conjugate mismatch at step {step}: ({conjugated.U.Value},{conjugated.V.Value}) != ({leftU.Value},{(-leftV).Value})";
        }

        return null;
    }

    // ---------------------------------------------------------------------------------------------------------
    // FLOOR 1: DoublingAlgebra<FixedScalarRing> vs FixedComplex, full raw range — Add, Subtract, Negate, Conjugate
    // and Norm were entirely uncovered in the ratchet (Multiply is credited generically via the octonion-floor twin
    // laws, but no case had ever exercised the Floor 1 delegation itself before this law).

    public static string? DoublingFloor1MatchesFixedComplexSurface() {
        for (var step = 0; step < 3000; ++step) {
            var a = ComplexFromRaw(real: DeterministicFullRangeRaw(step: step, lane: 0), imaginary: DeterministicFullRangeRaw(step: step, lane: 1));
            var b = ComplexFromRaw(real: DeterministicFullRangeRaw(step: step, lane: 2), imaginary: DeterministicFullRangeRaw(step: step, lane: 3));
            var error = CheckFloor1(a: a, b: b, step: step);

            if (error is not null) { return error; }
        }

        foreach (var leftReal in FullRangeEdgeRaws) {
            foreach (var leftImaginary in FullRangeEdgeRaws) {
                foreach (var rightReal in FullRangeEdgeRaws) {
                    foreach (var rightImaginary in FullRangeEdgeRaws) {
                        var a = ComplexFromRaw(real: leftReal, imaginary: leftImaginary);
                        var b = ComplexFromRaw(real: rightReal, imaginary: rightImaginary);
                        var error = CheckFloor1(a: a, b: b, step: -1);

                        if (error is not null) { return error; }
                    }
                }
            }
        }

        return null;
    }

    private static string? CheckFloor1(FixedComplex a, FixedComplex b, int step) {
        var left = ToFloor1(value: a);
        var right = ToFloor1(value: b);

        if (!MatchesComplex(actual: Floor1.Add(left: left, right: right), expected: (a + b))) { return $"floor1.add mismatch at step {step}"; }
        if (!MatchesComplex(actual: Floor1.Subtract(left: left, right: right), expected: (a - b))) { return $"floor1.sub mismatch at step {step}"; }
        if (!MatchesComplex(actual: Floor1.Multiply(left: left, right: right), expected: (a * b))) { return $"floor1.mul mismatch at step {step}"; }
        if (!MatchesComplex(actual: Floor1.Negate(value: left), expected: -a)) { return $"floor1.neg mismatch at step {step}"; }
        if (!MatchesComplex(actual: Floor1.Conjugate(value: left), expected: a.Conjugate())) { return $"floor1.conj mismatch at step {step}"; }

        if (a.TryMagnitudeSquared(out var expectedNorm)) {
            var norm = Floor1.Norm(value: left);

            if (norm.Value.Value != expectedNorm.Value) { return $"floor1.norm mismatch at step {step}: {norm.Value.Value} != {expectedNorm.Value}"; }
        }

        return null;
    }

    private static bool MatchesComplex(Floor1 actual, FixedComplex expected) =>
        ((actual.Left.Value.Value == expected.Real.Value) && (actual.Right.Value.Value == expected.Imaginary.Value));

    private static Floor1 ToFloor1(FixedComplex value) =>
        new(Left: new FixedScalarRing(Value: value.Real), Right: new FixedScalarRing(Value: value.Imaginary));

    private static FixedComplex ComplexFromRaw(long real, long imaginary) =>
        new(Real: FixedQ4816.FromRawBits(value: real), Imaginary: FixedQ4816.FromRawBits(value: imaginary));

    // ---------------------------------------------------------------------------------------------------------
    // FLOOR 2: DoublingAlgebra<DoublingAlgebra<FixedScalarRing>> vs FixedQuaternion, full raw range, PLUS an
    // independent exact-BigInteger Hamilton-product cross-check of the doubling pair convention itself.

    public static string? DoublingFloor2MatchesFixedQuaternionSurface() {
        for (var step = 0; step < 3000; ++step) {
            var a = QuaternionFromRaw(
                w: DeterministicFullRangeRaw(step: step, lane: 0), x: DeterministicFullRangeRaw(step: step, lane: 1),
                y: DeterministicFullRangeRaw(step: step, lane: 2), z: DeterministicFullRangeRaw(step: step, lane: 3));
            var b = QuaternionFromRaw(
                w: DeterministicFullRangeRaw(step: step, lane: 4), x: DeterministicFullRangeRaw(step: step, lane: 5),
                y: DeterministicFullRangeRaw(step: step, lane: 6), z: DeterministicFullRangeRaw(step: step, lane: 7));
            var error = CheckFloor2(a: a, b: b, step: step);

            if (error is not null) { return error; }
        }

        // Diagonal edge battery: all four components of each operand share one edge value — the narrow/wide seam and
        // the wrap extremes on every lane at once, without the 9^8 cost of a full cross product.
        foreach (var leftEdge in FullRangeEdgeRaws) {
            foreach (var rightEdge in FullRangeEdgeRaws) {
                var a = QuaternionFromRaw(w: leftEdge, x: leftEdge, y: leftEdge, z: leftEdge);
                var b = QuaternionFromRaw(w: rightEdge, x: rightEdge, y: rightEdge, z: rightEdge);
                var error = CheckFloor2(a: a, b: b, step: -1);

                if (error is not null) { return error; }
            }
        }

        for (var step = 0; step < 800; ++step) {
            BigInteger[] left = [
                DeterministicSmallInteger(step: step, lane: 0, bound: 1000), DeterministicSmallInteger(step: step, lane: 1, bound: 1000),
                DeterministicSmallInteger(step: step, lane: 2, bound: 1000), DeterministicSmallInteger(step: step, lane: 3, bound: 1000),
            ];
            BigInteger[] right = [
                DeterministicSmallInteger(step: step, lane: 4, bound: 1000), DeterministicSmallInteger(step: step, lane: 5, bound: 1000),
                DeterministicSmallInteger(step: step, lane: 6, bound: 1000), DeterministicSmallInteger(step: step, lane: 7, bound: 1000),
            ];
            var product = BFloor2Components(value: BFloor2.Multiply(left: BQuaternion(c: left), right: BQuaternion(c: right)));
            var reference = HamiltonReference(l: left, r: right);

            for (var lane = 0; lane < 4; ++lane) {
                if (product[lane] != reference[lane]) { return $"floor2.hamilton mismatch at step {step}, lane {lane}: {product[lane]} != {reference[lane]}"; }
            }
        }

        return null;
    }

    private static string? CheckFloor2(FixedQuaternion a, FixedQuaternion b, int step) {
        var left = ToFloor2(value: a);
        var right = ToFloor2(value: b);

        if (!MatchesQuaternion(actual: Floor2.Add(left: left, right: right), expected: (a + b))) { return $"floor2.add mismatch at step {step}"; }
        if (!MatchesQuaternion(actual: Floor2.Subtract(left: left, right: right), expected: (a - b))) { return $"floor2.sub mismatch at step {step}"; }
        if (!MatchesQuaternion(actual: Floor2.Multiply(left: left, right: right), expected: (a * b))) { return $"floor2.mul mismatch at step {step}"; }
        if (!MatchesQuaternion(actual: Floor2.Negate(value: left), expected: -a)) { return $"floor2.neg mismatch at step {step}"; }
        if (!MatchesQuaternion(actual: Floor2.Conjugate(value: left), expected: a.Conjugate())) { return $"floor2.conj mismatch at step {step}"; }

        if (a.TryLengthSquared(out var expectedNorm)) {
            var norm = Floor2.Norm(value: left);

            if (norm.Right.Value.Value != 0L) { return $"floor2.norm nonzero imaginary leaf at step {step}"; }
            if (norm.Left.Value.Value != expectedNorm.Value) { return $"floor2.norm mismatch at step {step}: {norm.Left.Value.Value} != {expectedNorm.Value}"; }
        }

        return null;
    }

    private static bool MatchesQuaternion(Floor2 actual, FixedQuaternion expected) {
        var q = FromFloor2(value: actual);

        return ((q.X.Value == expected.X.Value) && (q.Y.Value == expected.Y.Value) && (q.Z.Value == expected.Z.Value) && (q.W.Value == expected.W.Value));
    }

    private static Floor2 ToFloor2(FixedQuaternion value) =>
        new(
            Left: new Floor1(Left: new FixedScalarRing(Value: value.W), Right: new FixedScalarRing(Value: value.X)),
            Right: new Floor1(Left: new FixedScalarRing(Value: value.Y), Right: new FixedScalarRing(Value: value.Z))
        );

    private static FixedQuaternion FromFloor2(Floor2 value) =>
        new(X: value.Left.Right.Value, Y: value.Right.Left.Value, Z: value.Right.Right.Value, W: value.Left.Left.Value);

    private static FixedQuaternion QuaternionFromRaw(long w, long x, long y, long z) =>
        new(X: FixedQ4816.FromRawBits(value: x), Y: FixedQ4816.FromRawBits(value: y), Z: FixedQ4816.FromRawBits(value: z), W: FixedQ4816.FromRawBits(value: w));

    // ---------------------------------------------------------------------------------------------------------
    // FLOOR 2 non-commutativity witness, over the exact BigInteger carrier — Commutator was entirely uncovered.

    public static string? DoublingFloor2CommutatorWitnessSurface() {
        BigInteger[] i = [BigInteger.Zero, BigInteger.One, BigInteger.Zero, BigInteger.Zero];
        BigInteger[] j = [BigInteger.Zero, BigInteger.Zero, BigInteger.One, BigInteger.Zero];
        var commutator = BFloor2Components(value: BFloor2.Commutator(left: BQuaternion(c: i), right: BQuaternion(c: j)));

        foreach (var component in commutator) {
            if (component != BigInteger.Zero) { return null; }
        }

        return "expected a nonzero commutator [i,j] at the canonical quaternion basis units — the floor-2 non-commutativity witness — but every component was zero";
    }

    // ---------------------------------------------------------------------------------------------------------
    // FLOOR 3: the octonion floor's fused Norm vs an independent Int128-free BigInteger sum-of-squares oracle,
    // rounded once through the module's own dyadic rounding face — Norm was entirely uncovered on DoublingAlgebra.

    public static string? DoublingFloor3OctonionNormVsOracleSurface() {
        for (var step = 0; step < 1500; ++step) {
            var leaves = new long[8];

            for (var lane = 0; lane < 8; ++lane) { leaves[lane] = DeterministicFullRangeRaw(step: step, lane: lane); }

            var error = CheckFloor3Norm(leaves: leaves, step: step);

            if (error is not null) { return error; }
        }

        // All eight leaves strictly below 2^29: the kernel's narrow Int64 accumulation branch, which a wide draw
        // above would hit with probability roughly 2^-232 and so is never otherwise exercised.
        const long NarrowBound = (1L << 29) - 1L;

        for (var step = 0; step < 1500; ++step) {
            var leaves = new long[8];

            for (var lane = 0; lane < 8; ++lane) { leaves[lane] = DeterministicBoundedRaw(step: step, lane: (100 + lane), bound: NarrowBound); }

            var error = CheckFloor3Norm(leaves: leaves, step: step);

            if (error is not null) { return error; }
        }

        return null;
    }

    private static string? CheckFloor3Norm(long[] leaves, int step) {
        var norm = Floor3.Norm(value: ToFloor3(o: leaves));
        var sumOfSquares = BigInteger.Zero;

        for (var lane = 0; lane < 8; ++lane) { sumOfSquares += ((BigInteger)leaves[lane] * leaves[lane]); }

        var expected = Oracles.RoundDyadic(exact: sumOfSquares, shift: 16);

        if (norm.Left.Left.Value.Value != expected) {
            return $"floor3.norm mismatch at step {step}: {norm.Left.Left.Value.Value} != {expected}";
        }
        if ((norm.Left.Right.Value.Value != 0L) || (norm.Right.Left.Value.Value != 0L) || (norm.Right.Right.Value.Value != 0L)) {
            return $"floor3.norm nonzero imaginary leaf at step {step}";
        }

        return null;
    }

    private static Floor3 ToFloor3(long[] o) =>
        new(
            Left: new Floor2(
                Left: new Floor1(Left: FixedLeaf(raw: o[0]), Right: FixedLeaf(raw: o[1])),
                Right: new Floor1(Left: FixedLeaf(raw: o[2]), Right: FixedLeaf(raw: o[3]))
            ),
            Right: new Floor2(
                Left: new Floor1(Left: FixedLeaf(raw: o[4]), Right: FixedLeaf(raw: o[5])),
                Right: new Floor1(Left: FixedLeaf(raw: o[6]), Right: FixedLeaf(raw: o[7]))
            )
        );

    private static FixedScalarRing FixedLeaf(long raw) =>
        new(Value: FixedQ4816.FromRawBits(value: raw));

    // ---------------------------------------------------------------------------------------------------------
    // The exact BigInteger carrier shared by the Floor 2 Hamilton cross-check and the commutator witness — a
    // conjugation ring (conjugation is the identity), test-local because Puck.Maths ships no such carrier itself.

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

    private static BFloor1 BComplex(BigInteger real, BigInteger imaginary) =>
        new(Left: new BigIntegerRing(Value: real), Right: new BigIntegerRing(Value: imaginary));

    private static BFloor2 BQuaternion(BigInteger[] c) =>
        new(Left: BComplex(real: c[0], imaginary: c[1]), Right: BComplex(real: c[2], imaginary: c[3]));

    private static BigInteger[] BFloor2Components(BFloor2 value) => [
        value.Left.Left.Value, value.Left.Right.Value, value.Right.Left.Value, value.Right.Right.Value,
    ];

    // The independent Hamilton reference under (w,x,y,z) = (c0,c1,c2,c3) — no doubling machinery, no rounding.
    private static BigInteger[] HamiltonReference(BigInteger[] l, BigInteger[] r) {
        var (w1, x1, y1, z1) = (l[0], l[1], l[2], l[3]);
        var (w2, x2, y2, z2) = (r[0], r[1], r[2], r[3]);

        return [
            (((w1 * w2) - (x1 * x2)) - (y1 * y2)) - (z1 * z2),
            (((w1 * x2) + (x1 * w2)) + (y1 * z2)) - (z1 * y2),
            (((w1 * y2) - (x1 * z2)) + (y1 * w2)) + (z1 * x2),
            (((w1 * z2) + (x1 * y2)) - (y1 * x2)) + (z1 * w2),
        ];
    }
}
