// The key light the shadow march and the shading read from the lights table (sdfLights, SdfLight records generated from
// Puck.SignedDistance.SdfLight), and a positional light's current position.
#ifndef FRAME_SDF_LIGHTS_HLSLI
#define FRAME_SDF_LIGHTS_HLSLI
#include "sdf-environment.hlsli"

// A point or occluder light's current world-space position: the live dynamic transform its slot names (an anchored
// light tracking a moving shape), or the authored static position when unanchored. Falls back to the static position on
// a kernel that binds no per-frame dynamic-transform table.
float3 worldPointLightPosition(SdfLight light) {
#ifdef SDF_DYNAMIC_TRANSFORMS
    if (light.DynamicSlot == SDF_TRANSFORM_SLOT_NONE) return light.Direction;
    uint slot = (uint)light.DynamicSlot;
    return sdfDynamicTransforms[3u * slot].xyz + rotatePointByQuaternion(light.Direction, sdfDynamicTransforms[3u * slot + 1u]);
#else
    return light.Direction;
#endif
}
// The shadow light's direction (the one directional whose Lambert term is soft-shadowed, passGroup.shadowLight), or the
// pinned sun's when no light shadows, so the material specular still has a key.
float3 worldSunDirection() {
    int index = passGroup.shadowLight;

    return ((index >= 0) ? sdfLights[(uint)index].Direction : SdfSunDirection);
}
float worldShadowPenumbraSlope() {
    int index = passGroup.shadowLight;

    return ((index >= 0) ? max(sdfLights[(uint)index].Param, 1.0e-3) : (1.0 / 9.0));
}

#endif
