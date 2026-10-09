#define SDF_INDIRECT_PASS
#define SDF_SCREEN_SOURCES
#define SDF_GROUP_SHADOW_GATHER
#define SDF_DYNAMIC_TRANSFORMS
// Each field case's production helpers are the calls of one procedure (traceProbeStep below), so the probe inlines
// the interpreter once whatever case it runs.
#define SDF_INDIRECT_PROCS_CUSTOM
#define SDF_INDIRECT_PROC_MARCH
#define SDF_INDIRECT_PROC_PLACE
#define SDF_INDIRECT_PROC_PARTITION
#define SDF_INDIRECT_PROC_LAUNCH
#define SDF_INDIRECT_PROC_SEGMENT
#define SDF_INDIRECT_PROC_KERNEL
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-cache.hlsli"

// Each case binds its program and asks the production helpers for one ray, placement, cell, launch or finite proof.
// Loops over field-evaluating helpers stay rolled: each unrolled call would inline another whole interpreter.
[[vk::binding(126, 3)]] StructuredBuffer<float4> traceCases : register(t126, space3);
[[vk::binding(127, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> traceResults : register(u127, space3);
struct TraceProbeIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<TraceProbeIndex> traceProbeIndex : register(b0, space4);

// The field cases: a ray (0), a placement (1), a cell's eight placements and partition (2), a launch (3), a segment (4),
// a launch whose packed handoff then proves a segment (7) and a stored partition's cell and proof buckets (10).
struct TraceProbeProc {
    uint index;
    uint mode;
    float4 position;
    float4 vector;
    float spacing;
    uint mask;
    uint corner;
    SdfIndirectPlacement corners[8];
    uint phase;
};
static TraceProbeProc traceProbeProc = (TraceProbeProc)0;

bool traceProbeUsesField(uint mode) {
    return mode == 0u || mode == 1u || mode == 2u || mode == 3u || mode == 4u || mode == 7u || mode == 10u;
}

float3 traceProbeCorner(uint mode, uint corner) {
    float3 position = traceProbeProc.position.xyz;
    float spacing = traceProbeProc.spacing;
    if (mode == 2u) {
        float3 offset = float3(corner & 1u, (corner >> 1u) & 1u, (corner >> 2u) & 1u) * spacing;
        return position + offset;
    }
    return position + float3(sdfIndirectCorner(corner)) * spacing;
}

// Stores a case 10 cell and reads back what its store touched.
void traceProbeStoredCell(SdfIndirectCell value) {
    uint index = traceProbeProc.index;
    const uint cellAddress = 1u, proofAddress = 8u;
    uint end = proofAddress + SdfIndirectProofsPerCell * SdfIndirectProofWords;
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
}

uint sdfIndirectKernelStep() {
    uint index = traceProbeProc.index;
    uint mode = traceProbeProc.mode;
    float4 position = traceProbeProc.position;
    float4 vector = traceProbeProc.vector;
    float spacing = traceProbeProc.spacing;
    uint phase = traceProbeProc.phase;
    if (phase == 0u) {
        if (mode == 0u) {
            traceProbeProc.phase = 1u;
            return sdfIndirectCall(sdfIndirectMarchBegin(position.xyz, vector.xyz, position.w, vector.w, traceProbeProc.mask, 0.0, 64u));
        }
        if (mode == 1u) {
            traceProbeProc.phase = 2u;
            return sdfIndirectCall(sdfIndirectPlaceBegin(position.xyz, spacing));
        }
        if (mode == 3u) {
            traceProbeProc.phase = 5u;
            return sdfIndirectCall(sdfIndirectLaunchBegin(position.xyz, vector.xyz, spacing, 8u));
        }
        if (mode == 4u) {
            traceProbeProc.phase = 6u;
            return sdfIndirectCall(sdfIndirectSegmentBegin(position.xyz, vector.xyz, 16u));
        }
        if (mode == 7u) {
            traceProbeProc.phase = 7u;
            return sdfIndirectCall(sdfIndirectLaunchBegin(position.xyz, vector.xyz, spacing, SdfIndirectLaunchSteps));
        }
        if (mode == 10u) {
            const uint proofAddress = 8u;
            uint end = proofAddress + SdfIndirectProofsPerCell * SdfIndirectProofWords;
            [loop] for (uint word = 0u; word <= end; word++) { indirectCacheRW[word] = 0x13572468u; }
            if (position.w == 0.0) {
                SdfIndirectCell value = (SdfIndirectCell)0;
                value.components = 0xffffffffu;
                traceProbeStoredCell(value);
                return SdfIndirectStepReturn;
            }
        }
        traceProbeProc.corner = 0u;
        traceProbeProc.phase = 3u;
        return sdfIndirectCall(sdfIndirectPlaceBegin(traceProbeCorner(mode, 0u), spacing));
    }
    if (phase == 1u) {
        SdfIndirectRay ray = sdfIndirectMarchProc.result;
        traceResults[uint2(index, 0u)] = float4((float)ray.kind, ray.distance, (float)ray.material, (float)sdfIndirectMarchProc.budget);
        traceResults[uint2(index, 1u)] = float4(ray.normal, 0.0);
        uint4 packed = sdfIndirectPackRay(ray, 165u, 1u);
        traceResults[uint2(index, 10u)] = float4(asfloat(packed.x), (float)packed.z, (float)packed.w, 0.0);
        traceResults[uint2(index, 11u)] = float4(packed.y == 0u ? 0.0 : sdfIndirectUnpackNormal(packed.y), 0.0);
        return SdfIndirectStepReturn;
    }
    if (phase == 2u) {
        SdfIndirectPlacement placement = sdfIndirectPlaceProc.result;
        traceResults[uint2(index, 0u)] = float4(placement.position, (float)placement.classification);
        return SdfIndirectStepReturn;
    }
    if (phase == 3u) {
        uint corner = traceProbeProc.corner;
        traceProbeProc.corners[corner] = sdfIndirectPlaceProc.result;
        if (mode == 2u) {
            traceResults[uint2(index, corner)] = float4(traceProbeProc.corners[corner].position, (float)traceProbeProc.corners[corner].classification);
        }
        traceProbeProc.corner = corner + 1u;
        if (traceProbeProc.corner < 8u) {
            return sdfIndirectCall(sdfIndirectPlaceBegin(traceProbeCorner(mode, traceProbeProc.corner), spacing));
        }
        traceProbeProc.phase = 4u;
        return sdfIndirectCall(sdfIndirectPartitionBegin(traceProbeProc.corners, spacing));
    }
    if (phase == 4u) {
        SdfIndirectCell cell = sdfIndirectPartitionProc.result;
        if (mode == 2u) {
            traceResults[uint2(index, 8u)] = float4((float)(cell.components & 65535u), (float)(cell.components >> 16u), cell.offset, 0.0);
            traceResults[uint2(index, 9u)] = float4(cell.normal, 0.0);
        } else {
            traceProbeStoredCell(cell);
        }
        return SdfIndirectStepReturn;
    }
    if (phase == 5u) {
        traceResults[uint2(index, 0u)] = float4(sdfIndirectLaunchProc.position, sdfIndirectLaunchProc.launched ? 1.0 : 0.0);
        traceResults[uint2(index, 1u)] = float4(sdfIndirectLaunchProc.clearance, (float)sdfIndirectLaunchProc.budget, 0.0, 0.0);
        return SdfIndirectStepReturn;
    }
    if (phase == 6u) {
        traceResults[uint2(index, 0u)] = float4(sdfIndirectSegmentProc.blockedPoint, sdfIndirectSegmentProc.clear ? 1.0 : 0.0);
        return SdfIndirectStepReturn;
    }
    if (phase == 7u) {
        float3 launched = sdfIndirectLaunchProc.position;
        float clearance = sdfIndirectLaunchProc.clearance;
        traceResults[uint2(index, 0u)] = float4(launched, sdfIndirectLaunchProc.launched ? 1.0 : 0.0);
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
        if (clearance > 0.0) {
            traceProbeProc.phase = 8u;
            return sdfIndirectCall(sdfIndirectSegmentBegin(reconstructed,
                reconstructed + sdfIndirectUnpackNormal(packed.y) * (clearance * 0.25), SdfIndirectSegmentSteps));
        }
        traceResults[uint2(index, 4u)] = float4(0.0, 0.0, 0.0, 0.0);
        return SdfIndirectStepReturn;
    }
    traceResults[uint2(index, 4u)] = float4(0.0, 0.0, 0.0, sdfIndirectSegmentProc.clear ? 1.0 : 0.0);
    return SdfIndirectStepReturn;
}
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-procedures.hlsli"

// A receiver's read of a published canonical record, exactly as the shared proof takes it: a readable earlier
// publication of its key whose anchor ball its certified ball reaches.
bool fixtureReuseProof(uint proof, uint key, uint frame, float3 position, float certifiedClearance, float spacing, out uint mask) {
    uint cachedMask;
    float3 anchor;
    float clearance;
    mask = 0u;
    if (!sdfIndirectCanonicalProof(proof, key, frame, cachedMask, anchor, clearance)
        || !sdfIndirectProofReusable(position, certifiedClearance, anchor, clearance, spacing)) { return false; }
    mask = cachedMask;
    return true;
}

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
    if (traceProbeUsesField(mode)) {
        traceProbeProc.index = index;
        traceProbeProc.mode = mode;
        traceProbeProc.position = position;
        traceProbeProc.vector = vector;
        traceProbeProc.spacing = spacing;
        traceProbeProc.mask = mask;
        traceProbeProc.phase = 0u;
        sdfIndirectRun(SdfIndirectProcKernel);
    } else if (mode == 5u) {
        bool supported = sdfIndirectEndpointSupports(position.xyz, float3(1.0, 0.0, 0.0), vector.xyz);
        traceResults[uint2(index, 0u)] = float4(0.0, 0.0, 0.0, supported ? 1.0 : 0.0);
    } else if (mode == 6u) {
        uint packed = sdfIndirectPackNormal(vector.xyz);
        traceResults[uint2(index, 0u)] = float4(packed == 0u ? 0.0 : sdfIndirectUnpackNormal(packed), packed == 0u ? 0.0 : 1.0);
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
        bool earlier = fixtureReuseProof(proofAddress, key, publishedFrame + 1u, position.xyz, 0.0, spacing, earlierMask);
        bool same = fixtureReuseProof(proofAddress, key, publishedFrame, position.xyz, 0.0, spacing, sameMask);
        bool future = fixtureReuseProof(proofAddress, key, publishedFrame - 1u, position.xyz, 0.0, spacing, futureMask);
        bool wrongKey = fixtureReuseProof(proofAddress, key ^ 1u, publishedFrame + 1u, position.xyz, 0.0, spacing, wrongKeyMask);
        indirectCacheRW[proofAddress + 6u] = 0u;
        DeviceMemoryBarrier();
        bool empty = fixtureReuseProof(proofAddress, key, publishedFrame + 1u, position.xyz, 0.0, spacing, emptyMask);
        traceResults[uint2(index, 0u)] = float4(earlier ? 1.0 : 0.0, same ? 1.0 : 0.0, future ? 1.0 : 0.0, empty ? 1.0 : 0.0);
        traceResults[uint2(index, 1u)] = float4((float)earlierMask, (float)(sameMask | futureMask | wrongKeyMask | emptyMask), wrongKey ? 1.0 : 0.0, 0.0);
    }
}
