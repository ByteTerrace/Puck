// Generated native records are bound only by the passes that consume them.
#ifndef FRAME_SDF_LIGHTS_HLSLI
#define FRAME_SDF_LIGHTS_HLSLI
#include "sdf-environment.hlsli"

#ifdef SDF_LIGHTING_TABLES
float3 worldPointLightPosition(SdfLightData light) {
#ifdef SDF_DYNAMIC_TRANSFORMS
    if (light.DynamicSlot < 0) return light.Direction;
    uint slot = (uint)light.DynamicSlot;
    return sdfDynamicTransforms[3u * slot].xyz + rotatePointByQuaternion(light.Direction, sdfDynamicTransforms[3u * slot + 1u]);
#else
    return light.Direction;
#endif
}
float3 worldSunDirection() {
    int index = (lightFrame[0].ShadowIndex);
    return index >= 0 ? lights[(uint)index].Direction : SdfSunDirection;
}
float worldShadowPenumbraSlope() {
    int index = (lightFrame[0].ShadowIndex);
    return index >= 0 ? max(lights[(uint)index].Parameter, 1.0e-3) : (1.0 / 9.0);
}
#endif

#endif
