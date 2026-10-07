// The slot lights the shadow march and shading read from the lights table (sdfLights, SdfLight records generated from
// Puck.SignedDistance.SdfLight), and a positional light's current position.
#ifndef FRAME_SDF_LIGHTS_HLSLI
#define FRAME_SDF_LIGHTS_HLSLI
#include "sdf-environment.hlsli"
#ifndef SDF_SHADOW_FADE_SLOTS
#define SDF_SHADOW_FADE_SLOTS 0
#endif

// Both the declared count and the actual bound buffer constrain a read.
uint worldLightCount() {
    uint count;
    uint stride;
    sdfLights.GetDimensions(count, stride);
    return min(passGroup.lightCount, min(count, SDF_MAX_LIGHTS));
}
uint worldShadowStableCount() { return min(passGroup.shadowSlotCount, SDF_MAX_SHADOW_SLOTS); }
uint worldShadowIncomingCount() {
#if SDF_SHADOW_FADE_SLOTS > 0
    uint count;
    uint stride;
    sdfShadowHandoffs.GetDimensions(count, stride);
    return min(passGroup.shadowFadeCount, min(count, (uint)SDF_SHADOW_FADE_SLOTS));
#else
    return 0u;
#endif
}

// A point or occluder light's current world-space position: the live dynamic transform its slot names (an anchored
// light tracking a moving shape), or the authored static position when unanchored. Falls back to the static position on
// a kernel that binds no per-frame dynamic-transform table.
float3 worldPointLightPosition(SdfLight light) {
#ifdef SDF_DYNAMIC_TRANSFORMS
    if (light.DynamicSlot == SDF_TRANSFORM_SLOT_NONE) return light.Direction;
    uint count;
    uint stride;
    sdfDynamicTransforms.GetDimensions(count, stride);
    if ((uint)light.DynamicSlot >= count / 3u) { return light.Direction; }
    uint slot = (uint)light.DynamicSlot;
    return sdfDynamicTransforms[3u * slot].xyz + rotatePointByQuaternion(light.Direction, sdfDynamicTransforms[3u * slot + 1u]);
#else
    return light.Direction;
#endif
}
// Slot zero's direction, or the pinned sun's when it is vacant, for the glass tint and material fill.
float3 worldSunDirection() {
    int index = passGroup.shadowSlots.x;

    return ((index >= 0 && (uint)index < worldLightCount()) ? sdfLights[(uint)index].Direction : SdfSunDirection);
}
float worldShadowPenumbraSlope(int index) {
    return ((index >= 0 && (uint)index < worldLightCount()) ? max(sdfLights[(uint)index].Param, 1.0e-3) : (1.0 / 9.0));
}
// March rows are the K stable slots followed by the active incoming controls, never the reserved fade capacity.
uint worldShadowMarchCount() {
    return worldShadowStableCount() + worldShadowIncomingCount();
}
int worldShadowMarchLight(uint shadowSlot) {
    uint stable = worldShadowStableCount();
    if (shadowSlot < stable) {
        return passGroup.shadowSlots[shadowSlot];
    }
#if SDF_SHADOW_FADE_SLOTS > 0
    return shadowSlot - stable < worldShadowIncomingCount() ? sdfShadowHandoffs[shadowSlot - stable].Incoming : -1;
#else
    return -1;
#endif
}

#endif
