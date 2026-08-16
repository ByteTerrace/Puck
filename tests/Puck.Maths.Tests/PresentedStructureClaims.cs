using System.Numerics;
using Xunit;
using Puck.Maths.Research;

using LeafOctonion = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>>>;
using LeafSedenion = Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.DoublingAlgebra<Puck.Maths.FixedScalarRing>>>>;

namespace Puck.Maths.Tests;

/// <summary>
/// Structure claims for the presented-algebra family: conformal Clifford cells and the sedenion basis against
/// the doubling tower, quiver and divisor counting against walk oracles, weighted duality and the non-metric
/// complement, the transfer functor, the motor sandwich against geometric algebra, the torus's integral homology,
/// and the shuffle presentation's cap at its reachable edge.
/// </summary>
internal static class PresentedStructureClaims {
    // ---- shared bookkeeping: normal-form key -> Clifford blade bitmask ----

    private static int[] KeyToBladeMap<TValue, TOps>(ChargedPresentation<TValue, TOps> presentation)
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

    // ---- the conformal (4,1,0) signature's cells, individually, past GeometricAlgebra.Create's cap ----

    /// <summary>Proves every one of the conformal <c>(4,1,0)</c> signature's 32x32 compiled cells — the target
    /// key and the charge — against <see cref="Oracles.CliffordCharge"/>, one cell at a time. <c>GeometricAlgebra.Create</c>
    /// enforces a four-generator cap, so this signature (five generators, 32 blades) has no <see cref="GeometricAlgebra"/>
    /// counterpart anywhere in the tree; the only existing statement about it is the aggregate certificate's flags
    /// (<c>presented.clifford-charge-vs-oracle</c>), which a defect scrambling two cells' targets or charges while
    /// leaving associativity/unitality/the zero-divisor count intact would not catch.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ConformalCliffordCellsSurface() {
        const int PositiveCount = 4;
        const int NegativeCount = 1;
        const int DegenerateCount = 0;

        var presentation = Presentations.Clifford<BigInteger, IntegerMaterial>(
            degenerateCount: DegenerateCount,
            material: default,
            negativeCount: NegativeCount,
            positiveCount: PositiveCount
        );
        var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: presentation);
        var compiled = algebra.Compile();
        var keyToBlade = KeyToBladeMap(presentation: presentation);

        if (32 != compiled.KeyCount) {
            return $"the conformal (4,1,0) signature compiles to {compiled.KeyCount} normal forms, expected 32";
        }

        for (var leftKey = 0; (leftKey < 32); ++leftKey) {
            for (var rightKey = 0; (rightKey < 32); ++rightKey) {
                var oracle = Oracles.CliffordCharge(
                    leftBlade: keyToBlade[leftKey],
                    rightBlade: keyToBlade[rightKey],
                    positiveCount: PositiveCount,
                    negativeCount: NegativeCount,
                    degenerateCount: DegenerateCount
                );

                var entries = compiled.TargetCount(leftKey: leftKey, rightKey: rightKey);

                if (1 != entries) {
                    return $"conformal cell ({leftKey},{rightKey}) carries {entries} entr(ies), expected exactly one";
                }

                var targetBlade = keyToBlade[((int)compiled.Target(leftKey: leftKey, rightKey: rightKey))];
                var expectedBlade = keyToBlade[leftKey] ^ keyToBlade[rightKey];

                if (targetBlade != expectedBlade) {
                    return $"conformal cell ({leftKey},{rightKey}) targets blade {targetBlade}, expected {expectedBlade}";
                }

                if (compiled.Charge(leftKey: leftKey, rightKey: rightKey) != oracle) {
                    return $"conformal cell ({leftKey},{rightKey}) charges {compiled.Charge(leftKey: leftKey, rightKey: rightKey)}, the bubble-sort oracle says {oracle}";
                }
            }
        }

        return null;
    }

    // ---- the sedenion floor vs the shipped doubling tower, and the octonion floor's own zero-divisor search ----

    private static FixedScalarRing UnitScalarAt(int index, int offset) =>
        new(Value: ((offset == index) ? FixedQ4816.One : FixedQ4816.Zero));
    private static DoublingAlgebra<FixedScalarRing> UnitComplexAt(int index, int offset) =>
        new(Left: UnitScalarAt(index: index, offset: offset), Right: UnitScalarAt(index: index, offset: (offset + 1)));
    private static DoublingAlgebra<DoublingAlgebra<FixedScalarRing>> UnitQuaternionAt(int index, int offset) =>
        new(Left: UnitComplexAt(index: index, offset: offset), Right: UnitComplexAt(index: index, offset: (offset + 2)));
    private static LeafOctonion UnitOctonionAt(int index, int offset) =>
        new(Left: UnitQuaternionAt(index: index, offset: offset), Right: UnitQuaternionAt(index: index, offset: (offset + 4)));
    private static LeafSedenion UnitSedenion(int index) =>
        new(Left: UnitOctonionAt(index: index, offset: 0), Right: UnitOctonionAt(index: index, offset: 8));
    private static void WriteComplexLanes(DoublingAlgebra<FixedScalarRing> value, Span<long> lanes, int offset) {
        lanes[offset] = value.Left.Value.Value;
        lanes[(offset + 1)] = value.Right.Value.Value;
    }
    private static void WriteQuaternionLanes(DoublingAlgebra<DoublingAlgebra<FixedScalarRing>> value, Span<long> lanes, int offset) {
        WriteComplexLanes(value: value.Left, lanes: lanes, offset: offset);
        WriteComplexLanes(value: value.Right, lanes: lanes, offset: (offset + 2));
    }
    private static void WriteOctonionLanesAt(LeafOctonion value, Span<long> lanes, int offset) {
        WriteQuaternionLanes(value: value.Left, lanes: lanes, offset: offset);
        WriteQuaternionLanes(value: value.Right, lanes: lanes, offset: (offset + 4));
    }
    private static void WriteSedenionLanes(LeafSedenion value, Span<long> lanes) {
        WriteOctonionLanesAt(value: value.Left, lanes: lanes, offset: 0);
        WriteOctonionLanesAt(value: value.Right, lanes: lanes, offset: 8);
    }

    /// <summary>Proves the sedenion floor's 16x16 basis products bit-identical to <see cref="DoublingAlgebra{TInner}"/>'s
    /// own nested product one floor deeper than any existing case reaches (the shipped <c>LeafSedenion</c> tower — inert
    /// rounding, unit operands) and the derived charge equal to <see cref="Oracles.CayleyDicksonCharge"/> at every ordered
    /// pair; and that the octonion floor's two-term basis-pair sums produce NO zero divisor at any of the 441 ordered
    /// combinations — the same bounded search that finds 84 at the sedenion floor one tower up
    /// (<c>presented.sedenion-pair-zero-divisor-count</c>), run one floor down where the division-algebra property says
    /// it must come back empty.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? SedenionBasisVsDoublingTowerSurface() {
        const int Width = 16;

        var fixedAlgebra = PresentedAlgebra<FixedQ4816, FixedMaterial>.Create(
            presentation: Presentations.CayleyDickson<FixedQ4816, FixedMaterial>(floors: 4, basisRelabelling: [], material: default)
        );
        var integerAlgebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(
            presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(floors: 4, basisRelabelling: [], material: default)
        );
        var productLanes = new long[Width];
        var doublingLanes = new long[Width];

        for (var left = 0; (left < Width); ++left) {
            var leftFixed = fixedAlgebra.FromSupport(keys: [((long)left)], coefficients: [FixedQ4816.One]);
            var leftInteger = integerAlgebra.FromSupport(keys: [((long)left)], coefficients: [BigInteger.One]);

            for (var right = 0; (right < Width); ++right) {
                var rightFixed = fixedAlgebra.FromSupport(keys: [((long)right)], coefficients: [FixedQ4816.One]);
                var product = fixedAlgebra.Multiply(left: leftFixed, right: rightFixed);

                Array.Clear(array: productLanes);

                for (var index = 0; (index < product.SupportCount); ++index) { productLanes[((int)product.Keys[index])] = product.Coefficients[index].Value; }

                WriteSedenionLanes(value: LeafSedenion.Multiply(left: UnitSedenion(index: left), right: UnitSedenion(index: right)), lanes: doublingLanes);

                for (var lane = 0; (lane < Width); ++lane) {
                    if (productLanes[lane] != doublingLanes[lane]) {
                        return $"the sedenion basis product of ({left},{right}) differs from DoublingAlgebra on lane {lane} ({productLanes[lane]} != {doublingLanes[lane]})";
                    }
                }

                var rightInteger = integerAlgebra.FromSupport(keys: [((long)right)], coefficients: [BigInteger.One]);
                var charged = integerAlgebra.Multiply(left: leftInteger, right: rightInteger);
                var oracle = Oracles.CayleyDicksonCharge(floors: 4, leftIndex: left, rightIndex: right);

                if ((1 != charged.SupportCount) || (charged.Keys[0] != (left ^ right)) || (charged.Coefficients[0] != oracle)) {
                    return $"the sedenion basis charge of ({left},{right}) differs from the doubling recursion, expected {oracle} at key {(left ^ right)}";
                }
            }
        }

        var octonionAlgebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(
            presentation: Presentations.CayleyDickson<BigInteger, IntegerMaterial>(floors: 3, basisRelabelling: [], material: default)
        );
        var pairs = new List<(int First, int Second)>();

        for (var first = 1; (first < 8); ++first) {
            for (var second = (first + 1); (second < 8); ++second) { pairs.Add(item: (first, second)); }
        }

        foreach (var left in pairs) {
            var leftElement = octonionAlgebra.FromSupport(keys: [left.First, left.Second], coefficients: [BigInteger.One, BigInteger.One]);

            foreach (var right in pairs) {
                var rightElement = octonionAlgebra.FromSupport(keys: [right.First, right.Second], coefficients: [BigInteger.One, BigInteger.One]);

                if (0 == octonionAlgebra.Multiply(left: leftElement, right: rightElement).SupportCount) {
                    return $"the octonion floor carries a two-term zero divisor (e{left.First}+e{left.Second})*(e{right.First}+e{right.Second}), which the division-algebra property forbids";
                }
            }
        }

        return null;
    }

    // ---- CountingWalks/CountingStar vs the summed WalkCount oracle on a genuine acyclic weighted digraph ----

    private static (int Source, int Target, BigInteger Weight)[] CodiscreteArrows(int order) {
        var arrows = new (int Source, int Target, BigInteger Weight)[(order * order)];

        for (var source = 0; (source < order); ++source) {
            for (var target = 0; (target < order); ++target) { arrows[((source * order) + target)] = (source, target, BigInteger.One); }
        }

        return arrows;
    }
    private static PresentedAlgebra<BigInteger, CountingMaterial>.Element CountingAdjacency(
        PresentedAlgebra<BigInteger, CountingMaterial> algebra,
        int order,
        (int Source, int Target)[] arcs
    ) {
        var coefficients = new BigInteger[(order * order)];
        var keys = new long[(order * order)];
        var support = 0;

        foreach (var arc in arcs) { coefficients[((arc.Source * order) + arc.Target)] += BigInteger.One; }

        for (var entry = 0; (entry < coefficients.Length); ++entry) {
            if (coefficients[entry].IsZero) { continue; }

            coefficients[support] = coefficients[entry];
            keys[support] = entry;
            ++support;
        }

        return algebra.FromSupport(keys: keys.AsSpan(length: support, start: 0), coefficients: coefficients.AsSpan(length: support, start: 0));
    }
    private static BigInteger[] DenseAdjacency(int order, (int Source, int Target)[] arcs) {
        var dense = new BigInteger[(order * order)];

        foreach (var arc in arcs) { dense[((arc.Source * order) + arc.Target)] += BigInteger.One; }

        return dense;
    }
    private static void Scatter(PresentedAlgebra<BigInteger, CountingMaterial>.Element element, Span<BigInteger> dense) {
        dense.Clear();

        for (var index = 0; (index < element.SupportCount); ++index) { dense[((int)element.Keys[index])] = element.Coefficients[index]; }
    }

    /// <summary>Proves <see cref="PresentedAlgebra{TValue, TOps}.Power"/> read as directed-walk counts at lengths
    /// zero through the vertex count, and <see cref="PresentedAlgebra{TValue, TOps}.TrySumOverAllLengths"/>'s nilpotent
    /// total, both against <see cref="Oracles.WalkCount"/> on a genuine five-vertex acyclic weighted digraph. The
    /// existing Power/PowerSum cases exercise a single exponent or a Trace reduction; none reads the star's own guarded infinite
    /// sum against the independently summed oracle powers on non-trivial graph data.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? QuiverCountingStarVsWalkOracleSurface() {
        const int Order = 5;

        var arcs = new (int Source, int Target)[] { (0, 1), (0, 2), (0, 4), (1, 2), (1, 3), (2, 3), (2, 4), (3, 4) };
        var algebra = PresentedAlgebra<BigInteger, CountingMaterial>.Create(
            presentation: Presentations.Quiver<BigInteger, CountingMaterial>(
                objectCount: Order,
                arrows: CodiscreteArrows(order: Order),
                material: default
            )
        );
        var element = CountingAdjacency(algebra: algebra, arcs: arcs, order: Order);
        var dense = DenseAdjacency(arcs: arcs, order: Order);
        var expected = new BigInteger[(Order * Order)];
        var actual = new BigInteger[(Order * Order)];
        var runningTotal = new BigInteger[(Order * Order)];

        for (var length = 0; (length <= Order); ++length) {
            var power = algebra.Power(exponent: ((ulong)length), value: element);

            Scatter(dense: actual, element: power);
            Oracles.WalkCount(adjacency: dense, length: length, order: Order, result: expected);

            for (var entry = 0; (entry < (Order * Order)); ++entry) {
                if (actual[entry] != expected[entry]) {
                    return $"walks of length {length} at entry {entry} are {actual[entry]}, the BigInteger matrix power says {expected[entry]}";
                }

                runningTotal[entry] += expected[entry];
            }
        }

        if (!algebra.TrySumOverAllLengths(obstruction: out var obstruction, total: out var total, value: element)) {
            return $"the counting star refused on an acyclic digraph, attempting {obstruction.Attempted} after {obstruction.StepsTaken} step(s)";
        }

        Scatter(dense: actual, element: total);

        for (var entry = 0; (entry < (Order * Order)); ++entry) {
            if (actual[entry] != runningTotal[entry]) {
                return $"the nilpotent star at entry {entry} is {actual[entry]}, the summed matrix powers (length 0 through {Order}) say {runningTotal[entry]}";
            }
        }

        return null;
    }
    // ---- the Dirichlet window: the CUBED divisor count d3(n) ----
    //
    // presented.mobius-star-round-trip already proves NormalFormCount, TryMobius's issuance and coefficients (against
    // Factorize), mu*zeta = identity, DivisorCounts(2) (against Factorize), the Mobius inversion identity at every
    // bound, the Mertens partial sum, and the Legendre sieve via PrimeCountingFunction — all at window 60. It cites
    // DivisorCounts only at order 2. DivisorCounts(3) is Power(Zeta, 3), a different exponent of the ascending-bit
    // power schedule, and no existing case compares it to an independent ordered-triple-factorization count.
    public static string? DirichletDivisorCubeSurface() {
        const long Window = 48L;
        ReadOnlySpan<ulong> primes = [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47];
        var divisibility = DivisibilityAlgebra<BigInteger, IntegerMaterial>.Create(material: default, primes: primes, window: Window);

        if (divisibility.NormalFormCount != Window) {
            return $"a window generated by every prime through {Window} holds {divisibility.NormalFormCount} integer(s), expected {Window}";
        }

        var triples = divisibility.DivisorCounts(order: 3);

        for (var value = 1L; (value <= Window); ++value) {
            if (!divisibility.TryKey(value: value, out var key)) {
                return $"{value} is not in a window generated by every prime through {Window}";
            }

            var expected = CubeDivisorCountOracle(value: ((uint)value));

            if (triples[key] != expected) {
                return $"d3({value}) = {triples[key]}, the ordered-triple-factorization count says {expected}";
            }
        }

        return null;
    }

    // The number of ordered triples whose product is value: the product over prime-power exponents e of
    // C(e+2, 2) = (e+1)(e+2)/2. Built from PrimeExtensions.Factorize — a prime reports itself, a value below two
    // reports nothing — which is the current contract this oracle relies on.
    private static BigInteger CubeDivisorCountOracle(uint value) {
        Span<uint> factors = stackalloc uint[32];
        var count = value.Factorize(destination: factors);
        var total = BigInteger.One;
        var index = 0;

        while (index < count) {
            var multiplicity = 1;

            while (((index + multiplicity) < count) && (factors[(index + multiplicity)] == factors[index])) { ++multiplicity; }

            total *= (((multiplicity + 1L) * (multiplicity + 2L)) / 2L);
            index += multiplicity;
        }

        return total;
    }

    // ---- the duality layer: the pairing-radical decision over a NUMERIC field material ----
    //
    // presented.machine-equivalence-vs-enumeration already covers the quiver-minimization half of the duality layer:
    // a three-object PresentedAlgebra<ulong, PrimeFieldMaterial> quiver machine quotients from three reachable states
    // to the two-state four-cell minimum (MaximumSupportCount == 4), the quotient preserves every behaviour,
    // PresentedMachine.AreEquivalent confirms it, and a second quotient is idempotent.
    // What it does NOT cover is PatternMatcher<TValue, TOps>.AreEquivalent's pairing-radical decision over a field
    // material's own weighted patterns: every existing PatternMatcher/TokenPattern law instantiates BooleanMaterial,
    // whose semiring (OR/AND) is not a field, so AreEquivalent there decides nothing — the type's own remarks say the
    // decision runs only "over a prime field, an exact rational". No PatternMatcher<ulong, PrimeFieldMaterial> or
    // TokenPattern<ulong, PrimeFieldMaterial> case exists anywhere in the suite.
    public static string? WeightedDualityEquivalenceSurface() {
        var material = PrimeFieldMaterial.Create(modulus: 65_521UL);
        var pattern = TokenPattern<ulong, PrimeFieldMaterial>.Create(letterCount: 2, material: material, window: 4);
        var a = pattern.Predicate(letters: 0b01UL);
        var b = pattern.Predicate(letters: 0b10UL);
        var cases = new (string Name, PresentedAlgebra<ulong, PrimeFieldMaterial>.Element Left, PresentedAlgebra<ulong, PrimeFieldMaterial>.Element Right, bool Equal)[] {
            (
                "(a|b).a == a.a | b.a",
                pattern.Concatenate(left: pattern.Union(left: a, right: b), right: a),
                pattern.Union(left: pattern.Concatenate(left: a, right: a), right: pattern.Concatenate(left: b, right: a)),
                true
            ),
            (
                "(a|b).a != a.a",
                pattern.Concatenate(left: pattern.Union(left: a, right: b), right: a),
                pattern.Concatenate(left: a, right: a),
                false
            ),
            (
                "a.(a|b) != (a|b).a",
                pattern.Concatenate(left: a, right: pattern.Union(left: a, right: b)),
                pattern.Concatenate(left: pattern.Union(left: a, right: b), right: a),
                false
            ),
        };

        foreach (var (name, leftValue, rightValue, equal) in cases) {
            if (!PatternMatcher<ulong, PrimeFieldMaterial>.TryCompile(matcher: out var left, obstruction: out _, pattern: pattern, stateLimit: 8, value: leftValue)
                || !PatternMatcher<ulong, PrimeFieldMaterial>.TryCompile(matcher: out var right, obstruction: out _, pattern: pattern, stateLimit: 8, value: rightValue)) {
                return $"{name}: a matcher did not compile";
            }

            var decided = PatternMatcher<ulong, PrimeFieldMaterial>.AreEquivalent(left: left, right: right, witness: out var witness);

            if (decided != equal) { return $"{name}: the pairing radical decided {decided}, expected {equal}"; }

            // The brute decision, sharing nothing with the radical: enumerate every span the window represents and
            // compare the numeric weight both machines give it.
            int[]? shortest = null;

            foreach (var word in EnumerateWords(letterCount: 2, maximumLength: pattern.Window)) {
                _ = left.TryMatch(letters: word, obstruction: out _, weight: out var leftWeight);
                _ = right.TryMatch(letters: word, obstruction: out _, weight: out var rightWeight);

                if (leftWeight == rightWeight) { continue; }

                shortest ??= word;
            }

            if ((shortest is null) != equal) { return $"{name}: brute enumeration disagreed with the radical"; }

            if (!equal) {
                _ = left.TryMatch(letters: witness.Word, weight: out var leftWeight, obstruction: out _);
                _ = right.TryMatch(letters: witness.Word, weight: out var rightWeight, obstruction: out _);

                if ((witness.LeftValue != leftWeight) || (witness.RightValue != rightWeight) || (leftWeight == rightWeight) || (witness.Word.Length != shortest!.Length)) {
                    return $"{name}: the witness does not carry the shortest separating span's numeric weights";
                }
            }
        }

        return null;
    }

    private static IEnumerable<int[]> EnumerateWords(int letterCount, int maximumLength) {
        for (var length = 0; (length <= maximumLength); ++length) {
            var word = new int[length];

            while (true) {
                yield return ((int[])word.Clone());

                var position = (length - 1);

                while ((position >= 0) && (++word[position] == letterCount)) {
                    word[position] = 0;
                    --position;
                }

                if (position < 0) { break; }
            }
        }
    }

    // ---- the non-metric complement, beyond a single Euclidean (3,0,0) signature ----
    //
    // presented.orientation-twins-determinant and presented.complement-admission-proves-inverses already prove the
    // complement's defining equations (mutual inverses, the pseudoscalar identities), the top-grade triple wedge
    // against an independent determinant oracle, and the double-right-complement parity — all at (3,0,0) only, where
    // n = 3 in the formula (-1)^(g*(n-g)). What is missing: the FULL pairwise wedge sign — every ordered pair of
    // blades, not only a top-grade triple — against an independent merge-permutation oracle at a DEGENERATE signature
    // (3,0,1) and a MIXED signature (4,1,0); the general double-complement formula at those larger n; and the
    // projective (3,0,1) incidence (two planes meeting in their shared line, a self-join vanishing) that no other case
    // in the suite exercises at all.
    public static string? NonMetricComplementBeyondEuclideanSurface() {
        foreach (var (positiveCount, negativeCount, degenerateCount) in new (int P, int Q, int R)[] { (3, 0, 1), (4, 1, 0) }) {
            var presentation = Presentations.Clifford<BigInteger, IntegerMaterial>(degenerateCount: degenerateCount, material: default, negativeCount: negativeCount, positiveCount: positiveCount);
            var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: presentation);
            var complement = GradedComplement<BigInteger, IntegerMaterial>.Create(algebra: algebra);
            var count = presentation.NormalFormCount;
            var generatorCount = ((positiveCount + negativeCount) + degenerateCount);
            var name = $"({positiveCount},{negativeCount},{degenerateCount})";
            var bladeOfKey = new int[count];

            for (var key = 0; (key < count); ++key) {
                var mask = 0;

                foreach (var symbol in presentation.NormalFormWord(key: key)) { mask |= (1 << symbol); }

                bladeOfKey[key] = mask;
            }

            for (var left = 0; (left < count); ++left) {
                for (var right = 0; (right < count); ++right) {
                    var joined = complement.OuterProduct(
                        left: algebra.FromSupport(keys: [left], coefficients: [BigInteger.One]),
                        right: algebra.FromSupport(keys: [right], coefficients: [BigInteger.One])
                    );
                    var sign = WedgeSignOracle(leftBlade: bladeOfKey[left], rightBlade: bladeOfKey[right]);

                    if (0 == sign) {
                        if (0 != joined.SupportCount) { return $"{name}: blades {bladeOfKey[left]} and {bladeOfKey[right]} share a generator but joined to {joined.SupportCount} term(s)"; }
                    } else {
                        var target = bladeOfKey[left] | bladeOfKey[right];

                        if ((1 != joined.SupportCount) || (bladeOfKey[((int)joined.Keys[0])] != target) || (joined.Coefficients[0] != sign)) {
                            return $"{name}: blades {bladeOfKey[left]} ^ {bladeOfKey[right]} joined to {joined.SupportCount} term(s), expected {sign} times blade {target}";
                        }
                    }
                }
            }

            for (var key = 0; (key < count); ++key) {
                var basis = algebra.FromSupport(keys: [key], coefficients: [BigInteger.One]);
                var grade = presentation.NormalFormWord(key: key).Length;
                var twice = complement.RightComplement(value: complement.RightComplement(value: basis));
                var expected = ((0 == ((grade * (generatorCount - grade)) & 1)) ? BigInteger.One : BigInteger.MinusOne);

                if ((1 != twice.SupportCount) || (twice.Keys[0] != key) || (twice.Coefficients[0] != expected)) {
                    return $"{name}: key {key}: the double right complement is not (-1)^(g*(n-g))";
                }
            }
        }

        // The rich case: PGA (3,0,1). Two planes sharing a generator meet in the shared line, and a generator joined
        // to a blade already containing it vanishes rather than surviving as a spurious higher meet.
        {
            var presentation = Presentations.Clifford<BigInteger, IntegerMaterial>(degenerateCount: 1, material: default, negativeCount: 0, positiveCount: 3);
            var algebra = PresentedAlgebra<BigInteger, IntegerMaterial>.Create(presentation: presentation);
            var complement = GradedComplement<BigInteger, IntegerMaterial>.Create(algebra: algebra);
            var count = presentation.NormalFormCount;
            var keyOfBlade = new int[16];

            for (var key = 0; (key < count); ++key) {
                var mask = 0;

                foreach (var symbol in presentation.NormalFormWord(key: key)) { mask |= (1 << symbol); }

                keyOfBlade[mask] = key;
            }

            string? Meet(int leftBlade, int rightBlade, int expectedBlade) {
                var met = complement.RegressiveProduct(
                    left: algebra.FromSupport(keys: [keyOfBlade[leftBlade]], coefficients: [BigInteger.One]),
                    right: algebra.FromSupport(keys: [keyOfBlade[rightBlade]], coefficients: [BigInteger.One])
                );

                if (0 == expectedBlade) {
                    return ((0 != met.SupportCount) ? $"pga: blades {leftBlade} v {rightBlade} met in {met.SupportCount} term(s), expected nothing" : null);
                }

                var metMask = 0;

                foreach (var symbol in presentation.NormalFormWord(key: ((int)met.Keys[0]))) { metMask |= (1 << symbol); }

                return (((1 != met.SupportCount) || (metMask != expectedBlade) || (BigInteger.Abs(value: met.Coefficients[0]) != BigInteger.One))
                    ? $"pga: blades {leftBlade} v {rightBlade} met in blade {metMask}, expected plus-or-minus blade {expectedBlade}"
                    : null);
            }

            // e012 v e013 share e01; e012 v e023 share e02; e012 v e123 share e12.
            var failure = (Meet(expectedBlade: 0b0011, leftBlade: 0b0111, rightBlade: 0b1011)
                ?? (Meet(expectedBlade: 0b0101, leftBlade: 0b0111, rightBlade: 0b1101)
                ?? Meet(expectedBlade: 0b0110, leftBlade: 0b0111, rightBlade: 0b1110)));

            if (failure is not null) { return failure; }

            var self = complement.OuterProduct(
                left: algebra.FromSupport(keys: [keyOfBlade[0b0001]], coefficients: [BigInteger.One]),
                right: algebra.FromSupport(keys: [keyOfBlade[0b0011]], coefficients: [BigInteger.One])
            );

            if (0 != self.SupportCount) { return "pga: a generator joined to a blade containing it did not vanish"; }
        }

        return null;
    }

    // The outer product's charge on two basis blades: zero when they share a generator, otherwise the sign of the
    // permutation that merges the two ascending generator lists. No signature enters, which is the whole point.
    private static int WedgeSignOracle(int leftBlade, int rightBlade) {
        if (0 != (leftBlade & rightBlade)) { return 0; }

        Span<int> merged = stackalloc int[8];
        var mergedCount = 0;

        for (var generator = 0; (generator < 8); ++generator) {
            if (0 != (leftBlade & (1 << generator))) { merged[mergedCount++] = generator; }
        }

        for (var generator = 0; (generator < 8); ++generator) {
            if (0 != (rightBlade & (1 << generator))) { merged[mergedCount++] = generator; }
        }

        var sign = 1;

        for (var outer = 0; (outer < mergedCount); ++outer) {
            for (var inner = (outer + 1); (inner < mergedCount); ++inner) {
                if (merged[outer] > merged[inner]) { sign = -sign; }
            }
        }

        return sign;
    }

    // ---- the transfer functor: no existing coverage at all against these four legacy copies ----
    //
    // presented.functor-twins-transfer-varied-length exercises ConvergentTransfer against PresentedFunctor.Map and
    // PresentedMachine — a different pair of comparisons entirely. QuadraticInflation, ContinuedFraction and
    // QuadraticQuasicrystal each carry their own laws for their OWN invariants, but none compares ConvergentTransfer's
    // codiscrete-quiver product against them, and QuadraticOstrowskiSystem and SturmianReturnSpectrumResearch do not
    // appear anywhere else in the suite at all.
    public static string? TransferFunctorLegacyCopiesSurface() {
        var transfer = ConvergentTransfer<BigInteger, IntegerMaterial>.Create(material: default);
        var irrationals = new (long P, long Q, long D, long R)[] { (0, 1, 2, 1), (0, 1, 3, 1), (0, 1, 5, 1), (1, 1, 5, 2), (0, 1, 7, 1) };
        var terms = new long[128];

        foreach (var (p, q, d, r) in irrationals) {
            _ = ContinuedFraction.Expand(d: d, p: p, periodLength: out var periodLength, periodStart: out var periodStart, q: q, r: r, terms: terms);

            var period = new BigInteger[periodLength];

            for (var offset = 0; (offset < periodLength); ++offset) { period[offset] = terms[(periodStart + offset)]; }

            var evaluated = transfer.Evaluate(partialQuotients: period);
            var inflation = QuadraticInflation.FromQuadraticIrrational(d: d, p: p, q: q, r: r);

            if ((transfer.Entry(column: 0, row: 0, value: evaluated) != inflation.A)
                || (transfer.Entry(column: 1, row: 0, value: evaluated) != inflation.B)
                || (transfer.Entry(column: 0, row: 1, value: evaluated) != inflation.C)
                || (transfer.Entry(column: 1, row: 1, value: evaluated) != inflation.D)) {
                return $"the transfer product over the period of ({p}+{q}rt{d})/{r} is not QuadraticInflation's substitution matrix";
            }

            var index = QuadraticQuasicrystal.Compile(d: d, p: p, q: q, r: r);

            if ((transfer.Entry(column: 0, row: 0, value: evaluated) != index.A)
                || (transfer.Entry(column: 1, row: 0, value: evaluated) != index.B)
                || (transfer.Entry(column: 0, row: 1, value: evaluated) != index.C)
                || (transfer.Entry(column: 1, row: 1, value: evaluated) != index.D)) {
                return $"the transfer product is not QuadraticQuasicrystalIndex's substitution matrix at ({p}+{q}rt{d})/{r}";
            }
        }

        // The Ostrowski copy folds the SAME recurrence into its convergent denominators, so the transfer's first
        // entry over a_1..a_k is the denominator q_k the shipped evaluator reports for the representation 1.q_k.
        foreach (var (p, q, d, r) in new (long P, long Q, long D, long R)[] { (0, 1, 2, 1), (1, 1, 5, 2) }) {
            var system = QuadraticOstrowskiSystem.Create(basis: QuadraticSurd.Create(denominator: r, radicand: d, rationalNumerator: p, surdNumerator: q));

            for (var length = 1; (length <= 8); ++length) {
                var quotients = new BigInteger[length];
                var digits = new BigInteger[(length + 1)];

                for (var index = 0; (index < length); ++index) { quotients[index] = system.PartialQuotient(index: (index + 1)); }

                digits[0] = BigInteger.One;

                var denominator = transfer.Entry(value: transfer.Evaluate(partialQuotients: quotients), row: 0, column: 0);
                var systemValue = system.Evaluate(digits: digits);

                if (denominator != systemValue) {
                    return $"the transfer's convergent denominator q_{length} is {denominator}, the Ostrowski system says {systemValue}";
                }
            }
        }

        // The Sturmian copy folds the same digits into a quadratic tail, so the tail recomputed from the transfer's
        // own entries is the one the shipped research reports, at every phase and both directions.
        foreach (var period in new int[][] { [1, 1], [2, 1, 3] }) {
            foreach (var step in new[] { 1, -1 }) {
                for (var start = 0; (start < period.Length); ++start) {
                    var quotients = new BigInteger[period.Length];
                    var phase = start;

                    for (var index = 0; (index < period.Length); ++index) {
                        quotients[index] = period[phase];
                        phase = ((((phase + step) % period.Length) + period.Length) % period.Length);
                    }

                    var product = transfer.Evaluate(partialQuotients: quotients);
                    var entryA = transfer.Entry(column: 0, row: 0, value: product);
                    var entryB = transfer.Entry(column: 1, row: 0, value: product);
                    var entryC = transfer.Entry(column: 0, row: 1, value: product);
                    var entryD = transfer.Entry(column: 1, row: 1, value: product);
                    var discriminant = (((entryD - entryA) * (entryD - entryA)) + ((4 * entryB) * entryC));
                    var tail = QuadraticSurd.Create(rationalNumerator: (entryA - entryD), surdNumerator: BigInteger.One, radicand: discriminant, denominator: (2 * entryC));

                    if (tail != SturmianReturnSpectrumResearch.PeriodicTail(period: period, start: start, step: step)) {
                        return $"the Sturmian periodic tail at start {start} step {step} disagrees with the transfer product";
                    }
                }
            }
        }

        return null;
    }
    // ---- motors: GeometricAlgebra.Reverse and GeometricAlgebra.SandwichTransform are cited nowhere ----
    //
    // Every existing GeometricAlgebra law exercises Create, GeometricProduct, Square and BladeCount; none reaches
    // Reverse or SandwichTransform. FixedRigidTransform.TransformPoint is independently pinned elsewhere (against
    // Oracles.RigidPointAction, a quaternion-and-translation BigInteger oracle with its own rounding schedule), so
    // this claim does not repeat that tolerance canary — it proves the genuinely new thing: that the DERIVED (3,0,1)
    // sandwich, built from nothing but two PresentedAlgebra.Multiply calls and a per-grade-sign reverse, is
    // bit-for-bit identical to the shipped GeometricAlgebra.SandwichTransform. The motor catalogue below is exact by
    // construction — a 180-degree rotor is a unit bivector alone (cos(pi/2) = 0, sin(pi/2) = 1, both exact in
    // FixedQ4816), and a translator along a degenerate generator is exact because that bivector squares to zero at
    // ANY scale — so no transcendental or Random enters anywhere, and the whole catalogue is composed from a fixed
    // deterministic set by ordinary indices.
    public static string? MotorSandwichVsGeometricAlgebraSurface() {
        var geometric = GeometricAlgebra.Create(degenerateCount: 1, negativeCount: 0, positiveCount: 3);
        var presentation = Presentations.Clifford<FixedQ4816, FixedMaterial>(degenerateCount: 1, material: default, negativeCount: 0, positiveCount: 3);
        var algebra = PresentedAlgebra<FixedQ4816, FixedMaterial>.Create(presentation: presentation);
        var keyToBlade = new int[presentation.NormalFormCount];
        var reverseSign = new long[presentation.NormalFormCount];

        for (var key = 0; (key < keyToBlade.Length); ++key) {
            var mask = 0;

            foreach (var symbol in presentation.NormalFormWord(key: key)) { mask |= (1 << symbol); }

            keyToBlade[key] = mask;

            var grade = presentation.NormalFormWord(key: key).Length;

            reverseSign[key] = ((0 == (((grade * (grade - 1)) / 2) & 1)) ? 1L : -1L);
        }

        var oneRaw = FixedQ4816.One.Value;
        var catalogue = new long[][] {
            Lanes((0, oneRaw)),                        // identity
            Lanes((0b0011, oneRaw)),                    // 180-degree rotor, the e0e1 plane
            Lanes((0b0101, oneRaw)),                    // 180-degree rotor, the e0e2 plane
            Lanes((0b0110, oneRaw)),                    // 180-degree rotor, the e1e2 plane
            Lanes((0, oneRaw), (0b1001, (3L * oneRaw))),   // exact translation, along e0
            Lanes((0, oneRaw), (0b1010, (-2L * oneRaw))),  // exact translation, along e1
            Lanes((0, oneRaw), (0b1100, (5L * oneRaw))),   // exact translation, along e2
        };
        var vectors = new long[][] {
            Lanes((0b0001, oneRaw)),
            Lanes((0b0010, oneRaw)),
            Lanes((0b0100, oneRaw)),
            Lanes((0b0001, (2L * oneRaw)), (0b0010, (-3L * oneRaw)), (0b0100, (5L * oneRaw))),
            Lanes((0b0001, oneRaw), (0b0010, oneRaw), (0b0100, oneRaw), (0b1000, oneRaw)),
        };
        var motorLanes = new long[16];
        var reversedLanes = new long[16];
        var derivedReverseLanes = new long[16];
        var derivedSandwichLanes = new long[16];
        var shippedSandwichLanes = new long[16];

        foreach (var left in catalogue) {
            foreach (var right in catalogue) {
                var motor = geometric.GeometricProduct(left: ToMultivector(lanes: left), right: ToMultivector(lanes: right));

                MultivectorToLanes(lanes: motorLanes, value: motor);

                var motorElement = FromLanes(algebra: algebra, keyToBlade: keyToBlade, lanes: motorLanes);
                var derivedReverse = algebra.FromSupport(keys: motorElement.Keys, coefficients: SignedCoefficients(element: motorElement, signs: reverseSign));

                MultivectorToLanes(value: geometric.Reverse(value: motor), lanes: reversedLanes);
                ToLanes(element: derivedReverse, keyToBlade: keyToBlade, lanes: derivedReverseLanes);

                for (var blade = 0; (blade < 16); ++blade) {
                    if (derivedReverseLanes[blade] != reversedLanes[blade]) {
                        return $"the derived reverse differs from GeometricAlgebra.Reverse on blade {blade}";
                    }
                }

                foreach (var vectorLanes in vectors) {
                    var vectorElement = FromLanes(algebra: algebra, keyToBlade: keyToBlade, lanes: vectorLanes);
                    var derivedSandwich = algebra.Multiply(left: algebra.Multiply(left: motorElement, right: vectorElement), right: derivedReverse);

                    ToLanes(element: derivedSandwich, keyToBlade: keyToBlade, lanes: derivedSandwichLanes);
                    MultivectorToLanes(value: geometric.SandwichTransform(motor: motor, vector: ToMultivector(lanes: vectorLanes)), lanes: shippedSandwichLanes);

                    for (var blade = 0; (blade < 16); ++blade) {
                        if (derivedSandwichLanes[blade] != shippedSandwichLanes[blade]) {
                            return $"the derived sandwich differs from GeometricAlgebra.SandwichTransform on blade {blade}";
                        }
                    }
                }
            }
        }

        return null;
    }

    private static long[] Lanes(params (int Blade, long Raw)[] entries) {
        var lanes = new long[16];

        foreach (var (blade, raw) in entries) { lanes[blade] += raw; }

        return lanes;
    }
    private static Multivector ToMultivector(long[] lanes) {
        var coefficients = new FixedQ4816[16];

        for (var blade = 0; (blade < 16); ++blade) { coefficients[blade] = FixedQ4816.FromRawBits(value: lanes[blade]); }

        return Multivector.FromCoefficients(coefficients: coefficients);
    }
    private static void MultivectorToLanes(Multivector value, long[] lanes) {
        for (var blade = 0; (blade < 16); ++blade) { lanes[blade] = value[blade].Value; }
    }
    private static PresentedAlgebra<FixedQ4816, FixedMaterial>.Element FromLanes(PresentedAlgebra<FixedQ4816, FixedMaterial> algebra, int[] keyToBlade, long[] lanes) {
        var keys = new List<long>();
        var coefficients = new List<FixedQ4816>();

        for (var key = 0; (key < keyToBlade.Length); ++key) {
            var raw = lanes[keyToBlade[key]];

            if (0L != raw) {
                keys.Add(item: key);
                coefficients.Add(item: FixedQ4816.FromRawBits(value: raw));
            }
        }

        return algebra.FromSupport(keys: keys.ToArray(), coefficients: coefficients.ToArray());
    }
    private static void ToLanes(in PresentedAlgebra<FixedQ4816, FixedMaterial>.Element element, int[] keyToBlade, long[] lanes) {
        Array.Clear(array: lanes);

        for (var index = 0; (index < element.SupportCount); ++index) {
            lanes[keyToBlade[((int)element.Keys[index])]] = element.Coefficients[index].Value;
        }
    }
    private static FixedQ4816[] SignedCoefficients(in PresentedAlgebra<FixedQ4816, FixedMaterial>.Element element, long[] signs) {
        var coefficients = new FixedQ4816[element.SupportCount];

        for (var index = 0; (index < element.SupportCount); ++index) {
            var coefficient = element.Coefficients[index];

            coefficients[index] = ((0L < signs[((int)element.Keys[index])]) ? coefficient : -coefficient);
        }

        return coefficients;
    }

    /// <summary>Proves the integral homology of the minimal seven-vertex triangulation of the torus: Betti numbers
    /// [1, 2, 1], no torsion in any degree, and an Euler characteristic of zero — the suite's first complex whose
    /// free homology rank exceeds one, closing the gap next to <c>presented.homology-torsion-and-betti</c>'s
    /// projective-plane and rank-one-or-less worlds.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// The triangulation is the standard difference-set one: for each <c>i</c> in 0..6 (mod 7), the two triangles
    /// <c>{i, i+1, i+3}</c> and <c>{i, i+2, i+3}</c> — twenty-one edges, fourteen triangles, every edge shared by
    /// exactly two of them.
    /// </remarks>
    public static string? HomologyTorusFreeRankTwoSurface() {
        var faces = new int[14][];

        for (var index = 0; (index < 7); ++index) {
            faces[(2 * index)] = [index, ((index + 1) % 7), ((index + 3) % 7)];
            faces[((2 * index) + 1)] = [index, ((index + 2) % 7), ((index + 3) % 7)];
        }

        var (dimensions, incidences) = SimplicialComplexFromTopFaces(topFaces: faces);
        var calculus = ExteriorCalculus<BigInteger, IntegerMaterial>.Create(dimensions: dimensions, incidences: incidences, material: default);

        Assert.Equal(expected: 2, actual: calculus.Dimension);
        Assert.Equal(expected: 7, actual: calculus.CellsOfDegree(degree: 0).Length);
        Assert.Equal(expected: 21, actual: calculus.CellsOfDegree(degree: 1).Length);
        Assert.Equal(expected: 14, actual: calculus.CellsOfDegree(degree: 2).Length);

        Assert.True(condition: IntegerHomology.TryCompute(calculus: calculus, homology: out var homology, magnitudeBits: 65_536, obstruction: out _));

        int[] betti = [1, 2, 1];

        for (var degree = 0; (degree <= 2); ++degree) {
            Assert.Equal(expected: betti[degree], actual: homology.BettiNumber(degree: degree));
            Assert.Equal(expected: 0, actual: homology.Torsion(degree: degree).Length);
        }

        Assert.Equal(expected: 0, actual: homology.EulerCharacteristic);

        return null;
    }
    /// <summary>Proves the shuffle presentation's derived cap is REACHABLE, not merely refused past: at each of the
    /// three near-cap argument tuples — (1, 511), (2, 8) and (511, 1), at 512, 511 and 512 words — the presentation
    /// admits exactly the words the closed form counts, and every normal form is the one-letter word naming its own
    /// key.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>
    /// <para>
    /// The other half of <c>presented.shuffle-limits-refuse</c>, which carries only the six small tuples: a refusal
    /// gate proves the cap blocks past its edge, and says nothing about whether the last admitted tuple is real. A
    /// cap derived one too low would satisfy every refusal in that case.
    /// </para>
    /// <para>
    /// It stays affordable at the near-cap widths by reading <c>HasFiniteNormalForms</c> rather than
    /// <c>HasCompiledNormalFormBasis</c> — the sibling case reads the compiled one, which emits a rule per ordered
    /// pair of words and so costs roughly 785,000 rules across these three tuples. Nothing here needs the
    /// composition table; the claim is about the generating set alone. The full thirteen-tuple ladder is swept so
    /// the near-cap rows are read on the same scale as the small ones.
    /// </para>
    /// </remarks>
    public static string? ShuffleNearCapBasisSurface() {
        foreach (var (letters, window, words) in (((int Letters, int Window, int Words)[])[
            (0, 4, 1), (1, 0, 1), (1, 10, 11), (1, 511, 512), (2, 1, 3), (2, 4, 31), (2, 6, 127), (2, 8, 511),
            (3, 3, 40), (3, 4, 121), (4, 3, 85), (5, 3, 156), (511, 1, 512),
        ])) {
            // The closed form is summed here rather than transcribed, so the row's own expected count is checked
            // against arithmetic that shares nothing with the presentation: the words of length at most `window`
            // over `letters` letters number the sum of letters^length, and BigInteger keeps that exact at (511, 1).
            var closed = BigInteger.Zero;

            for (var length = 0; (length <= window); ++length) {
                closed += BigInteger.Pow(exponent: length, value: letters);
            }

            if (closed != words) {
                return $"shuffle({letters},{window}): the closed form counts {closed} word(s) against the {words} this row declares";
            }

            var presentation = Presentations.Shuffle<BigInteger, IntegerMaterial>(letterCount: letters, windowDegree: window, material: default);

            if (!presentation.HasFiniteNormalForms) {
                return $"shuffle({letters},{window}): the presentation reports no finite normal forms at an admitted tuple";
            }

            if ((closed != presentation.NormalFormCount) || (closed != presentation.GeneratorCount)) {
                return $"shuffle({letters},{window}): {presentation.GeneratorCount} generator(s) and {presentation.NormalFormCount} normal form(s), where the closed form counts {closed}";
            }

            for (var key = 0; (key < words); ++key) {
                var word = presentation.NormalFormWord(key: key);

                if ((1 != word.Length) || (key != word[0])) {
                    return $"shuffle({letters},{window}): the normal form at key {key} is not the one-letter word naming its own key";
                }
            }
        }

        return null;
    }

    /// <summary>Builds the simplicial complex generated by a list of top faces: every nonempty subset of a top face
    /// is a cell, cells are ordered by dimension then lexicographically, and a facet enters its coface's boundary
    /// with the sign of the position it drops. A local reimplementation of the same small builder
    /// <c>Subjects.cs</c>'s private <c>SimplicialComplex</c> carries — kept local so this file calls into no other law
    /// file's private surface.</summary>
    private static (int[] Dimensions, (int Face, int Coface, int Sign)[] Incidences) SimplicialComplexFromTopFaces(int[][] topFaces) {
        var collected = new List<int[]>();

        void Collect(int[] face) {
            collected.Add(item: face);

            if (face.Length < 2) { return; }

            for (var drop = 0; (drop < face.Length); ++drop) { Collect(face: DropAt(drop: drop, face: face)); }
        }

        foreach (var face in topFaces) {
            var sorted = ((int[])face.Clone());

            Array.Sort(array: sorted);
            Collect(face: sorted);
        }

        collected.Sort(comparison: CompareCells);

        var cells = new List<int[]>();

        foreach (var face in collected) {
            if ((0 == cells.Count) || (0 != CompareCells(left: cells[(cells.Count - 1)], right: face))) { cells.Add(item: face); }
        }

        var dimensions = new int[cells.Count];
        var incidences = new List<(int Face, int Coface, int Sign)>();

        for (var cell = 0; (cell < cells.Count); ++cell) { dimensions[cell] = (cells[cell].Length - 1); }

        for (var coface = 0; (coface < cells.Count); ++coface) {
            var vertices = cells[coface];

            if (vertices.Length < 2) { continue; }

            for (var drop = 0; (drop < vertices.Length); ++drop) {
                incidences.Add(item: (IndexOfCell(cells: cells, face: DropAt(drop: drop, face: vertices)), coface, ((0 == (drop & 1)) ? 1 : -1)));
            }
        }

        return (dimensions, [.. incidences]);
    }
    private static int CompareCells(int[] left, int[] right) =>
        ((left.Length != right.Length) ? left.Length.CompareTo(value: right.Length) : left.AsSpan().SequenceCompareTo(other: right.AsSpan()));
    private static int[] DropAt(int[] face, int drop) {
        var smaller = new int[(face.Length - 1)];
        var cursor = 0;

        for (var index = 0; (index < face.Length); ++index) {
            if (index != drop) { smaller[cursor++] = face[index]; }
        }

        return smaller;
    }
    private static int IndexOfCell(List<int[]> cells, int[] face) {
        for (var index = 0; (index < cells.Count); ++index) {
            if (0 == CompareCells(left: cells[index], right: face)) { return index; }
        }

        return -1;
    }
}
