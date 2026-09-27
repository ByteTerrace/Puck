// The mesh surface probe: resolves one case's mesh surface through the shipped mesh module (frame/sdf-mesh.hlsli), the
// normal the surface pass writes (sdfMeshSurfaceNormal) and the material primary records (sdfMeshMaterial), and writes
// both into one texel of the output row. MeshSurfaceDeviceLawTests packs its draws into the region with
// SdfMeshRegion.Write and dispatches the probe once per case, pushing the case's index. It binds one group, set 3: each
// register number equals its binding in space 3, and the pushed index is Direct3D 12's root constant at b0, space 4.

[[vk::binding(0, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t0, space3);
// Each case: (the hit point, the draw) and (the ray direction, the triangle).
[[vk::binding(1, 3)]] StructuredBuffer<float4> meshCases : register(t1, space3);
[[vk::binding(2, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> meshResults : register(u2, space3);

#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-mesh.hlsli"

struct CaseIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<CaseIndex> caseIndex : register(b0, space4);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint slot = caseIndex.index;
    float4 hit = meshCases[(2u * slot)];
    float4 ray = meshCases[((2u * slot) + 1u)];
    uint draw = (uint)hit.w;
    uint triangleIndex = (uint)ray.w;

    meshResults[uint2(slot, 0u)] = float4(sdfMeshSurfaceNormal(draw, triangleIndex, hit.xyz, ray.xyz), (float)sdfMeshMaterial(draw, triangleIndex));
}
