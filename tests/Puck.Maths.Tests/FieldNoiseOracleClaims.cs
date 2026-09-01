using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// A shared-nothing BigInteger transcription of <see cref="FieldNoise.Sample(ulong, FixedVector3)"/>'s hash-and-
/// interpolate recipe, pinning VALUES exactly rather than merely proving two samples differ. <see cref="FieldNoise"/>
/// has no external published specification — every constant here (the domain-separation constants, the axis combine
/// multipliers, the three-round avalanche mix, the corner-value sign extension and the quintic fade polynomial) is
/// necessarily the same one <c>src/Puck.Maths/Sampling/FieldNoise.cs</c> documents, since there is no other definition
/// of this in-house noise to check against. What is independent is the ARITHMETIC: every step below is written from
/// scratch in <see cref="BigInteger"/>, calls no <c>Puck.Maths</c> member, and shares no line with FieldNoise.cs, so
/// an implementation bug — a wrong shift amount, a dropped 64-bit wraparound, a swapped corner, a sign-extension
/// slip, an off-by-one in the fade polynomial's Horner steps — reddens this law even though the underlying formula
/// is the one the subject specifies.
/// </summary>
internal static class FieldNoiseOracleClaims {
    private static readonly BigInteger Mask64 = ((BigInteger.One << 64) - BigInteger.One);
    private static readonly BigInteger CombineX = 0x9E3779B97F4A7C15UL;
    private static readonly BigInteger CombineY = 0xC2B2AE3D27D4EB4FUL;
    private static readonly BigInteger CombineZ = 0x165667B19E3779F9UL;
    private static readonly BigInteger SeedDomainX = 0xA0761D6478BD642FUL;
    private static readonly BigInteger SeedDomainY = 0xE7037ED1A0B428DBUL;
    private static readonly BigInteger SeedDomainZ = 0x8EBC6AF09C88C6E3UL;
    private static readonly BigInteger MixMultiplier1 = 0xBF58476D1CE4E5B9UL;
    private static readonly BigInteger MixMultiplier2 = 0x94D049BB133111EBUL;

    private const int FadeFractionBitCount = 28;
    private const long FractionMask = 0xFFFFL;

    /// <summary>The independent oracle for <see cref="FieldNoise.Sample(ulong, FixedVector3)"/>'s flat overload: the
    /// same hash-and-interpolate recipe, computed from scratch in <see cref="BigInteger"/>.</summary>
    /// <param name="seed">The field seed.</param>
    /// <param name="xRaw">The X coordinate's raw Q48.16 value.</param>
    /// <param name="yRaw">The Y coordinate's raw Q48.16 value.</param>
    /// <param name="zRaw">The Z coordinate's raw Q48.16 value.</param>
    /// <returns>The expected raw sample value.</returns>
    private static long SampleOracle(ulong seed, long xRaw, long yRaw, long zRaw) {
        var x0 = (xRaw >> FixedQ4816.FractionBitCount);
        var y0 = (yRaw >> FixedQ4816.FractionBitCount);
        var z0 = (zRaw >> FixedQ4816.FractionBitCount);
        var xFraction = new BigInteger(value: xRaw & FractionMask);
        var yFraction = new BigInteger(value: yRaw & FractionMask);
        var zFraction = new BigInteger(value: zRaw & FractionMask);

        var seedX = Mix(value: (new BigInteger(value: seed) + SeedDomainX) & Mask64);
        var seedY = Mix(value: (new BigInteger(value: seed) + SeedDomainY) & Mask64);
        var seedZ = Mix(value: (new BigInteger(value: seed) + SeedDomainZ) & Mask64);

        var xTerm0 = (AsUnsigned64(value: x0) * CombineX) & Mask64;
        var xTerm1 = (xTerm0 + CombineX) & Mask64;
        var yTerm0 = (AsUnsigned64(value: y0) * CombineY) & Mask64;
        var yTerm1 = (yTerm0 + CombineY) & Mask64;
        var zTerm0 = (AsUnsigned64(value: z0) * CombineZ) & Mask64;
        var zTerm1 = (zTerm0 + CombineZ) & Mask64;

        var xState0 = Mix(value: (seedX + xTerm0) & Mask64);
        var xState1 = Mix(value: (seedX + xTerm1) & Mask64);
        var xy00 = Mix(value: ((xState0 + seedY) + yTerm0) & Mask64);
        var xy10 = Mix(value: ((xState1 + seedY) + yTerm0) & Mask64);
        var xy01 = Mix(value: ((xState0 + seedY) + yTerm1) & Mask64);
        var xy11 = Mix(value: ((xState1 + seedY) + yTerm1) & Mask64);

        var c000 = CornerValue(hash: Mix(value: ((xy00 + seedZ) + zTerm0) & Mask64));
        var c100 = CornerValue(hash: Mix(value: ((xy10 + seedZ) + zTerm0) & Mask64));
        var c010 = CornerValue(hash: Mix(value: ((xy01 + seedZ) + zTerm0) & Mask64));
        var c110 = CornerValue(hash: Mix(value: ((xy11 + seedZ) + zTerm0) & Mask64));
        var c001 = CornerValue(hash: Mix(value: ((xy00 + seedZ) + zTerm1) & Mask64));
        var c101 = CornerValue(hash: Mix(value: ((xy10 + seedZ) + zTerm1) & Mask64));
        var c011 = CornerValue(hash: Mix(value: ((xy01 + seedZ) + zTerm1) & Mask64));
        var c111 = CornerValue(hash: Mix(value: ((xy11 + seedZ) + zTerm1) & Mask64));

        var fadeX = FadeQ28(t: xFraction);
        var fadeY = FadeQ28(t: yFraction);
        var fadeZ = FadeQ28(t: zFraction);

        var x00 = Lerp(a: c000, b: c100, fadeQ28: fadeX);
        var x10 = Lerp(a: c010, b: c110, fadeQ28: fadeX);
        var x01 = Lerp(a: c001, b: c101, fadeQ28: fadeX);
        var x11 = Lerp(a: c011, b: c111, fadeQ28: fadeX);
        var y0Value = Lerp(a: x00, b: x10, fadeQ28: fadeY);
        var y1Value = Lerp(a: x01, b: x11, fadeQ28: fadeY);

        return ((long)Lerp(a: y0Value, b: y1Value, fadeQ28: fadeZ));
    }
    // The three-round avalanche finalizer, over ulong-width BigIntegers throughout (every value stays in [0, 2^64)).
    private static BigInteger Mix(BigInteger value) {
        value &= Mask64;
        value ^= (value >> 30);
        value = (value * MixMultiplier1) & Mask64;
        value ^= (value >> 27);
        value = (value * MixMultiplier2) & Mask64;
        value ^= (value >> 31);

        return value & Mask64;
    }
    // A signed C# long reinterpreted as its unsigned 64-bit bit pattern -- the BigInteger equivalent of `(ulong)value`.
    private static BigInteger AsUnsigned64(long value) =>
        ((value >= 0L) ? new BigInteger(value: value) : (new BigInteger(value: value) + (BigInteger.One << 64)));
    // The corner hash's signed value: sign-extend the top 32 bits, then an arithmetic shift right by 15 -- the same
    // (long)(int)(hash >> 32) >> 15 the subject reads, transcribed in BigInteger.
    private static BigInteger CornerValue(BigInteger hash) {
        var top32 = (hash >> 32) & 0xFFFFFFFFUL;
        var signed = ((top32 >= 0x80000000UL) ? (top32 - (BigInteger.One << 32)) : top32);

        return ArithmeticShiftRight(shift: 15, value: signed);
    }
    // The quintic fade 6t^5 - 15t^4 + 10t^3 of a UQ0.16 fraction, at Q28 -- transcribed exactly, Horner-style, from
    // FieldNoise's own FadeQ28.
    private static BigInteger FadeQ28(BigInteger t) {
        var t28 = (t << 12);
        var inner = ((6 * t28) - (15L << FadeFractionBitCount));

        inner = (ArithmeticShiftRight(shift: FadeFractionBitCount, value: (inner * t28)) + (10L << FadeFractionBitCount));

        var t2 = ArithmeticShiftRight(shift: FadeFractionBitCount, value: (t28 * t28));
        var t3 = ArithmeticShiftRight(shift: FadeFractionBitCount, value: (t2 * t28));

        return ArithmeticShiftRight(shift: FadeFractionBitCount, value: (t3 * inner));
    }
    // The blend rounds its Q28 product to nearest on the magnitude (half away from zero), then re-signs -- the
    // subject's RoundShift, transcribed in BigInteger.
    private static BigInteger Lerp(BigInteger a, BigInteger b, BigInteger fadeQ28) =>
        (a + RoundShift(shift: FadeFractionBitCount, value: ((b - a) * fadeQ28)));
    private static BigInteger RoundShift(BigInteger value, int shift) {
        var magnitude = BigInteger.Abs(value: value);
        var rounded = ((magnitude + (BigInteger.One << (shift - 1))) >> shift);

        return ((value.Sign < 0) ? -rounded : rounded);
    }
    // Arithmetic (floor) right shift of a possibly-negative BigInteger by a non-negative amount, matching C#'s `>>`
    // on a signed integral type -- which floors toward negative infinity rather than truncating toward zero, the one
    // place BigInteger's own division semantics would silently disagree with the subject's native shifts.
    private static BigInteger ArithmeticShiftRight(BigInteger value, int shift) =>
        ((value.Sign >= 0) ? (value >> shift) : (-(((-value - BigInteger.One) >> shift) + BigInteger.One)));

    /// <summary>Pins <see cref="FieldNoise.Sample(ulong, FixedVector3)"/> at exact VALUES: the absolute sibling
    /// sampling.field-noise-periodicity-canary-and-distribution's two relative canaries are owed. A hand-picked
    /// ladder of seeds and positions -- the origin, every axis's unit step at both signs, both lattice-cell-boundary
    /// fractions (zero and the top of the mask), an interior multi-cell position, extreme seeds (zero and
    /// ulong.MaxValue), and a position spanning several cells on every axis -- checked against the independent
    /// BigInteger oracle above.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? FieldNoiseSampleMatchesExactOracle() {
        (ulong Seed, long X, long Y, long Z)[] ladder = [
            (0UL, 0L, 0L, 0L),
            (1UL, 0L, 0L, 0L),
            (0UL, 65536L, 0L, 0L),
            (0UL, 0L, 65536L, 0L),
            (0UL, 0L, 0L, 65536L),
            (0UL, -65536L, 0L, 0L),
            (0UL, 32768L, 32768L, 32768L),
            (0UL, -32768L, -32768L, -32768L),
            (0UL, 1L, 1L, 1L),
            (0UL, 65535L, 65535L, 65535L),
            (0UL, -1L, -1L, -1L),
            (42UL, 12345L, -67890L, 555555L),
            (ulong.MaxValue, -1L, -1L, -1L),
            (ulong.MaxValue, long.MinValue, 0L, 0L),
            (7UL, ((3L << 16) + 8192L), ((-5L << 16) - 40000L), ((100L << 16) + 1L)),
            (0xDEADBEEFUL, (1L << 40), -(1L << 40), (1L << 20)),
            (123456789UL, long.MaxValue, long.MinValue, 0L),
        ];

        foreach (var (seed, x, y, z) in ladder) {
            var position = new FixedVector3(X: FixedQ4816.FromRawBits(value: x), Y: FixedQ4816.FromRawBits(value: y), Z: FixedQ4816.FromRawBits(value: z));
            var subject = FieldNoise.Sample(position: position, seed: seed).Value;
            var expected = SampleOracle(seed: seed, xRaw: x, yRaw: y, zRaw: z);

            if (subject != expected) {
                return $"seed={seed} position=({x},{y},{z}): FieldNoise.Sample returned raw {subject}, the independent oracle computed {expected}";
            }

            // The prepared-seed door: Prepare carries the seed it was built from, and every overload taking it
            // answers exactly what the ulong twin answers.
            var prepared = FieldNoise.Prepare(seed: seed);

            if (prepared.Seed != seed) { return $"Prepare({seed}).Seed reads {prepared.Seed}"; }
            if (FieldNoise.Sample(position: position, seed: prepared).Value != subject) { return $"seed={seed} position=({x},{y},{z}): the prepared-seed Sample diverges from the ulong overload"; }
            if (FieldNoise.Sample(octaves: 3, position: position, seed: prepared) != FieldNoise.Sample(octaves: 3, position: position, seed: seed)) { return $"seed={seed} position=({x},{y},{z}): the prepared-seed three-octave Sample diverges from the ulong overload"; }
            if (FieldNoise.Hash(seed: prepared, x: x, y: y, z: z) != FieldNoise.Hash(seed: seed, x: x, y: y, z: z)) { return $"seed={seed}: the prepared-seed Hash diverges from the ulong overload"; }
            if (FieldNoise.SampleGradient(gradient: out var preparedGradient, position: position, seed: prepared) != FieldNoise.SampleGradient(gradient: out var gradient, position: position, seed: seed)) { return $"seed={seed} position=({x},{y},{z}): the prepared-seed SampleGradient diverges from the ulong overload"; }
            if (preparedGradient != gradient) { return $"seed={seed} position=({x},{y},{z}): the prepared-seed gradient diverges from the ulong overload"; }
        }

        return null;
    }
}
