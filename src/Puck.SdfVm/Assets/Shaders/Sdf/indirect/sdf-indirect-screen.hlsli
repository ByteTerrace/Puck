// Physical screen-face emission, separate from the analytic room glow used only at the view's own surface.
// The buffer and mappings belong to the same pinned finite source. CPU host colors never enter this sample.
#ifndef SDF_INDIRECT_SCREEN_HLSLI
#define SDF_INDIRECT_SCREEN_HLSLI
#include "../frame/sdf-environment.hlsli"

bool sdfIndirectScreenEmission(int material, float3 surfacePoint, float3 direction, out float3 radiance) {
    radiance = 0.0;
    if (material <= SDF_SCREEN_MATERIAL) { return false; }
#ifdef SDF_SCREEN_SOURCES
    uint screen = (uint)(material - SDF_SCREEN_MATERIAL - 1);
    if (screen >= SDF_MAX_SCREEN_SURFACES || (passGroup.indirectSources & SdfIndirectSourcesScreens) == 0u ||
        worldScreenLightsDisabled()) { return true; }
    ScreenMappingData mapping = worldScreenMapping(screen);
    if (mapping.state.x == 0.0 || mapping.state.z == 0.0 || mapping.imageU.w == 0.0) { return true; }
    ScreenSurfaceData surface = worldScreenSurface(screen);
    if (dot(cross(surface.right.xyz, surface.up.xyz), -direction) <= 0.0 ||
        surface.right.w <= 0.0 || surface.up.w <= 0.0) { return true; }
    float3 local = surfacePoint - surface.origin.xyz;
    float2 uv = float2(0.5 + 0.5 * dot(local, surface.right.xyz) / surface.right.w,
        0.5 - 0.5 * dot(local, surface.up.xyz) / surface.up.w);
    float3 face = float3(uv, 1.0);
    float2 warped = float2(dot(mapping.warpU.xyz, face), dot(mapping.warpV.xyz, face));
    if (any(warped < 0.0) || any(warped > 1.0)) { return true; }
    float2 source = float2(dot(mapping.imageU.xyz, float3(warped, 1.0)), dot(mapping.imageV.xyz, float3(warped, 1.0)));
    if (mapping.imageV.w != 0.0 && (any(source < mapping.crop.xy) || any(source >= mapping.crop.zw))) { return true; }
    float2 at = clamp(source, mapping.sampleClamp.xy, mapping.sampleClamp.zw) * (float)SDF_SCREEN_EMISSION_EDGE - 0.5;
    int2 first = int2(floor(at));
    float2 f = frac(at);
    uint2 a = (uint2)clamp(first, 0, (int)SDF_SCREEN_EMISSION_EDGE - 1);
    uint2 b = (uint2)clamp(first + 1, 0, (int)SDF_SCREEN_EMISSION_EDGE - 1);
    uint row = screen * SDF_SCREEN_EMISSION_RECORDS;
    float3 top = lerp(sdfScreenLights[row + a.y * SDF_SCREEN_EMISSION_EDGE + a.x].rgb,
        sdfScreenLights[row + a.y * SDF_SCREEN_EMISSION_EDGE + b.x].rgb, f.x);
    float3 bottom = lerp(sdfScreenLights[row + b.y * SDF_SCREEN_EMISSION_EDGE + a.x].rgb,
        sdfScreenLights[row + b.y * SDF_SCREEN_EMISSION_EDGE + b.x].rgb, f.x);
    radiance = lerp(top, bottom, f.y) * passGroup.indirectSourceGains.z;
    sdfIndirectLoads += 4u;
#endif
    return true;
}
#endif
