namespace Puck.Maths;

/// <summary>
/// A deterministic spatial noise field: a stateless pure integer function from a seed and a world position to a
/// smooth pseudo-random value in <c>[−1, 1]</c>. Identical arguments produce identical bits on every machine;
/// there is no state to snapshot. Complements <see cref="Pcg32XshRr"/> (sequential draws) and
/// <see cref="AliasTable{TElement}"/> (weighted choice).
/// </summary>
/// <remarks>
/// Value noise over the unit integer lattice: corner values come from an avalanche hash of the lattice coordinates,
/// blended by a quintic fade that keeps the value and first derivative continuous across cell boundaries. One noise
/// unit spans one world unit; scale the position before sampling to change frequency. The octave overload sums
/// frequency-doubled, amplitude-halved layers and stays within <c>[−1, 1]</c>.
/// </remarks>
public static class FieldNoise {
    private const ulong CombineX = 0x9E3779B97F4A7C15UL;
    private const ulong CombineXHigh = 0xD6E8FEB86659FD93UL;
    private const ulong CombineY = 0xC2B2AE3D27D4EB4FUL;
    private const ulong CombineYHigh = 0xA5A3564E27F8862BUL;
    private const ulong CombineZ = 0x165667B19E3779F9UL;
    private const ulong CombineZHigh = 0x9E6C63D0676A9A99UL;
    private const int FadeFractionBitCount = 28;
    private const long FractionMask = 0xFFFFL;
    private const int MaximumOctaveCount = 16;
    private const ulong OctaveSeedStep = 0xD1B54A32D192ED03UL;
    private const ulong SeedDomainX = 0xA0761D6478BD642FUL;
    private const ulong SeedDomainY = 0xE7037ED1A0B428DBUL;
    private const ulong SeedDomainZ = 0x8EBC6AF09C88C6E3UL;

    private static long BlendCorners(long c000, long c100, long c010, long c110, long c001, long c101, long c011, long c111, long fadeX, long fadeY, long fadeZ) {
        var x00 = Lerp(
            a: c000,
            b: c100,
            fadeQ28: fadeX
        );
        var x10 = Lerp(
            a: c010,
            b: c110,
            fadeQ28: fadeX
        );
        var x01 = Lerp(
            a: c001,
            b: c101,
            fadeQ28: fadeX
        );
        var x11 = Lerp(
            a: c011,
            b: c111,
            fadeQ28: fadeX
        );
        var y0Value = Lerp(
            a: x00,
            b: x10,
            fadeQ28: fadeY
        );
        var y1Value = Lerp(
            a: x01,
            b: x11,
            fadeQ28: fadeY
        );

        return Lerp(
            a: y0Value,
            b: y1Value,
            fadeQ28: fadeZ
        );
    }
    // The trilinear blend and its three partials share every intermediate. Differentiating the blend with respect to a
    // faded weight replaces that axis's pair of corner lerps with their difference and leaves the other two axes
    // untouched; the chain rule then scales by the fade's derivative. One noise unit spans one world unit, so the
    // partial with respect to the cell fraction is already the partial with respect to the axis.
    private static long BlendCornersWithGradient(ReadOnlySpan<long> corners, long xFraction, long yFraction, long zFraction, out FixedVector3 gradient) {
        var fadeX = FadeQ28(t: xFraction);
        var fadeY = FadeQ28(t: yFraction);
        var fadeZ = FadeQ28(t: zFraction);
        var x00 = Lerp(
            a: corners[0],
            b: corners[1],
            fadeQ28: fadeX
        );
        var x10 = Lerp(
            a: corners[2],
            b: corners[3],
            fadeQ28: fadeX
        );
        var x01 = Lerp(
            a: corners[4],
            b: corners[5],
            fadeQ28: fadeX
        );
        var x11 = Lerp(
            a: corners[6],
            b: corners[7],
            fadeQ28: fadeX
        );
        var y0Value = Lerp(
            a: x00,
            b: x10,
            fadeQ28: fadeY
        );
        var y1Value = Lerp(
            a: x01,
            b: x11,
            fadeQ28: fadeY
        );

        // ∂/∂x: the x-lerps collapse to corner differences, then blend through y and z unchanged.
        var xSlope = Lerp(
            a: Lerp(
                a: (corners[1] - corners[0]),
                b: (corners[3] - corners[2]),
                fadeQ28: fadeY
            ),
            b: Lerp(
                a: (corners[5] - corners[4]),
                b: (corners[7] - corners[6]),
                fadeQ28: fadeY
            ),
            fadeQ28: fadeZ
        );
        // ∂/∂y: the y-lerp collapses to the difference of the already-blended x pairs.
        var ySlope = Lerp(
            a: (x10 - x00),
            b: (x11 - x01),
            fadeQ28: fadeZ
        );
        // ∂/∂z: the outermost lerp collapses to the difference of its own operands.
        var zSlope = (y1Value - y0Value);

        gradient = new(
            X: FixedQ4816.FromRawBits(value: ((xSlope * FadeDerivativeQ28(t: xFraction)) >> FadeFractionBitCount)),
            Y: FixedQ4816.FromRawBits(value: ((ySlope * FadeDerivativeQ28(t: yFraction)) >> FadeFractionBitCount)),
            Z: FixedQ4816.FromRawBits(value: ((zSlope * FadeDerivativeQ28(t: zFraction)) >> FadeFractionBitCount))
        );

        return Lerp(
            a: y0Value,
            b: y1Value,
            fadeQ28: fadeZ
        );
    }
    // The eight lattice corners in x-major order (index = i + 2j + 4k), which is the order BlendCorners' c000..c111
    // parameters take. The staged tree is the public Hash construction shared across all eight corners: independent
    // seed-domain states are injected at x, y, and z, so no single seed-state shift translates the whole field. Every
    // sampler stages its corners here, which is what makes the gradient sampler and the value samplers agree bit for
    // bit rather than leaving that agreement to two hand-kept copies.
    private static void ComputeCorners(
        in FieldNoiseSeed seed,
        ulong xTerm0,
        ulong xTerm1,
        ulong yTerm0,
        ulong yTerm1,
        ulong zTerm0,
        ulong zTerm1,
        Span<long> corners
    ) {
        unchecked {
            var seedX = seed.X;
            var seedY = seed.Y;
            var seedZ = seed.Z;
            var xState0 = Mix(value: (seedX + xTerm0));
            var xState1 = Mix(value: (seedX + xTerm1));
            var xy00 = Mix(value: ((xState0 + seedY) + yTerm0));
            var xy10 = Mix(value: ((xState1 + seedY) + yTerm0));
            var xy01 = Mix(value: ((xState0 + seedY) + yTerm1));
            var xy11 = Mix(value: ((xState1 + seedY) + yTerm1));

            corners[0] = CornerValue(hash: Mix(value: ((xy00 + seedZ) + zTerm0)));
            corners[1] = CornerValue(hash: Mix(value: ((xy10 + seedZ) + zTerm0)));
            corners[2] = CornerValue(hash: Mix(value: ((xy01 + seedZ) + zTerm0)));
            corners[3] = CornerValue(hash: Mix(value: ((xy11 + seedZ) + zTerm0)));
            corners[4] = CornerValue(hash: Mix(value: ((xy00 + seedZ) + zTerm1)));
            corners[5] = CornerValue(hash: Mix(value: ((xy10 + seedZ) + zTerm1)));
            corners[6] = CornerValue(hash: Mix(value: ((xy01 + seedZ) + zTerm1)));
            corners[7] = CornerValue(hash: Mix(value: ((xy11 + seedZ) + zTerm1)));
        }
    }
    // Signed corner value in [−65536, 65535] from the hash's top 32 bits.
    private static long CornerValue(ulong hash) =>
        (((long)((int)(hash >> 32))) >> 15);
    // Doubling a floor-split coordinate is exact: 2(cell + fraction/2^16) becomes
    // (2*cell + carry) + ((2*fraction) mod 2^16)/2^16. Under the 16-octave API limit a Q48.16 input's cell
    // remains inside long for every layer; checked arithmetic keeps that invariant explicit if the limit changes.
    private static void DoubleFrequency(ref long cell, ref long fraction) {
        var doubledFraction = (fraction << 1);

        cell = checked(((cell * 2L) + (doubledFraction >> FixedQ4816.FractionBitCount)));
        fraction = doubledFraction & FractionMask;
    }
    // The quintic fade's derivative 30t⁴ − 60t³ + 30t², factored as 30t²(t − 1)² to keep every intermediate positive
    // and cancellation-free. Zero at both ends, peaking at 1.875 for t = 0.5.
    private static long FadeDerivativeQ28(long t) {
        var t28 = (t << 12);
        var oneMinus = ((1L << FadeFractionBitCount) - t28);
        var square = ((t28 * t28) >> FadeFractionBitCount);
        var oneMinusSquare = ((oneMinus * oneMinus) >> FadeFractionBitCount);

        return (30L * ((square * oneMinusSquare) >> FadeFractionBitCount));
    }
    // Quintic fade 6t⁵ − 15t⁴ + 10t³ of a UQ0.16 fraction, at Q28; zero slope at both ends keeps the field's first
    // derivative continuous across cell boundaries.
    private static long FadeQ28(long t) {
        var t28 = (t << 12);
        var inner = ((6L * t28) - (15L << FadeFractionBitCount));

        inner = (((inner * t28) >> FadeFractionBitCount) + (10L << FadeFractionBitCount));

        var t2 = ((t28 * t28) >> FadeFractionBitCount);
        var t3 = ((t2 * t28) >> FadeFractionBitCount);

        return ((t3 * inner) >> FadeFractionBitCount);
    }
    private static bool FitsNativeLattice(Int128 value) =>
        ((value >= long.MinValue) && (value < long.MaxValue));
    // The Q28 blend rounds to nearest (half away from zero on the magnitude) instead of flooring: a floor loses half
    // a raw unit downward on average per blend, seven blends per sample, a systematic offset a mean-zero field cannot
    // carry.
    private static long Lerp(long a, long b, long fadeQ28) =>
        (a + RoundShift(
            shift: FadeFractionBitCount,
            value: ((b - a) * fadeQ28)
        ));
    private static long RoundShift(long value, int shift) {
        var sign = (value >> 63);
        var magnitude = ((value ^ sign) - sign);
        var rounded = ((magnitude + (1L << (shift - 1))) >> shift);

        return ((rounded ^ sign) - sign);
    }
    // The gradient sampler's corner load: the terms SampleLattice forms, through the tree every sampler shares.
    private static void LoadCorners(in FieldNoiseSeed seed, long x0, long y0, long z0, Span<long> corners) {
        unchecked {
            var xTerm0 = (((ulong)x0) * CombineX);
            var yTerm0 = (((ulong)y0) * CombineY);
            var zTerm0 = (((ulong)z0) * CombineZ);

            ComputeCorners(
                corners: corners,
                seed: seed,
                xTerm0: xTerm0,
                xTerm1: (xTerm0 + CombineX),
                yTerm0: yTerm0,
                yTerm1: (yTerm0 + CombineY),
                zTerm0: zTerm0,
                zTerm1: (zTerm0 + CombineZ)
            );
        }
    }
    private static ulong Mix(ulong value) {
        unchecked {
            value ^= (value >> 30);
            value *= 0xBF58476D1CE4E5B9UL;
            value ^= (value >> 27);
            value *= 0x94D049BB133111EBUL;
            value ^= (value >> 31);

            return value;
        }
    }
    private static ulong MixWideAxis(
        ulong state,
        ulong seedState,
        Int128 coordinate,
        ulong lowMultiplier,
        ulong highMultiplier
    ) {
        unchecked {
            var low = ((ulong)coordinate);
            // Remove the low word's sign extension so every signed-64 coordinate has a zero high component. This
            // makes the wide and native hash trees bit-identical throughout their shared lattice range.
            var high = ((ulong)(((long)(coordinate >> 64)) - (((long)low) >> 63)));

            state = Mix(value: ((state + seedState) + (low * lowMultiplier)));

            // A separate avalanche prevents a high-word change from being algebraically canceled by translating the
            // low word. Skip it for zero so signed-64 coordinates remain identical to the native hash path.
            return ((0UL == high)
                ? state
                : Mix(value: (state + (high * highMultiplier)))
            );
        }
    }
    private static long SampleCore(in FieldNoiseSeed seed, long xRaw, long yRaw, long zRaw) =>
        SampleLattice(
            seed: seed,
            x0: (xRaw >> FixedQ4816.FractionBitCount),
            xFraction: xRaw & FractionMask,
            y0: (yRaw >> FixedQ4816.FractionBitCount),
            yFraction: yRaw & FractionMask,
            z0: (zRaw >> FixedQ4816.FractionBitCount),
            zFraction: zRaw & FractionMask
        );
    // The native lattice sampler stays on long coordinates for the flat hot path.
    private static long SampleLattice(in FieldNoiseSeed seed, long x0, long y0, long z0, long xFraction, long yFraction, long zFraction) {
        unchecked {
            var xTerm0 = (((ulong)x0) * CombineX);
            var yTerm0 = (((ulong)y0) * CombineY);
            var zTerm0 = (((ulong)z0) * CombineZ);

            return SampleLatticeTerms(
                seed: seed,
                xFraction: xFraction,
                xTerm0: xTerm0,
                xTerm1: (xTerm0 + CombineX),
                yFraction: yFraction,
                yTerm0: yTerm0,
                yTerm1: (yTerm0 + CombineY),
                zFraction: zFraction,
                zTerm0: zTerm0,
                zTerm1: (zTerm0 + CombineZ)
            );
        }
    }
    // The value sampler, once the lattice coordinates have been reduced to their six hash terms.
    private static long SampleLatticeTerms(
        in FieldNoiseSeed seed,
        ulong xTerm0,
        ulong xTerm1,
        ulong yTerm0,
        ulong yTerm1,
        ulong zTerm0,
        ulong zTerm1,
        long xFraction,
        long yFraction,
        long zFraction
    ) {
        var fadeX = FadeQ28(t: xFraction);
        var fadeY = FadeQ28(t: yFraction);
        var fadeZ = FadeQ28(t: zFraction);

        Span<long> corners = stackalloc long[8];

        ComputeCorners(
            corners: corners,
            seed: seed,
            xTerm0: xTerm0,
            xTerm1: xTerm1,
            yTerm0: yTerm0,
            yTerm1: yTerm1,
            zTerm0: zTerm0,
            zTerm1: zTerm1
        );

        return BlendCorners(
            c000: corners[0],
            c100: corners[1],
            c010: corners[2],
            c110: corners[3],
            c001: corners[4],
            c101: corners[5],
            c011: corners[6],
            c111: corners[7],
            fadeX: fadeX,
            fadeY: fadeY,
            fadeZ: fadeZ
        );
    }
    // The hierarchical path retains the full cell-derived lattice coordinate. MixWideAxis's high component is zero
    // for signed-64-bit coordinates, preserving the flat overload's output in their shared range.
    private static long SampleWideLattice(in FieldNoiseSeed seed, Int128 x0, Int128 y0, Int128 z0, long xFraction, long yFraction, long zFraction) {
        var fadeX = FadeQ28(t: xFraction);
        var fadeY = FadeQ28(t: yFraction);
        var fadeZ = FadeQ28(t: zFraction);
        var seedX = seed.X;
        var seedY = seed.Y;
        var seedZ = seed.Z;

        var xState0 = MixWideAxis(
            coordinate: x0,
            highMultiplier: CombineXHigh,
            lowMultiplier: CombineX,
            seedState: seedX,
            state: 0UL
        );
        var xState1 = MixWideAxis(
            state: 0UL,
            seedState: seedX,
            coordinate: (x0 + Int128.One),
            lowMultiplier: CombineX,
            highMultiplier: CombineXHigh
        );
        var xy00 = MixWideAxis(
            coordinate: y0,
            highMultiplier: CombineYHigh,
            lowMultiplier: CombineY,
            seedState: seedY,
            state: xState0
        );
        var xy10 = MixWideAxis(
            coordinate: y0,
            highMultiplier: CombineYHigh,
            lowMultiplier: CombineY,
            seedState: seedY,
            state: xState1
        );
        var xy01 = MixWideAxis(
            state: xState0,
            seedState: seedY,
            coordinate: (y0 + Int128.One),
            lowMultiplier: CombineY,
            highMultiplier: CombineYHigh
        );
        var xy11 = MixWideAxis(
            state: xState1,
            seedState: seedY,
            coordinate: (y0 + Int128.One),
            lowMultiplier: CombineY,
            highMultiplier: CombineYHigh
        );

        return BlendCorners(
            c000: CornerValue(hash: MixWideAxis(
                coordinate: z0,
                highMultiplier: CombineZHigh,
                lowMultiplier: CombineZ,
                seedState: seedZ,
                state: xy00
            )),
            c100: CornerValue(hash: MixWideAxis(
                coordinate: z0,
                highMultiplier: CombineZHigh,
                lowMultiplier: CombineZ,
                seedState: seedZ,
                state: xy10
            )),
            c010: CornerValue(hash: MixWideAxis(
                coordinate: z0,
                highMultiplier: CombineZHigh,
                lowMultiplier: CombineZ,
                seedState: seedZ,
                state: xy01
            )),
            c110: CornerValue(hash: MixWideAxis(
                coordinate: z0,
                highMultiplier: CombineZHigh,
                lowMultiplier: CombineZ,
                seedState: seedZ,
                state: xy11
            )),
            c001: CornerValue(hash: MixWideAxis(
                state: xy00,
                seedState: seedZ,
                coordinate: (z0 + Int128.One),
                lowMultiplier: CombineZ,
                highMultiplier: CombineZHigh
            )),
            c101: CornerValue(hash: MixWideAxis(
                state: xy10,
                seedState: seedZ,
                coordinate: (z0 + Int128.One),
                lowMultiplier: CombineZ,
                highMultiplier: CombineZHigh
            )),
            c011: CornerValue(hash: MixWideAxis(
                state: xy01,
                seedState: seedZ,
                coordinate: (z0 + Int128.One),
                lowMultiplier: CombineZ,
                highMultiplier: CombineZHigh
            )),
            c111: CornerValue(hash: MixWideAxis(
                state: xy11,
                seedState: seedZ,
                coordinate: (z0 + Int128.One),
                lowMultiplier: CombineZ,
                highMultiplier: CombineZHigh
            )),
            fadeX: fadeX,
            fadeY: fadeY,
            fadeZ: fadeZ
        );
    }

    /// <summary>Hashes a lattice point to 64 well-mixed bits. Suitable for per-cell decisions that need no smoothing.</summary>
    /// <param name="seed">The field seed; each seed is domain-separated at every coordinate stage to decorrelate fields.</param>
    /// <param name="x">The lattice X coordinate.</param>
    /// <param name="y">The lattice Y coordinate.</param>
    /// <param name="z">The lattice Z coordinate.</param>
    /// <returns>A well-mixed 64-bit hash, bit-identical across machines.</returns>
    public static ulong Hash(ulong seed, long x, long y, long z) =>
        Hash(
            seed: Prepare(seed: seed),
            x: x,
            y: y,
            z: z
        );
    /// <summary>Hashes a lattice coordinate under a prepared seed — the same bits as the <see cref="ulong"/> overload
    /// without re-deriving the three seed-domain states.</summary>
    /// <param name="seed">The prepared seed.</param>
    /// <param name="x">The lattice X coordinate.</param>
    /// <param name="y">The lattice Y coordinate.</param>
    /// <param name="z">The lattice Z coordinate.</param>
    /// <returns>A well-mixed 64-bit hash, bit-identical across machines.</returns>
    public static ulong Hash(in FieldNoiseSeed seed, long x, long y, long z) {
        unchecked {
            var state = Mix(value: (seed.X + (((ulong)x) * CombineX)));

            state = Mix(value: ((state + seed.Y) + (((ulong)y) * CombineY)));

            return Mix(value: ((state + seed.Z) + (((ulong)z) * CombineZ)));
        }
    }
    /// <summary>Derives a seed's three per-axis domain states once, so a caller sampling one field at many positions
    /// pays that derivation per field rather than per sample.</summary>
    /// <param name="seed">The field seed.</param>
    /// <returns>The prepared seed; every overload taking it returns the same bits as its <see cref="ulong"/> twin.</returns>
    public static FieldNoiseSeed Prepare(ulong seed) {
        unchecked {
            return new(
                seed: seed,
                x: Mix(value: (seed + SeedDomainX)),
                y: Mix(value: (seed + SeedDomainY)),
                z: Mix(value: (seed + SeedDomainZ))
            );
        }
    }
    /// <summary>Samples the smooth noise field at a position.</summary>
    /// <param name="seed">The field seed; distinct seeds yield domain-separated, decorrelated fields.</param>
    /// <param name="position">The position to sample; one noise unit spans one world unit (scale the position for other frequencies).</param>
    /// <returns>A smooth pseudo-random value in <c>[−1, 1]</c>.</returns>
    public static FixedQ4816 Sample(ulong seed, FixedVector3 position) =>
        Sample(
            position: position,
            seed: Prepare(seed: seed)
        );
    /// <summary>Samples the smooth noise field at a position under a prepared seed.</summary>
    /// <param name="seed">The prepared seed.</param>
    /// <param name="position">The position to sample; one noise unit spans one world unit.</param>
    /// <returns>A smooth pseudo-random value in <c>[−1, 1]</c>.</returns>
    public static FixedQ4816 Sample(in FieldNoiseSeed seed, FixedVector3 position) =>
        new(Value: SampleCore(
            seed: seed,
            xRaw: position.X.Value,
            yRaw: position.Y.Value,
            zRaw: position.Z.Value
        ));
    /// <summary>Samples fractal noise at a position: <paramref name="octaves"/> layers, each doubling frequency and halving amplitude.</summary>
    /// <param name="seed">The field seed; each octave derives a domain-separated, decorrelated lattice from it.</param>
    /// <param name="position">The position to sample; one noise unit spans one world unit at the first octave.</param>
    /// <param name="octaves">The layer count, in <c>[1, 16]</c>.</param>
    /// <returns>A smooth pseudo-random value in <c>[−1, 1]</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="octaves"/> is outside <c>[1, 16]</c>.</exception>
    public static FixedQ4816 Sample(ulong seed, FixedVector3 position, int octaves) =>
        Sample(
            octaves: octaves,
            position: position,
            seed: Prepare(seed: seed)
        );
    /// <summary>Samples fractal noise at a position under a prepared seed: the first octave reads the prepared
    /// states, each further octave prepares its own stepped seed.</summary>
    /// <param name="seed">The prepared seed.</param>
    /// <param name="position">The position to sample.</param>
    /// <param name="octaves">The layer count, in <c>[1, 16]</c>.</param>
    /// <returns>A smooth pseudo-random value in <c>[−1, 1]</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="octaves"/> is outside <c>[1, 16]</c>.</exception>
    public static FixedQ4816 Sample(in FieldNoiseSeed seed, FixedVector3 position, int octaves) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: octaves,
            other: 1
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: octaves,
            other: MaximumOctaveCount
        );

        var accumulated = 0L;
        var octaveSeedValue = seed.Seed;
        var octaveSeed = seed;
        var xCell = (position.X.Value >> FixedQ4816.FractionBitCount);
        var yCell = (position.Y.Value >> FixedQ4816.FractionBitCount);
        var zCell = (position.Z.Value >> FixedQ4816.FractionBitCount);
        var xFraction = position.X.Value & FractionMask;
        var yFraction = position.Y.Value & FractionMask;
        var zFraction = position.Z.Value & FractionMask;

        for (var octave = 0; (octave < octaves); ++octave) {
            // Each octave's halving rounds to nearest on the magnitude; a floor would droop the sum by up to half a raw
            // unit per octave, one-signed, so two octave counts of one field would disagree on its mean.
            accumulated += RoundShift(
                shift: (octave + 1),
                value: SampleLattice(
                    seed: octaveSeed,
                    x0: xCell,
                    xFraction: xFraction,
                    y0: yCell,
                    yFraction: yFraction,
                    z0: zCell,
                    zFraction: zFraction
                )
            );
            octaveSeedValue = unchecked((octaveSeedValue + OctaveSeedStep));
            octaveSeed = Prepare(seed: octaveSeedValue);

            if ((octave + 1) < octaves) {
                DoubleFrequency(
                    cell: ref xCell,
                    fraction: ref xFraction
                );
                DoubleFrequency(
                    cell: ref yCell,
                    fraction: ref yFraction
                );
                DoubleFrequency(
                    cell: ref zCell,
                    fraction: ref zFraction
                );
            }
        }

        return new(Value: accumulated);
    }
    /// <summary>Samples the smooth noise field at a hierarchical world position, exact at planet scale.</summary>
    /// <param name="seed">The field seed; distinct seeds yield domain-separated, decorrelated fields.</param>
    /// <param name="position">The position to sample; one noise unit spans one world unit.</param>
    /// <returns>A smooth pseudo-random value in <c>[−1, 1]</c>.</returns>
    /// <remarks>The cell index and local whole-unit offset form a signed 128-bit lattice coordinate, so no cell bits
    /// are discarded before hashing. Coordinates whose absolute lattice position fits <see cref="long"/> retain the
    /// exact same samples as the flat <see cref="FixedVector3"/> overload.</remarks>
    public static FixedQ4816 Sample(ulong seed, FixedPosition position) =>
        Sample(
            position: position,
            seed: Prepare(seed: seed)
        );
    /// <summary>Samples the smooth noise field at a hierarchical world position under a prepared seed.</summary>
    /// <param name="seed">The prepared seed.</param>
    /// <param name="position">The position to sample; one noise unit spans one world unit.</param>
    /// <returns>A smooth pseudo-random value in <c>[−1, 1]</c>.</returns>
    public static FixedQ4816 Sample(in FieldNoiseSeed seed, FixedPosition position) {
        var x0 = ((((Int128)position.CellX) << FixedPosition.CellSizeLog2) + (position.Local.X.Value >> FixedQ4816.FractionBitCount));
        var y0 = ((((Int128)position.CellY) << FixedPosition.CellSizeLog2) + (position.Local.Y.Value >> FixedQ4816.FractionBitCount));
        var z0 = ((((Int128)position.CellZ) << FixedPosition.CellSizeLog2) + (position.Local.Z.Value >> FixedQ4816.FractionBitCount));
        var xFraction = position.Local.X.Value & FractionMask;
        var yFraction = position.Local.Y.Value & FractionMask;
        var zFraction = position.Local.Z.Value & FractionMask;

        // Most worlds still sit inside one signed-64-bit lattice. Preserve that hot path and pay for the wide hash only
        // when a coordinate (or its +1 interpolation corner) genuinely needs the hierarchical high word.
        if (
            FitsNativeLattice(value: x0) &&
            FitsNativeLattice(value: y0) &&
            FitsNativeLattice(value: z0)
        ) {
            return new(Value: SampleLattice(
                seed: seed,
                x0: ((long)x0),
                xFraction: xFraction,
                y0: ((long)y0),
                yFraction: yFraction,
                z0: ((long)z0),
                zFraction: zFraction
            ));
        }

        return new(Value: SampleWideLattice(
            seed: seed,
            x0: x0,
            xFraction: xFraction,
            y0: y0,
            yFraction: yFraction,
            z0: z0,
            zFraction: zFraction
        ));
    }
    /// <summary>Samples the smooth noise field and its exact analytic gradient at a position, in one pass.</summary>
    /// <param name="seed">The field seed; distinct seeds yield domain-separated, decorrelated fields.</param>
    /// <param name="position">The position to sample; one noise unit spans one world unit.</param>
    /// <param name="gradient">The field's rate of change per world unit along each axis.</param>
    /// <returns>A smooth pseudo-random value in <c>[−1, 1]</c>, identical to <see cref="Sample(ulong, FixedVector3)"/>.</returns>
    /// <remarks>The eight corner hashes — the sampler's real cost — are shared between the value and all three
    /// partials, so this is far cheaper than differencing <see cref="Sample(ulong, FixedVector3)"/> and carries no
    /// step-size choice. The gradient is continuous across cell boundaries because the quintic fade's derivative
    /// vanishes at both ends. Each component lies in <c>[−3.75, 3.75]</c>.</remarks>
    public static FixedQ4816 SampleGradient(ulong seed, FixedVector3 position, out FixedVector3 gradient) =>
        SampleGradient(
            gradient: out gradient,
            position: position,
            seed: Prepare(seed: seed)
        );
    /// <summary>Samples the field and its gradient at a position under a prepared seed.</summary>
    /// <param name="seed">The prepared seed.</param>
    /// <param name="position">The position to sample.</param>
    /// <param name="gradient">Receives the field's gradient at the position.</param>
    /// <returns>The field value at the position.</returns>
    public static FixedQ4816 SampleGradient(in FieldNoiseSeed seed, FixedVector3 position, out FixedVector3 gradient) {
        Span<long> corners = stackalloc long[8];

        LoadCorners(
            seed: seed,
            x0: (position.X.Value >> FixedQ4816.FractionBitCount),
            y0: (position.Y.Value >> FixedQ4816.FractionBitCount),
            z0: (position.Z.Value >> FixedQ4816.FractionBitCount),
            corners: corners
        );

        return new(Value: BlendCornersWithGradient(
            corners: corners,
            xFraction: position.X.Value & FractionMask,
            yFraction: position.Y.Value & FractionMask,
            zFraction: position.Z.Value & FractionMask,
            gradient: out gradient
        ));
    }
}
/// <summary>
/// A <see cref="FieldNoise"/> seed with its three per-axis domain states derived once — the state every sample under
/// one seed would otherwise re-derive. Obtained from <see cref="FieldNoise.Prepare"/>; every <see cref="FieldNoise"/>
/// overload taking it returns the same bits as its <see cref="ulong"/> twin.
/// </summary>
public readonly struct FieldNoiseSeed {
    internal readonly ulong X;
    internal readonly ulong Y;
    internal readonly ulong Z;

    internal FieldNoiseSeed(ulong seed, ulong x, ulong y, ulong z) {
        Seed = seed;
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>Gets the field seed the states were derived from.</summary>
    public ulong Seed { get; }
}
