// The environment's horizon and studio reflection, the material layers and the tonemap.
#ifndef SHADE_SDF_LIGHTING_HLSLI
#define SHADE_SDF_LIGHTING_HLSLI
// Generic surface coverage, available wherever materials shade.
#include "shade-weathering.hlsli"

#include "../frame/sdf-lights.hlsli"

// render.environment: analytic studio-softbox reflections plus a two-color reflection horizon, sampled at the
// shading site as studioReflection(reflect(rayDirection, normal), roughness) — an absent section (zero softboxes,
// zero-black horizon) contributes exactly 0, so the reflection term is a no-op addition then.
float3 worldEnvironmentHorizon(float3 direction) {
    float3 low = worldEnvRow(SdfEnvHorizonLow).rgb;
    float3 high = worldEnvRow(SdfEnvHorizonHigh).rgb;

    return lerp(low, high, saturate((direction.y * 0.5) + 0.5));
}
// The analytic studio reflection: the horizon gradient plus each authored softbox's angular falloff from `direction`,
// widened by the surface roughness (a rougher surface blurs the softbox into a broader, dimmer catch). Every softbox
// falloff is a smooth (never hard-edged) disc, so the sum stays finite and free of the reflect() singularity a mirror
// direction could otherwise expose.
float3 worldStudioReflection(float3 direction, float roughness) {
    float3 result = worldEnvironmentHorizon(direction);
    uint count = worldEnvironmentSoftboxCount();

    [loop]
    for (uint index = 0u; (index < count); index++) {
        SdfEnvSoftbox box = worldEnvironmentSoftbox(index);
        float cosAngle = saturate(dot(direction, box.direction));
        float angle = acos(cosAngle);
        float radius = max((length(box.size) + max(box.blur, roughness)), 1.0e-3);
        float falloff = saturate(1.0 - (angle / radius));

        falloff = ((falloff * falloff) * (3.0 - (2.0 * falloff))); // smoothstep shaping
        result += (box.color * (box.weight * falloff));
    }

    return result;
}
#include "shade-layers.hlsli"
// render.tonemap: the Narkowicz ACES-fit filmic curve, and ONLY the curve. The study follows it with a gamma-2.2
// encode because its shading is linear light; this pipeline's stylized shading is already display-referred (no sRGB
// encode exists anywhere between the shade and the rgba8 store), so a second encode here washes the whole frame out.
// None (the default) is a no-op — the pipeline stores its stylized color directly, as it always has.
float3 sdfFilmicTonemap(float3 color) {
    return saturate((color * ((2.51 * color) + 0.03)) / (((color * ((2.43 * color) + 0.59)) + 0.14)));
}

#endif
