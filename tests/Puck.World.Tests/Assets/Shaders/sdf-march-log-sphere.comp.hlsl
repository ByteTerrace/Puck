// The fold-wall march probe: runs a shipped march along one ray per case over the program the host packed into
// sdfWords, with no instance mask and no beam-proven gap, and writes where it stopped, or evaluates the field at one
// point. SdfLogSphereMarchDeviceLawTests packs each program with SdfProgram, binds it at the world interface's sdfWords
// and dispatches the probe once per case, pushing its index. The probe's own two resources sit in the Pass group at
// bindings the world interface leaves free; the pushed index is Direct3D 12's root constant at b0, space 4.
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-vm.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-work.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/frame/sdf-levers.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/march/sdf-march-constants.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/march/sdf-primary.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/surface/sdf-ambient.hlsli"

// Three rows per case: the ray's origin (or the field's point) and its far distance; its unit direction and the pixel
// footprint; then the probe's mode and inputs (x: the mode; y: the primary march's start or the shadow's reach; z: the
// shadow's sharpness).
#define SDF_MARCH_PROBE_PRIMARY 0
#define SDF_MARCH_PROBE_SHADOW 1
#define SDF_MARCH_PROBE_FIELD 2
[[vk::binding(60, 3)]] StructuredBuffer<float4> marchCases : register(t60, space3);
// One result per case. Primary: where the march stopped, whether it found a surface, its steps and its material's
// bits. Shadow: the visibility. Field: the distance map() returns.
[[vk::binding(61, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> marchResults : register(u61, space3);
struct MarchProbeIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<MarchProbeIndex> marchProbeIndex : register(b0, space4);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint index = marchProbeIndex.index;
    float4 origin = marchCases[(3u * index)];
    float4 direction = marchCases[((3u * index) + 1u)];
    float4 inputs = marchCases[((3u * index) + 2u)];
    int mode = (int)round(inputs.x);
    float4 result = float4(0.0, 0.0, 0.0, 0.0);

    sdfProgramLayout = sdfLoadProgramLayout();

    if (mode == SDF_MARCH_PROBE_PRIMARY) {
        SdfPrimaryMarch march = sdfTracePrimaryField(origin.xyz, direction.xyz, inputs.y, origin.w, origin.w, origin.w, origin.w,
            SDF_INSTANCE_MASK_ALL, direction.w, uint4(0u, 0u, 0u, 0u), false, false);

        result = float4(march.traveled, (march.found ? 1.0 : 0.0), (float)march.steps, asfloat(march.material));
    }
    else if (mode == SDF_MARCH_PROBE_SHADOW) {
        // The ray leaves the surface along its normal, toward a light along the same direction.
        result.x = softShadowVisibilityMarch(origin.xyz, direction.xyz, direction.xyz, SDF_INSTANCE_MASK_ALL, 1.0, inputs.y, false, inputs.z);
    }
    else {
        result.x = map(origin.xyz).distance;
    }

    marchResults[uint2(index, 0u)] = result;
}
