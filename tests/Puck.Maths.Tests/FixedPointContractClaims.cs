using System.Numerics;
using Xunit;

namespace Puck.Maths.Tests;

/// <summary>
/// Fast exact and structural fixed-point contract claims (
/// <c>fixed-point</c>) for the narrow slice of that stage's checks that no existing law already pins. The
/// declarations in <see cref="LawRegistry"/> invoke these methods as Default-tier laws, so every assertion
/// participates in both the ordinary test gate and the mechanically generated public-member coverage ledger.
/// </summary>
internal static class FixedPointContractClaims {
    // ---- LayerSequence: the closed-form inverse vs. an incremental walker, and the bounded-horizon channels ----

    /// <summary>A bit-for-bit port of FixedPointStage's layer-sequence regression: <see cref="LayerSequence.LayerOf"/>
    /// agrees with an O(n) walker that accumulates <see cref="LayerSequence.LayerSize"/> forward from the sequence's
    /// own <see cref="LayerSequence.Seed"/>, <see cref="LayerSequence.Count"/> lands its boundary exactly at scale,
    /// <see cref="LayerSequence.Linear"/> indexes flatly and refuses an unrepresentable layer, and a hand-built
    /// bounded horizon exercises <see cref="LayerSequence.MaxLayer"/>, <see cref="LayerSequence.Capacity"/>,
    /// <see cref="LayerSequence.Locate"/> and <see cref="LayerSequence.Project"/>'s overflow/depth channels,
    /// including the refusal past capacity and the saturating overflow on an unbounded sequence.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? LayerSequenceWalkerAndBoundedHorizonSurface() {
        (string Name, LayerSequence Sequence)[] layerPresets = [
            ("triangular", LayerSequence.Triangular), ("pronic", LayerSequence.Pronic), ("square", LayerSequence.Square),
            ("centered-square", LayerSequence.CenteredSquare), ("centered-hexagonal", LayerSequence.CenteredHexagonal),
        ];

        foreach (var (name, sequence) in layerPresets) {
            var layer = 0L;
            var layerEnd = sequence.Seed;

            for (var x = 0L; (x < 65_536L); x++) {
                while (layerEnd <= x) {
                    layer++;
                    layerEnd += sequence.LayerSize(layer: layer);
                }

                Assert.True(condition: (sequence.LayerOf(index: x) == layer), userMessage: $"layer-sequence {name}: LayerOf({x}) disagrees with the walker at layer {layer}");
            }

            for (var n = 1L; (n < 100_000_000L); n <<= 3) {
                var boundary = sequence.Count(layerCount: n);

                Assert.True(
                    condition: ((sequence.LayerOf(index: boundary) == (n + 1L)) && (sequence.LayerOf(index: (boundary - 1L)) == n)),
                    userMessage: $"layer-sequence {name}: boundary of layer {n} is not exact"
                );
            }
        }

        var flat = LayerSequence.Linear(seed: 3L, size: 5L);

        Assert.Equal(expected: 0L, actual: flat.LayerOf(index: 2L));
        Assert.Equal(expected: 1L, actual: flat.LayerOf(index: 3L));
        Assert.Equal(expected: 2L, actual: flat.LayerOf(index: 12L));
        Assert.Throws<OverflowException>(testCode: () => LayerSequence.Linear(seed: 0L, size: 1L).LayerOf(index: long.MaxValue));

        var horizon = LayerSequence.Create(seed: 1L, start: 6L, step: -2L);

        Assert.Equal(expected: 3L, actual: horizon.MaxLayer);
        Assert.Equal(expected: 13L, actual: horizon.Capacity);
        Assert.Equal(expected: 3L, actual: horizon.LayerOf(index: 12L));
        Assert.Equal(expected: new LayerLocation(Layer: 1L, Offset: 4L), actual: horizon.Locate(index: 5L));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => horizon.LayerOf(index: 13L));
        Assert.Equal(expected: new LayerProjection(Depth: 2L, Layer: 3L, Overflow: 8L), actual: horizon.Project(index: 20L));
        Assert.Equal(expected: new LayerProjection(Depth: 0L, Layer: 3L, Overflow: 0L), actual: horizon.Project(index: 12L));
        Assert.Equal(expected: long.MaxValue, actual: LayerSequence.Linear(seed: 0L, size: 0L).Project(index: long.MaxValue).Overflow);

        return null;
    }

    // ---- BinaryIntegerFunctions: the signed narrow (short -> uint, BMI2) and wide (long -> Int128, SWAR) pairing branches ----

    /// <summary>Interleaves the low sixteen bits of <paramref name="value"/> and <paramref name="other"/> one bit at
    /// a time, transcribed from the DEFINITION — value's bits at the even positions, other's at the odd ones —
    /// sharing no line with <see cref="BinaryIntegerFunctions.BitwisePair{TInput,TResult}"/>'s
    /// <c>Bmi2.ParallelBitDeposit</c> branch for <c>short</c>/<c>ushort</c>.</summary>
    private static uint NarrowBitwisePairReference(ushort value, ushort other) {
        var paired = 0U;

        for (var bit = 0; (bit < 16); ++bit) {
            paired |= (((uint)((value >> bit) & 1)) << (bit << 1));
            paired |= (((uint)((other >> bit) & 1)) << ((bit << 1) + 1));
        }

        return paired;
    }
    /// <summary>Interleaves the full sixty-four-bit two's-complement pattern of <paramref name="value"/> and
    /// <paramref name="other"/> one bit at a time, transcribed from the DEFINITION and sharing no line with
    /// <see cref="BinaryIntegerFunctions.BitwisePair{TInput,TResult}"/>'s width-agnostic SWAR fallback for
    /// <c>long</c>.</summary>
    private static Int128 WideBitwisePairReference(ulong value, ulong other) {
        var paired = UInt128.Zero;

        for (var bit = 0; (bit < 64); ++bit) {
            paired |= (((UInt128)((value >> bit) & 1UL)) << (bit << 1));
            paired |= (((UInt128)((other >> bit) & 1UL)) << ((bit << 1) + 1));
        }

        return unchecked((Int128)paired);
    }

    // Negative, boundary and index-derived short pairs: both MinValue, one MinValue against zero either side, MaxValue
    // against MinValue, both lanes at -1, and two bit-derived patterns (0xACE5, 0x53A1) that exercise every nibble.
    private static readonly (short Value, short Other)[] NarrowPairLadder = [
        (short.MinValue, 0), (0, short.MinValue), (short.MinValue, short.MinValue),
        (short.MaxValue, short.MinValue), (-1, -1), (-1, 0), (0, -1),
        (unchecked((short)0xACE5), unchecked((short)0x53A1)),
    ];
    // The same shape at the wide (long -> Int128) width.
    private static readonly (long Value, long Other)[] WidePairLadder = [
        (long.MinValue, 0L), (0L, long.MinValue), (long.MinValue, long.MinValue),
        (long.MaxValue, long.MinValue), (-1L, -1L), (-1L, 0L), (0L, -1L),
        (unchecked((long)0xACE5_1234_5678_9ABCUL), unchecked((long)0x53A1_FEDC_BA98_7654UL)),
    ];

    /// <summary>Proves <see cref="BinaryIntegerFunctions.BitwisePair{TInput,TResult}"/> and
    /// <see cref="BinaryIntegerFunctions.BitwiseUnpair{TInput,TResult}"/> at the two branches no existing law reaches:
    /// the <c>short</c>/<c>ushort</c> <c>Bmi2.ParallelBitDeposit</c> path (distinct from the <c>int</c>/<c>uint</c>
    /// branch <c>core.binary-integer-contracts</c> exercises), and negative operands on the SWAR fallback the
    /// existing wide-carrier law only ever feeds non-negative <see langword="ulong"/> values. A sign-extension leak
    /// into the interleave — CreateTruncating widening a negative short or long before the deposit mask trims it back
    /// down — would show up as a wrong pattern here rather than a coincidentally-matching one.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? BitwisePairSignedNarrowAndWideCarriersSurface() {
        foreach (var (value, other) in NarrowPairLadder) {
            var expectedPair = NarrowBitwisePairReference(other: unchecked((ushort)other), value: unchecked((ushort)value));
            var actualPair = value.BitwisePair<short, uint>(other: other);

            Assert.True(
                condition: (actualPair == expectedPair),
                userMessage: $"BitwisePair<short,uint> at value={value} other={other} produced {actualPair:X8}, expected the truncated-width interleave {expectedPair:X8}"
            );

            var unpaired = actualPair.BitwiseUnpair<uint, short>();

            Assert.Equal(actual: unpaired, expected: (value, other));
        }

        foreach (var (value, other) in WidePairLadder) {
            var expectedPair = WideBitwisePairReference(other: unchecked((ulong)other), value: unchecked((ulong)value));
            var actualPair = value.BitwisePair<long, Int128>(other: other);

            Assert.True(
                condition: (actualPair == expectedPair),
                userMessage: $"BitwisePair<long,Int128> at value={value} other={other} produced {actualPair:X32}, expected the wide-width interleave {expectedPair:X32}"
            );

            var unpaired = actualPair.BitwiseUnpair<Int128, long>();

            Assert.Equal(actual: unpaired, expected: (value, other));
        }

        return null;
    }
    // ---- FieldNoise: the WIDE hierarchical path, reached only when a FixedPosition's combined coordinate leaves signed-64 ----

    /// <summary>Proves <see cref="FieldNoise.Sample(ulong, FixedPosition)"/>'s WIDE path (<c>SampleWideLattice</c>,
    /// taken when the cell-scaled coordinate leaves the signed sixty-four-bit range the flat fast path tests for)
    /// does not silently discard the high cell bits, and that two different wide encodings of one logical point
    /// sample identically. Every existing sampling law stays on signed-64 coordinates: the bounds/gradient case's own
    /// DelegationTwin leg states outright that the WIDE path "is not reached by any position here, and no leg claims
    /// it", and the periodicity canary's hash-period and octave-wrap probes stay on signed-64 coordinates too.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FieldNoiseWidePositionAliasAndRebaseSurface() {
        // The alias-period probe: eight positions whose CellX is zero against the same positions with CellX pushed to
        // 2^(64 - CellSizeLog2), each under its own seed. A wide path that truncated CellX to its low bits before
        // hashing would alias every one of the eight pairs.
        var aliasPeriod = (1L << (64 - FixedPosition.CellSizeLog2));
        var allAliased = true;

        for (var probe = 0; (probe < 8); probe++) {
            var local = new FixedVector3(
                X: FixedQ4816.FromRawBits(value: ((probe * 7919L) - 20000L)),
                Y: FixedQ4816.FromRawBits(value: ((probe * 3571L) + 1234L)),
                Z: FixedQ4816.FromRawBits(value: ((probe * -421L) + 4321L))
            );
            var originCellNoise = FieldNoise.Sample(seed: ((ulong)(42 + probe)), position: new FixedPosition(cellX: 0L, cellY: 0L, cellZ: 0L, local: local));
            var farCellNoise = FieldNoise.Sample(seed: ((ulong)(42 + probe)), position: new FixedPosition(cellX: aliasPeriod, cellY: 0L, cellZ: 0L, local: local));

            allAliased &= (originCellNoise == farCellNoise);
        }

        Assert.False(condition: allAliased, userMessage: "field noise discards high WorldCoord3 cell bits on the wide path");

        // The wide rebase probe: an equivalent wide hierarchical representation across a cell carry must address the
        // same field point. Bumping CellX/CellY/CellZ by one cell while shifting Local by exactly minus/plus one cell
        // width denotes the identical logical position.
        var wideCell = (1L << 50);
        var wide = new FixedPosition(cellX: wideCell, cellY: -wideCell, cellZ: wideCell, local: FixedVector3.Zero);
        var wideRebased = new FixedPosition(
            cellX: (wideCell + 1L),
            cellY: (-wideCell - 1L),
            cellZ: (wideCell + 1L),
            local: new FixedVector3(
                X: FixedQ4816.FromRawBits(value: -(1L << (FixedPosition.CellSizeLog2 + FixedQ4816.FractionBitCount))),
                Y: FixedQ4816.FromRawBits(value: (1L << (FixedPosition.CellSizeLog2 + FixedQ4816.FractionBitCount))),
                Z: FixedQ4816.FromRawBits(value: -(1L << (FixedPosition.CellSizeLog2 + FixedQ4816.FractionBitCount)))
            )
        );

        Assert.Equal(
            expected: FieldNoise.Sample(position: wide, seed: 91UL),
            actual: FieldNoise.Sample(position: wideRebased, seed: 91UL)
        );

        return null;
    }
    // ---- UnsignedNumberFunctions.SquareRoot at its T = UInt128 instantiation, near the carrier's own ceiling ----

    /// <summary>Proves <see cref="UnsignedNumberFunctions.SquareRoot{T}(T)"/> at <c>T = UInt128</c>, the instantiation
    /// <c>core.unsigned-integer-contracts</c> credits by name but never actually reaches — that case's own leg states
    /// its sweep runs "through 10000" at type <see langword="uint"/>. Boundary rows are checked against hand-derived
    /// exact literals; interior rows near the carrier's own ceiling are checked against the defining inequality
    /// <c>root² ≤ value &lt; (root + 1)²</c> formed in <see cref="BigInteger"/>, since <c>(root + 1)²</c> itself
    /// overflows <see cref="UInt128"/> for several of them and would silently wrap if formed in the carrier under
    /// test.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? UnsignedSquareRootUInt128CarrierBoundarySurface() {
        (UInt128 Value, UInt128 Expected)[] boundaryLadder = [
            (UInt128.Zero, UInt128.Zero),
            ((UInt128.One << 100), (UInt128.One << 50)),
            (((UInt128.One << 100) - UInt128.One), ((UInt128.One << 50) - UInt128.One)),
            (((UInt128.One << 100) + UInt128.One), (UInt128.One << 50)),
            ((((UInt128)ulong.MaxValue) * ulong.MaxValue), ulong.MaxValue),
            (UInt128.MaxValue, ulong.MaxValue),
            ((UInt128.MaxValue - UInt128.One), ulong.MaxValue),
        ];

        foreach (var (value, expected) in boundaryLadder) {
            Assert.Equal(expected: expected, actual: value.SquareRoot());
        }

        // Thirty-two index-derived interior operands spanning the widest quarter of the carrier: 2^(127-k) plus a
        // small index-derived offset, for k from 0 through 31.
        for (var k = 0; (k < 32); ++k) {
            var value = ((((UInt128)1) << (127 - k)) + (((UInt128)(k * 104_729)) << (k % 40)));
            var root = value.SquareRoot();
            var rootBig = ((BigInteger)root);
            var valueBig = ((BigInteger)value);

            Assert.True(condition: ((rootBig * rootBig) <= valueBig), userMessage: $"SquareRoot<UInt128>({value}) = {root} overshoots: root^2 exceeds the radicand");
            Assert.True(condition: (((rootBig + 1) * (rootBig + 1)) > valueBig), userMessage: $"SquareRoot<UInt128>({value}) = {root} undershoots: (root+1)^2 does not exceed the radicand");
        }

        return null;
    }

    // ---- FixedTickConversion: the seconds-to-engine-ticks round-up rule vs. exact BigInteger rational arithmetic ----

    /// <summary>Checks one raw Q48.16 duration against an INDEPENDENT BigInteger recomputation of the round-up rule —
    /// exact rational ceiling division, transcribed from the definition (duration * TicksPerSecond / 65536, rounded
    /// up), never reusing <see cref="BinaryIntegerFunctions.CeilingDivide{T}(T, T)"/> or the subject's own Int128
    /// arithmetic. Non-positive durations are pinned to zero by the same check.</summary>
    /// <param name="raw">The raw Q48.16 duration to check.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    private static string? CheckFixedTickConversion(long raw) {
        var seconds = FixedQ4816.FromRawBits(value: raw);
        var actual = FixedTickConversion.DurationEngineTicks(seconds: seconds);
        BigInteger expected;

        if (raw <= 0L) {
            expected = BigInteger.Zero;
        } else {
            var numerator = (((BigInteger)raw) * FixedTickConversion.TicksPerSecond);
            var denominator = ((BigInteger)65536);

            expected = ((numerator + (denominator - 1)) / denominator);
        }

        return ((((BigInteger)actual) == expected)
            ? null
            : $"FixedTickConversion.DurationEngineTicks(raw={raw}) = {actual}, expected ceil({raw}*{FixedTickConversion.TicksPerSecond}/65536) = {expected}");
    }

    /// <summary>Exact-by-construction: <see cref="FixedTickConversion.DurationEngineTicks"/> matches independent
    /// BigInteger rational ceiling division over a curated edge set (zero, the smallest positive raw, one-second and
    /// near-one-second boundaries, negative raws) plus a dense sweep across the first five seconds (positive and
    /// negative), so every residue class the Int128 ceiling-divide path can take near a tick boundary is exercised.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FixedTickConversionRoundsUpAgainstRationalArithmetic() {
        long[] edges = [
            0L, 1L, -1L, long.MinValue, long.MaxValue,
            65536L, 65535L, 65537L, -65536L, -65535L,
            (5L * 65536L), ((5L * 65536L) - 1L), ((5L * 65536L) + 1L),
        ];

        foreach (var raw in edges) {
            if (CheckFixedTickConversion(raw: raw) is { } detail) {
                return detail;
            }
        }

        for (var raw = 0L; (raw <= (5L * 65536L)); raw += 97L) {
            if (CheckFixedTickConversion(raw: raw) is { } detail) {
                return detail;
            }
        }

        for (var raw = -65536L; (raw < 0L); raw += 251L) {
            if (CheckFixedTickConversion(raw: raw) is { } detail) {
                return detail;
            }
        }

        return null;
    }

    // ---- FixedTickConversion.TryDurationEngineTicksExact: the exact-or-refuse rule vs. an independent BigInteger
    // decomposition of the authored decimal's own bits, with an independent BigInteger carrier ----

    /// <summary>Checks one authored decimal duration against an INDEPENDENT BigInteger recomputation — decomposing
    /// <paramref name="seconds"/> via <see cref="decimal.GetBits(decimal)"/> into its sign, base-10 scale, and
    /// unscaled 96-bit integer (lossless), then testing <c>unscaled * TicksPerSecond</c> for exact divisibility by
    /// <c>10^scale</c> in <see cref="BigInteger"/> rather than the subject's bounded <see cref="UInt128"/> carrier.
    /// </summary>
    /// <param name="seconds">The authored duration to check.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    private static string? CheckTryDurationEngineTicksExact(decimal seconds) {
        var actualExact = FixedTickConversion.TryDurationEngineTicksExact(seconds: seconds, ticks: out var actualTicks);
        var bits = decimal.GetBits(d: seconds);
        var negative = ((((uint)bits[3]) & 0x80000000U) != 0U);
        var scale = (((uint)bits[3]) >> 16) & 0xFFU;
        var unscaled = (((BigInteger)((uint)bits[2])) << 64) | (((BigInteger)((uint)bits[1])) << 32) | ((BigInteger)((uint)bits[0]));

        if (negative) {
            return (actualExact
                ? $"TryDurationEngineTicksExact({seconds}) = true (ticks={actualTicks}), expected false (negative duration)"
                : null);
        }

        var scalePower = BigInteger.Pow(exponent: ((int)scale), value: 10);
        var numerator = (unscaled * FixedTickConversion.TicksPerSecond);
        var expectedTicks = (numerator / scalePower);
        var expectedExact = (((numerator % scalePower) == BigInteger.Zero) && (expectedTicks <= ulong.MaxValue));

        if (actualExact != expectedExact) {
            return $"TryDurationEngineTicksExact({seconds}) returned {actualExact}, expected {expectedExact} (unscaled={unscaled}, scale={scale})";
        }

        if (expectedExact) {
            if (((BigInteger)actualTicks) != expectedTicks) {
                return $"TryDurationEngineTicksExact({seconds}) = {actualTicks} ticks, expected {expectedTicks}";
            }
        }

        return null;
    }

    /// <summary>Exact-or-refuse: <see cref="FixedTickConversion.TryDurationEngineTicksExact"/> matches an independent
    /// BigInteger decomposition of the authored decimal's own bits over a curated edge set (zero, a negative
    /// duration, every dyadic value already authored in a shipped or scenario document, the finest
    /// terminating-decimal grid that is always exact — 1/800 s and its multiples, each non-trivial divisor of 50400,
    /// curated inexact decimals including approximations of 1/24 s and 35/12 s, and exact values beyond the ulong
    /// result carrier) plus a dense millisecond sweep.
    /// </summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    // ---- FixedPointRounding.TryRoundRational: the scale shift, the tie, the sign, and every refusal ----
    // The scaling branch is the one no other law reaches: the mass-property chain calls this helper only with a zero
    // fraction bit count, so a shift dropped from the body leaves that chain bit-identical. Physics is where nonzero
    // shifts come from. Ties are counted rather than assumed, so the claim cannot quietly stop exercising them.
    public static string? TryRoundRationalScalesTiesAndRefuses() {
        int[] shifts = [0, 1, 2, 3, 7, 16, 31, 32, 48, 62, 63];
        long[] numerators = [0L, 1L, -1L, 2L, -2L, 3L, -3L, 5L, -5L, 7L, 11L, -11L, 1023L, -1024L, 65535L, -65537L];
        long[] denominators = [1L, -1L, 2L, -2L, 3L, 4L, -4L, 5L, 8L, 16L, -16L, 1000L, 65536L];
        var ties = 0;

        foreach (var shift in shifts) {
            foreach (var numerator in numerators) {
                foreach (var denominator in denominators) {
                    var expectedOk = Oracles.RoundedRational(
                        denominator: denominator,
                        fractionBitCount: shift,
                        numerator: numerator,
                        result: out var expected
                    );
                    var actualOk = FixedPointRounding.TryRoundRational(
                        denominator: denominator,
                        fractionBitCount: shift,
                        numerator: numerator,
                        result: out var actual
                    );

                    if (
                        (expectedOk != actualOk) ||
                        (expected != actual)
                    ) {
                        return $"n={numerator} d={denominator} f={shift}: subject ({actualOk}, {actual}) but oracle ({expectedOk}, {expected})";
                    }

                    var scaled = (BigInteger.Abs(value: new BigInteger(value: numerator)) * BigInteger.Pow(exponent: shift, value: 2));
                    var divisor = BigInteger.Abs(value: new BigInteger(value: denominator));

                    if ((((scaled % divisor) * 2) == divisor) && !divisor.IsOne) {
                        ++ties;
                    }
                }
            }
        }

        if (ties < 16) {
            return $"the case set stopped exercising the tie branch: only {ties} exact tie(s)";
        }

        // A zero denominator refuses, at every shift, and clears the result rather than leaving it.
        foreach (var shift in shifts) {
            if (FixedPointRounding.TryRoundRational(
                denominator: BigInteger.Zero,
                fractionBitCount: shift,
                numerator: BigInteger.One,
                result: out var zeroDenominator
            ) || (zeroDenominator != 0L)) {
                return $"a zero denominator was accepted at f={shift}";
            }
        }

        // A negative fraction bit count refuses rather than shifting the other way.
        foreach (var shift in ((int[])[-1, -16, -64, int.MinValue])) {
            if (FixedPointRounding.TryRoundRational(
                denominator: BigInteger.One,
                fractionBitCount: shift,
                numerator: BigInteger.One,
                result: out var negativeShift
            ) || (negativeShift != 0L)) {
                return $"a negative fraction bit count was accepted at f={shift}";
            }
        }

        // An obviously overflowing scale must be refused without attempting to materialize a 2^int.MaxValue
        // numerator. Zero remains exactly representable at that same scale.
        if (FixedPointRounding.TryRoundRational(
            denominator: BigInteger.One,
            fractionBitCount: int.MaxValue,
            numerator: BigInteger.One,
            result: out var enormousShift
        ) || (enormousShift != 0L)) {
            return "an obviously overflowing enormous fraction bit count was accepted";
        }

        if (!FixedPointRounding.TryRoundRational(
            denominator: BigInteger.One,
            fractionBitCount: int.MaxValue,
            numerator: BigInteger.Zero,
            result: out var enormousZero
        ) || (enormousZero != 0L)) {
            return "zero was refused at an enormous fraction bit count";
        }

        // Leaving the signed 64-bit raw refuses rather than wrapping — on both sides, and exactly at the boundary.
        (BigInteger Numerator, BigInteger Denominator, int Shift, bool Representable)[] boundaries = [
            (long.MaxValue, BigInteger.One, 0, true),
            (long.MinValue, BigInteger.One, 0, true),
            ((((BigInteger)long.MaxValue) + 1), BigInteger.One, 0, false),
            ((((BigInteger)long.MinValue) - 1), BigInteger.One, 0, false),
            (BigInteger.One, BigInteger.One, 63, false),
            (BigInteger.MinusOne, BigInteger.One, 63, true),
            (BigInteger.One, BigInteger.One, 62, true),
        ];

        foreach (var (numerator, denominator, shift, representable) in boundaries) {
            var expectedOk = Oracles.RoundedRational(
                denominator: denominator,
                fractionBitCount: shift,
                numerator: numerator,
                result: out var expected
            );
            var actualOk = FixedPointRounding.TryRoundRational(
                denominator: denominator,
                fractionBitCount: shift,
                numerator: numerator,
                result: out var actual
            );

            if (
                (expectedOk != representable) ||
                (actualOk != representable) ||
                (expected != actual)
            ) {
                return $"boundary n={numerator} d={denominator} f={shift}: expected representable={representable}, subject ({actualOk}, {actual}), oracle ({expectedOk}, {expected})";
            }
        }

        return null;
    }
    public static string? TryDurationEngineTicksExactAgainstDecimalBits() {
        decimal[] edges = [
            0.0m, -0.01m, -1.0m,
            0.25m, 0.5m, 1.0m, 3.0m,
            0.00125m, 0.0025m, 0.00375m, 0.005m, 0.00625m, 0.0075m, 0.00875m, 0.01m, 0.01125m, 0.0125m, 0.01375m, 0.015m,
            0.1m, 0.02m, 0.04m, 0.05m, 0.2m,
            0.041667m, 0.0417m, 0.04166666667m, 2.91667m,
            1000000000000000m, decimal.MaxValue,
        ];

        foreach (var seconds in edges) {
            if (CheckTryDurationEngineTicksExact(seconds: seconds) is { } detail) {
                return detail;
            }
        }

        for (var milli = 0; (milli <= 2000); milli += 37) {
            if (CheckTryDurationEngineTicksExact(seconds: (milli / 1000.0m)) is { } detail) {
                return detail;
            }
        }

        return null;
    }
}
