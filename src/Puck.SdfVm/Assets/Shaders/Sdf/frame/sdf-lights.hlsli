// The slot lights shading reads from the lights table (sdfLights, SdfLight records generated from
// Puck.SignedDistance.SdfLight), the shadow handoffs fading a slot from one light to another, and a positional light's
// current position.
#ifndef FRAME_SDF_LIGHTS_HLSLI
#define FRAME_SDF_LIGHTS_HLSLI
#include "sdf-environment.hlsli"

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
// Slot zero's direction, or the pinned sun's when it is vacant, for the glass tint and material fill.
float3 worldSunDirection() {
    int index = passGroup.shadowSlots.x;

    return ((index >= 0 && (uint)index < worldLightCount()) ? sdfLights[(uint)index].Direction : SdfSunDirection);
}
// Slot zero's light as the luminance it gives a white surface facing it, in nits: the scale an authored material fill
// stands in for indirect light by. Zero while the slot is vacant.
float3 worldKeyRadiance() {
    int index = passGroup.shadowSlots.x;

    if ((index < 0) || ((uint)index >= worldLightCount())) {
        return float3(0.0, 0.0, 0.0);
    }
    SdfLight key = sdfLights[(uint)index];

    return (key.Color * key.Weight);
}
// A point or spot light's reach at a surface point, and the unit direction toward the light: the windowed inverse square
// saturate(1 - (d / r)^4)^2 / max(d, SDF_LIGHT_MINIMUM_DISTANCE)^2, exactly zero at the range r, under the spot's cone
// saturate(dot(-l, SpotAxis) + SpotOffset)^2 (one for a point, whose axis is zero and offset one). The light's own weight
// is no part of it. KEEP IN SYNC with SdfPhotometry.Falloff and SdfPhotometry.Cone.
float sdfPointReach(SdfLight light, float3 lightPosition, float3 surfacePosition, out float3 direction) {
    float3 toLight = (lightPosition - surfacePosition);
    float distanceSquared = dot(toLight, toLight);
    float lightDistance = sqrt(distanceSquared);

    direction = (toLight / max(lightDistance, 1.0e-4));

    float ratio = (distanceSquared / max((light.Param * light.Param), 1.0e-8));
    float window = saturate(1.0 - (ratio * ratio));
    float cone = saturate(dot(-direction, light.SpotAxis) + light.SpotOffset);

    return (((window * window) * (cone * cone)) / max(distanceSquared, (SDF_LIGHT_MINIMUM_DISTANCE * SDF_LIGHT_MINIMUM_DISTANCE)));
}
// Whether the view shades the light at a table index: its bit of the view's light mask (SdfLightSelection), which
// leaves out every light that cannot reach a surface the view shows.
bool worldLightSelected(uint index) {
    return ((index < (uint)SDF_MAX_LIGHTS) &&((passGroup.lightMask[index >> 5u] & (1u << (index & 31u))) != 0u));
}

#endif
