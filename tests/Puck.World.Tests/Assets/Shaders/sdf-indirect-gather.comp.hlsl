#define SDF_INDIRECT_PASS
#define SDF_SCREEN_SOURCES
#define SDF_GROUP_SHADOW_GATHER
#define SDF_DYNAMIC_TRANSFORMS
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

float4 gatherProbeIntervals(float3 origin, float3 direction, float reach, float farDistance, uint mask) {
    float4 result = 0.0;
    float traveled = 0.0;
    [loop]
    for (uint step = 0u; step < 64u && traveled < farDistance; step++) {
        bool masked = sdfIndirectMasked(traveled, reach, mask);
        sdfShadowMaskActive = masked;
        float field = mapDistance(origin + direction * traveled);
        float clearance = sdfMapBallClearance(field);
        sdfShadowMaskActive = false;
        if (clearance <= 0.00001) { break; }
        float advance = sdfIndirectAdvance(clearance, traveled, reach, farDistance, mask);
        if (advance <= 0.0) { break; }
        if (masked && traveled + advance > reach + 0.000001) { result.y += 1.0; }
        [loop]
        for (uint sample = 1u; sample < 16u; sample++) {
            float at = traveled + advance * ((float)sample / 16.0);
            float full = mapDistance(origin + direction * at);
            if (full < -0.00001) { result.x += 1.0; }
        }
        result.z += 1.0;
        traveled += advance;
    }
    result.w = traveled;
    return result;
}

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
        uint maskedBudget = 64u, fullBudget = 64u;
        SdfIndirectRay masked = sdfIndirectMarch(origin.xyz, direction.xyz, origin.w, direction.w, mask, 0.0, maskedBudget);
        SdfIndirectRay full = sdfIndirectMarch(origin.xyz, direction.xyz, origin.w, direction.w, SDF_INSTANCE_MASK_ALL, 0.0, fullBudget);
        gatherResults[uint2(index, 0u)] = float4((float)masked.kind, masked.distance, (float)masked.material, (float)maskedBudget);
        gatherResults[uint2(index, 1u)] = float4((float)full.kind, full.distance, (float)full.material, (float)fullBudget);
        gatherResults[uint2(index, 2u)] = gatherProbeIntervals(origin.xyz, direction.xyz, origin.w, direction.w, mask);
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
