using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>Claims for <see cref="FixedContributionFold"/>. Sampled claims receive all operands from
/// <see cref="Domains"/>; only the explicitly exhaustive grids and hand-derived boundary witnesses live here.</summary>
internal static class FixedContributionFoldClaims {
    private const long OneRaw = (1L << FixedQ4816.FractionBitCount);
    private const long HalfRaw = (OneRaw >> 1);

    /// <summary>Checks a small exact grid across both optional stages against the shared-nothing oracle.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the grid agrees.</returns>
    public static string? FormulaExactGrid() {
        long[] operands = [-2L, -1L, 0L, 1L, 2L];
        long?[] radii = [null, 0L, 1L, 3L];
        (long Minimum, long Maximum)[] ranges = [(-4L, 4L), (0L, 4L)];

        foreach (var baseline in operands) {
            foreach (var poolDelta in operands) {
                foreach (var outsideDelta in operands) {
                    foreach (var radius in radii) {
                        foreach (var (minimum, maximum) in ranges) {
                            long?[] thresholds = [null, minimum, ((minimum + maximum) / 2L), maximum];

                            foreach (var threshold in thresholds) {
                                if (CompareWithOracle(
                                    baselineRaw: baseline,
                                    poolDeltaRaw: poolDelta,
                                    outsidePoolDeltaRaw: outsideDelta,
                                    poolRadiusRaw: radius,
                                    minimumRaw: minimum,
                                    maximumRaw: maximum,
                                    thresholdRaw: threshold
                                ) is { } failure) {
                                    return $"grid {failure}";
                                }
                            }
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Maps a full-width edge/random/frontier sample to a valid fold configuration and checks the oracle.</summary>
    /// <param name="left">The first four domain raws.</param>
    /// <param name="right">The second four domain raws.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the sample agrees.</returns>
    public static string? FormulaSample(long[] left, long[] right) {
        var minimum = Math.Min(val1: left[3], val2: right[0]);
        var maximum = Math.Max(val1: left[3], val2: right[0]);
        var radius = (((right[2] & 1L) == 0L) ? null : (long?)NonNegative(raw: right[1]));
        var threshold = (((right[2] & 2L) == 0L) ? null : (long?)FoldIntoRange(raw: right[3], minimum: minimum, maximum: maximum));

        return CompareWithOracle(
            baselineRaw: left[0],
            poolDeltaRaw: left[1],
            outsidePoolDeltaRaw: left[2],
            poolRadiusRaw: radius,
            minimumRaw: minimum,
            maximumRaw: maximum,
            thresholdRaw: threshold
        );
    }

    /// <summary>Checks the baseline-zero, no-pool specialization over a small exact grid.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the grid agrees.</returns>
    public static string? NoPoolExactGrid() {
        long[] operands = [-OneRaw, -1L, 0L, 1L, OneRaw];

        foreach (var poolDelta in operands) {
            foreach (var outsideDelta in operands) {
                foreach (var threshold in new long?[] { null, 0L, HalfRaw, OneRaw }) {
                    if (CompareNoPoolSpecialization(
                        poolDeltaRaw: poolDelta,
                        outsidePoolDeltaRaw: outsideDelta,
                        minimumRaw: 0L,
                        maximumRaw: OneRaw,
                        thresholdRaw: threshold
                    ) is { } failure) {
                        return $"grid {failure}";
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Checks the no-pool specialization over a full-width edge/random/frontier sample.</summary>
    /// <param name="left">The first two domain raws.</param>
    /// <param name="right">The second two domain raws.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the sample agrees.</returns>
    public static string? NoPoolSample(long[] left, long[] right) {
        var minimum = Math.Min(val1: left[1], val2: right[0]);
        var maximum = Math.Max(val1: left[1], val2: right[0]);
        var threshold = (((right[1] & 1L) == 0L) ? null : (long?)FoldIntoRange(raw: left[0], minimum: minimum, maximum: maximum));

        return CompareNoPoolSpecialization(
            poolDeltaRaw: left[0],
            outsidePoolDeltaRaw: right[1],
            minimumRaw: minimum,
            maximumRaw: maximum,
            thresholdRaw: threshold
        );
    }

    /// <summary>Checks every permutation of a modest contribution set and the three-term boundary discriminator.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when raw-once accumulation is order independent.</returns>
    public static string? RawSumEveryPermutation() {
        long[] contributions = [HalfRaw, (HalfRaw - 1L), -HalfRaw, (HalfRaw / 2L), -((HalfRaw / 2L) - 1L)];
        var expectedRawSum = contributions.Aggregate(seed: 0L, func: static (sum, contribution) => checked(sum + contribution));
        var expected = Evaluate(
            baselineRaw: 0L,
            poolDeltaRaw: expectedRawSum,
            outsidePoolDeltaRaw: 0L,
            poolRadiusRaw: HalfRaw,
            minimumRaw: -OneRaw,
            maximumRaw: OneRaw,
            thresholdRaw: null,
            poolClamped: out var expectedClamped
        );
        var permutations = 0;

        foreach (var permutation in Permutations(values: contributions)) {
            var sum = permutation.Aggregate(seed: 0L, func: static (total, contribution) => checked(total + contribution));
            var actual = Evaluate(
                baselineRaw: 0L,
                poolDeltaRaw: sum,
                outsidePoolDeltaRaw: 0L,
                poolRadiusRaw: HalfRaw,
                minimumRaw: -OneRaw,
                maximumRaw: OneRaw,
                thresholdRaw: null,
                poolClamped: out var clamped
            );

            ++permutations;

            if ((sum != expectedRawSum) || (actual != expected) || (clamped != expectedClamped)) {
                return $"permutation [{string.Join(separator: ',', values: permutation)}] produced sum={sum}, result={actual}, clamped={clamped}; expected sum={expectedRawSum}, result={expected}, clamped={expectedClamped}";
            }
        }

        if (permutations != 120) {
            return $"the five-element permutation battery visited {permutations} rows rather than 120";
        }

        long[] discriminator = [HalfRaw, HalfRaw, -HalfRaw];
        var rawOnce = Evaluate(
            baselineRaw: 0L,
            poolDeltaRaw: discriminator.Sum(),
            outsidePoolDeltaRaw: 0L,
            poolRadiusRaw: HalfRaw,
            minimumRaw: -OneRaw,
            maximumRaw: OneRaw,
            thresholdRaw: null,
            poolClamped: out _
        );
        var perAdd = PoolClampPerAdd(baselineRaw: 0L, contributions: discriminator, radiusRaw: HalfRaw);

        return ((rawOnce == HalfRaw) && (perAdd == 0L))
            ? null
            : $"the boundary discriminator [+0.5,+0.5,-0.5] produced raw-once={rawOnce}, per-add={perAdd}; expected {HalfRaw} and 0";
    }

    /// <summary>Checks longer contribution lists supplied by the edge/random/frontier domain in several orders.</summary>
    /// <param name="left">The first eight contribution sources.</param>
    /// <param name="right">The second eight contribution sources.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when all orders agree.</returns>
    public static string? RawSumSampledLonger(long[] left, long[] right) {
        var contributions = left.Concat(second: right).Select(selector: static raw => (raw % (OneRaw + 1L))).ToArray();
        var expectedSum = Sum(values: contributions);
        var expected = Evaluate(
            baselineRaw: 0L,
            poolDeltaRaw: expectedSum,
            outsidePoolDeltaRaw: 0L,
            poolRadiusRaw: OneRaw,
            minimumRaw: (-4L * OneRaw),
            maximumRaw: (4L * OneRaw),
            thresholdRaw: null,
            poolClamped: out var expectedClamped
        );
        long[][] orders = [
            contributions,
            [.. contributions.Reverse()],
            [.. contributions.Order()],
            [.. contributions.Skip(count: 5).Concat(second: contributions.Take(count: 5))],
        ];

        foreach (var order in orders) {
            var sum = Sum(values: order);
            var actual = Evaluate(
                baselineRaw: 0L,
                poolDeltaRaw: sum,
                outsidePoolDeltaRaw: 0L,
                poolRadiusRaw: OneRaw,
                minimumRaw: (-4L * OneRaw),
                maximumRaw: (4L * OneRaw),
                thresholdRaw: null,
                poolClamped: out var clamped
            );

            if ((sum != expectedSum) || (actual != expected) || (clamped != expectedClamped)) {
                return $"a sixteen-contribution ordering produced sum={sum}, result={actual}, clamped={clamped}; expected sum={expectedSum}, result={expected}, clamped={expectedClamped}";
            }
        }

        return null;
    }

    /// <summary>Checks the continuous pool bound for a shape-valid baseline and zero outside-pool sum.</summary>
    /// <param name="left">Baseline and pooled-delta sources.</param>
    /// <param name="right">Radius and spare domain sources.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the bound holds.</returns>
    public static string? AnalogPoolBound(long[] left, long[] right) {
        var baseline = left[0];
        var poolDelta = left[1];
        var radius = NonNegative(raw: right[0]);
        var result = Evaluate(
            baselineRaw: baseline,
            poolDeltaRaw: poolDelta,
            outsidePoolDeltaRaw: 0L,
            poolRadiusRaw: radius,
            minimumRaw: long.MinValue,
            maximumRaw: long.MaxValue,
            thresholdRaw: null,
            poolClamped: out _
        );
        var difference = BigInteger.Abs(value: ((BigInteger)result - baseline));

        return (difference <= radius)
            ? null
            : $"baseline={baseline}, poolDelta={poolDelta}, radius={radius}, result={result}, difference={difference}";
    }

    /// <summary>Checks the exact binary non-flip bound and one-raw sharpness at every legal threshold.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the bound is exact.</returns>
    public static string? BinaryFlipBoundAndSharpness() {
        for (var threshold = 1L; (threshold <= OneRaw); ++threshold) {
            var bound = Math.Min(val1: (threshold - 1L), val2: (OneRaw - threshold));

            foreach (var baseline in new long[] { 0L, OneRaw }) {
                var adverseDelta = ((baseline == 0L) ? long.MaxValue : long.MinValue);
                var held = Evaluate(
                    baselineRaw: baseline,
                    poolDeltaRaw: adverseDelta,
                    outsidePoolDeltaRaw: 0L,
                    poolRadiusRaw: bound,
                    minimumRaw: 0L,
                    maximumRaw: OneRaw,
                    thresholdRaw: threshold,
                    poolClamped: out _
                );

                if (held != baseline) {
                    return $"threshold={threshold}, baseline={baseline}, bound={bound} flipped to {held}";
                }
            }

            var sharpRadius = (bound + 1L);
            var sharpBaseline = (((threshold - 1L) <= (OneRaw - threshold)) ? 0L : OneRaw);
            var sharpDelta = ((sharpBaseline == 0L) ? long.MaxValue : long.MinValue);
            var flipped = Evaluate(
                baselineRaw: sharpBaseline,
                poolDeltaRaw: sharpDelta,
                outsidePoolDeltaRaw: 0L,
                poolRadiusRaw: sharpRadius,
                minimumRaw: 0L,
                maximumRaw: OneRaw,
                thresholdRaw: threshold,
                poolClamped: out _
            );

            if (flipped == sharpBaseline) {
                return $"threshold={threshold}, baseline={sharpBaseline}, one-above radius={sharpRadius} did not flip";
            }
        }

        return null;
    }

    /// <summary>Checks per-site preservation as an induction over a sequence of independently bounded sites.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when every prefix preserves its base bit.</returns>
    public static string? BinaryCompositionByInduction() {
        long[] deltas = [long.MaxValue, long.MinValue, OneRaw, -OneRaw, 1L, -1L, HalfRaw, -HalfRaw];

        for (var threshold = 1L; (threshold <= OneRaw); ++threshold) {
            var bound = Math.Min(val1: (threshold - 1L), val2: (OneRaw - threshold));

            foreach (var baseBit in new long[] { 0L, OneRaw }) {
                var inductionValue = baseBit;

                for (var site = 0; (site < deltas.Length); ++site) {
                    var radius = ((site % 3) == 0) ? 0L : (((site % 3) == 1) ? bound : (bound / 2L));

                    inductionValue = Evaluate(
                        baselineRaw: inductionValue,
                        poolDeltaRaw: deltas[site],
                        outsidePoolDeltaRaw: 0L,
                        poolRadiusRaw: radius,
                        minimumRaw: 0L,
                        maximumRaw: OneRaw,
                        thresholdRaw: threshold,
                        poolClamped: out _
                    );

                    if (inductionValue != baseBit) {
                        return $"threshold={threshold}, base={baseBit}, prefix through site {site}, radius={radius}, delta={deltas[site]} produced {inductionValue}";
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Checks that applying terminal quantization to its own output is idempotent.</summary>
    /// <param name="left">Range and input sources.</param>
    /// <param name="right">Threshold and spare domain sources.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when quantization is idempotent.</returns>
    public static string? TerminalQuantizationIdempotence(long[] left, long[] right) {
        var minimum = Math.Min(val1: left[1], val2: right[0]);
        var maximum = Math.Max(val1: left[1], val2: right[0]);
        var threshold = FoldIntoRange(raw: right[1], minimum: minimum, maximum: maximum);
        var first = Evaluate(
            baselineRaw: left[0],
            poolDeltaRaw: 0L,
            outsidePoolDeltaRaw: 0L,
            poolRadiusRaw: null,
            minimumRaw: minimum,
            maximumRaw: maximum,
            thresholdRaw: threshold,
            poolClamped: out _
        );
        var second = Evaluate(
            baselineRaw: first,
            poolDeltaRaw: 0L,
            outsidePoolDeltaRaw: 0L,
            poolRadiusRaw: null,
            minimumRaw: minimum,
            maximumRaw: maximum,
            thresholdRaw: threshold,
            poolClamped: out _
        );

        return (second == first)
            ? null
            : $"input={left[0]}, range=[{minimum},{maximum}], threshold={threshold}, first={first}, second={second}";
    }

    /// <summary>Checks the exact raw accumulator boundary and the widened intermediate envelope.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when both exact claims hold.</returns>
    public static string? OverflowBoundaryExact() {
        var one = (BigInteger.One << FixedQ4816.FractionBitCount);
        var maximumSafeCount = ((BigInteger.One << 47) - BigInteger.One);
        var firstOverflowingCount = (BigInteger.One << 47);
        var maximumSafeSum = (maximumSafeCount * one);
        var positiveMargin = (new BigInteger(value: long.MaxValue) - maximumSafeSum);

        if ((maximumSafeSum != ((BigInteger.One << 63) - one)) || (positiveMargin != (one - BigInteger.One))) {
            return $"maximum-safe count {maximumSafeCount} produced sum {maximumSafeSum} and margin {positiveMargin}";
        }

        if ((firstOverflowingCount * one) != (BigInteger.One << 63)) {
            return $"first-overflow count {firstOverflowingCount} did not total 2^63";
        }

        var negativeBoundarySum = (-firstOverflowingCount * one);
        var firstUnderflowingCount = (firstOverflowingCount + BigInteger.One);

        if ((negativeBoundarySum != new BigInteger(value: long.MinValue)) || ((-firstUnderflowingCount * one) >= long.MinValue)) {
            return $"negative boundary count {firstOverflowingCount} produced {negativeBoundarySum}, or count {firstUnderflowingCount} did not underflow";
        }

        var widestNegative = (-3 * (BigInteger.One << 63));
        var widestPositive = (3 * ((BigInteger.One << 63) - BigInteger.One));
        var int128Minimum = -(BigInteger.One << 127);
        var int128Maximum = ((BigInteger.One << 127) - BigInteger.One);

        if ((widestNegative < int128Minimum) || (widestPositive > int128Maximum)) {
            return $"the three-term public envelope [{widestNegative},{widestPositive}] leaves Int128";
        }

        (long Baseline, long Pool, long Outside, long? Radius)[] extremes = [
            (long.MaxValue, long.MaxValue, long.MaxValue, null),
            (long.MinValue, long.MinValue, long.MinValue, null),
            (long.MinValue, long.MaxValue, long.MinValue, long.MaxValue),
            (long.MaxValue, long.MinValue, long.MaxValue, 0L),
        ];

        foreach (var (baseline, pool, outside, radius) in extremes) {
            if (CompareWithOracle(
                baselineRaw: baseline,
                poolDeltaRaw: pool,
                outsidePoolDeltaRaw: outside,
                poolRadiusRaw: radius,
                minimumRaw: long.MinValue,
                maximumRaw: long.MaxValue,
                thresholdRaw: null
            ) is { } failure) {
                return $"extreme {failure}";
            }
        }

        return null;
    }

    /// <summary>Checks all three named configuration refusals and the legal zero-radius boundary.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when every refusal names its parameter.</returns>
    public static string? ConfigurationRefusals() {
        if (Refusal<ArgumentException>(
            action: static () => FixedContributionFold.Evaluate(
                baseline: FixedQ4816.Zero,
                poolDeltaRaw: 0L,
                outsidePoolDeltaRaw: 0L,
                poolRadius: null,
                minimum: FixedQ4816.One,
                maximum: FixedQ4816.Zero,
                threshold: null,
                poolClamped: out _
            ),
            parameterName: "minimum"
        ) is { } invertedFailure) {
            return invertedFailure;
        }

        if (Refusal<ArgumentOutOfRangeException>(
            action: static () => FixedContributionFold.Evaluate(
                baseline: FixedQ4816.Zero,
                poolDeltaRaw: 0L,
                outsidePoolDeltaRaw: 0L,
                poolRadius: FixedQ4816.FromRawBits(value: -1L),
                minimum: FixedQ4816.Zero,
                maximum: FixedQ4816.One,
                threshold: null,
                poolClamped: out _
            ),
            parameterName: "poolRadius"
        ) is { } radiusFailure) {
            return radiusFailure;
        }

        foreach (var threshold in new[] { FixedQ4816.NegativeOne, FixedQ4816.One + FixedQ4816.Epsilon }) {
            if (Refusal<ArgumentOutOfRangeException>(
                action: () => FixedContributionFold.Evaluate(
                    baseline: FixedQ4816.Zero,
                    poolDeltaRaw: 0L,
                    outsidePoolDeltaRaw: 0L,
                    poolRadius: null,
                    minimum: FixedQ4816.Zero,
                    maximum: FixedQ4816.One,
                    threshold: threshold,
                    poolClamped: out _
                ),
                parameterName: "threshold"
            ) is { } thresholdFailure) {
                return thresholdFailure;
            }
        }

        var zeroRadius = FixedContributionFold.Evaluate(
            baseline: FixedQ4816.FromRawBits(value: 7L),
            poolDeltaRaw: 1L,
            outsidePoolDeltaRaw: 0L,
            poolRadius: FixedQ4816.Zero,
            minimum: FixedQ4816.MinValue,
            maximum: FixedQ4816.MaxValue,
            threshold: null,
            poolClamped: out var zeroRadiusClamped
        );

        return ((zeroRadius.Value == 7L) && zeroRadiusClamped)
            ? null
            : $"zero radius was not admitted as a clamping pool: result={zeroRadius.Value}, clamped={zeroRadiusClamped}";
    }

    /// <summary>Runs the three hand-derived cases used to compare this primitive with the pre-existing raw-once rule.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when all three land on their derived raws.</returns>
    public static string? DiscriminatingExamples() {
        long[] cancelAcrossBoundary = [HalfRaw, HalfRaw, -HalfRaw];
        var rawOnce = Evaluate(
            baselineRaw: 0L,
            poolDeltaRaw: cancelAcrossBoundary.Sum(),
            outsidePoolDeltaRaw: 0L,
            poolRadiusRaw: HalfRaw,
            minimumRaw: -OneRaw,
            maximumRaw: OneRaw,
            thresholdRaw: null,
            poolClamped: out _
        );
        var currentRawOnce = CurrentRawOnceRule(
            baselineRaw: 0L,
            poolDeltaRaw: cancelAcrossBoundary.Sum(),
            outsidePoolDeltaRaw: 0L,
            poolRadiusRaw: HalfRaw,
            minimumRaw: -OneRaw,
            maximumRaw: OneRaw,
            thresholdRaw: null
        );
        var perAdd = PoolClampPerAdd(baselineRaw: 0L, contributions: cancelAcrossBoundary, radiusRaw: HalfRaw);
        var negative = Evaluate(
            baselineRaw: (OneRaw / 4L),
            poolDeltaRaw: -HalfRaw,
            outsidePoolDeltaRaw: -(OneRaw / 4L),
            poolRadiusRaw: ((3L * OneRaw) / 4L),
            minimumRaw: -OneRaw,
            maximumRaw: OneRaw,
            thresholdRaw: null,
            poolClamped: out _
        );
        var currentNegative = CurrentRawOnceRule(
            baselineRaw: (OneRaw / 4L),
            poolDeltaRaw: -HalfRaw,
            outsidePoolDeltaRaw: -(OneRaw / 4L),
            poolRadiusRaw: ((3L * OneRaw) / 4L),
            minimumRaw: -OneRaw,
            maximumRaw: OneRaw,
            thresholdRaw: null
        );
        var atThreshold = Evaluate(
            baselineRaw: 0L,
            poolDeltaRaw: (OneRaw / 4L),
            outsidePoolDeltaRaw: (OneRaw / 4L),
            poolRadiusRaw: HalfRaw,
            minimumRaw: 0L,
            maximumRaw: OneRaw,
            thresholdRaw: HalfRaw,
            poolClamped: out _
        );
        var currentAtThreshold = CurrentRawOnceRule(
            baselineRaw: 0L,
            poolDeltaRaw: (OneRaw / 4L),
            outsidePoolDeltaRaw: (OneRaw / 4L),
            poolRadiusRaw: HalfRaw,
            minimumRaw: 0L,
            maximumRaw: OneRaw,
            thresholdRaw: HalfRaw
        );
        var strictGreaterAlternative = StrictGreaterQuantize(raw: HalfRaw, thresholdRaw: HalfRaw, minimumRaw: 0L, maximumRaw: OneRaw);

        return (
            (rawOnce == HalfRaw) &&
            (currentRawOnce == HalfRaw) &&
            (perAdd == 0L) &&
            (negative == -HalfRaw) &&
            (currentNegative == -HalfRaw) &&
            (atThreshold == OneRaw) &&
            (currentAtThreshold == OneRaw) &&
            (strictGreaterAlternative == 0L)
        )
            ? null
            : $"derived raws were cancel/primitive={rawOnce}, cancel/current={currentRawOnce}, cancel/per-add={perAdd}, negative/primitive={negative}, negative/current={currentNegative}, at-threshold/primitive={atThreshold}, at-threshold/current={currentAtThreshold}, at-threshold/strict-greater={strictGreaterAlternative}";
    }

    /// <summary>Records a concrete counterexample to distributing terminal folding over site composition.</summary>
    /// <returns>The counterexample text when the known-false equality unexpectedly holds; otherwise <see langword="null"/>.</returns>
    public static string? SiteCompositionDoesNotDistribute() {
        var a = (OneRaw + HalfRaw);
        var b = -HalfRaw;
        var firstSite = Evaluate(
            baselineRaw: a,
            poolDeltaRaw: 0L,
            outsidePoolDeltaRaw: 0L,
            poolRadiusRaw: null,
            minimumRaw: 0L,
            maximumRaw: OneRaw,
            thresholdRaw: null,
            poolClamped: out _
        );
        var left = Evaluate(
            baselineRaw: firstSite,
            poolDeltaRaw: b,
            outsidePoolDeltaRaw: 0L,
            poolRadiusRaw: null,
            minimumRaw: 0L,
            maximumRaw: OneRaw,
            thresholdRaw: null,
            poolClamped: out _
        );
        var right = Evaluate(
            baselineRaw: a,
            poolDeltaRaw: b,
            outsidePoolDeltaRaw: 0L,
            poolRadiusRaw: null,
            minimumRaw: 0L,
            maximumRaw: OneRaw,
            thresholdRaw: null,
            poolClamped: out _
        );

        return (left != right)
            ? null
            : $"known-false equality unexpectedly held at a={a}, b={b}: Q(Q(a)+b)={left}, Q(a+b)={right}";
    }

    private static string? CompareWithOracle(
        long baselineRaw,
        long poolDeltaRaw,
        long outsidePoolDeltaRaw,
        long? poolRadiusRaw,
        long minimumRaw,
        long maximumRaw,
        long? thresholdRaw
    ) {
        var actual = Evaluate(
            baselineRaw: baselineRaw,
            poolDeltaRaw: poolDeltaRaw,
            outsidePoolDeltaRaw: outsidePoolDeltaRaw,
            poolRadiusRaw: poolRadiusRaw,
            minimumRaw: minimumRaw,
            maximumRaw: maximumRaw,
            thresholdRaw: thresholdRaw,
            poolClamped: out var actualClamped
        );
        var expected = Oracles.FixedContributionFold(
            baselineRaw: baselineRaw,
            poolDeltaRaw: poolDeltaRaw,
            outsidePoolDeltaRaw: outsidePoolDeltaRaw,
            poolRadiusRaw: poolRadiusRaw,
            minimumRaw: minimumRaw,
            maximumRaw: maximumRaw,
            thresholdRaw: thresholdRaw
        );

        return ((actual == expected.ResultRaw) && (actualClamped == expected.PoolClamped))
            ? null
            : $"baseline={baselineRaw}, pool={poolDeltaRaw}, outside={outsidePoolDeltaRaw}, radius={Render(nullable: poolRadiusRaw)}, range=[{minimumRaw},{maximumRaw}], threshold={Render(nullable: thresholdRaw)} produced ({actual},{actualClamped}), oracle ({expected.ResultRaw},{expected.PoolClamped})";
    }

    private static string? CompareNoPoolSpecialization(long poolDeltaRaw, long outsidePoolDeltaRaw, long minimumRaw, long maximumRaw, long? thresholdRaw) {
        var actual = Evaluate(
            baselineRaw: 0L,
            poolDeltaRaw: poolDeltaRaw,
            outsidePoolDeltaRaw: outsidePoolDeltaRaw,
            poolRadiusRaw: null,
            minimumRaw: minimumRaw,
            maximumRaw: maximumRaw,
            thresholdRaw: thresholdRaw,
            poolClamped: out var clamped
        );
        var expected = Oracles.FixedContributionFoldNoPool(
            poolDeltaRaw: poolDeltaRaw,
            outsidePoolDeltaRaw: outsidePoolDeltaRaw,
            minimumRaw: minimumRaw,
            maximumRaw: maximumRaw,
            thresholdRaw: thresholdRaw
        );

        return ((actual == expected) && !clamped)
            ? null
            : $"no-pool pool={poolDeltaRaw}, outside={outsidePoolDeltaRaw}, range=[{minimumRaw},{maximumRaw}], threshold={Render(nullable: thresholdRaw)} produced ({actual},{clamped}), direct oracle ({expected},false)";
    }

    private static long Evaluate(
        long baselineRaw,
        long poolDeltaRaw,
        long outsidePoolDeltaRaw,
        long? poolRadiusRaw,
        long minimumRaw,
        long maximumRaw,
        long? thresholdRaw,
        out bool poolClamped
    ) =>
        FixedContributionFold.Evaluate(
            baseline: FixedQ4816.FromRawBits(value: baselineRaw),
            poolDeltaRaw: poolDeltaRaw,
            outsidePoolDeltaRaw: outsidePoolDeltaRaw,
            poolRadius: (poolRadiusRaw is { } radius ? FixedQ4816.FromRawBits(value: radius) : null),
            minimum: FixedQ4816.FromRawBits(value: minimumRaw),
            maximum: FixedQ4816.FromRawBits(value: maximumRaw),
            threshold: (thresholdRaw is { } threshold ? FixedQ4816.FromRawBits(value: threshold) : null),
            poolClamped: out poolClamped
        ).Value;

    private static long FoldIntoRange(long raw, long minimum, long maximum) {
        var width = ((BigInteger)maximum - minimum + BigInteger.One);
        var residue = (new BigInteger(value: raw) % width);

        if (residue.Sign < 0) { residue += width; }

        return ((long)(minimum + residue));
    }

    private static long NonNegative(long raw) =>
        ((long)((ulong)raw & long.MaxValue));

    private static long Sum(ReadOnlySpan<long> values) {
        var sum = 0L;

        foreach (var value in values) { sum = checked(sum + value); }

        return sum;
    }

    private static IEnumerable<long[]> Permutations(long[] values) {
        var working = values.ToArray();

        return Enumerate(index: 0);

        IEnumerable<long[]> Enumerate(int index) {
            if (index == working.Length) {
                yield return working.ToArray();

                yield break;
            }

            for (var swap = index; (swap < working.Length); ++swap) {
                (working[index], working[swap]) = (working[swap], working[index]);

                foreach (var permutation in Enumerate(index: (index + 1))) { yield return permutation; }

                (working[index], working[swap]) = (working[swap], working[index]);
            }
        }
    }

    private static long PoolClampPerAdd(long baselineRaw, ReadOnlySpan<long> contributions, long radiusRaw) {
        var minimum = (baselineRaw - radiusRaw);
        var maximum = (baselineRaw + radiusRaw);
        var accumulator = baselineRaw;

        foreach (var contribution in contributions) {
            accumulator = Math.Clamp(value: (accumulator + contribution), min: minimum, max: maximum);
        }

        return accumulator;
    }

    // The pre-existing fold's arithmetic only, expanded locally for the three compatibility controls above. All three
    // are deliberately far from long overflow, so its original narrow additions and the new widened ones denote the
    // same integers; this is a compatibility witness, not an oracle (the BigInteger laws carry the absolute evidence).
    private static long CurrentRawOnceRule(
        long baselineRaw,
        long poolDeltaRaw,
        long outsidePoolDeltaRaw,
        long poolRadiusRaw,
        long minimumRaw,
        long maximumRaw,
        long? thresholdRaw
    ) {
        var rawPooled = (baselineRaw + poolDeltaRaw);
        var pooled = Math.Clamp(
            value: rawPooled,
            min: (baselineRaw - poolRadiusRaw),
            max: (baselineRaw + poolRadiusRaw)
        );
        var ranged = Math.Clamp(value: (pooled + outsidePoolDeltaRaw), min: minimumRaw, max: maximumRaw);

        return thresholdRaw is { } threshold
            ? ((ranged >= threshold) ? maximumRaw : minimumRaw)
            : ranged;
    }

    private static long StrictGreaterQuantize(long raw, long thresholdRaw, long minimumRaw, long maximumRaw) =>
        ((raw > thresholdRaw) ? maximumRaw : minimumRaw);

    private static string? Refusal<TException>(Action action, string parameterName) where TException : ArgumentException {
        try {
            action();

            return $"accepted invalid configuration instead of throwing {typeof(TException).Name} naming '{parameterName}'";
        } catch (TException exception) {
            return (exception.ParamName == parameterName)
                ? null
                : $"threw {typeof(TException).Name} naming '{exception.ParamName}' rather than '{parameterName}'";
        } catch (Exception exception) {
            return $"threw {exception.GetType().Name} rather than {typeof(TException).Name} naming '{parameterName}'";
        }
    }

    private static string Render(long? nullable) =>
        (nullable?.ToString() ?? "null");
}
