// A periodic signed-noise ridge in the admitted, transformed sky frame.
#ifndef SKY_KIND_AURORA_HLSLI
#define SKY_KIND_AURORA_HLSLI
#include "../../field/sdf-hash.hlsli"
#include "../../field/sdf-noise.hlsli"

float4 sdfSkyAurora(float3 localDirection, SdfSkyAuroraData parameters, uint quality, inout uint hashes) {
    if (parameters.Intensity <= 0.0) return 0.0;
    float3 coordinate = float3(localDirection.x, localDirection.y * parameters.HeightScale, localDirection.z)
        * parameters.InverseScale + parameters.Offset;
    uint octaves = quality == 0u ? 1u : parameters.Octaves;
    float value = sdfPeriodicFbm3(coordinate, parameters.Seed, octaves, hashes);
    float ridge = saturate(1.0 - abs(value - parameters.Bias) * parameters.InverseWidth);
    return float4(parameters.Color * parameters.Intensity, (ridge > 0.0 ? pow(ridge, parameters.Sharpness) : 0.0));
}
#endif
