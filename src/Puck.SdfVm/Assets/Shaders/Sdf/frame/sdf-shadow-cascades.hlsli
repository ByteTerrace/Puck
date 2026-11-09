// The shadow cascades' light cameras as the pass block carries them (SdfMeshShadowCascades, SdfFrameBlock), and the one
// test of which cascade holds a point: the surface pass's soft shadows (sdfMeshShadow) and the views pass's cel shadow
// step (sdfToonShadowTexels) choose a cascade by it, so both read the same texel at a receiver.
#ifndef FRAME_SDF_SHADOW_CASCADES_HLSLI
#define FRAME_SDF_SHADOW_CASCADES_HLSLI

// One cascade's light camera, from the pass block's rows.
struct SdfMeshCascade {
    float3 position;
    float halfHeight;
    float3 right;
    float aspect;
    float3 up;
    float near;
    float3 forward;
    float far;
};
SdfMeshCascade sdfMeshCascadeAt(uint cascade) {
    uint row = (cascade * 4u);
    float4 r0 = passGroup.meshShadowCascades[row];
    float4 r1 = passGroup.meshShadowCascades[row + 1u];
    float4 r2 = passGroup.meshShadowCascades[row + 2u];
    float4 r3 = passGroup.meshShadowCascades[row + 3u];
    SdfMeshCascade c;
    c.position = r0.xyz; c.halfHeight = r0.w;
    c.right = r1.xyz; c.aspect = r1.w;
    c.up = r2.xyz; c.near = r2.w;
    c.forward = r3.xyz; c.far = r3.w;
    return c;
}
// A cascade texel's width in world units.
float sdfMeshCascadeTexel(SdfMeshCascade c) {
    return ((2.0 * c.halfHeight) / float(max(passGroup.meshShadowTexels, 1u)));
}
// Whether the cascade holds a point: its plane coordinates (each in [-1, 1] across the target) within the cascade's
// guard band, and its depth along the light between the near and far planes. A cascade holds the casters between those
// planes only; a receiver past its far plane (the floor far below a high camera's near slice) would read every empty
// texel's far depth as a blocker in front of it.
bool sdfMeshCascadeHolds(SdfMeshCascade c, float3 receiver, out float2 plane, out float depth) {
    float3 relative = (receiver - c.position);
    plane = float2((dot(relative, c.right) / (c.aspect * c.halfHeight)), (dot(relative, c.up) / c.halfHeight));
    depth = dot(relative, c.forward);
    return (all(abs(plane) <= 0.97) && (depth > c.near) && (depth < c.far));
}
// The first of the cascades [first, end) that holds a point, or end when none does.
uint sdfMeshCascadeHolding(float3 receiver, uint first, uint end) {
    [loop]
    for (uint cascade = first; (cascade < end); cascade++) {
        float2 plane;
        float depth;
        if (sdfMeshCascadeHolds(sdfMeshCascadeAt(cascade), receiver, plane, depth)) {
            return cascade;
        }
    }
    return end;
}

#endif
