#ifndef SDF_INDIRECT_LIGHT_HLSLI
#define SDF_INDIRECT_LIGHT_HLSLI
#include "sdf-indirect-march.hlsli"
#include "sdf-indirect-light-projection.hlsli"

// A map is usable only for its current pinned snapshot and receiver box. Fine regions precede coarse regions;
// unresolved texels try the next region before requesting a bounded full-field ray.
bool sdfIndirectLightLookup(uint light, float3 surfacePoint, float3 normal, out float visibility, out uint region) {
    visibility = 0.0;
    region = 0u;
    uint depthCount, depthStride;
    indirectLightDepth.GetDimensions(depthCount, depthStride);
    uint mapCount = min(min(passGroup.lightMapCount, SdfIndirectLightMaxMaps),
        depthCount / (SdfIndirectLightResolution * SdfIndirectLightResolution));
    [loop]
    for (uint index = 0u; index < mapCount; index++) {
        uint row = index * SdfIndirectLightMetadataRows;
        float4 origin = passGroup.lightMaps[row];
        float4 right = passGroup.lightMaps[row + 1u];
        float4 up = passGroup.lightMaps[row + 2u];
        float4 toward = passGroup.lightMaps[row + 3u];
        float4 low = passGroup.lightMaps[row + 4u];
        float4 high = passGroup.lightMaps[row + 5u];
        if (origin.w == 0.0 || high.w != (float)(light + 1u) || any(surfacePoint < low.xyz) || any(surfacePoint > high.xyz)) { continue; }
        uint2 pixel;
        float travel;
        float3 ray;
        if (!sdfIndirectLightProject(surfacePoint, origin.xyz, right, up, toward, SdfIndirectLightResolution, pixel, travel, ray)) { continue; }
        float depth = indirectLightDepth[index * SdfIndirectLightResolution * SdfIndirectLightResolution + pixel.y * SdfIndirectLightResolution + pixel.x];
        if (isnan(depth)) { continue; }
        region = index;
        visibility = sdfIndirectLightCompare(travel, depth, low.w, normal, ray);
        return true;
    }
    return false;
}

// Point and spot lights retain the direct path's unshadowed policy. Directional misses and invalid maps use one
// bounded ray, which the receiving procedure marches (sdfIndirectVisibilitiesStep); its samples and normal witnesses
// count at the existing full-field evaluation sites. False when the light needs that ray: no facing decision and no
// usable map texel.
bool sdfIndirectLightDecided(uint light, float3 surfacePoint, float3 normal, float3 toward, out float visibility, out uint region) {
    visibility = 0.0;
    if (dot(normal, toward) <= 0.0) { region = 0u; return true; }
    return sdfIndirectLightLookup(light, surfacePoint, normal, visibility, region);
}
#endif
