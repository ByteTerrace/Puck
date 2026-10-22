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
// The environment a lobe of a roughness reflects about a direction: the lighting-visible map there for a mirror, eased
// toward the sky's cosine-weighted mean radiance about the direction (its irradiance over pi) as the lobe widens to
// the hemisphere at full roughness, then the panels of the artistic reflection path.
float3 worldEnvironmentReflection(float3 direction, float roughness) {
    if (sdfSky[0].Reflection <= 0.0) {
        return 0.0.xxx;
    }
    float3 result = lerp(sdfSkyMapRadiance(direction, 1u), (sdfSkyPhysicalIrradiance(direction) / 3.141592653589793), smoothstep(0.1, 0.8, roughness));
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
        float4 value = sdfSkyPanelValue(sdfSkyPanelOf(layer), rotatePointByQuaternion(sky, layer.Rotation), roughness);
        float3 scale, offset;
        sdfSkyAffine(layer.Blend, value.rgb, value.a * mask, scale, offset);
        result = result * scale + offset;
    }
    return result * sdfSky[0].Reflection;
}
#endif
#include "shade-layers.hlsli"
#endif
