#define SDF_INDIRECT_PASS
#define SDF_SCREEN_SOURCES
#define SDF_GROUP_SHADOW_GATHER
#define SDF_DYNAMIC_TRANSFORMS
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-field.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-march.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-light-projection.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-viewport.hlsli"

[[vk::binding(126, 3)]] StructuredBuffer<float4> lightCases : register(t126, space3);
[[vk::binding(127, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> lightResults : register(u127, space3);
struct LightProbeIndex { [[vk::offset(0)]] uint index; };
[[vk::push_constant]] ConstantBuffer<LightProbeIndex> lightProbeIndex : register(b0, space4);

// One group builds a finite depth map with the production sweep, then reads receivers through the production
// projection and slope comparison. A second dispatch carries radius zero and must expose the rod's missing shadow.
[numthreads(64, 1, 1)]
void CSMain(uint3 groupThread : SV_GroupThreadID) {
    sdfProgramLayout = sdfLoadProgramLayout();
    uint lane = groupThread.x;
    float4 origin = lightCases[0];
    float4 right = lightCases[1];
    float4 up = lightCases[2];
    float4 toward = lightCases[3];
    uint resolution = (uint)lightCases[4].x;
    uint count = (uint)lightCases[4].y;
    float radius = lightProbeIndex.index == 0u ? origin.w : 0.0;
    ViewportData view = (ViewportData)0;
    view.position = float4(origin.xyz, 0.0);
    view.right = right;
    view.up = float4(up.xyz, 1.0);
    view.forward = float4(-toward.xyz, 0.0);
    view.lens = float4(up.w, 0.0, 0.0, toward.w);
    float start = worldSurfaceNearDistance(view);
    if (lane == 0u) { lightResults[uint2(lightProbeIndex.index, count)] = float4(start, 0.0, 0.0, 0.0); }
    [loop]
    for (uint texel = lane; texel < resolution * resolution; texel += 64u) {
        uint2 pixel = uint2(texel % resolution, texel / resolution);
        float2 uv = (float2(pixel) + 0.5) / resolution;
        float3 ray = cameraRayDirection(view, uv);
        float3 rayOrigin = cameraRayOrigin(view, uv);
        uint budget = SdfIndirectLightMarchSteps;
        SdfIndirectRay result = sdfIndirectMarch(rayOrigin + ray * start, ray, 0.0, toward.w - start,
            SDF_INSTANCE_MASK_ALL, radius, budget);
        float depth = result.kind == SdfIndirectKindHit ? start + result.distance
            : (result.kind == SdfIndirectKindExit ? asfloat(0x7f800000u) : asfloat(0x7fc00000u));
        indirectCacheRW[texel] = asuint(depth);
    }
    DeviceMemoryBarrierWithGroupSync();
    [loop]
    for (uint receiver = lane; receiver < count; receiver += 64u) {
        uint2 pixel;
        float travel;
        float3 ray;
        float3 surfacePoint = lightCases[5u + receiver].xyz;
        bool inside = sdfIndirectLightProject(surfacePoint, origin.xyz, right, up, toward, resolution, pixel, travel, ray);
        float depth = inside ? asfloat(indirectCacheRW[pixel.y * resolution + pixel.x]) : asfloat(0x7fc00000u);
        float visibility = isnan(depth) ? -1.0 : sdfIndirectLightCompare(travel, depth, radius, float3(0.0, 1.0, 0.0), ray);
        lightResults[uint2(lightProbeIndex.index, receiver)] = float4(visibility, depth, travel, radius);
    }
}
