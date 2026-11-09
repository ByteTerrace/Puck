namespace Puck.Maths;

/// <summary>
/// Deterministic PCG3D hash-lattice value noise and scatter, Q48.16 throughout — the shared home behind a world
/// field-lattice fill and a placement-distribution region: a world's live <c>fields</c> section (its Noise/Scatter cell
/// fills) and a static placement's own Noise/Scatter distribution regions both route their corner hash, quintic-smoothed
/// value noise and jittered scatter points through this type, so a patchy field and a scattered stand of trees agree
/// bit for bit on what a seed means. Every decision is integer/fixed-point; no float ever enters the admission or
/// position math (both feed colliders, which are simulation state).
/// </summary>
/// <remarks>
/// <see cref="Pcg3d"/> is the integer mix the renderer's <c>sdfPcg3d</c> uses (Jarzynski &amp; Olano). The generated
/// <c>sdf-hash.hlsli</c> and <c>sdf-noise.hlsli</c> read their constants from the public fields here, so the C# and
/// HLSL mixes share one source.
/// </remarks>
public static class Pcg3dLatticeNoise {
    /// <summary>Knuth's LCG multiplier, the mixing core of PCG3D (<c>SDF_PCG_MULTIPLIER</c>).</summary>
    public const uint PcgMultiplier = 1664525U;
    /// <summary>Knuth's LCG increment (<c>SDF_PCG_INCREMENT</c>).</summary>
    public const uint PcgIncrement = 1013904223U;
    /// <summary>The golden-ratio decorrelation multiplier the GPU derives a second hash stream from one seed with
    /// (<c>SDF_HASH_STREAM_A</c>).</summary>
    public const uint HashStreamA = 0x9E3779B9U;
    /// <summary>The Murmur3 finalizer constant the GPU derives a third hash stream with (<c>SDF_HASH_STREAM_B</c>).</summary>
    public const uint HashStreamB = 0x85EBCA6BU;
    /// <summary>The constant keying the GPU's cell-jitter tumble stream apart from its position and material streams
    /// (<c>SDF_HASH_TUMBLE</c>).</summary>
    public const uint HashTumble = 0x27D4EB2FU;
    /// <summary>The exclusive-or the GPU's 3D value noise applies to its seed for the Y lane; equal to
    /// <see cref="HashStreamA"/>.</summary>
    public const uint NoiseSeedStreamY = 0x9E3779B9U;
    /// <summary>The exclusive-or the GPU's 3D value noise applies to its seed for the Z lane. It is not
    /// <see cref="HashStreamB"/>: the two differ in their low byte, and the noise lattice every baked field was
    /// hashed under keeps this value.</summary>
    public const uint NoiseSeedStreamZ = 0x85EBCA77U;

    // A corner's [0, 1) value in Q48.16: the hash's top 16 bits ARE the fractional ticks — integer in, integer out.
    private static FixedQ4816 Corner01(uint cellX, uint cellZ, uint seed) => FixedQ4816.FromRawBits(value: ((long)(Pcg3d(
        x: cellX,
        y: cellZ,
        z: seed
    ).X >> 16)));
    // A fade's raw scale, one in raws: a lattice fraction t is a raw in [0, FadeOne).
    private const long FadeOne = (1L << FixedQ4816.FractionBitCount);
    // The most lattice cells along one axis, and in all, whose fades ValueNoise3Bounds blends one by one; past them it
    // takes the hull of every corner the box touches, and past HullCorners corners the whole band.
    private const long BlendedCells = 8L;
    private const long HullCorners = 64L;
    // Every ValueNoise3 answer: twice a blend of corners in [0, 1), less one.
    private static readonly FixedInterval NoiseBand = new(lower: -FixedQ4816.One, upper: FixedQ4816.One);

    /// <summary>Returns the quintic fade <c>6t⁵ − 15t⁴ + 10t³</c> at a lattice fraction, rounded once: the CPU twin of
    /// the renderer's blend, which <see cref="ValueNoise01"/> and <see cref="ValueNoise3"/> weigh their corners by.</summary>
    /// <param name="t">The fraction, in <c>[0, 1]</c>.</param>
    /// <returns>The exact polynomial at <paramref name="t"/> rounded to nearest, ties to even: within half a raw of it,
    /// in <c>[0, 1]</c>, and non-decreasing in <paramref name="t"/>, since the exact fade rises over <c>[0, 1]</c> and
    /// rounding keeps order.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="t"/> lies outside <c>[0, 1]</c>.</exception>
    public static FixedQ4816 QuinticFade(FixedQ4816 t) {
        var r = t.Value;

        if (((ulong)r) > ((ulong)FadeOne)) {
            ThrowFadeOutOfRange(t: t);
        }

        // r³·(6r² − 15·2¹⁶·r + 10·2³²) at scale 2⁸⁰, exact: under 2⁴⁸ times under 11·2³².
        Int128 wide = r;
        var inner = ((((6L * r) - (15L * FadeOne)) * r) + (10L * FadeOne * FadeOne));

        return FixedQ4816.FromRawBits(value: FixedQ4816.RoundProduct(fractionBitCount: 64, product: (((wide * r) * r) * inner)));
    }
    private static void ThrowFadeOutOfRange(FixedQ4816 t) =>
        throw new ArgumentOutOfRangeException(paramName: nameof(t), message: $"A fade's fraction must lie in [0, 1]; got {t}.");

    /// <summary>Mixes a 3D integer coordinate into three well-avalanched 32-bit lanes.</summary>
    /// <param name="x">The X lane input.</param>
    /// <param name="y">The Y lane input.</param>
    /// <param name="z">The Z lane input.</param>
    /// <returns>The three mixed lanes.</returns>
    public static (uint X, uint Y, uint Z) Pcg3d(uint x, uint y, uint z) {
        unchecked {
            x = ((x * PcgMultiplier) + PcgIncrement);
            y = ((y * PcgMultiplier) + PcgIncrement);
            z = ((z * PcgMultiplier) + PcgIncrement);
            x += (y * z); y += (z * x); z += (x * y);
            x ^= (x >> 16); y ^= (y >> 16); z ^= (z >> 16);
            x += (y * z); y += (z * x); z += (x * y);

            return (x, y, z);
        }
    }
    /// <summary>Returns the one jittered point a scatter block holds: its origin plus the radius inset, plus a hash
    /// offset inside the inset band, so the point's disc never leaves the block.</summary>
    /// <param name="blockX">The block index along X.</param>
    /// <param name="blockZ">The block index along Z.</param>
    /// <param name="seed">The hash seed.</param>
    /// <param name="spacing">The block edge in cells.</param>
    /// <param name="radius">The radius inset from the block's edge, in cells.</param>
    /// <returns>The point's cell coordinates: <c>block·spacing + radius</c> on each axis, plus the matching
    /// <see cref="Pcg3d"/> lane (X for X, Y for Z) modulo the inset <c>max(0, spacing − 2·radius)</c> when that inset
    /// is positive.</returns>
    public static (int X, int Z) ScatterPoint(int blockX, int blockZ, uint seed, int spacing, int radius) {
        var hash = Pcg3d(
            x: unchecked((uint)blockX),
            y: unchecked((uint)blockZ),
            z: seed
        );
        var inset = Math.Max(
            val1: 0,
            val2: (spacing - (2 * radius))
        );

        return (
            (((blockX * spacing) + radius) + ((inset > 0)
                ? ((int)(hash.X % ((uint)inset)))
                : 0)),
            (((blockZ * spacing) + radius) + ((inset > 0)
                ? ((int)(hash.Y % ((uint)inset)))
                : 0))
        );
    }
    /// <summary>Samples one octave of 3D value noise at a lattice point, Q48.16 throughout: the CPU twin of the
    /// renderer's <c>sdfValueNoise3</c>, which a relieved creation's bake and its contact both read.</summary>
    /// <param name="q">The point in lattice cells; the cell is its floor on each axis.</param>
    /// <param name="seedX">The seed each corner's X lane is keyed with.</param>
    /// <param name="seedY">The seed each corner's Y lane is keyed with.</param>
    /// <param name="seedZ">The seed each corner's Z lane is keyed with.</param>
    /// <returns>The quintic-smoothed trilinear blend of the eight corners' <see cref="Pcg3d"/> X lanes (each the top 16
    /// bits as a fraction in <c>[0, 1)</c>), remapped to <c>[-1, 1)</c>.</returns>
    public static FixedQ4816 ValueNoise3(FixedVector3 q, uint seedX, uint seedY, uint seedZ) {
        var cx = (q.X.Value >> FixedQ4816.FractionBitCount);
        var cy = (q.Y.Value >> FixedQ4816.FractionBitCount);
        var cz = (q.Z.Value >> FixedQ4816.FractionBitCount);
        var ux = QuinticFade(t: (q.X - FixedQ4816.FromInteger(value: cx)));
        var uy = QuinticFade(t: (q.Y - FixedQ4816.FromInteger(value: cy)));
        var uz = QuinticFade(t: (q.Z - FixedQ4816.FromInteger(value: cz)));
        ref readonly var cell = ref CellCorners(seedX: seedX, seedY: seedY, seedZ: seedZ, x: cx, y: cy, z: cz);
        var x00 = FixedQ4816.Lerp(amount: ux, from: cell.C000, to: cell.C100);
        var x10 = FixedQ4816.Lerp(amount: ux, from: cell.C010, to: cell.C110);
        var x01 = FixedQ4816.Lerp(amount: ux, from: cell.C001, to: cell.C101);
        var x11 = FixedQ4816.Lerp(amount: ux, from: cell.C011, to: cell.C111);
        var y0 = FixedQ4816.Lerp(amount: uy, from: x00, to: x10);
        var y1 = FixedQ4816.Lerp(amount: uy, from: x01, to: x11);

        return ((FixedQ4816.Lerp(amount: uz, from: y0, to: y1) * FixedQ4816.FromInteger(value: 2)) - FixedQ4816.One);
    }

    // One lattice cell's eight corner values under one seed triple, the cell its least corner.
    private struct LatticeCell {
        public bool Filled;
        public uint SeedX, SeedY, SeedZ;
        public long X, Y, Z;
        public FixedQ4816 C000, C100, C010, C110, C001, C101, C011, C111;
    }

    // The cells this thread read last, direct-mapped by key: a field read and its nearby reads (a gradient's taps, a
    // ray's samples) fall in the same cell of every octave, so they hash its corners once. The corners are a pure
    // function of the key, so a cached cell answers exactly what hashing it again would.
    [ThreadStatic]
    private static LatticeCell[]? LatticeCells;
    private const int CellCacheBits = 6;

    private static ref readonly LatticeCell CellCorners(uint seedX, uint seedY, uint seedZ, long x, long y, long z) {
        var cells = (LatticeCells ??= new LatticeCell[1 << CellCacheBits]);
        var slot = unchecked((int)((((seedX * HashStreamA) ^ ((uint)x * PcgMultiplier)) ^ (((uint)y * HashStreamB) ^ ((uint)z * HashTumble))) >> (32 - CellCacheBits)));
        ref var cell = ref cells[slot];

        if (!cell.Filled || (cell.SeedX != seedX) || (cell.SeedY != seedY) || (cell.SeedZ != seedZ) || (cell.X != x) || (cell.Y != y) || (cell.Z != z)) {
            cell = new LatticeCell {
                Filled = true,
                SeedX = seedX,
                SeedY = seedY,
                SeedZ = seedZ,
                X = x,
                Y = y,
                Z = z,
                C000 = Corner(x: x, y: y, z: z),
                C100 = Corner(x: (x + 1L), y: y, z: z),
                C010 = Corner(x: x, y: (y + 1L), z: z),
                C110 = Corner(x: (x + 1L), y: (y + 1L), z: z),
                C001 = Corner(x: x, y: y, z: (z + 1L)),
                C101 = Corner(x: (x + 1L), y: y, z: (z + 1L)),
                C011 = Corner(x: x, y: (y + 1L), z: (z + 1L)),
                C111 = Corner(x: (x + 1L), y: (y + 1L), z: (z + 1L)),
            };
        }

        return ref cell;

        FixedQ4816 Corner(long x, long y, long z) => FixedQ4816.FromRawBits(value: ((long)(Pcg3d(
            x: (unchecked((uint)x) ^ seedX),
            y: (unchecked((uint)y) ^ seedY),
            z: (unchecked((uint)z) ^ seedZ)
        ).X >> 16)));
    }
    /// <summary>Sums octaves of <see cref="ValueNoise3"/> as the renderer's <c>NoiseDisplace</c> does: octave
    /// <c>k</c> samples at <c>q·lacunarityᵏ</c> with seed <c>seed + k</c> (its Y and Z lanes keyed by that seed times
    /// <see cref="HashStreamA"/> and <see cref="HashStreamB"/>) and weighs <c>gainᵏ</c>.</summary>
    /// <param name="q">The first octave's point in lattice cells.</param>
    /// <param name="seed">The first octave's seed.</param>
    /// <param name="octaves">The octave count; none sums to zero.</param>
    /// <param name="gain">Each octave's weight over the one before.</param>
    /// <param name="lacunarity">Each octave's frequency over the one before.</param>
    /// <returns>The weighted sum, each term in <c>[-1, 1)</c> times its weight; the caller normalizes and scales it.</returns>
    public static FixedQ4816 Fbm3(FixedVector3 q, uint seed, int octaves, FixedQ4816 gain, FixedQ4816 lacunarity) {
        var weight = FixedQ4816.One;
        var sum = FixedQ4816.Zero;

        for (var octave = 0u; (octave < octaves); octave++) {
            var octaveSeed = unchecked(seed + octave);

            sum += (weight * ValueNoise3(q: q, seedX: octaveSeed, seedY: unchecked(octaveSeed * HashStreamA), seedZ: unchecked(octaveSeed * HashStreamB)));
            q *= lacunarity;
            weight *= gain;
        }

        return sum;
    }
    /// <summary>Encloses every <see cref="ValueNoise3"/> answer over a box of lattice points.</summary>
    /// <param name="x">The box's extent along X, in lattice cells.</param>
    /// <param name="y">The box's extent along Y, in lattice cells.</param>
    /// <param name="z">The box's extent along Z, in lattice cells.</param>
    /// <param name="seedX">The seed each corner's X lane is keyed with.</param>
    /// <param name="seedY">The seed each corner's Y lane is keyed with.</param>
    /// <param name="seedZ">The seed each corner's Z lane is keyed with.</param>
    /// <returns>An interval holding <see cref="ValueNoise3"/> at every representable point of the box: certified, not
    /// estimated, and never wider than <c>[-1, 1]</c>, which an unbounded box answers.</returns>
    /// <remarks>
    /// <para>Inside one cell the noise is a trilinear blend of its corners over the fades <c>u</c>, and a trilinear
    /// function takes its extremes over a box of <c>u</c> at the box's vertices. The fade is non-decreasing
    /// (<see cref="QuinticFade"/>), so the fades of a box of fractions lie between those of its ends, and the blend's
    /// exact range over them is read at its eight vertices, exactly, in integers. The point blend rounds each of its
    /// three nested lerps once, so it lies within a raw and a half of the exact blend at the same fades; the range is
    /// widened by two raws a side for it, and held to the hull of the cell's corners, which every lerp of them stays
    /// inside.</para>
    /// <para>A box over more than eight cells takes the hull of every corner it touches instead, and one over more than
    /// sixty-four corners the whole band. Each rule only widens as the box grows, so a box inside another answers inside
    /// the other's interval.</para>
    /// </remarks>
    public static FixedInterval ValueNoise3Bounds(FixedInterval x, FixedInterval y, FixedInterval z, uint seedX, uint seedY, uint seedZ) {
        if (x.IsUnbounded || y.IsUnbounded || z.IsUnbounded) {
            return NoiseBand;
        }

        var (x0, x1) = (x.Lower.Value >> FixedQ4816.FractionBitCount, x.Upper.Value >> FixedQ4816.FractionBitCount);
        var (y0, y1) = (y.Lower.Value >> FixedQ4816.FractionBitCount, y.Upper.Value >> FixedQ4816.FractionBitCount);
        var (z0, z1) = (z.Lower.Value >> FixedQ4816.FractionBitCount, z.Upper.Value >> FixedQ4816.FractionBitCount);
        // The cells the box touches along each axis; a span past the hull's corner count along any axis answers the band
        // before the product of the spans can overflow.
        var (nx, ny, nz) = (((x1 - x0) + 1L), ((y1 - y0) + 1L), ((z1 - z0) + 1L));

        if ((nx >= HullCorners) || (ny >= HullCorners) || (nz >= HullCorners) || (((nx + 1L) * (ny + 1L) * (nz + 1L)) > HullCorners)) {
            return NoiseBand;
        }

        var least = long.MaxValue;
        var most = long.MinValue;

        if ((nx * ny * nz) > BlendedCells) {
            for (var cz = z0; (cz <= z1); cz++) {
                for (var cy = y0; (cy <= y1); cy++) {
                    for (var cx = x0; (cx <= x1); cx++) {
                        ref readonly var cell = ref CellCorners(seedX: seedX, seedY: seedY, seedZ: seedZ, x: cx, y: cy, z: cz);
                        var (low, high) = CornerHull(cell: in cell);

                        least = Math.Min(val1: least, val2: low);
                        most = Math.Max(val1: most, val2: high);
                    }
                }
            }
        } else {
            for (var cz = z0; (cz <= z1); cz++) {
                var (uz0, uz1) = Fades(cell: cz, extent: z);

                for (var cy = y0; (cy <= y1); cy++) {
                    var (uy0, uy1) = Fades(cell: cy, extent: y);

                    for (var cx = x0; (cx <= x1); cx++) {
                        var (ux0, ux1) = Fades(cell: cx, extent: x);
                        ref readonly var cell = ref CellCorners(seedX: seedX, seedY: seedY, seedZ: seedZ, x: cx, y: cy, z: cz);
                        var (low, high) = BlendRange(cell: in cell, ux0: ux0, ux1: ux1, uy0: uy0, uy1: uy1, uz0: uz0, uz1: uz1);

                        least = Math.Min(val1: least, val2: low);
                        most = Math.Max(val1: most, val2: high);
                    }
                }
            }
        }

        // Twice the blend less one, exact on the grid.
        return new FixedInterval(lower: FixedQ4816.FromRawBits(value: ((2L * least) - FadeOne)), upper: FixedQ4816.FromRawBits(value: ((2L * most) - FadeOne)));
    }
    /// <summary>Encloses every <see cref="Fbm3"/> answer over a box of first-octave lattice points: each octave's
    /// <see cref="ValueNoise3Bounds"/> over the box carried to that octave by the same rounded scaling
    /// (<see cref="FixedInterval.RoundedProduct"/>), weighed and summed in outward-rounded interval arithmetic.</summary>
    /// <param name="x">The box's extent along X, in first-octave lattice cells.</param>
    /// <param name="y">The box's extent along Y, in first-octave lattice cells.</param>
    /// <param name="z">The box's extent along Z, in first-octave lattice cells.</param>
    /// <param name="seed">The first octave's seed.</param>
    /// <param name="octaves">The octave count; none sums to zero.</param>
    /// <param name="gain">Each octave's weight over the one before.</param>
    /// <param name="lacunarity">Each octave's frequency over the one before.</param>
    /// <returns>An interval holding <see cref="Fbm3"/> at every representable point of the box; unbounded only where a
    /// weighted sum leaves the carrier.</returns>
    public static FixedInterval Fbm3Bounds(FixedInterval x, FixedInterval y, FixedInterval z, uint seed, int octaves, FixedQ4816 gain, FixedQ4816 lacunarity) {
        var weight = FixedQ4816.One;
        var sum = FixedInterval.FromPoint(value: FixedQ4816.Zero);

        for (var octave = 0u; (octave < octaves); octave++) {
            var octaveSeed = unchecked(seed + octave);

            sum += (FixedInterval.FromPoint(value: weight) * ValueNoise3Bounds(seedX: octaveSeed, seedY: unchecked(octaveSeed * HashStreamA),
                seedZ: unchecked(octaveSeed * HashStreamB), x: x, y: y, z: z));
            (x, y, z) = (FixedInterval.RoundedProduct(factor: lacunarity, value: x), FixedInterval.RoundedProduct(factor: lacunarity, value: y),
                FixedInterval.RoundedProduct(factor: lacunarity, value: z));
            weight *= gain;
        }

        return sum;
    }
    /// <summary>Samples fractal value noise over a cell index (XZ), Q48.16 throughout: the one construction a world
    /// field-lattice Noise fill and a placement Noise region both sum. Octave <c>k</c> is <see cref="ValueNoise01"/> over a
    /// noise-cell edge of <c>frequency</c> halved <c>k</c> times (floored at one cell), seeded <c>seed + k·</c><see
    /// cref="HashStreamA"/>, and weighs the amplitude one halved <c>k</c> times (a raw shift); the sum is divided by the
    /// weights' sum.</summary>
    /// <param name="cellX">The cell index along X.</param>
    /// <param name="cellZ">The cell index along Z.</param>
    /// <param name="frequency">The first octave's noise-cell edge in lattice cells; below one reads as one.</param>
    /// <param name="octaves">The octave count (at least 1).</param>
    /// <param name="seed">The first octave's hash seed; a caller folds the world seed into it
    /// (<see cref="PackedIntPair.FoldXor"/>).</param>
    /// <returns>The weighted mean of the octaves, in <c>[0, 1)</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="octaves"/> is below one.</exception>
    public static FixedQ4816 Fbm01(int cellX, int cellZ, int frequency, int octaves, uint seed) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1,
            value: octaves
        );

        var amplitude = FixedQ4816.One;
        var total = FixedQ4816.Zero;
        var weight = FixedQ4816.Zero;
        var cells = frequency;

        for (var octave = 0; (octave < octaves); octave++) {
            total += (amplitude * ValueNoise01(
                cellX: cellX,
                cellZ: cellZ,
                noiseCells: Math.Max(
                    val1: 1,
                    val2: cells
                ),
                seed: unchecked((seed + (((uint)octave) * HashStreamA)))
            ));
            weight += amplitude;
            amplitude = FixedQ4816.FromRawBits(value: (amplitude.Value >> 1));
            cells = Math.Max(
                val1: 1,
                val2: (cells >> 1)
            );
        }

        return (total / weight);
    }
    // The fades of the box's fractions within one cell along an axis: those of its two ends, clipped to the cell.
    private static (long Low, long High) Fades(long cell, FixedInterval extent) {
        var origin = (cell << FixedQ4816.FractionBitCount);
        var low = (Math.Max(val1: extent.Lower.Value, val2: origin) - origin);
        var high = (Math.Min(val1: extent.Upper.Value, val2: (origin + (FadeOne - 1L))) - origin);

        return (QuinticFade(t: FixedQ4816.FromRawBits(value: low)).Value, QuinticFade(t: FixedQ4816.FromRawBits(value: high)).Value);
    }
    private static (long Low, long High) CornerHull(in LatticeCell cell) {
        var low = Math.Min(val1: Math.Min(val1: Math.Min(val1: cell.C000.Value, val2: cell.C100.Value), val2: Math.Min(val1: cell.C010.Value, val2: cell.C110.Value)),
            val2: Math.Min(val1: Math.Min(val1: cell.C001.Value, val2: cell.C101.Value), val2: Math.Min(val1: cell.C011.Value, val2: cell.C111.Value)));
        var high = Math.Max(val1: Math.Max(val1: Math.Max(val1: cell.C000.Value, val2: cell.C100.Value), val2: Math.Max(val1: cell.C010.Value, val2: cell.C110.Value)),
            val2: Math.Max(val1: Math.Max(val1: cell.C001.Value, val2: cell.C101.Value), val2: Math.Max(val1: cell.C011.Value, val2: cell.C111.Value)));

        return (low, high);
    }
    // The raws one cell's rounded blend can take over a box of fades: the exact trilinear blend at the box's eight
    // vertices, each lerp a·(1 − u) + b·u formed exactly in unsigned integers (corners under 2¹⁶ and fades in [0, 2¹⁶]
    // keep the third level under 2⁶⁴), widened by two raws a side for the point blend's three roundings and held to the
    // corners' hull.
    private static (long Low, long High) BlendRange(in LatticeCell cell, long ux0, long ux1, long uy0, long uy1, long uz0, long uz1) {
        var least = ulong.MaxValue;
        var most = ulong.MinValue;

        for (var ix = 0; (ix < 2); ix++) {
            var ux = (ulong)((ix == 0) ? ux0 : ux1);
            var x00 = Blend(from: (ulong)cell.C000.Value, to: (ulong)cell.C100.Value, u: ux);
            var x10 = Blend(from: (ulong)cell.C010.Value, to: (ulong)cell.C110.Value, u: ux);
            var x01 = Blend(from: (ulong)cell.C001.Value, to: (ulong)cell.C101.Value, u: ux);
            var x11 = Blend(from: (ulong)cell.C011.Value, to: (ulong)cell.C111.Value, u: ux);

            for (var iy = 0; (iy < 2); iy++) {
                var uy = (ulong)((iy == 0) ? uy0 : uy1);
                var y0 = Blend(from: x00, to: x10, u: uy);
                var y1 = Blend(from: x01, to: x11, u: uy);

                for (var iz = 0; (iz < 2); iz++) {
                    var value = Blend(from: y0, to: y1, u: (ulong)((iz == 0) ? uz0 : uz1));

                    least = Math.Min(val1: least, val2: value);
                    most = Math.Max(val1: most, val2: value);
                }
            }
        }

        const int Scale = (3 * FixedQ4816.FractionBitCount);
        var (hullLow, hullHigh) = CornerHull(cell: in cell);
        var low = (((long)(least >> Scale)) - 2L);
        var high = ((long)(most >> Scale) + (((most & ((1UL << Scale) - 1UL)) != 0UL) ? 1L : 0L) + 2L);

        return (Math.Max(val1: low, val2: hullLow), Math.Min(val1: high, val2: hullHigh));

        static ulong Blend(ulong from, ulong to, ulong u) =>
            ((from * (((ulong)FadeOne) - u)) + (to * u));
    }
    /// <summary>Samples one octave of 2D value noise over a cell index (XZ), Q48.16 throughout.</summary>
    /// <param name="cellX">The cell index along X.</param>
    /// <param name="cellZ">The cell index along Z.</param>
    /// <param name="noiseCells">The noise-cell edge in lattice cells (at least 1).</param>
    /// <param name="seed">The hash seed for this octave.</param>
    /// <returns>A quintic-smoothed value in <c>[0, 1)</c>: the four corner hashes blended by
    /// <see cref="FixedQ4816.Lerp"/>, which rounds each blend once.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="noiseCells"/> is below one.</exception>
    public static FixedQ4816 ValueNoise01(int cellX, int cellZ, int noiseCells, uint seed) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1,
            value: noiseCells
        );

        // Floored division, so a negative cell index still yields a fractional offset in [0, 1) — truncating
        // division would hand the quintic a negative fade and extrapolate outside the corner band.
        var nx = cellX.FloorDivide(divisor: noiseCells);
        var nz = cellZ.FloorDivide(divisor: noiseCells);
        var edge = FixedQ4816.FromInteger(value: noiseCells);
        var fx = (FixedQ4816.FromInteger(value: cellX.FloorModulo(modulus: noiseCells)) / edge);
        var fz = (FixedQ4816.FromInteger(value: cellZ.FloorModulo(modulus: noiseCells)) / edge);
        var ux = QuinticFade(t: fx);
        var uz = QuinticFade(t: fz);
        var c00 = Corner01(
            cellX: ((uint)nx),
            cellZ: ((uint)nz),
            seed: seed
        );
        var c10 = Corner01(
            cellX: ((uint)(nx + 1)),
            cellZ: ((uint)nz),
            seed: seed
        );
        var c01 = Corner01(
            cellX: ((uint)nx),
            cellZ: ((uint)(nz + 1)),
            seed: seed
        );
        var c11 = Corner01(
            cellX: ((uint)(nx + 1)),
            cellZ: ((uint)(nz + 1)),
            seed: seed
        );

        return FixedQ4816.Lerp(
            amount: uz,
            from: FixedQ4816.Lerp(
                amount: ux,
                from: c00,
                to: c10
            ),
            to: FixedQ4816.Lerp(
                amount: ux,
                from: c01,
                to: c11
            )
        );
    }
}
