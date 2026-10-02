// The primary-march probe: runs the shipped primary march (sdfTracePrimaryField, march/sdf-primary.hlsli) along one
// ray per case over the program the host packed into sdfWords, with no instance mask and no beam-proven gap, and writes
// where it stopped. SdfMarchLodDeviceLawTests packs each program with SdfProgram, binds it at the world interface's
// sdfWords and dispatches the probe once per case, pushing its index. The probe's own two resources sit in the Pass
// group at bindings the world interface leaves free; the pushed index is Direct3D 12's root constant at b0, space 4.
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-vm.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-work.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-lights.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/march/sdf-march-constants.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/march/sdf-primary.hlsli"

// Two rows per case: the ray's origin and its far distance, then its unit direction and the pixel footprint. The LOD
// origin is the ray's origin, as a view's camera position is.
[[vk::binding(60, 3)]] StructuredBuffer<float4> marchCases : register(t60, space3);
// One result per case: where the march stopped, whether it found a surface, its steps and its material's bits.
[[vk::binding(61, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> marchResults : register(u61, space3);
struct MarchProbeIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<MarchProbeIndex> marchProbeIndex : register(b0, space4);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint index = marchProbeIndex.index;
    float4 origin = marchCases[(2u * index)];
    float4 direction = marchCases[((2u * index) + 1u)];

    sdfProgramLayout = sdfLoadProgramLayout();
    sdfLodOrigin = origin.xyz;
    SdfPrimaryMarch march = sdfTracePrimaryField(origin.xyz, direction.xyz, 0.0, origin.w, origin.w, origin.w, origin.w,
        SDF_INSTANCE_MASK_ALL, direction.w, uint4(0u, 0u, 0u, 0u), false);

    marchResults[uint2(index, 0u)] = float4(march.traveled, (march.found ? 1.0 : 0.0), (float)march.steps, asfloat(march.material));
}
