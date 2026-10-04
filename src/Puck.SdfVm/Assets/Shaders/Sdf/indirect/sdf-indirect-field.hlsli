#ifndef SDF_INDIRECT_FIELD_HLSLI
#define SDF_INDIRECT_FIELD_HLSLI
#include "../field/sdf-vm.hlsli"
#include "../frame/sdf-work.hlsli"
#include "../march/sdf-grid-walk.hlsli"
#include "../isa/sdf-indirect-layout.hlsli"
#include "../field/sdf-octahedral.hlsli"

static uint sdfIndirectEvaluations = 0u;
static uint sdfIndirectSteps = 0u;
static uint sdfIndirectProofEvaluations = 0u;
static uint sdfIndirectLaunchEvaluations = 0u;

SdfHit sdfIndirectSample(float3 position, uint mask) {
    sdfIndirectEvaluations++;
    sdfWorkSteps++;
    bool previousTape = sdfTapeActive;
    sdfTapeActive = false;
#ifdef SDF_SCREEN_SOURCES
    bool previousMask = sdfShadowMaskActive;
    sdfShadowMaskActive = mask != SDF_INSTANCE_MASK_ALL;
#endif
    SdfHit hit = mapCore(position, mask, true);
    sdfTapeActive = previousTape;
#ifdef SDF_SCREEN_SOURCES
    sdfShadowMaskActive = previousMask;
#endif
    return hit;
}

float3 sdfIndirectGradient(float3 position) {
    sdfIndirectEvaluations++;
    sdfWorkSteps++;
    bool previousTape = sdfTapeActive;
    sdfTapeActive = false;
    float3 gradient;
    mapGradCore(position, SDF_INSTANCE_MASK_ALL, gradient);
    sdfTapeActive = previousTape;
    float magnitude = length(gradient);
    return magnitude > 0.0 && isfinite(magnitude) ? gradient / magnitude : 0.0;
}

bool sdfIndirectMasked(float travelled, float reach, uint mask) {
    return mask != SDF_INSTANCE_MASK_ALL && reach > 0.0 && travelled < reach;
}

float sdfIndirectAdvance(float clearance, float travelled, float reach, float farDistance, uint mask) {
    float end = sdfIndirectMasked(travelled, reach, mask) ? min(reach, farDistance) : farDistance;
    return max(0.0, min(clearance, end - travelled));
}

#ifdef SDF_INDIRECT_PASS
// All 64 lanes participate, including dormant probes. World programs are always evaluated by the VM.
uint sdfIndirectGather(float3 origin, float reach, uint lane) {
    uint count = sdfInstanceCount();
    uint directory = sdfInstanceDirectoryOffset();
    SdfInstanceGridHeader grid = sdfLoadInstanceGridHeader(directory, count);
    if (reach <= 0.0 || !grid.enabled || !sdfCanTracePartsIndependently()) { return SDF_INSTANCE_MASK_ALL; }
    for (uint word = lane; word < SDF_SHADOW_MASK_WORDS; word += 64u) { sdfShadowMaskWords[word] = 0u; }
    GroupMemoryBarrierWithGroupSync();
    SdfGridQuery query = sdfGridBall(origin, reach);
    SdfGridWalk walk = sdfGridWalkBegin(grid, query, lane, 64u);
    uint instance;
    [loop]
    while (sdfGridWalkNext(grid, query, walk, instance)) {
        if (sdfGridQueryContains(query, sdfInstanceBoundAt(directory, instance))) {
            InterlockedOr(sdfShadowMaskWords[instance >> 5u], 1u << (instance & 31u));
        }
    }
    GroupMemoryBarrierWithGroupSync();
    return 0u;
}
#endif

uint sdfIndirectPackNormal(float3 normal) {
    if (dot(normal, normal) == 0.0) { return 0u; }
    uint2 packed = uint2(round(saturate(sdfOctEncode(normal) * 0.5 + 0.5) * 65535.0));
    // Zero belongs to the absent-plane sentinel; its octahedral neighbour has the same quantized pole.
    return max(1u, packed.x | (packed.y << 16u));
}
float3 sdfIndirectUnpackNormal(uint packed) {
    return sdfOctDecode(float2(packed & 65535u, packed >> 16u) / 65535.0 * 2.0 - 1.0);
}
#endif
