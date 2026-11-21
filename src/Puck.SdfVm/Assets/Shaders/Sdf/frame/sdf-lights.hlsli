// The slot lights shading reads from the lights table (sdfLights, SdfLight records generated from
// Puck.SignedDistance.SdfLight), the shadow handoffs fading a slot from one light to another, and a positional light's
// current position.
#ifndef FRAME_SDF_LIGHTS_HLSLI
#define FRAME_SDF_LIGHTS_HLSLI
#include "sdf-environment.hlsli"
#include "../field/sdf-component.hlsli"

// Both the declared count and the actual bound buffer constrain a read.
uint worldLightCount() {
    uint count;
    uint stride;
    sdfLights.GetDimensions(count, stride);
    return min(passGroup.lightCount, min(count, SDF_MAX_LIGHTS));
}
uint worldShadowStableCount() { return min(passGroup.shadowSlotCount, SDF_MAX_SHADOW_SLOTS); }
// The active handoffs, at most the fade capacity the handoff table holds.
uint worldShadowHandoffCount() {
    uint count;
    uint stride;
    sdfShadowHandoffs.GetDimensions(count, stride);
    return min(passGroup.shadowFadeCount, min(count, (uint)SDF_SHADOW_FADE_CAPACITY));
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
// The light that holds a shadow role (SdfLights.RoleLight): the stable slot passGroup.shadowRoles names for it, or -1 when
// no slot holds it.
int worldShadowRoleLight(uint role) {
    uint slot = (uint)sdfComponent(passGroup.shadowRoles, role);

    return ((slot < worldShadowStableCount()) ? sdfComponent(passGroup.shadowSlots, slot) : -1);
}
// The shadow role a stable slot holds, or -1 for a slot that holds none (its light takes the probes' answer).
int worldShadowRoleOfSlot(int slot) {
    if ((uint)slot >= worldShadowStableCount()) { return -1; }
    [unroll]
    for (uint role = 0u; (role < SDF_SHADOW_ROLES); role++) {
        if (sdfComponent(passGroup.shadowRoles, role) == slot) {
            return (int)role;
        }
    }
    return -1;
}
// The key light's direction (the primary shadow role's light), or the pinned sun's when no slotted light emits, for the
// glass tint and material fill.
float3 worldSunDirection() {
    int index = worldShadowRoleLight(0u);

    return ((index >= 0 && (uint)index < worldLightCount()) ? sdfLights[(uint)index].Direction : SdfSunDirection);
}
// The key light as the luminance it gives a white surface facing it, in nits: the scale an authored material fill stands
// in for indirect light by. Zero while no slotted light emits.
float3 worldKeyRadiance() {
    int index = worldShadowRoleLight(0u);

    if ((index < 0) || ((uint)index >= worldLightCount())) {
        return float3(0.0, 0.0, 0.0);
    }
    SdfLight key = sdfLights[(uint)index];

    return (key.Color * key.Weight);
}
// The window a positional light's falloff is eased to zero by at its range r: saturate(1 - (d / r)^4)^2, one at the light
// and exactly zero at and past r, so nothing past the range receives any of its light. A point's and a screen's reach
// both take it. KEEP IN SYNC with SdfPhotometry.Falloff.
float sdfLightWindow(float distanceSquared, float range) {
    float ratio = (distanceSquared / max((range * range), 1.0e-8));
    float window = saturate(1.0 - (ratio * ratio));

    return (window * window);
}
// A point or spot light's reach at a surface point, and the unit direction toward the light: the windowed inverse square
// sdfLightWindow(d, r) / max(d, SDF_LIGHT_MINIMUM_DISTANCE)^2 under the spot's cone
// saturate(dot(-l, SpotAxis) + SpotOffset)^2 (one for a point, whose axis is zero and offset one). The light's own weight
// is no part of it. KEEP IN SYNC with SdfPhotometry.Falloff and SdfPhotometry.Cone.
float sdfPointReach(SdfLight light, float3 lightPosition, float3 surfacePosition, out float3 direction) {
    float3 toLight = (lightPosition - surfacePosition);
    float distanceSquared = dot(toLight, toLight);
    float lightDistance = sqrt(distanceSquared);

    direction = (toLight / max(lightDistance, 1.0e-4));

    float cone = saturate(dot(-direction, light.SpotAxis) + light.SpotOffset);

    return ((sdfLightWindow(distanceSquared, light.Param) * (cone * cone)) / max(distanceSquared, (SDF_LIGHT_MINIMUM_DISTANCE * SDF_LIGHT_MINIMUM_DISTANCE)));
}
#endif
