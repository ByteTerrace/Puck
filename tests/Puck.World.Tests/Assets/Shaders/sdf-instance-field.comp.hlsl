// The per-instance field probe: the walk the mesh bake samples a group through (passes/sdf-mesh-bake.comp.hlsl), over the
// program the host packed into sdfWords, with only the instances a leg's mask selects in it and no world segment, at
// each case's point. It writes what mapCore returns into one texel of the results: the distance, the material's bits,
// the answering instance's index bits and the program's step scale. SdfFieldDeviceLawTests.Instances packs one mask
// entry a leg at sdfInstanceMasks (its primary words, then its summary words) and the legs' dynamic slots at
// sdfDynamicTransforms, and dispatches the probe once per leg, pushing the leg's first case and its case count.

#define SDF_INSTANCE_MASKS
#define SDF_DYNAMIC_TRANSFORMS
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-vm.hlsli"

// Each case: the point, and the index of the results texel it writes (as bits).
[[vk::binding(60, 3)]] StructuredBuffer<float4> fieldCases : register(t60, space3);
// Each result: the distance, the material's bits, the instance's bits and the program's step scale; the results image is
// FieldResultsWidth texels wide.
[[vk::binding(61, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> fieldResults : register(u61, space3);

static const uint FieldResultsWidth = 256u;
// The words one leg's mask entry takes: one primary word and one summary word, for a program of at most 32 instances.
static const uint FieldMaskEntryWords = 2u;

struct FieldProbeIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<FieldProbeIndex> fieldProbeIndex : register(b0, space4);

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    // The pushed index packs the leg's first case in its low 16 bits and its case count in its high 16; every leg has
    // the same count, so the leg is the first case over the count.
    uint first = (fieldProbeIndex.index & 0xFFFFu);
    uint count = (fieldProbeIndex.index >> 16u);

    if (id.x >= count) {
        return;
    }

    float4 probe = fieldCases[(first + id.x)];
    uint slot = asuint(probe.w);

    sdfProgramLayout = sdfLoadProgramLayout();
    sdfMapOmitWorldSegments = true;

    SdfHit hit = mapCore(probe.xyz, ((first / count) * FieldMaskEntryWords), true);

    fieldResults[uint2((slot % FieldResultsWidth), (slot / FieldResultsWidth))] = float4(hit.distance, asfloat(hit.material), asfloat(hit.instanceIndex), sdfProgramLayout.stepScale);
}
