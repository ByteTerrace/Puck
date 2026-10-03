#define SDF_INDIRECT_PASS
#define SDF_SCREEN_SOURCES
#define SDF_GROUP_SHADOW_GATHER
#define SDF_DYNAMIC_TRANSFORMS
#include "../indirect/sdf-indirect-cache.hlsli"

[numthreads(64, 1, 1)]
void CSMain(uint3 group : SV_GroupID, uint lane : SV_GroupIndex) {
    if (passGroup.indirectTier == SdfIndirectTierOff || group.x >= passGroup.indirectTraceCount) { return; }
    sdfProgramLayout = sdfLoadProgramLayout();
    uint4 update = indirectUpdates[passGroup.indirectPlaceCount + passGroup.indirectClassifyCount + group.x];
    uint index = update.x;
    uint level = update.z;
    uint local = index % SdfIndirectProbesPerBrick;
    int3 lattice = indirectBricks[index / SdfIndirectProbesPerBrick].xyz * 4 + int3(local & 3u, (local >> 2u) & 3u, local >> 4u);
    SdfIndirectPlacement probe = sdfIndirectReadProbe((int)index);
    float reach = sdfIndirectReach(passGroup.indirectTier, level);
    uint gather = sdfIndirectGather(probe.position, reach, lane);
    if (probe.classification != SdfIndirectClassActive && probe.classification != SdfIndirectClassRelocated) { return; }
    uint ordinal = lane * (sdfIndirectRaysPerProbe(passGroup.indirectTier) / 64u) + update.y;
    sdfIndirectProofOwner = ordinal == 0u ? index : 0xffffffffu;
    float3 direction = sdfIndirectDirection(lattice, level, ordinal);
    uint budget = SdfIndirectTraceSteps;
    uint mask = 0u;
    uint proofLevel = level;
    SdfIndirectRay ray = (SdfIndirectRay)0;
    ray.kind = SdfIndirectKindUnresolved;
    float travel = 0.0;
    // Reach is a support-seeking boundary, never an unproved terminal. Between attempts the full field certifies travel.
    [loop] while (budget > 0u) {
        float segmentEnd = reach > 0.0 && level + 1u < sdfIndirectLevelCount()
            ? min(passGroup.farDistance, travel == 0.0 ? reach : travel + sdfIndirectSpacing(passGroup.indirectTier, level + 1u))
            : passGroup.farDistance;
        ray = sdfIndirectMarch(probe.position + direction * travel, direction, travel == 0.0 ? reach : 0.0,
            segmentEnd - travel, travel == 0.0 ? gather : SDF_INSTANCE_MASK_ALL, budget);
        ray.distance += travel;
        if (ray.kind != SdfIndirectKindExit || ray.distance >= passGroup.farDistance) { break; }
        travel = ray.distance;
        proofLevel = level + 1u;
        uint component = sdfIndirectProve(probe.position + direction * travel, proofLevel, budget, 0.0);
        mask = sdfIndirectSupport(probe.position + direction * travel, direction, proofLevel, component);
        if (mask != 0u) { ray.kind = SdfIndirectKindContinuation; break; }
        ray.kind = SdfIndirectKindUnresolved;
    }
    if (ray.kind == SdfIndirectKindHit) {
        uint launch = SdfIndirectLaunchSteps;
        uint feedback = SdfIndirectFeedbackSteps - SdfIndirectLaunchSteps;
        float3 position;
        float clearance;
        proofLevel = level;
        float3 surface = probe.position + direction * ray.distance;
        float spacing = sdfIndirectSpacing(passGroup.indirectTier, level);
        if (sdfIndirectLaunch(surface, ray.normal, spacing, launch, position, clearance)) {
            ray.launchHeight = sdfIndirectQuantizeLaunch(surface, ray.normal, spacing, position, clearance);
            if (clearance > 0.0) { mask = sdfIndirectProve(position, level, feedback, clearance); }
        }
    }
    if (ray.kind == SdfIndirectKindUnresolved || ray.kind == SdfIndirectKindExit) { mask = 0u; }
    uint4 packed = sdfIndirectPackRay(ray, mask, proofLevel);
    uint address = sdfIndirectHitWordOffset(passGroup.indirectTier) + (index * sdfIndirectRaysPerProbe(passGroup.indirectTier) + ordinal) * SdfIndirectHitWords;
    indirectCacheRW[address] = packed.x;
    indirectCacheRW[address + 1u] = packed.y;
    indirectCacheRW[address + 2u] = packed.z;
    indirectCacheRW[address + 3u] = packed.w;
    DeviceMemoryBarrierWithGroupSync();
    if (lane == 0u) { InterlockedOr(indirectCacheRW[index * SdfIndirectProbeWords + 3u], 1u << (SdfIndirectTracedShift + update.y)); }
    uint detail = passGroup.indirectTier == SdfIndirectTierHigh ? level : level + 1u;
    sdfWorkTexels = 1u;
    puckCountDetail(detail, sdfIndirectEvaluations - sdfIndirectLaunchEvaluations - sdfIndirectProofEvaluations, sdfWorkTexels, 0u, 0u, 0u);
    puckCountDetail(3u, sdfIndirectLaunchEvaluations, 0u, 0u, 0u, 0u);
    puckCountDetail(4u, sdfIndirectProofEvaluations, 0u, 0u, 0u, 0u);
    puckCountIndirect(detail, 0u, 0u, ray.kind == SdfIndirectKindUnresolved ? 1u : 0u);
    // A detail row holds this lane's work when the pass has detail rows; the plain row then adds none of it.
    if (passGroup.workCounterRowDetail != 0u) { sdfWorkSteps = 0u; sdfWorkTexels = 0u; }
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
}
