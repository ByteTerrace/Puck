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
    // Quintic fade 6t⁵−15t⁴+10t³ in Q48.16 — the CPU twin of the renderer's blend, exact for t in [0, 1].
    private static FixedQ4816 Quintic(FixedQ4816 t) {
        var t2 = (t * t);
        var t3 = (t2 * t);

        return (t3 * (((t * ((t * FixedQ4816.FromInteger(value: 6)) - FixedQ4816.FromInteger(value: 15)))) + FixedQ4816.FromInteger(value: 10)));
    }

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
        var ux = Quintic(t: fx);
        var uz = Quintic(t: fz);
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
