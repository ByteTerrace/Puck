using System.Numerics;
using Xunit;

namespace Puck.Maths.Tests;

/// <summary>
/// Claims over <see cref="Pcg32XshRr"/> against the published reference generator, the gaussian/alias sampling
/// surface, <c>FieldNoise</c>, and <see cref="CertifiedLowDiscrepancy"/> — each one covering evidence the standing
/// <c>sampling.*</c> Default-tier cases in <see cref="LawRegistry"/> explicitly admit as a gap, via an
/// <c>ENVELOPE</c> or an <c>OWED</c> citation. What those cases already pin is not restated here: Log2/Exp2/Pow's
/// accuracy belongs to the <c>scalar.*</c> family, and the PCG published vector, the snapshot/advance/adapter
/// contracts, the alias table's refusal ladder and index bounds, R1/R2's recurrence and coverage, and the
/// certificate's agreement with its continued fraction all have standing cases. Every claim below runs at Tier.Deep.
/// </summary>
internal static class SamplingClaims {
    // ---- Pcg32XshRr against the reference implementation ----

    /// <summary>A verbatim transcription of the third-party PCG32 XSH-RR reference generator
    /// (<c>pcg32_random_r</c> / <c>pcg32_srandom_r</c>), shared-nothing with <see cref="Pcg32XshRr"/>: it recomputes
    /// the 64-bit LCG state advance and the xorshift-rotate output step from the published algorithm directly, and
    /// calls no Puck.Maths member.</summary>
    private struct PcgRef {
        public ulong State;
        public ulong Inc;

        public uint Next() {
            var old = State;

            State = unchecked((old * 6364136223846793005UL) + Inc);

            var xorshifted = unchecked((uint)(((old >> 18) ^ old) >> 27));
            var rotation = ((int)(old >> 59));

            return ((xorshifted >> rotation) | (xorshifted << ((-rotation) & 31)));
        }
    }

    public static string? PcgTranscribedReferenceAndDecorrelationSurface() {
        // Sixteen hand-chosen raw state/increment pairs spanning small, large and adversarial values (all-ones,
        // near-zero, a repeating byte pattern) rather than drawn from any generator, so this claim consumes no
        // domain and needs no seed of its own.
        (ulong State, ulong Increment)[] pairs = [
            (12345UL, 91UL), (0UL, 1UL), (ulong.MaxValue, 3UL), (777UL, 12345UL),
            (999999999UL, 7UL), (1UL, ulong.MaxValue), (42UL, 54UL), (2UL, 999UL),
            (0xDEADBEEFUL, 0xCAFEBABEUL), (0xFFFF0000FFFF0000UL, 5UL), (1000000007UL, 13UL),
            (8UL, 8UL), (55UL, 6UL), (123456789UL, 3UL), (2026UL, 13UL), (7UL, 7UL),
        ];

        foreach (var (state, increment) in pairs) {
            var oddIncrement = (increment | 1UL);
            var reference = new PcgRef { Inc = oddIncrement, State = state };
            var subject = Pcg32XshRr.FromRawBits(increment: oddIncrement, multiplier: Pcg32XshRr.DefaultMultiplier, state: state);

            for (var draw = 0; (draw < 50_000); ++draw) {
                var expected = reference.Next();
                var actual = subject.NextUInt32();

                if (expected != actual) {
                    return $"transcribed reference diverged at state={state} increment={oddIncrement} draw={draw}: reference=0x{expected:x8} subject=0x{actual:x8}";
                }
            }
        }

        // Stream decorrelation tripwire over five stream-id pairs: distinct streams seeded from the same state must
        // not walk in lockstep. Not a distribution claim — a sentinel against two stream
        // ids silently colliding.
        foreach (var (firstStreamId, secondStreamId) in new (ulong, ulong)[] { (0UL, 1UL), (1UL, 2UL), (7UL, 8UL), (100UL, 101UL), (0UL, 2UL) }) {
            var firstStream = Pcg32XshRr.Create(state: 99UL, stream: firstStreamId);
            var secondStream = Pcg32XshRr.Create(state: 99UL, stream: secondStreamId);
            var identicalDraws = 0;

            for (var draw = 0; (draw < 1000); ++draw) {
                if (firstStream.NextUInt32() == secondStream.NextUInt32()) { ++identicalDraws; }
            }

            if (identicalDraws > 10) {
                return $"streams ({firstStreamId},{secondStreamId}) gave {identicalDraws}/1000 identical draws, over the 10-draw tripwire";
            }
        }

        return null;
    }

    // ---- gaussian moments and the alias table (Log2/Exp2/Pow accuracy is the scalar.* family's) ----

    public static string? GaussianMomentsCdfTailSurface() {
        var generator = Pcg32XshRr.Create(state: 2026UL, stream: 13UL);
        // Two million pairs (four million samples), down from twenty million: every threshold below is unchanged
        // from what the full volume asserts, and is met here with wide empirical margin rather than loosened to fit.
        const int PairCount = 2_000_000;
        var mean = 0.0;
        var secondMoment = 0.0;
        var thirdMoment = 0.0;
        var fourthMoment = 0.0;
        var beyondThreeSigma = 0L;
        double[] binEdges = [0.5, 1.0, 1.5, 2.0, 2.5, 3.0];
        var binCounts = new long[binEdges.Length];

        for (var pair = 0; (pair < PairCount); ++pair) {
            var (first, second) = generator.NextGaussianPair();

            foreach (var sample in new[] { ((double)first), ((double)second) }) {
                mean += sample;
                secondMoment += (sample * sample);
                thirdMoment += ((sample * sample) * sample);
                fourthMoment += (((sample * sample) * sample) * sample);

                var magnitude = Math.Abs(sample);

                if (magnitude > 3.0) { ++beyondThreeSigma; }

                for (var bin = 0; (bin < binEdges.Length); ++bin) {
                    if (magnitude <= binEdges[bin]) { ++binCounts[bin]; }
                }
            }
        }

        var total = (2.0 * PairCount);

        mean /= total;
        secondMoment /= total;
        thirdMoment /= total;
        fourthMoment /= total;

        var variance = (secondMoment - (mean * mean));
        var kurtosis = (fourthMoment / (variance * variance));

        if (Math.Abs(mean) > 1e-3) { return $"mean {mean:E4} exceeds 1e-3"; }
        if (Math.Abs(variance - 1.0) > 3e-3) { return $"variance {variance:F6} departs from 1 by more than 3e-3"; }
        if (Math.Abs(thirdMoment) > 5e-3) { return $"third moment (skew) {thirdMoment:E4} exceeds 5e-3"; }
        if (Math.Abs(kurtosis - 3.0) > 2e-2) { return $"kurtosis {kurtosis:F5} departs from 3 by more than 2e-2"; }

        double[] twoSidedPhi = [0.3829249, 0.6826895, 0.8663856, 0.9544997, 0.9875807, 0.9973002];

        for (var bin = 0; (bin < binEdges.Length); ++bin) {
            var empirical = (binCounts[bin] / total);

            if (Math.Abs(empirical - twoSidedPhi[bin]) > 1.5e-3) {
                return $"CDF bin |z|<={binEdges[bin]} empirical {empirical:F6} vs target {twoSidedPhi[bin]:F6}";
            }
        }

        var tail = (beyondThreeSigma / total);
        // The tail reference is no longer a bare published constant: Oracles.EncloseGaussianTailBeyondThreeSigma
        // derives an exact BigInteger enclosure of P(|Z|>3) from Gordon's classical inequality, e^4.5's own Taylor
        // series and Oracles.Pi's Machin-derived π. Its ~10% relative width (a classical, not razor-tight, bound)
        // widens the acceptance band beyond the old ±3e-4-around-a-magic-number, which is an honest, stated
        // tradeoff for replacing an unprovenanced constant with a derived one — not a loosened fit, since the SAME
        // ±3e-4 statistical margin still applies on top of the enclosure, unchanged from before.
        var tailEnclosure = Oracles.EncloseGaussianTailBeyondThreeSigma(guardBitCount: Oracles.GuardBitCount);
        var enclosureScale = ((double)(BigInteger.One << (16 + Oracles.GuardBitCount)));
        var tailLow = ((double)tailEnclosure.Low / enclosureScale);
        var tailHigh = ((double)tailEnclosure.High / enclosureScale);
        const double StatisticalMargin = 3e-4;

        if ((tail < (tailLow - StatisticalMargin)) || (tail > (tailHigh + StatisticalMargin))) {
            return $"tail P(|z|>3) {tail:E5} is outside the enclosure-derived band [{(tailLow - StatisticalMargin):E5}, {(tailHigh + StatisticalMargin):E5}] (Gordon bounds [{tailLow:E5}, {tailHigh:E5}])";
        }

        return null;
    }

    public static string? ShuffleUniformitySurface() {
        var generator = Pcg32XshRr.Create(state: 777UL, stream: 30UL);
        var counts = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        // 240,000 trials, down from 480,000; the tolerance below is scaled with margin rather than merely shrunk in
        // proportion.
        const int Trials = 240_000;

        for (var trial = 0; (trial < Trials); ++trial) {
            Span<int> deck = [0, 1, 2, 3];

            generator.Shuffle(values: deck);

            var key = $"{deck[0]}{deck[1]}{deck[2]}{deck[3]}";

            counts[key] = (counts.GetValueOrDefault(key) + 1);
        }

        if (counts.Count != 24) {
            return $"produced {counts.Count} distinct permutations of four elements, expected 24";
        }

        const int Expected = (Trials / 24);

        foreach (var (permutation, count) in counts) {
            if (Math.Abs(count - Expected) > 500) {
                return $"permutation {permutation} occurred {count} times, expected {Expected} +/- 500";
            }
        }

        return null;
    }

    public static string? AliasTableFrequencyDistributionSurface() {
        (string Label, ulong[] Weights, int Draws, ulong Seed)[] cases = [
            ("small-integer-ratios", [1UL, 2UL, 7UL], 2_000_000, 101UL),
            ("uniform-four-way", [5UL, 5UL, 5UL, 5UL], 2_000_000, 102UL),
            ("extreme-ratio", [1UL, (1UL << 20)], 2_000_000, 105UL),
        ];

        foreach (var (label, weights, draws, seed) in cases) {
            var entries = new (int Element, ulong Weight)[weights.Length];

            for (var i = 0; (i < weights.Length); ++i) { entries[i] = (i, weights[i]); }

            var table = WeightedSampler.Create<int>(entries: entries);
            var generator = Pcg32XshRr.Create(state: seed, stream: 1UL);
            var counts = new long[weights.Length];

            for (var draw = 0; (draw < draws); ++draw) { ++counts[table.SampleIndex(generator: ref generator)]; }

            var total = BigInteger.Zero;

            foreach (var weight in weights) { total += weight; }

            for (var i = 0; (i < weights.Length); ++i) {
                // |count/draws - weight/total| <= 3/1000, cross-multiplied so the comparison is exact BigInteger
                // arithmetic rather than a floating-point ratio.
                var lhs = (BigInteger.Abs((counts[i] * total) - ((BigInteger)draws * weights[i])) * 1000);
                var rhs = ((3 * (BigInteger)draws) * total);

                if (lhs > rhs) {
                    return $"{label}[{i}] weight={weights[i]} count={counts[i]} of {draws} departs from weight/total by more than 3/1000";
                }
            }
        }

        return null;
    }

    // ---- field noise (R1/R2's recurrence and coverage are sampling.low-discrepancy-recurrence's) ----

    public static string? FieldNoisePeriodicityAndDistributionSurface() {
        // Two former linear-hash aliasing regressions, restated as relative canaries with no absolute sibling:
        // FieldNoise carries no independent value-level oracle anywhere in this tree (worklist R4). The honest
        // evidence is that translating the position or
        // the seed by the amount the OLD linear hash would have folded away must still move the sample.
        const long NoisePeriodX = 852_863L;
        const long NoisePeriodY = 1_285_698L;
        const long NoisePeriodZ = 183_727L;
        const ulong FormerSeedCombineConstant = 0x9E3779B97F4A7C15UL;

        for (var probe = 0; (probe < 32); ++probe) {
            var position = new FixedVector3(
                X: FixedQ4816.FromRawBits(value: ((probe * 65_537L) + 12_345L)),
                Y: FixedQ4816.FromRawBits(value: ((probe * -31_337L) + 22_222L)),
                Z: FixedQ4816.FromRawBits(value: ((probe * 9_973L) - 33_333L))
            );
            var periodShifted = new FixedVector3(
                X: FixedQ4816.FromRawBits(value: (position.X.Value + (NoisePeriodX << FixedQ4816.FractionBitCount))),
                Y: FixedQ4816.FromRawBits(value: (position.Y.Value + (NoisePeriodY << FixedQ4816.FractionBitCount))),
                Z: FixedQ4816.FromRawBits(value: (position.Z.Value + (NoisePeriodZ << FixedQ4816.FractionBitCount)))
            );
            var unitTranslated = (position + new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));
            var probeSeed = unchecked((ulong)(97 + probe));

            if (FieldNoise.Sample(seed: probeSeed, position: position) == FieldNoise.Sample(seed: probeSeed, position: periodShifted)) {
                return $"probe {probe}: the sample still aliases at the former hash period, which must no longer alias";
            }
            if (FieldNoise.Sample(seed: unchecked(probeSeed + FormerSeedCombineConstant), position: position) == FieldNoise.Sample(seed: probeSeed, position: unitTranslated)) {
                return $"probe {probe}: seeding by the former combine constant still matches a unit translation, which must no longer hold";
            }
        }

        // The octave-wrap seam at raw 2^62: a five-octave sample must not jump by more than the interior lattice
        // continuity bound across the wrap boundary.
        var seamRaw = (1L << 62);
        var seamLeft = FieldNoise.Sample(seed: 42UL, position: new FixedVector3(X: FixedQ4816.FromRawBits(value: (seamRaw - 1L)), Y: FixedQ4816.FromRawBits(value: 17_123L), Z: FixedQ4816.FromRawBits(value: -9_321L)), octaves: 5);
        var seamRight = FieldNoise.Sample(seed: 42UL, position: new FixedVector3(X: FixedQ4816.FromRawBits(value: seamRaw), Y: FixedQ4816.FromRawBits(value: 17_123L), Z: FixedQ4816.FromRawBits(value: -9_321L)), octaves: 5);
        var seamStep = Math.Abs(seamRight.Value - seamLeft.Value);

        if (seamStep > 16L) {
            return $"octave-wrap seam step {seamStep} raw exceeds the 16-raw continuity bound";
        }

        // Distribution: positions drawn from a seeded generator over the full signed-raw span must average near
        // zero with a standard deviation the noise shape implies. 500,000 positions, down from ten million; the
        // thresholds are unchanged from what the full volume asserts and are met here with wide empirical margin.
        var generator = Pcg32XshRr.Create(state: 4242UL, stream: 9UL);
        const int SampleCount = 500_000;
        var sum = 0.0;
        var sumOfSquares = 0.0;

        for (var sample = 0; (sample < SampleCount); ++sample) {
            var position = new FixedVector3(
                X: FixedQ4816.FromRawBits(value: NextSignedRaw(generator: ref generator)),
                Y: FixedQ4816.FromRawBits(value: NextSignedRaw(generator: ref generator)),
                Z: FixedQ4816.FromRawBits(value: NextSignedRaw(generator: ref generator))
            );
            var value = ((double)FieldNoise.Sample(seed: 42UL, position: position).Value);

            sum += value;
            sumOfSquares += (value * value);
        }

        var mean = ((sum / SampleCount) / 65536.0);
        var standardDeviation = (Math.Sqrt(((sumOfSquares / SampleCount) - Math.Pow((sum / SampleCount), 2))) / 65536.0);

        if (Math.Abs(mean) > 1e-3) { return $"noise mean {mean:E4} exceeds 1e-3"; }
        if ((standardDeviation < 0.15) || (standardDeviation > 0.45)) { return $"noise standard deviation {standardDeviation:F5} outside [0.15, 0.45]"; }

        return null;
    }

    private static long NextSignedRaw(ref Pcg32XshRr generator) {
        var high = generator.NextUInt32();
        var low = generator.NextUInt32();
        var wide = unchecked((long)(((ulong)high << 32) | low));

        return ((wide % (1L << 41)) - (1L << 40));
    }

    // ---- CertifiedLowDiscrepancy: badly-approximable equidistribution, certified by the continued fraction ----

    public static string? CertifiedLowDiscrepancyBoundTeethAndGapSurface() {
        // Three certificates spanning the range this family names — golden (K=1, the Hurwitz optimum), silver
        // (K=2) and sqrt(2501) (K=100) — whose certificates are already independently pinned against
        // Oracles.MaximumPartialQuotient by certified.certificate-vs-partial-quotients; this claim trusts that
        // pinning and adds the DiscrepancyBound and Point evidence that case does not reach.
        (string Name, long P, long Q, long D, long R, long Certificate)[] cases = [
            ("golden", 1L, 1L, 5L, 2L, 1L),        // (1 + sqrt 5) / 2 = [1; (1)]
            ("silver", 1L, 1L, 2L, 1L, 2L),        // 1 + sqrt 2 = [2; (2)]
            ("sqrt2501", 0L, 1L, 2501L, 1L, 100L), // sqrt 2501 = [50; (100)]
        ];

        foreach (var certifiedCase in cases) {
            var sequence = CertifiedLowDiscrepancy.FromQuadraticIrrational(p: certifiedCase.P, q: certifiedCase.Q, d: certifiedCase.D, r: certifiedCase.R);

            Assert.Equal(expected: certifiedCase.Certificate, actual: sequence.Certificate);

            foreach (var pointCount in new[] { 64, 4096, 16384 }) {
                var (numerator, denominator) = ExactStarDiscrepancy(sequence: sequence, pointCount: pointCount);
                var bound = sequence.DiscrepancyBound(pointCount: pointCount);
                var lhs = (numerator * 65536);
                var rhs = ((BigInteger)bound.Value * denominator);

                if (lhs > rhs) {
                    return $"{certifiedCase.Name} measured star discrepancy {numerator}/{denominator} exceeds the certified bound {bound} at N={pointCount}";
                }
            }
        }

        // Teeth: the K=100 certificate measures markedly worse than K=1's at every scale, and three certificates
        // 1 < 2 < 100 measure strictly monotone at every one of those scales.
        var golden = CertifiedLowDiscrepancy.FromQuadraticIrrational(p: 1L, q: 1L, d: 5L, r: 2L);
        var silver = CertifiedLowDiscrepancy.FromQuadraticIrrational(p: 1L, q: 1L, d: 2L, r: 1L);
        var badK = CertifiedLowDiscrepancy.FromQuadraticIrrational(p: 0L, q: 1L, d: 2501L, r: 1L);

        foreach (var pointCount in new[] { 1024, 4096, 16384 }) {
            var (goldenNumerator, goldenDenominator) = ExactStarDiscrepancy(sequence: golden, pointCount: pointCount);
            var (silverNumerator, _) = ExactStarDiscrepancy(sequence: silver, pointCount: pointCount);
            var (badKNumerator, _) = ExactStarDiscrepancy(sequence: badK, pointCount: pointCount);

            // Every side shares the SAME pointCount, and therefore the same denominator, so the comparison is on the
            // numerators alone.
            if (badKNumerator <= (2 * goldenNumerator)) {
                return $"K=100 discrepancy {badKNumerator}/{goldenDenominator} is not markedly worse than K=1's {goldenNumerator}/{goldenDenominator} at N={pointCount}";
            }
            if (!((goldenNumerator < silverNumerator) && (silverNumerator < badKNumerator))) {
                return $"measured discrepancy is not monotone in K at N={pointCount}: golden={goldenNumerator} silver={silverNumerator} badK={badKNumerator}";
            }
        }

        // No sequence leaves an empty circular gap wider than 1/20 of the unit interval over its first 4096 points,
        // exactly: gap * 20 <= 2^32 on the raw UQ0.32 values.
        foreach (var (name, sequence) in new (string, CertifiedLowDiscrepancy)[] { ("golden", golden), ("silver", silver), ("badK", badK) }) {
            var points = new uint[4096];

            for (var i = 0; (i < points.Length); ++i) { points[i] = sequence.Point(index: (ulong)(i + 1)).Value; }

            Array.Sort(points);

            var gap = ((((ulong)points[0]) + (uint.MaxValue - points[^1])) + 1UL);

            for (var i = 1; (i < points.Length); ++i) {
                var step = ((ulong)(points[i] - points[i - 1]));

                if (step > gap) { gap = step; }
            }

            if ((gap * 20UL) > (1UL << 32)) {
                return $"{name} leaves an empty gap of {gap} raw (> 1/20 of the unit interval) over its first 4096 points";
            }
        }

        // Determinism: two independently constructed instances of the same generator agree bit for bit.
        var goldenAgain = CertifiedLowDiscrepancy.FromQuadraticIrrational(p: 1L, q: 1L, d: 5L, r: 2L);

        for (var i = 0UL; (i < 4096UL); ++i) {
            Assert.Equal(expected: golden.Point(index: i), actual: goldenAgain.Point(index: i));
        }

        // The golden sequence's point one is EXACTLY the published Fibonacci-hashing constant 0x9E3779B9 — the top
        // 32 bits of round(2^64 / phi) — the same constant LowDiscrepancy.R1(1) is pinned to exactly in
        // sampling.low-discrepancy-recurrence.
        Assert.Equal(expected: 0x9E3779B9U, actual: golden.Point(index: 1UL).Value);

        // Refusals: the metallic index and the point count must both be positive.
        Assert.Equal(
            expected: "n",
            actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => CertifiedLowDiscrepancy.MetallicMean(n: 0)).ParamName
        );
        Assert.Equal(
            expected: "pointCount",
            actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => golden.DiscrepancyBound(pointCount: 0L)).ParamName
        );

        return null;
    }

    /// <summary>The exact star discrepancy of <paramref name="sequence"/>'s first <paramref name="pointCount"/>
    /// points, in <see cref="UnitFraction32"/>'s own raw scale: the returned numerator over the returned denominator
    /// (<paramref name="pointCount"/> * 2^32) is <c>max_i max((i+1)/N - x_i, x_i - i/N)</c> with no rounding
    /// anywhere — BigInteger cross-multiplication in place of a <see langword="double"/> sort-and-scan, so no float
    /// enters this oracle.</summary>
    private static (BigInteger Numerator, BigInteger Denominator) ExactStarDiscrepancy(CertifiedLowDiscrepancy sequence, int pointCount) {
        var points = new uint[pointCount];

        for (var i = 0; (i < pointCount); ++i) { points[i] = sequence.Point(index: (ulong)(i + 1)).Value; }

        Array.Sort(points);

        var denominator = (((BigInteger)pointCount) << 32);
        var numerator = BigInteger.Zero;

        for (var i = 0; (i < pointCount); ++i) {
            var upper = ((((BigInteger)(i + 1)) << 32) - (((BigInteger)points[i]) * pointCount));
            var lower = ((((BigInteger)points[i]) * pointCount) - (((BigInteger)i) << 32));

            if (upper > numerator) { numerator = upper; }
            if (lower > numerator) { numerator = lower; }
        }

        return (numerator, denominator);
    }
}
