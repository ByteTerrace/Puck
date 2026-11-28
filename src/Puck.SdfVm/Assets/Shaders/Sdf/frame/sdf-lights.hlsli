// The slot lights shading reads from the lights table (sdfLights, SdfLight records generated from
// Puck.SignedDistance.SdfLight), the shadow handoffs fading a slot from one light to another, and a positional light's
// current position. The table is the one light list: the frame's lights, then a face light (SDF_LIGHT_FACE) for every
// resident screen slot, whose luminance the screens' emission pass reduces (sdfScreenLights, sdfFaceLight).
#ifndef FRAME_SDF_LIGHTS_HLSLI
#define FRAME_SDF_LIGHTS_HLSLI
#include "Shared/luma.hlsli"
#include "sdf-environment.hlsli"
#include "sdf-screen-mapping.hlsli"
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

// Whether a light reaches only the range about its position: a point or spot light or a face (the sphere of its range)
// or a line light (the capsule of its range about its segment). KEEP IN SYNC with SdfLight.IsRanged.
bool sdfLightRanged(uint kind) {
    return ((kind == SDF_LIGHT_POINT) || (kind == SDF_LIGHT_LINE) || (kind == SDF_LIGHT_FACE));
}
// Whether a light's position may ride a dynamic transform (worldPointLightPosition): a point or a line. KEEP IN SYNC with
// SdfLight.IsPositional.
bool sdfLightPositional(uint kind) {
    return ((kind == SDF_LIGHT_POINT) || (kind == SDF_LIGHT_LINE));
}

// A point or line light's current world-space position (a line's midpoint): the live dynamic transform its slot names (an anchored
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
// A ranged light's current position: a point's or a line's as worldPointLightPosition reads it, a face's center.
float3 sdfLightPlace(SdfLight light) {
    return (sdfLightPositional(light.Kind) ? worldPointLightPosition(light) : light.Direction);
}
// Whether the frame's screen lights are off (SdfFrame.DisableScreenLights), which every face light reads as dark.
bool worldScreenLightsDisabled() {
    return (passGroup.disableScreenLights != 0u);
}
// The screen slot a face light's luminance is read from (SdfLight.Face: SpotOffset, a whole number).
uint sdfFaceSlot(SdfLight light) {
    return (uint)light.SpotOffset;
}
// A face light's luminance in nits, the face's mean as the screens' emission pass reduced it (sdfScreenLights): zero while
// screen lights are off, past the slots, and for a slot that shows no bound source and is no emitter face (the state
// row's bound flag and face, SdfWorldTables.SetScreenBound and WriteEmitterState), since a source binds and unbinds
// without moving the lighting a tile stands on, and the reduction keeps an unbound slot's last mean until it runs again.
float3 sdfFaceLight(SdfLight light) {
    uint slot = sdfFaceSlot(light);

    if (worldScreenLightsDisabled() || (slot >= SDF_MAX_SCREEN_SURFACES)) {
        return float3(0.0, 0.0, 0.0);
    }

    float4 state = worldScreenState(slot);

    return (((state.x != 0.0) || (state.w == 2.0)) ? sdfScreenLights[slot].rgb : float3(0.0, 0.0, 0.0));
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

// The nearest a line light's distance is taken as: its tube's radius (SpotOffset), never under a point light's.
float sdfLineNearest(SdfLight light) {
    return max(light.SpotOffset, SDF_LIGHT_MINIMUM_DISTANCE);
}
// The point of a line light's segment nearest a surface point, as the offset from the surface to it. KEEP IN SYNC with
// the tile bound in sdfLightReachesBox, which bounds this distance from the midpoint less the half segment.
float3 sdfLineNearestOffset(SdfLight light, float3 midpoint, float3 surfacePosition) {
    float3 halfSegment = light.SpotAxis;
    float halfSquared = max(dot(halfSegment, halfSegment), 1.0e-12);
    float along = clamp((dot((surfacePosition - midpoint), halfSegment) / halfSquared), -1.0, 1.0);

    return ((midpoint + (halfSegment * along)) - surfacePosition);
}
// A line light's reach at a surface point, and the unit direction its diffuse light arrives along: the segment's vector
// irradiance V, the integral along it of the unit direction toward each element over its squared distance, whose closed
// form is (u0 + u1) * length / (|l0| |l1| + l0 . l1) for the offsets l0, l1 to its ends and their unit directions u0,
// u1 (Karis 2013, "Real Shading in Unreal Engine 4": exact for a segment the horizon does not clip; n . V is the
// irradiance of a facing element's integral of cos / r^2). The reach is |V| under the point light's window at the
// segment's nearest point and the direction V / |V|, so a wrapped Lambert of the direction times the reach is n . V
// wherever the whole segment lies over the surface's horizon. The denominator is held to twice the nearest distance's
// square, as a point light's distance is held to its nearest, so |V| never passes length / nearest^2, the bound
// sdfLightReachesBox tests. The light's own weight (its intensity per metre over pi) is no part of it.
float sdfLineReach(SdfLight light, float3 midpoint, float3 surfacePosition, out float3 direction) {
    float3 halfSegment = light.SpotAxis;
    float3 toStart = ((midpoint - halfSegment) - surfacePosition);
    float3 toEnd = ((midpoint + halfSegment) - surfacePosition);
    float startLength = length(toStart);
    float endLength = length(toEnd);
    float nearest = sdfLineNearest(light);
    float denominator = max(((startLength * endLength) + dot(toStart, toEnd)), ((2.0 * nearest) * nearest));
    float3 irradiance = (((toStart / max(startLength, 1.0e-6)) + (toEnd / max(endLength, 1.0e-6))) * ((2.0 * length(halfSegment)) / denominator));
    float magnitude = length(irradiance);
    float3 toNearest = sdfLineNearestOffset(light, midpoint, surfacePosition);

    direction = ((magnitude > 1.0e-12) ? (irradiance / magnitude) : (toNearest * rsqrt(max(dot(toNearest, toNearest), 1.0e-12))));

    return (magnitude * sdfLightWindow(dot(toNearest, toNearest), light.Param));
}

// The share a reach is widened by before a box is tested against it, past the rounding between this test and the
// response's own: a light is left out only where its response is zero by a margin.
static const float SdfLightReachMargin = 1.0e-3;

// The squared distance from `center` to the box `low`..`high`; zero inside it.
float sdfBoxDistanceSquared(float3 center, float3 low, float3 high) {
    float3 outside = max(max((low - center), (center - high)), 0.0);

    return dot(outside, outside);
}
// Whether a point, spot or line light at `position` can add anything at a point of the box `low`..`high` in a pixel
// pass: whether the box meets the sphere of its reach, for a spot whether the sphere about the box meets its cone, and
// whether a point or spot light's bound over the box reaches the view's cutoff. A point or spot light gives a point at
// most its weight times its window at the box's nearest point over that point's squared distance, times the largest
// cone factor over the sphere about the box, and fades to nothing under the cutoff (sdfLightCutoffFade,
// shade/sdf-light.hlsli: luminance times the view's exposure against SDF_LIGHT_RANGE_THRESHOLD; the two read one rule).
// The cone is where saturate(dot(-l, SpotAxis) + SpotOffset) is above zero (sdfPointReach): the directions from the
// light within the angle whose cosine is -SpotOffset / |SpotAxis| of the axis. A sphere lies wholly outside a cone of
// half-angle a when cos(a) * across - sin(a) * along exceeds its radius, `along` and `across` its centre's offset from
// the apex along and across the axis: that expression is never more than the centre's distance to the cone.
//
// A line light at midpoint `position` is bounded by its segment's nearest distance to the box, which is at least the
// midpoint's less the half segment: every element of the segment lies at least that far from every point of the box, so
// the line gives a point of it at most its weight times its length, under its window there, over that distance squared
// (sdfLineReach), and it has no cone.
//
// A face light lights only what lies in front of its face's plane, and nothing while its luminance is zero
// (sdfLightInputs): a face of luminance L and weight w (its area over pi) gives a point at most L * w / (d^2 + w), its
// facing and window at most one. So it reaches the box where the box has a point in front of the plane (the box's
// farthest reach along the facing, its centre's offset plus its half extent projected on the facing's magnitudes) within
// its range, and that bound at the box's nearest point reaches the view's cutoff.
bool sdfLightReachesBox(SdfLight light, float3 position, float3 low, float3 high) {
    float reach = (light.Param * (1.0 + SdfLightReachMargin));
    float nearest = sdfBoxDistanceSquared(position, low, high);

    if (light.Kind == SDF_LIGHT_FACE) {
        float luminance = dot(sdfFaceLight(light), PuckRec709Luminance);
        float3 offset = (((low + high) * 0.5) - position);
        float3 extent = ((high - low) * 0.5);

        return ((nearest <= (reach * reach))
            && (((luminance * light.Weight * passGroup.exposure) * (1.0 + SdfLightReachMargin)) > (SDF_LIGHT_RANGE_THRESHOLD * (nearest + light.Weight)))
            && ((dot(light.SpotAxis, offset) + dot(abs(light.SpotAxis), extent)) > (-SdfLightReachMargin * (length(offset) + length(extent)))));
    }

    if (light.Kind == SDF_LIGHT_LINE) {
        float gap = max((sqrt(nearest) - length(light.SpotAxis)), 0.0);

        nearest = (gap * gap);
    }
    if (nearest > (reach * reach)) {
        return false;
    }
    if (light.Kind == SDF_LIGHT_LINE) {
        float floorDistance = sdfLineNearest(light);
        float lineBound = (((light.Weight * (2.0 * length(light.SpotAxis))) * sdfLightWindow(nearest, light.Param)) / max(nearest, (floorDistance * floorDistance)));

        return (((lineBound * passGroup.exposure) * (1.0 + SdfLightReachMargin)) > SDF_LIGHT_RANGE_THRESHOLD);
    }
    if (light.Kind != SDF_LIGHT_POINT) {
        return true;
    }

    // The light's bound at the box's nearest point, window included (sdfLightWindow falls with distance), before the cone.
    float bound = ((light.Weight * sdfLightWindow(nearest, light.Param)) / max(nearest, (SDF_LIGHT_MINIMUM_DISTANCE * SDF_LIGHT_MINIMUM_DISTANCE)));
    float axisLength = length(light.SpotAxis);

    if ((axisLength > 0.0) && (light.SpotOffset < axisLength)) {
        float cosine = clamp((-light.SpotOffset / axisLength), -1.0, 1.0);
        float sine = sqrt(max((1.0 - (cosine * cosine)), 0.0));
        float3 offset = (((low + high) * 0.5) - position);
        float radius = ((0.5 * length(high - low)) * (1.0 + SdfLightReachMargin));
        float distance = length(offset);
        float along = (dot(offset, light.SpotAxis) / axisLength);
        float across = sqrt(max(((distance * distance) - (along * along)), 0.0));

        if (((cosine * across) - (sine * along)) > (radius + (SdfLightReachMargin * distance))) {
            return false;
        }
        // The cone's largest factor over the sphere about the box: the cosine of the least angle from the axis to a
        // direction into the sphere, the angle to its centre less the sphere's angular radius (one where the sphere holds
        // the light or the axis meets it).
        float least = ((radius >= distance) ? 0.0 : max((atan2(across, along) - asin(saturate(radius / max(distance, 1.0e-6)))), 0.0));
        float cone = saturate((axisLength * cos(least)) + light.SpotOffset);

        bound *= (cone * cone);
    }

    return (((bound * passGroup.exposure) * (1.0 + SdfLightReachMargin)) > SDF_LIGHT_RANGE_THRESHOLD);
}
#endif
