// The deterministic hashes' noise and dither: the R2 dither, value, lattice and cellular noise.
#ifndef FIELD_SDF_NOISE_HLSLI
#define FIELD_SDF_NOISE_HLSLI
// The R2 low-discrepancy lattice: alpha_i = round(2^32 / phi2^i) for the plastic number
// phi2 = 1.32471795724474602596 (the real root of x^3 = x + 1). The uint multiply wraps mod 2^32, which IS the
// fractional part of the additive recurrence — so the lattice is exact in fixed point.
#define SDF_R2_ALPHA1 3242174889u
#define SDF_R2_ALPHA2 2447445414u
// One R2 dither sample in [0, 1] from integer pixel coordinates: a "blue-ish" low-discrepancy pattern the volumes jitter
// their samples with, the same sequence the display encode dithers its quantization with (display-encode.frag.hlsl in
// Puck.Shaders). Fixed-point, so both backends compute the IDENTICAL pattern (a float frac(x/phi2 + y/phi2^2) would
// diverge and break cross-backend parity).
float sdfR2Dither(uint2 pixel) {
    uint h = ((pixel.x * SDF_R2_ALPHA1) + (pixel.y * SDF_R2_ALPHA2));

    return ((float)h * SDF_INV_2POW32);
}

// 3D value noise on the integer lattice (KEEP IN SYNC with Puck.SignedDistance.SdfOp.NoiseDisplace and
// SdfProgram.NoiseDisplaceLipschitz): one integer-only sdfPcg3d per corner keyed on the two's-complement lattice cell
// xored with the seed streams (bit-identical across both DXC targets), quintic-smoothed trilinear blend (float
// mul/add, +-1 LSB), output in [-1, 1]. The quintic slope bound ((15/8) on the corner span of 2, axes combined
// Euclidean) is what the host bakes into the step clamp.
// A cell mask of all ones is the unbounded lattice and leaves every hashed bit as it is; one less than a power of two
// wraps the lattice to that period.
float sdfNoiseCorner3(int3 cell, uint3 seed, uint cellMask) {
    return ((float)sdfPcg3d((asuint(cell) & cellMask) ^ seed).x * SDF_INV_2POW32);
}
float sdfNoiseCorner3(int3 cell, uint3 seed) {
    return sdfNoiseCorner3(cell, seed, 0xFFFFFFFFu);
}
float sdfValueNoise3Cells(float3 q, uint3 seed, uint cellMask) {
    float3 cellFloor = floor(q);
    int3 cell = int3(cellFloor);
    float3 f = (q - cellFloor);
    float3 u = (((f * f) * f) * ((f * ((f * 6.0) - 15.0)) + 10.0));
    float c000 = sdfNoiseCorner3(cell, seed, cellMask);
    float c100 = sdfNoiseCorner3((cell + int3(1, 0, 0)), seed, cellMask);
    float c010 = sdfNoiseCorner3((cell + int3(0, 1, 0)), seed, cellMask);
    float c110 = sdfNoiseCorner3((cell + int3(1, 1, 0)), seed, cellMask);
    float c001 = sdfNoiseCorner3((cell + int3(0, 0, 1)), seed, cellMask);
    float c101 = sdfNoiseCorner3((cell + int3(1, 0, 1)), seed, cellMask);
    float c011 = sdfNoiseCorner3((cell + int3(0, 1, 1)), seed, cellMask);
    float c111 = sdfNoiseCorner3((cell + int3(1, 1, 1)), seed, cellMask);
    float x00 = lerp(c000, c100, u.x);
    float x10 = lerp(c010, c110, u.x);
    float x01 = lerp(c001, c101, u.x);
    float x11 = lerp(c011, c111, u.x);
    float y0 = lerp(x00, x10, u.y);
    float y1 = lerp(x01, x11, u.y);
    return ((lerp(y0, y1, u.z) * 2.0) - 1.0);
}
float sdfValueNoise3(float3 q, uint3 seed) {
    return sdfValueNoise3Cells(q, seed, 0xFFFFFFFFu);
}
// A seeded 3D value-noise sample over the same deterministic lattice (sdfValueNoise3), folding one uint seed into
// its three hash streams so two consumers sharing a lattice (a material's wear, a volume's advection) read different
// noise fields.
uint3 sdfLatticeSeeds(uint seed) {
    return uint3(seed, (seed ^ 0x9E3779B9u), (seed ^ 0x85EBCA77u));
}
float sdfLatticeNoise3(float3 q, uint seed) {
    return sdfValueNoise3(q, sdfLatticeSeeds(seed));
}
// The same lattice wrapped to SDF_NOISE_PERIOD_CELLS on every axis (a power of two): the noise a time-advected field
// reads, whose host-baked offset is reduced by that period (SdfVolume.NoisePeriodCells), so the field joins without a
// seam wherever the offset wraps.
float sdfPeriodicNoise3(float3 q, uint seed) {
    return sdfValueNoise3Cells(q, sdfLatticeSeeds(seed), (SDF_NOISE_PERIOD_CELLS - 1u));
}
// The same normalized sum for the signed 3D periodic lattice. Preserve its [-1, 1] range; callers own shaping.
// Each octave executes the existing eight-corner sample once and counts those eight integer hashes.
// Reduce finite coordinates before integer conversion and each octave; signed remainders preserve local rounding.
float sdfPeriodicFbm3(float3 p, uint seed, uint octaves, inout uint hashes) {
    p = fmod(p, (float)SDF_NOISE_PERIOD_CELLS);
    float value = 0.0;
    float amplitude = 0.5;
    float normalization = 0.0;
    [loop] for (uint octave = 0u; octave < octaves; octave++) {
        value += amplitude * sdfPeriodicNoise3(p, seed + octave);
        hashes += 8u;
        normalization += amplitude;
        p = fmod(p * 2.0 + 17.0, (float)SDF_NOISE_PERIOD_CELLS);
        amplitude *= 0.5;
    }
    return normalization > 0.0 ? value / normalization : 0.0;
}
// The two-dimensional periodic lattice the sky's cloud layer reads: one sdfPcg3d per corner, the seed in its third
// stream, quintic-smoothed bilinear blend, output in [0, 1].
float sdfPeriodicNoise2(float2 p, uint seed) {
    float2 cellFloor = floor(p);
    uint2 cell = (asuint(int2(cellFloor)) & (SDF_NOISE_PERIOD_CELLS - 1u));
    uint2 next = ((cell + 1u) & (SDF_NOISE_PERIOD_CELLS - 1u));
    float2 f = (p - cellFloor);
    float2 u = ((f * f * f) * ((f * ((f * 6.0) - 15.0)) + 10.0));
    float a = ((float)sdfPcg3d(uint3(cell.x, cell.y, seed)).x * SDF_INV_2POW32);
    float b = ((float)sdfPcg3d(uint3(next.x, cell.y, seed)).x * SDF_INV_2POW32);
    float c = ((float)sdfPcg3d(uint3(cell.x, next.y, seed)).x * SDF_INV_2POW32);
    float d = ((float)sdfPcg3d(uint3(next.x, next.y, seed)).x * SDF_INV_2POW32);

    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}
// Periodic 2D fractal sum shared by sky and media. Integer lacunarity preserves the base lattice's period.
// The caller chooses its bounded octave count; every lattice sample executes exactly four integer hashes.
float sdfPeriodicFbm2(float2 p, uint seed, uint octaves, inout uint hashes) {
    p = fmod(p, (float)SDF_NOISE_PERIOD_CELLS);
    float value = 0.0;
    float amplitude = 0.5;
    float normalization = 0.0;
    [loop] for (uint octave = 0u; octave < octaves; octave++) {
        value += amplitude * sdfPeriodicNoise2(p, seed + octave);
        hashes += 4u;
        normalization += amplitude;
        p = fmod(p * 2.0 + 17.0, (float)SDF_NOISE_PERIOD_CELLS);
        amplitude *= 0.5;
    }
    return normalization > 0.0 ? value / normalization : 0.0;
}
// Exact 27-cell Worley field within SdfCellDisplacement's mode-specific randomness bounds.
// KEEP IN SYNC with SdfFieldEvaluator.Cells.cs: PCG streams, top 16 bits, and z/y/x visit order.
float sdfCellDistanceGrad(float3 q, uint seed, uint mode, float randomness, out float3 gradient) {
    float3 cellFloor = floor(q);
    int3 cell = int3(cellFloor);
    float3 f = q - cellFloor;
    float first = 1.0e20, second = 1.0e20;
    float3 firstDelta = 0.0, secondDelta = 0.0;
    [loop] for (int z = -1; z <= 1; z++) {
        [loop] for (int y = -1; y <= 1; y++) {
            [loop] for (int x = -1; x <= 1; x++) {
                int3 offset = int3(x, y, z);
                uint3 hash = sdfPcg3d(asuint(cell + offset) ^ uint3(seed, seed ^ 0x9E3779B9u, seed ^ 0x85EBCA77u));
                float3 feature = 0.5 + randomness * (float3(hash >> 16u) * (1.0 / 65536.0) - 0.5);
                float3 delta = f - (float3(offset) + feature);
                float squared = dot(delta, delta);
                if (squared < first) {
                    second = first; secondDelta = firstDelta;
                    first = squared; firstDelta = delta;
                } else if (squared < second) {
                    second = squared; secondDelta = delta;
                }
            }
        }
    }
    first = sqrt(first); second = sqrt(second);
    gradient = first > 1.0e-12 ? firstDelta / first : 0.0;
    if (mode == SDF_CELL_MODE_F1) return first;
    gradient = (second > 1.0e-12 ? secondDelta / second : 0.0) - gradient;
    return second - first;
}
// The gradient twin (KEEP IN SYNC with sdfValueNoise3 and mapGradCore's SDF_OP_NOISE_DISPLACE case): the same eight
// corners plus the analytic partials of the quintic-blended trilinear (du = 30*f^2*(f-1)^2), in NOISE-CELL units —
// the caller scales by the octave's world frequency.
float sdfValueNoise3Grad(float3 q, uint3 seed, out float3 gradient) {
    float3 cellFloor = floor(q);
    int3 cell = int3(cellFloor);
    float3 f = (q - cellFloor);
    float3 u = (((f * f) * f) * ((f * ((f * 6.0) - 15.0)) + 10.0));
    float3 fm = (f - 1.0);
    float3 du = (((30.0 * f) * f) * (fm * fm));
    float c000 = sdfNoiseCorner3(cell, seed);
    float c100 = sdfNoiseCorner3((cell + int3(1, 0, 0)), seed);
    float c010 = sdfNoiseCorner3((cell + int3(0, 1, 0)), seed);
    float c110 = sdfNoiseCorner3((cell + int3(1, 1, 0)), seed);
    float c001 = sdfNoiseCorner3((cell + int3(0, 0, 1)), seed);
    float c101 = sdfNoiseCorner3((cell + int3(1, 0, 1)), seed);
    float c011 = sdfNoiseCorner3((cell + int3(0, 1, 1)), seed);
    float c111 = sdfNoiseCorner3((cell + int3(1, 1, 1)), seed);
    float x00 = lerp(c000, c100, u.x);
    float x10 = lerp(c010, c110, u.x);
    float x01 = lerp(c001, c101, u.x);
    float x11 = lerp(c011, c111, u.x);
    float y0 = lerp(x00, x10, u.y);
    float y1 = lerp(x01, x11, u.y);
    float dvdux = lerp(lerp((c100 - c000), (c110 - c010), u.y), lerp((c101 - c001), (c111 - c011), u.y), u.z);
    float dvduy = lerp((x10 - x00), (x11 - x01), u.z);
    float dvduz = (y1 - y0);
    gradient = (2.0 * (du * float3(dvdux, dvduy, dvduz)));
    return ((lerp(y0, y1, u.z) * 2.0) - 1.0);
}

// === 3D primitives ===================================================================================================
// Exact signed-distance fields unless a comment says otherwise. Derived per-shape constants (a reciprocal, a sqrt, a
// normalization) are HOST-BAKED into the spare Data0/Data1 lanes by SdfProgramBuilder: shapes evaluate millions of
// times per frame while programs build once, and a shared multiply keeps both DXC targets on the identical operation
// (a varying-numerator divide contracted differently is a cross-backend fuzz-signature risk).

#endif
