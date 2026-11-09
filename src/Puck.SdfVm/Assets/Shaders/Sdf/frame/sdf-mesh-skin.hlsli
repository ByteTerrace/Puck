// Shared by visibility, surface, glass, shadows and previous-position reprojection. No field walk.
#ifndef SDF_MESH_SKIN_HLSLI
#define SDF_MESH_SKIN_HLSLI
// Carries a member's motion into the group's frame, removing its baked rest pose.
void sdfMeshMemberDelta(uint entry, float3 rootPosition, float4 rootRotation, float scale,
    float3 position, float4 rotation, out float4 q, out float3 t) {
    q = float4(0.0, 0.0, 0.0, 1.0);
    t = 0.0;
    if (entry == 0u) { return; }
    float3 restPosition = asfloat(uint3(sdfMeshRegion[entry + 1u], sdfMeshRegion[entry + 2u], sdfMeshRegion[entry + 3u]));
    float4 restRotation = asfloat(uint4(sdfMeshRegion[entry + 4u], sdfMeshRegion[entry + 5u], sdfMeshRegion[entry + 6u], sdfMeshRegion[entry + 7u]));
    q = sdfQuaternionProduct(sdfQuaternionProduct(sdfQuaternionConjugate(rootRotation), rotation), sdfQuaternionConjugate(restRotation));
    t = rotatePointByInverseQuaternion(position - rootPosition, rootRotation) / scale - rotatePointByQuaternion(restPosition, q);
}
// The transform slots a skinned vertex reads: the draw's root slot, then its first and second members' slots (the root's
// for the root sentinel), with the members' entries and the vertex's influence word. A vertex with no share reads no
// second member: SdfMeshSkinTuple.Of stores a vertex wholly one owner's under that owner, so it takes the rigid path.
struct SdfMeshSkinSlots {
    uint word;
    uint first;
    uint second;
    uint root;
    uint s0;
    uint s1;
    float w;
};
SdfMeshSkinSlots sdfMeshSkinSlotsOf(uint record, uint vertex) {
    SdfMeshSkinSlots slots;
    slots.word = sdfMeshInfluenceWord(record, vertex);
    uint packed = sdfMeshRegion[slots.word + 1u];
    slots.w = float((packed >> 8u) & 65535u) / 65535.0;
    slots.first = sdfMeshMemberEntryOf(record, sdfMeshRegion[slots.word]);
    slots.second = slots.w > 0.0 ? sdfMeshMemberEntryOf(record, packed & 255u) : 0u;
    slots.root = asuint(asint(sdfMeshRegion[record + SDF_MESH_FRAME_SLOT_WORD]));
    slots.s0 = slots.first == 0u ? slots.root : sdfMeshMemberSlot(record, slots.first);
    slots.s1 = slots.second == 0u ? slots.root : sdfMeshMemberSlot(record, slots.second);
    return slots;
}
// A skinned vertex in the group's frame under one frame's transforms (the current table or the previous one, read by the
// caller): rigid under its first member at no share, otherwise the normalized dual-quaternion blend of its two members'
// motions with the normal bent by the share's gradient. The first member fixes the quaternion hemisphere.
float3 sdfMeshSkinPose(SdfMeshSkinSlots slots, float3 p, inout float3 n, float scale, float3 rp, float4 rq,
    float3 p0, float4 r0, float3 p1, float4 r1) {
    float4 q0, q1;
    float3 t0, t1;
    sdfMeshMemberDelta(slots.first, rp, rq, scale, p0, r0, q0, t0);
    if (slots.w <= 0.0) { n = rotatePointByQuaternion(n, q0); return rotatePointByQuaternion(p, q0) + t0; }
    sdfMeshMemberDelta(slots.second, rp, rq, scale, p1, r1, q1, t1);
    float4 d0 = 0.5 * sdfQuaternionProduct(float4(t0, 0.0), q0);
    float4 d1 = 0.5 * sdfQuaternionProduct(float4(t1, 0.0), q1);
    if (dot(q0, q1) < 0.0) { q1 = -q1; d1 = -d1; }
    float4 real = lerp(q0, q1, slots.w);
    float inverseLength = rsqrt(dot(real, real));
    real *= inverseLength;
    float4 dual = lerp(d0, d1, slots.w) * inverseLength;
    dual -= real * dot(real, dual);
    float3 translation = 2.0 * sdfQuaternionProduct(dual, sdfQuaternionConjugate(real)).xyz;
    uint xy = sdfMeshRegion[slots.word + 2u];
    float3 v = float3(f16tof32(xy & 65535u), f16tof32(xy >> 16u), f16tof32(sdfMeshRegion[slots.word + 3u] & 65535u));
    float3 separation = (rotatePointByQuaternion(p, q1) + t1) - (rotatePointByQuaternion(p, q0) + t0);
    float3 u = rotatePointByInverseQuaternion(separation, real);
    n = rotatePointByQuaternion(n - v * (dot(u, n) / (1.0 + dot(u, v))), real);
    return rotatePointByQuaternion(p, real) + translation;
}
float3 sdfMeshSkinCurrent(uint record, uint k, float3 p, inout float3 normal) {
    if ((sdfMeshRegion[record + SDF_MESH_FLAGS_WORD] & SDF_MESH_SKIN_FLAG) == 0u) { return p; }
    SdfMeshSkinSlots slots = sdfMeshSkinSlotsOf(record, sdfMeshIndexAt(record, k));
    return sdfMeshSkinPose(slots, p, normal, sdfMeshDrawScale(record),
        sdfDynamicTransforms[3u * slots.root].xyz, sdfDynamicTransforms[3u * slots.root + 1u],
        sdfDynamicTransforms[3u * slots.s0].xyz, sdfDynamicTransforms[3u * slots.s0 + 1u],
        sdfDynamicTransforms[3u * slots.s1].xyz, sdfDynamicTransforms[3u * slots.s1 + 1u]);
}
#endif
