#define SDF_INDIRECT_PASS
#define SDF_SCREEN_SOURCES
#define SDF_GROUP_SHADOW_GATHER
#define SDF_DYNAMIC_TRANSFORMS
// The masked and full marches and the independent interval reference are one procedure, so the probe inlines the
// interpreter once: the reference's samples are plain field queries under its own mask flag.
#define SDF_INDIRECT_PROCS_CUSTOM
#define SDF_INDIRECT_PROC_MARCH
#define SDF_INDIRECT_PROC_KERNEL
#define SDF_INDIRECT_PLAIN_QUERIES
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-field.hlsli"
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-march.hlsli"

// The host binds a program for each group. Rows carry origin/reach and direction/far distance; each output column
// holds masked/full marches, interval failures, shape-query differences and the fixtures' mutation witnesses.
[[vk::binding(126, 3)]] StructuredBuffer<float4> gatherCases : register(t126, space3);
[[vk::binding(127, 3)]] [[vk::image_format("rgba32f")]] RWTexture2D<float4> gatherResults : register(u127, space3);
struct GatherProbeIndex {
    [[vk::offset(0)]] uint index;
};
[[vk::push_constant]] ConstantBuffer<GatherProbeIndex> gatherProbeIndex : register(b0, space4);

// The masked march, the full march, then the reference intervals along the ray: each step samples the field under
// the masked flag at its travel, advances by the production advance, and samples its interval fifteen times on the full
// field, counting inside samples (x), masked overruns past the reach (y) and steps (z), with the final travel (w).
struct GatherProbeProc {
    float3 origin;
    float3 direction;
    float reach;
    float farDistance;
    uint mask;
    SdfIndirectRay masked;
    uint maskedBudget;
    SdfIndirectRay full;
    uint fullBudget;
    float4 intervals;
    float traveled;
    uint step;
    float advance;
    uint sample;
    uint phase;
};
static GatherProbeProc gatherProbeProc = (GatherProbeProc)0;

uint gatherProbeBegin(float3 origin, float3 direction, float reach, float farDistance, uint mask) {
    gatherProbeProc.origin = origin;
    gatherProbeProc.direction = direction;
    gatherProbeProc.reach = reach;
    gatherProbeProc.farDistance = farDistance;
    gatherProbeProc.mask = mask;
    gatherProbeProc.phase = 0u;
    return SdfIndirectProcKernel;
}

uint sdfIndirectKernelStep() {
    float3 origin = gatherProbeProc.origin;
    float3 direction = gatherProbeProc.direction;
    float reach = gatherProbeProc.reach;
    float farDistance = gatherProbeProc.farDistance;
    uint mask = gatherProbeProc.mask;
    if (gatherProbeProc.phase == 0u) {
        gatherProbeProc.phase = 1u;
        return sdfIndirectCall(sdfIndirectMarchBegin(origin, direction, reach, farDistance, mask, 0.0, 64u));
    }
    if (gatherProbeProc.phase == 1u) {
        gatherProbeProc.masked = sdfIndirectMarchProc.result;
        gatherProbeProc.maskedBudget = sdfIndirectMarchProc.budget;
        gatherProbeProc.phase = 2u;
        return sdfIndirectCall(sdfIndirectMarchBegin(origin, direction, reach, farDistance, SDF_INSTANCE_MASK_ALL, 0.0, 64u));
    }
    if (gatherProbeProc.phase == 2u) {
        gatherProbeProc.full = sdfIndirectMarchProc.result;
        gatherProbeProc.fullBudget = sdfIndirectMarchProc.budget;
        gatherProbeProc.intervals = 0.0;
        gatherProbeProc.traveled = 0.0;
        gatherProbeProc.step = 0u;
    } else if (gatherProbeProc.phase == 3u) {
        // The step's sample under the masked flag.
        float clearance = sdfMapBallClearance(sdfIndirectReply.distance);
        sdfShadowMaskActive = false;
        bool masked = sdfIndirectMasked(gatherProbeProc.traveled, reach, mask);
        bool stop = clearance <= 0.00001;
        if (!stop) {
            gatherProbeProc.advance = sdfIndirectAdvance(clearance, gatherProbeProc.traveled, reach, farDistance, mask);
            stop = gatherProbeProc.advance <= 0.0;
        }
        if (stop) {
            gatherProbeProc.intervals.w = gatherProbeProc.traveled;
            return SdfIndirectStepReturn;
        }
        if (masked && gatherProbeProc.traveled + gatherProbeProc.advance > reach + 0.000001) { gatherProbeProc.intervals.y += 1.0; }
        gatherProbeProc.sample = 1u;
    } else {
        // One of the step's interval samples on the full field.
        if (sdfIndirectReply.distance < -0.00001) { gatherProbeProc.intervals.x += 1.0; }
        gatherProbeProc.sample++;
    }
    if (gatherProbeProc.phase >= 3u) {
        if (gatherProbeProc.sample < 16u) {
            float at = gatherProbeProc.traveled + gatherProbeProc.advance * ((float)gatherProbeProc.sample / 16.0);
            gatherProbeProc.phase = 4u;
            return sdfIndirectAskPlain(origin + direction * at, SDF_INSTANCE_MASK_ALL);
        }
        gatherProbeProc.intervals.z += 1.0;
        gatherProbeProc.traveled += gatherProbeProc.advance;
        gatherProbeProc.step++;
    }
    if (gatherProbeProc.step < 64u && gatherProbeProc.traveled < farDistance) {
        sdfShadowMaskActive = sdfIndirectMasked(gatherProbeProc.traveled, reach, mask);
        gatherProbeProc.phase = 3u;
        return sdfIndirectAskPlain(origin + direction * gatherProbeProc.traveled, SDF_INSTANCE_MASK_ALL);
    }
    gatherProbeProc.intervals.w = gatherProbeProc.traveled;
    return SdfIndirectStepReturn;
}
#include "../../../../src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-procedures.hlsli"
void gatherProbeQuery(SdfInstanceGridHeader grid, SdfGridQuery query, uint lane, uint lanes) {
    for (uint word = lane; word < SDF_SHADOW_MASK_WORDS; word += SDF_GROUP_SHADOW_LANES) {
        sdfAmbientMaskWords[word] = 0u;
    }
    GroupMemoryBarrierWithGroupSync();
    if (lane < lanes) {
        SdfGridWalk walk = sdfGridWalkBegin(grid, query, lane, lanes);
        uint index;
        uint offset = sdfInstanceDirectoryOffset();
        [loop]
        while (sdfGridWalkNext(grid, query, walk, index)) {
            if (sdfGridQueryContains(query, sdfInstanceBoundAt(offset, index))) {
                InterlockedOr(sdfAmbientMaskWords[index >> 5u], 1u << (index & 31u));
            }
        }
    }
    GroupMemoryBarrierWithGroupSync();
}

float gatherProbeQueryDifferences(SdfGridQuery query) {
    uint count = sdfInstanceCountClamped(), offset = sdfInstanceDirectoryOffset();
    float differences = 0.0;
    [loop]
    for (uint instance = 0u; instance < count; instance++) {
        bool expected = sdfGridQueryContains(query, sdfInstanceBoundAt(offset, instance));
        bool actual = (sdfAmbientMaskWords[instance >> 5u] & (1u << (instance & 31u))) != 0u;
        if (expected != actual) { differences += 1.0; }
    }
    return differences;
}

[numthreads(64, 1, 1)]
void CSMain(uint lane : SV_GroupIndex) {
    uint index = gatherProbeIndex.index;
    float4 origin = gatherCases[2u * index];
    float4 direction = gatherCases[2u * index + 1u];
    sdfProgramLayout = sdfLoadProgramLayout();
    SdfInstanceGridHeader grid = sdfLoadInstanceGridHeader(sdfInstanceDirectoryOffset(), sdfInstanceCount());
    uint mask = sdfIndirectGather(origin.xyz, origin.w, lane);
    if (lane == 0u) {
        sdfIndirectRun(gatherProbeBegin(origin.xyz, direction.xyz, origin.w, direction.w, mask));
        SdfIndirectRay masked = gatherProbeProc.masked;
        SdfIndirectRay full = gatherProbeProc.full;
        gatherResults[uint2(index, 0u)] = float4((float)masked.kind, masked.distance, (float)masked.material, (float)gatherProbeProc.maskedBudget);
        gatherResults[uint2(index, 1u)] = float4((float)full.kind, full.distance, (float)full.material, (float)gatherProbeProc.fullBudget);
        gatherResults[uint2(index, 2u)] = gatherProbeProc.intervals;
        float4 controls = 0.0;
        SdfGridQuery ball = sdfGridBall(origin.xyz, origin.w);
        SdfGridQuery shortBall = sdfGridBall(origin.xyz, max(origin.w - 1.0, 0.0));
        SdfGridQuery atHit = sdfGridBall(origin.xyz + direction.xyz * full.distance, 0.002);
        controls.x = grid.enabled && !sdfGridQueryContains(ball, float4(origin.xyz, -1.0)) ? 1.0 : 0.0;
        controls.w = mask != SDF_INSTANCE_MASK_ALL ? 1.0 : 0.0;
        [loop]
        for (uint instance = 0u; instance < sdfInstanceCountClamped(); instance++) {
            float4 bound = sdfInstanceBoundAt(sdfInstanceDirectoryOffset(), instance);
            bool inside = sdfGridQueryContains(ball, bound);
            if (inside && !sdfGridQueryContains(shortBall, bound)) { controls.y += 1.0; }
            if (!inside && sdfGridQueryContains(atHit, bound)) { controls.z += 1.0; }
        }
        gatherResults[uint2(index, 4u)] = controls;
    }
    GroupMemoryBarrierWithGroupSync();
    float4 differences = 0.0;
    [loop]
    for (uint shape = 0u; shape < 3u; shape++) {
        SdfGridQuery query;
        if (shape == 0u) {
            query = sdfGridCone(origin.xyz, direction.xyz, 0.15, rsqrt(1.0 - 0.15 * 0.15), 0.25, 1.0e20);
        } else if (shape == 1u) {
            query = sdfGridBall(origin.xyz, origin.w);
        } else {
            query = sdfGridBox(origin.xyz - origin.w, origin.xyz + origin.w);
        }
        gatherProbeQuery(grid, query, lane, SDF_GROUP_SHADOW_LANES);
        if (lane == 0u) { differences[shape] = gatherProbeQueryDifferences(query); }
        GroupMemoryBarrierWithGroupSync();
        gatherProbeQuery(grid, query, lane, 1u);
        if (lane == 0u) { differences.w += gatherProbeQueryDifferences(query); }
        GroupMemoryBarrierWithGroupSync();
    }
    if (lane == 0u) { gatherResults[uint2(index, 3u)] = differences; }
}
