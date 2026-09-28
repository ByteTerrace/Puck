// The environment's typed reads: its lights, the sky's parameters, the curvature gains and the softboxes, each decoded
// from the frame's environment rows.
#ifndef FRAME_SDF_LIGHTS_HLSLI
#define FRAME_SDF_LIGHTS_HLSLI
#include "sdf-environment.hlsli"

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
uint worldLightCount() { return min((uint)max(worldEnvRow(SDF_ENV_CONTROL_ROW).x + 0.5, 0.0), SDF_ENV_MAX_LIGHTS); }
int worldShadowLightIndex() { return (int)round(worldEnvRow(SDF_ENV_CONTROL_ROW).y); }
bool worldSkyEnabled() { return (worldEnvRow(SDF_ENV_CONTROL_ROW).z > 0.5); }
float worldSkyFogDensity() { return worldEnvRow(SDF_ENV_CONTROL_ROW).w; }
SdfEnvLight worldLight(uint index) {
    uint row = (SDF_ENV_LIGHTS_ROW + (index * SDF_ENV_ROWS_PER_LIGHT));
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
float worldCurvatureCavity() { return worldEnvRow(SDF_ENV_CURVATURE_ROW).x; }
float worldCurvatureRim() { return worldEnvRow(SDF_ENV_CURVATURE_ROW).y; }
float worldCurvatureInk() { return worldEnvRow(SDF_ENV_CURVATURE_ROW).z; }
float worldCurvatureInkLow() { return worldEnvRow(SDF_ENV_CURVATURE_ROW).w; }
float3 worldCurvatureInkColor() { return worldEnvRow((SDF_ENV_CURVATURE_ROW + 1u)).rgb; }
float worldCurvatureInkHigh() { return worldEnvRow((SDF_ENV_CURVATURE_ROW + 1u)).w; }
uint worldSkyStopCount() { return min((uint)max(worldEnvRow(SDF_ENV_SKY_CONTROL_ROW).x + 0.5, 0.0), SDF_ENV_MAX_SKY_STOPS); }
float4 worldSkyStop(uint index) { return worldEnvRow(SDF_ENV_SKY_STOPS_ROW + index); } // rgb colour, w elevation
int worldSkySunDiscLightIndex() { return (int)round(worldEnvRow(SDF_ENV_SKY_CONTROL_ROW).y); }
float worldSkySunDiscExponent() { return worldEnvRow(SDF_ENV_SKY_CONTROL_ROW).z; }
float worldSkySunDiscIntensity() { return worldEnvRow(SDF_ENV_SKY_CONTROL_ROW).w; }
float worldSkyStarDensity() { return worldEnvRow(SDF_ENV_STARS_ROW).x; }
float worldSkyStarBrightness() { return worldEnvRow(SDF_ENV_STARS_ROW).y; }
uint worldSkyStarSeed() { return (uint)(worldEnvRow(SDF_ENV_STARS_ROW).z + 0.5); }
float worldSkyStarTwinkleShare() { return worldEnvRow(SDF_ENV_TWINKLE_ROW).x; }
float worldSkyStarTwinkleDepth() { return worldEnvRow(SDF_ENV_TWINKLE_ROW).y; }
float worldSkyStarTwinklePhase() { return worldEnvRow(SDF_ENV_TWINKLE_ROW).z; }
float3 worldSkyCloudColor() { return worldEnvRow(SDF_ENV_CLOUDS_ROW).rgb; }
float worldSkyCloudCoverage() { return worldEnvRow(SDF_ENV_CLOUDS_ROW).w; }
float worldSkyCloudSoftness() { return worldEnvRow((SDF_ENV_CLOUDS_ROW + 1u)).x; }
float worldSkyCloudScale() { return worldEnvRow((SDF_ENV_CLOUDS_ROW + 1u)).y; }
uint worldSkyCloudSeed() { return (uint)(worldEnvRow((SDF_ENV_CLOUDS_ROW + 1u)).z + 0.5); }
float2 worldSkyCloudOffset() { return worldEnvRow((SDF_ENV_CLOUDS_ROW + 2u)).xy; }
float2 worldSkyCloudShearOffset() { return worldEnvRow((SDF_ENV_CLOUDS_ROW + 2u)).zw; }
float worldSkyCloudSpinAngle() { return worldEnvRow((SDF_ENV_CLOUDS_ROW + 3u)).x; }
float worldSkyCloudCurl() { return worldEnvRow((SDF_ENV_CLOUDS_ROW + 3u)).y; }
// render.environment's studio softboxes (the reflection shade/sdf-lighting.hlsli samples).
struct SdfEnvSoftbox {
    float3 direction; // unit, surface -> the softbox (host-normalized on upload)
    float weight;
    float3 color;
    float2 size;       // angular half-extent proxy (width, height), world-authored radians-scale units
    float blur;
};
uint worldEnvironmentSoftboxCount() { return min((uint)max(worldEnvRow(SDF_ENV_SOFTBOX_CONTROL_ROW).x + 0.5, 0.0), SDF_ENV_MAX_SOFTBOXES); }
SdfEnvSoftbox worldEnvironmentSoftbox(uint index) {
    uint row = (SDF_ENV_SOFTBOXES_ROW + (index * SDF_ENV_ROWS_PER_SOFTBOX));
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

#endif
