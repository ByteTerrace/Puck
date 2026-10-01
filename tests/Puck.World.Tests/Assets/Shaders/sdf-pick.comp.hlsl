// Probe the shipped winner and visibility seam with an articulated instance whose bound slot differs from its shape.
#define SDF_DYNAMIC_TRANSFORMS
#define SDF_PRIMARY_PASS
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-vm.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-work.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-visibility.hlsli"
[[vk::binding(60, 3)]] StructuredBuffer<float4> fieldCases : register(t60, space3);
[[vk::binding(61, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> fieldResults : register(u61, space3);
struct FieldProbeIndex { [[vk::offset(0)]] uint index; };
[[vk::push_constant]] ConstantBuffer<FieldProbeIndex> fieldProbeIndex : register(b0, space4);
[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint first = fieldProbeIndex.index & 0xFFFFu;
    uint count = fieldProbeIndex.index >> 16u;
    if (id.x >= count) return;
    float4 probe = fieldCases[first + id.x];
    uint index = asuint(probe.w);
    sdfProgramLayout = sdfLoadProgramLayout();
    SdfHit hit = map(probe.xyz);
    float3 gradient;
    SdfHit dual = mapGradMasked(probe.xyz, 0u, gradient);
    SdfVisibility visibility;
    visibility.t = 1.0;
    visibility.identity = sdfVisibilitySdfIdentity(true, hit.instanceIndex);
    visibility.material = hit.material;
    visibility.flags = 0;
    uint record = index * SdfVisibilityWords;
    sdfStoreVisibility(record, visibility);
    sdfStoreVisibilityFrameSlot(record, hit.frameSlot);
    SdfSurfaceSample sample = sdfLoadSurfaceSample(record);
    fieldResults[uint2(index % 256u, index / 256u)] = float4(
        (float)sdfVisibilitySource(sdfLoadVisibility(record).identity), (float)sample.frameSlot,
        all(sample.lanes == hit.lanes) ? 1.0 : 0.0,
        (dual.instanceIndex == hit.instanceIndex && dual.frameSlot == hit.frameSlot && all(dual.lanes == sample.lanes)) ? 1.0 : 0.0);
}
