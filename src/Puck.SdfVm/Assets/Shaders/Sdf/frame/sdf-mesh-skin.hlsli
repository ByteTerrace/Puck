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
// Normalized dual-quaternion blend, in the group's frame. The first slot fixes the hemisphere in both frames.
float3 sdfMeshBlendPose(uint record, uint vertex, float3 p, inout float3 n, float4 q0, float3 t0, float4 q1, float3 t1) {
    uint word = sdfMeshInfluenceWord(record, vertex);
    uint packed = sdfMeshRegion[word + 1u];
    float w = float((packed >> 8u) & 65535u) / 65535.0;
    if (w <= 0.0) { n = rotatePointByQuaternion(n, q0); return rotatePointByQuaternion(p, q0) + t0; }
    if (w >= 1.0) { n = rotatePointByQuaternion(n, q1); return rotatePointByQuaternion(p, q1) + t1; }
    float4 d0 = 0.5 * sdfQuaternionProduct(float4(t0, 0.0), q0);
    float4 d1 = 0.5 * sdfQuaternionProduct(float4(t1, 0.0), q1);
    if (dot(q0, q1) < 0.0) { q1 = -q1; d1 = -d1; }
    float4 real = lerp(q0, q1, w);
    float inverseLength = rsqrt(dot(real, real));
    real *= inverseLength;
    float4 dual = lerp(d0, d1, w) * inverseLength;
    dual -= real * dot(real, dual);
    float3 translation = 2.0 * sdfQuaternionProduct(dual, sdfQuaternionConjugate(real)).xyz;
    uint xy = sdfMeshRegion[word + 2u];
    float3 v = float3(f16tof32(xy & 65535u), f16tof32(xy >> 16u), f16tof32(sdfMeshRegion[word + 3u] & 65535u));
    float3 separation = (rotatePointByQuaternion(p, q1) + t1) - (rotatePointByQuaternion(p, q0) + t0);
    float3 u = rotatePointByInverseQuaternion(separation, real);
    n = rotatePointByQuaternion(n - v * (dot(u, n) / (1.0 + dot(u, v))), real);
    return rotatePointByQuaternion(p, real) + translation;
}
float3 sdfMeshSkinCurrent(uint record, uint k, float3 p, inout float3 normal) {
    if ((sdfMeshRegion[record + SDF_MESH_FLAGS_WORD] & SDF_MESH_SKIN_FLAG) == 0u) { return p; }
    uint vertex = sdfMeshIndexAt(record, k);
    uint first = sdfMeshMemberEntry(record, vertex);
    uint second = sdfMeshSecondEntry(record, vertex);
    uint root = asuint(asint(sdfMeshRegion[record + SDF_MESH_FRAME_SLOT_WORD]));
    float3 rp = sdfDynamicTransforms[3u * root].xyz;
    float4 rq = sdfDynamicTransforms[3u * root + 1u];
    uint s0 = first == 0u ? root : sdfMeshMemberSlot(record, first);
    uint s1 = second == 0u ? root : sdfMeshMemberSlot(record, second);
    float4 q0, q1;
    float3 t0, t1;
    float scale = sdfMeshDrawScale(record);
    sdfMeshMemberDelta(first, rp, rq, scale, sdfDynamicTransforms[3u * s0].xyz, sdfDynamicTransforms[3u * s0 + 1u], q0, t0);
    if (((sdfMeshRegion[sdfMeshInfluenceWord(record, vertex) + 1u] >> 8u) & 65535u) == 0u) {
        normal = rotatePointByQuaternion(normal, q0);
        return rotatePointByQuaternion(p, q0) + t0;
    }
    sdfMeshMemberDelta(second, rp, rq, scale, sdfDynamicTransforms[3u * s1].xyz, sdfDynamicTransforms[3u * s1 + 1u], q1, t1);
    return sdfMeshBlendPose(record, vertex, p, normal, q0, t0, q1, t1);
}
#endif
