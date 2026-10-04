// Sky irradiance, environment reflections and the material layers.
#ifndef SHADE_SDF_LIGHTING_HLSLI
#define SHADE_SDF_LIGHTING_HLSLI
#include "shade-weathering.hlsli"
#include "../frame/sdf-lights.hlsli"
#ifdef SDF_VIEWS_PASS
#include "sdf-sky-environment.hlsli"
#include "sdf-sky-common.hlsli"
#include "sdf-sky-panel.hlsli"

float3 worldSkyIrradiance(float3 normal) {
    if (sdfSky[0].Ambient <= 0.0) {
        return 0.0.xxx;
    }
    puckAddWork(passGroup.workCounterRow * PuckWorkRowWords + PuckWorkSkyWord, 1u);
    float basis[9];
    sdfSkyEnvironmentBasis(normal, basis);
    float3 result = 0.0.xxx;
    [unroll] for (uint k = 0u; k < 9u; k++) {
        float convolution = ((k == 0u) ? 3.141592653589793 : ((k < 4u) ? 2.0943951023931953 : 0.7853981633974483));
        result += sdfSkyCoefficients[k].rgb * (basis[k] * convolution);
    }
    return max(result, 0.0.xxx) * sdfSky[0].Ambient;
}
float3 worldEnvironmentReflection(float3 direction, float roughness) {
    if (sdfSky[0].Reflection <= 0.0) {
        return 0.0.xxx;
    }
    puckAddWork(passGroup.workCounterRow * PuckWorkRowWords + PuckWorkSkyTextureLoadsWord, 4u);
    uint taps[4];
    float weights[4];
    sdfSkyEnvironmentTaps(direction, taps, weights);
    float3 result = 0.0.xxx;
    [unroll] for (uint i = 0u; i < 4u; i++) {
        result += weights[i] * sdfSkyEnvironmentUnpack(sdfSkyEnvironment[taps[i] + (uint)(SdfSkyEnvironmentSize * SdfSkyEnvironmentSize)]);
    }
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
