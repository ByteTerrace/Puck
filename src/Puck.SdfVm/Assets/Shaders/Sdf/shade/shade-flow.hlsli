// The flow layer (SdfFlow) and the layer frame it reads, shared by every pass that shades a material: the views pass's
// light stage and the glass pass. One implementation, so an opaque surface and a glass surface wearing one flow move
// alike.
#ifndef SDF_SHADE_FLOW_HLSLI
#define SDF_SHADE_FLOW_HLSLI
#include "../field/sdf-noise.hlsli"
#include "../frame/sdf-mesh.hlsli"
#include "sdf-material.hlsli"
#include "shade-weathering.hlsli"

// A surface's layer frame: the winning instance frame its material layers read, the frame slot's (its origin, its turn
// and its root scale) for a surface riding one, world space for one with none. `layerRotation` is the slot's turn, the
// identity with no slot.
void sdfLayerFrame(int frameSlot, float3 surfacePoint, float3 normal, float3 rayDirection, out float3 layerPoint, out float3 layerNormal,
    out float3 layerRay, out float4 layerRotation) {
    layerPoint = surfacePoint;
    layerNormal = normal;
    layerRay = rayDirection;
    layerRotation = float4(0.0, 0.0, 0.0, 1.0);
    if (frameSlot != SDF_TRANSFORM_SLOT_NONE) {
        float4 origin = sdfDynamicTransforms[3u * (uint)frameSlot];
        layerRotation = sdfDynamicTransforms[(3u * (uint)frameSlot) + 1u];
        layerPoint = rotatePointByInverseQuaternion((surfacePoint - origin.xyz), layerRotation);
        // Rigid copies share unit-space palette layers; scale belongs to their root, not a palette copy.
        layerPoint /= ((origin.w > 0.0) ? origin.w : 1.0);
        layerNormal = rotatePointByInverseQuaternion(normal, layerRotation);
        layerRay = rotatePointByInverseQuaternion(rayDirection, layerRotation);
    }
}
// The flow layer (SdfFlow, the material's flow rows): the pattern's lookup point advects along the flow's tangential part
// by the flow clock, in two layers half a cycle apart whose triangle weights cross-fade so neither restart shows, each
// place's phase offset by a low-frequency noise so the surface never pulses as one, and the blend renormalized so its
// contrast holds through the cross-fade. The pattern reveals the flow's surface over its coverage and tilts the normal
// by its slope, scaled by the share of the flow that runs along the surface. `layerFlow` is the velocity in the layer
// frame the caller converted it to; `clock` is the pass block's flow clock (SdfFlow.Clock), the one way the phase enters;
// `footprint` the world size of the pixel at the hit, which fades unresolved pattern toward its mean as the weathering
// does; `lanes` the surface's frame-slot lanes, which with the world's (sdfLane) drive the velocity and coverage lanes;
// `contact` a coverage the caller adds to the flow's own (a glass's contact ramp, sdfGlassContact), revealed in the same
// pattern. Returns the shading normal in the layer frame, `layerNormal` itself for a material with no flow or no tilt,
// and in `revealed` the share of the surface the coverage revealed.
// KEEP IN SYNC with SdfAppearance.Pack's flow rows.
float3 sdfApplyFlow(float3 layerPoint, float3 layerNormal, float3 layerFlow, float clock, float footprint, float4 lanes, float contact,
    inout SdfMaterialData material, out float revealed) {
    revealed = 0.0;
    float scale = material.flowVelocity.w;
    float4 shape = material.flowShape;
    if ((scale <= 0.0) || ((shape.z <= 0.0) && (shape.w <= 0.0) && (contact <= 0.0))) return layerNormal;
    uint lane = (material.flowControl & SDF_FLOW_LANE_MASK);
    uint coverageLane = ((material.flowControl >> SDF_FLOW_COVERAGE_LANE_SHIFT) & SDF_FLOW_LANE_MASK);
    float3 flow = (layerFlow * ((lane != 0u) ? sdfLane(lanes, lane - 1u) : 1.0));
    float coverage = saturate((shape.w * ((coverageLane != 0u) ? saturate(sdfLane(lanes, coverageLane - 1u)) : 1.0)) + contact);
    float3 tangent = (flow - (layerNormal * dot(layerNormal, flow)));
    float speed = length(tangent);
    float3 along = ((speed > 1.0e-6) ? (tangent / speed) : float3(0.0, 0.0, 0.0));
    float squeeze = (1.0 - (1.0 / max(shape.x, 1.0)));
    uint seed = material.flowSeed;
    float offset = (0.5 + (0.5 * sdfLatticeNoise3((layerPoint * (0.125 * scale)), (seed ^ 0x68E31DA4u))));
    float c0 = frac((clock * shape.y) + offset);
    float c1 = frac(c0 + 0.5);
    float w0 = (1.0 - abs((2.0 * c0) - 1.0));
    float w1 = (1.0 - w0);
    float3 travel = (tangent * (SDF_FLOW_CLOCK_PERIOD / shape.y));
    float3 q0 = ((layerPoint - (travel * (c0 - 0.5))) * scale);
    float3 q1 = (((layerPoint - (travel * (c1 - 0.5))) * scale) + float3(5.3, 2.9, 7.1));
    q0 -= (along * (dot(q0, along) * squeeze));
    q1 -= (along * (dot(q1, along) * squeeze));
    float4 pattern = (((w0 * sdfLatticeNoiseGradient3(q0, seed)) + (w1 * sdfLatticeNoiseGradient3(q1, seed))) * rsqrt(max(((w0 * w0) + (w1 * w1)), 1.0e-6)));
    float rate = (footprint * scale);
    float fade = (1.0 - smoothstep(0.35, 0.9, rate));
    if (coverage > 0.0) {
        float threshold = (1.0 - coverage);
        float soft = max(material.flowColor.w, 0.001);
        float reveal = smoothstep((threshold - soft), (threshold + soft), (0.5 + (0.5 * clamp(pattern.x, -1.0, 1.0))));
        revealed = lerp(coverage, reveal, fade);
        sdfRevealSurface(material.flowColor.rgb, material.flowResponse, revealed, material);
    }
    if ((shape.z <= 0.0) || (speed <= 1.0e-6)) return layerNormal;
    float3 slope = (pattern.yzw * fade);
    slope -= (along * (dot(slope, along) * squeeze));
    slope -= (layerNormal * dot(layerNormal, slope));
    float share = saturate(speed / max(length(layerFlow), 1.0e-6));
    return normalize(layerNormal - (slope * (shape.z * share)));
}
// The flow layer at a shaded surface, in the frame its flow names: a world-frame flow runs in world space, its pattern
// continuous across every surface wearing it; an object-frame flow runs in the layer frame (sdfLayerFrame), its velocity
// riding a slot's frame as it is, and turned by the draw's matrix and carry on a draw shading in world space (a static
// copy). Returns the world shading normal, `normal` itself for a material with no flow; `revealed` and `contact` as
// sdfApplyFlow's.
float3 sdfShadeFlow(float3 surfacePoint, float3 normal, float3 layerPoint, float3 layerNormal, float4 layerRotation, int frameSlot, uint meshDraw,
    float footprint, float4 lanes, float contact, inout SdfMaterialData material, out float revealed) {
    revealed = 0.0;
    if (material.flowVelocity.w <= 0.0) {
        return normal;
    }
    bool worldFlow = ((material.flowControl & SDF_FLOW_WORLD_FRAME) != 0u);
    bool slotFlow = (!worldFlow && (frameSlot != SDF_TRANSFORM_SLOT_NONE));
    float3 flowVelocity = material.flowVelocity.xyz;
    if (!worldFlow && !slotFlow) {
        uint flowRecord = sdfMeshRecord(meshDraw);
        flowVelocity = sdfMeshCarryDirection(flowRecord, sdfMeshWorldDirection(flowRecord, flowVelocity));
    }
    float3 flowNormal = sdfApplyFlow((worldFlow ? surfacePoint : layerPoint), (worldFlow ? normal : layerNormal), flowVelocity,
        passGroup.flowClock, footprint, lanes, contact, material, revealed);
    return (slotFlow ? rotatePointByQuaternion(flowNormal, layerRotation) : flowNormal);
}
#endif
