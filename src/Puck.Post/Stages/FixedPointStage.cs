using System.Globalization;
using System.Numerics;
using Puck.Maths;

namespace Puck.Post;

/// <summary>
/// Tier-A stage A1. Checks that <see cref="FixedQ4816"/> is not merely deterministic but CORRECT — its arithmetic,
/// square root, <see cref="FixedQ4816.Atan2"/>/<see cref="FixedQ4816.SinCos"/>, and banker's rounding all
/// agree with a <see cref="double"/> reference within the Q48.16 resolution — that <see cref="Pcg32XshRr"/>
/// reproduces the published PCG32 reference vector, with logarithmic advance and snapshot restore continuing the
/// exact sequence, and that <see cref="LayerSequence"/>'s constant-time inverse agrees with an incremental walker,
/// including the bounded-horizon saturation channels. It runs before the determinism stages, since a determinism
/// gate alone cannot catch a wrong-but-deterministic operation.
/// </summary>
internal sealed class FixedPointStage : IPostStage {
    /// <inheritdoc/>
    public string Name => "fixed-point";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        double[] roundTrip = [0d, 1d, -1d, 0.5d, -0.5d, 3.14159d, -2.71828d, 123.456d, -987.654d, 0.001d, -0.001d,];

        foreach (var value in roundTrip) {
            if (!Close(actual: ((double)FixedQ4816.FromDouble(value: value)), expected: value, tolerance: 3e-5d)) {
                return PostStageOutcome.Fail(detail: $"round-trip of {value} lost precision");
            }
        }

        // FromDouble saturates to the EXACT extremes, not merely the nearest representable double below them (which sits
        // a full ULP short of MaxValue), and still routes NaN to zero.
        if ((FixedQ4816.FromDouble(value: double.PositiveInfinity) != FixedQ4816.MaxValue) ||
            (FixedQ4816.FromDouble(value: 1e30d) != FixedQ4816.MaxValue) ||
            (FixedQ4816.FromDouble(value: double.NegativeInfinity) != FixedQ4816.MinValue) ||
            (FixedQ4816.FromDouble(value: double.NaN) != FixedQ4816.Zero) ||
            (FixedQ4816.FromDouble(value: (9223372036854774784d / 65536d)).Value != 9223372036854774784L) ||
            (UFixedQ4816.FromDouble(value: double.PositiveInfinity) != UFixedQ4816.MaxValue) ||
            (UFixedQ4816.FromDouble(value: (18446744073709549568d / 65536d)).Value != 18446744073709549568UL) ||
            (CreateChecked<UFixedQ4816, double>(value: (18446744073709549568d / 65536d)).Value != 18446744073709549568UL)) {
            return PostStageOutcome.Fail(detail: "FromDouble did not saturate to the exact extremes");
        }

        // The complete .NET generic-math surface is usable through constraints, and conversions operate on numeric
        // values rather than exposing the Q16 storage bits.
        var genericSum = Sum<FixedQ4816>(values: [FixedQ4816.FromDouble(value: 1.25d), FixedQ4816.FromDouble(value: 2.5d)]);
        var highDouble = 92_129_141_622_112.72d;
        var highFixed = FixedQ4816.FromRawBits(value: 6_037_775_425_346_779_136L);
        var highUnsigned = UFixedQ4816.FromRawBits(value: 18_446_744_073_709_547_520UL);

        if ((genericSum != FixedQ4816.FromDouble(value: 3.75d)) ||
            (FixedQ4816.Parse(s: "-12.125", provider: CultureInfo.InvariantCulture) != FixedQ4816.FromDouble(value: -12.125d)) ||
            (CreateChecked<FixedQ4816, int>(value: 7) != FixedQ4816.FromInteger(value: 7L)) ||
            (CreateSaturating<FixedQ4816, double>(value: double.PositiveInfinity) != FixedQ4816.MaxValue) ||
            (CreateSaturating<UFixedQ4816, int>(value: -1) != UFixedQ4816.Zero) ||
            (CreateChecked<FixedQ4816, double>(value: highDouble) != FixedQ4816.FromDouble(value: highDouble)) ||
            (BitConverter.DoubleToInt64Bits(value: double.CreateChecked(value: highFixed)) != BitConverter.DoubleToInt64Bits(value: ((double)highFixed))) ||
            (BitConverter.DoubleToInt64Bits(value: double.CreateChecked(value: highUnsigned)) != BitConverter.DoubleToInt64Bits(value: ((double)highUnsigned))) ||
            (int.CreateChecked(value: FixedQ4816.FromDouble(value: 7.75d)) != 7) ||
            !FixedQ4816.IsOddInteger(value: FixedQ4816.FromInteger(value: -3L)) ||
            FixedQ4816.IsInteger(value: FixedQ4816.FromDouble(value: 0.5d)) ||
            !UFixedQ4816.IsPositive(value: UFixedQ4816.Zero)) {
            return PostStageOutcome.Fail(detail: "generic-math parsing, conversion, classification, or constrained addition");
        }

        Span<char> formatted = stackalloc char[34];

        if (!FixedQ4816.FromDouble(value: -12.125d).TryFormat(destination: formatted, charsWritten: out var charsWritten, format: default, provider: CultureInfo.InvariantCulture) ||
            !formatted[..charsWritten].SequenceEqual(other: "-12.125")) {
            return PostStageOutcome.Fail(detail: "generic-math span formatting");
        }

        var commaFormat = ((NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone());

        commaFormat.NumberDecimalSeparator = ",";
        var ambiguousFormat = ((NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone());

        ambiguousFormat.NumberDecimalSeparator = "E";
        var ignoredCurrencyFormat = ((NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone());

        ignoredCurrencyFormat.CurrencySymbol = "E2";
        var alphabeticCurrencyFormat = ((NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone());

        alphabeticCurrencyFormat.CurrencySymbol = "AED";
        var embeddedDigitSeparatorFormat = ((NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone());

        embeddedDigitSeparatorFormat.NumberDecimalSeparator = "d1";
        var digitLeadingSeparatorFormat = ((NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone());

        digitLeadingSeparatorFormat.NumberDecimalSeparator = "1";
        var exponentLikeGroupFormat = ((NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone());

        exponentLikeGroupFormat.NumberGroupSeparator = "E";

        if ((FixedQ4816.FromDouble(value: -12.125d).ToString(format: "G", formatProvider: commaFormat) != "-12,125") ||
            (UFixedQ4816.FromDouble(value: 12.125d).ToString(format: "G", formatProvider: commaFormat) != "12,125") ||
            (FixedQ4816.Parse(s: "-12,125", provider: commaFormat) != FixedQ4816.FromDouble(value: -12.125d)) ||
            (FixedQ4816.Parse(s: "1E5", style: NumberStyles.Float, provider: ambiguousFormat) != FixedQ4816.FromDouble(value: 1.5d)) ||
            (FixedQ4816.Parse(s: "1E2", style: NumberStyles.Float, provider: ignoredCurrencyFormat) != FixedQ4816.FromInteger(value: 100L)) ||
            (FixedQ4816.Parse(s: "AED1.5", style: NumberStyles.Currency, provider: alphabeticCurrencyFormat) != FixedQ4816.FromDouble(value: 1.5d)) ||
            (FixedQ4816.Parse(s: "0d15", style: NumberStyles.Number, provider: embeddedDigitSeparatorFormat) != FixedQ4816.FromDouble(value: 0.5d)) ||
            (FixedQ4816.Parse(s: "15", style: NumberStyles.Number, provider: digitLeadingSeparatorFormat) != FixedQ4816.FromInteger(value: 15L)) ||
            (FixedQ4816.Parse(s: "1E5", style: NumberStyles.Any, provider: exponentLikeGroupFormat) != FixedQ4816.FromInteger(value: 15L)) ||
            !Throws<FormatException>(action: () => _ = FixedQ4816.One.ToString(format: "F2", formatProvider: CultureInfo.InvariantCulture))) {
            return PostStageOutcome.Fail(detail: "generic-math formatting provider or format rejection");
        }

        if (!Throws<OverflowException>(action: () => _ = AddChecked(left: FixedQ4816.MaxValue, right: FixedQ4816.One)) ||
            !Throws<OverflowException>(action: () => _ = SubtractChecked(left: FixedQ4816.MinValue, right: FixedQ4816.One)) ||
            !Throws<OverflowException>(action: () => _ = MultiplyChecked(left: FixedQ4816.MaxValue, right: FixedQ4816.FromInteger(value: 2L))) ||
            !Throws<OverflowException>(action: () => _ = DivideChecked(left: FixedQ4816.MinValue, right: FixedQ4816.NegativeOne)) ||
            !Throws<OverflowException>(action: () => _ = NegateChecked(value: FixedQ4816.MinValue)) ||
            !Throws<OverflowException>(action: () => _ = AddChecked(left: UFixedQ4816.MaxValue, right: UFixedQ4816.One)) ||
            !Throws<OverflowException>(action: () => _ = SubtractChecked(left: UFixedQ4816.Zero, right: UFixedQ4816.One)) ||
            !Throws<OverflowException>(action: () => _ = NegateChecked(value: UFixedQ4816.One)) ||
            (MultiplyChecked(left: FixedQ4816.FromDouble(value: 1.5d), right: FixedQ4816.FromInteger(value: 2L)) != FixedQ4816.FromInteger(value: 3L))) {
            return PostStageOutcome.Fail(detail: "checked generic arithmetic must trap overflow and preserve rounded in-range results");
        }

        var copySignOverflowed = false;

        try {
            _ = FixedQ4816.CopySign(value: FixedQ4816.MinValue, sign: FixedQ4816.One);
        } catch (OverflowException) {
            copySignOverflowed = true;
        }

        if (!copySignOverflowed ||
            (FixedQ4816.Parse(s: FixedQ4816.MaxValue.ToString(), provider: CultureInfo.InvariantCulture) != FixedQ4816.MaxValue) ||
            (UFixedQ4816.Parse(s: UFixedQ4816.MaxValue.ToString(), provider: CultureInfo.InvariantCulture) != UFixedQ4816.MaxValue) ||
            (FixedQ4816.Parse(s: "1,234.5", style: NumberStyles.Number, provider: CultureInfo.InvariantCulture) != FixedQ4816.FromDouble(value: 1234.5d)) ||
            (FixedQ4816.Parse(s: "100000000000000.00000762939453125000000000000001", provider: CultureInfo.InvariantCulture).Value != 6_553_600_000_000_000_001L) ||
            (FixedQ4816.Parse(s: "-100000000000000.00000762939453125000000000000001", provider: CultureInfo.InvariantCulture).Value != -6_553_600_000_000_000_001L) ||
            (UFixedQ4816.Parse(s: "100000000000000.00000762939453125000000000000001", provider: CultureInfo.InvariantCulture).Value != 6_553_600_000_000_000_001UL) ||
            (UFixedQ4816.Parse(s: "100,000,000,000,000.00000762939453125000000000000001", style: NumberStyles.Number, provider: CultureInfo.InvariantCulture).Value != 6_553_600_000_000_000_001UL) ||
            (FixedQ4816.Parse(s: "100000000000000.00000762939453125000000000000001e0", style: NumberStyles.Float, provider: CultureInfo.InvariantCulture).Value != 6_553_600_000_000_000_001L) ||
            !FixedQ4816.TryParse(s: "-140737488355328.00000762939453125", provider: CultureInfo.InvariantCulture, result: out var signedOuterTie) ||
            (signedOuterTie != FixedQ4816.MinValue) ||
            FixedQ4816.TryParse(s: "-140737488355328.0000076293945312500001", provider: CultureInfo.InvariantCulture, result: out _) ||
            FixedQ4816.TryParse(s: "140737488355327.99999237060546875", provider: CultureInfo.InvariantCulture, result: out _) ||
            UFixedQ4816.TryParse(s: "281474976710655.99999237060546875", style: NumberStyles.Number, provider: CultureInfo.InvariantCulture, result: out _) ||
            (UFixedQ0016.Parse(s: "0.00000762939453125000000000000001", provider: CultureInfo.InvariantCulture).Value != 1U) ||
            (UFixedQ0032.Parse(s: "0.000000000116415321826934814453126", provider: CultureInfo.InvariantCulture).Value != 1U) ||
            (FixedQ4816.MaxMagnitude(x: FixedQ4816.MinValue, y: FixedQ4816.MaxValue) != FixedQ4816.MinValue) ||
            (CreateChecked<FixedQ4816, UFixedQ4816>(value: new UFixedQ4816(Value: long.MaxValue)).Value != long.MaxValue) ||
            (CreateSaturating<FixedQ4816, UFixedQ4816>(value: UFixedQ4816.MaxValue) != FixedQ4816.MaxValue) ||
            (CreateSaturating<UFixedQ4816, FixedQ4816>(value: FixedQ4816.MinValue) != UFixedQ4816.Zero)) {
            return PostStageOutcome.Fail(detail: "generic-math boundary conversion or CopySign contract");
        }

        // Addition is an exact modular monoid. Rounded multiplication deliberately is not associative; pinning the
        // counter-law prevents future generic code from treating INumber as a proof of ring laws.
        var half = FixedQ4816.FromRawBits(value: 32768L);
        var twoForLaw = FixedQ4816.FromInteger(value: 2L);

        if ((((FixedQ4816.MaxValue + FixedQ4816.One) + FixedQ4816.NegativeOne) !=
             (FixedQ4816.MaxValue + (FixedQ4816.One + FixedQ4816.NegativeOne))) ||
            (((FixedQ4816.Epsilon * half) * twoForLaw) == (FixedQ4816.Epsilon * (half * twoForLaw)))) {
            return PostStageOutcome.Fail(detail: "fixed-point exact/counter-law contract");
        }

        double[] samples = [-7.5d, -2.25d, -0.5d, 0d, 0.5d, 1d, 2.25d, 3.75d, 8d, -8d,];

        foreach (var a in samples) {
            foreach (var b in samples) {
                var fa = FixedQ4816.FromDouble(value: a);
                var fb = FixedQ4816.FromDouble(value: b);

                if (!Close(actual: ((double)(fa + fb)), expected: (a + b), tolerance: 1e-3d)) { return PostStageOutcome.Fail(detail: $"add {a}+{b}"); }
                if (!Close(actual: ((double)(fa - fb)), expected: (a - b), tolerance: 1e-3d)) { return PostStageOutcome.Fail(detail: $"subtract {a}-{b}"); }
                if (!Close(actual: ((double)(fa * fb)), expected: (a * b), tolerance: 3e-3d)) { return PostStageOutcome.Fail(detail: $"multiply {a}*{b}"); }

                if ((b != 0d) && !Close(actual: ((double)(fa / fb)), expected: (a / b), tolerance: 3e-3d)) { return PostStageOutcome.Fail(detail: $"divide {a}/{b}"); }
            }
        }

        double[] roots = [0d, 0.25d, 1d, 2d, 4d, 64d, 123.456d, 0.0001d,];

        foreach (var value in roots) {
            if (!Close(actual: ((double)FixedQ4816.Sqrt(value: FixedQ4816.FromDouble(value: value))), expected: Math.Sqrt(d: value), tolerance: 1e-2d)) {
                return PostStageOutcome.Fail(detail: $"sqrt {value}");
            }
        }

        for (var step = 0; (step < 64); step++) {
            var angle = (-Math.PI + (step * ((2d * Math.PI) / 64d)));
            var actual = ((double)FixedQ4816.Atan2(y: FixedQ4816.FromDouble(value: Math.Sin(a: angle)), x: FixedQ4816.FromDouble(value: Math.Cos(d: angle))));
            var expected = Math.Atan2(y: Math.Sin(a: angle), x: Math.Cos(d: angle));

            if (Math.Abs(value: WrapPi(angle: (actual - expected))) > 0.02d) {
                return PostStageOutcome.Fail(detail: $"atan2 at {angle} rad");
            }
        }

        // SinCos agrees with the double reference (of the QUANTIZED angle) over two full turns, and inverts Atan2.
        for (var step = 0; (step <= 128); step++) {
            var angle = ((-2d * Math.PI) + (step * ((4d * Math.PI) / 128d)));
            var fixedAngle = FixedQ4816.FromDouble(value: angle);
            var quantized = ((double)fixedAngle);

            var (sin, cos) = FixedQ4816.SinCos(angle: fixedAngle);

            if (!Close(actual: ((double)sin), expected: Math.Sin(a: quantized), tolerance: 3e-5d) ||
                !Close(actual: ((double)cos), expected: Math.Cos(d: quantized), tolerance: 3e-5d)) {
                return PostStageOutcome.Fail(detail: $"sincos at {angle} rad");
            }
        }

        var roundTripAngle = FixedQ4816.Atan2(y: FixedQ4816.FromDouble(value: 0.6d), x: FixedQ4816.FromDouble(value: 0.8d));

        var (dirSin, dirCos) = FixedQ4816.SinCos(angle: roundTripAngle);

        if (!Close(actual: ((double)dirSin), expected: 0.6d, tolerance: 1e-3d) || !Close(actual: ((double)dirCos), expected: 0.8d, tolerance: 1e-3d)) {
            return PostStageOutcome.Fail(detail: "sincos(atan2) did not recover the unit direction");
        }

        // Log2: exact at powers of two; MinValue sentinel below the domain.
        double[] logSamples = [0.5d, 1d, 2d, 8d, 1024d, 3.75d, 0.001d,];

        foreach (var value in logSamples) {
            var fixedValue = FixedQ4816.FromDouble(value: value);

            if (!Close(actual: ((double)FixedQ4816.Log2(value: fixedValue)), expected: Math.Log2(x: ((double)fixedValue)), tolerance: 3e-5d)) {
                return PostStageOutcome.Fail(detail: $"log2 {value}");
            }
        }

        if (FixedQ4816.Log2(value: FixedQ4816.Zero).Value != long.MinValue) {
            return PostStageOutcome.Fail(detail: "log2 of a non-positive value must be MinValue");
        }

        // Exp2: exact whole-number powers, saturation policy, and the Pow composition (exact integer path).
        if (FixedQ4816.Exp2(value: FixedQ4816.FromInteger(value: 3L)).Value != (8L * 65536L)) {
            return PostStageOutcome.Fail(detail: "exp2(3) is not 8");
        }

        if (FixedQ4816.Exp2(value: FixedQ4816.FromInteger(value: -1L)).Value != 32768L) {
            return PostStageOutcome.Fail(detail: "exp2(-1) is not 0.5");
        }

        if (FixedQ4816.Exp2(value: FixedQ4816.FromInteger(value: 47L)).Value != long.MaxValue) {
            return PostStageOutcome.Fail(detail: "exp2(47) must saturate");
        }

        if (!Close(actual: ((double)FixedQ4816.Exp2(value: FixedQ4816.FromDouble(value: 5.5d))), expected: Math.Pow(x: 2d, y: 5.5d), tolerance: 1e-3d)) {
            return PostStageOutcome.Fail(detail: "exp2(5.5)");
        }

        if (FixedQ4816.Pow(x: FixedQ4816.FromInteger(value: 3L), y: FixedQ4816.FromInteger(value: 2L)).Value != (9L * 65536L)) {
            return PostStageOutcome.Fail(detail: "pow(3, 2) is not exactly 9");
        }

        if (!Close(actual: ((double)FixedQ4816.Pow(x: FixedQ4816.FromInteger(value: 2L), y: FixedQ4816.FromDouble(value: 0.5d))), expected: Math.Sqrt(d: 2d), tolerance: 1e-4d)) {
            return PostStageOutcome.Fail(detail: "pow(2, 0.5)");
        }

        var hugeExponent = FixedQ4816.FromInteger(value: (1L << 46));

        if ((FixedQ4816.Pow(x: FixedQ4816.FromInteger(value: 4L), y: hugeExponent) != FixedQ4816.MaxValue) ||
            (FixedQ4816.Pow(x: FixedQ4816.FromDouble(value: 0.25d), y: hugeExponent) != FixedQ4816.Zero) ||
            ((FixedQ4816.MinValue % FixedQ4816.FromRawBits(value: -1L)) != FixedQ4816.Zero)) {
            return PostStageOutcome.Fail(detail: "pow exponent overflow or fixed remainder edge regressed");
        }

        // Shuffle: the pinned permutation for a pinned seed, and no element lost.
        var shuffleRng = Pcg32XshRr.Create(state: 5UL, stream: 31UL);
        Span<int> deck = [0, 1, 2, 3, 4, 5, 6, 7,];

        shuffleRng.Shuffle(values: deck);

        ReadOnlySpan<int> expectedDeck = [0, 1, 3, 7, 2, 6, 4, 5,];

        for (var i = 0; (i < 8); i++) {
            if (deck[i] != expectedDeck[i]) {
                return PostStageOutcome.Fail(detail: "shuffle diverged from the pinned permutation");
            }
        }

        // Gaussian: seeded moments over 50k pairs; exactly two advances per pair.
        var gaussian = Pcg32XshRr.Create(state: 7UL, stream: 11UL);
        var gaussianTwin = Pcg32XshRr.Create(state: 7UL, stream: 11UL);
        var gaussianSum = 0d;
        var gaussianSumSquared = 0d;

        for (var n = 0; (n < 50_000); n++) {
            var (first, second) = gaussian.NextGaussianPair();

            gaussianSum += (((double)first) + ((double)second));
            gaussianSumSquared += (((double)first * (double)first) + ((double)second * (double)second));
        }

        var gaussianMean = (gaussianSum / 100_000d);
        var gaussianVariance = ((gaussianSumSquared / 100_000d) - (gaussianMean * gaussianMean));

        if ((Math.Abs(value: gaussianMean) > 0.02d) || (Math.Abs(value: (gaussianVariance - 1d)) > 0.03d)) {
            return PostStageOutcome.Fail(detail: $"gaussian moments: mean {gaussianMean}, variance {gaussianVariance}");
        }

        gaussianTwin.Advance(count: 100_000UL);

        if (gaussianTwin.State != gaussian.State) {
            return PostStageOutcome.Fail(detail: "gaussian pair must consume exactly two advances");
        }

        // Alias table: {1, 2, 7} frequencies from 100k samples; exactly two advances per sample.
        var aliasRng = Pcg32XshRr.Create(state: 3UL, stream: 9UL);
        var aliasTwin = Pcg32XshRr.Create(state: 3UL, stream: 9UL);
        var aliasTable = AliasTable.Create<int>(entries: [(0, 1UL), (1, 2UL), (2, 7UL),]);
        var aliasCounts = new long[3];

        for (var n = 0; (n < 100_000); n++) {
            aliasCounts[aliasTable.SampleIndex(generator: ref aliasRng)]++;
        }

        double[] aliasExpected = [0.1d, 0.2d, 0.7d,];

        for (var i = 0; (i < 3); i++) {
            if (Math.Abs(value: ((aliasCounts[i] / 100_000d) - aliasExpected[i])) > 0.01d) {
                return PostStageOutcome.Fail(detail: $"alias frequency of entry {i}: {(aliasCounts[i] / 100_000d)} vs {aliasExpected[i]}");
            }
        }

        aliasTwin.Advance(count: 200_000UL);

        if (aliasTwin.State != aliasRng.State) {
            return PostStageOutcome.Fail(detail: "alias sample must consume exactly two advances");
        }

        // The real-valued weight overload quantizes deterministically to the same distribution.
        var aliasDoubleRng = Pcg32XshRr.Create(state: 3UL, stream: 10UL);
        var aliasDoubleTable = AliasTable.Create<int>(entries: [(0, 0.1d), (1, 0.2d), (2, 0.7d),]);
        var aliasDoubleCounts = new long[3];

        for (var n = 0; (n < 100_000); n++) {
            aliasDoubleCounts[aliasDoubleTable.SampleIndex(generator: ref aliasDoubleRng)]++;
        }

        for (var i = 0; (i < 3); i++) {
            if (Math.Abs(value: ((aliasDoubleCounts[i] / 100_000d) - aliasExpected[i])) > 0.01d) {
                return PostStageOutcome.Fail(detail: $"alias double-weight frequency of entry {i}: {(aliasDoubleCounts[i] / 100_000d)} vs {aliasExpected[i]}");
            }
        }

        // Pcg32XshRr: the published reference vector for srandom(42, 54), the logarithmic advance, and an exact
        // snapshot restore that continues the sequence.
        var rng = Pcg32XshRr.Create(state: 42UL, stream: 54UL);
        uint[] referenceDraws = [0xa15c02b7U, 0x7b47f409U, 0xba1d3330U, 0x83d2f293U, 0xbfa4784bU, 0xcbed606eU,];

        foreach (var expected in referenceDraws) {
            if (rng.NextUInt32() != expected) {
                return PostStageOutcome.Fail(detail: "pcg32 reference vector diverged");
            }
        }

        var advanced = Pcg32XshRr.Create(state: 42UL, stream: 54UL);

        advanced.Advance(count: ((ulong)referenceDraws.Length));

        if (advanced.State != rng.State) {
            return PostStageOutcome.Fail(detail: "pcg32 advance disagrees with sequential draws");
        }

        var restored = Pcg32XshRr.FromRawBits(increment: rng.Increment, multiplier: rng.Multiplier, state: rng.State);

        if (restored.NextUInt32() != rng.NextUInt32()) {
            return PostStageOutcome.Fail(detail: "pcg32 snapshot restore did not continue the sequence");
        }

        // Field noise: bounded, continuous across lattice boundaries, and identical through both position overloads.
        var noiseProbe = Pcg32XshRr.Create(state: 17UL, stream: 5UL);

        for (var n = 0; (n < 5_000); n++) {
            var position = new FixedVector3(
                X: FixedQ4816.FromRawBits(value: unchecked((long)(((ulong)noiseProbe.NextUInt32()) << 9))),
                Y: FixedQ4816.FromRawBits(value: unchecked((long)(((ulong)noiseProbe.NextUInt32()) << 9))),
                Z: FixedQ4816.FromRawBits(value: unchecked((long)(((ulong)noiseProbe.NextUInt32()) << 9)))
            );
            var sample = FieldNoise.Sample(seed: 42UL, position: position).Value;

            if ((sample < -65536L) || (sample > 65536L)) {
                return PostStageOutcome.Fail(detail: "field noise left [-1, 1]");
            }

            if (FieldNoise.Sample(seed: 42UL, position: WorldCoord3.FromLocal(local: position)).Value != sample) {
                return PostStageOutcome.Fail(detail: "field noise overloads disagree");
            }
        }

        // The hierarchical overload must incorporate cell bits above the low 44 that fit a long whole-unit lattice
        // coordinate. Individual noise values may legitimately collide, so reject only a full-field alias across
        // several observable probes separated by the 2^44-cell boundary.
        var aliasPeriod = (1L << (64 - WorldCoord3.CellSizeLog2));
        var allAliased = true;

        for (var probe = 0; (probe < 8); probe++) {
            var local = new FixedVector3(
                X: FixedQ4816.FromRawBits(value: ((probe * 7919L) - 20000L)),
                Y: FixedQ4816.FromRawBits(value: ((probe * 3571L) + 1234L)),
                Z: FixedQ4816.FromRawBits(value: ((probe * -421L) + 4321L))
            );
            var originCellNoise = FieldNoise.Sample(seed: ((ulong)(42 + probe)), position: new WorldCoord3(CellX: 0L, CellY: 0L, CellZ: 0L, Local: local));
            var farCellNoise = FieldNoise.Sample(seed: ((ulong)(42 + probe)), position: new WorldCoord3(CellX: aliasPeriod, CellY: 0L, CellZ: 0L, Local: local));

            allAliased &= (originCellNoise == farCellNoise);
        }

        if (allAliased) {
            return PostStageOutcome.Fail(detail: "field noise discards high WorldCoord3 cell bits");
        }

        // Hash axes and seeds must be domain-separated: the former linear combine admitted this exact short lattice
        // period and made seed+CombineX a translated copy of the original field.
        const long noisePeriodX = 852_863L;
        const long noisePeriodY = 1_285_698L;
        const long noisePeriodZ = 183_727L;
        const ulong formerCombineX = 0x9E3779B97F4A7C15UL;
        var periodStillAliases = true;
        var seedStillTranslates = true;

        for (var probe = 0; (probe < 8); probe++) {
            var probePosition = new FixedVector3(
                X: FixedQ4816.FromRawBits(value: ((probe * 65_537L) + 12_345L)),
                Y: FixedQ4816.FromRawBits(value: ((probe * -31_337L) + 22_222L)),
                Z: FixedQ4816.FromRawBits(value: ((probe * 9_973L) - 33_333L))
            );
            var periodPosition = new FixedVector3(
                X: FixedQ4816.FromRawBits(value: (probePosition.X.Value + (noisePeriodX << FixedQ4816.FractionBitCount))),
                Y: FixedQ4816.FromRawBits(value: (probePosition.Y.Value + (noisePeriodY << FixedQ4816.FractionBitCount))),
                Z: FixedQ4816.FromRawBits(value: (probePosition.Z.Value + (noisePeriodZ << FixedQ4816.FractionBitCount)))
            );
            var translatedPosition = probePosition + new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
            var probeSeed = unchecked((ulong)(97 + probe));

            periodStillAliases &= (FieldNoise.Sample(seed: probeSeed, position: probePosition) == FieldNoise.Sample(seed: probeSeed, position: periodPosition));
            seedStillTranslates &= (FieldNoise.Sample(seed: unchecked(probeSeed + formerCombineX), position: probePosition) == FieldNoise.Sample(seed: probeSeed, position: translatedPosition));
        }

        if (periodStillAliases || seedStillTranslates) {
            return PostStageOutcome.Fail(detail: "field noise retained a linear lattice period or translated-seed alias");
        }

        var octaveSeamRaw = (1L << 62);
        var octaveSeamLeft = FieldNoise.Sample(
            seed: 42UL,
            position: new FixedVector3(X: FixedQ4816.FromRawBits(value: (octaveSeamRaw - 1L)), Y: FixedQ4816.FromRawBits(value: 17_123L), Z: FixedQ4816.FromRawBits(value: -9_321L)),
            octaves: 5
        );
        var octaveSeamRight = FieldNoise.Sample(
            seed: 42UL,
            position: new FixedVector3(X: FixedQ4816.FromRawBits(value: octaveSeamRaw), Y: FixedQ4816.FromRawBits(value: 17_123L), Z: FixedQ4816.FromRawBits(value: -9_321L)),
            octaves: 5
        );

        if (Math.Abs(value: (octaveSeamRight.Value - octaveSeamLeft.Value)) > 16L) {
            return PostStageOutcome.Fail(detail: "field-noise octave frequency wrap introduced a discontinuity");
        }

        // Equivalent wide hierarchical representations must still address the same field point across a cell carry.
        var wideCell = (1L << 50);
        var wide = new WorldCoord3(CellX: wideCell, CellY: -wideCell, CellZ: wideCell, Local: FixedVector3.Zero);
        var wideRebased = new WorldCoord3(
            CellX: (wideCell + 1L),
            CellY: (-wideCell - 1L),
            CellZ: (wideCell + 1L),
            Local: new FixedVector3(
                X: FixedQ4816.FromRawBits(value: -(1L << (WorldCoord3.CellSizeLog2 + FixedQ4816.FractionBitCount))),
                Y: FixedQ4816.FromRawBits(value: (1L << (WorldCoord3.CellSizeLog2 + FixedQ4816.FractionBitCount))),
                Z: FixedQ4816.FromRawBits(value: -(1L << (WorldCoord3.CellSizeLog2 + FixedQ4816.FractionBitCount)))
            )
        );

        if (FieldNoise.Sample(seed: 91UL, position: wide) != FieldNoise.Sample(seed: 91UL, position: wideRebased)) {
            return PostStageOutcome.Fail(detail: "field noise changed across an equivalent wide cell rebase");
        }

        for (var k = -32L; (k <= 32L); k++) {
            var left = FieldNoise.Sample(seed: 42UL, position: new FixedVector3(X: FixedQ4816.FromRawBits(value: ((k << 16) - 1L)), Y: FixedQ4816.FromRawBits(value: (k << 14)), Z: FixedQ4816.FromRawBits(value: (k << 12)))).Value;
            var right = FieldNoise.Sample(seed: 42UL, position: new FixedVector3(X: FixedQ4816.FromRawBits(value: (k << 16)), Y: FixedQ4816.FromRawBits(value: (k << 14)), Z: FixedQ4816.FromRawBits(value: (k << 12)))).Value;

            if (Math.Abs(value: (left - right)) > 16L) {
                return PostStageOutcome.Fail(detail: $"field noise discontinuity at lattice x = {k}");
            }
        }

        // Low discrepancy: R2's first 4096 points cover an 8x8 grid evenly.
        var coverage = new int[8, 8];

        for (var n = 0UL; (n < 4096UL); n++) {
            var (px, py) = LowDiscrepancy.R2(index: n);

            coverage[(px.Value >> 29), (py.Value >> 29)]++;
        }

        for (var bx = 0; (bx < 8); bx++) {
            for (var by = 0; (by < 8); by++) {
                if (Math.Abs(value: (coverage[bx, by] - 64)) > 16) {
                    return PostStageOutcome.Fail(detail: $"r2 coverage box ({bx},{by}) = {coverage[bx, by]}");
                }
            }
        }

        if (LowDiscrepancy.R1(index: 0UL) == LowDiscrepancy.R1(index: (1UL << 63))) {
            return PostStageOutcome.Fail(detail: "r1 Weyl phase repeats after only 2^63 indices");
        }

        var signedShortPair = BinaryIntegerFunctions.BitwisePair<short, uint>(value: short.MinValue, other: 0);
        var signedLongPair = BinaryIntegerFunctions.BitwisePair<long, Int128>(value: long.MinValue, other: 0L);

        if ((signedShortPair != (1U << 30)) ||
            (signedLongPair != (Int128.One << 126)) ||
            (BinaryIntegerFunctions.BitwiseUnpair<Int128, long>(value: signedLongPair) != (long.MinValue, 0L))) {
            return PostStageOutcome.Fail(detail: "signed Morton pairing polluted bits or failed to invert");
        }

        // Quaternion: a quarter turn about +Y maps +X to −Z, rotation preserves length, and q·conj(q) ≈ identity.
        var quarterY = FixedQuaternion.FromAxisAngle(
            axis: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero),
            angle: FixedQ4816.FromDouble(value: (Math.PI / 2d)));
        var rotatedX = quarterY.Rotate(vector: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));

        if (!Close(actual: ((double)rotatedX.Z), expected: -1d, tolerance: 1e-3d) || !Close(actual: ((double)rotatedX.X), expected: 0d, tolerance: 1e-3d)) {
            return PostStageOutcome.Fail(detail: "quaternion quarter turn about Y did not map X to -Z");
        }

        if (!Close(actual: ((double)rotatedX.Length), expected: 1d, tolerance: 1e-3d)) {
            return PostStageOutcome.Fail(detail: "quaternion rotation did not preserve length");
        }

        if (!Close(actual: ((double)(quarterY * quarterY.Conjugate()).W), expected: 1d, tolerance: 1e-3d)) {
            return PostStageOutcome.Fail(detail: "quaternion times conjugate is not identity");
        }

        // Exp/Log: the quarter turn's log is the +Y bivector at the quarter-angle π/4, the round trip recovers the
        // rotation, and the zero bivector maps to the identity exactly.
        var logY = quarterY.Log();

        if (!Close(actual: ((double)logY.Y), expected: (Math.PI / 4d), tolerance: 1e-3d) || !Close(actual: ((double)logY.X), expected: 0d, tolerance: 1e-3d)) {
            return PostStageOutcome.Fail(detail: "quaternion log of the quarter turn is not the Y bivector at pi/4");
        }

        var expLogRoundTrip = FixedQuaternion.Exp(bivector: logY);

        if (!Close(actual: ((double)expLogRoundTrip.Y), expected: ((double)quarterY.Y), tolerance: 1e-3d) || !Close(actual: ((double)expLogRoundTrip.W), expected: ((double)quarterY.W), tolerance: 1e-3d)) {
            return PostStageOutcome.Fail(detail: "quaternion exp did not invert log");
        }

        if (FixedQuaternion.Exp(bivector: FixedVector3.Zero) != FixedQuaternion.Identity) {
            return PostStageOutcome.Fail(detail: "quaternion exp of the zero bivector is not the identity");
        }

        // Exp far beyond π: the wrapped angle agrees with the double reference and the result stays unit — the
        // axis must survive magnitudes where a Q16 sin/θ quotient would quantize to zero.
        var farSpin = FixedQuaternion.Exp(bivector: new FixedVector3(X: FixedQ4816.FromInteger(value: 131072L), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));
        var farSpinNorm = Math.Sqrt(d:
            (((double)farSpin.X) * ((double)farSpin.X)) + (((double)farSpin.Y) * ((double)farSpin.Y)) +
            (((double)farSpin.Z) * ((double)farSpin.Z)) + (((double)farSpin.W) * ((double)farSpin.W)));

        if (!Close(actual: farSpinNorm, expected: 1d, tolerance: 1e-3d) || !Close(actual: ((double)farSpin.X), expected: Math.Sin(a: 131072d), tolerance: 1e-3d)) {
            return PostStageOutcome.Fail(detail: "quaternion exp at a large magnitude is not the wrapped unit rotation");
        }

        // Exp with a multi-axis norm beyond the signed Q48.16 carrier: the phase must reduce from the unsaturated
        // 64-bit magnitude (components 90e12 and 120e12 have an exact norm of 150e12).
        var wideSpin = FixedQuaternion.Exp(bivector: new FixedVector3(
            X: FixedQ4816.FromInteger(value: 90_000_000_000_000L),
            Y: FixedQ4816.FromInteger(value: 120_000_000_000_000L),
            Z: FixedQ4816.Zero));

        if (!Close(actual: ((double)wideSpin.X), expected: (0.6d * Math.Sin(a: 150_000_000_000_000d)), tolerance: 1e-3d) ||
            !Close(actual: ((double)wideSpin.Y), expected: (0.8d * Math.Sin(a: 150_000_000_000_000d)), tolerance: 1e-3d) ||
            !Close(actual: ((double)wideSpin.W), expected: Math.Cos(d: 150_000_000_000_000d), tolerance: 1e-3d)) {
            return PostStageOutcome.Fail(detail: "quaternion exp with a norm beyond the Q48.16 carrier phased through a saturated angle");
        }

        // Rigid exp at the same large-angle regime: dual motion perpendicular to the axis must survive the sin/θ
        // quotient (dual⊥ · sinθ/θ with dual = axis·θ-sized magnitude lands at sin θ).
        var perpendicularScrew = FixedRigidTransform.Exp(
            real: new FixedVector3(X: FixedQ4816.FromInteger(value: 131072L), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            dual: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromInteger(value: 131072L), Z: FixedQ4816.Zero));

        if (!Close(actual: ((double)perpendicularScrew.Value.Dual.Y), expected: Math.Sin(a: 131072d), tolerance: 1e-3d)) {
            return PostStageOutcome.Fail(detail: "rigid exp at a large angle dropped the perpendicular dual motion");
        }

        // The normalization branch boundary: a squared sum just below 2^96 rounds its Q16-scaled root up to exactly
        // 2^64, which must route to the wide branch instead of zeroing the denominator.
        var boundarySpin = FixedQuaternion.Exp(bivector: new FixedVector3(
            X: FixedQ4816.FromRawBits(value: ((1L << 48) - 1L)),
            Y: FixedQ4816.FromRawBits(value: (1L << 24)),
            Z: FixedQ4816.FromRawBits(value: ((1L << 24) - 1L))));
        var boundaryNorm = Math.Sqrt(d:
            (((double)boundarySpin.X) * ((double)boundarySpin.X)) + (((double)boundarySpin.Y) * ((double)boundarySpin.Y)) +
            (((double)boundarySpin.Z) * ((double)boundarySpin.Z)) + (((double)boundarySpin.W) * ((double)boundarySpin.W)));

        if (!Close(actual: boundaryNorm, expected: 1d, tolerance: 1e-3d)) {
            return PostStageOutcome.Fail(detail: "quaternion exp at the normalization branch boundary is not unit");
        }

        // A giant dual exactly perpendicular to a giant rotation bivector: the half slide comes from the exact
        // bivector·dual product, so no spurious axial motion may appear.
        var perpendicularGiant = FixedRigidTransform.Exp(
            real: new FixedVector3(X: FixedQ4816.FromInteger(value: 90_000_000_000_000L), Y: FixedQ4816.FromInteger(value: 120_000_000_000_000L), Z: FixedQ4816.Zero),
            dual: new FixedVector3(X: FixedQ4816.FromInteger(value: -120_000_000_000_000L), Y: FixedQ4816.FromInteger(value: 90_000_000_000_000L), Z: FixedQ4816.Zero));

        if (!Close(actual: ((double)perpendicularGiant.Value.Dual.X), expected: (-0.8d * Math.Sin(a: 150_000_000_000_000d)), tolerance: 1e-3d) ||
            !Close(actual: ((double)perpendicularGiant.Value.Dual.W), expected: 0d, tolerance: 1e-3d)) {
            return PostStageOutcome.Fail(detail: "rigid exp manufactured slide from a perpendicular giant dual");
        }

        // Parallel giants: the exact bivector·dual sum needs 129 signed bits and the half slide exceeds the signed
        // carrier — signed-magnitude accumulation must keep every representable output correct. The 1:2:2:3
        // quadruple gives the norm as the exact integer 210e12, so the double trig reference carries no
        // irrational-phase error (ratio compare; the tolerance covers only the documented turn-reduction error).
        var parallelGiant = FixedRigidTransform.Exp(
            real: new FixedVector3(X: FixedQ4816.FromInteger(value: 70_000_000_000_000L), Y: FixedQ4816.FromInteger(value: 140_000_000_000_000L), Z: FixedQ4816.FromInteger(value: 140_000_000_000_000L)),
            dual: new FixedVector3(X: FixedQ4816.FromInteger(value: 70_000_000_000_000L), Y: FixedQ4816.FromInteger(value: 140_000_000_000_000L), Z: FixedQ4816.FromInteger(value: 140_000_000_000_000L)));

        // Tolerances cover the representation itself: the Q16 axis quantization (~2⁻¹⁷ relative) on the vector
        // ratio, and the half-ULP sine quantization amplified by 1/sin ≈ 7.8 on the W ratio. The overflow this
        // pins produced ratios near 0.5.
        if (!Close(actual: (((double)parallelGiant.Value.Dual.X) / (70_000_000_000_000d * Math.Cos(d: 210_000_000_000_000d))), expected: 1d, tolerance: 2e-4d) ||
            !Close(actual: (((double)parallelGiant.Value.Dual.W) / (-210_000_000_000_000d * Math.Sin(a: 210_000_000_000_000d))), expected: 1d, tolerance: 1e-3d)) {
            return PostStageOutcome.Fail(detail: "rigid exp wrapped the wide axial slide of parallel giants");
        }

        // Direct-rounded phase: the norm of (40001, 200, 1) raw rounds to 40001, not through the denominator's
        // second rounding to 40002.
        if (FixedQuaternion.Exp(bivector: new FixedVector3(
            X: FixedQ4816.FromRawBits(value: 40001L),
            Y: FixedQ4816.FromRawBits(value: 200L),
            Z: FixedQ4816.FromRawBits(value: 1L))).W != FixedQ4816.Cos(angle: FixedQ4816.FromRawBits(value: 40001L))) {
            return PostStageOutcome.Fail(detail: "normalization double-rounded the phase magnitude");
        }

        // FromTo: the shortest-arc constructor maps +X onto +Z.
        var xToZ = FixedQuaternion.FromTo(
            from: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            to: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One));
        var mappedX = xToZ.Rotate(vector: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));

        if (!Close(actual: ((double)mappedX.Z), expected: 1d, tolerance: 1e-3d) || !Close(actual: ((double)mappedX.X), expected: 0d, tolerance: 1e-3d)) {
            return PostStageOutcome.Fail(detail: "quaternion fromto did not map X to Z");
        }

        // Wedge: the unit square has signed area one, self-wedge vanishes, and the fused planar dot is exact.
        var unitX = new FixedVector2(X: FixedQ4816.One, Y: FixedQ4816.Zero);
        var unitY = new FixedVector2(X: FixedQ4816.Zero, Y: FixedQ4816.One);
        var threeFour = new FixedVector2(X: FixedQ4816.FromInteger(value: 3L), Y: FixedQ4816.FromInteger(value: 4L));

        if (FixedVector2.Wedge(left: unitX, right: unitY) != FixedQ4816.One) {
            return PostStageOutcome.Fail(detail: "wedge of the unit square is not one");
        }

        if (FixedVector2.Wedge(left: threeFour, right: threeFour) != FixedQ4816.Zero) {
            return PostStageOutcome.Fail(detail: "self-wedge is not zero");
        }

        if (FixedVector2.Dot(left: threeFour, right: threeFour) != FixedQ4816.FromInteger(value: 25L)) {
            return PostStageOutcome.Fail(detail: "planar dot of (3,4) with itself is not 25");
        }

        // Raw Q32 accumulation must not overflow at ~46k world units; the rounded Q48.16 result still has ample range.
        var wideComponent = FixedQ4816.FromInteger(value: 46_341L);
        var wideVector = new FixedVector2(X: wideComponent, Y: FixedQ4816.Zero);
        var wideSquare = (wideComponent * wideComponent);

        if ((FixedVector2.Dot(left: wideVector, right: wideVector) != wideSquare) || (wideVector.LengthSquared != wideSquare)) {
            return PostStageOutcome.Fail(detail: "planar dot overflowed its fused product accumulator");
        }

        var tinyDiagonal = new FixedVector2(X: FixedQ4816.Epsilon, Y: FixedQ4816.Epsilon);
        var fullRangeAxisComponent = FixedQ4816.FromRawBits(value: (1L << 40));
        var fullRangeAxis = new FixedVector2(X: fullRangeAxisComponent, Y: FixedQ4816.Zero);

        if ((tinyDiagonal.Length != FixedQ4816.Epsilon) ||
            !tinyDiagonal.TryLength(length: out var tinyDiagonalLength) ||
            (tinyDiagonalLength != FixedQ4816.Epsilon) ||
            !fullRangeAxis.TryLength(length: out var fullRangeAxisLength) ||
            (fullRangeAxisLength != fullRangeAxisComponent) ||
            fullRangeAxis.TryLengthSquared(squaredLength: out _) ||
            (fullRangeAxis.LengthSquared != FixedQ4816.MaxValue)) {
            return PostStageOutcome.Fail(detail: "planar norm did not preserve tiny or full-range magnitudes");
        }

        var wideDiagonal = new FixedVector2(X: wideComponent, Y: wideComponent);
        var wideCounterDiagonal = new FixedVector2(X: -wideComponent, Y: wideComponent);

        if (FixedVector2.Wedge(left: wideDiagonal, right: wideCounterDiagonal) != (wideSquare + wideSquare)) {
            return PostStageOutcome.Fail(detail: "planar wedge overflowed its fused product accumulator");
        }

        // FromTo (2D): the rotor from +X to +Y is the quarter turn i.
        var planarQuarter = FixedComplex.FromTo(from: unitX, to: unitY);

        if (!Close(actual: ((double)planarQuarter.Imaginary), expected: 1d, tolerance: 1e-3d) || !Close(actual: ((double)planarQuarter.Real), expected: 0d, tolerance: 1e-3d)) {
            return PostStageOutcome.Fail(detail: "planar fromto of X to Y is not the quarter turn");
        }

        var epsilonX = new FixedVector2(X: FixedQ4816.Epsilon, Y: FixedQ4816.Zero);
        var epsilonNegativeX = new FixedVector2(X: -FixedQ4816.Epsilon, Y: FixedQ4816.Zero);
        var epsilonNegativeY = new FixedVector2(X: FixedQ4816.Zero, Y: -FixedQ4816.Epsilon);
        var epsilonHalfTurn = FixedComplex.FromTo(from: epsilonX, to: epsilonNegativeX);
        var epsilonClockwiseQuarter = FixedComplex.FromTo(from: epsilonX, to: epsilonNegativeY);

        if ((epsilonHalfTurn != new FixedComplex(Real: FixedQ4816.NegativeOne, Imaginary: FixedQ4816.Zero)) ||
            (epsilonClockwiseQuarter != new FixedComplex(Real: FixedQ4816.Zero, Imaginary: FixedQ4816.NegativeOne)) ||
            (new FixedComplex(Real: FixedQ4816.Epsilon, Imaginary: FixedQ4816.Zero).Normalize() != FixedComplex.MultiplicativeIdentity)) {
            return PostStageOutcome.Fail(detail: "complex scale-safe direction handling");
        }

        var wideComplex = new FixedComplex(
            Real: FixedQ4816.FromRawBits(value: (1L << 32)),
            Imaginary: FixedQ4816.FromRawBits(value: long.MinValue));

        var wideComplexIdentity = (wideComplex / wideComplex);
        var signSymmetryComplex = new FixedComplex(
            Real: FixedQ4816.FromRawBits(value: long.MaxValue),
            Imaginary: FixedQ4816.FromRawBits(value: (1L << 46)));

        if ((wideComplexIdentity != FixedComplex.MultiplicativeIdentity) ||
            ((-signSymmetryComplex).Normalize() != -signSymmetryComplex.Normalize())) {
            return PostStageOutcome.Fail(detail: $"complex division overflowed its full-width denominator ({wideComplexIdentity.Real.Value}, {wideComplexIdentity.Imaginary.Value})");
        }

        var extremeQuaternion = new FixedQuaternion(
            X: FixedQ4816.FromRawBits(value: long.MaxValue),
            Y: FixedQ4816.FromRawBits(value: (1L << 46)),
            Z: FixedQ4816.Zero,
            W: FixedQ4816.Zero);
        var normalizedExtreme = extremeQuaternion.Normalize();
        var minimumBasis = new FixedQuaternion(
            X: FixedQ4816.FromRawBits(value: long.MinValue),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero,
            W: FixedQ4816.Zero).Normalize();
        var allMaximum = new FixedQuaternion(
            X: FixedQ4816.MaxValue,
            Y: FixedQ4816.MaxValue,
            Z: FixedQ4816.MaxValue,
            W: FixedQ4816.MaxValue).Normalize();
        var expectedHalfQuaternion = new FixedQuaternion(
            X: FixedQ4816.FromRawBits(value: 32768L),
            Y: FixedQ4816.FromRawBits(value: 32768L),
            Z: FixedQ4816.FromRawBits(value: 32768L),
            W: FixedQ4816.FromRawBits(value: 32768L));

        if ((-extremeQuaternion).Normalize() != -normalizedExtreme ||
            (normalizedExtreme != new FixedQuaternion(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero, W: FixedQ4816.Zero)) ||
            (minimumBasis != new FixedQuaternion(X: FixedQ4816.NegativeOne, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero, W: FixedQ4816.Zero)) ||
            (allMaximum != expectedHalfQuaternion) ||
            (new FixedQuaternion(X: FixedQ4816.Epsilon, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero, W: FixedQ4816.Zero).Normalize() !=
             new FixedQuaternion(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero, W: FixedQ4816.Zero))) {
            return PostStageOutcome.Fail(detail: "quaternion scale-safe normalization or sign symmetry");
        }

        // Complex: a quarter-turn rotation maps (1, 0) to (0, 1); multiplication composes angles.
        var quarterComplex = FixedComplex.FromAngle(angle: FixedQ4816.FromDouble(value: (Math.PI / 2d)));
        var rotated2d = quarterComplex.Rotate(vector: new FixedVector2(X: FixedQ4816.One, Y: FixedQ4816.Zero));

        if (!Close(actual: ((double)rotated2d.Y), expected: 1d, tolerance: 1e-3d) || !Close(actual: ((double)rotated2d.X), expected: 0d, tolerance: 1e-3d)) {
            return PostStageOutcome.Fail(detail: "complex quarter turn did not map (1,0) to (0,1)");
        }

        var composedAngle = (FixedComplex.FromAngle(angle: FixedQ4816.FromDouble(value: 0.4d)) * FixedComplex.FromAngle(angle: FixedQ4816.FromDouble(value: 0.7d))).Argument;

        if (!Close(actual: ((double)composedAngle), expected: 1.1d, tolerance: 1e-3d)) {
            return PostStageOutcome.Fail(detail: "complex multiplication did not compose angles");
        }

        // Rigid transform: rotate a quarter turn about Y then translate; composition matches sequential application.
        var rigid = FixedRigidTransform.FromRotationTranslation(
            rotation: quarterY,
            translation: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.FromInteger(value: 2L), Z: FixedQ4816.FromInteger(value: 3L)));
        var transformed = rigid.TransformPoint(point: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));

        if (!Close(actual: ((double)transformed.X), expected: 1d, tolerance: 1e-2d) ||
            !Close(actual: ((double)transformed.Y), expected: 2d, tolerance: 1e-2d) ||
            !Close(actual: ((double)transformed.Z), expected: 2d, tolerance: 1e-2d)) {
            return PostStageOutcome.Fail(detail: "rigid transform of (1,0,0) is not (1,2,2)");
        }

        // Factory boundaries normalize non-unit rotations rather than letting scale corrupt translation and point action.
        var scaledRigid = FixedRigidTransform.FromRotationTranslation(
            rotation: (quarterY * FixedQ4816.FromInteger(value: 2L)),
            translation: rigid.Translation);
        var scaledTransformed = scaledRigid.TransformPoint(point: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));

        if (
            !Close(actual: ((double)scaledTransformed.X), expected: ((double)transformed.X), tolerance: 1e-3d) ||
            !Close(actual: ((double)scaledTransformed.Y), expected: ((double)transformed.Y), tolerance: 1e-3d) ||
            !Close(actual: ((double)scaledTransformed.Z), expected: ((double)transformed.Z), tolerance: 1e-3d)
        ) {
            return PostStageOutcome.Fail(detail: "rigid factory did not normalize a scaled rotation");
        }

        var two = FixedQ4816.FromInteger(value: 2L);
        var parallelBias = (rigid.Value.Real * FixedQ4816.FromRawBits(value: 8192L));
        var normalizedDual = FixedRigidTransform.FromDualQuaternion(value: new(
            Real: (rigid.Value.Real * two),
            Dual: ((rigid.Value.Dual * two) + parallelBias)
        ));
        var normalizedDualPoint = normalizedDual.TransformPoint(point: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));

        if (
            !Close(actual: ((double)normalizedDualPoint.X), expected: ((double)transformed.X), tolerance: 1e-3d) ||
            !Close(actual: ((double)normalizedDualPoint.Y), expected: ((double)transformed.Y), tolerance: 1e-3d) ||
            !Close(actual: ((double)normalizedDualPoint.Z), expected: ((double)transformed.Z), tolerance: 1e-3d) ||
            !SatisfiesRigidStudyCondition(value: normalizedDual)
        ) {
            return PostStageOutcome.Fail(detail: "dual-quaternion boundary factory did not restore a rigid transform");
        }

        if (FixedRigidTransform.TryFromDualQuaternion(value: default, result: out var invalidRigid) || (invalidRigid != FixedRigidTransform.Identity)) {
            return PostStageOutcome.Fail(detail: "degenerate dual quaternion was accepted as a rigid transform");
        }

        var tinyRealDualQuaternion = new FixedDual<FixedQuaternion>(
            Real: new FixedQuaternion(
                X: FixedQ4816.Epsilon,
                Y: FixedQ4816.Epsilon,
                Z: FixedQ4816.Zero,
                W: FixedQ4816.Zero
            ),
            Dual: FixedQuaternion.AdditiveIdentity
        );

        if (!FixedRigidTransform.TryFromDualQuaternion(value: tinyRealDualQuaternion, result: out var tinyNormalizedRigid) ||
            !Close(actual: (double)tinyNormalizedRigid.Value.Real.X, expected: Math.Sqrt(d: 0.5d), tolerance: 2e-5d) ||
            !Close(actual: (double)tinyNormalizedRigid.Value.Real.Y, expected: Math.Sqrt(d: 0.5d), tolerance: 2e-5d) ||
            !Close(actual: (double)tinyNormalizedRigid.Value.Real.LengthSquared, expected: 1d, tolerance: 4e-5d)) {
            return PostStageOutcome.Fail(detail: "rigid boundary normalization lost a tiny nonzero real quaternion");
        }

        var rigidSquared = (rigid * rigid);
        var composedPoint = rigidSquared.TransformPoint(point: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));
        var sequentialPoint = rigid.TransformPoint(point: transformed);

        if (!Close(actual: ((double)composedPoint.X), expected: ((double)sequentialPoint.X), tolerance: 1e-2d)) {
            return PostStageOutcome.Fail(detail: "rigid composition disagrees with sequential application");
        }

        var normalizedChain = FixedRigidTransform.Identity;

        for (var i = 0; (i < 256); ++i) {
            normalizedChain = FixedRigidTransform.ComposeNormalized(left: normalizedChain, right: rigid);
        }

        if (
            !Close(actual: ((double)normalizedChain.Rotation.LengthSquared), expected: 1d, tolerance: 2e-3d) ||
            !SatisfiesRigidStudyCondition(value: normalizedChain)
        ) {
            return PostStageOutcome.Fail(detail: "normalized rigid composition did not bound the unit/Study constraints");
        }

        // Screw exp/log: the roundtrip reproduces the transform's action.
        var (screwReal, screwDual) = rigid.Log();
        var screwBack = FixedRigidTransform.Exp(real: screwReal, dual: screwDual).TransformPoint(point: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));

        if (!Close(actual: ((double)screwBack.X), expected: ((double)transformed.X), tolerance: 1e-2d) ||
            !Close(actual: ((double)screwBack.Z), expected: ((double)transformed.Z), tolerance: 1e-2d)) {
            return PostStageOutcome.Fail(detail: "rigid exp did not invert log");
        }

        // Dual numbers: d(x^2)/dx at 3 is exactly 6; d(sqrt(x))/dx at 4 is exactly 1/4.
        var dualThree = FixedDual.Variable(value: FixedQ4816.FromInteger(value: 3L));

        if ((dualThree * dualThree).Dual.Value != (6L * 65536L)) {
            return PostStageOutcome.Fail(detail: "dual derivative of x^2 at 3 is not 6");
        }

        if (FixedDual.Sqrt(value: FixedDual.Variable(value: FixedQ4816.FromInteger(value: 4L))).Dual.Value != 16384L) {
            return PostStageOutcome.Fail(detail: "dual derivative of sqrt at 4 is not 1/4");
        }

        // Banker's rounding: ties go to the nearest even integer, symmetrically for both signs.
        if (!RoundsTo(value: 2.5d, expected: 2d) || !RoundsTo(value: 3.5d, expected: 4d) ||
            !RoundsTo(value: -2.5d, expected: -2d) || !RoundsTo(value: -3.5d, expected: -4d) ||
            !RoundsTo(value: 0.5d, expected: 0d) || !RoundsTo(value: -0.5d, expected: 0d)) {
            return PostStageOutcome.Fail(detail: "round half-to-even");
        }

        // Sign / CopySign / Lerp: sign trichotomy, magnitude-with-borrowed-sign (zero counts positive), and an
        // interpolation that lands its endpoints exactly and interpolates the midpoint.
        if ((FixedQ4816.Sign(value: FixedQ4816.FromDouble(value: -3.5d)) != -1) ||
            (FixedQ4816.Sign(value: FixedQ4816.Zero) != 0) ||
            (FixedQ4816.Sign(value: FixedQ4816.FromDouble(value: 0.001d)) != 1)) {
            return PostStageOutcome.Fail(detail: "sign trichotomy");
        }

        if ((FixedQ4816.CopySign(value: FixedQ4816.FromInteger(value: 5L), sign: FixedQ4816.NegativeOne) != FixedQ4816.FromInteger(value: -5L)) ||
            (FixedQ4816.CopySign(value: FixedQ4816.FromInteger(value: -5L), sign: FixedQ4816.One) != FixedQ4816.FromInteger(value: 5L)) ||
            (FixedQ4816.CopySign(value: FixedQ4816.FromInteger(value: -5L), sign: FixedQ4816.Zero) != FixedQ4816.FromInteger(value: 5L))) {
            return PostStageOutcome.Fail(detail: "copysign magnitude/sign");
        }

        var lerpFrom = FixedQ4816.FromInteger(value: -4L);
        var lerpTo = FixedQ4816.FromInteger(value: 12L);

        if ((FixedQ4816.Lerp(from: lerpFrom, to: lerpTo, amount: FixedQ4816.Zero) != lerpFrom) ||
            (FixedQ4816.Lerp(from: lerpFrom, to: lerpTo, amount: FixedQ4816.One) != lerpTo) ||
            (FixedQ4816.Lerp(from: lerpFrom, to: lerpTo, amount: FixedQ4816.FromDouble(value: 0.5d)) != FixedQ4816.FromInteger(value: 4L))) {
            return PostStageOutcome.Fail(detail: "scalar lerp endpoints/midpoint");
        }

        var lerpVector = FixedVector3.Lerp(
            from: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromInteger(value: 2L), Z: FixedQ4816.FromInteger(value: -6L)),
            to: new FixedVector3(X: FixedQ4816.FromInteger(value: 8L), Y: FixedQ4816.FromInteger(value: 2L), Z: FixedQ4816.FromInteger(value: 6L)),
            amount: FixedQ4816.FromDouble(value: 0.25d));

        if (lerpVector != new FixedVector3(X: FixedQ4816.FromInteger(value: 2L), Y: FixedQ4816.FromInteger(value: 2L), Z: FixedQ4816.FromInteger(value: -3L))) {
            return PostStageOutcome.Fail(detail: "vector lerp componentwise");
        }

        // Vector unary negate and divide-by-scalar: componentwise, with negate the exact inverse of scaling by -1.
        var vectorSample = new FixedVector3(X: FixedQ4816.FromInteger(value: 6L), Y: FixedQ4816.FromInteger(value: -9L), Z: FixedQ4816.FromInteger(value: 12L));

        if ((-vectorSample) != new FixedVector3(X: FixedQ4816.FromInteger(value: -6L), Y: FixedQ4816.FromInteger(value: 9L), Z: FixedQ4816.FromInteger(value: -12L))) {
            return PostStageOutcome.Fail(detail: "vector unary negate");
        }

        if ((vectorSample / FixedQ4816.FromInteger(value: 3L)) != new FixedVector3(X: FixedQ4816.FromInteger(value: 2L), Y: FixedQ4816.FromInteger(value: -3L), Z: FixedQ4816.FromInteger(value: 4L))) {
            return PostStageOutcome.Fail(detail: "vector divide-by-scalar");
        }

        var planarSample = new FixedVector2(X: FixedQ4816.FromInteger(value: 8L), Y: FixedQ4816.FromInteger(value: -4L));

        if (((-planarSample) != new FixedVector2(X: FixedQ4816.FromInteger(value: -8L), Y: FixedQ4816.FromInteger(value: 4L))) ||
            ((planarSample / FixedQ4816.FromInteger(value: 4L)) != new FixedVector2(X: FixedQ4816.FromInteger(value: 2L), Y: FixedQ4816.NegativeOne))) {
            return PostStageOutcome.Fail(detail: "planar negate/divide-by-scalar");
        }

        // LayerSequence: the closed-form inverse agrees with an incremental walker, layer boundaries stay exact at
        // scale, and a bounded sequence saturates through Project with its documented overflow/depth channels.
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

                if (sequence.LayerOf(index: x) != layer) {
                    return PostStageOutcome.Fail(detail: $"layer-sequence {name}: LayerOf({x}) disagrees with the walker at layer {layer}");
                }
            }

            for (var n = 1L; (n < 100_000_000L); n <<= 3) {
                var boundary = sequence.Count(layerCount: n);

                if ((sequence.LayerOf(index: boundary) != (n + 1L)) || (sequence.LayerOf(index: (boundary - 1L)) != n)) {
                    return PostStageOutcome.Fail(detail: $"layer-sequence {name}: boundary of layer {n} is not exact");
                }
            }
        }

        var flat = LayerSequence.Linear(size: 5L, seed: 3L);

        if ((flat.LayerOf(index: 2L) != 0L) || (flat.LayerOf(index: 3L) != 1L) || (flat.LayerOf(index: 12L) != 2L)) {
            return PostStageOutcome.Fail(detail: "layer-sequence flat: linear indexing");
        }

        if (!Throws<OverflowException>(action: () => _ = LayerSequence.Linear(size: 1L, seed: 0L).LayerOf(index: long.MaxValue))) {
            return PostStageOutcome.Fail(detail: "layer-sequence flat: extremal layer silently wrapped");
        }

        var horizon = LayerSequence.Create(start: 6L, step: -2L, seed: 1L);

        if ((horizon.MaxLayer != 3L) || (horizon.Capacity != 13L)) {
            return PostStageOutcome.Fail(detail: "layer-sequence horizon: expected 3 layers holding 13 indices");
        }

        if ((horizon.LayerOf(index: 12L) != 3L) || (horizon.Locate(index: 5L) != new LayerLocation(Layer: 1L, Offset: 4L))) {
            return PostStageOutcome.Fail(detail: "layer-sequence horizon: in-range lookup");
        }

        var beyond = false;

        try {
            horizon.LayerOf(index: 13L);
        } catch (ArgumentOutOfRangeException) {
            beyond = true;
        }

        if (!beyond) {
            return PostStageOutcome.Fail(detail: "layer-sequence horizon: LayerOf beyond capacity must throw");
        }

        var projection = horizon.Project(index: 20L);

        if ((projection.Layer != 3L) || (projection.Overflow != 8L) || (projection.Depth != 2L)) {
            return PostStageOutcome.Fail(detail: $"layer-sequence horizon: Project(20) = ({projection.Layer}, {projection.Overflow}, {projection.Depth}), expected (3, 8, 2)");
        }

        if (horizon.Project(index: 12L) != new LayerProjection(Layer: 3L, Overflow: 0L, Depth: 0L)) {
            return PostStageOutcome.Fail(detail: "layer-sequence horizon: Project in range must be clean");
        }

        if (LayerSequence.Linear(size: 0L, seed: 0L).Project(index: long.MaxValue).Overflow != long.MaxValue) {
            return PostStageOutcome.Fail(detail: "layer-sequence: Project overflow must saturate at the widest excess");
        }

        // SquareRoot over UInt128: exact at and beside the widest boundaries.
        if (((UInt128.One << 100).SquareRoot() != (UInt128.One << 50)) ||
            (((UInt128.One << 100) - UInt128.One).SquareRoot() != ((UInt128.One << 50) - UInt128.One)) ||
            (((UInt128)ulong.MaxValue * ulong.MaxValue).SquareRoot() != ulong.MaxValue) ||
            (UInt128.MaxValue.SquareRoot() != ulong.MaxValue)) {
            return PostStageOutcome.Fail(detail: "uint128 square root boundaries");
        }

        return PostStageOutcome.Pass(detail: "arithmetic, sqrt, atan2/sincos, log2/exp2/pow, sign/copysign/lerp, banker's rounding, pcg32, gaussian, shuffle, alias, field-noise, low-discrepancy, layer-sequence, quaternion (incl. exp/log), wedge, dual, complex, and rigid-transform checks agree with their references");
    }

    private static bool Close(double actual, double expected, double tolerance) =>
        (Math.Abs(value: (actual - expected)) <= tolerance);
    private static T Sum<T>(ReadOnlySpan<T> values)
        where T : INumber<T> {
        var sum = T.Zero;

        foreach (var value in values) {
            sum += value;
        }

        return sum;
    }
    private static TResult CreateChecked<TResult, TValue>(TValue value)
        where TResult : INumberBase<TResult>
        where TValue : INumberBase<TValue> =>
        TResult.CreateChecked(value: value);
    private static TResult CreateSaturating<TResult, TValue>(TValue value)
        where TResult : INumberBase<TResult>
        where TValue : INumberBase<TValue> =>
        TResult.CreateSaturating(value: value);
    private static T AddChecked<T>(T left, T right)
        where T : INumber<T> =>
        checked(left + right);
    private static T SubtractChecked<T>(T left, T right)
        where T : INumber<T> =>
        checked(left - right);
    private static T MultiplyChecked<T>(T left, T right)
        where T : INumber<T> =>
        checked(left * right);
    private static T DivideChecked<T>(T left, T right)
        where T : INumber<T> =>
        checked(left / right);
    private static T NegateChecked<T>(T value)
        where T : INumber<T> =>
        checked(-value);
    private static bool Throws<TException>(Action action)
        where TException : Exception {
        try {
            action();

            return false;
        } catch (TException) {
            return true;
        }
    }
    private static bool SatisfiesRigidStudyCondition(FixedRigidTransform value) {
        var dual = value.Value.Dual;
        var scale = FixedQ4816.Max(
            x: FixedQ4816.Max(x: FixedQ4816.Abs(value: dual.X), y: FixedQ4816.Abs(value: dual.Y)),
            y: FixedQ4816.Max(x: FixedQ4816.Abs(value: dual.Z), y: FixedQ4816.Abs(value: dual.W))
        );
        var toleranceRaw = (8L + (scale.Value >> 14));
        var studyError = FixedQ4816.Abs(value: FixedQuaternion.Dot(left: value.Value.Real, right: dual));

        return (studyError.Value <= toleranceRaw);
    }
    private static bool RoundsTo(double value, double expected) =>
        (((double)FixedQ4816.Round(value: FixedQ4816.FromDouble(value: value))) == expected);
    private static double WrapPi(double angle) {
        while (angle > Math.PI) { angle -= (2d * Math.PI); }
        while (angle <= -Math.PI) { angle += (2d * Math.PI); }

        return angle;
    }
}
