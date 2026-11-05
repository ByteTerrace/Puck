// Physical lighting from the residency's completed environment, before the artistic ambient and reflection gains.
// The includer binds sdfSkyEnvironment and sdfSkyCoefficients from the same publication. Diffuse transport divides
// irradiance by pi; directional transport reads the full map's first plane. Each lookup counts its own work once.
#ifndef SHADE_SDF_SKY_LIGHTING_HLSLI
#define SHADE_SDF_SKY_LIGHTING_HLSLI
#include "sdf-sky-environment.hlsli"

float3 sdfSkyPhysicalIrradiance(float3 normal) {
    puckAddWork(passGroup.workCounterRow * PuckWorkRowWords + PuckWorkSkyWord, 1u);
    float3 coefficients[9];
    [unroll] for (uint k = 0u; k < 9u; k++) { coefficients[k] = sdfSkyCoefficients[k].rgb; }
    return sdfSkyEnvironmentIrradiance(normal, coefficients);
}
// Plane zero holds every lighting-visible layer; plane one omits panels for the artistic rough-reflection path.
float3 sdfSkyMapRadiance(float3 direction, uint plane) {
    puckAddWork(passGroup.workCounterRow * PuckWorkRowWords + PuckWorkSkyTextureLoadsWord, 4u);
    uint taps[4];
    float weights[4];
    sdfSkyEnvironmentTaps(direction, taps, weights);
    float3 result = 0.0.xxx;
    [unroll] for (uint i = 0u; i < 4u; i++) {
        result += weights[i] * sdfSkyEnvironmentUnpack(sdfSkyEnvironment[taps[i] + (plane * (uint)(SDF_SKY_ENVIRONMENT_SIZE * SDF_SKY_ENVIRONMENT_SIZE))]);
    }
    return result;
}
float3 sdfSkyPhysicalRadiance(float3 direction) {
    return sdfSkyMapRadiance(direction, 0u);
}
#endif
