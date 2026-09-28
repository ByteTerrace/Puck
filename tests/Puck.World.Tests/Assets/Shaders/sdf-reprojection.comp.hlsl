// Runs the production reprojection against explicit visibility records, rigid poses, mesh matrices and camera rows.
[[vk::binding(0, 3)]] StructuredBuffer<uint> sdfMeshRegion : register(t0, space3);
[[vk::binding(1, 3)]] StructuredBuffer<float4> sdfDynamicTransforms : register(t1, space3);
[[vk::binding(2, 3)]] StructuredBuffer<float4> sdfPreviousDynamicTransforms : register(t2, space3);
[[vk::binding(3, 3)]] StructuredBuffer<float4> sdfPreviousMeshTransforms : register(t3, space3);
[[vk::binding(4, 3)]] StructuredBuffer<uint> sdfVisibilityRecords : register(t4, space3);
[[vk::binding(5, 3)]] StructuredBuffer<float4> motionCases : register(t5, space3);
[[vk::binding(6, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> motionResults : register(u6, space3);
struct MotionPass { float4 previousView[6]; };
static MotionPass passGroup;
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-reprojection.hlsli"

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint first = (7u * id.x);
    float4 samplePoint = motionCases[first];
    for (uint row = 0u; row < 6u; row++) {
        passGroup.previousView[row] = motionCases[first + 1u + row];
    }
    float2 pixel;
    float t;
    bool valid = sdfReprojection((16u * id.x), samplePoint.xyz, pixel, t);
    motionResults[uint2(id.x, 0u)] = float4(pixel, t, (valid ? 1.0 : 0.0));
}
