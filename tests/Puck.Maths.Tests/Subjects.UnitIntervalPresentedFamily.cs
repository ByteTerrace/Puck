using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- the unit interval as a material family: ONE quiver presentation at three more materials ----
    //
    // The same operand encoding the tropical and counting graph laws use — lane i·n + j is the arc i → j, present when
    // the right operand's low bit is set — with the left operand folded onto a closed-unit raw instead of a Q16 weight.
    // The three materials answer three questions about the same graph: the most probable route, the widest bottleneck,
    // and the route whose steps' shortfalls from certainty still sum to less than one.

    /// <summary>The subject most-probable route through an explicit simple-path-length schedule at the
    /// most-likely-path material — the one material of the three whose product rounds and therefore carries no global
    /// semiring/star licence.</summary>
    /// <returns>The bound operation, owning its own presentation.</returns>
    public static VectorBinaryOp PresentedMostLikelyPathStar() {
        PresentedAlgebra<UnitInterval32, MostLikelyPathMaterial>? algebra = null;

        return (left, right, result) => {
            algebra ??= PresentedAlgebra<UnitInterval32, MostLikelyPathMaterial>.Create(presentation: CodiscreteQuiver<UnitInterval32, MostLikelyPathMaterial>(
                material: default,
                order: GraphOrder
            ));

            UnitIntervalTruncated(
                algebra: algebra,
                bound: (GraphOrder - 1),
                left: left,
                result: result,
                right: right
            );
        };
    }
    /// <summary>The shared-nothing most-probable-route oracle: simple-path enumeration with one rounding per step.</summary>
    /// <param name="left">The weight lanes.</param>
    /// <param name="right">The arc-presence lanes.</param>
    /// <param name="result">The best-likelihood lanes.</param>
    public static void MostLikelyPathStarOracle(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) =>
        UnitIntervalRoutes(
            left: left,
            result: result,
            right: right,
            route: Oracles.ClosedUnitBestRoute
        );
    /// <summary>The subject widest bottleneck: the guarded sum over all lengths of a quiver element at the fuzzy
    /// material, which rounds nowhere.</summary>
    /// <returns>The bound operation, owning its own presentation.</returns>
    public static VectorBinaryOp PresentedFuzzyStar() {
        PresentedAlgebra<UnitInterval32, FuzzyMaterial>? algebra = null;

        return (left, right, result) => {
            algebra ??= PresentedAlgebra<UnitInterval32, FuzzyMaterial>.Create(presentation: CodiscreteQuiver<UnitInterval32, FuzzyMaterial>(
                material: default,
                order: GraphOrder
            ));

            UnitIntervalStar(
                algebra: algebra,
                left: left,
                result: result,
                right: right
            );
        };
    }
    /// <summary>The shared-nothing widest-bottleneck oracle: the max-min triple loop, which forms no power at all.</summary>
    /// <param name="left">The weight lanes.</param>
    /// <param name="right">The arc-presence lanes.</param>
    /// <param name="result">The bottleneck lanes.</param>
    public static void FuzzyStarOracle(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) =>
        UnitIntervalRoutes(
            left: left,
            result: result,
            right: right,
            route: Oracles.ClosedUnitBottleneckClosure
        );
    /// <summary>The subject bounded-sum route: the guarded sum over all lengths of a quiver element at the bounded-sum
    /// material, which rounds nowhere.</summary>
    /// <returns>The bound operation, owning its own presentation.</returns>
    public static VectorBinaryOp PresentedBoundedSumStar() {
        PresentedAlgebra<UnitInterval32, BoundedSumMaterial>? algebra = null;

        return (left, right, result) => {
            algebra ??= PresentedAlgebra<UnitInterval32, BoundedSumMaterial>.Create(presentation: CodiscreteQuiver<UnitInterval32, BoundedSumMaterial>(
                material: default,
                order: GraphOrder
            ));

            UnitIntervalStar(
                algebra: algebra,
                left: left,
                result: result,
                right: right
            );
        };
    }
    /// <summary>The shared-nothing bounded-sum route oracle: simple-path enumeration in exact arbitrary width.</summary>
    /// <param name="left">The weight lanes.</param>
    /// <param name="right">The arc-presence lanes.</param>
    /// <param name="result">The route lanes.</param>
    public static void BoundedSumStarOracle(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) =>
        UnitIntervalRoutes(
            left: left,
            result: result,
            right: right,
            route: Oracles.ClosedUnitBoundedSumRoute
        );
    /// <summary>THE BOOLEAN SUBLATTICE TWIN. With every present arc carrying the exact <c>One</c>, the three
    /// unit-interval materials are confined to the two endpoints, where the maximum is disjunction and all three
    /// products — the rounded product, the minimum and the bounded sum — are conjunction. Their quiver answers must
    /// therefore agree with each other AND with the Boolean material's reachability, exactly.</summary>
    /// <returns>The bound operation, owning its own presentations.</returns>
    /// <remarks>A lane the three disagree on, or one carrying a value that is neither endpoint, is poisoned rather than
    /// asserted here, so the failure surfaces through the same lane comparison as everything else.</remarks>
    public static VectorBinaryOp PresentedUnitIntervalBooleanSublattice() {
        PresentedAlgebra<UnitInterval32, BoundedSumMaterial>? bounded = null;
        PresentedAlgebra<UnitInterval32, FuzzyMaterial>? fuzzy = null;
        PresentedAlgebra<UnitInterval32, MostLikelyPathMaterial>? likely = null;

        return (left, right, result) => {
            bounded ??= PresentedAlgebra<UnitInterval32, BoundedSumMaterial>.Create(presentation: CodiscreteQuiver<UnitInterval32, BoundedSumMaterial>(
                material: default,
                order: GraphOrder
            ));
            fuzzy ??= PresentedAlgebra<UnitInterval32, FuzzyMaterial>.Create(presentation: CodiscreteQuiver<UnitInterval32, FuzzyMaterial>(
                material: default,
                order: GraphOrder
            ));
            likely ??= PresentedAlgebra<UnitInterval32, MostLikelyPathMaterial>.Create(presentation: CodiscreteQuiver<UnitInterval32, MostLikelyPathMaterial>(
                material: default,
                order: GraphOrder
            ));

            var count = right.Length;
            var boundedLanes = new long[count];
            var fuzzyLanes = new long[count];
            var likelyLanes = new long[count];
            // The weight lane is IGNORED here: the sublattice statement is about the two endpoints, so every present arc
            // is forced onto the exact one and the operand's own weight has no say.
            var certain = new long[count];

            Array.Fill(
                array: certain,
                value: ((long)ClosedUnitOneRaw)
            );
            UnitIntervalStar(
                algebra: bounded,
                left: certain,
                result: boundedLanes,
                right: right
            );
            UnitIntervalStar(
                algebra: fuzzy,
                left: certain,
                result: fuzzyLanes,
                right: right
            );
            UnitIntervalTruncated(
                algebra: likely,
                bound: (GraphOrder - 1),
                left: certain,
                result: likelyLanes,
                right: right
            );

            for (var lane = 0; (lane < count); ++lane) {
                var value = likelyLanes[lane];

                if (
                    (value != fuzzyLanes[lane]) ||
                    (value != boundedLanes[lane])
                ) {
                    result[lane] = -1L;
                } else if (
                    (0L != value) &&
                    (((long)ClosedUnitOneRaw) != value)
                ) {
                    result[lane] = -2L;
                } else {
                    result[lane] = ((0L == value)
                        ? 0L
                        : 1L
                    );
                }
            }
        };
    }
    /// <summary>THE POWER-OF-TWO TWIN, most-likely-path side: arc weights are exact powers of two, so every product is
    /// exact and the best route's likelihood is <c>2⁻ᵗ</c> for an integer total cost <c>t</c>. The lane reports that
    /// <c>t</c>, or <c>-1</c> where no route exists.</summary>
    /// <returns>The bound operation, owning its own presentation.</returns>
    /// <remarks>THE ENVELOPE, and why the twin is silent outside it: an arc's exponent is drawn from zero through seven
    /// and a simple path over four vertices spends at most three arcs, so a total cost never exceeds 21 and the carrier
    /// holds <c>2⁻²¹</c> exactly. Above a total of 32 the product underflows the <c>2⁻³²</c> grid and the likelihood
    /// collapses to the impossible outcome while the tropical cost stays finite, so the isomorphism is a law on this
    /// subfamily and a falsehood beyond it.</remarks>
    public static VectorBinaryOp PresentedMostLikelyPathPowerOfTwo() {
        PresentedAlgebra<UnitInterval32, MostLikelyPathMaterial>? algebra = null;

        return (left, right, result) => {
            algebra ??= PresentedAlgebra<UnitInterval32, MostLikelyPathMaterial>.Create(presentation: CodiscreteQuiver<UnitInterval32, MostLikelyPathMaterial>(
                material: default,
                order: GraphOrder
            ));

            var count = right.Length;
            var coefficients = new UnitInterval32[count];
            var keys = new long[count];
            var support = 0;

            for (var lane = 0; (lane < count); ++lane) {
                if (0L == (right[lane] & 1L)) { continue; }

                coefficients[support] = UnitInterval32.Create(value: (ClosedUnitOneRaw >> PowerOfTwoExponent(raw: left[lane])));
                keys[support] = lane;
                ++support;
            }

            var element = algebra.FromSupport(
                keys: keys.AsSpan(
                    length: support,
                    start: 0
                ),
                coefficients: coefficients.AsSpan(
                    length: support,
                    start: 0
                )
            );

            result.Fill(value: -1L);

            var total = algebra.TruncatedSum(
                bound: (GraphOrder - 1),
                value: element
            );

            for (var index = 0; (index < total.SupportCount); ++index) {
                var raw = total.Coefficients[index].Value;

                // Inside the envelope every reachable value is an exact power of two; anything else is a defect, and it
                // is reported as a lane value no cost can take rather than swallowed.
                result[((int)total.Keys[index])] = ((0UL == (raw & (raw - 1UL)))
                    ? ((long)(UnitInterval32.FractionBitCount - BitOperations.TrailingZeroCount(value: raw)))
                    : -2L
                );
            }
        };
    }
    /// <summary>THE POWER-OF-TWO TWIN, tropical side: the same graph with the same exponents as integer costs, so the
    /// shortest distance IS the exponent the likelihood side reports. The lane reports that integer cost, or <c>-1</c>
    /// where no route exists.</summary>
    /// <returns>The bound operation, owning its own presentation.</returns>
    public static VectorBinaryOp PresentedTropicalPowerOfTwo() {
        PresentedAlgebra<FixedQ4816, TropicalMaterial>? algebra = null;

        return (left, right, result) => {
            algebra ??= PresentedAlgebra<FixedQ4816, TropicalMaterial>.Create(presentation: CodiscreteQuiver<FixedQ4816, TropicalMaterial>(
                material: default,
                order: GraphOrder
            ));

            var count = right.Length;
            var coefficients = new FixedQ4816[count];
            var keys = new long[count];
            var support = 0;

            for (var lane = 0; (lane < count); ++lane) {
                if (0L == (right[lane] & 1L)) { continue; }

                coefficients[support] = FixedQ4816.FromInteger(value: PowerOfTwoExponent(raw: left[lane]));
                keys[support] = lane;
                ++support;
            }

            var element = algebra.FromSupport(
                keys: keys.AsSpan(
                    length: support,
                    start: 0
                ),
                coefficients: coefficients.AsSpan(
                    length: support,
                    start: 0
                )
            );

            result.Fill(value: -1L);

            if (!algebra.TrySumOverAllLengths(
                obstruction: out _,
                total: out var total,
                value: element
            )) {
                result.Fill(value: -4L);

                return;
            }

            for (var index = 0; (index < total.SupportCount); ++index) {
                var raw = total.Coefficients[index].Value;

                // A cost that is not an exact integer of the carrier would mean the twin's arithmetic left the exact
                // subfamily, which is a defect rather than a disagreement about the answer.
                result[((int)total.Keys[index])] = ((0L == (raw & ((1L << FixedQ4816.FractionBitCount) - 1L)))
                    ? (raw >> FixedQ4816.FractionBitCount)
                    : -3L
                );
            }
        };
    }
    /// <summary>The fused canary's charge lanes: the material's fused sum, which takes the exact three-factor product of
    /// each term and rounds it ONCE.</summary>
    /// <param name="left">The first operand vector; its first half carries the charges and its second the left values.</param>
    /// <param name="right">The second operand vector; its first half carries the right values and its second a second
    /// set of charges.</param>
    /// <param name="result">The two folded raws, then zeros.</param>
    public static void UnitIntervalFusedTerms(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var material = default(MostLikelyPathMaterial);
        var terms = UnitIntervalCanaryTerms(
            left: left,
            right: right
        );

        result.Clear();
        result[0] = ((long)material.FusedChargedSum(
            charges: terms.FirstCharges,
            lane: ChargeLane.General,
            left: terms.Left,
            right: terms.Right
        ).Value);
        result[1] = ((long)material.FusedChargedSum(
            charges: terms.SecondCharges,
            lane: ChargeLane.General,
            left: terms.Left,
            right: terms.Right
        ).Value);
    }
    /// <summary>The fused canary's other discipline: the same fold with each term's two products rounded separately, the
    /// shape a material without a three-factor product is forced into.</summary>
    /// <param name="left">The first operand vector.</param>
    /// <param name="right">The second operand vector.</param>
    /// <param name="result">The two folded raws, then zeros.</param>
    public static void UnitIntervalPerTermRounding(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result) {
        var terms = UnitIntervalCanaryTerms(
            left: left,
            right: right
        );

        result.Clear();
        result[0] = ((long)PerTermRoundedFold(
            charges: terms.FirstCharges,
            left: terms.Left,
            right: terms.Right
        ).Value);
        result[1] = ((long)PerTermRoundedFold(
            charges: terms.SecondCharges,
            left: terms.Left,
            right: terms.Right
        ).Value);
    }
    /// <summary>Proves the three unit-interval materials ARE the semirings they claim to be, stated against
    /// arbitrary-width arithmetic rather than against the carrier they are built on: one addition (the maximum) and
    /// three products (the once-rounded product, the minimum, and the bounded sum), each with its identities, its zero
    /// test, its distributivity over the addition, and its fused fold at a two-term span — plus the absolute statement,
    /// at the one material of the three whose product rounds, that a fused term of three INTERIOR factors is one
    /// ties-to-even rounding of their exact product and not two.</summary>
    /// <param name="left">The first sampled operand lane pair.</param>
    /// <param name="right">The second sampled operand lane pair.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnitIntervalSemiringsExact(long[] left, long[] right) {
        var rawA = ClosedUnitRaw(raw: left[0]);
        var rawB = ClosedUnitRaw(raw: right[0]);
        var rawC = ClosedUnitRaw(raw: left[1]);
        var exactOne = new BigInteger(value: ClosedUnitOneRaw);
        var exactA = new BigInteger(value: rawA);
        var exactB = new BigInteger(value: rawB);

        // The ABSOLUTE statement of the fused term's ONE rounding, which no other case makes: a charge times two
        // coefficients, all three strictly inside the interval, must land on one ties-to-even rounding of the exact
        // triple product. Every other fused fold in the suite — the quivers, the generic leg below — charges its terms
        // with one, and at a charge of one the one-rounding and two-rounding disciplines are bit-identical by
        // definition, so a fold that quietly nested two pairwise products passes all of them; the canary passes too,
        // because both of ITS sides would then be two-rounding folds that still disagree with each other. This leg is
        // the one that fails outright.
        var fusedCharge = ClosedUnitInterior(raw: left[0]);
        var fusedLeft = ClosedUnitInterior(raw: left[1]);
        var fusedRight = ClosedUnitInterior(raw: right[0]);
        var fusedTerm = default(MostLikelyPathMaterial).FusedChargedSum(
            charges: [fusedCharge],
            lane: ChargeLane.General,
            left: [fusedLeft],
            right: [fusedRight]
        );
        var fusedExpected = Oracles.ClosedUnitTripleProduct(
            x: fusedCharge.Value,
            y: fusedLeft.Value,
            z: fusedRight.Value
        );

        if (fusedTerm.Value != fusedExpected) { return $"most-likely-path: the fused term at ({fusedCharge.Value}, {fusedLeft.Value}, {fusedRight.Value}) is {fusedTerm.Value}, expected {fusedExpected}"; }

        return (UnitIntervalSemiring<MostLikelyPathMaterial>(
            name: "most-likely-path",
            rawA: rawA,
            rawB: rawB,
            rawC: rawC,
            product: new BigInteger(value: Oracles.ClosedUnitProduct(
                x: rawA,
                y: rawB
            ))
        )
            ?? (UnitIntervalSemiring<FuzzyMaterial>(
            name: "fuzzy",
            rawA: rawA,
            rawB: rawB,
            rawC: rawC,
            product: BigInteger.Min(
                left: exactA,
                right: exactB
            )
        )
            ?? UnitIntervalSemiring<BoundedSumMaterial>(
            name: "bounded-sum",
            rawA: rawA,
            rawB: rawB,
            rawC: rawC,
            product: BigInteger.Max(
                left: ((exactA + exactB) - exactOne),
                right: BigInteger.Zero
            )
        )));
    }
    /// <summary>Proves the guarded sum over all lengths is licensed only where the shipped material declares the exact
    /// laws it needs: the fuzzy and bounded-sum semirings issue idempotent certificates, while the rounded
    /// most-likely-path material is refused before a step and remains available through an explicit finite schedule.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnitIntervalStarLicensing() {
        // A cycle through every vertex plus a chord: no power is ever zero, so nilpotence is unavailable and only
        // idempotence can license the sum.
        var arcs = ((ReadOnlySpan<(int Source, int Target, ulong Weight)>)[
            (0, 1, (ClosedUnitOneRaw - 1UL)), (1, 2, (ClosedUnitOneRaw >> 1)), (2, 0, ((ClosedUnitOneRaw * 3UL) >> 2)),
            (2, 3, (ClosedUnitOneRaw >> 3)), (3, 1, ClosedUnitOneRaw),
        ]);
        var counting = PresentedAlgebra<BigInteger, CountingMaterial>.Create(presentation: CodiscreteQuiver<BigInteger, CountingMaterial>(
            material: default,
            order: GraphOrder
        ));
        var countingKeys = new long[arcs.Length];
        var countingValues = new BigInteger[arcs.Length];

        for (var index = 0; (index < arcs.Length); ++index) {
            countingKeys[index] = ((arcs[index].Source * GraphOrder) + arcs[index].Target);
            countingValues[index] = BigInteger.One;
        }

        Array.Sort(
            items: countingValues,
            keys: countingKeys
        );

        if (counting.TrySumOverAllLengths(
            value: counting.FromSupport(
                coefficients: countingValues,
                keys: countingKeys
            ),
            total: out _,
            obstruction: out var countingObstruction
        )) {
            return "the counting material issued a certificate on the cyclic graph, so the contrast the claim rests on is gone";
        }

        if (
            (ClosureCertificate.Nilpotent != countingObstruction.Attempted) ||
            (0L >= countingObstruction.StepsTaken)
        ) {
            return $"the counting refusal attempted {countingObstruction.Attempted} after {countingObstruction.StepsTaken} step(s), expected Nilpotent after at least one";
        }

        var likely = PresentedAlgebra<UnitInterval32, MostLikelyPathMaterial>.Create(presentation: CodiscreteQuiver<UnitInterval32, MostLikelyPathMaterial>(
            material: default,
            order: GraphOrder
        ));
        var likelyKeys = new long[arcs.Length];
        var likelyValues = new UnitInterval32[arcs.Length];

        for (var index = 0; (index < arcs.Length); ++index) {
            likelyKeys[index] = ((arcs[index].Source * GraphOrder) + arcs[index].Target);
            likelyValues[index] = UnitInterval32.Create(value: arcs[index].Weight);
        }

        Array.Sort(
            items: likelyValues,
            keys: likelyKeys
        );

        if (likely.TrySumOverAllLengths(
            value: likely.FromSupport(
                coefficients: likelyValues,
                keys: likelyKeys
            ),
            total: out _,
            obstruction: out var likelyObstruction
        )) {
            return "most-likely-path: the rounded material issued an exact-semiring closure certificate";
        }

        if (
            (ClosureCertificate.None != likelyObstruction.Attempted) ||
            (0L != likelyObstruction.StepsTaken)
        ) {
            return $"most-likely-path: the refusal attempted {likelyObstruction.Attempted} after {likelyObstruction.StepsTaken} step(s), expected None before any step";
        }

        return (UnitIntervalLicence<FuzzyMaterial>(
            arcs: arcs,
            name: "fuzzy"
        )
            ?? UnitIntervalLicence<BoundedSumMaterial>(
            arcs: arcs,
            name: "bounded-sum"
        ));
    }
    /// <summary>Proves the fuzzy material's complement is a De Morgan involution at the carrier's own endpoints and
    /// neighbourhoods, and that the pattern lens's complement — until now a Boolean-only surface — is exact and GRADED
    /// there: a complemented pattern carries <c>1 − w</c> at every span, complementing twice returns the pattern, and the
    /// free monoid still refuses for having no finite basis to complement against.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FuzzyComplementLens() {
        var material = default(FuzzyMaterial);
        var ladder = ((ReadOnlySpan<ulong>)[
            0UL, 1UL, (1UL << 15), (1UL << 31), ((ClosedUnitOneRaw * 3UL) >> 2), (ClosedUnitOneRaw - 1UL), ClosedUnitOneRaw,
        ]);

        if (!material.Add(
            left: material.One,
            right: material.One
        ).Equals(other: material.One)) {
            return "the fuzzy addition is not idempotent at one, which is the admission condition for a De Morgan complement";
        }

        foreach (var first in ladder) {
            var a = UnitInterval32.Create(value: first);

            if (material.Complement(value: material.Complement(value: a)) != a) { return $"the complement is not an involution at raw {first}"; }

            foreach (var second in ladder) {
                var b = UnitInterval32.Create(value: second);
                var complementOfSum = material.Complement(value: material.Add(
                    left: a,
                    right: b
                ));
                var productOfComplements = material.Multiply(
                    left: material.Complement(value: a),
                    right: material.Complement(value: b)
                );
                var complementOfProduct = material.Complement(value: material.Multiply(
                    left: a,
                    right: b
                ));
                var sumOfComplements = material.Add(
                    left: material.Complement(value: a),
                    right: material.Complement(value: b)
                );

                if (complementOfSum != productOfComplements) { return $"De Morgan fails on the sum at ({first}, {second})"; }
                if (complementOfProduct != sumOfComplements) { return $"De Morgan fails on the product at ({first}, {second})"; }
            }
        }

        // The lens. A window of two over two letters holds the empty word, the two one-letter words and the four
        // two-letter words, so the basis a complement is taken against is seven spans wide.
        var pattern = TokenPattern<UnitInterval32, FuzzyMaterial>.Create(
            letterCount: 2,
            material: default,
            window: 2
        );
        var algebra = pattern.Algebra;
        var count = algebra.Presentation.NormalFormCount;

        if (7 != count) { return $"the window-two alphabet-two basis holds {count} spans, expected 7"; }

        var half = UnitInterval32.Create(value: (ClosedUnitOneRaw >> 1));
        var letters = pattern.Union(
            left: pattern.Predicate(letters: 1UL),
            right: pattern.Predicate(letters: 2UL)
        );
        var graded = pattern.Scale(
            value: letters,
            weight: half
        );
        var complemented = pattern.Complement(value: graded);

        for (var key = 0; (key < count); ++key) {
            var expected = UnitInterval32.Complement(value: graded[key]);

            if (complemented[key] != expected) { return $"the complemented pattern carries {complemented[key].Value} at key {key}, expected {expected.Value}"; }
        }

        // Graded rather than two-valued: the one-letter spans sit at the exact half on BOTH sides, which no Boolean
        // complement can produce.
        var gradedSpans = 0;

        for (var key = 0; (key < count); ++key) {
            if (
                (graded[key] == half) &&
                (complemented[key] == half)
            ) { ++gradedSpans; }
        }

        if (2 != gradedSpans) { return $"{gradedSpans} span(s) carry the exact half on both sides, expected the two one-letter spans"; }

        if (!algebra.AreEqual(
            left: pattern.Complement(value: complemented),
            right: graded
        )) { return "complementing a pattern twice did not return it"; }
        if (pattern.Complement(value: algebra.Zero).SupportCount != count) { return "the complement of the empty language is not the whole window"; }

        // The free monoid has no finite basis, so the complement is refused rather than truncated.
        var free = TokenPattern<UnitInterval32, FuzzyMaterial>.Create(
            letterCount: 2,
            material: default,
            window: 0
        );

        try {
            _ = free.Complement(value: free.EmptyWord);

            return "a free monoid admitted a complement, which has no finite basis to complement against";
        } catch (InvalidOperationException) {
            return null;
        }
    }
    /// <summary>Proves the carrier's three-factor product is ONE ties-to-even rounding of the exact triple product, that
    /// its identities are exact, and that it is the documented partial evaluation of the pairwise product at a factor of
    /// one.</summary>
    /// <param name="left">The first sampled operand lane pair.</param>
    /// <param name="right">The second sampled operand lane pair.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ClosedUnitTripleProductExact(long[] left, long[] right) {
        var rawX = ClosedUnitRaw(raw: left[0]);
        var rawY = ClosedUnitRaw(raw: left[1]);
        var rawZ = ClosedUnitRaw(raw: right[0]);
        var x = UnitInterval32.Create(value: rawX);
        var y = UnitInterval32.Create(value: rawY);
        var z = UnitInterval32.Create(value: rawZ);
        var product = UnitInterval32.Multiply(
            x: x,
            y: y,
            z: z
        );
        var expected = Oracles.ClosedUnitTripleProduct(
            x: rawX,
            y: rawY,
            z: rawZ
        );

        if (product.Value != expected) { return $"the triple product of ({rawX}, {rawY}, {rawZ}) is {product.Value}, expected {expected}"; }

        // At a factor of one the triple product IS the pairwise product, in every position: the third factor contributes
        // an exact shift and cannot introduce a second rounding.
        if (UnitInterval32.Multiply(
            x: x,
            y: y,
            z: UnitInterval32.One
        ) != UnitInterval32.Multiply(
            x: x,
            y: y
        )) { return $"the triple product at a trailing one moved ({rawX}, {rawY})"; }
        if (UnitInterval32.Multiply(
            x: UnitInterval32.One,
            y: y,
            z: z
        ) != UnitInterval32.Multiply(
            x: y,
            y: z
        )) { return $"the triple product at a leading one moved ({rawY}, {rawZ})"; }
        if (UnitInterval32.Multiply(
            x: x,
            y: UnitInterval32.Zero,
            z: z
        ) != UnitInterval32.Zero) { return $"the triple product is not annihilated by zero at ({rawX}, {rawZ})"; }

        // The order of the factors cannot matter, because the exact product is taken before the single rounding.
        if (UnitInterval32.Multiply(
            x: x,
            y: y,
            z: z
        ) != UnitInterval32.Multiply(
            x: z,
            y: x,
            z: y
        )) { return $"the triple product depends on the order of ({rawX}, {rawY}, {rawZ})"; }

        return null;
    }

    // The canary's operand fold: the low thirty-two bits, which is always a legal raw and always BELOW one. The carrier's
    // own fold saturates half its draws onto the upper endpoint, where a factor of one makes the two rounding
    // disciplines coincide by definition; this one keeps every factor strictly inside the interval, which is where the
    // second rounding is free to move the answer.
    private static UnitInterval32 ClosedUnitInterior(long raw) =>
        UnitInterval32.Create(value: unchecked((ulong)raw) & (ClosedUnitOneRaw - 1UL));
    // The three-factor exponent ladder the power-of-two twin runs on: zero through seven, so a three-arc simple path
    // spends at most 21 of the carrier's 32 fraction bits and every product on it is exact.
    private static int PowerOfTwoExponent(long raw) =>
        ((int)((raw >>> 7) & 7L));
    // The canary's three term vectors, folded from the two operand vectors. The fold is deterministic and identical on
    // both disciplines, so a divergence is the rounding and never the operands.
    private static (UnitInterval32[] FirstCharges, UnitInterval32[] SecondCharges, UnitInterval32[] Left, UnitInterval32[] Right) UnitIntervalCanaryTerms(ReadOnlySpan<long> left, ReadOnlySpan<long> right) {
        var width = (left.Length / 2);
        var firstCharges = new UnitInterval32[width];
        var secondCharges = new UnitInterval32[width];
        var leftValues = new UnitInterval32[width];
        var rightValues = new UnitInterval32[width];

        for (var index = 0; (index < width); ++index) {
            firstCharges[index] = ClosedUnitInterior(raw: left[index]);
            secondCharges[index] = ClosedUnitInterior(raw: right[(width + index)]);
            leftValues[index] = ClosedUnitInterior(raw: left[(width + index)]);
            rightValues[index] = ClosedUnitInterior(raw: right[index]);
        }

        return (firstCharges, secondCharges, leftValues, rightValues);
    }
    // The fold a material without a three-factor product is forced into: round the pair, then round again against the
    // charge. It is the discipline the canary requires the shipped fold to DIVERGE from.
    private static UnitInterval32 PerTermRoundedFold(ReadOnlySpan<UnitInterval32> charges, ReadOnlySpan<UnitInterval32> left, ReadOnlySpan<UnitInterval32> right) {
        var accumulator = UnitInterval32.Zero;

        for (var index = 0; (index < charges.Length); ++index) {
            var term = UnitInterval32.Multiply(
                x: charges[index],
                y: UnitInterval32.Multiply(
                    x: left[index],
                    y: right[index]
                )
            );

            accumulator = UnitInterval32.Max(
                x: accumulator,
                y: term
            );
        }

        return accumulator;
    }
    // One unit-interval material against its arbitrary-width specification. The product's expected value is passed in
    // because it is the ONE thing that differs between the three; everything below — the addition, the identities, the
    // zero test, distributivity, and the fused fold at a two-term span — is the same statement at all of them, so a
    // material that quietly stopped being a semiring fails here rather than only at a graph.
    private static string? UnitIntervalSemiring<TOps>(string name, ulong rawA, ulong rawB, ulong rawC, BigInteger product)
        where TOps : struct, IMaterialOps<UnitInterval32, TOps> {
        var material = default(TOps);
        var a = UnitInterval32.Create(value: rawA);
        var b = UnitInterval32.Create(value: rawB);
        var c = UnitInterval32.Create(value: rawC);
        var exactA = new BigInteger(value: rawA);
        var exactB = new BigInteger(value: rawB);

        if (material.Zero.Value != 0UL) { return $"{name}: the additive identity has raw {material.Zero.Value}"; }
        if (material.One.Value != ClosedUnitOneRaw) { return $"{name}: the multiplicative identity has raw {material.One.Value}"; }
        if (material.Add(
            left: a,
            right: b
        ).Value != BigInteger.Max(
            left: exactA,
            right: exactB
        )) { return $"{name}: the sum of {rawA} and {rawB} is not their maximum"; }
        if (material.Multiply(
            left: a,
            right: b
        ).Value != product) {
            return $"{name}: the product of {rawA} and {rawB} is {material.Multiply(
            left: a,
            right: b
        ).Value}, expected {product}";
        }
        if (material.IsZero(value: a) != (0UL == rawA)) { return $"{name}: the zero test disagrees at raw {rawA}"; }
        if (material.Add(
            left: a,
            right: a
        ) != a) { return $"{name}: the addition is not idempotent at raw {rawA}"; }
        if (material.Add(
            left: a,
            right: material.Zero
        ) != a) { return $"{name}: zero is not neutral for the addition at raw {rawA}"; }
        if (material.Multiply(
            left: a,
            right: material.One
        ) != a) { return $"{name}: one is not neutral for the product at raw {rawA}"; }
        if (!material.IsZero(value: material.Multiply(
            left: a,
            right: material.Zero
        ))) { return $"{name}: zero does not annihilate the product at raw {rawA}"; }

        // Distributivity over the maximum, which over the rounding product holds because rounding to nearest is
        // monotone: the larger operand cannot round below the smaller one's product.
        var distributed = material.Multiply(
            left: a,
            right: material.Add(
                left: b,
                right: c
            )
        );
        var separated = material.Add(
            left: material.Multiply(
                left: a,
                right: b
            ),
            right: material.Multiply(
                left: a,
                right: c
            )
        );

        if (distributed != separated) { return $"{name}: the product does not distribute over the maximum at ({rawA}, {rawB}, {rawC})"; }

        // The fused fold at a two-term span, against the same specification the pairwise product answers to: the fold is
        // the maximum of its terms and nothing else, so a fold that silently reordered or dropped a term fails here.
        var folded = material.FusedChargedSum(
            charges: [material.One, material.One],
            left: [a, a],
            right: [b, c],
            lane: ChargeLane.General
        );
        var expected = material.Add(
            left: material.Multiply(
                left: a,
                right: b
            ),
            right: material.Multiply(
                left: a,
                right: c
            )
        );

        if (folded != expected) { return $"{name}: the two-term fused sum is {folded.Value}, expected {expected.Value}"; }

        return null;
    }
    // One material's licence, proved rather than assumed: the star issues, the total absorbs one more step, and the
    // exact finite truncation at four times the basis size lands on the same element.
    private static string? UnitIntervalLicence<TOps>(string name, ReadOnlySpan<(int Source, int Target, ulong Weight)> arcs)
        where TOps : struct, IIdempotentMaterial<UnitInterval32, TOps> {
        var algebra = PresentedAlgebra<UnitInterval32, TOps>.Create(presentation: CodiscreteQuiver<UnitInterval32, TOps>(
            material: default,
            order: GraphOrder
        ));
        var coefficients = new UnitInterval32[arcs.Length];
        var keys = new long[arcs.Length];

        for (var index = 0; (index < arcs.Length); ++index) {
            coefficients[index] = UnitInterval32.Create(value: arcs[index].Weight);
            keys[index] = ((arcs[index].Source * GraphOrder) + arcs[index].Target);
        }

        Array.Sort(
            items: coefficients,
            keys: keys
        );

        var element = algebra.FromSupport(
            coefficients: coefficients,
            keys: keys
        );

        if (!algebra.TrySumOverAllLengths(
            obstruction: out var obstruction,
            total: out var total,
            value: element
        )) {
            return $"{name}: the star refused, attempting {obstruction.Attempted} after {obstruction.StepsTaken} step(s)";
        }

        if (!algebra.AreEqual(
            left: algebra.Add(
                left: total,
                right: algebra.Multiply(
                    left: total,
                    right: element
                )
            ),
            right: total
        )) {
            return $"{name}: the total is not a fixed point of one more step, so the stabilization it was issued on is not one";
        }

        if (!algebra.AreEqual(
            left: algebra.TruncatedSum(
                bound: ((4 * GraphOrder) * GraphOrder),
                value: element
            ),
            right: total
        )) {
            return $"{name}: the exact finite truncation disagrees with the total the certificate licensed";
        }

        var material = default(TOps);

        for (var vertex = 0; (vertex < GraphOrder); ++vertex) {
            if (total[((vertex * GraphOrder) + vertex)] != material.One) { return $"{name}: the diagonal at object {vertex} is not the material's one"; }
        }

        return null;
    }
    // The star at one unit-interval material, written into the lane vector: absent keys stay zero, and a refusal poisons
    // every lane rather than throwing, so it reaches the comparison as a mismatch.
    private static void UnitIntervalStar<TOps>(PresentedAlgebra<UnitInterval32, TOps> algebra, ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result)
        where TOps : struct, IMaterialOps<UnitInterval32, TOps> {
        var count = right.Length;
        var coefficients = new UnitInterval32[count];
        var keys = new long[count];
        var support = 0;

        for (var lane = 0; (lane < count); ++lane) {
            if (0L == (right[lane] & 1L)) { continue; }

            coefficients[support] = ClosedUnit(raw: left[lane]);
            keys[support] = lane;
            ++support;
        }

        var element = algebra.FromSupport(
            keys: keys.AsSpan(
                length: support,
                start: 0
            ),
            coefficients: coefficients.AsSpan(
                length: support,
                start: 0
            )
        );

        result.Clear();

        if (!algebra.TrySumOverAllLengths(
            obstruction: out _,
            total: out var total,
            value: element
        )) {
            result.Fill(value: -1L);

            return;
        }

        for (var index = 0; (index < total.SupportCount); ++index) {
            result[((int)total.Keys[index])] = ((long)total.Coefficients[index].Value);
        }
    }
    // The scheduled-material sibling of UnitIntervalStar. It names the finite left-extension schedule explicitly
    // instead of asking a rounded multiplication for an exact infinite-sum certificate.
    private static void UnitIntervalTruncated<TOps>(
        PresentedAlgebra<UnitInterval32, TOps> algebra,
        ReadOnlySpan<long> left,
        ReadOnlySpan<long> right,
        int bound,
        Span<long> result
    )
        where TOps : struct, IMaterialOps<UnitInterval32, TOps> {
        var count = right.Length;
        var coefficients = new UnitInterval32[count];
        var keys = new long[count];
        var support = 0;

        for (var lane = 0; (lane < count); ++lane) {
            if (0L == (right[lane] & 1L)) { continue; }

            coefficients[support] = ClosedUnit(raw: left[lane]);
            keys[support] = lane;
            ++support;
        }

        var element = algebra.FromSupport(
            keys: keys.AsSpan(
                length: support,
                start: 0
            ),
            coefficients: coefficients.AsSpan(
                length: support,
                start: 0
            )
        );
        var total = algebra.TruncatedSum(
            bound: bound,
            value: element
        );

        result.Clear();

        for (var index = 0; (index < total.SupportCount); ++index) {
            result[((int)total.Keys[index])] = ((long)total.Coefficients[index].Value);
        }
    }
    // The oracle side of the same encoding: the operand pair becomes a weight matrix of closed-unit raws, and the named
    // reference walks it.
    private static void UnitIntervalRoutes(ReadOnlySpan<long> left, ReadOnlySpan<long> right, Span<long> result, RouteOracle route) {
        var count = right.Length;
        var best = new ulong[count];
        var weights = new ulong[count];

        for (var lane = 0; (lane < count); ++lane) {
            weights[lane] = ((0L == (right[lane] & 1L))
                ? 0UL
                : ClosedUnitRaw(raw: left[lane])
            );
        }

        route(
            weights,
            GraphOrder,
            best
        );

        for (var lane = 0; (lane < count); ++lane) { result[lane] = ((long)best[lane]); }
    }

}
