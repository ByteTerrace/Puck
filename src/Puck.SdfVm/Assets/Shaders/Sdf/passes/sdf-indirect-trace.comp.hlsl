#define SDF_INDIRECT_PASS
#define SDF_SCREEN_SOURCES
#define SDF_GROUP_SHADOW_GATHER
#define SDF_DYNAMIC_TRANSFORMS
#include "../indirect/sdf-indirect-cache.hlsli"

[numthreads(64, 1, 1)]
void CSMain(uint3 group : SV_GroupID, uint lane : SV_GroupIndex) {
    if (passGroup.indirectTier == SdfIndirectTierOff) { return; }
    // One existing transport dispatch resets the allowance shared by all subsequent view consumers, even at rest.
    if (group.x == 0u && lane == 0u) {
        sdfIndirectStore(sdfIndirectReceiverProofWordOffset(passGroup.indirectTier), 0u);
        puckCountDetail(4u, 0u, 1u, 0u, 0u, 0u);
        if (passGroup.workCounterRowDetail == 0u) { puckCountWork(0u, 1u); }
    }
    if (group.x >= passGroup.indirectTraceCount || group.x >= sdfIndirectTraceBudget(passGroup.indirectTier)
        || passGroup.indirectPlaceCount > sdfIndirectClassifyBudget(passGroup.indirectTier)
        || passGroup.indirectClassifyCount > sdfIndirectClassifyBudget(passGroup.indirectTier)) { return; }
    sdfProgramLayout = sdfLoadProgramLayout();
    uint4 update;
    int3 lattice;
    if (!sdfIndirectProbeUpdate(passGroup.indirectPlaceCount + passGroup.indirectClassifyCount + group.x, update, lattice)
        || update.y >= sdfIndirectRaysPerProbe(passGroup.indirectTier) / 64u) { return; }
    uint index = update.x;
    uint level = update.z;
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
    [loop] for (uint segment = 0u; segment < SdfIndirectTraceSteps && budget > 0u; segment++) {
        float segmentEnd = reach > 0.0 && level + 1u < sdfIndirectLevelCount()
            ? min(passGroup.farDistance, travel == 0.0 ? reach : travel + sdfIndirectSpacing(passGroup.indirectTier, level + 1u))
            : passGroup.farDistance;
        ray = sdfIndirectMarch(probe.position + direction * travel, direction, travel == 0.0 ? reach : 0.0,
            segmentEnd - travel, travel == 0.0 ? gather : SDF_INSTANCE_MASK_ALL, 0.0, budget);
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
    sdfIndirectStore(address, packed.x);
    sdfIndirectStore(address + 1u, packed.y);
    sdfIndirectStore(address + 2u, packed.z);
    sdfIndirectStore(address + 3u, packed.w);
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
