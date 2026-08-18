using System.Numerics;
using LeafComplex = Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>;
using LeafOctonion = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>>>;
using LeafQuaternion = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>>;
using LeafSedenion = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>>>>;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    /// <summary>Proves the material contract every fused kernel rests on, at EVERY material: the charged linear fold is
    /// the charged bilinear fold at a constant one, bit for bit and in both lanes; a signed material's subtraction is its
    /// addition composed with its negation; a complemented material's complement is a De Morgan involution; and a field
    /// material's inversion round-trips on units and refuses the zero it names as the witness.</summary>
    /// <param name="charges">The charge lanes.</param>
    /// <param name="values">The value lanes.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? MaterialFusedIdentities(long[] charges, long[] values) {
        var boolean = MaterialIdentities<bool, BooleanMaterial>(
            name: "boolean",
            material: default,
            charges: Map(
                selector: static raw => (0L != (raw & 1L)),
                source: charges
            ),
            values: Map(
                selector: static raw => (0L != (raw & 1L)),
                source: values
            )
        );

        if (boolean is not null) { return boolean; }

        var boundedSum = MaterialIdentities<UnitInterval32, BoundedSumMaterial>(
            name: "bounded-sum",
            material: default,
            charges: Map(
                source: charges,
                selector: static raw => ClosedUnit(raw: raw)
            ),
            values: Map(
                source: values,
                selector: static raw => ClosedUnit(raw: raw)
            )
        );

        if (boundedSum is not null) { return boundedSum; }

        var counting = MaterialIdentities<BigInteger, CountingMaterial>(
            name: "counting",
            material: default,
            charges: Map(
                source: charges,
                selector: static raw => BigInteger.Abs(value: raw)
            ),
            values: Map(
                source: values,
                selector: static raw => BigInteger.Abs(value: raw)
            )
        );

        if (counting is not null) { return counting; }

        var fixedPoint = MaterialIdentities<FixedQ4816, FixedMaterial>(
            name: "fixed",
            material: default,
            charges: Map(
                source: charges,
                selector: static raw => FixedQ4816.FromRawBits(value: raw)
            ),
            values: Map(
                source: values,
                selector: static raw => FixedQ4816.FromRawBits(value: raw)
            )
        );

        if (fixedPoint is not null) { return fixedPoint; }

        // The second complemented material, and the first whose complement is graded: the De Morgan leg of the shared
        // helper runs here for real rather than on two truth values.
        var fuzzy = MaterialIdentities<UnitInterval32, FuzzyMaterial>(
            name: "fuzzy",
            material: default,
            charges: Map(
                source: charges,
                selector: static raw => ClosedUnit(raw: raw)
            ),
            values: Map(
                source: values,
                selector: static raw => ClosedUnit(raw: raw)
            )
        );

        if (fuzzy is not null) { return fuzzy; }

        var integer = MaterialIdentities<BigInteger, IntegerMaterial>(
            name: "integer",
            material: default,
            charges: Map(
                selector: static raw => ((BigInteger)raw),
                source: charges
            ),
            values: Map(
                selector: static raw => ((BigInteger)raw),
                source: values
            )
        );

        if (integer is not null) { return integer; }

        var mostLikelyPath = MaterialIdentities<UnitInterval32, MostLikelyPathMaterial>(
            name: "most-likely-path",
            material: default,
            charges: Map(
                source: charges,
                selector: static raw => ClosedUnit(raw: raw)
            ),
            values: Map(
                source: values,
                selector: static raw => ClosedUnit(raw: raw)
            )
        );

        if (mostLikelyPath is not null) { return mostLikelyPath; }

        var parity = MaterialIdentities<ulong, ParityMaterial>(
            name: "parity",
            material: default,
            charges: Map(
                selector: static raw => (((ulong)raw) & 1UL),
                source: charges
            ),
            values: Map(
                selector: static raw => (((ulong)raw) & 1UL),
                source: values
            )
        );

        if (parity is not null) { return parity; }

        var primeMaterial = PrimeFieldMaterial.Create(modulus: PrimeFieldModulus);
        var primeField = MaterialIdentities<ulong, PrimeFieldMaterial>(
            name: "prime-field",
            material: primeMaterial,
            charges: Map(
                selector: static raw => (((ulong)raw) % PrimeFieldModulus),
                source: charges
            ),
            values: Map(
                selector: static raw => (((ulong)raw) % PrimeFieldModulus),
                source: values
            )
        );

        if (primeField is not null) { return primeField; }

        if (PrimeFieldModulus != primeMaterial.Field.Modulus) { return $"prime-field: the material carries modulus {primeMaterial.Field.Modulus}, expected {PrimeFieldModulus}"; }

        if (PrimeFieldModulus != new PrimeFieldMaterial(field: PrimeField64.Create(modulus: PrimeFieldModulus)).Field.Modulus) { return "prime-field: the wrapping constructor lost the field"; }

        var rational = MaterialIdentities<QuadraticSurd, RationalMaterial>(
            name: "rational",
            material: default,
            charges: Map(
                source: charges,
                selector: static raw => QuadraticSurd.Rational(
                    denominator: 65536,
                    numerator: raw
                )
            ),
            values: Map(
                source: values,
                selector: static raw => QuadraticSurd.Rational(
                    denominator: 65536,
                    numerator: raw
                )
            )
        );

        if (rational is not null) { return rational; }

        var fusedFolds = FusedFoldsMatchExactFolds(
            charges: charges,
            values: values
        );

        if (fusedFolds is not null) { return fusedFolds; }

        return MaterialIdentities<FixedQ4816, TropicalMaterial>(
            name: "tropical",
            material: default,
            charges: Map(
                source: charges,
                selector: static raw => FixedQ4816.FromRawBits(value: raw & 0xFFFFL)
            ),
            values: Map(
                source: values,
                selector: static raw => FixedQ4816.FromRawBits(value: raw & 0xFFFFL)
            )
        );
    }

    // The odd prime the prime-field material runs at. Small enough that a raw reduces into the field cheaply, large
    // enough that the reduction is not degenerate.
    private const ulong PrimeFieldModulus = 1000003UL;
    private const long OneRaw = 65536L;

    // The associator's coefficient on its lowest-key support entry, recomputed from the compiled table alone. Every
    // Cayley-Dickson cell is a single signed basis element, so both bracketings land on the SAME key and the associator
    // is the difference of two charges there.
    private static BigInteger AssociatorLeadingCharge(CompiledProduct<BigInteger> compiled, int left, int middle, int right) {
        var before = (compiled.Charge(
            leftKey: left,
            rightKey: middle
        ) * compiled.Charge(
            leftKey: left ^ middle,
            rightKey: right
        ));
        var after = (compiled.Charge(
            leftKey: middle,
            rightKey: right
        ) * compiled.Charge(
            leftKey: left,
            rightKey: middle ^ right
        ));

        return (before - after);
    }
    private static PresentedAlgebra<FixedQ4816, FixedMaterial>.Element BasisElement(PresentedAlgebra<FixedQ4816, FixedMaterial> algebra, long key) =>
        algebra.FromSupport(
            keys: [key],
            coefficients: [FixedQ4816.One]
        );
    private static FixedLaneAlgebra CayleyDicksonBinding(int floors) {
        var presentation = Presentations.CayleyDickson<FixedQ4816, FixedMaterial>(
            floors: floors,
            basisRelabelling: [],
            material: default
        );
        var keyToLane = new int[presentation.NormalFormCount];

        // A Cayley-Dickson generator IS a basis element, so the single-letter normal form's only symbol is its lane.
        for (var key = 0; (key < keyToLane.Length); ++key) { keyToLane[key] = presentation.NormalFormWord(key: key)[0]; }

        return new FixedLaneAlgebra(
            keyToLane: keyToLane,
            presentation: presentation
        );
    }
    private static FixedLaneAlgebra CliffordBinding(int positiveCount, int negativeCount, int degenerateCount) {
        var presentation = Presentations.Clifford<FixedQ4816, FixedMaterial>(
            degenerateCount: degenerateCount,
            material: default,
            negativeCount: negativeCount,
            positiveCount: positiveCount
        );
        var keyToLane = new int[presentation.NormalFormCount];

        // A Clifford normal form is an ascending generator subset; its lane is that subset read as a bitmask, which is
        // exactly the blade index a Multivector uses. The two orders differ — keys are graded-lexicographic — so the
        // permutation is recomputed here from the presentation rather than assumed.
        for (var key = 0; (key < keyToLane.Length); ++key) {
            var mask = 0;

            foreach (var symbol in presentation.NormalFormWord(key: key)) { mask |= (1 << symbol); }

            keyToLane[key] = mask;
        }

        return new FixedLaneAlgebra(
            keyToLane: keyToLane,
            presentation: presentation
        );
    }
    private static Func<int, int, int> CliffordChargeSource(int positiveCount, int negativeCount, int degenerateCount) {
        var count = (1 << ((positiveCount + negativeCount) + degenerateCount));
        var table = new int[(count * count)];

        for (var left = 0; (left < count); ++left) {
            for (var right = 0; (right < count); ++right) {
                table[((left * count) + right)] = Oracles.CliffordCharge(
                    degenerateCount: degenerateCount,
                    leftBlade: left,
                    negativeCount: negativeCount,
                    positiveCount: positiveCount,
                    rightBlade: right
                );
            }
        }

        return (left, right) => table[((left * count) + right)];
    }
    // The first key at which two elements differ, rendered; null when they are identical.
    private static string? Difference(in PresentedAlgebra<FixedQ4816, FixedMaterial>.Element left, in PresentedAlgebra<FixedQ4816, FixedMaterial>.Element right) {
        if (left.SupportCount != right.SupportCount) { return $"support {left.SupportCount} vs {right.SupportCount}"; }

        for (var index = 0; (index < left.SupportCount); ++index) {
            if (
                (left.Keys[index] != right.Keys[index]) ||
                (left.Coefficients[index].Value != right.Coefficients[index].Value)
            ) {
                return $"index {index}: ({left.Keys[index]},{left.Coefficients[index].Value}) vs ({right.Keys[index]},{right.Coefficients[index].Value})";
            }
        }

        return null;
    }
    private static BigInteger GraphMultiplicity(long left, long right) =>
        ((0L == (right & 1L))
            ? BigInteger.Zero
            : (BigInteger.One + ((left >>> 5) & 3L))
        );
    private static long GraphWeight(long raw) =>
        raw & 0xFFFFL;
    private static TValue[] Map<TValue>(long[] source, Func<long, TValue> selector) {
        var mapped = new TValue[source.Length];

        for (var index = 0; (index < source.Length); ++index) { mapped[index] = selector(source[index]); }

        return mapped;
    }
    // The general-charge regime's third leg (worklist B1): the two ROUNDING materials' fused folds, at arbitrary charges
    // and arbitrary left AND right coefficients, against the same folds recomputed exactly in BigInteger. Every other
    // classical pin on FusedChargedSum in the tree runs at Clifford charges in {−1, 0, +1}, and every multi-term pin on
    // MostLikelyPathMaterial runs at charges of one, where the fused and pairwise disciplines coincide by construction.
    // The other nine materials are exact and need nothing.
    //
    // ENVELOPE: the raws are reduced to thirty-two signed bits. That is the regime in which the subject's own Int128
    // accumulator cannot overflow at these widths, so the statement is about the ROUNDING and never about a wrap of the
    // accumulator, which is outside the audited envelope on both sides.
    private static string? FusedFoldsMatchExactFolds(long[] charges, long[] values) {
        var width = charges.Length;
        var chargeRaws = new long[width];
        var leftRaws = new long[width];
        var rightRaws = new long[width];

        for (var index = 0; (index < width); ++index) {
            chargeRaws[index] = (charges[index] >> 32);
            leftRaws[index] = (values[index] >> 32);
            rightRaws[index] = (values[((index + 1) % width)] >> 32);
        }

        var fixedCharges = Map(
            source: chargeRaws,
            selector: static raw => FixedQ4816.FromRawBits(value: raw)
        );
        var fixedLeft = Map(
            source: leftRaws,
            selector: static raw => FixedQ4816.FromRawBits(value: raw)
        );
        var fixedRight = Map(
            source: rightRaws,
            selector: static raw => FixedQ4816.FromRawBits(value: raw)
        );
        var exactLinear = BigInteger.Zero;
        var generalLinear = BigInteger.Zero;
        var exactSum = BigInteger.Zero;
        var generalSum = BigInteger.Zero;

        for (var index = 0; (index < width); ++index) {
            var charge = ((BigInteger)chargeRaws[index]);
            var left = ((BigInteger)leftRaws[index]);
            var right = ((BigInteger)rightRaws[index]);
            var narrowed = (charge >> 16);

            exactLinear += (narrowed * left);
            generalLinear += (charge * left);
            exactSum += (narrowed * (left * right));
            generalSum += (charge * (left * right));
        }

        FixedMaterial fixedMaterial = default;
        var checks = new (string What, long Actual, long Expected)[] {
            ("FixedMaterial.FusedChargedLinear at ChargeLane.Exact",
                fixedMaterial.FusedChargedLinear(
            charges: fixedCharges,
            lane: ChargeLane.Exact,
            values: fixedLeft
        ).Value,
                Oracles.WrapToRaw(value: exactLinear)),
            ("FixedMaterial.FusedChargedLinear at ChargeLane.General",
                fixedMaterial.FusedChargedLinear(
            charges: fixedCharges,
            lane: ChargeLane.General,
            values: fixedLeft
        ).Value,
                Oracles.RoundDyadic(
            exact: generalLinear,
            shift: 16
        )),
            ("FixedMaterial.FusedChargedSum at ChargeLane.Exact",
                fixedMaterial.FusedChargedSum(
            charges: fixedCharges,
            lane: ChargeLane.Exact,
            left: fixedLeft,
            right: fixedRight
        ).Value,
                Oracles.RoundDyadic(
            exact: exactSum,
            shift: 16
        )),
            ("FixedMaterial.FusedChargedSum at ChargeLane.General",
                fixedMaterial.FusedChargedSum(
            charges: fixedCharges,
            lane: ChargeLane.General,
            left: fixedLeft,
            right: fixedRight
        ).Value,
                Oracles.RoundDyadic(
            exact: generalSum,
            shift: 32
        )),
        };

        foreach (var (what, actual, expected) in checks) {
            if (actual != expected) { return $"{what} folds to {actual} where one rounding of the exact sum is {expected}"; }
        }

        // The likelihood material rounds PER TERM by construction — its fold is a maximum, which selects rather than
        // accumulates — so the exact reference is the same selection over independently rounded exact products.
        MostLikelyPathMaterial likelihoodMaterial = default;
        var likelihoodCharges = Map(
            source: chargeRaws,
            selector: static raw => ClosedUnit(raw: raw)
        );
        var likelihoodLeft = Map(
            source: leftRaws,
            selector: static raw => ClosedUnit(raw: raw)
        );
        var likelihoodRight = Map(
            source: rightRaws,
            selector: static raw => ClosedUnit(raw: raw)
        );
        var likelihoodLinear = 0UL;
        var likelihoodSum = 0UL;

        for (var index = 0; (index < width); ++index) {
            var charge = ClosedUnitRaw(raw: chargeRaws[index]);
            var left = ClosedUnitRaw(raw: leftRaws[index]);
            var right = ClosedUnitRaw(raw: rightRaws[index]);

            likelihoodLinear = Math.Max(
                val1: likelihoodLinear,
                val2: Oracles.ClosedUnitProduct(
                    x: charge,
                    y: left
                )
            );
            likelihoodSum = Math.Max(
                val1: likelihoodSum,
                val2: Oracles.ClosedUnitTripleProduct(
                    x: charge,
                    y: left,
                    z: right
                )
            );
        }

        if (likelihoodMaterial.FusedChargedLinear(
            charges: likelihoodCharges,
            lane: ChargeLane.General,
            values: likelihoodLeft
        ).Value != likelihoodLinear) {
            return $"MostLikelyPathMaterial.FusedChargedLinear folds to {likelihoodMaterial.FusedChargedLinear(
                charges: likelihoodCharges,
                lane: ChargeLane.General,
                values: likelihoodLeft
            ).Value} where the max over one rounding per term is {likelihoodLinear}";
        }

        if (likelihoodMaterial.FusedChargedSum(
            charges: likelihoodCharges,
            lane: ChargeLane.General,
            left: likelihoodLeft,
            right: likelihoodRight
        ).Value != likelihoodSum) {
            return $"MostLikelyPathMaterial.FusedChargedSum folds to {likelihoodMaterial.FusedChargedSum(
                charges: likelihoodCharges,
                lane: ChargeLane.General,
                left: likelihoodLeft,
                right: likelihoodRight
            ).Value} where the max over one rounding of each exact triple product is {likelihoodSum}";
        }

        return null;
    }

    /// <summary>Pins the two Oracle material-contract boundaries: residue admission is canonical, and only materials
    /// whose whole carrier satisfies the semiring laws advertise the exact-semiring marker. It also exercises the
    /// tropical overflow edge and both shipped schedule-dependent associativity counterexamples.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the contract holds.</returns>
    public static string? OracleMaterialContractBoundaries() {
        if (
            !IsExactSemiring<bool, BooleanMaterial>(material: default) ||
            !IsExactSemiring<ulong, ParityMaterial>(material: default) ||
            !IsExactSemiring<BigInteger, CountingMaterial>(material: default) ||
            !IsExactSemiring<FixedQ4816, TropicalMaterial>(material: default) ||
            !IsExactSemiring<BigInteger, IntegerMaterial>(material: default) ||
            !IsExactSemiring<QuadraticSurd, RationalMaterial>(material: default) ||
            !IsExactSemiring<ulong, PrimeFieldMaterial>(material: PrimeFieldMaterial.Create(modulus: 5UL)) ||
            !IsExactSemiring<UnitInterval32, FuzzyMaterial>(material: default) ||
            !IsExactSemiring<UnitInterval32, BoundedSumMaterial>(material: default)
        ) {
            return "an exact shipped semiring does not advertise IExactSemiringMaterial";
        }

        if (
            IsExactSemiring<FixedQ4816, FixedMaterial>(material: default) ||
            IsExactSemiring<UnitInterval32, MostLikelyPathMaterial>(material: default)
        ) {
            return "a schedule-dependent rounded material advertises IExactSemiringMaterial";
        }

        var fixedMaterial = default(FixedMaterial);
        var fixedA = FixedQ4816.FromRawBits(value: 3_032_738_444L);
        var fixedB = FixedQ4816.FromRawBits(value: 64_279_523L);
        var fixedC = FixedQ4816.FromRawBits(value: 1_581_944_319L);
        var fixedLeft = fixedMaterial.Multiply(
            left: fixedMaterial.Multiply(
                left: fixedA,
                right: fixedB
            ),
            right: fixedC
        );
        var fixedRight = fixedMaterial.Multiply(
            left: fixedA,
            right: fixedMaterial.Multiply(
                left: fixedB,
                right: fixedC
            )
        );

        if (
            (71_802_395_543_139_229L != fixedLeft.Value) ||
            (71_802_395_543_144_108L != fixedRight.Value)
        ) {
            return $"the fixed schedule counterexample moved to ({fixedLeft.Value},{fixedRight.Value})";
        }

        var likelihood = default(MostLikelyPathMaterial);
        var likelyA = UnitInterval32.Create(value: 2_606_326_770UL);
        var likelyB = UnitInterval32.Create(value: 1_545_851_103UL);
        var likelyC = UnitInterval32.Create(value: 4_203_973_715UL);
        var likelyLeft = likelihood.Multiply(
            left: likelihood.Multiply(
                left: likelyA,
                right: likelyB
            ),
            right: likelyC
        );
        var likelyRight = likelihood.Multiply(
            left: likelyA,
            right: likelihood.Multiply(
                left: likelyB,
                right: likelyC
            )
        );

        if (
            (918_198_956UL != likelyLeft.Value) ||
            (918_198_955UL != likelyRight.Value)
        ) {
            return $"the likelihood schedule counterexample moved to ({likelyLeft.Value},{likelyRight.Value})";
        }

        var tropical = default(TropicalMaterial);
        var finiteMaximum = FixedQ4816.FromRawBits(value: (long.MaxValue - 1L));

        if (!tropical.IsZero(value: tropical.Multiply(
            left: finiteMaximum,
            right: FixedQ4816.FromRawBits(value: 2L)
        ))) {
            return "tropical multiplication wrapped past its finite maximum instead of saturating to infinity";
        }

        if ((long.MaxValue - 1L) != tropical.Multiply(
            left: FixedQ4816.FromRawBits(value: (long.MaxValue - 2L)),
            right: FixedQ4816.FromRawBits(value: 1L)
        ).Value) {
            return "tropical multiplication saturated before the last finite weight";
        }

        if (!Throws<ArgumentOutOfRangeException>(action: () => tropical.Multiply(
            left: FixedQ4816.FromRawBits(value: -1L),
            right: tropical.One
        ))) {
            return "tropical multiplication admitted a negative carrier value";
        }

        if (!Throws<ArgumentOutOfRangeException>(action: () => tropical.Canonicalize(value: FixedQ4816.FromRawBits(value: -1L)))) {
            return "tropical canonicalization admitted a negative carrier value";
        }

        var tropicalGenerator = new Generator(
            symbol: 0,
            inputs: ReadOnlyMemory<int>.Empty,
            outputs: ReadOnlyMemory<int>.Empty,
            degree: 1
        );

        if (!Throws<ArgumentOutOfRangeException>(
            action: () => _ = ChargedPresentation<FixedQ4816, TropicalMaterial>.Create(
                generators: [tropicalGenerator],
                rules: [],
                material: tropical,
                generatorCharges: [FixedQ4816.FromRawBits(value: -1L)]
            ),
            paramName: "generatorCharges"
        )) {
            return "presentation admission accepted a negative tropical generator charge or named the wrong parameter";
        }

        var negativeTropicalRule = new RewriteRule<FixedQ4816>(
            kind: RuleKind.Reduce,
            pattern: new int[] { 0, 0 },
            replacement: RewriteRule<FixedQ4816>.PackReplacement([0]),
            charges: new FixedQ4816[] { FixedQ4816.FromRawBits(value: -1L) }
        );

        if (!Throws<ArgumentException>(
            action: () => _ = ChargedPresentation<FixedQ4816, TropicalMaterial>.Create(
                generators: [tropicalGenerator],
                rules: [negativeTropicalRule],
                material: tropical
            ),
            paramName: "rules"
        )) {
            return "presentation admission accepted a negative tropical rule charge or named the wrong parameter";
        }

        var prime = PrimeFieldMaterial.Create(modulus: 5UL);

        if (
            (0UL != prime.Canonicalize(value: 5UL)) ||
            (1UL != prime.Canonicalize(value: 6UL)) ||
            !prime.IsZero(value: 5UL) ||
            !prime.IsZero(value: 10UL) ||
            prime.TryInvert(
            inverse: out var zeroInverse,
            value: 5UL
        ) ||
            (0UL != zeroInverse) ||
            !prime.TryInvert(
            inverse: out var oneInverse,
            value: 6UL
        ) ||
            (1UL != oneInverse)
        ) {
            return "PrimeFieldMaterial does not reduce before zero or inversion decisions";
        }

        var primeAlgebra = PresentedAlgebra<ulong, PrimeFieldMaterial>.Create(presentation: Presentations.Shift<ulong, PrimeFieldMaterial>(
            degreeBound: 1,
            material: prime
        ));
        var reducedZero = primeAlgebra.FromSupport(
            coefficients: [5UL],
            keys: [0L]
        );
        var reducedOne = primeAlgebra.FromSupport(
            coefficients: [6UL],
            keys: [0L]
        );

        if (
            (0 != reducedZero.SupportCount) ||
            !primeAlgebra.AreEqual(
            left: reducedOne,
            right: primeAlgebra.Identity
        )
        ) {
            return "PresentedAlgebra.FromSupport retained a noncanonical prime-field coefficient";
        }

        var generator = new Generator(
            symbol: 0,
            inputs: ReadOnlyMemory<int>.Empty,
            outputs: ReadOnlyMemory<int>.Empty,
            degree: 1
        );

        if (!Throws<ArgumentOutOfRangeException>(action: () => _ = ChargedPresentation<ulong, PrimeFieldMaterial>.Create(
            generators: [generator],
            rules: [],
            material: prime,
            generatorCharges: [6UL]
        ))) {
            return "presentation admission accepted a noncanonical generator charge";
        }

        var noncanonicalRule = new RewriteRule<ulong>(
            kind: RuleKind.Reduce,
            pattern: new int[] { 0, 0 },
            replacement: RewriteRule<ulong>.PackReplacement([0]),
            charges: new ulong[] { 6UL }
        );

        if (!Throws<ArgumentException>(action: () => _ = ChargedPresentation<ulong, PrimeFieldMaterial>.Create(
            generators: [generator],
            rules: [noncanonicalRule],
            material: prime
        ))) {
            return "presentation admission accepted a noncanonical rule charge";
        }

        return null;
    }

    private static bool IsExactSemiring<TValue, TOps>(TOps material)
        where TOps : struct, IMaterialOps<TValue, TOps> =>
        (material is IExactSemiringMaterial<TValue, TOps>);
    private static string? MaterialIdentities<TValue, TOps>(string name, TOps material, TValue[] charges, TValue[] values)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var comparer = EqualityComparer<TValue>.Default;
        var ones = new TValue[values.Length];

        for (var index = 0; (index < ones.Length); ++index) {
            ones[index] = material.One;

            if (!comparer.Equals(
                x: material.Canonicalize(value: values[index]),
                y: values[index]
            )) {
                return $"{name}: an admitted value is not in canonical form";
            }
        }

        foreach (var lane in new[] { ChargeLane.Exact, ChargeLane.General }) {
            var linear = material.FusedChargedLinear(
                charges: charges,
                lane: lane,
                values: values
            );
            var bilinear = material.FusedChargedSum(
                charges: charges,
                lane: lane,
                left: values,
                right: ones
            );

            if (!comparer.Equals(
                x: linear,
                y: bilinear
            )) {
                return $"{name}: FusedChargedLinear {linear} differs from FusedChargedSum at one {bilinear} in lane {lane}";
            }

            if (!comparer.Equals(
                x: material.FusedChargedSum(
                    charges: [],
                    lane: lane,
                    left: [],
                    right: []
                ),
                y: material.Zero
            )) {
                return $"{name}: the empty fused sum is not the material's zero in lane {lane}";
            }
        }

        if (
            !material.IsZero(value: material.Zero) ||
            material.IsZero(value: material.One)
        ) {
            return $"{name}: IsZero does not separate the material's two identities";
        }

        for (var index = 0; (index < values.Length); ++index) {
            if (
                !comparer.Equals(
                x: material.Multiply(
                    left: values[index],
                    right: material.One
                ),
                y: values[index]
            ) ||
                !comparer.Equals(
                x: material.Add(
                    left: values[index],
                    right: material.Zero
                ),
                y: values[index]
            )
            ) {
                return $"{name}: {values[index]} is moved by the material's identities";
            }
        }

        if (material is ISignedMaterial<TValue, TOps> signed) {
            for (var index = 0; (index < values.Length); ++index) {
                if (!comparer.Equals(
                    x: signed.Subtract(
                        left: charges[index],
                        right: values[index]
                    ),
                    y: signed.Add(
                        left: charges[index],
                        right: signed.Negate(value: values[index])
                    )
                )) {
                    return $"{name}: Subtract({charges[index]},{values[index]}) is not Add composed with Negate";
                }
            }
        }

        if (material is IComplementedMaterial<TValue, TOps> complemented) {
            for (var index = 0; (index < values.Length); ++index) {
                if (!comparer.Equals(
                    x: complemented.Complement(value: complemented.Complement(value: values[index])),
                    y: values[index]
                )) {
                    return $"{name}: the complement of {values[index]} is not an involution";
                }

                // De Morgan: the complement carries the semiring's addition to its multiplication and back.
                var left = complemented.Complement(value: complemented.Add(
                    left: values[index],
                    right: charges[index]
                ));
                var right = complemented.Multiply(
                    left: complemented.Complement(value: values[index]),
                    right: complemented.Complement(value: charges[index])
                );

                if (!comparer.Equals(
                    x: left,
                    y: right
                )) { return $"{name}: De Morgan fails at ({values[index]},{charges[index]})"; }
            }
        }

        if (material is IFieldMaterial<TValue, TOps> field) {
            if (
                field.TryInvert(
                value: material.Zero,
                inverse: out var zeroInverse
            ) ||
                !comparer.Equals(
                x: zeroInverse,
                y: material.Zero
            )
            ) {
                return $"{name}: the material's zero must be refused, with the zero returned as the non-unit witness";
            }

            for (var index = 0; (index < values.Length); ++index) {
                if (material.IsZero(value: values[index])) { continue; }

                if (
                    !field.TryInvert(
                    value: values[index],
                    inverse: out var inverse
                ) ||
                    !comparer.Equals(
                    x: material.Multiply(
                        left: values[index],
                        right: inverse
                    ),
                    y: material.One
                )
                ) {
                    return $"{name}: {values[index]} did not invert back to the material's one";
                }
            }
        }

        return null;
    }
    private static BigInteger PackBits(ReadOnlySpan<long> lanes) {
        var packed = BigInteger.Zero;

        for (var lane = 0; (lane < lanes.Length); ++lane) {
            if (0L != (lanes[lane] & 1L)) { packed |= (BigInteger.One << lane); }
        }

        return packed;
    }
    private static FixedLaneAlgebra QuadraticBinding(long pRaw, long qRaw) {
        // The monogenic tail [m₀, m₁] states x² = −m₀ − m₁·x, so the relation x² = P·x + Q is the tail [−Q, −P]; the
        // presentation's own key IS the exponent, so lane and key coincide.
        var presentation = Presentations.Monogenic<FixedQ4816, FixedMaterial>(
            modulus: [Raw(value: unchecked(-qRaw)), Raw(value: unchecked(-pRaw))],
            material: default
        );

        return new FixedLaneAlgebra(
            keyToLane: [0, 1],
            presentation: presentation
        );
    }
    private static LeafOctonion ReadOctonion(ReadOnlySpan<long> lanes) {
        // Lane index bit two selects the outer half, bit one the middle, bit zero the innermost — the tower's own
        // (Z/2)³ coordinate order.
        static DoublingAlgebra<FixedScalarRing> Pair(ReadOnlySpan<long> lanes, int offset) =>
            new(
                Left: new FixedScalarRing(Value: Raw(value: lanes[offset])),
                Right: new FixedScalarRing(Value: Raw(value: lanes[(offset + 1)]))
            );

        return new LeafOctonion(
            Left: new DoublingAlgebra<DoublingAlgebra<FixedScalarRing>>(
                Left: Pair(
                    lanes: lanes,
                    offset: 0
                ),
                Right: Pair(
                    lanes: lanes,
                    offset: 2
                )
            ),
            Right: new DoublingAlgebra<DoublingAlgebra<FixedScalarRing>>(
                Left: Pair(
                    lanes: lanes,
                    offset: 4
                ),
                Right: Pair(
                    lanes: lanes,
                    offset: 6
                )
            )
        );
    }
    private static void UnpackBits<T>(T value, Span<long> lanes) where T : IBinaryInteger<T> {
        for (var lane = 0; (lane < lanes.Length); ++lane) {
            lanes[lane] = (T.IsZero(value: (value >> lane) & T.One)
                ? 0L
                : 1L
            );
        }
    }

    // ---- re-association helpers, shared by the two phase-3 claims ----

    private const long NormalizationSteps = (1L << 20);

    private static Term BracketPair(Term left, Term right) =>
        Term.Node(
            children: [left, right],
            symbol: Term.Product
        );
    private static Generator[] SingleColourBasis(int count) {
        var generators = new Generator[count];

        for (var symbol = 0; (symbol < count); ++symbol) {
            generators[symbol] = new Generator(
                degree: 1,
                inputs: new int[] { 0 },
                outputs: new int[] { 0 },
                symbol: symbol
            );
        }

        return generators;
    }
    private static PresentedAlgebra<TValue, TOps>.Element PresentedBasis<TValue, TOps>(PresentedAlgebra<TValue, TOps> algebra, long key)
        where TOps : struct, IMaterialOps<TValue, TOps> =>
        algebra.FromSupport(
            keys: [key],
            coefficients: [algebra.Presentation.Material.One]
        );
    // The oracle for one bracketing: the SAME tree evaluated bracket by bracket through the compiled product, which
    // re-associates nothing and so shares no step with the flat rewriter the normalizer runs.
    private static string? NormalizesTo<TValue, TOps>(PresentedAlgebra<TValue, TOps> algebra, in Term term, in PresentedAlgebra<TValue, TOps>.Element expected)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        if (!algebra.TryNormalize(
            normalForm: out var normalForm,
            obstruction: out var obstruction,
            stepLimit: NormalizationSteps,
            term: term
        )) {
            return $"did not normalize (steps={obstruction.StepsTaken} blocked={obstruction.BlockedKey})";
        }

        if (algebra.AreEqual(
            left: normalForm,
            right: expected
        )) { return null; }

        return $"normalized to [{ElementText(value: normalForm)}], its own nested products give [{ElementText(value: expected)}]";
    }
    private static string ElementText<TValue, TOps>(in PresentedAlgebra<TValue, TOps>.Element value)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var parts = new string[value.SupportCount];

        for (var index = 0; (index < parts.Length); ++index) { parts[index] = $"{value.Keys[index]}:{value.Coefficients[index]}"; }

        return string.Join(
            separator: ' ',
            values: parts
        );
    }
    // All five bracketings of a quadruple, each against its own nested products. Four factors is the smallest arity at
    // which two DIFFERENT rebalancing routes reach the flat word, which is exactly what coherence is a statement about.
    private static string? QuadrupleBracketingsAgree<TValue, TOps>(PresentedAlgebra<TValue, TOps> algebra, int first, int second, int third, int fourth)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var a = Term.Leaf(symbol: first);
        var b = Term.Leaf(symbol: second);
        var c = Term.Leaf(symbol: third);
        var d = Term.Leaf(symbol: fourth);
        var w = PresentedBasis(
            algebra: algebra,
            key: first
        );
        var x = PresentedBasis(
            algebra: algebra,
            key: second
        );
        var y = PresentedBasis(
            algebra: algebra,
            key: third
        );
        var z = PresentedBasis(
            algebra: algebra,
            key: fourth
        );
        var trees = new[] {
            BracketPair(
            left: BracketPair(
                left: BracketPair(
                    left: a,
                    right: b
                ),
                right: c
            ),
            right: d
        ),
            BracketPair(
            left: BracketPair(
                left: a,
                right: BracketPair(
                    left: b,
                    right: c
                )
            ),
            right: d
        ),
            BracketPair(
            left: BracketPair(
                left: a,
                right: b
            ),
            right: BracketPair(
                left: c,
                right: d
            )
        ),
            BracketPair(
            left: a,
            right: BracketPair(
                left: BracketPair(
                    left: b,
                    right: c
                ),
                right: d
            )
        ),
            BracketPair(
            left: a,
            right: BracketPair(
                left: b,
                right: BracketPair(
                    left: c,
                    right: d
                )
            )
        ),
        };
        var values = new[] {
            algebra.Multiply(
            left: algebra.Multiply(
                left: algebra.Multiply(
                    left: w,
                    right: x
                ),
                right: y
            ),
            right: z
        ),
            algebra.Multiply(
            left: algebra.Multiply(
                left: w,
                right: algebra.Multiply(
                    left: x,
                    right: y
                )
            ),
            right: z
        ),
            algebra.Multiply(
            left: algebra.Multiply(
                left: w,
                right: x
            ),
            right: algebra.Multiply(
                left: y,
                right: z
            )
        ),
            algebra.Multiply(
            left: w,
            right: algebra.Multiply(
                left: algebra.Multiply(
                    left: x,
                    right: y
                ),
                right: z
            )
        ),
            algebra.Multiply(
            left: w,
            right: algebra.Multiply(
                left: x,
                right: algebra.Multiply(
                    left: y,
                    right: z
                )
            )
        ),
        };

        for (var shape = 0; (shape < trees.Length); ++shape) {
            if (NormalizesTo(
                algebra: algebra,
                term: trees[shape],
                expected: values[shape]
            ) is { } detail) { return $"at bracketing {shape} {detail}"; }
        }

        return null;
    }
    // Bracket-inertness: with a uniform charge of one, every bracketing of one leaf word — nested either way, and the
    // n-ary node that brackets nothing — normalizes to the same element. It is the phase-1 statement, and it is what a
    // splice charge leaking into the uniform regime would break.
    private static string? BracketsAreInert<TValue, TOps>(string name, ChargedPresentation<TValue, TOps> presentation)
        where TOps : struct, IMaterialOps<TValue, TOps> {
        var algebra = PresentedAlgebra<TValue, TOps>.Create(presentation: presentation);

        if (presentation.HasLiveReassociation) { return $"{name}: the catalogue entry declares a live re-association charge"; }

        var symbols = presentation.GeneratorCount;

        for (var first = 0; (first < symbols); ++first) {
            for (var second = 0; (second < symbols); ++second) {
                for (var third = 0; (third < symbols); ++third) {
                    var a = Term.Leaf(symbol: first);
                    var b = Term.Leaf(symbol: second);
                    var c = Term.Leaf(symbol: third);
                    var trees = new[] {
                        Term.Node(
                        children: [a, b, c],
                        symbol: Term.Product
                    ),
                        BracketPair(
                        left: BracketPair(
                            left: a,
                            right: b
                        ),
                        right: c
                    ),
                        BracketPair(
                        left: a,
                        right: BracketPair(
                            left: b,
                            right: c
                        )
                    ),
                        Term.Node(
                        symbol: first,
                        children: [BracketPair(
                                left: b,
                                right: c
                            )]
                    ),
                        Term.Node(
                        symbol: Term.Product,
                        children: [Term.Unit, BracketPair(
                                left: a,
                                right: BracketPair(
                                    left: b,
                                    right: c
                                )
                            )]
                    ),
                    };

                    if (!algebra.TryNormalize(
                        term: trees[0],
                        stepLimit: NormalizationSteps,
                        normalForm: out var expected,
                        obstruction: out var obstruction
                    )) {
                        return $"{name}: the flat term ({first},{second},{third}) did not normalize (steps={obstruction.StepsTaken} blocked={obstruction.BlockedKey})";
                    }

                    for (var shape = 1; (shape < trees.Length); ++shape) {
                        if (!algebra.TryNormalize(
                            term: trees[shape],
                            stepLimit: NormalizationSteps,
                            normalForm: out var bracketed,
                            obstruction: out var refusal
                        )) {
                            return $"{name}: bracketing {shape} of ({first},{second},{third}) did not normalize (steps={refusal.StepsTaken} blocked={refusal.BlockedKey})";
                        }

                        if (!algebra.AreEqual(
                            left: bracketed,
                            right: expected
                        )) {
                            return $"{name}: bracketing {shape} of ({first},{second},{third}) normalized to [{ElementText(value: bracketed)}], the flat term to [{ElementText(value: expected)}]";
                        }
                    }
                }
            }
        }

        return null;
    }
    // The three-element group with a 3-cochain that is NOT a cocycle: the entry at (1, 1, 1) flipped, which is enough
    // to make the two rebalancing routes of a quadruple charge differently while leaving the declaration normalized at
    // the unit, which admission requires. The cocycle that IS one lives at the same shape over two elements and
    // differs only in the order and in which entry moves, so the two share one builder.
    private static ChargedPresentation<BigInteger, IntegerMaterial> PerturbedCochainPresentation() =>
        CyclicGroupPresentation(
            flippedTriple: PerturbedTriple,
            order: PerturbedOrder
        );

    // The rank-two bonds the dihedral family is pinned at: the commuting pair, the three smallest odd and even bonds,
    // and two wider ones, so a normal-form count that drifted with the bond would be caught rather than fitted.
    private static readonly int[] DihedralBonds = [2, 3, 4, 5, 6, 8, 12];
    // The letters of a rank-three Coxeter element, in the order that makes its alternating word irreducible: the two
    // commuting mirrors first and the branch mirror last.
    private static readonly int[] CoxeterElementWord = [0, 2, 1];
    // Coxeter diagrams whose connected pieces all have rank at most two, where the involution and braid rules decide the
    // word problem outright. Each order is the product of its pieces': eight is three commuting involutions, and
    // forty-eight is a dihedral six times a dihedral eight.
    private static readonly (string Name, int Rank, int[] Bonds, int Order)[] ReducibleCoxeterWorlds = [
        ("three commuting mirrors", 3, [1, 2, 2, 2, 1, 2, 2, 2, 1], 8),
        ("dihedral(3) times dihedral(4)", 4, [1, 3, 2, 2, 3, 1, 2, 2, 2, 2, 1, 4, 2, 2, 4, 1], 48),
    ];
    // The reflection worlds a bounded enumeration reaches, with the order each one is a theorem about: one mirror gives
    // an involution, two commuting mirrors the Klein group, and the chains of bonds of three the symmetric groups on
    // three, four and five letters.
    private static readonly (int[] Mirrors, int Order)[] EnumerableReflectionWorlds = [
        ([0], 2),
        ([1, 2], 4),
        ([0, 2], 6),
        ([0, 2, 3], 24),
        ([0, 2, 3, 4], 120),
    ];

    private static int Factorial(int value) {
        var total = 1;

        for (var factor = 2; (factor <= value); ++factor) { total *= factor; }

        return total;
    }
    // Whether the bonded pairs form a path: one edge fewer than the rank, no mirror bonded to three others, and one
    // connected piece. A path of bonds of three is the diagram whose group is the symmetric group on rank+1 letters.
    private static bool IsChainDiagram(ReadOnlySpan<int> bonds, int rank) {
        var degrees = new int[rank];
        var edges = 0;

        for (var high = 1; (high < rank); ++high) {
            for (var low = 0; (low < high); ++low) {
                if (3 != bonds[((high * rank) + low)]) { continue; }

                ++degrees[high];
                ++degrees[low];
                ++edges;
            }
        }

        if (
            (edges != (rank - 1)) ||
            (MaximumOf(values: degrees) > 2)
        ) { return false; }

        var reached = new bool[rank];
        var frontier = new List<int> { 0 };

        reached[0] = true;

        for (var cursor = 0; (cursor < frontier.Count); ++cursor) {
            for (var next = 0; (next < rank); ++next) {
                if (
                    reached[next] ||
                    (3 != bonds[((frontier[cursor] * rank) + next)])
                ) { continue; }

                reached[next] = true;
                frontier.Add(item: next);
            }
        }

        return (frontier.Count == rank);
    }
    private static int MaximumOf(ReadOnlySpan<int> values) {
        var largest = 0;

        foreach (var value in values) {
            largest = Math.Max(
            val1: largest,
            val2: value
        );
        }

        return largest;
    }
    // The mixed-radix key a presentation with no finite basis gives a word, recomputed here so the law reads a normal
    // form as the word it is rather than as an opaque number.
    private static long PackedWord(ReadOnlySpan<int> word, int generatorCount) {
        var radix = (((long)generatorCount) + 1L);
        var key = 0L;
        var scale = 1L;

        for (var index = 0; (index < word.Length); ++index) {
            key += (scale * (word[index] + 1L));
            scale *= radix;
        }

        return key;
    }
    // The group regime of one algebra, proved whole: every generator carries a witness that multiplies out to the unit,
    // every basis element inverts and its inverse is multiplied out and checked, and the orbit of the unit is the whole
    // basis, which is the statement that the basis IS a group rather than merely containing units.
    private static string? GroupRegimeIsWhole(string name, PresentedAlgebra<BigInteger, IntegerMaterial> algebra, int order, bool enumerateOrbit) {
        if (!PresentedGroup<BigInteger, IntegerMaterial>.TryCertify(
            algebra: algebra,
            group: out var group,
            obstruction: out var obstruction
        )) {
            return $"{name}: the group regime refused with outcome={obstruction.Outcome} symbol={obstruction.BlockedSymbol} searched={obstruction.PointsReached}";
        }

        if (!ReferenceEquals(
            objA: group.Algebra,
            objB: algebra
        )) { return $"{name}: the certified group names another algebra"; }

        var identity = algebra.Identity;
        var witnesses = group.UnitWitnesses;

        if (witnesses.Length != algebra.Presentation.GeneratorCount) {
            return $"{name}: {witnesses.Length} witness(es) against {algebra.Presentation.GeneratorCount} generator(s)";
        }

        for (var symbol = 0; (symbol < witnesses.Length); ++symbol) {
            var witness = witnesses[symbol];
            var inverse = algebra.FromSupport(
                keys: [witness.InverseKey],
                coefficients: [witness.InverseCharge]
            );

            if (
                (symbol != witness.Symbol) ||
                !algebra.AreEqual(
                left: algebra.Multiply(
                    left: algebra.Generator(symbol: symbol),
                    right: inverse
                ),
                right: identity
            ) ||
                !algebra.AreEqual(
                left: algebra.Multiply(
                    left: inverse,
                    right: algebra.Generator(symbol: symbol)
                ),
                right: identity
            )
            ) {
                return $"{name}: the witness of generator {symbol} is ({witness.Symbol},{witness.InverseKey},{witness.InverseCharge}), which does not invert it";
            }
        }

        for (var key = 0L; (key < order); ++key) {
            var element = PresentedBasis(
                algebra: algebra,
                key: key
            );

            if (!group.TryInvert(
                inverse: out var inverse,
                obstruction: out var refusal,
                value: element
            )) {
                return $"{name}: basis element {key} did not invert (outcome={refusal.Outcome} key={refusal.BlockedKey} letters={refusal.PointsReached})";
            }

            if (
                !algebra.AreEqual(
                left: algebra.Multiply(
                    left: element,
                    right: inverse
                ),
                right: identity
            ) ||
                !algebra.AreEqual(
                left: algebra.Multiply(
                    left: inverse,
                    right: element
                ),
                right: identity
            )
            ) {
                return $"{name}: the inverse returned for basis element {key} is not one";
            }
        }

        if (!enumerateOrbit) { return null; }

        if (!group.TryEnumerateOrbit(
            seedKey: identity.Keys[0],
            searchLimit: order,
            orbit: out var orbit,
            obstruction: out var orbitObstruction
        )) {
            return $"{name}: the orbit of the unit refused with {orbitObstruction.Outcome} after {orbitObstruction.PointsReached} key(s)";
        }

        if (order != orbit.Length) { return $"{name}: the orbit of the unit holds {orbit.Length} key(s) against an order of {order}"; }

        for (var index = 1; (index < orbit.Length); ++index) {
            if (orbit.Span[index] <= orbit.Span[(index - 1)]) { return $"{name}: the orbit of the unit is not ascending at {index}"; }
        }

        return null;
    }
    // The word presentation and the table presentation of one reflection group, held equal as algebras: the map sending
    // a normal-form word to the permutation it acts as is a bijection and carries the product to the product. It is the
    // only place the two constructions meet, and neither one computed the other.
    private static string? WordAndTableAgree(string name, ReflectionSystem system, ReadOnlySpan<int> permutations, PresentedAlgebra<BigInteger, IntegerMaterial> table) {
        var pointCount = system.Points.Length;
        var order = (permutations.Length / pointCount);
        var word = Presentations.Coxeter<BigInteger, IntegerMaterial>(
            rank: system.Mirrors.Length,
            bonds: system.BondMatrix,
            material: default
        );
        var wordAlgebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: word);

        if (word.NormalFormCount != order) {
            return $"{name}: the word presentation holds {word.NormalFormCount} normal form(s) against an order of {order}";
        }

        var map = new int[order];
        var image = new int[pointCount];

        for (var key = 0; (key < order); ++key) {
            for (var point = 0; (point < pointCount); ++point) {
                image[point] = system.Points.IndexOf(value: system.Apply(
                    word: word.NormalFormWord(key: key),
                    node: system.Points[point]
                ));
            }

            map[key] = -1;

            for (var element = 0; (element < order); ++element) {
                if (permutations.Slice(
                    length: pointCount,
                    start: (element * pointCount)
                ).SequenceEqual(other: image)) { map[key] = element; }
            }

            if (map[key] < 0) { return $"{name}: the normal form {key} acts as no enumerated element"; }
        }

        for (var left = 0; (left < order); ++left) {
            for (var right = (left + 1); (right < order); ++right) {
                if (map[left] == map[right]) { return $"{name}: the normal forms {left} and {right} act as the same element"; }
            }
        }

        for (var left = 0; (left < order); ++left) {
            for (var right = 0; (right < order); ++right) {
                var product = wordAlgebra.Multiply(
                    left: PresentedBasis(
                        algebra: wordAlgebra,
                        key: left
                    ),
                    right: PresentedBasis(
                        algebra: wordAlgebra,
                        key: right
                    )
                );
                var composed = table.Multiply(
                    left: PresentedBasis(
                        algebra: table,
                        key: map[left]
                    ),
                    right: PresentedBasis(
                        algebra: table,
                        key: map[right]
                    )
                );

                if (
                    (1 != product.SupportCount) ||
                    (1 != composed.SupportCount) ||
                    (map[product.Keys[0]] != composed.Keys[0])
                ) {
                    return $"{name}: the word product ({left},{right}) and the table product of their images disagree";
                }
            }
        }

        return null;
    }
    // A refusal has to be an ArgumentException AND has to name the parameter that carried the offending data, since a
    // refusal with no parameter tells a caller nothing about which argument to fix. The name itself is not compared:
    // the helper serves two families and roughly thirty cases, and a per-case expected name would pin the exception's
    // wording rather than its content. What it does pin is that the refusal is attributable at all.
    private static string? RefusesDeclaration(string name, Action build) {
        try {
            build();
        } catch (ArgumentException refusal) {
            return ((refusal.ParamName is null)
                ? $"{name} was refused without naming a parameter, so the refusal does not say which argument carried the data"
                : null
            );
        }

        return $"{name} was admitted; data that names no such object is refused rather than approximated";
    }
    private static Term WordTerm(ReadOnlySpan<int> word) {
        var children = new Term[word.Length];

        for (var index = 0; (index < word.Length); ++index) { children[index] = Term.Leaf(symbol: word[index]); }

        return Term.Node(
            children: children,
            symbol: Term.Product
        );
    }
    private static void WriteOctonion(LeafOctonion value, Span<long> lanes) {
        lanes[0] = value.Left.Left.Left.Value.Value;
        lanes[1] = value.Left.Left.Right.Value.Value;
        lanes[2] = value.Left.Right.Left.Value.Value;
        lanes[3] = value.Left.Right.Right.Value.Value;
        lanes[4] = value.Right.Left.Left.Value.Value;
        lanes[5] = value.Right.Left.Right.Value.Value;
        lanes[6] = value.Right.Right.Left.Value.Value;
        lanes[7] = value.Right.Right.Right.Value.Value;
    }
    // ---- the doubling tower as a witness, at every floor it ships ----
    //
    // The charge one ordered pair of unit basis elements carries, reached by MULTIPLYING both units out through the
    // shipped nested tower and reading the sign off the product's own lane. It reads no compiled cell, runs no step the
    // presented product runs, and transcribes nothing: it is the second implementation, which is what a transcribed
    // charge oracle needs standing beside it. The tower ships four floors, and so does this.

    private static FixedScalarRing UnitScalarAt(int index, int offset) =>
        new(Value: ((offset == index)
            ? FixedQ4816.One
            : FixedQ4816.Zero));
    private static LeafComplex UnitComplexAt(int index, int offset) =>
        new(
            Left: UnitScalarAt(
                index: index,
                offset: offset
            ),
            Right: UnitScalarAt(
                index: index,
                offset: (offset + 1)
            )
        );
    private static LeafQuaternion UnitQuaternionAt(int index, int offset) =>
        new(
            Left: UnitComplexAt(
                index: index,
                offset: offset
            ),
            Right: UnitComplexAt(
                index: index,
                offset: (offset + 2)
            )
        );
    private static LeafOctonion UnitOctonionAt(int index, int offset) =>
        new(
            Left: UnitQuaternionAt(
                index: index,
                offset: offset
            ),
            Right: UnitQuaternionAt(
                index: index,
                offset: (offset + 4)
            )
        );
    private static LeafSedenion UnitSedenionAt(int index, int offset) =>
        new(
            Left: UnitOctonionAt(
                index: index,
                offset: offset
            ),
            Right: UnitOctonionAt(
                index: index,
                offset: (offset + 8)
            )
        );
    private static int DoublingTowerUnitCharge(int left, int right, int floors) {
        var lanes = new long[(1 << floors)];

        switch (floors) {
            case 1:
                WriteComplexLanes(
                    value: LeafComplex.Multiply(
                        left: UnitComplexAt(
                            index: left,
                            offset: 0
                        ),
                        right: UnitComplexAt(
                            index: right,
                            offset: 0
                        )
                    ),
                    lanes: lanes,
                    offset: 0
                );

                break;
            case 2:
                WriteQuaternionLanes(
                    value: LeafQuaternion.Multiply(
                        left: UnitQuaternionAt(
                            index: left,
                            offset: 0
                        ),
                        right: UnitQuaternionAt(
                            index: right,
                            offset: 0
                        )
                    ),
                    lanes: lanes,
                    offset: 0
                );

                break;
            case 3:
                WriteOctonion(
                    value: LeafOctonion.Multiply(
                        left: UnitOctonionAt(
                            index: left,
                            offset: 0
                        ),
                        right: UnitOctonionAt(
                            index: right,
                            offset: 0
                        )
                    ),
                    lanes: lanes
                );

                break;
            default:
                var sedenion = LeafSedenion.Multiply(
                    left: UnitSedenionAt(
                        index: left,
                        offset: 0
                    ),
                    right: UnitSedenionAt(
                        index: right,
                        offset: 0
                    )
                );

                WriteOctonionAt(
                    value: sedenion.Left,
                    lanes: lanes,
                    offset: 0
                );
                WriteOctonionAt(
                    value: sedenion.Right,
                    lanes: lanes,
                    offset: 8
                );

                break;
        }

        return Math.Sign(value: lanes[left ^ right]);
    }
    private static void WriteComplexLanes(LeafComplex value, Span<long> lanes, int offset) {
        lanes[offset] = value.Left.Value.Value;
        lanes[(offset + 1)] = value.Right.Value.Value;
    }
    private static void WriteQuaternionLanes(LeafQuaternion value, Span<long> lanes, int offset) {
        WriteComplexLanes(
            value: value.Left,
            lanes: lanes,
            offset: offset
        );
        WriteComplexLanes(
            value: value.Right,
            lanes: lanes,
            offset: (offset + 2)
        );
    }
    private static void WriteOctonionAt(LeafOctonion value, Span<long> lanes, int offset) {
        WriteQuaternionLanes(
            value: value.Left,
            lanes: lanes,
            offset: offset
        );
        WriteQuaternionLanes(
            value: value.Right,
            lanes: lanes,
            offset: (offset + 4)
        );
    }

    // ---- phase 2: modules by presentation morphism ----
    //
    // Every binding below is the same kernel at another presentation. Nothing here adds arithmetic: a tensor is a
    // presentation, a machine is a module, a derivative is the residual operator, and each cross-check is either an
    // already-shipped kernel or a shared-nothing oracle.

    /// <summary>The number of normal forms each tensor factor carries; the pair key's stride.</summary>
    public const int TensorFactorKeys = 2;

    /// <summary>The subject tensor behavior over the exact integers: the behavior of a pair-up, written to lane zero.</summary>
    /// <returns>The bound operation. The operand vectors carry <c>(a, b, u)</c> on the left and <c>(c, d, v)</c> on the
    /// right, two lanes each: the two factors' states, the two factors' steps, and the two factors' readouts.</returns>
    public static VectorBinaryOp PresentedTensorBehavior() {
        TensorBinding<BigInteger, IntegerMaterial>? binding = null;

        return (left, right, result) => {
            binding ??= IntegerTensorBinding();

            result.Clear();
            result[0] = ((long)binding.PairedBehavior(
                left: left,
                right: right,
                map: static raw => SmallInteger(raw: raw)
            ));
        };
    }
    /// <summary>The SECOND SIDE of the pair-up theorem, not an oracle: the termwise product of the two factors' own
    /// behaviors, each computed in its own factor algebra and multiplied afterwards.</summary>
    /// <returns>The bound side.</returns>
    /// <remarks>It sits in an oracle-shaped slot and CALLS <c>PresentedAlgebra.Behavior</c>, so agreement with
    /// <see cref="PresentedTensorBehavior"/> is the presented object against itself at another entry point and never
    /// independent evidence (worklist T13, O14). The leg that stands outside both is
    /// <see cref="ExactTensorBehavior"/>.</remarks>
    public static VectorBinaryOp TensorBehaviorProductOracle() {
        TensorBinding<BigInteger, IntegerMaterial>? binding = null;

        return (left, right, result) => {
            binding ??= IntegerTensorBinding();

            result.Clear();
            result[0] = ((long)binding.BehaviorProduct(
                left: left,
                right: right,
                map: static raw => SmallInteger(raw: raw)
            ));
        };
    }
    /// <summary>The subject tensor behavior over the house scalar — the same statement on the rounding carrier.</summary>
    /// <returns>The bound operation.</returns>
    public static VectorBinaryOp PresentedFixedTensorBehavior() {
        TensorBinding<FixedQ4816, FixedMaterial>? binding = null;

        return (left, right, result) => {
            binding ??= FixedTensorBinding();

            result.Clear();
            result[0] = binding.PairedBehavior(
                left: left,
                map: Raw,
                right: right
            ).Value;
        };
    }
    /// <summary>The termwise product of behaviors over the house scalar — the discipline the pair-up is claimed to
    /// DIVERGE from, because a tensor's cells are not products of already-rounded cells.</summary>
    /// <returns>The bound side.</returns>
    /// <remarks>Named for the slot it fills rather than for what it is: like
    /// <see cref="TensorBehaviorProductOracle"/> it calls <c>PresentedAlgebra.Behavior</c>, so the canary's two sides
    /// are one kernel at two entry points (worklist T13, O14). What pins this side absolutely is
    /// <see cref="FixedTensorBehaviorProductIsExact"/>.</remarks>
    public static VectorBinaryOp FixedTensorBehaviorProductOracle() {
        TensorBinding<FixedQ4816, FixedMaterial>? binding = null;

        return (left, right, result) => {
            binding ??= FixedTensorBinding();

            result.Clear();
            result[0] = binding.BehaviorProduct(
                left: left,
                map: Raw,
                right: right
            ).Value;
        };
    }
    /// <summary>The ABSOLUTE sibling of the pair-up canary, on the rounding carrier: the termwise product of the two
    /// factors' behaviors over <see cref="FixedQ4816"/>, against the same quantity restated in <see cref="BigInteger"/>
    /// with one rounding per step.</summary>
    /// <returns>The swept claim.</returns>
    /// <remarks>
    /// The canary asks <c>PairedBehavior</c> and <c>BehaviorProduct</c> to DIFFER, and both sides run
    /// <c>PresentedAlgebra.Behavior</c> over <c>FixedMaterial</c>, so a quiet change to the pairing fold moves both
    /// together and the canary goes quiet without failing. This is what R7 asks for beside it, in the claim-(y) pattern:
    /// each factor's behavior is the fused fold, and it is required to equal the exact value restated in BigInteger.
    /// The two factors are the degree-two relations <c>x² = x + 1</c> and <c>x² = −1</c>, so each factor product is one
    /// ties-to-even rounding per component of the exact Q48 expression; each factor's pairing is the EXACT-lane charged
    /// linear fold, which narrows the covector's charges by sixteen bits and rounds nothing; and the two behaviors meet
    /// in one more rounding of their exact product. An exact-material theorem sibling does not discharge this: it says
    /// the theorem holds where nothing rounds, which is silent about the regime the canary lives in.
    /// </remarks>
    public static Func<long[], long[], string?> FixedTensorBehaviorProductIsExact() {
        var oracle = FixedTensorBehaviorProductOracle();
        var lanes = new long[TensorLaneWidth];

        return (left, right) => {
            oracle(
                left,
                right,
                lanes
            );

            var leftBehavior = FactorBehaviorOracle(
                pRaw: OneRaw,
                qRaw: OneRaw,
                stateRaw: left[0],
                stateRootRaw: left[1],
                stepRaw: right[0],
                stepRootRaw: right[1],
                readoutRaw: left[4],
                readoutRootRaw: left[5]
            );
            var rightBehavior = FactorBehaviorOracle(
                pRaw: 0L,
                qRaw: -OneRaw,
                stateRaw: left[2],
                stateRootRaw: left[3],
                stepRaw: right[2],
                stepRootRaw: right[3],
                readoutRaw: right[4],
                readoutRootRaw: right[5]
            );
            var expected = Oracles.RoundDyadic(
                exact: (((BigInteger)leftBehavior) * rightBehavior),
                shift: 16
            );

            return ((lanes[0] == expected)
                ? null
                : $"the termwise behaviour product reads {lanes[0]} where the exact restatement is {expected}"
            );
        };
    }
    /// <summary>The tensor behaviour over the exact integers, restated from the two factors' companion relations in
    /// <see cref="BigInteger"/> — the leg that stands outside the pair-up theorem instead of inside it.</summary>
    /// <returns>The bound witness.</returns>
    /// <remarks>Both sides of <c>presented.tensor-behavior-vs-product</c> are the presented object: one pairs up and
    /// takes a behaviour in the tensor, the other takes a behaviour in each factor and multiplies. Neither says what
    /// the answer IS. This does: <c>(a + b·x)(c + d·x)</c> reduced by <c>x² = P·x + Q</c> at each factor's own relation
    /// — <c>x² = x + 1</c> and <c>x² = −1</c> — paired with that factor's readout, and the two behaviours multiplied.
    /// Exact material, so nothing rounds on either side and the whole statement is condition (A).</remarks>
    public static VectorBinaryOp ExactTensorBehavior() =>
        (left, right, result) => {
            result.Clear();
            result[0] = ((long)(
                ExactFactorBehavior(
                linear: 1L,
                constant: 1L,
                state: left[0],
                stateRoot: left[1],
                step: right[0],
                stepRoot: right[1],
                readout: left[4],
                readoutRoot: left[5]
            )
                * ExactFactorBehavior(
                linear: 0L,
                constant: -1L,
                state: left[2],
                stateRoot: left[3],
                step: right[2],
                stepRoot: right[3],
                readout: right[4],
                readoutRoot: right[5]
            )
            ));
        };

    // One factor's behaviour over the exact integers: the schoolbook product of two linear forms carried down through
    // x² = linear·x + constant, then paired with the readout. Nothing here calls a Puck.Maths kernel.
    private static BigInteger ExactFactorBehavior(long linear, long constant, long state, long stateRoot, long step, long stepRoot, long readout, long readoutRoot) {
        var a = SmallInteger(raw: state);
        var b = SmallInteger(raw: stateRoot);
        var c = SmallInteger(raw: step);
        var e = SmallInteger(raw: stepRoot);
        var unit = ((a * c) + ((b * e) * constant));
        var root = (((a * e) + (b * c)) + ((b * e) * linear));

        return ((SmallInteger(raw: readout) * unit) + (SmallInteger(raw: readoutRoot) * root));
    }
    // One factor's behaviour, restated exactly: the quadratic product of the state and the step, one rounding per
    // component, paired with the readout through the EXACT-lane charged linear fold, which narrows each charge by
    // sixteen bits and rounds nothing.
    private static long FactorBehaviorOracle(long pRaw, long qRaw, long stateRaw, long stateRootRaw, long stepRaw, long stepRootRaw, long readoutRaw, long readoutRootRaw) {
        var product = Oracles.QuadraticMultiply(
            pRaw: pRaw,
            qRaw: qRaw,
            u1: stateRaw,
            u2: stepRaw,
            v1: stateRootRaw,
            v2: stepRootRaw
        );
        var exact = (((((BigInteger)readoutRaw) >> 16) * product.U) + ((((BigInteger)readoutRootRaw) >> 16) * product.V));

        return Oracles.WrapToRaw(value: exact);
    }

    /// <summary>The subject Clifford product written into caller buffers by the zero-allocation overload.</summary>
    /// <param name="positiveCount">The number of generators squaring to <c>+1</c>.</param>
    /// <param name="negativeCount">The number of generators squaring to <c>−1</c>.</param>
    /// <param name="degenerateCount">The number of degenerate generators.</param>
    /// <returns>The bound operation.</returns>
    public static VectorBinaryOp PresentedCliffordMultiplyInto(int positiveCount, int negativeCount, int degenerateCount) {
        FixedLaneAlgebra? binding = null;

        return (left, right, result) => {
            binding ??= CliffordBinding(
                degenerateCount: degenerateCount,
                negativeCount: negativeCount,
                positiveCount: positiveCount
            );

            binding.MultiplyInto(
                left: left,
                result: result,
                right: right
            );
        };
    }
    /// <summary>The subject Möbius step formed as a companion-quiver product: the companion matrix of
    /// <c>x² = P·x + Q</c> multiplied into the projective pair carried in the quiver's first column.</summary>
    /// <param name="pRaw">The linear coefficient, raw Q16.</param>
    /// <param name="qRaw">The constant coefficient, raw Q16.</param>
    /// <returns>The bound operation.</returns>
    /// <remarks>Ledger row 10: the linear form <c>P·n + Q·d</c> is the documented partial evaluation of the bilinear
    /// one, so a matrix step through the shared kernel must reproduce
    /// <see cref="QuadraticAlgebra{TScalar}.MobiusStep"/> bit for bit.</remarks>
    public static UnaryElemOp PresentedCompanionMobius(long pRaw, long qRaw) {
        PresentedAlgebra<FixedQ4816, FixedMaterial>? algebra = null;
        var companion = default(PresentedAlgebra<FixedQ4816, FixedMaterial>.Element);

        return (n, d) => {
            if (algebra is null) {
                algebra = CompanionQuiver();
                companion = algebra.FromSupport(
                    keys: [0L, 1L, 2L],
                    coefficients: [Raw(value: pRaw), Raw(value: qRaw), FixedQ4816.One]
                );
            }

            var stepped = algebra.Multiply(
                left: companion,
                right: algebra.FromSupport(
                    keys: [0L, 2L],
                    coefficients: [Raw(value: n), Raw(value: d)]
                )
            );

            return (stepped[0L].Value, stepped[2L].Value);
        };
    }
    /// <summary>The subject residual of a jet product at the identity twist, as an element-pair operation.</summary>
    /// <returns>The bound operation, returning the residual's coefficients at the two keys of the jet presentation.</returns>
    /// <remarks>Ledger row 15. The derivation drops a degree, so the residual of a degree-one product is a constant and
    /// its second coefficient is identically zero; its first IS <see cref="FixedDual{TScalar}"/>'s dual part.</remarks>
    public static BinaryElemOp PresentedJetResidual() {
        PresentedAlgebra<FixedQ4816, FixedMaterial>? algebra = null;

        return (u1, v1, u2, v2) => {
            algebra ??= PresentedAlgebra<FixedQ4816, FixedMaterial>.Create(presentation: Presentations.Monogenic<FixedQ4816, FixedMaterial>(
                modulus: [FixedQ4816.Zero, FixedQ4816.Zero],
                material: default
            ));

            var product = algebra.Multiply(
                left: algebra.FromSupport(
                    keys: [0L, 1L],
                    coefficients: [Raw(value: u1), Raw(value: v1)]
                ),
                right: algebra.FromSupport(
                    keys: [0L, 1L],
                    coefficients: [Raw(value: u2), Raw(value: v2)]
                )
            );
            var residual = algebra.Residual(
                symbol: 0,
                value: product,
                twist: ResidualTwist.Identity
            );

            return (residual[0L].Value, residual[1L].Value);
        };
    }
    /// <summary>The dual part of a <see cref="FixedDual{TScalar}"/> product, as the twin of
    /// <see cref="PresentedJetResidual"/>.</summary>
    /// <param name="u1">The multiplicand's real part.</param>
    /// <param name="v1">The multiplicand's dual part.</param>
    /// <param name="u2">The multiplier's real part.</param>
    /// <param name="v2">The multiplier's dual part.</param>
    /// <returns>The chain-rule lift and the zero the derivation leaves above it.</returns>
    public static (long U, long V) DualChainRuleLift(long u1, long v1, long u2, long v2) =>
        ((new FixedDual<FixedQ4816>(
            Real: Raw(value: u1),
            Dual: Raw(value: v1)
        ) * new FixedDual<FixedQ4816>(
            Real: Raw(value: u2),
            Dual: Raw(value: v2)
        )).Dual.Value, 0L);
}
