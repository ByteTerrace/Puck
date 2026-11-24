// The SDF gradient probe: walks the shipped interpreter (field/sdf-vm.hlsli) in forward mode (SDF_FIELD_DUAL, as the
// bake's gradient kernel does) over the program the host packed into sdfWords at each case's point and at the bake's
// four tetrahedral taps around it, through one call site. Each case writes (|dual - stencil|, the most any tap's dual
// gradient differs from the point's, one more than the dual gradient's length where the walk kept every derivative and zero
// where it did not, |dual with its axes rotated - stencil|) into
// one texel of the results: the stencil is the four taps' values differenced as the bake's fallback differences them,
// and the spread tells a smooth neighbourhood from one a crease passes through, where the stencil blends two faces and
// the dual takes one. SdfFieldDeviceLawTests binds it as it binds sdf-field.comp.hlsl.
#define SDF_FIELD_DUAL
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/isa/sdf-world.interface.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-vm.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/isa/sdf-bake-taps.hlsli"

[[vk::binding(60, 3)]] StructuredBuffer<float4> fieldCases : register(t60, space3);
[[vk::binding(61, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> fieldResults : register(u61, space3);

static const uint FieldResultsWidth = 256u;
// The stencil's spacing: wide enough that single precision resolves the differences, narrow enough that the field's
// curvature over it stays under the law's tolerance.
static const float GradientSpacing = (1.0 / 1024.0);

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint first = (pushedIndex.index & 0xFFFFu);
    uint count = (pushedIndex.index >> 16u);

    if (id.x >= count) {
        return;
    }

    float4 probe = fieldCases[(first + id.x)];
    uint slot = asuint(probe.w);

    sdfProgramLayout = sdfLoadProgramLayout();

    float3 dual = 0.0;
    float3 stencil = 0.0;
    float spread = 0.0;
    bool covered = false;

    [loop]
    for (uint tap = 0u; tap <= SDF_BAKE_GRADIENT_TAPS; tap++) {
        float3 direction = sdfBakeTap(tap);
        SdfHit hit = mapCore(probe.xyz + (direction * GradientSpacing), SDF_INSTANCE_MASK_ALL, false);

        if (tap == 0u) {
            dual = hit.gradient;
            covered = sdfFieldDualCovered;
        } else {
            stencil += (direction * hit.distance);
            spread = max(spread, length(hit.gradient - dual));
        }
    }

    stencil *= (0.25 / GradientSpacing);
    fieldResults[uint2((slot % FieldResultsWidth), (slot / FieldResultsWidth))] =
        float4(length(dual - stencil), spread, (covered ? (1.0 + length(dual)) : 0.0), length(dual.yzx - stencil));
}
