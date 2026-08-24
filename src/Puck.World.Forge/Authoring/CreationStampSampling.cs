using Puck.Maths;

namespace Puck.Forge.Authoring;

/// <summary>
/// Deterministic hash-lattice placement sampling — the document-neutral engine behind a placement distribution's
/// Noise/Scatter regions (see <c>Puck.World.WorldDistributionRegion.Noise</c>/<c>.Scatter</c>), resolving the
/// placement-local instance offsets a static placement multiplies into. Mirrors
/// <c>Puck.World.Server.WorldFieldLattice</c>'s Noise/Scatter field fills EXACTLY — the same integer PCG3D mix, the
/// same quintic-smoothed value noise, the same Q48.16 arithmetic throughout, and the same seed fold against the
/// world seed — so a scattered stand of trees and a patchy field paint agree on what "the same seed" means. Every
/// decision is integer/fixed-point; no float ever enters the admission or position math (placements feed colliders,
/// which are simulation state). KEEP the hash mix IN SYNC with <c>WorldFieldLattice.Pcg3d</c> and
/// <c>sdfPcg3d</c> (<c>Assets/Shaders/Sdf/sdf-vm.hlsli</c>).
/// </summary>
public static class CreationStampSampling {
    // Integer PCG3D (Jarzynski & Olano) — see WorldFieldLattice.Pcg3d's own remark. Forge cannot reference
    // Puck.World.Server (the reverse dependency direction), so this is a deliberate, small, bit-identical copy —
    // the same convention ShaderIsa.Pcg3d already keeps for its own layer.
    private static (uint X, uint Y, uint Z) Pcg3d(uint x, uint y, uint z) {
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
    // A corner's [0, 1) value in Q48.16 — the hash's top 16 bits ARE the fractional ticks.
    private static FixedQ4816 Corner01(uint cellX, uint cellZ, uint seed) => FixedQ4816.FromRawBits(value: (long)(Pcg3d(x: cellX, y: cellZ, z: seed).X >> 16));
    // Quintic fade 6t^5-15t^4+10t^3 in Q48.16 — exact for t in [0, 1].
    private static FixedQ4816 Quintic(FixedQ4816 t) {
        var t2 = (t * t);
        var t3 = (t2 * t);

        return (t3 * (((t * ((t * FixedQ4816.FromInteger(value: 6)) - FixedQ4816.FromInteger(value: 15)))) + FixedQ4816.FromInteger(value: 10)));
    }
    private static FixedQ4816 Lerp(FixedQ4816 a, FixedQ4816 b, FixedQ4816 t) => (a + ((b - a) * t));
    // One octave of 2D value noise over the placement-local CELL INDEX (XZ), Q48.16 throughout.
    private static FixedQ4816 ValueNoise01(int cellX, int cellZ, int noiseCells, uint seed) {
        var nx = (cellX / noiseCells);
        var nz = (cellZ / noiseCells);
        var fx = (FixedQ4816.FromInteger(value: (cellX - (nx * noiseCells))) / FixedQ4816.FromInteger(value: noiseCells));
        var fz = (FixedQ4816.FromInteger(value: (cellZ - (nz * noiseCells))) / FixedQ4816.FromInteger(value: noiseCells));
        var ux = Quintic(t: fx);
        var uz = Quintic(t: fz);
        var c00 = Corner01(cellX: (uint)nx, cellZ: (uint)nz, seed: seed);
        var c10 = Corner01(cellX: (uint)(nx + 1), cellZ: (uint)nz, seed: seed);
        var c01 = Corner01(cellX: (uint)nx, cellZ: (uint)(nz + 1), seed: seed);
        var c11 = Corner01(cellX: (uint)(nx + 1), cellZ: (uint)(nz + 1), seed: seed);

        return Lerp(
            a: Lerp(a: c00, b: c10, t: ux),
            b: Lerp(a: c01, b: c11, t: ux),
            t: uz
        );
    }
    private static FixedQ4816 GridHalfExtent(FixedQ4816 cellSize, int count) => ((cellSize * FixedQ4816.FromInteger(value: count)) / FixedQ4816.FromInteger(value: 2));
    private static uint FoldSeed(uint seed, ulong worldSeed) => unchecked((uint)(seed ^ (uint)worldSeed ^ (uint)(worldSeed >> 32)));

    /// <summary>The exact, seed-independent block count a Scatter region materializes — every block yields exactly
    /// one jittered instance, so the count needs no hash evaluation: ceil(width/spacing) x ceil(depth/spacing).</summary>
    /// <param name="width">Cells along the placement's local +X.</param>
    /// <param name="depth">Cells along the placement's local +Z.</param>
    /// <param name="spacing">The scatter block edge in cells (clamped to at least 2, matching resolution).</param>
    public static long ScatterInstanceCeiling(int width, int depth, int spacing) {
        var effectiveSpacing = Math.Max(val1: 2, val2: spacing);
        var blocksX = ((Math.Max(val1: width, val2: 0) + (effectiveSpacing - 1)) / effectiveSpacing);
        var blocksZ = ((Math.Max(val1: depth, val2: 0) + (effectiveSpacing - 1)) / effectiveSpacing);

        return (((long)blocksX) * blocksZ);
    }
    /// <summary>The worst-case, seed-independent cell count a Noise region could ever admit — width x depth, the
    /// threshold-approaches-zero limit where every cell passes. The ACTUAL admitted count is threshold/seed
    /// dependent and only known by resolving (<see cref="ResolveNoise"/>); this bound is what the document validator
    /// checks against the engine's static-instance ceiling without paying for the fBm evaluation on every apply.</summary>
    /// <param name="width">Cells along the placement's local +X.</param>
    /// <param name="depth">Cells along the placement's local +Z.</param>
    public static long NoiseInstanceCeiling(int width, int depth) => (((long)Math.Max(val1: width, val2: 0)) * Math.Max(val1: depth, val2: 0));

    /// <summary>Resolves a Scatter region's one-jittered-point-per-block local offsets (Y = 0), centered on the
    /// placement origin — integer PCG3D hash, Q48.16 throughout, the placement twin of
    /// <c>WorldFieldLattice.ApplyScatterFill</c>'s per-block jitter (unlike the field fill's disc admission, every
    /// block here yields exactly the one jittered POINT itself, never a filled neighborhood).</summary>
    /// <param name="cellSize">The local grid's cubic cell edge, world units.</param>
    /// <param name="width">Cells along the placement's local +X.</param>
    /// <param name="depth">Cells along the placement's local +Z.</param>
    /// <param name="spacing">The scatter block edge in cells (at least 2).</param>
    /// <param name="radius">The jitter inset in cells (at least 1; at most spacing/2 — a point never leaves its block).</param>
    /// <param name="seed">The hash seed, folded with <paramref name="worldSeed"/>.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>).</param>
    public static FixedVector3[] ResolveScatter(FixedQ4816 cellSize, int width, int depth, int spacing, int radius, uint seed, ulong worldSeed) {
        var effectiveWidth = Math.Max(val1: width, val2: 0);
        var effectiveDepth = Math.Max(val1: depth, val2: 0);
        var effectiveSpacing = Math.Max(val1: 2, val2: spacing);
        var effectiveRadius = Math.Max(val1: 1, val2: radius);
        var blocksX = ((effectiveWidth + (effectiveSpacing - 1)) / effectiveSpacing);
        var blocksZ = ((effectiveDepth + (effectiveSpacing - 1)) / effectiveSpacing);
        var hashSeed = FoldSeed(seed: seed, worldSeed: worldSeed);
        var inset = Math.Max(val1: 0, val2: (effectiveSpacing - (2 * effectiveRadius)));
        var originX = (-GridHalfExtent(cellSize: cellSize, count: effectiveWidth));
        var originZ = (-GridHalfExtent(cellSize: cellSize, count: effectiveDepth));
        var offsets = new FixedVector3[(blocksX * blocksZ)];
        var index = 0;

        for (var bz = 0; (bz < blocksZ); bz++) {
            for (var bx = 0; (bx < blocksX); bx++) {
                var h = Pcg3d(
                    x: unchecked((uint)bx),
                    y: unchecked((uint)bz),
                    z: hashSeed
                );
                // The point sits inside its block, radius-inset so it (and the creation's own footprint) stays clear
                // of the block edge — the same inset WorldFieldLattice.ApplyScatterFill derives its disc from.
                var px = ((bx * effectiveSpacing) + effectiveRadius + ((inset > 0) ? (int)(h.X % (uint)inset) : 0));
                var pz = ((bz * effectiveSpacing) + effectiveRadius + ((inset > 0) ? (int)(h.Y % (uint)inset) : 0));

                offsets[index++] = new FixedVector3(
                    X: (originX + (cellSize * FixedQ4816.FromInteger(value: px))),
                    Y: FixedQ4816.Zero,
                    Z: (originZ + (cellSize * FixedQ4816.FromInteger(value: pz)))
                );
            }
        }

        return offsets;
    }
    /// <summary>Resolves a Noise region's admitted-cell local offsets (Y = 0), centered on the placement origin —
    /// fixed-point hash-lattice fBm over the cell index, threshold-admitted, the placement twin of
    /// <c>WorldFieldLattice.ApplyNoiseFill</c>. One instance per admitted cell, at the cell's center.</summary>
    /// <param name="cellSize">The local grid's cubic cell edge, world units.</param>
    /// <param name="width">Cells along the placement's local +X.</param>
    /// <param name="depth">Cells along the placement's local +Z.</param>
    /// <param name="frequency">Noise-cell edge in lattice cells. At least 1.</param>
    /// <param name="threshold">The patch admission level in [0, 1).</param>
    /// <param name="octaves">Octave count, 1..4.</param>
    /// <param name="seed">The hash seed, folded with <paramref name="worldSeed"/>.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>).</param>
    public static FixedVector3[] ResolveNoise(FixedQ4816 cellSize, int width, int depth, int frequency, FixedQ4816 threshold, int octaves, uint seed, ulong worldSeed) {
        var effectiveWidth = Math.Max(val1: width, val2: 0);
        var effectiveDepth = Math.Max(val1: depth, val2: 0);
        var effectiveOctaves = Math.Max(val1: 1, val2: octaves);
        var hashSeed = FoldSeed(seed: seed, worldSeed: worldSeed);
        var half = (cellSize / FixedQ4816.FromInteger(value: 2));
        var originX = (-GridHalfExtent(cellSize: cellSize, count: effectiveWidth));
        var originZ = (-GridHalfExtent(cellSize: cellSize, count: effectiveDepth));
        var offsets = new List<FixedVector3>(capacity: (effectiveWidth * effectiveDepth));

        for (var z = 0; (z < effectiveDepth); z++) {
            for (var x = 0; (x < effectiveWidth); x++) {
                // fBm: per-octave halved amplitude, halved noise-cell edge (floored at 1), decorrelated seed stream —
                // the SAME construction WorldFieldLattice.ApplyNoiseFill sums.
                var amplitude = FixedQ4816.One;
                var total = FixedQ4816.Zero;
                var weight = FixedQ4816.Zero;
                var cells = frequency;

                for (var octave = 0; (octave < effectiveOctaves); octave++) {
                    total += (amplitude * ValueNoise01(
                        cellX: x,
                        cellZ: z,
                        noiseCells: Math.Max(val1: 1, val2: cells),
                        seed: unchecked(hashSeed + ((uint)octave * 0x9E3779B9u))
                    ));
                    weight += amplitude;
                    amplitude = FixedQ4816.FromRawBits(value: (amplitude.Value >> 1));
                    cells = Math.Max(val1: 1, val2: (cells >> 1));
                }

                var n = (total / weight);

                if (n < threshold) {
                    continue;
                }

                offsets.Add(item: new FixedVector3(
                    X: ((originX + (cellSize * FixedQ4816.FromInteger(value: x))) + half),
                    Y: FixedQ4816.Zero,
                    Z: ((originZ + (cellSize * FixedQ4816.FromInteger(value: z))) + half)
                ));
            }
        }

        return offsets.ToArray();
    }
}
