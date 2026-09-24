using System.Numerics;

using LeafOctonion = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>>>;

namespace Puck.Maths.Tests;

/// <summary>
/// Claims over the derived kernels a presented algebra compiles to: the full 35-signature Clifford ladder against
/// <see cref="GeometricAlgebra"/>, the octonion twist's 2-cocycle failure count against the shipped doubling tower's
/// associator support, the wide GF(2^32)/GF(2^64) parity-material products against <see cref="BinaryFields"/>, the
/// sedenion two-term basis-pair zero-divisor count, and the path-algebra members' argument-validation contract.
/// </summary>
/// <remarks>
/// The oracles here are local on purpose: the bubble-sort Clifford charge, the twist-table/2-cocycle check, the GF(2)
/// carryless multiply and remainder, and the doubling-tower unit-basis construction are written out in this file and
/// called from nowhere else, so no claim shares reasoning with <see cref="LawRegistry"/>, <see cref="Subjects"/> or
/// <see cref="Oracles"/>.
/// </remarks>
internal static class PresentedKernelClaims {
    private static ulong BinaryFieldOracleProduct(ulong left, ulong right, int degree, ulong tail) {
        var product = CarrylessMultiply(
            left: left,
            right: right
        );
        var modulus = (BigInteger.One << degree) | tail;

        return ((ulong)CarrylessRemainder(
            dividend: product,
            divisor: modulus
        ));
    }
    // Reads the compiled BigInteger table back in LANE coordinates, refusing (returning null) unless every occupied
    // cell carries exactly one entry whose
    // target key is the lane XOR and whose charge is a unit +-1 sign — the shape a twisted GROUP algebra must have.
    private static int[]? BuildTwistTable(CompiledProduct<BigInteger> compiled, int[] keyToLane, int[] laneToKey) {
        var width = keyToLane.Length;
        var table = new int[(width * width)];

        for (var leftLane = 0; (leftLane < width); ++leftLane) {
            for (var rightLane = 0; (rightLane < width); ++rightLane) {
                var leftKey = laneToKey[leftLane];
                var rightKey = laneToKey[rightLane];

                if (0 == compiled.TargetCount(
                    leftKey: leftKey,
                    rightKey: rightKey
                )) { continue; }

                if (
                    (1 != compiled.TargetCount(
                    leftKey: leftKey,
                    rightKey: rightKey
                )) ||
                    (keyToLane[((int)compiled.Target(
                    leftKey: leftKey,
                    rightKey: rightKey
                ))] != (leftLane ^ rightLane))
                ) {
                    return null;
                }

                var charge = compiled.Charge(
                    leftKey: leftKey,
                    rightKey: rightKey
                );

                if (
                    (charge != BigInteger.One) &&
                    (charge != BigInteger.MinusOne)
                ) { return null; }

                table[((leftLane * width) + rightLane)] = ((int)charge);
            }
        }

        return table;
    }
    private static BigInteger CarrylessMultiply(BigInteger left, BigInteger right) {
        var product = BigInteger.Zero;

        for (var exponent = 0; (exponent <= PolynomialDegree(value: right)); ++exponent) {
            if (!((right >> exponent) & BigInteger.One).IsZero) { product ^= (left << exponent); }
        }

        return product;
    }
    private static BigInteger CarrylessRemainder(BigInteger dividend, BigInteger divisor) {
        var divisorDegree = PolynomialDegree(value: divisor);
        var remainder = dividend;

        for (var shift = (PolynomialDegree(value: remainder) - divisorDegree); (0 <= shift); shift = (PolynomialDegree(value: remainder) - divisorDegree)) {
            remainder ^= (divisor << shift);
        }

        return remainder;
    }
    // The multiplicative 2-cocycle condition sigma(a,b)*sigma(a^b,c) == sigma(b,c)*sigma(a,b^c), stated so a
    // degenerate zero is served without a
    // quotient. Its failures are exactly the support of the associator.
    private static int CocycleFailureCount(int[] twist, int width) {
        var failures = 0;

        for (var a = 0; (a < width); ++a) {
            for (var b = 0; (b < width); ++b) {
                for (var c = 0; (c < width); ++c) {
                    if ((twist[((a * width) + b)] * twist[(((a ^ b) * width) + c)]) != (twist[((b * width) + c)] * twist[((a * width) + (b ^ c))])) {
                        ++failures;
                    }
                }
            }
        }

        return failures;
    }
    private static int[] InvertBladeMap(int[] keyToBlade) {
        var bladeToKey = new int[keyToBlade.Length];

        for (var key = 0; (key < keyToBlade.Length); ++key) { bladeToKey[keyToBlade[key]] = key; }

        return bladeToKey;
    }

    // ---- local oracles (Clifford signature ladder + octonion cocycle count) ----

    internal static int[] KeyToBladeMap<TValue, TOps>(ChargedPresentation<TValue, TOps> presentation)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var count = presentation.NormalFormCount;
        var map = new int[count];

        for (var key = 0; (key < count); ++key) {
            var mask = 0;

            foreach (var symbol in presentation.NormalFormWord(key: key)) { mask |= (1 << symbol); }

            map[key] = mask;
        }

        return map;
    }

    // Builds the two coefficient-bitmask elements from the raw lanes and reads the product's support back as a
    // bitmask.
    private static ulong ParityFieldProduct(PresentedAlgebra<ulong, ParityMaterial> algebra, int degree, ulong left, ulong right) {
        var coefficients = new ulong[degree];
        var keys = new long[degree];
        var leftSupport = 0;
        var rightSupport = 0;

        for (var exponent = 0; (exponent < degree); ++exponent) {
            if (0UL != ((left >> exponent) & 1UL)) {
                coefficients[leftSupport] = 1UL;
                keys[leftSupport] = exponent;
                ++leftSupport;
            }
        }

        var leftElement = algebra.FromSupport(
            keys: keys.AsSpan(
                length: leftSupport,
                start: 0
            ),
            coefficients: coefficients.AsSpan(
                length: leftSupport,
                start: 0
            )
        );

        for (var exponent = 0; (exponent < degree); ++exponent) {
            if (0UL != ((right >> exponent) & 1UL)) {
                coefficients[rightSupport] = 1UL;
                keys[rightSupport] = exponent;
                ++rightSupport;
            }
        }

        var product = algebra.Multiply(
            left: leftElement,
            right: algebra.FromSupport(
                keys: keys.AsSpan(
                    length: rightSupport,
                    start: 0
                ),
                coefficients: coefficients.AsSpan(
                    length: rightSupport,
                    start: 0
                )
            )
        );
        var bits = 0UL;

        for (var index = 0; (index < product.SupportCount); ++index) { bits |= (1UL << ((int)product.Keys[index])); }

        return bits;
    }
    // ---- GF(2^32) / GF(2^64) against BinaryFields (existing coverage stops at degree 16) ----

    // GF(2)[x] arithmetic on a BigInteger bitmask — carryless multiply by XOR-shift-accumulate, and carryless
    // remainder by repeated degree-matched XOR — sharing no code with the presented monogenic product or the shipped
    // BinaryField<T> reduction.
    private static int PolynomialDegree(BigInteger value) =>
        (value.IsZero
            ? -1
            : (((int)value.GetBitLength()) - 1)
        );

    /// <summary>Proves the full 35-signature Clifford ladder (every <c>p+q+r&lt;=4</c> signature
    /// <see cref="GeometricAlgebra.Create(int, int, int)"/> admits — the existing twin/oracle cases between them reach
    /// only 8 of the 35): the generator-square reduction against <see cref="GeometricAlgebra.Square(int)"/>; every
    /// unit-blade product read per-lane through <see cref="GeometricAlgebra.GeometricProduct"/> so a leak into any
    /// OTHER lane is caught before the sign is read, against the bubble-sort charge oracle <see cref="Oracles.CliffordCharge"/>; the compiled
    /// exact-material table's own 2-cocycle condition; and the certificate's associativity/identity/commutativity
    /// flags together with the zero-divisor-witness count, checked against the formula (ordered blade pairs sharing a
    /// degenerate generator) rather than assumed from the signature.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? CliffordSignatureLadderMatchesGeometricAlgebra() {
        var signatures = new List<(int Positive, int Negative, int Degenerate)>();

        for (var total = 0; (total <= 4); ++total) {
            for (var positive = 0; (positive <= total); ++positive) {
                for (var negative = 0; (negative <= (total - positive)); ++negative) {
                    signatures.Add(item: (positive, negative, ((total - positive) - negative)));
                }
            }
        }

        if (35 != signatures.Count) {
            return $"the p+q+r<=4 Clifford signature enumeration produced {signatures.Count} signatures, expected 35";
        }

        foreach (var (positiveCount, negativeCount, degenerateCount) in signatures) {
            var name = $"clifford({positiveCount},{negativeCount},{degenerateCount})";
            var geometric = GeometricAlgebra.Create(
                degenerateCount: degenerateCount,
                negativeCount: negativeCount,
                positiveCount: positiveCount
            );
            var presentation = Presentations.Clifford<FixedQ4816, FixedMaterial>(
                degenerateCount: degenerateCount,
                material: default,
                negativeCount: negativeCount,
                positiveCount: positiveCount
            );
            var algebra = PresentedAlgebra<FixedQ4816, FixedMaterial>.Create(presentation: presentation);
            var compiled = algebra.Compile();
            var keyToBlade = KeyToBladeMap(presentation: presentation);
            var bladeToKey = InvertBladeMap(keyToBlade: keyToBlade);
            var dimension = geometric.BladeCount;

            if (compiled.KeyCount != dimension) {
                return $"{name}: the compiled table carries {compiled.KeyCount} keys, GeometricAlgebra reports {dimension} blades";
            }

            for (var generator = 0; (generator < geometric.GeneratorCount); ++generator) {
                var square = geometric.Square(generatorIndex: generator);
                var key = bladeToKey[(1 << generator)];
                var entries = compiled.TargetCount(
                    leftKey: key,
                    rightKey: key
                );

                if (0 == square) {
                    if (0 != entries) { return $"{name}: generator {generator} squares to zero in GeometricAlgebra but the compiled cell carries {entries} entr(ies)"; }
                } else if (
                    (1 != entries) ||
                    (0 != keyToBlade[((int)compiled.Target(
                    leftKey: key,
                    rightKey: key
                ))]) ||
                    (compiled.Charge(
                    leftKey: key,
                    rightKey: key
                ).Value != (square * FixedQ4816.One.Value))
                ) {
                    return $"{name}: generator {generator} squares to {square} in GeometricAlgebra but the compiled cell disagrees";
                }
            }

            for (var leftBlade = 0; (leftBlade < dimension); ++leftBlade) {
                for (var rightBlade = 0; (rightBlade < dimension); ++rightBlade) {
                    var left = new Multivector();
                    var right = new Multivector();

                    left[leftBlade] = FixedQ4816.One;
                    right[rightBlade] = FixedQ4816.One;

                    var product = geometric.GeometricProduct(
                        left: left,
                        right: right
                    );
                    var targetBlade = leftBlade ^ rightBlade;

                    for (var blade = 0; (blade < Multivector.BladeCapacity); ++blade) {
                        if (
                            (blade != targetBlade) &&
                            (0L != product[blade].Value)
                        ) {
                            return $"{name}: unit blades ({leftBlade},{rightBlade}) leaked a nonzero coefficient onto blade {blade}";
                        }
                    }

                    var sign = product[targetBlade].Value;
                    var oracleCharge = Oracles.CliffordCharge(
                        degenerateCount: degenerateCount,
                        leftBlade: leftBlade,
                        negativeCount: negativeCount,
                        positiveCount: positiveCount,
                        rightBlade: rightBlade
                    );

                    if (sign != (oracleCharge * FixedQ4816.One.Value)) {
                        return $"{name}: unit blades ({leftBlade},{rightBlade}) GeometricAlgebra charge {sign}, the bubble-sort oracle says {(oracleCharge * FixedQ4816.One.Value)}";
                    }

                    var leftKey = bladeToKey[leftBlade];
                    var rightKey = bladeToKey[rightBlade];
                    var cells = compiled.TargetCount(
                        leftKey: leftKey,
                        rightKey: rightKey
                    );

                    if (0 == oracleCharge) {
                        if (0 != cells) { return $"{name}: keys ({leftKey},{rightKey}) should annihilate but carry {cells} entr(ies)"; }
                    } else if (
                        (1 != cells) ||
                        (keyToBlade[((int)compiled.Target(
                        leftKey: leftKey,
                        rightKey: rightKey
                    ))] != targetBlade) ||
                        (compiled.Charge(
                        leftKey: leftKey,
                        rightKey: rightKey
                    ).Value != sign)
                    ) {
                        return $"{name}: keys ({leftKey},{rightKey}) compiled entry disagrees with the unit-blade charge {sign}";
                    }
                }
            }

            var integerPresentation = Presentations.Clifford<BigInteger, IntegerMaterial>(
                degenerateCount: degenerateCount,
                material: default,
                negativeCount: negativeCount,
                positiveCount: positiveCount
            );
            var integerAlgebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: integerPresentation);
            var integerCompiled = integerAlgebra.Compile();

            if (BuildTwistTable(
                compiled: integerCompiled,
                keyToLane: keyToBlade,
                laneToKey: bladeToKey
            ) is not { } twist) {
                return $"{name}: the compiled BigInteger table is not a twisted group algebra (a cell holds more than one entry, or a non-unit charge)";
            }

            if (0 != CocycleFailureCount(
                twist: twist,
                width: dimension
            )) {
                return $"{name}: the derived twist fails the 2-cocycle condition on at least one ordered triple of lanes";
            }

            var certificate = integerAlgebra.Certify(tupleLimit: (1L << 20));
            var degenerateMask = ((1 << geometric.GeneratorCount) - 1) & ~((1 << (positiveCount + negativeCount)) - 1);
            var expectedDivisors = 0;

            for (var leftBlade = 0; (leftBlade < dimension); ++leftBlade) {
                for (var rightBlade = 0; (rightBlade < dimension); ++rightBlade) {
                    if (0 != ((leftBlade & rightBlade) & degenerateMask)) { ++expectedDivisors; }
                }
            }

            if (
                (ClosureOutcome.BasisAssociativityVerified != certificate.Outcome) ||
                !certificate.IsAssociative ||
                !certificate.HasIdentity ||
                (0L != certificate.NonAssociativeTripleCount) ||
                (certificate.IsCommutative != (geometric.GeneratorCount <= 1)) ||
                (certificate.ZeroDivisorWitness.Length != expectedDivisors)
            ) {
                return $"{name}: certificate outcome={certificate.Outcome} associative={certificate.IsAssociative} unital={certificate.HasIdentity} nonassociative={certificate.NonAssociativeTripleCount} commutative={certificate.IsCommutative} zero-divisors={certificate.ZeroDivisorWitness.Length}, expected {expectedDivisors}";
            }
        }

        return null;
    }
    /// <summary>Proves the octonion floor's compiled-table 2-cocycle failure count equals the number of ordered
    /// basis triples on which the SHIPPED doubling tower's own product disagrees on bracketing — <c>(a.b).c</c>
    /// against <c>a.(b.c)</c>, formed through <see cref="DoublingAlgebra{TInner}"/> alone, which shares no code with
    /// the presented Clifford/twist machinery <see cref="CliffordSignatureLadderMatchesGeometricAlgebra"/> and this
    /// case's own twist table both use. The count is pinned at 168 of 512 as a REGRESSION PIN, set by observing the
    /// subject — the sibling <c>PresentedModuleClaims.LiveAssociatorMatchesDoublingTower</c> case reaches the same
    /// count by an unrelated route.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? OctonionTwistCocycleCountMatchesDoublingAssociatorSupport() {
        const int Width = 8;
        var presentation = Presentations.CayleyDickson<BigInteger, IntegerMaterial>(
            floors: 3,
            basisRelabelling: [],
            material: default
        );
        var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: presentation);
        var compiled = algebra.Compile();
        var identity = new int[Width];

        for (var lane = 0; (lane < Width); ++lane) { identity[lane] = lane; }

        if (BuildTwistTable(
            compiled: compiled,
            keyToLane: identity,
            laneToKey: identity
        ) is not { } twist) {
            return "the octonion floor's compiled table is not a twisted group algebra";
        }

        var cocycleFailures = CocycleFailureCount(
            twist: twist,
            width: Width
        );
        var nonAssociativeTriples = 0;

        for (var a = 0; (a < Width); ++a) {
            for (var b = 0; (b < Width); ++b) {
                for (var c = 0; (c < Width); ++c) {
                    var before = LeafOctonion.Multiply(
                        left: LeafOctonion.Multiply(
                            left: DoublingTower.UnitOctonionAt(index: a),
                            right: DoublingTower.UnitOctonionAt(index: b)
                        ),
                        right: DoublingTower.UnitOctonionAt(index: c)
                    );
                    var after = LeafOctonion.Multiply(
                        left: DoublingTower.UnitOctonionAt(index: a),
                        right: LeafOctonion.Multiply(
                            left: DoublingTower.UnitOctonionAt(index: b),
                            right: DoublingTower.UnitOctonionAt(index: c)
                        )
                    );

                    if (before != after) { ++nonAssociativeTriples; }
                }
            }
        }

        if (cocycleFailures != nonAssociativeTriples) {
            return $"the derived twist fails the 2-cocycle condition on {cocycleFailures} ordered triples, the shipped doubling tower disagrees on bracketing on {nonAssociativeTriples}";
        }

        if (168 != cocycleFailures) {
            return $"the octonion floor's 2-cocycle failure count is {cocycleFailures}, expected 168 (regression pin, set by observing the subject)";
        }

        return null;
    }
    /// <summary>Proves that <see cref="PresentedAlgebra{TValue, TOps}.FromSupport"/>,
    /// <see cref="CompiledProduct{TValue}.Target(long, long)"/> and <see cref="PresentedAlgebra{TValue, TOps}.Generator"/>
    /// refuse an out-of-range key, key or symbol BY SHAPE — naming the refused parameter — rather than by a wrong
    /// answer, and that a keys/coefficients length mismatch is refused as a distinct argument fault. No existing case
    /// exercises this negative-space contract on a path-algebra (quiver) presentation.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PathAlgebraArgumentValidationRefusesByShape() {
        var algebra = PresentedAlgebra<BigInteger, CountingMaterial>.Create(presentation: Presentations.Quiver<BigInteger, CountingMaterial>(
            objectCount: 5,
            arrows: [(0, 1, BigInteger.One)],
            material: default
        ));

        if (!Throws<ArgumentOutOfRangeException>(
            action: () => algebra.FromSupport(
                keys: [99L],
                coefficients: [BigInteger.One]
            ),
            paramName: "keys"
        )) {
            return "FromSupport admitted a key outside the normal-form range, or refused naming a different parameter";
        }

        if (!Throws<ArgumentOutOfRangeException>(
            action: () => algebra.Compile().Target(
                leftKey: -1L,
                rightKey: 0L
            ),
            paramName: "leftKey"
        )) {
            return "CompiledProduct.Target admitted a negative leftKey, or refused naming a different parameter";
        }

        if (!Throws<ArgumentOutOfRangeException>(
            action: () => algebra.Generator(symbol: 999),
            paramName: "symbol"
        )) {
            return "Generator admitted an out-of-range symbol, or refused naming a different parameter";
        }

        if (!ThrowsExactly<ArgumentException>(
            action: () => algebra.FromSupport(
                keys: [0L, 1L],
                coefficients: [BigInteger.One]
            ),
            paramName: "coefficients"
        )) {
            return "FromSupport admitted a mismatched keys/coefficients length, or refused naming a different parameter";
        }

        return null;
    }
    // ---- sedenion two-term basis-pair zero divisors ----

    /// <summary>Proves the sedenion tower carries exactly 84 zero divisors among the two-term basis-pair sums —
    /// a STRONGER statement than the basis-level <see cref="PresentationCertificate{TValue}.ZeroDivisorWitness"/>
    /// check the Cayley-Dickson certificate ladder already proves (no BASIS pair is a zero divisor at any floor), read
    /// entirely through the subject's own <c>Multiply</c> and <c>SupportCount</c>. The count 84 is a REGRESSION PIN,
    /// set by observing the subject, not an independently derived classical fact.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? SedenionPairSumZeroDivisorCount() {
        var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(
            floors: 4,
            basisRelabelling: [],
            material: default
        ));
        var witnesses = 0;

        for (var firstLeft = 1; (firstLeft < 16); ++firstLeft) {
            for (var secondLeft = (firstLeft + 1); (secondLeft < 16); ++secondLeft) {
                var left = algebra.FromSupport(
                    keys: [firstLeft, secondLeft],
                    coefficients: [BigInteger.One, BigInteger.One]
                );

                for (var firstRight = 1; (firstRight < 16); ++firstRight) {
                    for (var secondRight = (firstRight + 1); (secondRight < 16); ++secondRight) {
                        var right = algebra.FromSupport(
                            keys: [firstRight, secondRight],
                            coefficients: [BigInteger.One, BigInteger.One]
                        );

                        if (0 == algebra.Multiply(
                            left: left,
                            right: right
                        ).SupportCount) { ++witnesses; }
                    }
                }
            }
        }

        if (84 != witnesses) {
            return $"the sedenion tower carries {witnesses} two-term basis-pair zero divisors, expected 84 (regression pin, set by observing the subject)";
        }

        return null;
    }
    /// <summary>Proves the presented monogenic-over-<see cref="ParityMaterial"/> product equal to
    /// <see cref="BinaryFields.Degree32"/> and <see cref="BinaryFields.Degree64"/> — the two shipped GF(2^k) kernels no
    /// existing case reaches (<c>presented.gf2-twins-binaryfield</c> stops at degree 16) — and to the local
    /// carryless-reduction oracle, at 256 index-derived samples per degree (no <see cref="Random"/>, seeded or
    /// otherwise; every operand is a deterministic multiplicative hash of the sample index).</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? WideBinaryFieldTwinsShippedKernel() {
        var cases = new (int Degree, ulong Tail)[] { (32, 0x8DUL), (64, 0x1BUL) };

        foreach (var (degree, tail) in cases) {
            var modulus = new ulong[degree];

            for (var exponent = 0; (exponent < degree); ++exponent) { modulus[exponent] = (tail >> exponent) & 1UL; }

            var algebra = PresentedAlgebra<ulong, ParityMaterial>.Create(presentation: Presentations.Monogenic<ulong, ParityMaterial>(
                material: default,
                modulus: modulus
            ));
            var mask = ((64 == degree)
                ? ulong.MaxValue
                : ((1UL << degree) - 1UL)
            );

            for (var sample = 0; (sample < 256); ++sample) {
                var left = unchecked((((ulong)((sample * 2) + 1)) * 0x9E3779B97F4A7C15UL)) & mask;
                var right = unchecked((((ulong)((sample * 2) + 2)) * 0xBF58476D1CE4E5B9UL)) & mask;

                var subject = ParityFieldProduct(
                    algebra: algebra,
                    degree: degree,
                    left: left,
                    right: right
                );
                var oracle = BinaryFieldOracleProduct(
                    degree: degree,
                    left: left,
                    right: right,
                    tail: tail
                );
                var shipped = ((32 == degree)
                    ? ((ulong)BinaryFields.Degree32.Multiply(
                        left: ((uint)left),
                        right: ((uint)right)
                    ))
                    : BinaryFields.Degree64.Multiply(
                        left: left,
                        right: right
                    )
                );

                if (
                    (subject != shipped) ||
                    (subject != oracle)
                ) {
                    return $"degree {degree} sample {sample}: presented={subject:X} BinaryFields={shipped:X} oracle={oracle:X}";
                }
            }
        }

        return null;
    }
}
