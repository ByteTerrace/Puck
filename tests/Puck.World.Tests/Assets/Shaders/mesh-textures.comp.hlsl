// The mesh textures probe: resolves one case's textured mesh hit through the shipped atlas module
// (frame/sdf-mesh-textures.hlsli) and writes what the hit passes read into three texels of the output row: the linear
// albedo and the material, the world normal and the occlusion, the emission and the level. MeshTextureDeviceLawTests
// packs its draws with SdfMeshRegion.Write against the atlases SdfMeshAtlas.Pack made, uploads the atlases, and
// dispatches the probe once per case, pushing the case's index. It binds one group, set 3: each register number equals
// its binding in space 3, and the pushed index is Direct3D 12's root constant at b0, space 4.

[[vk::binding(0, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t0, space3);
// Each case: (the hit point, the draw) and (the pixel's footprint, the triangle, unused, unused).
[[vk::binding(1, 3)]] StructuredBuffer<float4> meshCases : register(t1, space3);
[[vk::binding(2, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> meshResults : register(u2, space3);
[[vk::binding(3, 3)]] Texture2D<float4> sdfMeshAlbedo : register(t3, space3);
[[vk::binding(4, 3)]] Texture2D<float4> sdfMeshNormals : register(t4, space3);
[[vk::binding(5, 3)]] Texture2D<float4> sdfMeshOcclusion : register(t5, space3);
[[vk::binding(6, 3)]] Texture2D<float4> sdfMeshMaterials : register(t6, space3);
[[vk::binding(7, 3)]] Texture2D<float4> sdfMeshEmission : register(t7, space3);
[[vk::binding(8, 3)]] SamplerState samplers[2] : register(s8, space3);

#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-mesh-textures.hlsli"

struct CaseIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<CaseIndex> caseIndex : register(b0, space4);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint slot = caseIndex.index;
    float4 hit = meshCases[(2u * slot)];
    float4 extra = meshCases[((2u * slot) + 1u)];
    uint draw = (uint)hit.w;
    uint triangleIndex = (uint)extra.y;
    SdfMeshTexel texel = sdfMeshTexelAt(draw, triangleIndex, hit.xyz, extra.x);

    meshResults[uint2((3u * slot), 0u)] = float4(sdfMeshTexelAlbedo(texel), (float)sdfMeshTexelMaterial(draw, texel));
    meshResults[uint2(((3u * slot) + 1u), 0u)] = float4(sdfMeshTexelNormal(draw, texel), sdfMeshTexelOcclusion(texel));
    meshResults[uint2(((3u * slot) + 2u), 0u)] = float4(sdfMeshTexelEmission(texel), texel.level);
}
