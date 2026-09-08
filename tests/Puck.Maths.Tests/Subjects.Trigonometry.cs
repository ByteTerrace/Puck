using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    public static string? TrigonometryTwiddles() {
        for (var length = 1; length <= 1024; length *= 2) {
            var fourier = FixedFourierTransformPlan.Create(length: length);
            var cosine = FixedCosineTransformPlan.Create(length: length);
            for (var kind = 0; kind < 2; ++kind) {
                var forward = ((kind == 0) ? fourier.ForwardTwiddles : cosine.ForwardTwiddles);
                var inverse = ((kind == 0) ? fourier.InverseTwiddles : cosine.InverseTwiddles);
                for (var k = 0; k < forward.Length; ++k) {
                    // The ideal phase is constructed as an exact rational on the turn grid, independently of the
                    // plan's shifts and reflection indices. Length 1's FFT is empty; its DCT phase is zero.
                    var divisor = new BigInteger((kind == 0) ? length : (4L * length));
                    var phase = unchecked((ulong)(long)((-(new BigInteger(k) << 64)) / divisor));
                    var expected = Oracles.EncloseSinCosTurns(fractionalTurns: phase, guardBitCount: Oracles.GuardBitCount);
                    if (WithinEnvelope(name: $"plan {kind}/{length}/{k} real", subjectRaw: forward[k].Real.Value, enclosure: expected.Cos, toleranceUnits: SinCosToleranceUnits()) is { } real) { return real; }
                    if (WithinEnvelope(name: $"plan {kind}/{length}/{k} imaginary", subjectRaw: forward[k].Imaginary.Value, enclosure: expected.Sin, toleranceUnits: SinCosToleranceUnits()) is { } imaginary) { return imaginary; }
                    if (inverse[k] != forward[k].Conjugate()) { return $"plan {kind}/{length}/{k} inverse conjugation"; }
                }
            }
        }
        return null;
    }

    public static string? TrigonometryConstants() {
        var pi = Oracles.Pi(bitCount: 384);
        var numerator = (BigInteger.One << 480);
        var reciprocalLow = Oracles.RoundRationalTiesToEven(numerator: numerator, denominator: (2 * pi.High));
        var reciprocalHigh = Oracles.RoundRationalTiesToEven(numerator: numerator, denominator: (2 * pi.Low));
        if ((reciprocalLow != reciprocalHigh) || (reciprocalLow != (BigInteger)FixedQ4816.SinCosInvTwoPiQ96)) { return "Q96 reciprocal is not independently rounded 1/(2*pi)"; }

        for (var i = 0; i <= 64; ++i) {
            var sine = Oracles.EncloseSinCosTurns(fractionalTurns: ((ulong)i << 56), guardBitCount: 80).Sin;
            var low = Oracles.RoundRationalTiesToEven(numerator: sine.Low, denominator: (BigInteger.One << 36));
            var high = Oracles.RoundRationalTiesToEven(numerator: sine.High, denominator: (BigInteger.One << 36));
            if ((low != high) || (low != FixedQ4816.SinCosTableQ60[i])) { return $"quarter-wave entry {i} disagrees with series [{low}, {high}]"; }
        }

        foreach (var (actual, denominator) in ((ReadOnlySpan<(long, int)>)[
            (FixedQ4816.SinPolyC1Q60, -6), (FixedQ4816.SinPolyC2Q60, 120),
            (FixedQ4816.CosPolyC1Q60, -2), (FixedQ4816.CosPolyC2Q60, 24)
        ])) {
            if (actual != Oracles.RoundRationalTiesToEven(numerator: (BigInteger.One << 60), denominator: denominator)) { return $"residual coefficient 1/{denominator} is not correctly rounded"; }
        }
        var circleLow = Oracles.RoundRationalTiesToEven(numerator: (2 * pi.Low), denominator: (BigInteger.One << 324));
        var circleHigh = Oracles.RoundRationalTiesToEven(numerator: (2 * pi.High), denominator: (BigInteger.One << 324));
        return ((circleLow == circleHigh) && (circleLow == FixedQ4816.SinCosTwoPiQ60)) ? null : "Q60 circle constant is not independently rounded";
    }

    public static string? TrigonometryTurns(long[] left, long[] right) =>
        CheckTrigonometryTurns(phase: unchecked((ulong)left[0]), checkCore: false);

    private static string? CheckTrigonometryTurns(ulong phase, bool checkCore) {
        var actual = FixedQ4816.SinCosTurns(fractionalTurns: phase);
        var expected = Oracles.EncloseSinCosTurns(fractionalTurns: phase, guardBitCount: Oracles.GuardBitCount);
        if (WithinEnvelope(name: $"turn sine {phase}", subjectRaw: actual.Sin.Value, enclosure: expected.Sin, toleranceUnits: SinCosToleranceUnits()) is { } sine) { return sine; }
        if (WithinEnvelope(name: $"turn cosine {phase}", subjectRaw: actual.Cos.Value, enclosure: expected.Cos, toleranceUnits: SinCosToleranceUnits()) is { } cosine) { return cosine; }
        var mirror = FixedQ4816.SinCosTurns(fractionalTurns: unchecked(0UL - phase));
        if ((mirror.Sin.Value != -actual.Sin.Value) || (mirror.Cos != actual.Cos)) { return $"turn symmetry at {phase}"; }

        if (checkCore) {
            var core = FixedQ4816.SinCosCore(fractionalTurns: unchecked((long)phase));
            var reference = Oracles.EncloseSinCosTurns(fractionalTurns: phase, guardBitCount: 80);
            var tolerance = ((6 * (BigInteger.One << 96)) / BigInteger.Pow(10, 15));
            foreach (var (value, bounds) in ((ReadOnlySpan<(long, Oracles.Enclosure)>)[(core.SinQ60, reference.Sin), (core.CosQ60, reference.Cos)])) {
                var scaled = ((BigInteger)value << 36);
                if ((scaled < (bounds.Low - tolerance)) || (scaled > (bounds.High + tolerance))) { return $"Gaussian Q60 core envelope at phase {phase}"; }
            }
        }
        return null;
    }

    public static string? TrigonometrySeams() {
        // Every table node and half-node, plus either adjacent phase tick, in every quadrant.
        for (var i = 0; i < 512; ++i) {
            for (var delta = -1; delta <= 1; ++delta) {
                var phase = unchecked(((ulong)i << 55) + (ulong)delta);
                if (CheckTrigonometryTurns(phase: phase, checkCore: true) is { } failure) { return failure; }
            }
        }
        ReadOnlySpan<(long Sin, long Cos)> cardinals = [(0, 65536), (65536, 0), (0, -65536), (-65536, 0)];
        for (var i = 0; i < 4; ++i) {
            var actual = FixedQ4816.SinCosTurns(fractionalTurns: ((ulong)i << 62));
            if ((actual.Sin.Value, actual.Cos.Value) != cardinals[i]) { return $"cardinal direction {i}"; }
        }
        ReadOnlySpan<long> witnesses = [0, 1, -1, -94521, 94521, -9223372036854774708L,
            -9074293991763690791L, -5576408924856405436L, long.MinValue, long.MinValue + 1, long.MaxValue];
        foreach (var raw in witnesses) {
            if (FixedSinCosWithinEnvelope(left: [raw], right: [0]) is { } failure) { return failure; }
            foreach (var bits in ((ReadOnlySpan<int>)[17, 32])) {
                var actual = ((bits == 17) ? FixedQ4816.SinCosHalfAngle(new(raw)) : FixedQ4816.SinCosQ32(raw));
                var expected = Oracles.EncloseSinCosScaled(raw: raw, fractionBitCount: bits, guardBitCount: Oracles.GuardBitCount);
                if (WithinEnvelope(name: $"Q{bits} sine {raw}", subjectRaw: actual.Sin.Value, enclosure: expected.Sin, toleranceUnits: SinCosToleranceUnits()) is { } sine) { return sine; }
                if (WithinEnvelope(name: $"Q{bits} cosine {raw}", subjectRaw: actual.Cos.Value, enclosure: expected.Cos, toleranceUnits: SinCosToleranceUnits()) is { } cosine) { return cosine; }
            }
            var unsigned = unchecked((ulong)raw);
            var pair = FixedQ4816.SinCosRaw(rawAngle: unsigned);
            var reference = Oracles.EncloseSinCosScaled(raw: unsigned, fractionBitCount: 16, guardBitCount: Oracles.GuardBitCount);
            if (WithinEnvelope(name: $"unsigned sine {unsigned}", subjectRaw: pair.Sin.Value, enclosure: reference.Sin, toleranceUnits: SinCosToleranceUnits()) is { } s) { return s; }
            if (WithinEnvelope(name: $"unsigned cosine {unsigned}", subjectRaw: pair.Cos.Value, enclosure: reference.Cos, toleranceUnits: SinCosToleranceUnits()) is { } c) { return c; }
        }
        return null;
    }
}
