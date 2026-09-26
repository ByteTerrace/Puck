// The environment's lights, softboxes, horizon and studio reflection, the material layers and the tonemap.
#ifndef SHADE_SDF_LIGHTING_HLSLI
#define SHADE_SDF_LIGHTING_HLSLI
// Generic surface coverage, available wherever materials shade.
#include "shade-weathering.hlsli"

// The environment's typed reads. worldSunDirection/worldSunColor name the SHADOW light (the one directional whose
// Lambert term is soft-shadowed); with no shadow light they read the pinned sun so the sky disc, the clouds' lighting
// and the material specular still have a key.
struct SdfEnvLight {
    float3 direction; // unit, surface -> light (directional); a world-space POSITION (point)
    float weight;
    float3 color;
    uint kind;        // SdfEnvLight{Directional,Hemisphere,Rim,Point}
    float param;      // penumbra half-slope / hemisphere gradient / rim exponent / point falloff radius
    bool shadows;
    int dynamicSlot;  // point only: the dynamic-transform slot its position rides, or -1 for the static position
};
uint worldLightCount() { return min((uint)max(worldEnvRow(SdfEnvControl).x + 0.5, 0.0), SdfEnvMaxLights); }
int worldShadowLightIndex() { return (int)round(worldEnvRow(SdfEnvControl).y); }
bool worldSkyEnabled() { return (worldEnvRow(SdfEnvControl).z > 0.5); }
float worldSkyFogDensity() { return worldEnvRow(SdfEnvControl).w; }
SdfEnvLight worldLight(uint index) {
    uint row = (SdfEnvLights + (index * SdfEnvRowsPerLight));
    float4 a = worldEnvRow(row);
    float4 b = worldEnvRow(row + 1u);
    float4 c = worldEnvRow(row + 2u);
    SdfEnvLight light;

    light.direction = a.xyz;
    light.weight = a.w;
    light.color = b.rgb;
    light.kind = (uint)(b.w + 0.5);
    light.param = c.x;
    light.shadows = (c.y > 0.5);
    light.dynamicSlot = (int)round(c.z);

    return light;
}
// A point light's current world-space position: the live dynamic transform its slot names (an anchored light
// tracking a moving shape), or the authored static position when unanchored. Falls back to the static position on
// a kernel that binds no per-frame dynamic-transform table.
float3 worldPointLightPosition(SdfEnvLight light) {
#ifdef SDF_DYNAMIC_TRANSFORMS
    if (light.dynamicSlot < 0) return light.direction;
    uint slot = (uint)light.dynamicSlot;
    return sdfDynamicTransforms[3u * slot].xyz + rotatePointByQuaternion(light.direction, sdfDynamicTransforms[3u * slot + 1u]);
#else
    return light.direction;
#endif
}
float3 worldSunDirection() {
    int index = worldShadowLightIndex();

    return ((index >= 0) ? worldLight((uint)index).direction : SdfSunDirection);
}
float3 worldSunColor() {
    int index = worldShadowLightIndex();

    return ((index >= 0) ? worldLight((uint)index).color : float3(1.0, 1.0, 1.0));
}
float worldShadowPenumbraSlope() {
    int index = worldShadowLightIndex();

    return ((index >= 0) ? max(worldLight((uint)index).param, 1.0e-3) : (1.0 / 9.0));
}
float worldCurvatureCavity() { return worldEnvRow(SdfEnvCurvatureA).x; }
float worldCurvatureRim() { return worldEnvRow(SdfEnvCurvatureA).y; }
float worldCurvatureInk() { return worldEnvRow(SdfEnvCurvatureA).z; }
float worldCurvatureInkLow() { return worldEnvRow(SdfEnvCurvatureA).w; }
float3 worldCurvatureInkColor() { return worldEnvRow(SdfEnvCurvatureB).rgb; }
float worldCurvatureInkHigh() { return worldEnvRow(SdfEnvCurvatureB).w; }
uint worldSkyStopCount() { return min((uint)max(worldEnvRow(SdfEnvSkyControl).x + 0.5, 0.0), 4u); }
float4 worldSkyStop(uint index) { return worldEnvRow(SdfEnvSkyStops + index); } // rgb colour, w elevation
int worldSkySunDiscLightIndex() { return (int)round(worldEnvRow(SdfEnvSkyControl).y); }
float worldSkySunDiscExponent() { return worldEnvRow(SdfEnvSkyControl).z; }
float worldSkySunDiscIntensity() { return worldEnvRow(SdfEnvSkyControl).w; }
float worldSkyStarDensity() { return worldEnvRow(SdfEnvStars).x; }
float worldSkyStarBrightness() { return worldEnvRow(SdfEnvStars).y; }
uint worldSkyStarSeed() { return (uint)(worldEnvRow(SdfEnvStars).z + 0.5); }
float worldSkyStarTwinkleShare() { return worldEnvRow(SdfEnvTwinkle).x; }
float worldSkyStarTwinkleDepth() { return worldEnvRow(SdfEnvTwinkle).y; }
uint worldSkyStarTwinklePeriodTicks() { return max((uint)(worldEnvRow(SdfEnvTwinkle).z + 0.5), 1u); }
float3 worldSkyCloudColor() { return worldEnvRow(SdfEnvCloudsA).rgb; }
float worldSkyCloudCoverage() { return worldEnvRow(SdfEnvCloudsA).w; }
float worldSkyCloudSoftness() { return worldEnvRow(SdfEnvCloudsB).x; }
float worldSkyCloudScale() { return worldEnvRow(SdfEnvCloudsB).y; }
uint worldSkyCloudSeed() { return (uint)(worldEnvRow(SdfEnvCloudsB).z + 0.5); }
float2 worldSkyCloudOffset() { return worldEnvRow(SdfEnvCloudsC).xy; }
float2 worldSkyCloudShearOffset() { return worldEnvRow(SdfEnvCloudsC).zw; }
float worldSkyCloudSpinAngle() { return worldEnvRow(SdfEnvCloudsD).x; }
float worldSkyCloudCurl() { return worldEnvRow(SdfEnvCloudsD).y; }

// render.environment: analytic studio-softbox reflections plus a two-color reflection horizon, sampled at the
// shading site as studioReflection(reflect(rayDirection, normal), roughness) — an absent section (zero softboxes,
// zero-black horizon) contributes exactly 0, so the reflection term is a no-op addition then.
struct SdfEnvSoftbox {
    float3 direction; // unit, surface -> the softbox (host-normalized on upload)
    float weight;
    float3 color;
    float2 size;       // angular half-extent proxy (width, height), world-authored radians-scale units
    float blur;
};
uint worldEnvironmentSoftboxCount() { return min((uint)max(worldEnvRow(SdfEnvSoftboxControl).x + 0.5, 0.0), SdfEnvMaxSoftboxes); }
uint worldTonemapMode() { return (uint)max(worldEnvRow(SdfEnvSoftboxControl).y + 0.5, 0.0); }
SdfEnvSoftbox worldEnvironmentSoftbox(uint index) {
    uint row = (SdfEnvSoftboxes + (index * SdfEnvRowsPerSoftbox));
    float4 a = worldEnvRow(row);
    float4 b = worldEnvRow(row + 1u);
    float4 c = worldEnvRow(row + 2u);
    SdfEnvSoftbox box;

    box.direction = a.xyz;
    box.weight = a.w;
    box.color = b.rgb;
    box.size = float2(b.w, c.x);
    box.blur = c.y;

    return box;
}
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
