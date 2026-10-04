#define SDF_SEGMENT_TAPES
#define SDF_DYNAMIC_TRANSFORMS
#define SDF_TAPE_BUILD
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-vm.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-tape-build.hlsli"

[[vk::binding(60, 3)]] StructuredBuffer<float4> fieldCases : register(t60, space3);
[[vk::binding(61, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> fieldResults : register(u61, space3);
struct TapeProbeIndex { [[vk::offset(0)]] uint index; };
[[vk::push_constant]] ConstantBuffer<TapeProbeIndex> fieldProbeIndex : register(b0, space4);

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint first = fieldProbeIndex.index & 0xFFFFu;
    uint count = fieldProbeIndex.index >> 16u;
    if (id.x >= count) return;
    float4 probe = fieldCases[first + id.x];
    uint slot = asuint(probe.w);
    sdfProgramLayout = sdfLoadProgramLayout();
    uint instanceMask = SDF_INSTANCE_MASK_ALL;
    uint modes = 6u;
#ifdef SDF_TAPE_PROBE_SPARSE_MASKS
    uint maskWords = sdfInstanceMaskWordCount(sdfProgramLayout.instanceCount);
    instanceMask = slot * (maskWords + ((maskWords + 31u) >> 5u));
    float4 expected = fieldCases[first + count + id.x];
#endif
    float3 direction = float3(0.0, 0.0, 1.0);
    float3 origin = probe.xyz - 2.0 * direction;
    float entry = 1.0;
    float farBound = 3.0;
    float chord = 0.01;
#ifdef SDF_TAPE_PROBE_RAYS
    float4 eye = fieldCases[first + count];
    float4 lens = fieldCases[first + count + 1u];
    origin = eye.xyz;
    direction = probe.xyz;
    entry = eye.w / dot(direction, lens.xyz);
    farBound = lens.w;
    chord = fieldCases[first + count + 2u].x;
#endif
    sdfWorkShapes = 0u;
    sdfBuildTileTape(slot, instanceMask, origin, direction, chord, entry, farBound);
    uint tapeShapes = sdfWorkShapes;
    DeviceMemoryBarrier();
    sdfTapeBase = slot * sdfTapeStride();
    sdfTapeInstanceMask = instanceMask;
    uint enabled = sdfSegmentTapesRW[sdfTapeBase];
    float traveled = entry;
    float saved = 0.0;
    uint misses = 0u;
    uint covered = 0u;
    [loop]
    for (uint sample = 0u; sample < 1024u && traveled <= farBound; sample++) {
        float3 samplePosition = origin + traveled * direction;
        float next = traveled;
        float primaryDistance = SDF_FAR_DISTANCE;
        [loop]
        for (uint mode = 0u; mode < modes; mode++) {
        uint queryMask = mode == 4u ? SDF_INSTANCE_MASK_ALL : instanceMask;
        sdfDetailShadingActive = mode == 1u;
        sdfSecondaryMarchActive = mode == 2u;
        sdfShadowParticipationActive = mode == 3u;
        sdfPrimaryOmitParts = mode == 5u;
        sdfTapeActive = false;
        sdfWorkShapes = 0u;
        SdfHit full = mapCore(samplePosition, queryMask, true);
        uint fullShapes = sdfWorkShapes;
        float fullWeight = sdfMaterialBlendWeight;
        int fullOther = sdfMaterialBlendOther;
#ifdef SDF_TAPE_PROBE_SPARSE_MASKS
        // Identity also holds the masked full walk to the authored scene: both
        // compared paths ignoring visibility cannot make the equality law pass.
        int expectedInstance = (int)(mode == 4u ? expected.z : expected.x);
        int expectedMaterial = (int)(mode == 4u ? expected.w : expected.y);
        if (full.instanceIndex != expectedInstance || full.material != expectedMaterial) misses++;
#endif
        bool proven;
        float switchAt;
        if (mode == 0u) {
            primaryDistance = full.distance;
            next = sdfMarchAdvance(origin, direction, traveled, full.distance, full.distance, 0.001, farBound, proven, switchAt);
        }
        sdfTapeActive = enabled != 0u;
        sdfWorkShapes = 0u;
        SdfHit pruned = mapCore(samplePosition, queryMask, true);
        saved += float(fullShapes) - float(sdfWorkShapes);
        if (sdfTapeSampleActive) covered++;
        if (sdfTapeSampleActive && (mode == 2u || mode == 3u || mode == 5u ||
            (mode == 4u && queryMask != instanceMask) || (mode == 1u && !sdfProgramLayout.noDetailShapes))) misses++;
        // The other-material slot has no consumer at zero weight; a hard winner
        // clears the weight while leaving that unused slot unspecified.
        if (asuint(full.distance) != asuint(pruned.distance) || full.material != pruned.material ||
            full.instanceIndex != pruned.instanceIndex || full.frameSlot != pruned.frameSlot ||
            any(asuint(full.lanes) != asuint(pruned.lanes)) || asuint(fullWeight) != asuint(sdfMaterialBlendWeight) ||
            (fullWeight != 0.0 && fullOther != sdfMaterialBlendOther)) misses++;
        }
        sdfDetailShadingActive = false;
        sdfSecondaryMarchActive = false;
        sdfShadowParticipationActive = false;
        sdfPrimaryOmitParts = false;
#ifdef SDF_TAPE_PROBE_RAYS
        if (primaryDistance <= 0.001) break;
        traveled = next;
#else
        if (sample == 64u) break;
        traveled = lerp(entry, farBound, float(sample + 1u) / 64.0);
#endif
    }
    fieldResults[uint2(slot % 256u, slot / 256u)] = float4(saved, float(misses), float(covered), float(tapeShapes));
}
