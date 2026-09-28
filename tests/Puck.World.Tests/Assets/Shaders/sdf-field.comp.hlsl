// The SDF field probe: evaluates the shipped interpreter (field/sdf-vm.hlsli) over the program the host packed into
// sdfWords, at each case's point, and writes what map() returns into one texel of the results. SdfFieldDeviceLawTests
// packs each program with SdfProgram, binds it at the world interface's sdfWords, and dispatches the probe once per
// program, pushing the program's first case and its case count. The probe's own two resources sit in the Pass group
// at bindings the world interface leaves free; the pushed index is Direct3D 12's root constant at b0, space 4.

#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-vm.hlsli"

// Each case: the point, and the index of the results texel it writes (as bits).
[[vk::binding(60, 3)]] StructuredBuffer<float4> fieldCases : register(t60, space3);
// Each result: the distance map() returns, the material's bits, and the program's step scale; the results image is
// FieldResultsWidth texels wide.
[[vk::binding(61, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> fieldResults : register(u61, space3);

static const uint FieldResultsWidth = 256u;

struct FieldProbeIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<FieldProbeIndex> fieldProbeIndex : register(b0, space4);

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    // The pushed index packs the program's first case in its low 16 bits and its case count in its high 16.
    uint first = (fieldProbeIndex.index & 0xFFFFu);
    uint count = (fieldProbeIndex.index >> 16u);

    if (id.x >= count) {
        return;
    }

    float4 probe = fieldCases[(first + id.x)];
    uint slot = asuint(probe.w);

    sdfProgramLayout = sdfLoadProgramLayout();

    SdfHit hit = map(probe.xyz);

    fieldResults[uint2((slot % FieldResultsWidth), (slot / FieldResultsWidth))] = float4(hit.distance, asfloat(hit.material), sdfProgramLayout.stepScale, 0.0);
}
