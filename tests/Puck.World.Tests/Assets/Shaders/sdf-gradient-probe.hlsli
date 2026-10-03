// The full dual walk supplies one independent basis derivative per leaf; its
// nonzero results count final contributors without reading the winner plan.
#define SDF_GRADIENT_LAW_REFERENCE
#define SDF_DYNAMIC_TRANSFORMS
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-vm.hlsli"

[[vk::binding(60, 3)]] StructuredBuffer<float4> fieldCases : register(t60, space3);
[[vk::binding(61, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> fieldResults : register(u61, space3);
struct GradientProbeIndex { [[vk::offset(0)]] uint index; };
[[vk::push_constant]] ConstantBuffer<GradientProbeIndex> fieldProbeIndex : register(b0, space4);

// Independent support inventory for the analytic counter; every other leaf uses four scalar taps.
bool gradientProbeHasAnalytic(uint shape) {
    return shape == SDF_SHAPE_SPHERE || shape == SDF_SHAPE_PLANE || shape == SDF_SHAPE_BOX
        || shape == SDF_SHAPE_SCREEN_SLAB || shape == SDF_SHAPE_TORUS || shape == SDF_SHAPE_CAPSULE
        || shape == SDF_SHAPE_CYLINDER || shape == SDF_SHAPE_SUPERELLIPSOID;
}

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint first = fieldProbeIndex.index & 0xFFFFu;
    uint count = fieldProbeIndex.index >> 16u;
    if (id.x >= count) return;
    float4 probe = fieldCases[first + id.x];
    uint slot = asuint(probe.w);
    uint2 output = uint2(slot % 256u, slot / 256u);
    sdfProgramLayout = sdfLoadProgramLayout();
    float3 samplePosition = probe.xyz;
#ifdef SDF_GRADIENT_PROBE_RAYS
    float4 eye = fieldCases[first + count];
    float4 lens = fieldCases[first + count + 1u];
    float3 direction = probe.xyz;
    float traveled = eye.w / dot(direction, lens.xyz);
    bool hit = false;
    [loop]
    for (uint step = 0u; step < 1024u && traveled < lens.w; step++) {
        samplePosition = eye.xyz + traveled * direction;
        float value = map(samplePosition).distance;
        if (value <= 0.001) { hit = true; break; }
        bool proven;
        float switchAt;
        traveled = sdfMarchAdvance(eye.xyz, direction, traveled, value, value, 0.001, lens.w, proven, switchAt);
    }
    if (!hit) { fieldResults[output] = float4(0.0, 0.0, -1.0, 0.0); return; }
#endif

    sdfDetailShadingActive = true;
    uint expected = 0u;
    sdfGradientMode = 3u;
    uint instructionCount = SDF_PROGRAM_INSTRUCTION_COUNT(sdfWords[0]);
    [loop]
    for (uint instruction = 0u; instruction < instructionCount; instruction++) {
        if (SDF_INSTRUCTION_OP(sdfWords[SDF_PROGRAM_HEADER_VECTORS + instruction]) != SDF_OP_SHAPE_BLEND) continue;
        sdfGradientReferenceShape = instruction;
        float3 weight;
        sdfMapGradientWalk(samplePosition, SDF_INSTANCE_MASK_ALL, weight);
        uint shape = SDF_INSTRUCTION_SHAPE(sdfWords[SDF_PROGRAM_HEADER_VECTORS + instruction]) & SDF_SHAPE_TYPE_MASK;
        if (weight.x != 0.0 && gradientProbeHasAnalytic(shape)) expected++;
    }

    sdfWorkShapes = 0u;
    sdfWorkGradients = 0u;
    sdfGradientMode = 0u;
    float3 referenceGradient;
    SdfHit reference = sdfMapGradientWalk(samplePosition, SDF_INSTANCE_MASK_ALL, referenceGradient);
    uint referenceShapes = sdfWorkShapes;
    sdfWorkShapes = 0u;
    sdfWorkGradients = 0u;
    float3 selectedGradient;
    SdfHit selected = mapGradCore(samplePosition, SDF_INSTANCE_MASK_ALL, selectedGradient);
    float error = max(abs(referenceGradient.x - selectedGradient.x), max(abs(referenceGradient.y - selectedGradient.y), abs(referenceGradient.z - selectedGradient.z)));
    if (asuint(selected.distance) != asuint(reference.distance) || selected.material != reference.material) error = 1e20;
#ifdef SDF_GRADIENT_PROBE_COUNTS
    uint selectedShapes = sdfWorkShapes, selectedGradients = sdfWorkGradients;
    SdfHit scalar = map(samplePosition);
    uint parts = sdfProgramLayout.partProgramOffset;
    // Counter fixtures contain either one shape or a nine-shape compiled scope.
    bool needsPart = instructionCount > 4u;
    if ((needsPart && (parts == 0u || (sdfWords[parts].x & 0x7FFFFFFFu) == 0u))
        || asuint(scalar.distance) != asuint(selected.distance) || scalar.material != selected.material || error > 0.003)
        referenceShapes = 0xFFFFFFFFu;
    fieldResults[output] = float4(float(selectedShapes), float(selectedGradients), float(expected), float(referenceShapes));
#else
    fieldResults[output] = float4(float(referenceShapes) - float(sdfWorkShapes), float(sdfWorkGradients), float(expected), error);
#endif
}
