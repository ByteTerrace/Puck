// Sky irradiance, environment reflections and the material layers.
#ifndef SHADE_SDF_LIGHTING_HLSLI
#define SHADE_SDF_LIGHTING_HLSLI
#include "shade-weathering.hlsli"
#include "../frame/sdf-lights.hlsli"
#ifdef SDF_VIEWS_PASS
#include "sdf-sky-lighting.hlsli"
#include "sdf-sky-common.hlsli"
#include "sdf-sky-panel.hlsli"

float3 worldSkyIrradiance(float3 normal) {
    if (sdfSky[0].Ambient <= 0.0) {
        return 0.0.xxx;
    }
    return sdfSkyPhysicalIrradiance(normal) * sdfSky[0].Ambient;
}
float3 worldEnvironmentReflection(float3 direction, float roughness) {
    if (sdfSky[0].Reflection <= 0.0) {
        return 0.0.xxx;
    }
    float3 result = sdfSkyMapRadiance(direction, 1u);
    float3 sky = sdfSkyFrameDirection(direction);
    [loop] for (uint index = 0u; index < sdfSky[0].LayerCount; index++) {
        SdfSkyLayer layer = sdfSkyLayers[index];
        if (layer.Kind != SDF_SKY_KIND_PANEL || (layer.Visibility & SDF_SKY_VISIBILITY_LIGHTING) == 0u) {
            continue;
        }
        float mask = layer.Opacity * sdfSkyMaskWeight(layer, sky);
        if (mask <= 0.0) {
            continue;
        }
        puckCountDetail(layer.Detail, 0u, 0u, 1u, 0u, 0u);
        float4 value = sdfSkyPanelValue(sdfSkyPanelOf(layer), sdfSkyRotate(sky, layer.Rotation), roughness);
        float3 scale, offset;
        sdfSkyAffine(layer.Blend, value.rgb, value.a * mask, scale, offset);
        result = result * scale + offset;
    }
    return result * sdfSky[0].Reflection;
}
#endif
#include "shade-layers.hlsli"
#endif
