#define SDF_SEGMENT_TAPES
#define SDF_DYNAMIC_TRANSFORMS
#define SDF_TAPE_BUILD
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-vm.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/field/sdf-tape-build.hlsli"

[[vk::binding(60, 3)]] StructuredBuffer<float4> fieldCases : register(t60, space3);
[[vk::binding(61, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> fieldResults : register(u61, space3);
struct TapeProbeIndex { [[vk::offset(0)]] uint index; };
[[vk::push_constant]] ConstantBuffer<TapeProbeIndex> fieldProbeIndex : register(b0, space4);

// One probe builds its tile's tape, then walks each sample in six modes, the full walk and then the pruned walk per
// mode. Every field evaluation, the build's slabs included, runs through the loop's one interpreter call site.
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
    bool building = sdfTapeBuildBegin(slot, instanceMask, entry, farBound);
    bool walking = false;
    uint slab = 0u;
    uint tapeShapes = 0u;
    uint enabled = 0u;
    float traveled = entry;
    float saved = 0.0;
    uint misses = 0u;
    uint covered = 0u;
    uint sample = 0u;
    uint mode = 0u;
    uint pass = 0u;
    float3 samplePosition = origin;
    float next = traveled;
    float primaryDistance = SDF_FAR_DISTANCE;
    SdfHit full = (SdfHit)0;
    uint fullShapes = 0u;
    float fullWeight = 0.0;
    int fullOther = 0;
    [loop]
    for (;;) {
        if (!building && !walking) {
            tapeShapes = sdfWorkShapes;
            DeviceMemoryBarrier();
            sdfTapeBase = slot * sdfTapeStride();
            sdfTapeInstanceMask = instanceMask;
            enabled = sdfSegmentTapesRW[sdfTapeBase];
            walking = true;
            if (!(traveled <= farBound)) { break; }
        }
        float3 at = samplePosition;
        uint queryMask = instanceMask;
        bool track = true;
        if (building) {
            at = sdfTapeSlabCentre(slab, origin, direction, chord, entry, farBound);
            track = false;
        } else {
            if (mode == 0u && pass == 0u) {
                samplePosition = origin + traveled * direction;
                next = traveled;
                primaryDistance = SDF_FAR_DISTANCE;
                at = samplePosition;
            }
            queryMask = mode == 4u ? SDF_INSTANCE_MASK_ALL : instanceMask;
            sdfDetailShadingActive = mode == 1u;
            sdfSecondaryMarchActive = mode == 2u;
            sdfShadowParticipationActive = mode == 3u;
            sdfPrimaryOmitParts = mode == 5u;
            sdfTapeActive = (pass == 1u) && (enabled != 0u);
            sdfWorkShapes = 0u;
        }
        SdfHit hit = mapCore(at, queryMask, track);
        if (building) {
            sdfTapeSlabEnd();
            slab++;
            if (slab == SDF_TAPE_SLAB_COUNT) {
                sdfTapeBuildEnd();
                building = false;
            }
            continue;
        }
        if (pass == 0u) {
            full = hit;
            fullShapes = sdfWorkShapes;
            fullWeight = sdfMaterialBlendWeight;
            fullOther = sdfMaterialBlendOther;
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
        } else {
            SdfHit pruned = hit;
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
        pass++;
        if (pass < 2u) { continue; }
        pass = 0u;
        mode++;
        if (mode < modes) { continue; }
        mode = 0u;
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
        sample++;
        if (sample >= 1024u || !(traveled <= farBound)) { break; }
    }
    fieldResults[uint2(slot % 256u, slot / 256u)] = float4(saved, float(misses), float(covered), float(tapeShapes));
}
