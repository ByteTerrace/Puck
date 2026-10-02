// The environment's horizon and studio reflection, and the material layers.
#ifndef SHADE_SDF_LIGHTING_HLSLI
#define SHADE_SDF_LIGHTING_HLSLI
// Generic surface coverage, available wherever materials shade.
#include "shade-weathering.hlsli"

#include "../frame/sdf-lights.hlsli"

// render.environment: analytic studio-softbox reflections plus a two-color reflection horizon, sampled at the
// shading site as studioReflection(reflect(rayDirection, normal), roughness) — an absent section (zero softboxes,
// zero-black horizon) contributes exactly 0, so the reflection term is a no-op addition then.
float3 worldEnvironmentHorizon(float3 direction) {
    float3 low = sdfSky[0].HorizonLow;
    float3 high = sdfSky[0].HorizonHigh;

    return lerp(low, high, saturate((direction.y * 0.5) + 0.5));
}
// The analytic studio reflection: the horizon gradient plus each authored softbox's angular falloff from `direction`,
// widened by the surface roughness (a rougher surface blurs the softbox into a broader, dimmer catch). Every softbox
// falloff is a smooth (never hard-edged) disc, so the sum stays finite and free of the reflect() singularity a mirror
// direction could otherwise expose.
float3 worldStudioReflection(float3 direction, float roughness) {
    float3 result = worldEnvironmentHorizon(direction);
    uint count = sdfSky[0].SoftboxCount;

    [loop]
    for (uint index = 0u; (index < count); index++) {
        SdfSoftbox box = sdfSoftboxes[index];
        float cosAngle = saturate(dot(direction, box.Direction));
        float angle = acos(cosAngle);
        float radius = max((length(box.Size) + max(box.Blur, roughness)), 1.0e-3);
        float falloff = saturate(1.0 - (angle / radius));

        falloff = ((falloff * falloff) * (3.0 - (2.0 * falloff))); // smoothstep shaping
        result += (box.Color * (box.Weight * falloff));
    }

    return result;
}
#include "shade-layers.hlsli"

#endif
