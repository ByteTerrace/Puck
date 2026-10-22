// The sky's environment map, resource-free: its texels' directions and solid angles, the packing of a texel, the
// spherical-harmonic basis its coefficients project onto, and the bilinear lookup the composite reads the fog's
// in-scattered colour through. The map is SDF_SKY_ENVIRONMENT_SIZE texels square over the octahedral projection with the pole
// at +y (sdfOctEncode over the direction's x, z and y), each texel the sky's gradient at its centre's direction as four
// half floats in two words. The CPU reference is SdfSkyEnvironment (Puck.SignedDistance), whose Size this size matches.
#ifndef SHADE_SDF_SKY_ENVIRONMENT_HLSLI
#define SHADE_SDF_SKY_ENVIRONMENT_HLSLI
#include "../field/sdf-octahedral.hlsli"
#include "../isa/sdf-render.hlsli"
// The map's size, its coefficients per colour channel and the largest finite half float a texel's channel is clamped to
// are SdfSkyEnvironment's (SDF_SKY_ENVIRONMENT_SIZE, SDF_SKY_ENVIRONMENT_COEFFICIENTS, SDF_SKY_ENVIRONMENT_MAX_RADIANCE).

// The unit direction at a texel's centre.
float3 sdfSkyEnvironmentDirection(uint2 texel) {
    float3 n = sdfOctDecode(((((float2(texel) + 0.5) / (float)SDF_SKY_ENVIRONMENT_SIZE) * 2.0) - 1.0));

    return float3(n.x, n.z, n.y);
}
// A texel's solid angle: its area in the projection, (2 / size)^2, times the cube of the L1 norm of its centre's unit
// direction, the projection's density there.
float sdfSkyEnvironmentSolidAngle(float3 direction) {
    float side = (2.0 / (float)SDF_SKY_ENVIRONMENT_SIZE);
    float norm = ((abs(direction.x) + abs(direction.y)) + abs(direction.z));

    return ((side * side) * ((norm * norm) * norm));
}
// The nine real spherical harmonics of bands zero to two at a unit direction, in the world's axes, in SdfSkyEnvironment.Basis
// order.
void sdfSkyEnvironmentBasis(float3 d, out float basis[9]) {
    basis[0] = 0.28209479177387814;
    basis[1] = (0.48860251190291992 * d.y);
    basis[2] = (0.48860251190291992 * d.z);
    basis[3] = (0.48860251190291992 * d.x);
    basis[4] = ((1.0925484305920792 * d.x) * d.y);
    basis[5] = ((1.0925484305920792 * d.y) * d.z);
    basis[6] = (0.31539156525252005 * (((3.0 * d.z) * d.z) - 1.0));
    basis[7] = ((1.0925484305920792 * d.x) * d.z);
    basis[8] = (0.54627421529603959 * ((d.x * d.x) - (d.y * d.y)));
}
// A texel's two words: the colour's channels clamped to [0, the largest half float] as half floats, and a zero.
uint2 sdfSkyEnvironmentPack(float3 color) {
    float3 clamped = clamp(color, 0.0, SDF_SKY_ENVIRONMENT_MAX_RADIANCE);

    return uint2((f32tof16(clamped.r) | (f32tof16(clamped.g) << 16u)), f32tof16(clamped.b));
}
// A texel's colour from its two words.
float3 sdfSkyEnvironmentUnpack(uint2 words) {
    return float3(f16tof32(words.x & 0xFFFFu), f16tof32(words.x >> 16u), f16tof32(words.y & 0xFFFFu));
}
// The texel a filter tap at most one texel past the map reads: inside, itself; past an edge, the texel the octahedral fold
// puts there, mirrored along that edge, and past a corner the opposite corner (SdfOctahedral.BorderSource).
uint sdfSkyEnvironmentTap(int2 tap) {
    int last = (SDF_SKY_ENVIRONMENT_SIZE - 1);
    bool mirrorX = ((tap.x < 0) || (tap.x > last));
    bool mirrorY = ((tap.y < 0) || (tap.y > last));
    int2 texel = tap;

    if (mirrorY) {
        texel = int2((last - clamp(tap.x, 0, last)), ((tap.y < 0) ? 0 : last));
    } else if (mirrorX) {
        texel = int2(((tap.x < 0) ? 0 : last), (last - clamp(tap.y, 0, last)));
    }
    if (mirrorX && mirrorY) {
        texel = int2(((tap.x < 0) ? last : 0), ((tap.y < 0) ? last : 0));
    }

    return ((uint)((texel.y * SDF_SKY_ENVIRONMENT_SIZE) + texel.x));
}
// The four taps and their bilinear weights about a direction's point, each tap a texel index.
void sdfSkyEnvironmentTaps(float3 direction, out uint taps[4], out float weights[4]) {
    float2 position = ((((sdfOctEncode(float3(direction.x, direction.z, direction.y)) * 0.5) + 0.5) * (float)SDF_SKY_ENVIRONMENT_SIZE) - 0.5);
    int2 origin = int2(floor(position));
    float2 fraction = (position - float2(origin));

    [unroll] for (uint i = 0u; i < 4u; i++) {
        int2 corner = int2((int)(i & 1u), (int)(i >> 1u));

        taps[i] = sdfSkyEnvironmentTap((origin + corner));
        weights[i] = (lerp((1.0 - fraction.x), fraction.x, (float)corner.x) * lerp((1.0 - fraction.y), fraction.y, (float)corner.y));
    }
}
#endif
