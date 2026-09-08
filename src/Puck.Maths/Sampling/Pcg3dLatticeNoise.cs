namespace Puck.Maths;

/// <summary>
/// Deterministic PCG3D hash-lattice value noise, Q48.16 throughout — the shared home behind a world field-lattice
/// fill and a placement-distribution region: a world's live <c>fields</c> section (its Noise/Scatter cell fills)
/// and a static placement's own Noise/Scatter distribution regions both route their corner hash and
/// quintic-smoothed value noise through this type, so a patchy field and a scattered stand of trees agree bit for
/// bit on what a seed means. Every decision is integer/fixed-point; no float ever enters the admission or
/// position math (both feed colliders, which are simulation state).
/// </summary>
/// <remarks>
/// <see cref="Pcg3d"/> is the integer mix the renderer's <c>sdfPcg3d</c> uses (Jarzynski &amp; Olano) — KEEP IN
/// SYNC with <c>sdfPcg3d</c> in <c>Assets/Shaders/Sdf/sdf-vm.hlsli</c> and with
/// <c>Puck.ShaderVm.ShaderIsa.Pcg3d</c>; that CPU/HLSL/Forge triplet is a hand-kept mirror across language
/// boundaries, separate from the C#-to-C# duplication this type exists to eliminate.
/// </remarks>
public static class Pcg3dLatticeNoise {
    // Integer PCG3D (Jarzynski & Olano) — see the type remarks for the cross-language KEEP-IN-SYNC triplet.
    /// <summary>Mixes a 3D integer coordinate into three well-avalanched 32-bit lanes.</summary>
    /// <param name="x">The X lane input.</param>
    /// <param name="y">The Y lane input.</param>
    /// <param name="z">The Z lane input.</param>
    /// <returns>The three mixed lanes.</returns>
    public static (uint X, uint Y, uint Z) Pcg3d(uint x, uint y, uint z) {
        unchecked {
            x = ((x * 1664525u) + 1013904223u);
            y = ((y * 1664525u) + 1013904223u);
            z = ((z * 1664525u) + 1013904223u);
            x += (y * z); y += (z * x); z += (x * y);
            x ^= (x >> 16); y ^= (y >> 16); z ^= (z >> 16);
            x += (y * z); y += (z * x); z += (x * y);

            return (x, y, z);
        }
    }

    // A corner's [0, 1) value in Q48.16: the hash's top 16 bits ARE the fractional ticks — integer in, integer out.
    private static FixedQ4816 Corner01(uint cellX, uint cellZ, uint seed) => FixedQ4816.FromRawBits(value: ((long)(Pcg3d(x: cellX, y: cellZ, z: seed).X >> 16)));
    // Quintic fade 6t⁵−15t⁴+10t³ in Q48.16 — the CPU twin of the renderer's blend, exact for t in [0, 1].
    private static FixedQ4816 Quintic(FixedQ4816 t) {
        var t2 = (t * t);
        var t3 = (t2 * t);

        return (t3 * (((t * ((t * FixedQ4816.FromInteger(value: 6)) - FixedQ4816.FromInteger(value: 15)))) + FixedQ4816.FromInteger(value: 10)));
    }
    private static FixedQ4816 Lerp(FixedQ4816 a, FixedQ4816 b, FixedQ4816 t) => (a + ((b - a) * t));
    // remainder / noiseCells in Q48.16, rounded to nearest with ties to even — the same bits FixedQ4816's division
    // returns for the two integers, reached by one machine-word division on the non-negative operands.
    private static FixedQ4816 FractionOfCell(int remainder, int noiseCells) {
        var numerator = (((long)remainder) << FixedQ4816.FractionBitCount);
        var quotient = (numerator / noiseCells);
        var residue = (numerator - (quotient * noiseCells));
        var twiceResidue = (residue << 1);

        if (
            (twiceResidue > noiseCells) ||
            ((twiceResidue == noiseCells) && (0L != (quotient & 1L)))
        ) {
            ++quotient;
        }

        return FixedQ4816.FromRawBits(value: quotient);
    }
    // Floored quotient and non-negative remainder for a positive divisor: the remainder always lies in [0, divisor).
    private static (int Quotient, int Remainder) FloorDivRem(int value, int divisor) {
        var quotient = (value / divisor);
        var remainder = (value - (quotient * divisor));

        if (remainder < 0) {
            --quotient;
            remainder += divisor;
        }

        return (quotient, remainder);
    }

    /// <summary>Samples one octave of 2D value noise over a cell index (XZ), Q48.16 throughout.</summary>
    /// <param name="cellX">The cell index along X.</param>
    /// <param name="cellZ">The cell index along Z.</param>
    /// <param name="noiseCells">The noise-cell edge in lattice cells (at least 1).</param>
    /// <param name="seed">The hash seed for this octave.</param>
    /// <returns>A quintic-smoothed value in <c>[0, 1)</c>.</returns>
    public static FixedQ4816 ValueNoise01(int cellX, int cellZ, int noiseCells, uint seed) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1,
            value: noiseCells
        );

        // Floored division, so a negative cell index still yields a fractional offset in [0, 1) — truncating
        // division would hand the quintic a negative fade and extrapolate outside the corner band. Identical to
        // truncating division for non-negative indices.
        var (nx, rx) = FloorDivRem(
            divisor: noiseCells,
            value: cellX
        );
        var (nz, rz) = FloorDivRem(
            divisor: noiseCells,
            value: cellZ
        );
        var fx = FractionOfCell(
            noiseCells: noiseCells,
            remainder: rx
        );
        var fz = FractionOfCell(
            noiseCells: noiseCells,
            remainder: rz
        );
        var ux = Quintic(t: fx);
        var uz = Quintic(t: fz);
        var c00 = Corner01(cellX: ((uint)nx), cellZ: ((uint)nz), seed: seed);
        var c10 = Corner01(cellX: ((uint)(nx + 1)), cellZ: ((uint)nz), seed: seed);
        var c01 = Corner01(cellX: ((uint)nx), cellZ: ((uint)(nz + 1)), seed: seed);
        var c11 = Corner01(cellX: ((uint)(nx + 1)), cellZ: ((uint)(nz + 1)), seed: seed);

        return Lerp(
            a: Lerp(a: c00, b: c10, t: ux),
            b: Lerp(a: c01, b: c11, t: ux),
            t: uz
        );
    }
}
