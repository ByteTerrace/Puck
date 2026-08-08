using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// The FULL-VOLUME statistical statements for the gaussian pair, the alias table, the shuffle and certified low
/// discrepancy. Every claim here supplies its own basis through <see cref="Laws.Claim"/> — none consumes a
/// <see cref="Domain"/>, so none can slide the operands its Default and Deep siblings read.
/// </summary>
/// <remarks>
/// <para>
/// Each claim has a reduced-volume sibling in <see cref="SamplingClaims"/>, sharing its seed and multiplying its draw
/// count. The siblings are what a change loop sees; these are the gates of record the sampling law family's ENVELOPE
/// and OWED legs name.
/// </para>
/// <para>
/// Determinism is total: every generator is seeded by literal, no wall clock or platform RNG is read, and every
/// threshold is either a published constant or a standard-error band derived from the sample count in the same run.
/// Every weight set is drawn from <see cref="Pcg32XshRr"/> rather than <c>System.Random</c>, whose sequence is not a
/// stable contract across runtimes.
/// </para>
/// </remarks>
internal static class SamplingDistributionClaims {
    // ---- the gaussian pair ----

    /// <summary>The number of standard errors a derived statistical band is allowed to span. Eight is the alias
    /// table's published rule, reused here so one rule governs every derived band in this file.</summary>
    private const double StandardErrorBand = 8.0;

    /// <summary>
    /// <see cref="Pcg32XshRr.NextGaussianPair"/>'s first four moments, six two-sided CDF bins and the beyond-three-sigma
    /// tail, over 20000000 pairs — 40000000 samples — at a fixed seed.
    /// </summary>
    /// <returns><see langword="null"/> when every gate holds; the counterexample otherwise.</returns>
    /// <remarks>Every gate is the TIGHTER of its fixed threshold and an eight-standard-error band derived from the
    /// sample count in this run, so the volume buys teeth rather than only confidence.</remarks>
    public static string? GaussianMomentsCdfTailAtScaleSurface() {
        var generator = Pcg32XshRr.Create(state: 2026UL, stream: 13UL);
        var consumptionTwin = Pcg32XshRr.Create(state: 2026UL, stream: 13UL);
        const int PairCount = 20_000_000;
        const double SampleCount = (2.0 * PairCount);
        var mean = 0.0;
        var secondMoment = 0.0;
        var thirdMoment = 0.0;
        var fourthMoment = 0.0;
        var beyondThreeSigma = 0L;
        double[] binEdges = [0.5, 1.0, 1.5, 2.0, 2.5, 3.0];
        var binCounts = new long[binEdges.Length];

        for (var pair = 0; (pair < PairCount); ++pair) {
            var (first, second) = generator.NextGaussianPair();
            var sample = ((double)first);

            for (var half = 0; (half < 2); ++half) {
                var square = (sample * sample);

                mean += sample;
                secondMoment += square;
                thirdMoment += (square * sample);
                fourthMoment += (square * square);

                var magnitude = Math.Abs(sample);

                if (magnitude > 3.0) { ++beyondThreeSigma; }

                for (var bin = 0; (bin < binEdges.Length); ++bin) {
                    if (magnitude <= binEdges[bin]) { ++binCounts[bin]; }
                }

                sample = ((double)second);
            }
        }

        mean /= SampleCount;
        secondMoment /= SampleCount;
        thirdMoment /= SampleCount;
        fourthMoment /= SampleCount;

        var variance = (secondMoment - (mean * mean));
        var kurtosis = (fourthMoment / (variance * variance));
        // The standard errors of the first four sample moments of a standard normal over n draws are 1/sqrt(n),
        // sqrt(2/n), sqrt(6/n) and sqrt(24/n). Each gate below takes the SMALLER of its fixed threshold and eight of
        // these, so raising the volume can only tighten the gate.
        var meanGate = Math.Min(1e-3, (StandardErrorBand / Math.Sqrt(SampleCount)));
        var varianceGate = Math.Min(3e-3, (StandardErrorBand * Math.Sqrt((2.0 / SampleCount))));
        var skewGate = Math.Min(5e-3, (StandardErrorBand * Math.Sqrt((6.0 / SampleCount))));
        var kurtosisGate = Math.Min(2e-2, (StandardErrorBand * Math.Sqrt((24.0 / SampleCount))));

        if (Math.Abs(mean) > meanGate) { return $"mean {mean:E4} exceeds {meanGate:E4} over {SampleCount:F0} samples"; }
        if (Math.Abs(variance - 1.0) > varianceGate) { return $"variance {variance:F7} departs from 1 by more than {varianceGate:E4}"; }
        if (Math.Abs(thirdMoment) > skewGate) { return $"third moment (skew) {thirdMoment:E4} exceeds {skewGate:E4}"; }
        if (Math.Abs(kurtosis - 3.0) > kurtosisGate) { return $"kurtosis {kurtosis:F7} departs from 3 by more than {kurtosisGate:E4}"; }

        // Two-sided Phi at the six bin edges, hand-tabulated outside this tree.
        double[] twoSidedPhi = [0.3829249, 0.6826895, 0.8663856, 0.9544997, 0.9875807, 0.9973002];

        for (var bin = 0; (bin < binEdges.Length); ++bin) {
            var empirical = (binCounts[bin] / SampleCount);
            var target = twoSidedPhi[bin];
            var binGate = Math.Min(1.5e-3, (StandardErrorBand * Math.Sqrt(((target * (1.0 - target)) / SampleCount))));

            if (Math.Abs(empirical - target) > binGate) {
                return $"CDF bin |z|<={binEdges[bin]} empirical {empirical:F7} vs target {target:F7}, over the {binGate:E4} band";
            }
        }

        // The tail's reference is derived rather than tabulated: Oracles.EncloseGaussianTailBeyondThreeSigma bounds
        // P(|Z|>3) exactly in BigInteger from Gordon's inequality, e^4.5's own Taylor series and Oracles.Pi. The
        // enclosure's width is the REFERENCE's uncertainty; the statistical margin added on top is the tighter of
        // 3e-4 and eight binomial standard errors at this volume.
        var tailEnclosure = Oracles.EncloseGaussianTailBeyondThreeSigma(guardBitCount: Oracles.GuardBitCount);
        var enclosureScale = ((double)(BigInteger.One << (16 + Oracles.GuardBitCount)));
        var tailLow = (((double)tailEnclosure.Low) / enclosureScale);
        var tailHigh = (((double)tailEnclosure.High) / enclosureScale);
        var tail = (beyondThreeSigma / SampleCount);
        var tailGate = Math.Min(3e-4, (StandardErrorBand * Math.Sqrt(((tailHigh * (1.0 - tailHigh)) / SampleCount))));

        if ((tail < (tailLow - tailGate)) || (tail > (tailHigh + tailGate))) {
            return $"tail P(|z|>3) {tail:E5} is outside [{(tailLow - tailGate):E5}, {(tailHigh + tailGate):E5}] (Gordon enclosure [{tailLow:E5}, {tailHigh:E5}], margin {tailGate:E4})";
        }

        // Consumption, read off the STATE after forty million samples rather than by counting calls: exactly two
        // advances per pair, which is what makes the draw replayable from a snapshot.
        consumptionTwin.Advance(count: (2UL * PairCount));

        if (consumptionTwin.State != generator.State) {
            return $"twenty million pairs did not consume exactly two advances each: state 0x{generator.State:x16} against 0x{consumptionTwin.State:x16}";
        }

        return null;
    }

    // ---- the shuffle ----

    /// <summary>
    /// <see cref="Pcg32XshRr.Shuffle{TElement}(Span{TElement})"/> over four elements at 480000 trials:
    /// all 24 permutations appear, each within 700 of the uniform 20000.
    /// </summary>
    /// <returns><see langword="null"/> when every gate holds; the counterexample otherwise.</returns>
    public static string? ShuffleUniformityAtScaleSurface() {
        var generator = Pcg32XshRr.Create(state: 777UL, stream: 30UL);
        const int Trials = 480_000;
        const int Expected = (Trials / 24);
        // A four-element permutation is two bits per position, so the whole ordering is one byte and the histogram is a
        // flat array rather than a keyed dictionary: same statement, no per-trial allocation.
        var counts = new int[256];

        for (var trial = 0; (trial < Trials); ++trial) {
            Span<int> deck = [0, 1, 2, 3];

            generator.Shuffle(values: deck);

            ++counts[((((deck[0] << 6) | (deck[1] << 4)) | (deck[2] << 2)) | deck[3])];
        }

        var distinct = 0;

        for (var key = 0; (key < counts.Length); ++key) {
            if (counts[key] == 0) { continue; }

            ++distinct;

            if (Math.Abs(counts[key] - Expected) > 700) {
                return $"permutation {(key >> 6) & 3}{(key >> 4) & 3}{(key >> 2) & 3}{key & 3} occurred {counts[key]} times over {Trials} trials, expected {Expected} +/- 700";
            }
        }

        if (distinct != 24) {
            return $"produced {distinct} distinct orderings of four elements over {Trials} trials, expected all 24";
        }

        // Two companion statements: an identically seeded pair shuffles identically, and the shuffle is a
        // permutation — every one of the eight elements survives exactly once.
        var firstGenerator = Pcg32XshRr.Create(state: 5UL, stream: 31UL);
        var secondGenerator = Pcg32XshRr.Create(state: 5UL, stream: 31UL);
        Span<int> firstDeck = [0, 1, 2, 3, 4, 5, 6, 7];
        Span<int> secondDeck = [0, 1, 2, 3, 4, 5, 6, 7];

        firstGenerator.Shuffle(values: firstDeck);
        secondGenerator.Shuffle(values: secondDeck);

        var seen = 0;

        for (var i = 0; (i < firstDeck.Length); ++i) {
            if (firstDeck[i] != secondDeck[i]) {
                return $"two identically seeded shuffles diverged at position {i}: {firstDeck[i]} against {secondDeck[i]}";
            }

            seen |= (1 << firstDeck[i]);
        }

        if (seen != 0xFF) {
            return $"the eight-element shuffle did not preserve the multiset: occupancy 0x{seen:X2}";
        }

        return null;
    }

    // ---- the alias table ----

    /// <summary>
    /// The alias table's draw frequencies over six weight shapes at volumes up to twelve million draws, against the
    /// construction weights.
    /// </summary>
    /// <returns><see langword="null"/> when every gate holds; the counterexample otherwise.</returns>
    /// <remarks>The comparison is exact: the departure is measured as
    /// <c>|count·total − draws·weight|</c> in <see cref="BigInteger"/>, and only the BAND — eight binomial standard
    /// errors — is formed in floating point.</remarks>
    public static string? AliasTableFrequencyAtScaleSurface() {
        // The sixth shape's 257 weights come from Pcg32XshRr rather than System.Random, whose sequence is not a stable
        // contract across runtimes. Index thirteen is deliberately zero, so the never-sample-a-zero statement has a
        // zero buried in a long weight vector.
        var weightGenerator = Pcg32XshRr.Create(state: 106UL, stream: 21UL);
        var wideWeights = new ulong[257];

        for (var i = 0; (i < wideWeights.Length); ++i) { wideWeights[i] = (weightGenerator.NextUInt32() % 1_000_000UL); }

        wideWeights[13] = 0UL;

        (string Label, ulong[] Weights, int Draws, ulong Seed)[] shapes = [
            ("small-integer-ratios", [1UL, 2UL, 7UL], 8_000_000, 101UL),
            ("uniform-four-way", [5UL, 5UL, 5UL, 5UL], 4_000_000, 102UL),
            ("zeros-interleaved", [0UL, 5UL, 0UL, 3UL, 0UL, 2UL], 8_000_000, 103UL),
            ("singleton", [42UL], 100_000, 104UL),
            ("one-to-a-trillion", [1UL, (1UL << 40)], 4_000_000, 105UL),
            ("two-hundred-fifty-seven-way", wideWeights, 12_000_000, 106UL),
        ];

        foreach (var (label, weights, draws, seed) in shapes) {
            var entries = new (int Element, ulong Weight)[weights.Length];

            for (var i = 0; (i < weights.Length); ++i) { entries[i] = (i, weights[i]); }

            var table = WeightedSampler.Create<int>(entries: entries);
            var tableTwin = WeightedSampler.Create<int>(entries: entries);

            if (table.Count != weights.Length) {
                return $"{label}: table Count is {table.Count} for {weights.Length} construction entries";
            }

            var generator = Pcg32XshRr.Create(state: seed, stream: 21UL);
            var counts = new long[weights.Length];

            for (var draw = 0; (draw < draws); ++draw) {
                var index = table.SampleIndex(generator: ref generator);

                if ((index < 0) || (index >= weights.Length)) {
                    return $"{label}: draw {draw} selected index {index}, outside [0, {weights.Length})";
                }

                ++counts[index];
            }

            var total = BigInteger.Zero;

            foreach (var weight in weights) { total += weight; }

            for (var i = 0; (i < weights.Length); ++i) {
                if ((weights[i] == 0UL) && (counts[i] != 0L)) {
                    return $"{label}: zero-weight entry {i} was selected {counts[i]} times";
                }

                // Eight binomial standard errors, floored at 2e-9 so a vanishing probability still has a band. Only
                // the band is floating point; the departure it gates is exact.
                var probability = (((double)weights[i]) / ((double)total));
                var band = Math.Max((StandardErrorBand * Math.Sqrt(((probability * (1.0 - probability)) / draws))), 2e-9);
                var allowed = new BigInteger(Math.Ceiling(((band * draws) * ((double)total))));
                var departure = BigInteger.Abs((counts[i] * total) - (((BigInteger)draws) * weights[i]));

                if (departure > allowed) {
                    return $"{label}[{i}]: weight {weights[i]} of {total} drew {counts[i]} of {draws}, departing by {departure} against the {StandardErrorBand:F0}-sigma allowance {allowed}";
                }
            }

            // Construction determinism: an independently built table over the identical entries samples identically.
            var probe = Pcg32XshRr.Create(state: 9UL, stream: 2UL);
            var probeTwin = Pcg32XshRr.Create(state: 9UL, stream: 2UL);

            for (var draw = 0; (draw < 10_000); ++draw) {
                if (table.SampleIndex(generator: ref probe) != tableTwin.SampleIndex(generator: ref probeTwin)) {
                    return $"{label}: two independently constructed tables diverged at draw {draw}";
                }
            }
        }

        return null;
    }

    // ---- certified low discrepancy: badly-approximable equidistribution, certified by the continued fraction ----

    /// <summary>
    /// The measured star discrepancy of <see cref="CertifiedLowDiscrepancy"/>'s points ACROSS SCALES: six certificates
    /// spanning K = 1 through K = 100, at each of five point counts, each required to fall under the closed-form
    /// certified bound.
    /// </summary>
    /// <returns><see langword="null"/> when every gate holds; the counterexample otherwise.</returns>
    public static string? CertifiedLowDiscrepancyMeasuredAcrossScalesSurface() {
        // Six quadratic irrationals, with the continued-fraction expansion each certificate comes
        // from named beside it. The certificates themselves are independently pinned against
        // Oracles.MaximumPartialQuotient by certified.certificate-vs-partial-quotients; what this claim adds is the
        // MEASURED discrepancy at five scales, which no other case in the suite reaches.
        (string Name, long P, long Q, long D, long R, long Certificate)[] cases = [
            ("golden", 1L, 1L, 5L, 2L, 1L),        // (1 + sqrt 5) / 2 = [1; (1)]
            ("silver", 1L, 1L, 2L, 1L, 2L),        // 1 + sqrt 2       = [2; (2)]
            ("sqrt2", 0L, 1L, 2L, 1L, 2L),         // sqrt 2           = [1; (2)]
            ("sqrt13", 0L, 1L, 13L, 1L, 6L),       // sqrt 13          = [3; (1, 1, 1, 1, 6)]
            ("sqrt50", 0L, 1L, 50L, 1L, 14L),      // sqrt 50          = [7; (14)]
            ("sqrt2501", 0L, 1L, 2501L, 1L, 100L), // sqrt 2501        = [50; (100)]
        ];
        int[] scales = [64, 256, 1024, 4096, 16384];

        foreach (var certifiedCase in cases) {
            var sequence = CertifiedLowDiscrepancy.FromQuadraticIrrational(p: certifiedCase.P, q: certifiedCase.Q, d: certifiedCase.D, r: certifiedCase.R);

            if (sequence.Certificate != certifiedCase.Certificate) {
                return $"{certifiedCase.Name} certifies at {sequence.Certificate}, expected {certifiedCase.Certificate}";
            }

            foreach (var pointCount in scales) {
                var (numerator, denominator) = ExactStarDiscrepancy(sequence: sequence, pointCount: pointCount);
                var bound = sequence.DiscrepancyBound(pointCount: pointCount);

                // measured = numerator/denominator, bound = bound.Value/2^16; cross-multiplied, so no rounding enters.
                // ENVELOPE: the certified bound is a classical UPPER bound and is not tight. Mutation-probed while
                // porting: the comparison still holds with the bound quartered and fails with it cut to an eighth, so
                // what it separates is a badly wrong bound rather than the bound's constant.
                if ((numerator * 65536) > (((BigInteger)bound.Value) * denominator)) {
                    return $"{certifiedCase.Name} measured star discrepancy {numerator}/{denominator} exceeds the certified bound {bound} at N={pointCount}";
                }
            }
        }

        // Teeth across the same five scales rather than the sibling's three: a certificate of 100 measures markedly
        // worse than one of 1, and the three certificates 1 < 2 < 14 measure strictly monotone. Every side shares the
        // point count and therefore the denominator, so the comparison is on numerators alone.
        var golden = CertifiedLowDiscrepancy.FromQuadraticIrrational(p: 1L, q: 1L, d: 5L, r: 2L);
        var silver = CertifiedLowDiscrepancy.FromQuadraticIrrational(p: 1L, q: 1L, d: 2L, r: 1L);
        var sqrt50 = CertifiedLowDiscrepancy.FromQuadraticIrrational(p: 0L, q: 1L, d: 50L, r: 1L);
        var badK = CertifiedLowDiscrepancy.FromQuadraticIrrational(p: 0L, q: 1L, d: 2501L, r: 1L);

        foreach (var pointCount in new[] { 1024, 4096, 16384 }) {
            var (goldenNumerator, _) = ExactStarDiscrepancy(sequence: golden, pointCount: pointCount);
            var (silverNumerator, _) = ExactStarDiscrepancy(sequence: silver, pointCount: pointCount);
            var (sqrt50Numerator, _) = ExactStarDiscrepancy(sequence: sqrt50, pointCount: pointCount);
            var (badKNumerator, _) = ExactStarDiscrepancy(sequence: badK, pointCount: pointCount);

            if (badKNumerator <= (2 * goldenNumerator)) {
                return $"K=100 discrepancy {badKNumerator} is not more than twice K=1's {goldenNumerator} at N={pointCount}";
            }
            if (!((goldenNumerator < silverNumerator) && (silverNumerator < sqrt50Numerator))) {
                return $"measured discrepancy is not monotone in K at N={pointCount}: golden={goldenNumerator} silver={silverNumerator} sqrt50={sqrt50Numerator}";
            }
        }

        // Coverage: no sequence leaves an empty circular gap wider than a twentieth of the unit interval over its
        // first 4096 points, computed exactly on the raw UQ0.32 values.
        foreach (var certifiedCase in cases) {
            var sequence = CertifiedLowDiscrepancy.FromQuadraticIrrational(p: certifiedCase.P, q: certifiedCase.Q, d: certifiedCase.D, r: certifiedCase.R);
            var points = new uint[4096];

            for (var i = 0; (i < points.Length); ++i) { points[i] = sequence.Point(index: ((ulong)(i + 1))).Value; }

            Array.Sort(array: points);

            var gap = ((((ulong)points[0]) + (uint.MaxValue - points[^1])) + 1UL);

            for (var i = 1; (i < points.Length); ++i) {
                var step = ((ulong)(points[i] - points[i - 1]));

                if (step > gap) { gap = step; }
            }

            if ((gap * 20UL) > (1UL << 32)) {
                return $"{certifiedCase.Name} leaves an empty gap of {gap} raw, wider than a twentieth of the unit interval, over its first 4096 points";
            }
        }

        // Determinism over 200000 points: two independently constructed instances of one generator
        // agree bit for bit, which is what lets a certificate be rebuilt from its four integers rather than persisted.
        var goldenAgain = CertifiedLowDiscrepancy.FromQuadraticIrrational(p: 1L, q: 1L, d: 5L, r: 2L);

        for (var index = 0UL; (index < 200_000UL); ++index) {
            if (golden.Point(index: index) != goldenAgain.Point(index: index)) {
                return $"two independently constructed golden sequences diverged at index {index}";
            }
        }

        return null;
    }

    /// <summary>The exact star discrepancy of <paramref name="sequence"/>'s first <paramref name="pointCount"/> points:
    /// the returned numerator over the returned denominator (<paramref name="pointCount"/> · 2³²) is
    /// <c>max_i max((i+1)/N − x_i, x_i − i/N)</c> with no rounding anywhere, rather than a floating-point
    /// sort-and-scan.</summary>
    /// <param name="sequence">The sequence to measure.</param>
    /// <param name="pointCount">How many of its opening points to measure.</param>
    /// <returns>The discrepancy as an exact rational.</returns>
    private static (BigInteger Numerator, BigInteger Denominator) ExactStarDiscrepancy(CertifiedLowDiscrepancy sequence, int pointCount) {
        var points = new uint[pointCount];

        for (var i = 0; (i < pointCount); ++i) { points[i] = sequence.Point(index: ((ulong)(i + 1))).Value; }

        Array.Sort(array: points);

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
