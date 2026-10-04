// The slot lights the shadow march and shading read from the lights table (sdfLights, SdfLight records generated from
// Puck.SignedDistance.SdfLight), and a positional light's current position.
#ifndef FRAME_SDF_LIGHTS_HLSLI
#define FRAME_SDF_LIGHTS_HLSLI
#include "sdf-environment.hlsli"
#ifndef SDF_SHADOW_FADE_SLOTS
#define SDF_SHADOW_FADE_SLOTS 0
#endif

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
// Slot zero's direction, or the pinned sun's when it is vacant, for the glass tint and material fill.
float3 worldSunDirection() {
    int index = passGroup.shadowSlots.x;

    return ((index >= 0) ? sdfLights[(uint)index].Direction : SdfSunDirection);
}
float worldShadowPenumbraSlope(int index) {
    return ((index >= 0) ? max(sdfLights[(uint)index].Param, 1.0e-3) : (1.0 / 9.0));
}
// March rows are the K stable slots followed by the active incoming controls, never the reserved fade capacity.
uint worldShadowMarchCount() {
    return (passGroup.shadowSlotCount + min(passGroup.shadowFadeCount, (uint)SDF_SHADOW_FADE_SLOTS));
}
int worldShadowMarchLight(uint shadowSlot) {
    if (shadowSlot < passGroup.shadowSlotCount) {
        return passGroup.shadowSlots[shadowSlot];
    }
#if SDF_SHADOW_FADE_SLOTS > 0
    return sdfShadowHandoffs[shadowSlot - passGroup.shadowSlotCount].Incoming;
#else
    return -1;
#endif
}

#endif
