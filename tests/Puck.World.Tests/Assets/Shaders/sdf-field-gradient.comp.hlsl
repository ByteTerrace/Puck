// The SDF gradient probe: walks the shipped interpreter (field/sdf-vm.hlsli) in forward mode (SDF_FIELD_DUAL, as the
// bake's gradient kernel does) over the program the host packed into sdfWords, at each case's point, and beside it takes
// the bake's tetrahedral stencil of the plain field there at two spacings, through the same call site. Each case writes
// (|dual - stencil|, |stencil - stencil at twice the spacing|, whether the walk kept every derivative, |dual with x and
// y exchanged - stencil|) into one texel of the results. SdfFieldDeviceLawTests binds it as it binds sdf-field.comp.hlsl.
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
    float3 near = 0.0;
    float3 far = 0.0;
    bool covered = false;

    [loop]
    for (uint tap = 0u; tap <= (2u * SDF_BAKE_GRADIENT_TAPS); tap++) {
        bool wide = (tap > SDF_BAKE_GRADIENT_TAPS);
        float3 direction = sdfBakeTap(wide ? (tap - SDF_BAKE_GRADIENT_TAPS) : tap);
        SdfHit hit = mapCore(probe.xyz + (direction * (wide ? (2.0 * GradientSpacing) : GradientSpacing)), SDF_INSTANCE_MASK_ALL, false);

        if (tap == 0u) {
            dual = hit.gradient;
            covered = sdfFieldDualCovered;
        } else if (wide) {
            far += (direction * hit.distance);
        } else {
            near += (direction * hit.distance);
        }
    }

    near *= (0.25 / GradientSpacing);
    far *= (0.125 / GradientSpacing);
    fieldResults[uint2((slot % FieldResultsWidth), (slot / FieldResultsWidth))] =
        float4(length(dual - near), length(near - far), (covered ? 1.0 : 0.0), length(dual.yxz - near));
}
