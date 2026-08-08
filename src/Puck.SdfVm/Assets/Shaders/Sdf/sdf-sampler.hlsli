#ifndef SDF_SAMPLER_HLSLI
#define SDF_SAMPLER_HLSLI
// The stratified sampler the world's area-light shadow estimator draws from: a two-dimensional digital net over
// GF(2), digitally shifted per lattice site, read back through a host-baked polar table.
//
// FILE INVARIANT — no `sqrt`, `rsqrt`, `normalize`, `sin`, `cos`, `pow`, or `exp` may appear anywhere in this file.
// Vulkan allows 3 ULP on GLSL.std.450 Sqrt and 2.5 ULP on FDiv; neither is correctly rounded, so any of them here
// would make the sampled DIRECTION a per-driver quantity rather than a shipped constant. Everything below is either
// integer (exact by definition) or a `precise` combination of table words the host rounded exactly once
// (SdfShadowSamplerTables / Puck.Maths.SphericalCapSampleTable). That is the entire reason the table exists.
//
// These are mirrors of the C# routines, not re-derivations. The `digital-net` stage that proved the C# side left the
// build on 2026-08-02, so neither side is gated now.
// KEEP IN SYNC with Puck.Maths.UnitriangularBitMix, Puck.Maths.NestedDyadicPermutation, and
// Puck.Maths.DigitalNetSampler.

// UnitriangularBitMix's multipliers and its two shift-exclusive-or constants. Each `x ^= x >> k` is a unit-diagonal
// matrix over GF(2) and each odd multiply is a bijection on Z/2^32, so the composition is invertible as a theorem
// rather than as a measured property of a tuned hash.
#define SDF_MIX_FIRST_MULTIPLIER  0x7FEB352Du
#define SDF_MIX_SECOND_MULTIPLIER 0x846CA68Bu
// DigitalNetSampler's fixed separator: the second coordinate's digital shift is a mix of the key against this rather
// than the key itself, so the two coordinates never ride correlated shift vectors.
#define SDF_SCRAMBLE_SEPARATION   0x9E3779B9u
// DigitalNetSampler's per-stream stride, applied before the key mix so independent consumers of one lattice site
// never collide.
#define SDF_STREAM_STRIDE         0x85EBCA6Bu
// NestedDyadicPermutation's three shifts. The third is above sixteen, so that step is its own inverse.
#define SDF_PERMUTE_FIRST_SHIFT   13
#define SDF_PERMUTE_SECOND_SHIFT  7
#define SDF_PERMUTE_THIRD_SHIFT   17

uint sdfUnitriangularMix(uint value) {
    value ^= (value >> 16u);
    value *= SDF_MIX_FIRST_MULTIPLIER;
    value ^= (value >> 15u);
    value *= SDF_MIX_SECOND_MULTIPLIER;
    value ^= (value >> 16u);

    return value;
}

// The index shuffle. NOT a plain mix: a general mixing bijection scatters a consumer's first 2^m draws across the
// whole index space and the resulting point set is not a net at all (the digital-net gate rejects exactly that). This
// permutation is conjugated by bit reversal, so every step is LOWER-unitriangular in the reversed word and the map
// carries an ALIGNED DYADIC BLOCK onto an aligned dyadic block. Every such block of a (0,2)-sequence is itself a
// (0,m,2)-net, which is what makes the shuffle safe. `reversebits` is one instruction on every supported target.
uint sdfShuffleIndex(uint index, uint seed) {
    uint value = reversebits(index);

    value += seed;
    value ^= (value << SDF_PERMUTE_FIRST_SHIFT);
    value *= SDF_MIX_FIRST_MULTIPLIER;
    value ^= (value << SDF_PERMUTE_SECOND_SHIFT);
    value *= SDF_MIX_SECOND_MULTIPLIER;
    value ^= (value << SDF_PERMUTE_THIRD_SHIFT);

    return reversebits(value);
}

// The per-lattice-site key. Packing is injective for sites within sixteen bits and the mix is a bijection, so two
// distinct sites in one stream can never share a key — and therefore never share a scramble.
uint sdfSamplerKey(uint2 site, uint stream) {
    return sdfUnitriangularMix((site.x | (site.y << 16u)) ^ (stream * SDF_STREAM_STRIDE));
}

// The two coordinates' digital shifts. XORing a digital net by a fixed vector is a digital shift, which preserves the
// (0,m,2)-net property exactly — the shift is a bijection on each dyadic column, so every elementary interval still
// receives exactly one point.
uint2 sdfSamplerScramble(uint key) {
    return uint2(key, sdfUnitriangularMix(key ^ SDF_SCRAMBLE_SEPARATION));
}

// One coordinate of the digitally shifted net point: the exclusive-or of the direction vectors the set bits of
// `index` select. Stateless and seekable — sample N is a pure function of N, so there is no sampler state to carry,
// to snapshot, or to reconcile with the no-random-state-in-simulation doctrine.
uint sdfDigitalNetCoordinate(uint index, uint directionBase, uint scramble) {
    uint result = scramble;
    uint remaining = index;

    [loop]
    while (0u != remaining) {
        result ^= sdfSamplerTable[directionBase + firstbitlow(remaining)];
        remaining &= (remaining - 1u);
    }

    return result;
}

// Both coordinates at once. Dimension 0's direction numbers occupy words [0, 32), dimension 1's words [32, 64) —
// SphericalCapSampleTable.DirectionNumberOffset / DigitalNetSampler.DirectionNumberCount.
uint2 sdfDigitalNetSample2D(uint index, uint2 scramble) {
    return uint2(
        sdfDigitalNetCoordinate(index, 0u, scramble.x),
        sdfDigitalNetCoordinate(index, 32u, scramble.y)
    );
}

// The net point mapped onto the sun disc, entirely by table lookup. The first coordinate picks an azimuth
// (cos, sin) pair; the second picks an (axial, radial) pair that already carries the area-preserving radius map, the
// cap slope, and the normalization denominator baked together — so the returned direction is UNIT LENGTH BY TABLE
// CONSTRUCTION and needs no normalize. Twelve bits per coordinate (SphericalCapSampleTable.TableIndexBitCount): the
// digital-net gate proves the net property survives that quantization, which is the property the estimator relies on.
// Every arithmetic combination is `precise` so neither DXC target may contract or reassociate it.
float3 sdfSunDiscDirection(uint2 netPoint) {
    uint azimuthIndex = (netPoint.x >> 20u);
    uint radiusIndex = (netPoint.y >> 20u);
    // AzimuthOffset = 64, RadiusOffset = 64 + 2 * 4096 = 8256; two words per entry.
    precise float cosine = asfloat(sdfSamplerTable[64u + (azimuthIndex * 2u)]);
    precise float sine = asfloat(sdfSamplerTable[64u + (azimuthIndex * 2u) + 1u]);
    precise float axial = asfloat(sdfSamplerTable[8256u + (radiusIndex * 2u)]);
    precise float radial = asfloat(sdfSamplerTable[8256u + (radiusIndex * 2u) + 1u]);
    precise float3 offset = ((cosine * SdfSunTangent) + (sine * SdfSunBitangent));
    precise float3 direction = ((axial * SdfSunDirection) + (radial * offset));

    return direction;
}
#endif
