// Exercise the unchanged production admission loop against one small counter allocation. Rebasing its address
// deliberately excludes the cache layout from this witness; no field, proof, launch or shading routine executes.
#define SDF_RECEIVER_PASS
#define SDF_DYNAMIC_TRANSFORMS
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/isa/sdf-indirect-layout.hlsli"
#define sdfIndirectReceiverProofWordOffset(tier) 0u
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-cache.hlsli"
#undef sdfIndirectReceiverProofWordOffset

[[vk::binding(126, 3)]] StructuredBuffer<float4> admissionCases : register(t126, space3);
[[vk::binding(127, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> admissionResults : register(u127, space3);
struct AdmissionProbeIndex { [[vk::offset(0)]] uint index; };
[[vk::push_constant]] ConstantBuffer<AdmissionProbeIndex> admissionIndex : register(b0, space4);

groupshared uint4 admissionCounts[64];
groupshared uint2 admissionWork[64];

void admissionAdd(uint word, uint count) {
    if (count != 0u) {
        uint ignored;
        InterlockedAdd(indirectCacheRW[word], count, ignored);
    }
}
float2 admissionSplit(uint value) { return float2((float)(value & 65535u), (float)(value >> 16u)); }

// Match the production Views workgroup. The harness orders whole dispatches; a group barrier never stands in
// for device-wide completion. Each request phase can cover one actual 960 by 1080 viewport.
[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID, uint lane : SV_GroupIndex) {
    uint index = admissionIndex.index;
    float4 parameters = admissionCases[index];
    uint mode = (uint)parameters.x;
    if (mode == 0u) {
        if (all(id == 0u)) {
            indirectCacheRW[0u] = (uint)parameters.y | ((uint)parameters.z << 16u);
            [unroll] for (uint word = 1u; word < 7u; word++) { indirectCacheRW[word] = 0u; }
        }
        return;
    }
    if (mode == 2u) {
        if (all(id == 0u)) {
            admissionResults[uint2(index, 0u)] = float4(admissionSplit(indirectCacheRW[0u]), admissionSplit(indirectCacheRW[1u]));
            admissionResults[uint2(index, 1u)] = float4(admissionSplit(indirectCacheRW[2u]), admissionSplit(indirectCacheRW[3u]));
            admissionResults[uint2(index, 2u)] = float4(admissionSplit(indirectCacheRW[4u]), admissionSplit(indirectCacheRW[5u]));
            admissionResults[uint2(index, 3u)] = float4(admissionSplit(indirectCacheRW[6u]), 0.0, 0.0);
        }
        return;
    }

    uint4 counts = 0u; // Accepted, deferred, requests, inconsistent return/flag pairs.
    uint beforeQueries = sdfIndirectEvaluations;
    uint beforeLoads = sdfIndirectLoads;
    [loop] for (uint request = 0u; request < (uint)parameters.w; request++) {
        sdfIndirectReceiverDeferred = false;
        bool accepted = sdfIndirectAdmitReceiver();
        bool deferred = sdfIndirectReceiverDeferred;
        counts.x += accepted ? 1u : 0u;
        counts.y += deferred ? 1u : 0u;
        counts.z++;
        counts.w += accepted == deferred ? 1u : 0u;
    }
    admissionCounts[lane] = counts;
    admissionWork[lane] = uint2(sdfIndirectEvaluations - beforeQueries, sdfIndirectLoads - beforeLoads);
    GroupMemoryBarrierWithGroupSync();
    if (lane == 0u) {
        uint4 total = 0u;
        uint2 work = 0u;
        [loop] for (uint member = 0u; member < 64u; member++) {
            total += admissionCounts[member];
            work += admissionWork[member];
        }
        admissionAdd(1u, total.x);
        admissionAdd(2u, total.y);
        admissionAdd(3u, total.z);
        admissionAdd(4u, total.w);
        admissionAdd(5u, work.x);
        admissionAdd(6u, work.y);
    }
}
