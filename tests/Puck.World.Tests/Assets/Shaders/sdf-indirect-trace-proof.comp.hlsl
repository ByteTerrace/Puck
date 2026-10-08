#define SDF_INDIRECT_PASS
#define SDF_SCREEN_SOURCES
#define SDF_GROUP_SHADOW_GATHER
#define SDF_DYNAMIC_TRANSFORMS
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-cache.hlsli"

// Each case binds its program and asks the production helpers for one ray, placement, cell, launch or finite proof.
// Loops over field-evaluating helpers stay rolled: each unrolled call would inline another whole interpreter.
[[vk::binding(126, 3)]] StructuredBuffer<float4> traceCases : register(t126, space3);
[[vk::binding(127, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> traceResults : register(u127, space3);
struct TraceProbeIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<TraceProbeIndex> traceProbeIndex : register(b0, space4);

[numthreads(64, 1, 1)]
void CSMain(uint lane : SV_GroupIndex) {
    uint index = traceProbeIndex.index;
    float4 position = traceCases[3u * index];
    float4 vector = traceCases[3u * index + 1u];
    float4 parameters = traceCases[3u * index + 2u];
    uint mode = (uint)parameters.x;
    float spacing = parameters.y;
    sdfProgramLayout = sdfLoadProgramLayout();
    uint mask = SDF_INSTANCE_MASK_ALL;
    if (mode == 0u) { mask = sdfIndirectGather(position.xyz, position.w, lane); }
    if (lane != 0u) { return; }
    [unroll] for (uint row = 0u; row < 12u; row++) { traceResults[uint2(index, row)] = 0.0; }
    if (mode == 0u) {
        uint budget = 64u;
        SdfIndirectRay ray = sdfIndirectMarch(position.xyz, vector.xyz, position.w, vector.w, mask, 0.0, budget);
        traceResults[uint2(index, 0u)] = float4((float)ray.kind, ray.distance, (float)ray.material, (float)budget);
        traceResults[uint2(index, 1u)] = float4(ray.normal, 0.0);
        uint4 packed = sdfIndirectPackRay(ray, 165u, 1u);
        traceResults[uint2(index, 10u)] = float4(asfloat(packed.x), (float)packed.z, (float)packed.w, 0.0);
        traceResults[uint2(index, 11u)] = float4(packed.y == 0u ? 0.0 : sdfIndirectUnpackNormal(packed.y), 0.0);
    } else if (mode == 1u) {
        SdfIndirectPlacement placement = sdfIndirectPlace(position.xyz, spacing);
        traceResults[uint2(index, 0u)] = float4(placement.position, (float)placement.classification);
    } else if (mode == 2u) {
        SdfIndirectPlacement corners[8];
        [loop] for (uint corner = 0u; corner < 8u; corner++) {
            float3 offset = float3(corner & 1u, (corner >> 1u) & 1u, (corner >> 2u) & 1u) * spacing;
            corners[corner] = sdfIndirectPlace(position.xyz + offset, spacing);
            traceResults[uint2(index, corner)] = float4(corners[corner].position, (float)corners[corner].classification);
        }
        SdfIndirectCell cell = sdfIndirectPartition(corners, spacing);
        traceResults[uint2(index, 8u)] = float4((float)(cell.components & 65535u), (float)(cell.components >> 16u), cell.offset, 0.0);
        traceResults[uint2(index, 9u)] = float4(cell.normal, 0.0);
    } else if (mode == 3u) {
        uint budget = 8u;
        float3 launched;
        float clearance;
        bool certified = sdfIndirectLaunch(position.xyz, vector.xyz, spacing, budget, launched, clearance);
        traceResults[uint2(index, 0u)] = float4(launched, certified ? 1.0 : 0.0);
        traceResults[uint2(index, 1u)] = float4(clearance, (float)budget, 0.0, 0.0);
    } else if (mode == 4u) {
        uint budget = 16u;
        float3 blocked;
        bool clear = sdfIndirectSegment(position.xyz, vector.xyz, budget, blocked);
        traceResults[uint2(index, 0u)] = float4(blocked, clear ? 1.0 : 0.0);
    } else if (mode == 5u) {
        bool supported = sdfIndirectEndpointSupports(position.xyz, float3(1.0, 0.0, 0.0), vector.xyz);
        traceResults[uint2(index, 0u)] = float4(0.0, 0.0, 0.0, supported ? 1.0 : 0.0);
    } else if (mode == 6u) {
        uint packed = sdfIndirectPackNormal(vector.xyz);
        traceResults[uint2(index, 0u)] = float4(packed == 0u ? 0.0 : sdfIndirectUnpackNormal(packed), packed == 0u ? 0.0 : 1.0);
    } else if (mode == 7u) {
        uint budget = SdfIndirectLaunchSteps;
        float3 launched;
        float clearance;
        bool success = sdfIndirectLaunch(position.xyz, vector.xyz, spacing, budget, launched, clearance);
        traceResults[uint2(index, 0u)] = float4(launched, success ? 1.0 : 0.0);
        float originalClearance = clearance;
        SdfIndirectRay ray = (SdfIndirectRay)0;
        ray.kind = SdfIndirectKindHit;
        ray.normal = vector.xyz;
        ray.launchHeight = sdfIndirectQuantizeLaunch(position.xyz, vector.xyz, spacing, launched, clearance);
        uint4 packed = sdfIndirectPackRay(ray, 165u, 1u);
        float3 reconstructed = sdfIndirectLaunchPosition(position.xyz, packed.y, packed.w, spacing);
        traceResults[uint2(index, 1u)] = float4(launched, clearance);
        traceResults[uint2(index, 2u)] = float4(reconstructed, sdfIndirectLaunchHeight(packed.w, spacing));
        traceResults[uint2(index, 3u)] = float4(originalClearance, (float)ray.launchHeight, (float)(packed.w & 65535u), (float)(packed.w >> 16u));
        uint proofBudget = SdfIndirectSegmentSteps;
        float3 blocked;
        bool proved = clearance > 0.0 && sdfIndirectSegment(reconstructed,
            reconstructed + sdfIndirectUnpackNormal(packed.y) * (clearance * 0.25), proofBudget, blocked);
        traceResults[uint2(index, 4u)] = float4(0.0, 0.0, 0.0, proved ? 1.0 : 0.0);
    } else if (mode == 8u) {
        int3 cell = int3(2, -1, 3);
        uint minimum = 0xffffffffu, maximum = 0u, coverage = 0u, errors = 0u;
        [loop] for (uint z = 0u; z < SdfIndirectProofSlotsPerAxis; z++) {
            [loop] for (uint y = 0u; y < SdfIndirectProofSlotsPerAxis; y++) {
                [loop] for (uint x = 0u; x < SdfIndirectProofSlotsPerAxis; x++) {
                    float3 anchor = (float3(cell) + (float3(x, y, z) + 0.5) / (float)SdfIndirectProofSlotsPerAxis) * spacing;
                    uint key;
                    uint entry = sdfIndirectProofEntry(7u, cell, sdfIndirectProofSlot(anchor, spacing), 1u, key);
                    minimum = min(minimum, entry);
                    maximum = max(maximum, entry);
                    coverage |= 1u << (entry % SdfIndirectProofsPerCell);
                    uint coordinates = x | (y << SdfIndirectProofSlotBits) | (z << (2u * SdfIndirectProofSlotBits));
                    uint exactKey = (1u << (SdfIndirectProofKeyLevelBits + 3u * SdfIndirectProofSlotBits))
                        | (coordinates << SdfIndirectProofKeyLevelBits) | 1u;
                    if (key != exactKey) { errors++; }
                }
            }
        }
        traceResults[uint2(index, 0u)] = float4((float)minimum, (float)maximum, (float)coverage, (float)errors);
    } else if (mode == 9u) {
        bool reusable = sdfIndirectProofReusable(position.xyz, vector.w, vector.xyz, position.w, spacing);
        traceResults[uint2(index, 0u)] = float4(length(position.xyz - vector.xyz), position.w + vector.w, 0.0, reusable ? 1.0 : 0.0);
    } else if (mode == 10u) {
        const uint cellAddress = 1u, proofAddress = 8u;
        uint end = proofAddress + SdfIndirectProofsPerCell * SdfIndirectProofWords;
        [loop] for (uint word = 0u; word <= end; word++) { indirectCacheRW[word] = 0x13572468u; }
        SdfIndirectCell value = (SdfIndirectCell)0;
        if (position.w == 0.0) {
            value.components = 0xffffffffu;
        } else {
            SdfIndirectPlacement corners[8];
            [loop] for (uint corner = 0u; corner < 8u; corner++) {
                corners[corner] = sdfIndirectPlace(position.xyz + float3(sdfIndirectCorner(corner)) * spacing, spacing);
            }
            value = sdfIndirectPartition(corners, spacing);
        }
        sdfIndirectStoreCell(cellAddress, proofAddress, value);
        DeviceMemoryBarrier();
        uint surviving = 0u, changed = 0u, cleared = 0u;
        [unroll] for (uint bucket = 0u; bucket < SdfIndirectProofsPerCell; bucket++) {
            uint occupied = 0u;
            [unroll] for (uint word = 0u; word < SdfIndirectProofWords; word++) {
                occupied += indirectCacheRW[proofAddress + bucket * SdfIndirectProofWords + word] == 0u ? 0u : 1u;
            }
            surviving += occupied;
            if (occupied == 0u) { cleared |= 1u << bucket; }
        }
        [loop] for (uint word = 0u; word <= end; word++) {
            bool stored = word >= cellAddress && word < cellAddress + SdfIndirectCellWords;
            bool owned = word >= proofAddress && word < end;
            if (!stored && !owned && indirectCacheRW[word] != 0x13572468u) { changed++; }
        }
        bool intact = indirectCacheRW[cellAddress] == value.components
            && indirectCacheRW[cellAddress + 1u] == sdfIndirectPackNormal(value.normal)
            && indirectCacheRW[cellAddress + 2u] == asuint(value.offset);
        traceResults[uint2(index, 0u)] = float4((float)surviving, (float)changed, intact ? 0.0 : 1.0, (float)cleared);
    } else {
        const uint proofAddress = 8u, publishedFrame = 17u;
        uint key;
        int3 cell = int3(floor(position.xyz / spacing));
        sdfIndirectProofEntry(0u, cell, sdfIndirectProofSlot(position.xyz, spacing), 1u, key);
        indirectCacheRW[proofAddress] = asuint(position.x);
        indirectCacheRW[proofAddress + 1u] = asuint(position.y);
        indirectCacheRW[proofAddress + 2u] = asuint(position.z);
        indirectCacheRW[proofAddress + 3u] = asuint(0.03);
        indirectCacheRW[proofAddress + 4u] = 165u;
        indirectCacheRW[proofAddress + 5u] = key;
        indirectCacheRW[proofAddress + 6u] = publishedFrame;
        DeviceMemoryBarrier();
        uint earlierMask, sameMask, futureMask, wrongKeyMask, emptyMask;
        bool earlier = sdfIndirectReuseProof(proofAddress, key, publishedFrame + 1u, position.xyz, 0.0, spacing, earlierMask);
        bool same = sdfIndirectReuseProof(proofAddress, key, publishedFrame, position.xyz, 0.0, spacing, sameMask);
        bool future = sdfIndirectReuseProof(proofAddress, key, publishedFrame - 1u, position.xyz, 0.0, spacing, futureMask);
        bool wrongKey = sdfIndirectReuseProof(proofAddress, key ^ 1u, publishedFrame + 1u, position.xyz, 0.0, spacing, wrongKeyMask);
        indirectCacheRW[proofAddress + 6u] = 0u;
        DeviceMemoryBarrier();
        bool empty = sdfIndirectReuseProof(proofAddress, key, publishedFrame + 1u, position.xyz, 0.0, spacing, emptyMask);
        traceResults[uint2(index, 0u)] = float4(earlier ? 1.0 : 0.0, same ? 1.0 : 0.0, future ? 1.0 : 0.0, empty ? 1.0 : 0.0);
        traceResults[uint2(index, 1u)] = float4((float)earlierMask, (float)(sameMask | futureMask | wrongKeyMask | emptyMask), wrongKey ? 1.0 : 0.0, 0.0);
    }
}
