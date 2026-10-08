#ifndef SDF_INDIRECT_FIELD_HLSLI
#define SDF_INDIRECT_FIELD_HLSLI
#include "../field/sdf-vm.hlsli"
#include "../frame/sdf-work.hlsli"
#include "../march/sdf-grid-walk.hlsli"
#include "../isa/sdf-indirect-layout.hlsli"
#include "../field/sdf-octahedral.hlsli"

// The procedures each engine kernel reaches (sdfIndirectRun below). A kernel that runs its own set defines
// SDF_INDIRECT_PROCS_CUSTOM and its SDF_INDIRECT_PROC_* before its includes.
#ifndef SDF_INDIRECT_PROCS_CUSTOM
#if defined(SDF_RECEIVER_PASS)
#define SDF_INDIRECT_PROC_LAUNCH
#define SDF_INDIRECT_PROC_SEGMENT
#define SDF_INDIRECT_PROC_PROVE
#define SDF_INDIRECT_PROC_MARCH
#define SDF_INDIRECT_PROC_VISIBILITIES
#define SDF_INDIRECT_PROC_RECEIVE
#define SDF_INDIRECT_PROC_RECEIVER
#ifdef SDF_INDIRECT_COMPARISON
#define SDF_INDIRECT_PROC_CONE_BOUNCE
#define SDF_INDIRECT_PROC_ALTERNATIVE
#else
#define SDF_INDIRECT_PROC_NEAR_INCOMING
#endif
#elif defined(SDF_LIGHT_VIEW)
#define SDF_INDIRECT_PROC_MARCH
#endif
#endif

static uint sdfIndirectEvaluations = 0u;
static uint sdfIndirectSteps = 0u;
static uint sdfIndirectProofEvaluations = 0u;
static uint sdfIndirectLaunchEvaluations = 0u;

#include "sdf-indirect-run.hlsli"

// One indirect field query of `kind` (SdfIndirectQuery*, indirect/sdf-indirect-run.hlsli). Each mapCore and mapGradCore
// call DXC sees inlines the whole interpreter, so a kernel's indirect queries all reach this one body: through
// sdfIndirectRun's single call for a kernel that runs its field work as procedures, or through sdfIndirectSample and
// sdfIndirectGradient, whose constant kind keeps only the half they ask for.
void sdfIndirectServe(uint kind, float3 position, uint mask, out SdfHit hit, out float3 normal) {
    hit = sdfIsaErrorHit();
    hit.distance = asfloat(0x7fc00000u);
    normal = 0.0;
    bool gradient = (kind == SdfIndirectQueryGradient);
#ifdef SDF_INDIRECT_PLAIN_QUERIES
    bool plain = (kind == SdfIndirectQueryPlain);
#else
    bool plain = false;
#endif
    bool previousTape = sdfTapeActive;
    bool previousParticipation = sdfIndirectParticipationActive;
#ifdef SDF_SCREEN_SOURCES
    bool previousMask = sdfShadowMaskActive;
    bool previousAmbientMask = sdfAmbientMaskActive;
#endif
    if (!plain) {
        sdfIndirectEvaluations++;
        sdfWorkSteps++;
        if (!all(isfinite(position))) { return; }
        sdfIndirectParticipationActive = true;
        sdfTapeActive = false;
#ifdef SDF_SCREEN_SOURCES
        sdfShadowMaskActive = !gradient && (mask != SDF_INSTANCE_MASK_ALL);
        sdfAmbientMaskActive = false;
#endif
    }
    if (gradient) {
        float3 fieldGradient;
        mapGradCore(position, SDF_INSTANCE_MASK_ALL, fieldGradient);
        float magnitude = length(fieldGradient);
        normal = magnitude > 0.0 && isfinite(magnitude) ? fieldGradient / magnitude : 0.0;
    } else {
        hit = mapCore(position, mask, true);
    }
    if (!plain) {
        sdfIndirectParticipationActive = previousParticipation;
        sdfTapeActive = previousTape;
#ifdef SDF_SCREEN_SOURCES
        sdfShadowMaskActive = previousMask;
        sdfAmbientMaskActive = previousAmbientMask;
#endif
    }
}
SdfHit sdfIndirectSample(float3 position, uint mask) {
    SdfHit hit;
    float3 normal;
    sdfIndirectServe(SdfIndirectQuerySample, position, mask, hit, normal);
    return hit;
}

float3 sdfIndirectGradient(float3 position) {
    SdfHit hit;
    float3 normal;
    sdfIndirectServe(SdfIndirectQueryGradient, position, SDF_INSTANCE_MASK_ALL, hit, normal);
    return normal;
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
    for (uint candidate = 0u; candidate < SDF_MAX_INSTANCES; candidate++) {
        if (!sdfGridWalkNext(grid, query, walk, instance)) { break; }
        if (sdfGridQueryContains(query, sdfInstanceBoundAt(directory, instance))) {
            InterlockedOr(sdfShadowMaskWords[instance >> 5u], 1u << (instance & 31u));
        }
    }
    GroupMemoryBarrierWithGroupSync();
    return 0u;
}
#endif

uint sdfIndirectPackNormal(float3 normal) {
    if (!all(isfinite(normal)) || dot(normal, normal) == 0.0) { return 0u; }
    uint2 packed = uint2(round(saturate(sdfOctEncode(normal) * 0.5 + 0.5) * 65535.0));
    // Zero belongs to the absent-plane sentinel; its octahedral neighbour has the same quantized pole.
    return max(1u, packed.x | (packed.y << 16u));
}
float3 sdfIndirectUnpackNormal(uint packed) {
    return sdfOctDecode(float2(packed & 65535u, packed >> 16u) / 65535.0 * 2.0 - 1.0);
}
#endif
