// Exercises the production proposal/admission helper and the shipped field interpreter, without a second marcher.
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-vm.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-lights.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/march/sdf-march-constants.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/march/sdf-march-seed.hlsli"

// Two rows per case: start/projected distance/footprint/direct clearance, then field mode/fold bound/unused/unused.
[[vk::binding(60, 3)]] StructuredBuffer<float4> seedCases : register(t60, space3);
[[vk::binding(61, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> seedResults : register(u61, space3);
struct SeedProbeIndex { [[vk::offset(0)]] uint index; };
[[vk::push_constant]] ConstantBuffer<SeedProbeIndex> seedProbeIndex : register(b0, space4);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint index = seedProbeIndex.index;
    float4 input = seedCases[2u * index];
    float4 mode = seedCases[2u * index + 1u];
    SdfMarchSeed seed;
    bool prepared = sdfPrepareMarchSeed(input.x, input.y, input.z, seed);
    float clearance = input.w;
    bool admitted = false;
    if (prepared) {
        if (mode.x > 0.0) {
            sdfProgramLayout = sdfLoadProgramLayout();
            SdfHit hit = map(float3(seed.midpoint, 0.0, 0.0));
            clearance = sdfMapBallClearance(hit.distance);
            // An explicit tighter bound tests the caller's fold-safe contract without inventing another field.
            clearance = min(clearance, mode.y);
        }
        admitted = sdfMarchSeedClears(seed, clearance);
    }
    seedResults[uint2(index, 0u)] = float4(seed.candidate, seed.midpoint, seed.radius,
        (prepared ? 1.0 : 0.0) + (admitted ? 2.0 : 0.0));
    seedResults[uint2(index, 1u)] = float4(clearance, 0.0, 0.0, 0.0);
}
