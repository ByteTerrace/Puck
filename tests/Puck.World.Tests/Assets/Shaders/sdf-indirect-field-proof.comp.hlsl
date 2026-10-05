// One packed four-shape scene, evaluated through either the ordinary VM entry or the actual Views helper.
// The admission mode rebases only the counter address; it does not exercise cache proofs, visibility or resets.
#define SDF_VIEWS_PASS
#define SDF_DYNAMIC_TRANSFORMS
#define SDF_SCREEN_SOURCES
#define SDF_SEGMENT_TAPES
#define SDF_INSTANCE_MASKS
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/isa/sdf-indirect-layout.hlsli"
#define sdfIndirectReceiverProofWordOffset(tier) 0u
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-cache.hlsli"
#undef sdfIndirectReceiverProofWordOffset

[[vk::binding(60, 3)]] StructuredBuffer<float4> fieldCases : register(t60, space3);
[[vk::binding(61, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> fieldResults : register(u61, space3);
struct FieldProbeIndex { [[vk::offset(0)]] uint index; };
[[vk::push_constant]] ConstantBuffer<FieldProbeIndex> fieldIndex : register(b0, space4);

groupshared uint4 fieldAdmissions[64];
groupshared uint4 fieldWork[64];
groupshared uint4 fieldState[64];
groupshared uint4 fieldMaterials[64];

float2 fieldSplit(uint value) { return float2((float)(value & 65535u), (float)(value >> 16u)); }
void fieldAdd(uint word, uint value) {
    if (value != 0u) {
        uint ignored;
        InterlockedAdd(indirectCacheRW[word], value, ignored);
    }
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID, uint lane : SV_GroupIndex) {
    uint index = fieldIndex.index;
    float4 parameters = fieldCases[index + 1u];
    uint mode = (uint)parameters.x;
    if (mode == 0u) {
        if (all(id == 0u)) {
            [unroll] for (uint word = 0u; word < 17u; word++) { indirectCacheRW[word] = 0u; }
        }
        return;
    }
    if (mode == 4u) {
        if (all(id == 0u)) {
            [unroll] for (uint row = 0u; row < 8u; row++) {
                fieldResults[uint2(index, row)] = float4(fieldSplit(indirectCacheRW[row * 2u]), fieldSplit(indirectCacheRW[row * 2u + 1u]));
            }
            fieldResults[uint2(index, 8u)] = float4(fieldSplit(indirectCacheRW[16u]), 0.0, 0.0);
        }
        return;
    }

    sdfProgramLayout = sdfLoadProgramLayout();
    sdfShadowMaskWords[0] = 0u;
    sdfAmbientMaskWords[0] = 0u;
    uint invocation = id.y * (uint)parameters.y + id.x;
    uint4 admissions = 0u; // Requests, accepted, deferred, inconsistent admission results.
    uint4 work = 0u; // Field calls, actual shape evaluations, value/material errors, scope errors.
    uint4 materials = 0u;
    [loop] for (uint request = 0u; request < (uint)parameters.w; request++) {
        if (mode == 3u) {
            sdfIndirectReceiverDeferred = false;
            bool accepted = sdfIndirectAdmitReceiver();
            admissions.x++;
            admissions.y += accepted ? 1u : 0u;
            admissions.z += sdfIndirectReceiverDeferred ? 1u : 0u;
            admissions.w += accepted == sdfIndirectReceiverDeferred ? 1u : 0u;
            if (!accepted) { continue; }
        }
        [loop] for (uint query = 0u; query < (uint)parameters.z; query++) {
            // All positions and independent expected values are runtime buffer inputs. Adjacent lanes and calls
            // visit different positions; every 256-lane block visits the complete table once per query.
            uint point = (invocation * 17u + query * 13u + request * 37u) % (uint)fieldCases[0].x;
            uint pointWord = 1u + (uint)fieldCases[0].y + 2u * point;
            float4 sample = fieldCases[pointWord];
            int expectedMaterial = (int)fieldCases[pointWord + 1u].x;
            uint scope = (invocation + query) & 63u;
            bool tape = (scope & 1u) != 0u;
            bool participation = (scope & 2u) != 0u;
            bool shadow = (scope & 4u) != 0u;
            bool ambient = (scope & 8u) != 0u;
            bool shadowParticipation = (scope & 16u) != 0u;
            bool secondary = (scope & 32u) != 0u;
            sdfShadowParticipationActive = shadowParticipation;
            sdfSecondaryMarchActive = secondary;
            SdfHit hit;
            if (mode == 1u) {
                // Give the direct entry the same full-field policy the wrapper establishes, without calling it.
                sdfTapeActive = false;
                sdfIndirectParticipationActive = true;
                sdfShadowMaskActive = false;
                sdfAmbientMaskActive = false;
                hit = mapCore(sample.xyz, SDF_INSTANCE_MASK_ALL, true);
                work.w += sdfTapeActive || !sdfIndirectParticipationActive || sdfShadowMaskActive || sdfAmbientMaskActive ? 1u : 0u;
            } else {
                sdfTapeActive = tape;
                sdfIndirectParticipationActive = participation;
                sdfShadowMaskActive = shadow;
                sdfAmbientMaskActive = ambient;
                hit = sdfIndirectSample(sample.xyz, SDF_INSTANCE_MASK_ALL);
                work.w += sdfTapeActive != tape || sdfIndirectParticipationActive != participation ||
                    sdfShadowMaskActive != shadow || sdfAmbientMaskActive != ambient ? 1u : 0u;
            }
            work.w += sdfShadowParticipationActive != shadowParticipation || sdfSecondaryMarchActive != secondary ||
                sdfShadowMaskWords[0] != 0u || sdfAmbientMaskWords[0] != 0u ? 1u : 0u;
            work.x++;
            work.z += !isfinite(hit.distance) || abs(hit.distance - sample.w) > 0.000002 || hit.material != expectedMaterial ? 1u : 0u;
            if (hit.material >= 0 && hit.material < 4) { materials[(uint)hit.material]++; }
        }
    }
    work.y = sdfWorkShapes;
    fieldAdmissions[lane] = admissions;
    fieldWork[lane] = work;
    fieldState[lane] = uint4(sdfIndirectEvaluations, sdfWorkSteps, sdfIndirectLoads, 0u);
    fieldMaterials[lane] = materials;
    GroupMemoryBarrierWithGroupSync();
    if (lane == 0u) {
        uint4 totalAdmissions = 0u, totalWork = 0u, totalState = 0u, totalMaterials = 0u;
        [loop] for (uint member = 0u; member < 64u; member++) {
            totalAdmissions += fieldAdmissions[member];
            totalWork += fieldWork[member];
            totalState += fieldState[member];
            totalMaterials += fieldMaterials[member];
        }
        [unroll] for (uint channel = 0u; channel < 4u; channel++) {
            fieldAdd(1u + channel, totalAdmissions[channel]);
            fieldAdd(5u + channel, totalWork[channel]);
            fieldAdd(9u + channel, totalState[channel]);
            fieldAdd(13u + channel, totalMaterials[channel]);
        }
    }
}
