// The deterministic hashes' noise and dither: the R2 dither, value, lattice and cellular noise.
#ifndef FIELD_SDF_NOISE_HLSLI
#define FIELD_SDF_NOISE_HLSLI
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
float sdfNoiseCorner3(int3 cell, uint3 seed) {
    return ((float)sdfPcg3d(asuint(cell) ^ seed).x * SDF_INV_2POW32);
}
float sdfValueNoise3(float3 q, uint3 seed) {
    float3 cellFloor = floor(q);
    int3 cell = int3(cellFloor);
    float3 f = (q - cellFloor);
    float3 u = (((f * f) * f) * ((f * ((f * 6.0) - 15.0)) + 10.0));
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
    return ((lerp(y0, y1, u.z) * 2.0) - 1.0);
}
// A seeded 3D value-noise sample over the same deterministic lattice (sdfValueNoise3), folding one uint seed into
// its three hash streams so two consumers sharing a lattice (a material's wear, a volume's advection) read different
// noise fields. The one definition: shade-weathering.hlsli and shade-volumes.hlsli both read it from here.
float sdfLatticeNoise3(float3 q, uint seed) {
    return sdfValueNoise3(q, uint3(seed, (seed ^ 0x9E3779B9u), (seed ^ 0x85EBCA77u)));
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
    if (mode == 0u) return first;
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
