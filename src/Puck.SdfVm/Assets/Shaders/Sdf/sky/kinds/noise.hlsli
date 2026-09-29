// A periodic 3D field sampled directly by direction, without a projected seam.
#ifndef SKY_KIND_NOISE_HLSLI
#define SKY_KIND_NOISE_HLSLI
#include "../../field/sdf-hash.hlsli"
#include "../../field/sdf-noise.hlsli"

float4 sdfSkyNoise(float3 localDirection, SdfSkyNoiseData parameters, uint quality, inout uint hashes) {
    if (parameters.Intensity <= 0.0) return 0.0;
    uint octaves = quality == 0u ? min(parameters.Octaves, 3u) : parameters.Octaves;
    float value = sdfPeriodicFbm3(localDirection * parameters.InverseScale + parameters.Offset, parameters.Seed, octaves, hashes);
    float shaped = saturate(value * parameters.Contrast + parameters.Bias);
    return float4(lerp(parameters.ColorLow, parameters.ColorHigh, shaped) * parameters.Intensity, 1.0);
}
#endif
